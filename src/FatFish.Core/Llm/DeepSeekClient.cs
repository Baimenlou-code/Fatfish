using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FatFish.Core.Llm;

/// <summary>
/// DeepSeek（及任何 OpenAI 兼容服务）的流式聊天客户端。
///
/// 为什么自己写 SSE 解析而不引第三方库：
///   · 协议本身极简（`data: {json}` + `data: [DONE]`），几十行就能写完；
///   · 引库会让「工具调用分片拼接」这类关键逻辑被藏进黑盒，
///     出问题时要翻别人的源码才知道发生了什么；
///   · 零依赖也意味着可以放心 AOT 发布。
///
/// ★ 三个最容易踩的坑，都在下面的实现里显式处理了：
///   1. function.arguments 是**分片下发的字符串**，必须按 index 累加后整体解析；
///   2. assistant 只调工具时 content 为 null，不能让序列化写成 "";
///   3. 超时不能用 HttpClient.Timeout（它会把长流一刀切断），
///      要用「空闲超时」：每次收到数据就续命。
/// </summary>
public sealed class DeepSeekClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;
    private readonly bool _ownsHttp;
    private bool _disposed;

    public DeepSeekClient(LlmOptions options, HttpClient? http = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (http is null)
        {
            var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                AutomaticDecompression = DecompressionMethods.All,
            };

            // ★ Timeout 必须无限：流式响应可能跑几分钟，
            //   HttpClient.Timeout 是「整个请求」的上限，会中途掐断。
            //   真正的超时控制交给下面的空闲计时器。
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
            _ownsHttp = false;
        }
    }

    public LlmOptions Options => _options;

    // ------------------------------------------------------------------
    // 主入口：流式调用
    // ------------------------------------------------------------------

    /// <summary>
    /// 发起一轮流式对话。
    /// </summary>
    /// <param name="messages">完整历史（必须至少含一条 user）。</param>
    /// <param name="tools">ToolRegistry.BuildToolSchema() 的结果；null 表示不带工具。</param>
    /// <param name="ct">取消令牌。</param>
    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        JsonElement? tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
            throw new LlmException("消息列表为空，无法调用模型。");

        var body = BuildRequestBody(messages, tools);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ChatUrl)
        {
            Content = new StringContent(
                body.ToJsonString(Tools.ToolJson.DefaultOptions),
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("User-Agent", "FatFish-CS/2.0");

        // ★ 空闲超时：连接/等首字节用一个独立计时器，
        //   每读到一行数据就重置，实现「只要还在吐字就不算超时」。
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var idleLimit = TimeSpan.FromSeconds(Math.Max(10, _options.TimeoutSeconds));
        idle.CancelAfter(idleLimit);

        HttpResponseMessage response;
        try
        {
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException(
                $"连接模型超时（{_options.TimeoutSeconds}s 内未响应）。检查网络或代理设置。");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"网络请求失败：{ex.Message}", inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
                throw new LlmException(
                    $"模型返回 {((int)response.StatusCode)} {response.StatusCode}：{Explain(errBody)}",
                    (int)response.StatusCode);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

            // 少数情况下服务端会忽略 stream=true 直接返回完整 JSON —— 兼容一下
            if (!mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var whole = await SafeReadBodyAsync(response, ct).ConfigureAwait(false);
                foreach (var evt in ParseWholeResponse(whole))
                    yield return evt;

                yield break;
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(ct)
                .ConfigureAwait(false);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);

            var acc = new ToolCallAccumulatorSet();
            string? finishReason = null;
            var anyEvent = false;

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new LlmException(
                        $"读取流超时：{_options.TimeoutSeconds}s 内没有收到任何数据。");
                }

                if (line is null)
                    break;

                // 收到任何一行（包括保活注释）都算「还活着」，续命
                idle.CancelAfter(idleLimit);

                if (line.Length == 0)
                    continue;

                // SSE 注释行（": ping" 之类），忽略
                if (line[0] == ':')
                    continue;

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var payload = line.AsSpan(5).TrimStart(' ').ToString();

                if (payload.Length == 0)
                    continue;

                if (payload == "[DONE]")
                    break;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(payload);
                }
                catch (JsonException)
                {
                    // 分片被异常截断之类，跳过这一行而不是整轮失败
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;

                    // usage 可能在独立 chunk 里
                    if (root.TryGetProperty("usage", out var usageEl)
                        && usageEl.ValueKind == JsonValueKind.Object)
                    {
                        yield return ParseUsage(usageEl);
                    }

                    if (!root.TryGetProperty("choices", out var choices)
                        || choices.ValueKind != JsonValueKind.Array
                        || choices.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    var choice = choices[0];

                    if (choice.TryGetProperty("finish_reason", out var fr)
                        && fr.ValueKind == JsonValueKind.String)
                    {
                        finishReason = fr.GetString();
                    }

                    if (!choice.TryGetProperty("delta", out var delta)
                        || delta.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    // --- 思考链 ---
                    if (delta.TryGetProperty("reasoning_content", out var rc)
                        && rc.ValueKind == JsonValueKind.String)
                    {
                        var t = rc.GetString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            anyEvent = true;
                            yield return new LlmStreamEvent.ReasoningDelta(t);
                        }
                    }

                    // --- 正文 ---
                    if (delta.TryGetProperty("content", out var c)
                        && c.ValueKind == JsonValueKind.String)
                    {
                        var t = c.GetString();
                        if (!string.IsNullOrEmpty(t))
                        {
                            anyEvent = true;
                            yield return new LlmStreamEvent.TextDelta(t);
                        }
                    }

                    // --- 工具调用 ---
                    if (delta.TryGetProperty("tool_calls", out var tcs)
                        && tcs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tcEl in tcs.EnumerateArray())
                        {
                            anyEvent = true;

                            var idx = tcEl.TryGetProperty("index", out var ie)
                                && ie.ValueKind == JsonValueKind.Number
                                ? ie.GetInt32()
                                : 0;

                            var slot = acc.GetOrAdd(idx);

                            if (tcEl.TryGetProperty("id", out var idEl)
                                && idEl.ValueKind == JsonValueKind.String)
                            {
                                var id = idEl.GetString();
                                if (!string.IsNullOrEmpty(id))
                                    slot.Id = id;
                            }

                            if (tcEl.TryGetProperty("function", out var fnEl)
                                && fnEl.ValueKind == JsonValueKind.Object)
                            {
                                if (fnEl.TryGetProperty("name", out var nameEl)
                                    && nameEl.ValueKind == JsonValueKind.String)
                                {
                                    var nm = nameEl.GetString();
                                    if (!string.IsNullOrEmpty(nm) && slot.Name.Length == 0)
                                    {
                                        slot.Name = nm;
                                        yield return new LlmStreamEvent.ToolCallStarted(idx, slot.Id, nm);
                                    }
                                    else if (!string.IsNullOrEmpty(nm))
                                    {
                                        slot.Name = nm;
                                    }
                                }

                                if (fnEl.TryGetProperty("arguments", out var argsEl)
                                    && argsEl.ValueKind == JsonValueKind.String)
                                {
                                    var frag = argsEl.GetString();
                                    if (!string.IsNullOrEmpty(frag))
                                    {
                                        slot.Arguments.Append(frag);
                                        yield return new LlmStreamEvent.ToolCallArgumentsDelta(idx, frag);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            if (!anyEvent && finishReason is null)
            {
                // 服务器 200 但一个事件都没给 —— 当作错误，避免静默失败
                throw new LlmException("模型未返回任何内容（流为空）。");
            }

            yield return new LlmStreamEvent.Completed(finishReason);
        }
    }

    // ------------------------------------------------------------------
    // 便捷封装：一次拿全（内部仍是流式，只是不逐片回调）
    // ------------------------------------------------------------------

    /// <summary>
    /// 跑一轮并把流聚合为 <see cref="LlmTurn"/>。
    /// <paramref name="onTextDelta"/> 可在流式过程中实时拿到正文增量（UI 边收边显示）。
    /// </summary>
    public async Task<LlmTurn> CompleteAsync(
        IReadOnlyList<ChatMessage> messages,
        JsonElement? tools,
        Action<string>? onTextDelta = null,
        Action<string>? onReasoningDelta = null,
        CancellationToken ct = default)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var acc = new ToolCallAccumulatorSet();
        string? finishReason = null;
        LlmUsage? usage = null;

        await foreach (var evt in StreamAsync(messages, tools, ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case LlmStreamEvent.TextDelta d:
                    text.Append(d.Text);
                    onTextDelta?.Invoke(d.Text);
                    break;

                case LlmStreamEvent.ReasoningDelta d:
                    reasoning.Append(d.Text);
                    onReasoningDelta?.Invoke(d.Text);
                    break;

                case LlmStreamEvent.ToolCallStarted s:
                    {
                        var slot = acc.GetOrAdd(s.Index);
                        if (slot.Id.Length == 0) slot.Id = s.Id;
                        if (slot.Name.Length == 0) slot.Name = s.Name;
                        break;
                    }

                case LlmStreamEvent.ToolCallArgumentsDelta a:
                    acc.GetOrAdd(a.Index).Arguments.Append(a.Delta);
                    break;

                case LlmStreamEvent.Usage u:
                    usage = new LlmUsage(u.PromptTokens, u.CompletionTokens, u.TotalTokens);
                    break;

                case LlmStreamEvent.Completed c:
                    finishReason = c.FinishReason;
                    break;
            }
        }

        return new LlmTurn
        {
            Text = text.ToString(),
            Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null,
            ToolCalls = acc.Build(),
            FinishReason = finishReason,
            Usage = usage,
        };
    }

    // ------------------------------------------------------------------
    // 内部：请求体
    // ------------------------------------------------------------------

    private JsonObject BuildRequestBody(IReadOnlyList<ChatMessage> messages, JsonElement? tools)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
            arr.Add(m.ToJson());

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = arr,
            ["stream"] = true,
            // 让服务端在流末尾附 usage，便于对账
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = _options.Temperature,
            ["max_tokens"] = _options.MaxTokens,
        };

        if (tools is { ValueKind: JsonValueKind.Array } t && t.GetArrayLength() > 0)
        {
            // 深拷贝一份：JsonElement 不能直接塞进 JsonNode
            body["tools"] = JsonNode.Parse(t.GetRawText());
            body["tool_choice"] = "auto";
        }

        return body;
    }

    // ------------------------------------------------------------------
    // 内部：解析助手
    // ------------------------------------------------------------------

    private static LlmStreamEvent.Usage ParseUsage(JsonElement el)
    {
        int Get(string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32()
                : 0;

        var prompt = Get("prompt_tokens");
        var completion = Get("completion_tokens");
        var total = Get("total_tokens");
        if (total == 0) total = prompt + completion;

        return new LlmStreamEvent.Usage(prompt, completion, total);
    }

    /// <summary>兼容「服务端没走 SSE，直接回了完整 JSON」的情况。</summary>
    private static IEnumerable<LlmStreamEvent> ParseWholeResponse(string raw)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new LlmException($"响应不是合法 JSON：{ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            string? text = null;
            string? reasoning = null;
            string? finish = null;
            var calls = new List<ToolCall>();

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var c0 = choices[0];

                if (c0.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    finish = fr.GetString();

                if (c0.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                {
                    if (msg.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String)
                        text = ct.GetString();

                    if (msg.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                        reasoning = rc.GetString();

                    if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            var id = tc.TryGetProperty("id", out var i) ? i.GetString() ?? string.Empty : string.Empty;
                            var name = string.Empty;
                            var args = string.Empty;

                            if (tc.TryGetProperty("function", out var fn))
                            {
                                if (fn.TryGetProperty("name", out var n)) name = n.GetString() ?? string.Empty;
                                if (fn.TryGetProperty("arguments", out var a)) args = a.GetString() ?? string.Empty;
                            }

                            calls.Add(new ToolCall { Id = id, Name = name, Arguments = args });
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(reasoning))
                yield return new LlmStreamEvent.ReasoningDelta(reasoning);

            if (!string.IsNullOrEmpty(text))
                yield return new LlmStreamEvent.TextDelta(text);

            for (var i = 0; i < calls.Count; i++)
            {
                yield return new LlmStreamEvent.ToolCallStarted(i, calls[i].Id, calls[i].Name);
                if (calls[i].Arguments.Length > 0)
                    yield return new LlmStreamEvent.ToolCallArgumentsDelta(i, calls[i].Arguments);
            }

            yield return new LlmStreamEvent.Completed(finish);
        }
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var s = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return s.Length > 2000 ? s[..2000] + "…" : s;
        }
        catch
        {
            return "(无法读取响应体)";
        }
    }

    /// <summary>把常见错误码翻译成人话——比让用户自己查文档有用。</summary>
    private static string Explain(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "(空响应)";

        // 尽量提取 error.message
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object
                    && err.TryGetProperty("message", out var m)
                    && m.ValueKind == JsonValueKind.String)
                {
                    return m.GetString() ?? body;
                }

                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // 不是 JSON，原样返回
        }

        return body;
    }

    // ------------------------------------------------------------------
    // 内部：工具调用累加器
    // ------------------------------------------------------------------

    private sealed class ToolCallSlot
    {
        public int Index { get; init; }
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }

    /// <summary>按 index 收集分片下发的工具调用。</summary>
    private sealed class ToolCallAccumulatorSet
    {
        private readonly Dictionary<int, ToolCallSlot> _slots = new();

        public ToolCallSlot GetOrAdd(int index)
        {
            if (!_slots.TryGetValue(index, out var slot))
            {
                slot = new ToolCallSlot { Index = index };
                _slots[index] = slot;
            }

            return slot;
        }

        public IReadOnlyList<ToolCall> Build()
        {
            if (_slots.Count == 0)
                return Array.Empty<ToolCall>();

            var list = new List<ToolCall>(_slots.Count);

            foreach (var slot in _slots.Values.OrderBy(s => s.Index))
            {
                if (slot.Name.Length == 0)
                    continue; // 没名字的碎片，丢弃

                list.Add(new ToolCall
                {
                    Id = slot.Id.Length > 0 ? slot.Id : $"call_{slot.Index}",
                    Name = slot.Name,
                    Arguments = slot.Arguments.ToString(),
                });
            }

            return list;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_ownsHttp)
            _http.Dispose();
    }
}

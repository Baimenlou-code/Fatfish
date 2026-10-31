using System.Text;
using System.Text.Json;

namespace FatFish.Core.Safety;

/// <summary>审查员请求失败（网络、鉴权、协议）。</summary>
public sealed class VerifierException : Exception
{
    public VerifierException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// 审查员的传输层 —— 一次**非流式** chat completions 调用。
///
/// 为什么不用主模型的 <c>DeepSeekClient</c>
/// ------------------------------------
/// 主模型客户端是流式的：它一边收 SSE 一边回调 <c>onTextDelta</c>，
/// 为的是让界面能一个字一个字地出。审查员的输出被限制在 5 行以内，
/// 流式毫无收益，反而多出一整套「半截 JSON 怎么处理」的问题。
/// 更关键的是：审查员要能配成**别家模型**（异源核验），
/// 而主模型客户端锁死了 DeepSeek 的 SSE 细节。
///
/// 所以这里刻意只有 100 行：发一个 JSON、收一个 JSON。
/// 简单到可以一眼看完，是这一层最重要的性质 ——
/// 它是整条核验链上唯一会碰网络的地方，出事时必须能一眼定位。
/// </summary>
public sealed class VerifierClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly VerifierEndpoint _endpoint;

    public VerifierClient(VerifierEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

        _ownsHttp = handler is null;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);

        // ★ 不设 HttpClient.Timeout，改用每次调用的 CancellationToken。
        //   原因：HttpClient.Timeout 抛的是 TaskCanceledException，
        //   和用户按 Ctrl+C 抛的异常**一模一样**，无法区分「超时」和「用户取消」。
        //   用 CTS 就能把「超时」包成自己的 VerifierException，日志里也不会把取消记成失败。
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// 调用审查员，返回原始文本（**不解析** —— 解析是 VerdictParser 的事）。
    /// </summary>
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _endpoint.Model,
            ["messages"] = new object[]
            {
                new Dictionary<string, object?> { ["role"] = "system", ["content"] = systemPrompt },
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = userPrompt },
            },
            ["temperature"] = _endpoint.Temperature,
            ["max_tokens"] = _endpoint.MaxTokens,

            // 显式 false：某些兼容网关在缺省时会返回 SSE 流，
            // 那边一开，这里收到的就是一堆 "data: {...}" 行，解析必然失败。
            ["stream"] = false,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _endpoint.TimeoutSeconds)));

        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOut), Encoding.UTF8, "application/json");

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint.ChatUrl)
            {
                Content = content,
            };

            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _endpoint.ApiKey);

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new VerifierException(
                    $"HTTP {(int)response.StatusCode}：{Clip(body, 300)}");
            }

            return ExtractContent(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new VerifierException($"核验超时（{_endpoint.TimeoutSeconds}s）");
        }
        catch (HttpRequestException ex)
        {
            throw new VerifierException($"网络错误：{ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new VerifierException($"响应不是合法 JSON：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 从响应里取 <c>choices[0].message.content</c>。
    /// </summary>
    /// <remarks>
    /// ★ 特意兼容 <c>reasoning_content</c> 缺失、<c>content</c> 为 null 的情况：
    ///   推理型模型在「思考被打断」时会返回空 content。
    ///   这时候**不能**当成「审查员说通过」—— 交给上层按「无法解析」处理（→ supplement）。
    /// </remarks>
    internal static string ExtractContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new VerifierException("响应里没有 choices：" + Clip(body, 200));
        }

        var choice = choices[0];

        if (choice.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var contentEl)
            && contentEl.ValueKind == JsonValueKind.String)
        {
            return (contentEl.GetString() ?? string.Empty).Trim();
        }

        throw new VerifierException("响应里没有 message.content：" + Clip(body, 200));
    }

    private static string Clip(string s, int n)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= n ? s : s[..n] + "…");

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

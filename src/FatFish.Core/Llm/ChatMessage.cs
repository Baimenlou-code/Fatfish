using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FatFish.Core.Llm;

/// <summary>
/// 一次工具调用请求（由模型发出）。
///
/// ★ 关键坑：<see cref="Arguments"/> 是**JSON 字符串原文**，不是对象。
///   OpenAI / DeepSeek 的协议里 function.arguments 是 string，
///   模型可能分多片流式下发（"{\"pa" + "th\":\"a.txt\"}"），
///   必须原样拼接后再统一解析。若中途每片都试着解析，必然报错。
/// </summary>
public sealed record ToolCall
{
    public required string Id { get; init; }

    /// <summary>协议固定为 "function"。</summary>
    public string Type { get; init; } = "function";

    public required string Name { get; init; }

    /// <summary>JSON 字符串原文，可能为空串（模型只给了工具名、参数分片未到）。</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>
    /// 把 <see cref="Arguments"/> 解析为 JsonElement。
    /// 解析失败时返回空对象而不是抛异常 —— 让工具层报「缺少参数」比抛 JSON 异常更可读。
    /// </summary>
    public JsonElement ParseArguments()
    {
        if (string.IsNullOrWhiteSpace(Arguments))
            return JsonDocument.Parse("{}").RootElement.Clone();

        try
        {
            using var doc = JsonDocument.Parse(Arguments);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }
}

/// <summary>
/// 一条对话消息。
///
/// 对应 OpenAI 兼容协议的四种角色：
///   system     系统提示
///   user       用户输入
///   assistant  模型输出（可能带 tool_calls）
///   tool       工具执行结果（必须带 ToolCallId 指回是哪次调用）
///
/// 设计成 immutable record：History 是一个只读列表，
/// 追加消息 = 生成新列表，不存在「谁偷偷改了历史」的问题。
/// </summary>
public sealed record ChatMessage
{
    public required string Role { get; init; }

    /// <summary>
    /// 消息正文。
    /// ★ assistant 带 tool_calls 且无文字时，此字段允许为 null —— 这是协议要求，
    ///   不是偷懒。若强行写 ""，部分服务端会认为「有内容但为空」而报错。
    /// </summary>
    public string? Content { get; init; }

    /// <summary>深度思考链（仅 deepseek-reasoner 返回）。回传历史时可选。</summary>
    public string? ReasoningContent { get; init; }

    /// <summary>assistant 发起的工具调用。</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>role=tool 时必填，指向对应的 tool_call.id。</summary>
    public string? ToolCallId { get; init; }

    // ---------- 工厂方法 ----------

    public static ChatMessage System(string content)
        => new() { Role = "system", Content = content };

    public static ChatMessage User(string content)
        => new() { Role = "user", Content = content };

    public static ChatMessage Assistant(
        string? content,
        IReadOnlyList<ToolCall>? toolCalls = null,
        string? reasoning = null)
        => new()
        {
            Role = "assistant",
            Content = content,
            ToolCalls = toolCalls,
            ReasoningContent = reasoning,
        };

    public static ChatMessage ToolResult(string toolCallId, string content)
        => new() { Role = "tool", ToolCallId = toolCallId, Content = content };

    // ---------- 序列化 ----------

    /// <summary>
    /// 转成 API 需要的 JSON 形状。
    ///
    /// ★ 为什么不用 JsonObject 而是 Dictionary + SerializeToNode：
    ///   System.Text.Json.Nodes 里 `JsonObject["k"] = null` 的语义是**删除这个键**，
    ///   而不是写入 JSON null。而 assistant 只调工具时协议要求
    ///   `"content": null` 必须存在 —— 用 JsonObject 会导致 content 字段凭空消失，
    ///   部分服务端会因此报 400。
    ///   改用 Dictionary&lt;string, object?&gt; 后，null 会被如实序列化成 JSON null。
    ///   （这一点是跑测试时发现的，测试名：Assistant_WithoutText_EmitsNullContent）
    /// </summary>
    public JsonNode ToJson()
    {
        var dict = new Dictionary<string, object?>(6)
        {
            ["role"] = Role,
        };

        if (ToolCallId is not null)
            dict["tool_call_id"] = ToolCallId;

        // 允许为 null：assistant 只调工具时不带文字。
        // 这里必须显式保留该键，不能被忽略。
        dict["content"] = Content;

        if (ToolCalls is { Count: > 0 })
        {
            var calls = new List<Dictionary<string, object>>(ToolCalls.Count);

            foreach (var tc in ToolCalls)
            {
                calls.Add(new Dictionary<string, object>(3)
                {
                    ["id"] = tc.Id,
                    ["type"] = tc.Type,
                    ["function"] = new Dictionary<string, object>(2)
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.Arguments,
                    },
                });
            }

            dict["tool_calls"] = calls;
        }

        if (ReasoningContent is not null)
            dict["reasoning_content"] = ReasoningContent;

        // 注意用专用的 options：不能带 WhenWritingNull，
        // 否则 content=null 会被过滤掉，又回到原来的问题。
        return JsonSerializer.SerializeToNode(dict, SerializeOptions)!;
    }

    /// <summary>
    /// 消息序列化专用选项。
    /// 与 ToolJson.DefaultOptions 的关键差异：**不忽略 null**。
    /// </summary>
    private static readonly JsonSerializerOptions SerializeOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>估算 token 数（粗略）。用于触发历史裁剪，不需要精确。</summary>
    public int EstimateTokens()
    {
        var len = Content?.Length ?? 0;

        if (ToolCalls is not null)
        {
            foreach (var tc in ToolCalls)
                len += tc.Name.Length + tc.Arguments.Length + 24;
        }

        // 中文约 1 字符 ≈ 0.6 token，英文约 0.25；取个折中偏保守的系数
        return Math.Max(1, (int)(len * 0.55)) + 8;
    }

    /// <summary>用于日志/调试的短摘要。</summary>
    public string Preview(int max = 60)
    {
        var body = Content ?? string.Empty;
        if (body.Length > max)
            body = body[..max] + "…";

        var extra = ToolCalls is { Count: > 0 }
            ? $" [tool_calls:{ToolCalls.Count}]"
            : string.Empty;

        return $"{Role}: {body}{extra}";
    }
}

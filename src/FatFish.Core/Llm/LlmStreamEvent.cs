namespace FatFish.Core.Llm;

/// <summary>
/// 流式事件 —— LLM 响应被拆成的东西。
///
/// 为什么不用「回调」而用 IAsyncEnumerable：
///   · 消费方可以 await foreach，天然按序、天然支持取消；
///   · 比一堆 Action&lt;string&gt; 回调更难写错（回调顺序、重入、异常传播都要自己管）；
///   · 与 C# 的 async 生态一致，不需要额外状态机。
///
/// 与 Python 版对应关系：stream_core.py 里那些 yield 的 dict 就是这个。
/// </summary>
public abstract record LlmStreamEvent
{
    /// <summary>正文增量。</summary>
    public sealed record TextDelta(string Text) : LlmStreamEvent;

    /// <summary>思考链增量（deepseek-reasoner 才有）。</summary>
    public sealed record ReasoningDelta(string Text) : LlmStreamEvent;

    /// <summary>模型开始一个工具调用（此时只有 id 和 name，参数还没到）。</summary>
    public sealed record ToolCallStarted(int Index, string Id, string Name) : LlmStreamEvent;

    /// <summary>工具调用参数的增量分片。</summary>
    public sealed record ToolCallArgumentsDelta(int Index, string Delta) : LlmStreamEvent;

    /// <summary>本轮用量统计（部分服务端仅在流末尾给出）。</summary>
    public sealed record Usage(int PromptTokens, int CompletionTokens, int TotalTokens) : LlmStreamEvent;

    /// <summary>正常结束。</summary>
    public sealed record Completed(string? FinishReason) : LlmStreamEvent;
}

/// <summary>
/// 一轮 LLM 调用的完整结果（把流式事件聚合回来）。
/// </summary>
public sealed record LlmTurn
{
    /// <summary>正文全文。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>思考链全文。</summary>
    public string? Reasoning { get; init; }

    /// <summary>模型请求的工具调用（已按 index 排好序）。</summary>
    public IReadOnlyList<ToolCall> ToolCalls { get; init; } = Array.Empty<ToolCall>();

    public string? FinishReason { get; init; }

    public LlmUsage? Usage { get; init; }

    public bool HasToolCalls => ToolCalls.Count > 0;

    /// <summary>转成可回填进历史的消息。</summary>
    public ChatMessage ToAssistantMessage()
        => ChatMessage.Assistant(
            string.IsNullOrEmpty(Text) ? null : Text,
            HasToolCalls ? ToolCalls : null,
            string.IsNullOrEmpty(Reasoning) ? null : Reasoning);
}

public sealed record LlmUsage(int PromptTokens, int CompletionTokens, int TotalTokens)
{
    public int CachedTokens { get; init; }

    public override string ToString()
        => $"in={PromptTokens} out={CompletionTokens} total={TotalTokens}" +
           (CachedTokens > 0 ? $" (cached={CachedTokens})" : string.Empty);
}

/// <summary>LLM 调用失败（带状态码与响应体摘要，便于定位 402 欠费 / 429 限流）。</summary>
public sealed class LlmException : Exception
{
    public int StatusCode { get; }

    public LlmException(string message, int statusCode = 0, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

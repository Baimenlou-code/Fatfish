using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FatFish.Core.Llm;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// DeepSeek 流式客户端测试（不联网，用桩 HttpClient 喂预置 SSE）。
///
/// 覆盖的都是真实踩过的坑：
///   · tool_calls 的参数是**分片下发**的，必须按 index 累加；
///   · [DONE] 之后不能再处理；
///   · 思考链字段是 reasoning_content 而不是 reasoning；
///   · 非 200 要把服务端的 error.message 透出来（402 欠费/429 限流时特别重要）。
/// </summary>
public class DeepSeekClientTests
{
    // ---------- 桩 ----------

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(string Body, HttpStatusCode Status, string? MediaType)> _script;

        public int CallCount { get; private set; }

        public List<string> RequestBodies { get; } = new();

        public StubHandler(params (string Body, HttpStatusCode Status, string? MediaType)[] script)
        {
            _script = new Queue<(string, HttpStatusCode, string?)>(script);
        }

        public StubHandler(string body)
            : this((body, HttpStatusCode.OK, "text/event-stream"))
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;

            if (request.Content is not null)
            {
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            var (body, status, mediaType) = _script.Count > 0
                ? _script.Dequeue()
                : ("data: [DONE]\n\n", HttpStatusCode.OK, "text/event-stream");

            var content = new StringContent(body, Encoding.UTF8);

            if (mediaType is not null)
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

            return new HttpResponseMessage(status) { Content = content };
        }
    }

    private static LlmOptions Options() => new()
    {
        ApiKey = "test-key",
        BaseUrl = "https://example.invalid",
        Model = "test-model",
    };

    private static DeepSeekClient Client(string sse) => new(Options(), new HttpClient(new StubHandler(sse)));

    private static async Task<LlmTurn> CollectAsync(DeepSeekClient client)
    {
        return await client.CompleteAsync(
            new[] { ChatMessage.User("hi") },
            tools: null);
    }

    // ---------- 用例 ----------

    [Fact]
    public async Task Stream_SimpleText_Accumulates()
    {
        const string sse =
            """
            data: {"id":"1","choices":[{"index":0,"delta":{"content":"你"},"finish_reason":null}]}

            data: {"id":"1","choices":[{"index":0,"delta":{"content":"好"},"finish_reason":null}]}

            data: {"id":"1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var turn = await CollectAsync(Client(sse));

        Assert.Equal("你好", turn.Text);
        Assert.Equal("stop", turn.FinishReason);
        Assert.False(turn.HasToolCalls);
    }

    [Fact]
    public async Task Stream_ToolCallArguments_AreConcatenatedAcrossChunks()
    {
        // 参数被切成三段下发 —— 这是最典型的形态
        const string sse =
            """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"ws_read","arguments":""}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"pa"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":".txt\"}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;

        var turn = await CollectAsync(Client(sse));

        Assert.True(turn.HasToolCalls);
        Assert.Single(turn.ToolCalls);

        var call = turn.ToolCalls[0];
        Assert.Equal("call_1", call.Id);
        Assert.Equal("ws_read", call.Name);
        Assert.Equal("{\"path\":\"a.txt\"}", call.Arguments);

        // 拼完之后应能正常解析
        var args = call.ParseArguments();
        Assert.Equal("a.txt", args.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Stream_ParallelToolCalls_KeepSeparateIndexes()
    {
        const string sse =
            """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"c0","type":"function","function":{"name":"ws_read","arguments":"{\"path\":\"a\"}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"c1","type":"function","function":{"name":"ws_list","arguments":"{\"path\":\"b\"}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;

        var turn = await CollectAsync(Client(sse));

        Assert.Equal(2, turn.ToolCalls.Count);
        Assert.Equal("ws_read", turn.ToolCalls[0].Name);
        Assert.Equal("ws_list", turn.ToolCalls[1].Name);
    }

    [Fact]
    public async Task Stream_ReasoningContent_IsCaptured()
    {
        const string sse =
            """
            data: {"choices":[{"index":0,"delta":{"reasoning_content":"先想想"}}]}

            data: {"choices":[{"index":0,"delta":{"content":"结论"}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var turn = await CollectAsync(Client(sse));

        Assert.Equal("先想想", turn.Reasoning);
        Assert.Equal("结论", turn.Text);
    }

    [Fact]
    public async Task Stream_UsageIsParsed()
    {
        const string sse =
            """
            data: {"choices":[{"index":0,"delta":{"content":"hi"}}]}

            data: {"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":20,"total_tokens":120}}

            data: [DONE]

            """;

        var turn = await CollectAsync(Client(sse));

        Assert.NotNull(turn.Usage);
        Assert.Equal(100, turn.Usage!.PromptTokens);
        Assert.Equal(20, turn.Usage.CompletionTokens);
        Assert.Equal(120, turn.Usage.TotalTokens);
    }

    [Fact]
    public async Task Stream_NonSuccess_ThrowsWithServerMessage()
    {
        var handler = new StubHandler((
            """{"error":{"message":"Insufficient Balance","type":"insufficient_balance"}}""",
            HttpStatusCode.PaymentRequired,
            "application/json"));

        var client = new DeepSeekClient(Options(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(client));

        Assert.Equal(402, ex.StatusCode);
        Assert.Contains("Insufficient Balance", ex.Message);
    }

    [Fact]
    public async Task Stream_CommentLinesAndBlankLines_AreIgnored()
    {
        const string sse =
            """
            : ping

            data: {"choices":[{"index":0,"delta":{"content":"ok"}}]}

            : another keep-alive

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var turn = await CollectAsync(Client(sse));

        Assert.Equal("ok", turn.Text);
    }

    [Fact]
    public async Task Stream_MalformedJsonLine_IsSkippedNotFatal()
    {
        const string sse =
            """
            data: {"choices":[{"index":0,"delta":{"content":"A"}}]}

            data: {this is broken json

            data: {"choices":[{"index":0,"delta":{"content":"B"}}]}

            data: [DONE]

            """;

        var turn = await CollectAsync(Client(sse));

        Assert.Equal("AB", turn.Text);
    }

    [Fact]
    public async Task Request_ToolsSchemaIsSentWhenProvided()
    {
        var handler = new StubHandler(
            """
            data: {"choices":[{"index":0,"delta":{"content":"x"}}]}

            data: [DONE]

            """);

        var client = new DeepSeekClient(Options(), new HttpClient(handler));

        var tools = System.Text.Json.JsonDocument.Parse(
            """[{"type":"function","function":{"name":"ws_read","description":"d","parameters":{"type":"object"}}}]""")
            .RootElement.Clone();

        await client.CompleteAsync(new[] { ChatMessage.User("hi") }, tools);

        Assert.Single(handler.RequestBodies);

        var body = handler.RequestBodies[0];
        Assert.Contains("\"tools\"", body);
        Assert.Contains("ws_read", body);
        Assert.Contains("\"stream\":true", body);
    }

    [Fact]
    public async Task Request_NoTools_OmitsToolsField()
    {
        var handler = new StubHandler(
            """
            data: {"choices":[{"index":0,"delta":{"content":"x"}}]}

            data: [DONE]

            """);

        var client = new DeepSeekClient(Options(), new HttpClient(handler));
        await client.CompleteAsync(new[] { ChatMessage.User("hi") }, tools: null);

        Assert.DoesNotContain("\"tools\"", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Request_EmptyMessages_Throws()
    {
        var client = Client("data: [DONE]\n\n");

        await Assert.ThrowsAsync<LlmException>(
            () => client.CompleteAsync(Array.Empty<ChatMessage>(), tools: null));
    }

    [Fact]
    public void ChatUrl_NormalizesCommonForms()
    {
        Assert.Equal("https://api.deepseek.com/chat/completions",
            new LlmOptions { ApiKey = "k", BaseUrl = "https://api.deepseek.com" }.ChatUrl);

        Assert.Equal("https://api.deepseek.com/v1/chat/completions",
            new LlmOptions { ApiKey = "k", BaseUrl = "https://api.deepseek.com/v1" }.ChatUrl);

        // 已经是完整地址则不再追加
        Assert.Equal("https://api.deepseek.com/chat/completions",
            new LlmOptions { ApiKey = "k", BaseUrl = "https://api.deepseek.com/chat/completions" }.ChatUrl);
    }
}

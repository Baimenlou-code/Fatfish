using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FatFish.Core.Agent;
using FatFish.Core.Llm;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 双人核验与编排层的集成测试。
///
/// 这一组守的是**顺序与协议**，而不是判定本身：
///   · 核验必须在工具执行与人工报批**之前**（否则用户先批了一个会被驳回的动作）；
///   · 驳回时工具绝对不能执行（文件不能出现）；
///   · 回填必须覆盖**每个** tool_call（少一条，下一次请求会被服务端直接拒绝，
///     而错误信息完全指不到这里）。
/// </summary>
public class AgentLoopVerifyTests : IDisposable
{
    private readonly string _root;

    public AgentLoopVerifyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_verify_" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    // ==================================================================
    // 桩
    // ==================================================================

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<string> _script;

        public int Calls { get; private set; }

        public SequenceHandler(params string[] responses) => _script = new Queue<string>(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var body = _script.Count > 0 ? _script.Dequeue() : SseText("（无脚本）");
            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static string SseText(string text)
    {
        var json = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { index = 0, delta = new { content = text }, finish_reason = (string?)null },
            },
        });

        return $"data: {json}\n\ndata: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}]}}\n\ndata: [DONE]\n\n";
    }

    private static string SseToolCalls(params (string Id, string Name, string Args)[] calls)
    {
        var items = calls.Select((c, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["id"] = c.Id,
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"] = c.Name,
                ["arguments"] = c.Args,
            },
        }).ToArray();

        var start = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { tool_calls = items },
                    finish_reason = (string?)null,
                },
            },
        });

        return $"data: {start}\n\ndata: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"tool_calls\"}}]}}\n\ndata: [DONE]\n\n";
    }

    private sealed class TestObserver : IAgentObserver
    {
        public List<string> Statuses { get; } = new();
        public List<ToolResult> Results { get; } = new();
        public int ApprovalRequests { get; private set; }

        public bool ApproveAll { get; set; } = true;

        public void OnStatus(string text) => Statuses.Add(text);

        public void OnToolCallFinished(string toolName, ToolResult result) => Results.Add(result);

        public ValueTask<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
        {
            ApprovalRequests++;
            return ValueTask.FromResult(ApproveAll);
        }
    }

    private (AgentLoop Loop, TestObserver Observer, List<string> Logs, Func<int> VerifierCalls) Build(
        VerifyOptions verifyOptions,
        Func<string, string, CancellationToken, Task<string>> verifierChat,
        params string[] script)
    {
        var guard = new PathGuard(_root);
        var tickets = new ReadTicketStore(guard);
        var policy = new ApprovalPolicy(guard);

        var context = new ToolContext
        {
            Guard = guard,
            Tickets = tickets,
            Approval = policy,
        };

        var registry = new ToolRegistry(new ITool[]
        {
            new WsReadTool(),
            new WsListTool(),
            new WsWriteTool(),
        });

        var handler = new SequenceHandler(script);
        var llmOptions = new LlmOptions
        {
            ApiKey = "test",
            BaseUrl = "https://example.invalid",
            Model = "test",
        };

        var llm = new DeepSeekClient(llmOptions, new HttpClient(handler));

        var calls = 0;

        Func<string, string, CancellationToken, Task<string>> counting = (system, user, token) =>
        {
            calls++;
            return verifierChat(system, user, token);
        };

        var service = new VerifierService(verifyOptions, counting);
        var logs = new List<string>();
        var session = new VerifySession(service, msg => logs.Add(msg));

        var loop = new AgentLoop(
            llm, registry, context, policy,
            new AgentOptions { MaxRounds = 4 },
            session,
            new UserEventLog());

        return (loop, new TestObserver(), logs, () => calls);
    }

    private static VerifyOptions Opts(
        VerifyMode mode = VerifyMode.Auto,
        bool strict = true,
        int maxRetries = 2,
        int maxSupplements = 4,
        VerifyFailMode failMode = VerifyFailMode.Open) => new()
    {
        Mode = mode,
        Strict = strict,
        MaxRetries = maxRetries,
        MaxSupplements = maxSupplements,
        FailMode = failMode,
        Endpoint = new VerifierEndpoint { ApiKey = "verifier-test-key" },
    };

    private string TempPath(string name) => Path.Combine(_root, name);

    // ==================================================================
    // 驳回
    // ==================================================================

    [Fact]
    public async Task Revise_ToolIsNotExecuted_AndFeedbackGoesBack()
    {
        var (loop, observer, _, verifierCalls) = Build(
            Opts(),
            (_, _, _) => Task.FromResult("变更\n理由：这个路径不该写\n要求：改成 data/ 下面"),
            SseToolCalls(("call_1", "ws_write", """{"path":"danger.txt","content":"x"}""")),
            SseText("好的，我换个位置。"));

        var result = await loop.RunAsync("写个文件", observer);

        // 1) 核验真的被调用了
        Assert.Equal(1, verifierCalls());

        // 2) ★ 文件绝不能存在
        Assert.False(File.Exists(TempPath("danger.txt")));

        // 3) 工具没有被执行
        Assert.Empty(observer.Results);

        // 4) 人工报批根本没轮到（核验在报批之前）
        Assert.Equal(0, observer.ApprovalRequests);

        // 5) 审查意见进了历史，模型能看到
        var toolMsg = loop.History.Last(m => m.Role == "tool");
        Assert.Contains("双人核验·未通过", toolMsg.Content);
        Assert.Contains("改成 data/ 下面", toolMsg.Content);

        // 6) 模型在下一轮给出了答复
        Assert.Equal("好的，我换个位置。", result.FinalText);
    }

    [Fact]
    public async Task Revise_EveryToolCallGetsAToolMessage()
    {
        // 同一批次里 ws_read 只读、ws_write 有副作用：
        // 只有 ws_write 会被送审，但**两个** tool_call 都必须拿到 tool 消息 ——
        // 少一条，下一次请求就会被服务端以「tool 必须紧跟 tool_calls」直接拒绝。
        var (loop, observer, _, _) = Build(
            Opts(),
            (_, _, _) => Task.FromResult("变更\n理由：不许写"),
            SseToolCalls(
                ("call_1", "ws_read", """{"path":"a.txt"}"""),
                ("call_2", "ws_write", """{"path":"b.txt","content":"x"}""")),
            SseText("明白了。"));

        await loop.RunAsync("先读再写", observer);

        var toolMessages = loop.History.Where(m => m.Role == "tool").ToList();

        Assert.Equal(2, toolMessages.Count);
        Assert.Contains(toolMessages, m => m.ToolCallId == "call_1");
        Assert.Contains(toolMessages, m => m.ToolCallId == "call_2");

        // 被送审的那条拿到完整意见，未送审的那条拿到「一并暂缓」
        Assert.Contains(toolMessages, m => m.Content!.Contains("双人核验·未通过", StringComparison.Ordinal));
        Assert.Contains(toolMessages, m => m.Content!.Contains("一并暂缓", StringComparison.Ordinal));

        // 一个都没执行
        Assert.False(File.Exists(TempPath("b.txt")));
        Assert.Empty(observer.Results);
    }

    // ==================================================================
    // 放行
    // ==================================================================

    [Fact]
    public async Task Approve_ToolExecutes()
    {
        var (loop, observer, _, _) = Build(
            Opts(),
            (_, _, _) => Task.FromResult("y"),
            SseToolCalls(("call_1", "ws_write", """{"path":"ok.txt","content":"写好了"}""")),
            SseText("写完了。"));

        var result = await loop.RunAsync("写个文件", observer);

        Assert.True(File.Exists(TempPath("ok.txt")));
        Assert.Equal("写好了", await File.ReadAllTextAsync(TempPath("ok.txt")));

        // 核验通过后仍然要过人工报批（两道闸都不能少）
        Assert.Equal(1, observer.ApprovalRequests);

        Assert.Single(observer.Results);
        Assert.True(observer.Results[0].Ok);

        Assert.Equal("写完了。", result.FinalText);
    }

    [Fact]
    public async Task Approve_JsonVerdict_AlsoExecutes()
    {
        var (loop, observer, _, _) = Build(
            Opts(),
            (_, _, _) => Task.FromResult("""{"verdict":"approve","reason":"可逆小动作"}"""),
            SseToolCalls(("call_1", "ws_write", """{"path":"ok2.txt","content":"x"}""")),
            SseText("好了。"));

        await loop.RunAsync("写", observer);

        Assert.True(File.Exists(TempPath("ok2.txt")));
    }

    // ==================================================================
    // 严格模式超限
    // ==================================================================

    [Fact]
    public async Task Strict_RetryLimitReached_StopsTheRun()
    {
        var (loop, observer, _, _) = Build(
            Opts(strict: true, maxRetries: 0),
            (_, _, _) => Task.FromResult("变更\n理由：不行"),
            SseToolCalls(("call_1", "ws_write", """{"path":"blocked.txt","content":"x"}""")),
            SseText("（不该走到这里）"));

        var result = await loop.RunAsync("写", observer);

        Assert.Equal("verify_blocked", result.StopReason);
        Assert.False(File.Exists(TempPath("blocked.txt")));

        // 拦截时给模型的话里必须明确「别再试了」
        Assert.Contains("已超出核验重试上限", result.FinalText);
        Assert.Contains("等待进一步指示", result.FinalText);
    }

    [Fact]
    public async Task Lenient_RetryLimitReached_StillExecutesAfterApproval()
    {
        var (loop, observer, _, _) = Build(
            Opts(strict: false, maxRetries: 0, maxSupplements: 0),
            (_, _, _) => Task.FromResult("变更\n理由：不太行"),
            SseToolCalls(("call_1", "ws_write", """{"path":"lenient.txt","content":"x"}""")),
            SseText("好了。"));

        await loop.RunAsync("写", observer);

        // 宽松模式：超限放行，但仍要人工点头
        Assert.Equal(1, observer.ApprovalRequests);
        Assert.True(File.Exists(TempPath("lenient.txt")));
    }

    // ==================================================================
    // 送审范围
    // ==================================================================

    [Fact]
    public async Task AutoMode_ReadOnlyTool_IsNotSentToVerifier()
    {
        await File.WriteAllTextAsync(TempPath("a.txt"), "内容");

        var (loop, observer, _, verifierCalls) = Build(
            Opts(mode: VerifyMode.Auto),
            (_, _, _) => Task.FromResult("y"),
            SseToolCalls(("call_1", "ws_read", """{"path":"a.txt"}""")),
            SseText("读到了。"));

        await loop.RunAsync("读一下", observer);

        Assert.Equal(0, verifierCalls());
        Assert.Single(observer.Results);
        Assert.True(observer.Results[0].Ok);
    }

    [Fact]
    public async Task AllMode_ReadOnlyTool_IsSentToVerifier()
    {
        await File.WriteAllTextAsync(TempPath("a.txt"), "内容");

        var (loop, observer, _, verifierCalls) = Build(
            Opts(mode: VerifyMode.All),
            (_, _, _) => Task.FromResult("y"),
            SseToolCalls(("call_1", "ws_read", """{"path":"a.txt"}""")),
            SseText("读到了。"));

        await loop.RunAsync("读一下", observer);

        Assert.Equal(1, verifierCalls());
        Assert.True(observer.Results[0].Ok);
    }

    [Fact]
    public async Task OffMode_NeverCallsVerifier()
    {
        var (loop, observer, _, verifierCalls) = Build(
            Opts(mode: VerifyMode.Off),
            (_, _, _) => throw new InvalidOperationException("不该被调用"),
            SseToolCalls(("call_1", "ws_write", """{"path":"off.txt","content":"x"}""")),
            SseText("好了。"));

        await loop.RunAsync("写", observer);

        Assert.Equal(0, verifierCalls());
        Assert.True(File.Exists(TempPath("off.txt")));
    }

    // ==================================================================
    // 服务故障
    // ==================================================================

    [Fact]
    public async Task VerifierDown_FailOpen_StillExecutes()
    {
        var (loop, observer, _, _) = Build(
            Opts(failMode: VerifyFailMode.Open),
            (_, _, _) => throw new HttpRequestException("network down"),
            SseToolCalls(("call_1", "ws_write", """{"path":"open.txt","content":"x"}""")),
            SseText("好了。"));

        await loop.RunAsync("写", observer);

        // 核验挂了不拦：人工报批还在后面兜底
        Assert.Equal(1, observer.ApprovalRequests);
        Assert.True(File.Exists(TempPath("open.txt")));

        Assert.Contains(observer.Statuses, s => s.Contains("fail-open", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifierDown_FailClosed_Blocks()
    {
        var (loop, observer, _, _) = Build(
            Opts(failMode: VerifyFailMode.Closed),
            (_, _, _) => throw new HttpRequestException("network down"),
            SseToolCalls(("call_1", "ws_write", """{"path":"closed.txt","content":"x"}""")),
            SseText("（不该走到这里）"));

        var result = await loop.RunAsync("写", observer);

        Assert.False(File.Exists(TempPath("closed.txt")));
        Assert.Empty(observer.Results);
        Assert.Equal("verify_blocked", result.StopReason);
    }

    // ==================================================================
    // 送审材料
    // ==================================================================

    [Fact]
    public async Task VerifierReceivesGoalPlanAndAction()
    {
        string? captured = null;

        var (loop, observer, _, _) = Build(
            Opts(),
            (_, user, _) =>
            {
                captured = user;
                return Task.FromResult("y");
            },
            SseToolCalls(("call_1", "ws_write", """{"path":"x.txt","content":"正文"}""")),
            SseText("好了。"));

        await loop.RunAsync("请帮我写个文件", observer);

        Assert.NotNull(captured);
        var promptText = captured!;
        Assert.Contains("请帮我写个文件", promptText);
        Assert.Contains("x.txt", promptText);
        Assert.Contains("正文", promptText);
        Assert.Contains("运行环境事实", promptText);
    }

    /// <summary>
    /// 用户原声必须进材料 —— 否则「用户亲手批准的动作」会被审查员判成越权。
    /// 这一条内容来自主程序，模型无法伪造。
    /// </summary>
    [Fact]
    public async Task VerifierReceivesUserEvents()
    {
        string? captured = null;

        var (loop, observer, _, _) = Build(
            Opts(),
            (_, user, _) =>
            {
                captured = user;
                return Task.FromResult("y");
            },
            SseToolCalls(("call_1", "ws_write", """{"path":"y.txt","content":"x"}""")),
            SseText("好了。"));

        await loop.RunAsync("写吧", observer);

        Assert.NotNull(captured);
        var promptText = captured!;
        Assert.Contains("用户真实操作记录", promptText);
        Assert.Contains("用户输入：写吧", promptText);
    }
}

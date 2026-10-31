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
/// AgentLoop 集成测试 —— 用桩模型驱动完整的「模型 ↔ 工具」闭环。
///
/// 这些测试守住的是编排层最关键的三条不变量：
///   1. 没有工具调用时，一轮就结束，不会空转；
///   2. 有工具调用时，结果必须回填历史，模型才能看到；
///   3. 用户拒绝审批时，工具**绝对不执行**（失败必须发生在执行之前）。
/// </summary>
public class AgentLoopTests : IDisposable
{
    private readonly string _root;

    public AgentLoopTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_agent_" + Guid.NewGuid().ToString("N")[..12]);
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
            // 清理失败不影响结论
        }

        GC.SuppressFinalize(this);
    }

    // ---------- 桩 ----------

    /// <summary>按脚本顺序返回 SSE 响应。</summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<string> _script;

        public int Calls { get; private set; }

        public SequenceHandler(params string[] responses)
        {
            _script = new Queue<string>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;

            var body = _script.Count > 0 ? _script.Dequeue() : SseText("（无脚本）");

            var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>构造一段「只返回文本」的 SSE。</summary>
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

    /// <summary>构造一段「请求调用某个工具」的 SSE。</summary>
    private static string SseToolCall(string id, string name, string argsJson)
    {
        var start = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new
                            {
                                index = 0,
                                id,
                                type = "function",
                                function = new { name, arguments = argsJson },
                            },
                        },
                    },
                    finish_reason = (string?)null,
                },
            },
        });

        return $"data: {start}\n\ndata: {{\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"tool_calls\"}}]}}\n\ndata: [DONE]\n\n";
    }

    private sealed class TestObserver : IAgentObserver
    {
        public List<string> Texts { get; } = new();
        public List<string> ToolCalls { get; } = new();
        public List<ToolResult> Results { get; } = new();

        public bool ApproveAll { get; set; }
        public int ApprovalRequests { get; private set; }

        public void OnTextDelta(string text) => Texts.Add(text);

        public void OnToolCallStarted(string toolName, JsonElement args) => ToolCalls.Add(toolName);

        public void OnToolCallFinished(string toolName, ToolResult result) => Results.Add(result);

        public ValueTask<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
        {
            ApprovalRequests++;
            return ValueTask.FromResult(ApproveAll);
        }

        public string AllText => string.Concat(Texts);
    }

    private (AgentLoop Loop, TestObserver Observer, SequenceHandler Handler) Build(params string[] script)
    {
        var guard = new PathGuard(_root);
        var tickets = new ReadTicketStore(guard);
        var policy = new ApprovalPolicy(guard) { Level = ApprovalLevel.ReadFree };

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
            new WsSearchTool(),
            new WsWriteTool(),
            new WsAppendTool(),
            new WsReplaceTool(),
            new WsDeleteTool(),
            new WsRunCmdTool(),
        });

        var handler = new SequenceHandler(script);
        var options = new LlmOptions
        {
            ApiKey = "test",
            BaseUrl = "https://example.invalid",
            Model = "test",
        };

        var llm = new DeepSeekClient(options, new HttpClient(handler));
        var loop = new AgentLoop(llm, registry, context, policy, new AgentOptions { MaxRounds = 4 });

        return (loop, new TestObserver(), handler);
    }

    // ---------- 用例 ----------

    [Fact]
    public async Task Run_PlainAnswer_EndsAfterOneRound()
    {
        var (loop, observer, handler) = Build(SseText("你好，我是肥鱼。"));

        var result = await loop.RunAsync("你好", observer);

        Assert.Equal("你好，我是肥鱼。", result.FinalText);
        Assert.Equal(1, result.Rounds);
        Assert.Equal(0, result.ToolCallCount);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(observer.ToolCalls);
    }

    [Fact]
    public async Task Run_WithToolCall_ExecutesToolAndContinues()
    {
        // 工作台里先放一个文件
        var target = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(target, "肥鱼爱吃 token");

        var (loop, observer, handler) = Build(
            SseToolCall("call_1", "ws_read", "{\"path\":\"notes.txt\"}"),
            SseText("文件内容是：肥鱼爱吃 token"));

        var result = await loop.RunAsync("读一下 notes.txt", observer);

        // 两轮：先调工具，再作答
        Assert.Equal(2, result.Rounds);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(2, handler.Calls);

        Assert.Single(observer.ToolCalls);
        Assert.Equal("ws_read", observer.ToolCalls[0]);

        // ws_read 是只读工具，不该触发审批
        Assert.Equal(0, observer.ApprovalRequests);

        // 工具真的要执行成功
        Assert.Single(observer.Results);
        Assert.True(observer.Results[0].Ok);

        // 历史里应该有 tool 结果消息，模型才能「看到」文件内容
        var toolMsg = loop.History.LastOrDefault(m => m.Role == "tool");
        Assert.NotNull(toolMsg);
        Assert.Contains("肥鱼爱吃 token", toolMsg!.Content);
        Assert.Equal("call_1", toolMsg.ToolCallId);

        Assert.Equal("文件内容是：肥鱼爱吃 token", result.FinalText);
    }

    [Fact]
    public async Task Run_ApprovalDenied_ToolIsNotExecuted()
    {
        var (loop, observer, handler) = Build(
            SseToolCall("call_1", "ws_write", "{\"path\":\"secret.txt\",\"content\":\"不该写进去\"}"),
            SseText("好的，我不写了。"));

        observer.ApproveAll = false;   // 用户拒绝

        var result = await loop.RunAsync("把这个写到 secret.txt", observer);

        // 应该问过审批
        Assert.Equal(1, observer.ApprovalRequests);

        // ★ 关键断言：文件绝不能存在
        Assert.False(File.Exists(Path.Combine(_root, "secret.txt")));

        // 工具没有被执行（OnToolCallFinished 不该被调用）
        Assert.Empty(observer.Results);

        // 历史里的 tool 消息应该是拒绝说明
        var toolMsg = loop.History.Last(m => m.Role == "tool");
        Assert.Contains("拒绝", toolMsg.Content);

        // 模型收到拒绝后仍能给出回答
        Assert.Equal("好的，我不写了。", result.FinalText);
    }

    [Fact]
    public async Task Run_ApprovalGranted_WritesFile()    {
        var (loop, observer, handler) = Build(
            SseToolCall("call_1", "ws_write", "{\"path\":\"out.txt\",\"content\":\"肥鱼写入\"}"),
            SseText("写好了。"));

        observer.ApproveAll = true;

        var result = await loop.RunAsync("写个文件", observer);

        Assert.Equal(1, observer.ApprovalRequests);
        Assert.True(File.Exists(Path.Combine(_root, "out.txt")));
        Assert.Equal("肥鱼写入", await File.ReadAllTextAsync(Path.Combine(_root, "out.txt")));
        Assert.Single(observer.Results);
        Assert.True(observer.Results[0].Ok);
        Assert.Equal("写好了。", result.FinalText);
    }

    /// <summary>
    /// ★ 高危工具的审批必须真的生效。
    ///
    /// 这条测试守的是 ApprovalPolicy 里那个「只查 ApprovalRequired、
    /// 漏查 HighRiskTools」的缺陷：修之前 ws_run_cmd 会走到 NotNeeded，
    /// 于是文件被建出来而用户从未被问过。
    /// 断言分两层：问过审批 + 文件不存在（拒绝必须发生在执行之前）。
    /// </summary>
    [Fact]
    public async Task Run_HighRiskTool_RequiresApprovalBeforeExecuting()
    {
        var (loop, observer, handler) = Build(
            SseToolCall("call_1", "ws_run_cmd", "{\"command\":\"echo hi > created.txt\"}"),
            SseText("好的，我不执行。"));

        observer.ApproveAll = false;   // 用户拒绝

        var result = await loop.RunAsync("在工作台里建个文件", observer);

        Assert.Equal(1, observer.ApprovalRequests);
        Assert.False(File.Exists(Path.Combine(_root, "created.txt")));
        Assert.Empty(observer.Results);
        Assert.Equal("好的，我不执行。", result.FinalText);
    }

    [Fact]
    public async Task Run_ToolFails_ErrorGoesBackToModel()
    {
        // 读一个不存在的文件 —— 工具会失败，但循环必须继续而不是崩掉
        var (loop, observer, handler) = Build(
            SseToolCall("call_1", "ws_read", "{\"path\":\"no-such-file.txt\"}"),
            SseText("那个文件不存在。"));

        var result = await loop.RunAsync("读 no-such-file.txt", observer);

        Assert.Single(observer.Results);
        Assert.False(observer.Results[0].Ok);

        var toolMsg = loop.History.Last(m => m.Role == "tool");
        Assert.Contains("\"ok\":false", toolMsg.Content);

        Assert.Equal("那个文件不存在。", result.FinalText);
    }

    [Fact]
    public async Task Run_UnknownTool_DoesNotCrash()
    {
        var (loop, observer, handler) = Build(
            SseToolCall("call_1", "ws_not_exist", "{}"),
            SseText("工具不存在。"));

        var result = await loop.RunAsync("调用一个不存在的工具", observer);

        Assert.Single(observer.Results);
        Assert.False(observer.Results[0].Ok);
        Assert.Contains("未知工具", observer.Results[0].Error);

        Assert.Equal("工具不存在。", result.FinalText);
    }

    [Fact]
    public async Task Run_RoundLimit_StopsAndReports()
    {
        // 每轮都要求调工具 → 必然撞上 MaxRounds(4)
        var (loop, _, handler) = Build(
            SseToolCall("c1", "ws_read", "{\"path\":\"a.txt\"}"),
            SseToolCall("c2", "ws_read", "{\"path\":\"a.txt\"}"),
            SseToolCall("c3", "ws_read", "{\"path\":\"a.txt\"}"),
            SseToolCall("c4", "ws_read", "{\"path\":\"a.txt\"}"));

        var observer = new TestObserver();
        var result = await loop.RunAsync("无限循环", observer);

        Assert.True(result.HitRoundLimit);
        Assert.Equal(4, result.Rounds);
    }

    [Fact]
    public async Task ResetHistory_KeepsSystemPrompt()
    {
        var (loop, observer, _) = Build(SseText("答"));

        await loop.RunAsync("问题", observer);
        Assert.True(loop.History.Count > 1);

        loop.ResetHistory();

        Assert.Single(loop.History);
        Assert.Equal("system", loop.History[0].Role);
    }

    [Fact]
    public async Task Run_EmptyInput_DoesNothing()
    {
        var (loop, observer, handler) = Build(SseText("不该被调用"));

        var result = await loop.RunAsync("   ", observer);

        Assert.Equal(0, handler.Calls);
        Assert.Equal("empty_input", result.StopReason);
    }

    [Fact]
    public async Task Run_AccumulatesUsage()
    {
        var (loop, observer, _) = Build(SseText("hi"));

        await loop.RunAsync("a", observer);
        await loop.RunAsync("b", observer);
        await loop.RunAsync("c", observer);

        // 三次调用，每次一个 user 消息，历史应持续增长
        Assert.Equal(7, loop.History.Count); // 1 system + 3 user + 3 assistant
    }

    // ---------- 会话落盘的触发点 ----------

    [Fact]
    public async Task Traced_FiresForUserToolAssistant_AndFinalAnswer()
    {
        var (loop, observer, _) = Build(
            SseToolCall("call_1", "ws_list", "{\"path\":\"\"}"),
            SseText("目录是空的。"));

        var traced = new List<AgentTraceEntry>();
        loop.Traced += e => traced.Add(e);

        await loop.RunAsync("看一下目录", observer);

        // 顺序必须和「历史里实际的样子」一致：user → assistant(带tool_calls) → tool → assistant
        Assert.Equal(4, traced.Count);

        Assert.Equal("user", traced[0].Message.Role);
        Assert.Equal("看一下目录", traced[0].Message.Content);

        Assert.Equal("assistant", traced[1].Message.Role);
        Assert.Equal("ws_list", Assert.Single(traced[1].ToolNames!));

        Assert.Equal("tool", traced[2].Message.Role);
        Assert.Equal("ws_list", traced[2].ToolName);
        Assert.True(traced[2].ToolOk);
        Assert.NotNull(traced[2].ElapsedMs);

        Assert.Equal("assistant", traced[3].Message.Role);
        Assert.Null(traced[3].ToolNames);
        Assert.Equal("目录是空的。", traced[3].Message.Content);
    }

    [Fact]
    public async Task Traced_MarksRejectedTool_AsFailedWithoutElapsed()
    {
        var (loop, observer, _) = Build(
            SseToolCall("call_1", "ws_write", "{\"path\":\"a.txt\",\"content\":\"x\"}"),
            SseText("好的，我不写了。"));

        // ★ 用户拒绝 ≠ 工具失败。两者在 ChatMessage 里都是 ok:false，
        //   只有 ElapsedMs 能区分「跑了但失败」和「压根没被允许跑」。
        observer.ApproveAll = false;

        var traced = new List<AgentTraceEntry>();
        loop.Traced += e => traced.Add(e);

        await loop.RunAsync("写个文件", observer);

        var toolEntry = Assert.Single(traced, e => e.Message.Role == "tool");

        Assert.Equal("ws_write", toolEntry.ToolName);
        Assert.False(toolEntry.ToolOk);
        Assert.Equal(0L, toolEntry.ElapsedMs);
    }
}

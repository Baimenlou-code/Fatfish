using System.Net;
using System.Text;
using System.Text.Json;
using FatFish.Core.Agent;
using FatFish.Core.Llm;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 模型调用失败（非 2xx，如 554 空响应）时，失败必须**进入对话上下文**，
/// 而不是只在界面上闪一下 —— 否则下一轮模型对「上一轮根本没跑通」毫无所知。
/// 通知本身保持一句话，不往历史里灌提示词。
/// </summary>
public class AgentLoopFailureTests : IDisposable
{
    private readonly string _root;

    public AgentLoopFailureTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_fail_" + Guid.NewGuid().ToString("N")[..12]);
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

    /// <summary>总是返回同一个错误状态码、且响应体为空的桩。</summary>
    private sealed class ErrorHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;

        public ErrorHandler(HttpStatusCode code) => _code = code;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8),
            });
    }

    /// <summary>观察者的所有成员都有默认实现，这里什么都不用写。</summary>
    private sealed class NullObserver : IAgentObserver
    {
    }

    private AgentLoop Build(HttpStatusCode code)
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

        var registry = new ToolRegistry(Array.Empty<ITool>());
        var options = new LlmOptions
        {
            ApiKey = "test",
            BaseUrl = "https://example.invalid",
            Model = "test",
        };

        var llm = new DeepSeekClient(options, new HttpClient(new ErrorHandler(code)));
        return new AgentLoop(llm, registry, context, policy, new AgentOptions { MaxRounds = 4 });
    }

    [Fact]
    public async Task LlmFailure_IsWrittenIntoHistory()
    {
        var loop = Build((HttpStatusCode)554);

        var ex = await Assert.ThrowsAsync<LlmException>(
            () => loop.RunAsync("你好", new NullObserver()));

        Assert.Equal(554, ex.StatusCode);

        // system + user：失败通知合进那条 user，不额外新增消息
        var history = loop.History;
        Assert.Equal(2, history.Count);

        var last = history[^1];
        Assert.Equal("user", last.Role);
        Assert.Contains("你好", last.Content);          // 用户原话仍在
        Assert.Contains("[系统通知]", last.Content);     // 通知确实进了上下文
        Assert.Contains("554", last.Content);            // 带上了状态码
    }

    [Fact]
    public async Task LlmFailure_NoticeStaysShort()
    {
        var loop = Build((HttpStatusCode)554);

        await Assert.ThrowsAsync<LlmException>(() => loop.RunAsync("你好", new NullObserver()));

        var notice = loop.History[^1].Content!;
        var noticePart = notice[(notice.IndexOf("[系统通知]", StringComparison.Ordinal))..];

        // 一句话：只报「谁失败了 + 为什么」，不往历史里灌提示词
        Assert.True(noticePart.Length <= 120, $"通知太长（{noticePart.Length} 字）：{noticePart}");
        Assert.DoesNotContain("臆造", noticePart);
        Assert.DoesNotContain("请据此判断", noticePart);
    }

    [Fact]
    public async Task LlmFailure_DoesNotInflateHistory()
    {
        var loop = Build((HttpStatusCode)554);

        await Assert.ThrowsAsync<LlmException>(() => loop.RunAsync("第一句", new NullObserver()));
        await Assert.ThrowsAsync<LlmException>(() => loop.RunAsync("第二句", new NullObserver()));

        var history = loop.History;

        // 两次失败只让历史多出两条用户输入，没有多出「通知消息」
        Assert.Equal(3, history.Count);
        Assert.Equal("system", history[0].Role);
        Assert.All(history.Skip(1), m => Assert.Contains("[系统通知]", m.Content));
        Assert.Contains("第一句", history[1].Content);
        Assert.Contains("第二句", history[2].Content);
    }

    [Fact]
    public async Task LlmFailure_CarriesStatusCodeIntoNotice()
    {
        var loop = Build((HttpStatusCode)429);

        var ex = await Assert.ThrowsAsync<LlmException>(
            () => loop.RunAsync("限流测一下", new NullObserver()));

        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("429", loop.History[^1].Content);
    }
}

using System.Text;
using System.Text.Json;
using FatFish.Core.Agent;
using FatFish.Core.Llm;
using FatFish.Core.Safety;
using FatFish.Core.Session;
using FatFish.Core.Tools;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 会话落盘的测试。
///
/// 这里每条用例都对应一个「不这么做会出什么错」，而不是「这个方法能被调用」：
///   · 恢复时把 tool 消息也塞回上下文 → 下一次提问直接超窗（工具输出单条可达 30k 字符）；
///   · 崩溃时最后一行只写了一半 → 若解析失败就让整个文件不可用，会话全丢；
///   · 同一秒内开两个会话 → 文件名排序不稳，「恢复最近一次」恢复成上一次；
///   · 写日志失败让异常冒出来 → 磁盘满了就连天都聊不了；
///   · 末尾留着「问了没答完」的 user → 恢复后历史是 user → user，模型以为用户在重复提问。
/// </summary>
public class SessionStoreTests : IDisposable
{
    private readonly string _root;

    public SessionStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_sess_" + Guid.NewGuid().ToString("N")[..12]);
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

    private SessionLoadResult? LoadLatest(int max = 40) => SessionStore.LoadLatest(_root, max);

    private string SessionDir => Path.Combine(_root, "logs", "sessions");

    /// <summary>常量数组提出来，避免每条用例都新建一个（CA1861）。</summary>
    private static readonly string[] ListToolNames = { "ws_list" };

    private static readonly string[] ReadToolNames = { "ws_read" };

    /// <summary>
    /// 在「写句柄还开着」的情况下读文件。
    ///
    /// 不能用 File.ReadAllText：它内部以 share=Read 打开，而 Windows 会检查
    /// 「本次 share 是否允许已有句柄的 Write 访问」，结论是不允许 → 直接报文件被占用。
    /// 这条测试因此同时守两件事：内容确实写进去了，且**会话进行中外部也能读**。
    /// </summary>
    private static string ReadAllowingConcurrentWrite(string path)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    // ------------------------------------------------------------------
    // 基本往返
    // ------------------------------------------------------------------

    [Fact]
    public void AppendAndLoad_RoundTripsUserAndAssistant()
    {
        using (var store = SessionStore.Create(_root))
        {
            store.Append(ChatMessage.User("你好，我是用户"));
            store.Append(ChatMessage.Assistant("你好，我是肥鱼"));
        }

        var loaded = LoadLatest();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Messages.Count);
        Assert.Equal("user", loaded.Messages[0].Role);
        Assert.Equal("你好，我是用户", loaded.Messages[0].Content);
        Assert.Equal("assistant", loaded.Messages[1].Role);
        Assert.Equal("你好，我是肥鱼", loaded.Messages[1].Content);
        Assert.Equal(0, loaded.SkippedLines);
    }

    [Fact]
    public void Append_WritesOneJsonObjectPerLine()
    {
        string path;

        using (var store = SessionStore.Create(_root))
        {
            store.Append(ChatMessage.User("第一行"));
            store.Append(ChatMessage.Assistant("第二行"));
            path = store.FilePath;
        }

        var lines = File.ReadAllLines(path);

        Assert.Equal(2, lines.Length);

        string? firstSid = null;

        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);

            Assert.Equal(SessionStore.FormatVersion, doc.RootElement.GetProperty("v").GetInt32());

            var sid = doc.RootElement.GetProperty("sid").GetString();
            Assert.False(string.IsNullOrEmpty(sid), "sid 不能为空");

            // 同一个文件里的记录必须属于同一个会话，否则「按 sid 归组」这条路就断了
            firstSid ??= sid;
            Assert.Equal(firstSid, sid);
        }
    }

    // ------------------------------------------------------------------
    // ★ 恢复时必须剥掉工具往返
    // ------------------------------------------------------------------

    [Fact]
    public void Load_SkipsToolTraffic_AndKeepsOnlyDialogue()
    {
        using (var store = SessionStore.Create(_root))
        {
            store.Append(ChatMessage.User("看一下目录"));

            // 中间轮：模型一个字都没说，直接调工具
            store.Append(
                ChatMessage.Assistant(null, new[]
                {
                    new ToolCall { Id = "call_1", Name = "ws_list", Arguments = "{\"path\":\"\"}" },
                }),
                toolNames: ListToolNames);

            store.Append(
                ChatMessage.ToolResult("call_1", "{\"ok\":true,\"data\":{\"entries\":[]}}"),
                toolName: "ws_list",
                toolOk: true,
                elapsedMs: 12);

            store.Append(ChatMessage.Assistant("目录是空的。"));
        }

        var loaded = LoadLatest();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Messages.Count);
        Assert.Equal("看一下目录", loaded.Messages[0].Content);
        Assert.Equal("目录是空的。", loaded.Messages[1].Content);

        // 文件里其实有 4 条记录 —— 两条被有意丢弃，不是丢失
        Assert.Equal(4, loaded.TotalEntries);
    }

    [Fact]
    public void Load_AssistantWithToolCalls_IsNotRestored()
    {
        using (var store = SessionStore.Create(_root))
        {
            store.Append(ChatMessage.User("问"));
            store.Append(
                ChatMessage.Assistant("我先读一下文件", new[]
                {
                    new ToolCall { Id = "c1", Name = "ws_read", Arguments = "{\"path\":\"a.txt\"}" },
                }),
                toolNames: ReadToolNames);
            store.Append(ChatMessage.ToolResult("c1", "{}"), toolName: "ws_read", toolOk: true, elapsedMs: 1);
            store.Append(ChatMessage.Assistant("读完了。"));
        }

        var loaded = LoadLatest();

        // 「我先读一下文件」这半句话没有下文（后半个动作是工具），回填会让模型看到断句
        Assert.Equal(2, loaded!.Messages.Count);
        Assert.Equal("读完了。", loaded.Messages[1].Content);
    }

    // ------------------------------------------------------------------
    // 崩溃容错
    // ------------------------------------------------------------------

    [Fact]
    public void Load_SurvivesTruncatedLastLine()
    {
        var store = SessionStore.Create(_root);
        store.Append(ChatMessage.User("问一"));
        store.Append(ChatMessage.Assistant("答一"));

        var path = store.FilePath;
        store.Dispose();

        // 模拟「写到一半断电」：追加一条不完整的 JSON，且不带换行
        File.AppendAllText(path, "{\"v\":1,\"role\":\"user\",\"text\":\"半", new UTF8Encoding(false));

        var loaded = SessionStore.LoadFile(path);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Messages.Count);
        Assert.Equal("答一", loaded.Messages[1].Content);
        Assert.Equal(1, loaded.SkippedLines);
    }

    [Fact]
    public void Load_EmptyDirectory_ReturnsNull()
    {
        Assert.Null(SessionStore.LoadLatest(Path.Combine(_root, "does-not-exist")));
        Assert.Null(SessionStore.LoadLatest(_root));
    }

    // ------------------------------------------------------------------
    // 末尾悬空的 user
    // ------------------------------------------------------------------

    [Fact]
    public void Load_DropsDanglingUser_ThatWasNeverAnswered()
    {
        using (var store = SessionStore.Create(_root))
        {
            store.Append(ChatMessage.User("第一问"));
            store.Append(ChatMessage.Assistant("第一答"));
            store.Append(ChatMessage.User("第二问（窗口在这一轮被关了）"));
        }

        var loaded = LoadLatest();

        Assert.NotNull(loaded);
        Assert.True(loaded!.DroppedDanglingUser);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal("第一问", loaded.Messages[0].Content);

        // 不能出现「恢复后最后一条是 user」——那会让模型以为用户在重复提问
        Assert.Equal("assistant", loaded.Messages[^1].Role);
    }

    // ------------------------------------------------------------------
    // 截断
    // ------------------------------------------------------------------

    [Fact]
    public void Append_ClipsHugeText_AndMarksTruncated()
    {
        var huge = new string('肥', SessionStore.MaxTextChars + 500);

        using (var store = SessionStore.Create(_root))
        {
            store.Append(ChatMessage.User("读个大文件"));
            store.Append(
                ChatMessage.ToolResult("c1", huge),
                toolName: "ws_read",
                toolOk: true,
                elapsedMs: 5);

            var raw = ReadAllowingConcurrentWrite(store.FilePath);

            Assert.Contains("\"truncated\":true", raw);
            Assert.Contains("已截断 500 字符", raw);

            // 整个文件不该被 30k 字符的工具输出撑大
            Assert.True(raw.Length < SessionStore.MaxTextChars + 1_000, $"文件 {raw.Length} 字符");
        }
    }

    // ------------------------------------------------------------------
    // 会话切换
    // ------------------------------------------------------------------

    [Fact]
    public void StartNew_KeepsPreviousFile_AndCreatesAnother()
    {
        using var store = SessionStore.Create(_root);

        store.Append(ChatMessage.User("旧会话"));
        var first = store.FilePath;

        store.StartNew();

        Assert.NotEqual(first, store.FilePath);
        Assert.True(File.Exists(first), "旧会话文件被删掉了");
        Assert.Equal(2, Directory.GetFiles(SessionDir, "chat_*.jsonl").Length);
    }

    [Fact]
    public void LoadLatest_PicksNewestSession()
    {
        using (var store = SessionStore.Create(_root))
        {
            store.Append(ChatMessage.User("旧会话"));
            store.Append(ChatMessage.Assistant("旧回答"));

            // ★ 同一毫秒内连开两次：靠文件名里的递增序号定序，
            //   若只靠时间戳，这里会随机恢复成旧会话。
            store.StartNew();

            store.Append(ChatMessage.User("新会话"));
            store.Append(ChatMessage.Assistant("新回答"));
        }

        var loaded = LoadLatest();

        Assert.NotNull(loaded);
        Assert.Equal("新会话", loaded!.Messages[0].Content);
    }

    [Fact]
    public void LoadLatest_SkipsEmptyNewestFile()
    {
        using (var store = SessionStore.Create(_root))
        {
            store.Append(ChatMessage.User("有内容的会话"));
            store.Append(ChatMessage.Assistant("回答"));
        }

        // 开一个新会话但一条都不写 —— 相当于「点了 Ctrl+N 就走了」
        using (var blank = SessionStore.Create(_root))
        {
            Assert.True(File.Exists(blank.FilePath));
        }

        var loaded = LoadLatest();

        Assert.NotNull(loaded);
        Assert.Equal("有内容的会话", loaded!.Messages[0].Content);
    }

    // ------------------------------------------------------------------
    // 条数上限与起点
    // ------------------------------------------------------------------

    [Fact]
    public void Load_RespectsMaxMessages_AndStartsAtUser()
    {
        using (var store = SessionStore.Create(_root))
        {
            for (var i = 0; i < 10; i++)
            {
                store.Append(ChatMessage.User($"问{i}"));
                store.Append(ChatMessage.Assistant($"答{i}"));
            }
        }

        var loaded = LoadLatest(max: 4);

        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Messages.Count);
        Assert.Equal("user", loaded.Messages[0].Role);
        Assert.Equal("问8", loaded.Messages[0].Content);
        Assert.Equal("答9", loaded.Messages[^1].Content);
    }

    // ------------------------------------------------------------------
    // 写入失败不能扬出来
    // ------------------------------------------------------------------

    [Fact]
    public void Append_AfterDispose_DoesNotThrow()
    {
        var store = SessionStore.Create(_root);
        store.Append(ChatMessage.User("a"));
        store.Dispose();

        var ex = Record.Exception(() => store.Append(ChatMessage.User("b")));

        Assert.Null(ex);
        Assert.Equal(1, store.EntryCount);
    }

    // ------------------------------------------------------------------
    // 装回 AgentLoop
    // ------------------------------------------------------------------

    [Fact]
    public void RestoreHistory_ReplacesNonSystemMessages_AndSkipsToolMessages()
    {
        var loop = BuildLoop();

        // 先放一条旧的，验证「替换」而不是「追加」
        loop.RestoreHistory(new[] { ChatMessage.User("更早的问题"), ChatMessage.Assistant("更早的回答") });
        Assert.Equal(3, loop.History.Count);

        var loaded = loop.RestoreHistory(new[]
        {
            ChatMessage.User("问题"),
            ChatMessage.ToolResult("c1", "{\"ok\":true}"),
            ChatMessage.Assistant(null, new[] { new ToolCall { Id = "c1", Name = "ws_read" } }),
            ChatMessage.Assistant("回答"),
        });

        Assert.Equal(2, loaded);
        Assert.Equal(3, loop.History.Count);   // system + 问题 + 回答
        Assert.Equal("system", loop.History[0].Role);
        Assert.Equal("问题", loop.History[1].Content);
        Assert.Equal("回答", loop.History[2].Content);
    }

    private static AgentLoop BuildLoop()
    {
        var root = Path.Combine(Path.GetTempPath(), "fatfish_loop_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        var guard = new PathGuard(root);
        var policy = new ApprovalPolicy(guard);

        var context = new ToolContext
        {
            Guard = guard,
            Tickets = new ReadTicketStore(guard),
            Approval = policy,
        };

        var llm = new DeepSeekClient(
            new LlmOptions { ApiKey = "test", BaseUrl = "https://example.invalid", Model = "test" },
            new HttpClient(new HttpClientHandler()));

        return new AgentLoop(llm, new ToolRegistry(Array.Empty<ITool>()), context, policy);
    }
}

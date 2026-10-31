using System.Text.Json;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 五个小工具（ws_mkdir / ws_where / ws_forget / ws_cd / ws_cd_approve）的行为测试。
///
/// ★ 这批测试的重点是「上下文是否真的跟着会话走」：
///   ws_cd 换根之后，**别的工具**（ws_write / ws_read）必须按新根解析路径。
///   这条性质一旦坏了，症状是「文件写到了另一个目录」，而调用方看不出任何异常。
/// </summary>
public class SmallToolsTests : IDisposable
{
    private readonly string _base;
    private readonly string _home;
    private readonly string _outside;
    private readonly WorkspaceSession _ws;
    private readonly ToolContext _context;
    private readonly ToolRegistry _registry;

    public SmallToolsTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "fatfish_small_" + Guid.NewGuid().ToString("N")[..10]);
        _home = Path.Combine(_base, "home");
        _outside = Path.Combine(_base, "outside");

        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_outside);

        _ws = new WorkspaceSession(_home);

        _context = new ToolContext
        {
            // 这里刻意**同时**给 Guard/Tickets 与 Workspace：
            // 宿主的真实装配就是这样，且正好验证「Workspace 优先」这条规则。
            Guard = _ws.Guard,
            Tickets = _ws.Tickets,
            Workspace = _ws,
            Approval = new ApprovalPolicy(_ws) { Level = ApprovalLevel.ReadFree },
        };

        _registry = new ToolRegistry(new ITool[]
        {
            new WsWhereTool(),
            new WsReadTool(),
            new WsAppendTool(),
            new WsWriteTool(),
            new WsMkdirTool(),
            new WsForgetTool(),
            new WsCdTool(),
            new WsCdApproveTool(),
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
            // 忽略清理失败
        }

        GC.SuppressFinalize(this);
    }

    // ---------- 辅助 ----------

    private async Task<ToolResult> Call(string name, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return await _registry.InvokeAsync(name, _context, doc.RootElement.Clone());
    }

    private static JsonElement Data(ToolResult r)
    {
        Assert.NotNull(r.Data);
        return r.Data!.Value;
    }

    private static string Str(JsonElement el, string name)
        => el.GetProperty(name).GetString() ?? string.Empty;

    private static bool Bool(JsonElement el, string name)
        => el.GetProperty(name).GetBoolean();

    // ---------- ws_mkdir ----------

    [Fact]
    public async Task Mkdir_CreatesNestedDirectories()
    {
        var r = await Call("ws_mkdir", """{"path":"a/b/c"}""");

        Assert.True(r.Ok);
        Assert.True(Directory.Exists(Path.Combine(_home, "a", "b", "c")));

        var d = Data(r);
        Assert.True(Bool(d, "created"));
        Assert.Equal(3, d.GetProperty("levels").GetInt32());
    }

    /// <summary>
    /// 已存在时必须如实说「没建」，而不是假装刚刚创建 ——
    /// 模型会据此判断「是不是别人先建了」，从而避免重复劳动。
    /// </summary>
    [Fact]
    public async Task Mkdir_ExistingDirectory_ReportsNotCreated()
    {
        Directory.CreateDirectory(Path.Combine(_home, "exists"));

        var r = await Call("ws_mkdir", """{"path":"exists"}""");

        Assert.True(r.Ok);
        Assert.False(Bool(Data(r), "created"));
        Assert.Equal(0, Data(r).GetProperty("levels").GetInt32());
    }

    /// <summary>同名文件占位时报错，不能把文件当成目录「建过去」。</summary>
    [Fact]
    public async Task Mkdir_PathOccupiedByFile_Fails()
    {
        File.WriteAllText(Path.Combine(_home, "blocker"), "x");

        var r = await Call("ws_mkdir", """{"path":"blocker"}""");

        Assert.False(r.Ok);
        Assert.Contains("文件", r.Error);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("C:\\Windows\\Temp\\fatfish_should_not_exist")]
    public async Task Mkdir_EscapesAreRejected(string path)
    {
        var json = JsonSerializer.Serialize(new { path });

        var r = await Call("ws_mkdir", json);

        Assert.False(r.Ok);
        Assert.Contains("越界", r.Error);
    }

    // ---------- ws_where ----------

    [Fact]
    public async Task Where_ReportsRootAndDefault()
    {
        var d = Data(await Call("ws_where", "{}"));

        Assert.Equal(_home, Str(d, "root"));
        Assert.Equal(_home, Str(d, "default_root"));
        Assert.True(Bool(d, "inside_default"));

        // 没有挂起请求时这个键**整个不出现** —— ToolJson 全局忽略 null
        // （与其它工具一致；模型看到「键不在」和看到 "null" 是一个意思）。
        Assert.False(d.TryGetProperty("pending_cd", out _));
    }

    [Fact]
    public async Task Where_ShowsPendingRequest()
    {
        await Call("ws_cd", JsonSerializer.Serialize(new { path = _outside }));

        var d = Data(await Call("ws_where", "{}"));

        Assert.Equal(_outside, Str(d, "pending_cd"));

        // 根没变 —— 挂起不等于切换
        Assert.Equal(_home, Str(d, "root"));
    }

    [Fact]
    public async Task Where_CountsTickets()
    {
        File.WriteAllText(Path.Combine(_home, "x.txt"), "hi");
        await Call("ws_read", """{"path":"x.txt"}""");

        var d = Data(await Call("ws_where", "{}"));

        Assert.Equal(1, d.GetProperty("open_tickets").GetInt32());
    }

    // ---------- ws_forget ----------

    [Fact]
    public async Task Forget_SinglePath_RemovesOnlyThatTicket()
    {
        File.WriteAllText(Path.Combine(_home, "a.txt"), "a");
        File.WriteAllText(Path.Combine(_home, "b.txt"), "b");

        await Call("ws_read", """{"path":"a.txt"}""");
        await Call("ws_read", """{"path":"b.txt"}""");
        Assert.Equal(2, _ws.Tickets.Count);

        var r = await Call("ws_forget", """{"path":"a.txt"}""");

        Assert.True(r.Ok);
        Assert.Equal(1, _ws.Tickets.Count);

        // 被清掉的那个再改就必须重读。
        // 这里用 ws_append 而不是 ws_write —— ws_write 是「全量覆盖，豁免凭证校验」
        // （与 Python 版一致），用它验不出凭证有没有被清掉。
        var write = await Call("ws_append", """{"path":"a.txt","content":"changed"}""");
        Assert.False(write.Ok);
        Assert.Contains("读凭证", write.Error);
    }

    [Fact]
    public async Task Forget_All_ClearsEverything()
    {
        foreach (var name in new[] { "a.txt", "b.txt" })
        {
            File.WriteAllText(Path.Combine(_home, name), name);
            await Call("ws_read", JsonSerializer.Serialize(new { path = name }));
        }

        var r = await Call("ws_forget", "{}");

        Assert.True(r.Ok);
        Assert.Equal(0, _ws.Tickets.Count);
        Assert.Equal(2, Data(r).GetProperty("cleared").GetInt32());
    }

    [Fact]
    public async Task Forget_MissingTicket_IsNotAnError()
    {
        var r = await Call("ws_forget", """{"path":"never-read.txt"}""");

        Assert.True(r.Ok);
        Assert.Equal(0, Data(r).GetProperty("cleared").GetInt32());
    }

    // ---------- ws_cd ----------

    [Fact]
    public async Task Cd_InsideDefault_SwitchesImmediately()
    {
        var sub = Path.Combine(_home, "sub");
        Directory.CreateDirectory(sub);

        var r = await Call("ws_cd", """{"path":"sub"}""");

        Assert.True(r.Ok);
        Assert.Equal(sub, Str(Data(r), "root"));
        Assert.Equal(_home, Str(Data(r), "previous_root"));
    }

    /// <summary>
    /// ★ 这条是整个批次里最重要的一条：
    ///   ws_cd 之后，**别的工具**必须按新根解析路径。
    ///   若 ToolContext.Guard 只是普通属性（不跟随会话），
    ///   ws_write 会写到旧根 —— 而返回值里路径看起来完全正常。
    /// </summary>
    [Fact]
    public async Task AfterCd_OtherToolsResolveAgainstNewRoot()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sub"));

        await Call("ws_cd", """{"path":"sub"}""");

        // 新根下写文件
        var write = await Call("ws_write", """{"path":"note.txt","content":"hi"}""");
        Assert.True(write.Ok);

        Assert.True(File.Exists(Path.Combine(_home, "sub", "note.txt")));
        Assert.False(File.Exists(Path.Combine(_home, "note.txt")));

        // 而 .. 现在指向 home（新根的上一层），仍然在沙箱内 —— 但再往上就出去了
        var escape = await Call("ws_write", """{"path":"../../escape.txt","content":"x"}""");
        Assert.False(escape.Ok);
        Assert.Contains("越界", escape.Error);
    }

    [Fact]
    public async Task Cd_OutsideDefault_ReturnsApprovalRequest()
    {
        var r = await Call("ws_cd", JsonSerializer.Serialize(new { path = _outside }));

        Assert.False(r.Ok);
        Assert.Contains("需用户批准", r.Error);
        Assert.Contains("ws_cd_approve", r.Error);

        // 根没变
        Assert.Equal(_home, _ws.Root);
        Assert.Equal(_outside, _ws.PendingRoot);
    }

    [Fact]
    public async Task CdApprove_ExecutesThePendingSwitch()
    {
        await Call("ws_cd", JsonSerializer.Serialize(new { path = _outside }));

        var r = await Call("ws_cd_approve", "{}");

        Assert.True(r.Ok);
        Assert.Equal(_outside, Str(Data(r), "root"));
        Assert.Equal(_outside, _ws.Root);
    }

    [Fact]
    public async Task CdApprove_WithoutPending_Fails()
    {
        var r = await Call("ws_cd_approve", "{}");

        Assert.False(r.Ok);
        Assert.Contains("没有待批准", r.Error);
    }

    /// <summary>
    /// 换根之后，之前签发的读凭证必须失效。
    ///
    /// 场景设得尽量刁钻：两个目录下**同名同内容**的文件。
    /// 若凭证只按内容指纹判定（而库里没换），模型就能「读过 A 根的 x.txt」
    /// 然后直接改 B 根下的 x.txt —— 没有任何提示。
    /// </summary>
    [Fact]
    public async Task AfterCd_TicketsFromOldRootAreGone()
    {
        File.WriteAllText(Path.Combine(_home, "twin.txt"), "identical");
        File.WriteAllText(Path.Combine(_outside, "twin.txt"), "identical");

        await Call("ws_read", """{"path":"twin.txt"}""");
        Assert.Equal(1, _ws.Tickets.Count);

        await Call("ws_cd", JsonSerializer.Serialize(new { path = _outside }));
        Assert.True((await Call("ws_cd_approve", "{}")).Ok);

        // 新根下同路径、同内容 —— 但仍然要求重读。
        // 用 ws_append（要凭证）；ws_write 豁免凭证，测不出这条。
        var write = await Call("ws_append", """{"path":"twin.txt","content":"appended"}""");

        Assert.False(write.Ok);
        Assert.Contains("读凭证", write.Error);

        Assert.Equal("identical", File.ReadAllText(Path.Combine(_outside, "twin.txt")));
    }

    /// <summary>没有会话（宿主未启用切换）时必须明确失败，不能悄悄当成相对路径。</summary>
    [Fact]
    public async Task Cd_WithoutSession_FailsClearly()
    {
        var standalone = new ToolContext
        {
            Guard = new PathGuard(_home),
            Tickets = new ReadTicketStore(new PathGuard(_home)),
            Approval = new ApprovalPolicy(new PathGuard(_home)) { Level = ApprovalLevel.ReadFree },
        };

        var registry = new ToolRegistry(new ITool[] { new WsCdTool(), new WsCdApproveTool() });

        using var doc = JsonDocument.Parse("""{"path":"sub"}""");

        var r = await registry.InvokeAsync("ws_cd", standalone, doc.RootElement.Clone());
        Assert.False(r.Ok);
        Assert.Contains("未启用工作台切换", r.Error);

        var approve = await registry.InvokeAsync("ws_cd_approve", standalone, doc.RootElement.Clone());
        Assert.False(approve.Ok);
        Assert.Contains("未启用工作台切换", approve.Error);
    }

    // ---------- 审批分级 ----------

    /// <summary>
    /// 三个低风险工具：有副作用，但**不弹窗**。
    /// 这是刻意的分级（对应 Python 版 L1），锁住它以免有人「顺手」把它们
    /// 塞进 APPROVAL_REQUIRED —— 那会让每次建目录都弹一次窗。
    /// </summary>
    [Theory]
    [InlineData("ws_mkdir")]
    [InlineData("ws_cd")]
    [InlineData("ws_cd_approve")]
    public void LowRiskTools_DoNotPromptUser(string name)
    {
        var policy = new ApprovalPolicy(_ws) { Level = ApprovalLevel.ReadFree };
        var tool = _registry.Find(name);

        Assert.NotNull(tool);
        Assert.True(tool!.HasSideEffects, "低风险不等于无副作用");
        Assert.False(policy.NeedsApproval(name).Need);
        Assert.Contains(name, policy.LowRiskTools);
    }

    [Theory]
    [InlineData("ws_where")]
    [InlineData("ws_forget")]
    public async Task ReadOnlyTools_AreNotVerifiedOrPrompted(string name)
    {
        var policy = new ApprovalPolicy(_ws) { Level = ApprovalLevel.ReadFree };
        var tool = _registry.Find(name);

        Assert.NotNull(tool);
        Assert.True(tool!.IsReadOnly);
        Assert.False(tool.HasSideEffects);
        Assert.False(policy.NeedsApproval(name).Need);

        // 能被调用（不抛异常就是通过）
        var json = name == "ws_forget" ? "{}" : "{}";
        await Call(name, json);
    }
}

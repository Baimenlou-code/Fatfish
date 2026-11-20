using System.Text.Json;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 车间（临时工作台）的行为测试：ws_site_new / list / enter / home / drop。
///
/// ★ 这组测试盯的是三件容易坏、且坏了不报错的事：
///   1. 新建之后**真的**切过去了（根变了、凭证库换了）；
///   2. 注销只摘登记、**绝不删文件**；
///   3. 登记簿能跨进程读回来（不然重启就白登记了）。
/// </summary>
public class WorkspaceSitesTests : IDisposable
{
    private readonly string _base;
    private readonly string _home;
    private readonly WorkspaceSession _ws;
    private readonly WorkspaceSites _sites;
    private readonly ToolContext _context;
    private readonly ToolRegistry _registry;

    public WorkspaceSitesTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "fatfish_sites_" + Guid.NewGuid().ToString("N")[..10]);
        _home = Path.Combine(_base, "home");
        Directory.CreateDirectory(_home);

        _ws = new WorkspaceSession(_home);
        _sites = new WorkspaceSites(_ws);

        _context = new ToolContext
        {
            Guard = _ws.Guard,
            Tickets = _ws.Tickets,
            Workspace = _ws,
            Sites = _sites,
            Approval = new ApprovalPolicy(_ws),
        };

        _registry = new ToolRegistry(new ITool[]
        {
            new WsSiteNewTool(),
            new WsSiteListTool(),
            new WsSiteEnterTool(),
            new WsSiteHomeTool(),
            new WsSiteDropTool(),
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

    private async Task<ToolResult> Call(string name, string json)
    {
        using var doc = JsonDocument.Parse(json);
        return await _registry.InvokeAsync(name, _context, doc.RootElement.Clone());
    }

    // ---------- 新建 ----------

    [Fact]
    public async Task New_CreatesDirectory_RegistersAndSwitches()
    {
        var guardBefore = _ws.Guard;
        var ticketsBefore = _ws.Tickets;

        var r = await Call("ws_site_new", """{"name":"audit","note":"看一份外来文档"}""");

        Assert.True(r.Ok, r.Error);

        var expected = Path.Combine(_home, WorkspaceSites.FolderName, "audit");

        Assert.True(Directory.Exists(expected), "车间目录应被创建");
        Assert.Equal(expected, _ws.Root, ignoreCase: true);

        var entry = Assert.Single(_sites.All);
        Assert.Equal("audit", entry.Name);
        Assert.Equal("看一份外来文档", entry.Note);

        // 换根必须换「新 Guard + 新凭证库」：
        // 旧凭证里存的是旧根下的绝对路径，留着它等于「读过 A 根的同名文件 = 读过了 B 根」。
        Assert.NotSame(guardBefore, _ws.Guard);
        Assert.NotSame(ticketsBefore, _ws.Tickets);
        Assert.Same(_ws.Guard, _context.Guard);   // 工具侧拿到的必须是新根
        Assert.True(Directory.Exists(_sites.SitesRoot));
    }

    [Fact]
    public async Task New_WithoutName_AutoNames()
    {
        var r = await Call("ws_site_new", "{}");

        Assert.True(r.Ok, r.Error);
        Assert.StartsWith("site_", Assert.Single(_sites.All).Name);
    }

    [Fact]
    public async Task New_SameNameTwice_IsRejected()
    {
        await Call("ws_site_new", """{"name":"dup"}""");

        var second = await Call("ws_site_new", """{"name":"dup"}""");

        Assert.False(second.Ok);
        Assert.Contains("同名", second.Error);
        Assert.Single(_sites.All);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    public async Task New_IllegalName_IsRejected(string name)
    {
        var r = await Call("ws_site_new", JsonSerializer.Serialize(new { name }));

        Assert.False(r.Ok);
        Assert.Empty(_sites.All);
    }

    // ---------- 进入 / 回家 ----------

    [Fact]
    public async Task Enter_SwitchesRoot_AndHomeRestoresIt()
    {
        await Call("ws_site_new", """{"name":"one"}""");
        var siteRoot = _ws.Root;

        await Call("ws_site_home", "{}");
        Assert.Equal(_home, _ws.Root, ignoreCase: true);

        var enter = await Call("ws_site_enter", """{"name":"one"}""");
        Assert.True(enter.Ok, enter.Error);
        Assert.Equal(siteRoot, _ws.Root, ignoreCase: true);
    }

    [Fact]
    public async Task Enter_UnknownName_FailsAndListsKnown()
    {
        await Call("ws_site_new", """{"name":"known"}""");
        await Call("ws_site_home", "{}");

        var r = await Call("ws_site_enter", """{"name":"nope"}""");

        Assert.False(r.Ok);
        Assert.Contains("known", r.Error);
        Assert.Equal(_home, _ws.Root, ignoreCase: true);
    }

    [Fact]
    public async Task Enter_DeletedDirectory_FailsLoudly()
    {
        await Call("ws_site_new", """{"name":"gone"}""");
        var path = _ws.Root;

        await Call("ws_site_home", "{}");
        Directory.Delete(path, recursive: true);

        var r = await Call("ws_site_enter", """{"name":"gone"}""");

        Assert.False(r.Ok);
        Assert.Contains("不存在", r.Error);
        Assert.Equal(_home, _ws.Root, ignoreCase: true);
    }

    // ---------- 注销 ----------

    [Fact]
    public async Task Drop_RemovesRegistryEntry_ButKeepsFiles()
    {
        await Call("ws_site_new", """{"name":"keep"}""");
        var path = _ws.Root;

        var file = Path.Combine(path, "note.txt");
        await File.WriteAllTextAsync(file, "别删我");

        await Call("ws_site_home", "{}");

        var r = await Call("ws_site_drop", """{"name":"keep"}""");

        Assert.True(r.Ok, r.Error);
        Assert.Empty(_sites.All);
        Assert.True(File.Exists(file), "注销是摘登记，不是删文件");
    }

    [Fact]
    public async Task Drop_CurrentSite_IsRefused()
    {
        await Call("ws_site_new", """{"name":"here"}""");

        var r = await Call("ws_site_drop", """{"name":"here"}""");

        Assert.False(r.Ok);
        Assert.Contains("ws_site_home", r.Error);
        Assert.Single(_sites.All);
    }

    // ---------- 落盘 ----------

    [Fact]
    public void Registry_SurvivesReload()
    {
        _sites.Create("persist", "备注", out var err);
        Assert.True(string.IsNullOrEmpty(err), err);

        var reopened = new WorkspaceSites(_ws);

        var entry = Assert.Single(reopened.All);
        Assert.Equal("persist", entry.Name);
        Assert.Equal("备注", entry.Note);
        Assert.True(File.Exists(reopened.RegistryPath));
    }

    [Fact]
    public async Task List_MarksCurrentSite()
    {
        await Call("ws_site_new", """{"name":"alpha"}""");

        var r = await Call("ws_site_list", "{}");

        Assert.True(r.Ok, r.Error);

        var json = r.Data!.Value.GetRawText();
        Assert.Contains("alpha", json);
        Assert.Contains("\"current\":true", json.Replace(" ", string.Empty));
        Assert.Contains("\"at_home\":false", json.Replace(" ", string.Empty));
    }

    [Fact]
    public async Task Tools_WithoutSitesInContext_FailWithClearMessage()
    {
        var standalone = new ToolContext
        {
            Guard = _ws.Guard,
            Tickets = _ws.Tickets,
            Approval = new ApprovalPolicy(_ws),
        };

        using var doc = JsonDocument.Parse("""{"name":"x"}""");

        var r = await _registry.InvokeAsync("ws_site_new", standalone, doc.RootElement.Clone());

        Assert.False(r.Ok);
        Assert.Contains("未启用车间登记", r.Error);
    }
}

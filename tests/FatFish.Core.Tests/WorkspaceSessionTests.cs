using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// WorkspaceSession 的行为测试。
///
/// 重点不在「能切过去」，而在三条**坏了也不会报错**的性质：
///   1. 移出默认作业区的切换必须挂起，不能直接生效；
///   2. 换根必须**换凭证库**，而不是只清空 —— 否则「读过 A 根的 x.txt」
///      就能直接改 B 根下的 x.txt（两个文件内容相同时指纹完全一样）；
///   3. 同根切换不能无故把凭证清掉（那会让模型莫名其妙被要求重读）。
/// </summary>
public class WorkspaceSessionTests : IDisposable
{
    private readonly string _base;
    private readonly string _home;      // 默认作业区
    private readonly string _outside;   // 默认作业区之外的另一个目录
    private readonly WorkspaceSession _session;

    public WorkspaceSessionTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "fatfish_ws_" + Guid.NewGuid().ToString("N")[..10]);
        _home = Path.Combine(_base, "home");
        _outside = Path.Combine(_base, "outside");

        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_outside);

        _session = new WorkspaceSession(_home);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
            // 目录被占用不影响结论
        }

        GC.SuppressFinalize(this);
    }

    // ---------- 基本状态 ----------

    [Fact]
    public void NewSession_RootIsGivenRoot_AndInsideDefault()
    {
        Assert.Equal(_home, _session.Root);
        Assert.Equal(_home, _session.DefaultRoot);
        Assert.True(_session.IsInsideDefault(_session.Root));
        Assert.Null(_session.PendingRoot);
    }

    /// <summary>默认作业区内的子目录：直接切，不打扰用户。</summary>
    [Fact]
    public void ChangeTo_InsideDefault_SwitchesImmediately()
    {
        var sub = Path.Combine(_home, "sub");
        Directory.CreateDirectory(sub);

        var r = _session.ChangeTo(sub);

        Assert.True(r.Ok);
        Assert.False(r.NeedsApproval);
        Assert.Equal(_home, r.OldRoot);
        Assert.Equal(sub, r.NewRoot);
        Assert.Equal(sub, _session.Root);
        Assert.Null(_session.PendingRoot);
    }

    /// <summary>默认作业区之外：挂起，**不生效**。</summary>
    [Fact]
    public void ChangeTo_OutsideDefault_PendsAndDoesNotSwitch()
    {
        var r = _session.ChangeTo(_outside);

        Assert.False(r.Ok);
        Assert.True(r.NeedsApproval);
        Assert.Equal(_outside, _session.PendingRoot);

        // 关键：根没变。挂起状态不能影响任何东西。
        Assert.Equal(_home, _session.Root);
    }

    [Fact]
    public void ApprovePending_ExecutesPendingSwitch()
    {
        _session.ChangeTo(_outside);

        var r = _session.ApprovePending();

        Assert.True(r.Ok);
        Assert.Equal(_outside, _session.Root);
        Assert.Null(_session.PendingRoot);
    }

    [Fact]
    public void ApprovePending_WithoutRequest_Fails()
    {
        var r = _session.ApprovePending();

        Assert.False(r.Ok);
        Assert.Contains("没有待批准", r.Message);
    }

    [Fact]
    public void CancelPending_ClearsPendingRequest()
    {
        _session.ChangeTo(_outside);
        Assert.Equal(_outside, _session.PendingRoot);

        Assert.True(_session.CancelPending());
        Assert.Null(_session.PendingRoot);
        Assert.Equal(_home, _session.Root);

        // 撤销之后再批准应当失败（请求已经不存在了）
        Assert.False(_session.ApprovePending().Ok);
    }

    [Fact]
    public void Reset_ReturnsToDefaultRoot()
    {
        var sub = Path.Combine(_home, "sub");
        Directory.CreateDirectory(sub);

        _session.ChangeTo(sub);
        Assert.Equal(sub, _session.Root);

        var r = _session.Reset();

        Assert.True(r.Ok);
        Assert.Equal(_home, _session.Root);
    }

    // ---------- 路径写法 ----------

    [Fact]
    public void ChangeTo_RelativePath_ResolvesAgainstCurrentRoot()
    {
        var sub = Path.Combine(_home, "sub");
        Directory.CreateDirectory(sub);

        var r = _session.ChangeTo("sub");

        Assert.True(r.Ok);
        Assert.Equal(sub, _session.Root);
    }

    [Fact]
    public void ChangeTo_StripsQuotes()
    {
        var sub = Path.Combine(_home, "quoted");
        Directory.CreateDirectory(sub);

        // 模型偶尔会把路径连引号一起带进来
        var r = _session.ChangeTo("\"" + sub + "\"");

        Assert.True(r.Ok);
        Assert.Equal(sub, _session.Root);
    }

    [Fact]
    public void ChangeTo_ExpandsEnvironmentVariables()
    {
        var sub = Path.Combine(_home, "envdir");
        Directory.CreateDirectory(sub);

        Environment.SetEnvironmentVariable("FATFISH_TEST_CD", sub);

        try
        {
            var r = _session.ChangeTo("%FATFISH_TEST_CD%");

            Assert.True(r.Ok);
            Assert.Equal(sub, _session.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FATFISH_TEST_CD", null);
        }
    }

    [Fact]
    public void ChangeTo_EmptyOrMissing_Fails()
    {
        Assert.False(_session.ChangeTo("").Ok);
        Assert.False(_session.ChangeTo(null).Ok);
        Assert.False(_session.ChangeTo(Path.Combine(_base, "nope")).Ok);
    }

    [Fact]
    public void ChangeTo_File_IsRejected()
    {
        var file = Path.Combine(_home, "a.txt");
        File.WriteAllText(file, "x");

        var r = _session.ChangeTo(file);

        Assert.False(r.Ok);
        Assert.Contains("不是目录", r.Message);
    }

    /// <summary>
    /// 默认作业区内的路径判定必须按**分隔符边界**，不能按字符串前缀。
    /// 否则 <c>C:\root2</c> 会被 <c>C:\root</c> 误判为「在里面」。
    /// </summary>
    [Fact]
    public void IsInsideDefault_RespectsSeparatorBoundary()
    {
        var sibling = _home + "2";
        Directory.CreateDirectory(sibling);

        Assert.False(_session.IsInsideDefault(sibling));
        Assert.True(_session.IsInsideDefault(_home));
        Assert.True(_session.IsInsideDefault(Path.Combine(_home, "a", "b")));
    }

    // ---------- 读凭证（最容易悄悄出错的地方）----------

    [Fact]
    public void ChangeTo_SameRoot_KeepsTickets()
    {
        var file = Path.Combine(_home, "note.txt");
        File.WriteAllText(file, "hello");

        _session.Tickets.Issue(file);
        Assert.Equal(1, _session.Tickets.Count);

        var r = _session.ChangeTo(_home);

        Assert.True(r.Ok);

        // 同根切换不该重新装 Guard —— 那会让刚签的凭证无故失效，
        // 表现为「模型刚读完就被告知没有凭证」，非常难理解。
        Assert.Equal(1, _session.Tickets.Count);
        Assert.True(_session.Tickets.IsValid(file));
    }

    /// <summary>
    /// ★ 换根必须换**库**，不能只清空。
    ///
    /// 凭证里存的是绝对路径，而 IsValid 比对的是**内容指纹**。
    /// 两个不同目录下的同名同内容文件指纹完全一致 ——
    /// 若只是清空，一旦有人把旧凭证塞回来（或复用了旧 store 实例），
    /// 「读过 A 根下的 x.txt」就能直接改 B 根下的 x.txt。
    /// </summary>
    [Fact]
    public void SwitchingRoot_DiscardsOldTicketsEntirely()
    {
        var inHome = Path.Combine(_home, "twin.txt");
        var inOutside = Path.Combine(_outside, "twin.txt");

        File.WriteAllText(inHome, "same-content");
        File.WriteAllText(inOutside, "same-content");

        var oldStore = _session.Tickets;
        oldStore.Issue(inHome);

        _session.ChangeTo(_outside);
        Assert.True(_session.ApprovePending().Ok);

        // 新库是空的
        Assert.Equal(0, _session.Tickets.Count);

        // 而且不是同一个对象 —— 旧 store 已与当前工作台脱钩
        Assert.NotSame(oldStore, _session.Tickets);

        // 旧路径在新守卫下甚至连「在工作台内」都不成立
        Assert.False(_session.Tickets.IsValid(inHome));
    }

    // ---------- 事件 ----------

    [Fact]
    public void RootChanged_FiresWithOldAndNew()
    {
        var sub = Path.Combine(_home, "sub");
        Directory.CreateDirectory(sub);

        string? oldSeen = null;
        string? newSeen = null;
        var fired = 0;

        _session.RootChanged += (o, n) =>
        {
            oldSeen = o;
            newSeen = n;
            fired++;
        };

        _session.ChangeTo(sub);

        Assert.Equal(1, fired);
        Assert.Equal(_home, oldSeen);
        Assert.Equal(sub, newSeen);
    }

    /// <summary>挂起不算切换，不该发事件（否则界面会以为已经切过去了）。</summary>
    [Fact]
    public void RootChanged_DoesNotFire_OnPendingRequest()
    {
        var fired = 0;
        _session.RootChanged += (_, _) => fired++;

        _session.ChangeTo(_outside);

        Assert.Equal(0, fired);
    }

    // ---------- 描述 ----------

    [Fact]
    public void Describe_ShowsPendingTarget()
    {
        _session.ChangeTo(_outside);

        var text = _session.Describe();

        Assert.Contains(_home, text);
        Assert.Contains(_outside, text);
        Assert.Contains("待批准", text);
    }
}

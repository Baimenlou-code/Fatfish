using System.Text.Json;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Workspace;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 端到端：分页读之后，ws_replace / ws_write 的闸门到底拦不拦、放不放（2026-11-05，P7）。
/// </summary>
/// <remarks>
/// ★ 前面几批测试分别验证了「行号换算」「区间判定」「未读块证明」，
///   但**三者的接缝**（工具里那几行调用）一直只有编译保证。
///   接缝错了不会报错：闸门要么永远不放行（工具全废），要么永远放行（白做）。
///   这批用例专门钉这个接缝。
/// ★ 这里走的是真实工具 + 真实 ws_read 签发凭证，不是桩 ——
///   桩会掩盖「ws_read 记的区间和工具查的区间对不上」这类错误。
/// </remarks>
public sealed class WsCoverageGateEndToEndTests : IDisposable
{
    private readonly string _root;
    private readonly ToolContext _ctx;

    public WsCoverageGateEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fatfish_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        var guard = new PathGuard(_root);

        _ctx = new ToolContext
        {
            Guard = guard,
            Tickets = new ReadTicketStore(guard),
            Approval = new ApprovalPolicy(guard),
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Write(string rel, string content)
        => File.WriteAllText(Path.Combine(_root, rel), content);

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private async Task<ToolResult> ReadPage(string rel, int offset, int limit)
        => await new WsReadTool().ExecuteAsync(_ctx, Args(new { path = rel, offset, limit }), default);

    private async Task<ToolResult> Replace(string rel, string oldText, string newText)
        => await new WsReplaceTool().ExecuteAsync(
            _ctx, Args(new { path = rel, old = oldText, @new = newText }), default);

    private async Task<ToolResult> WriteFile(string rel, string content, bool force = false)
        => await new WsWriteTool().ExecuteAsync(
            _ctx, Args(new { path = rel, content, confirm_partial_overwrite = force }), default);

    private static JsonElement Data(ToolResult result)
    {
        Assert.True(result.Ok, "工具应当成功，实际失败：" + result.Error);
        Assert.NotNull(result.Data);
        return result.Data!.Value;
    }

    // ---------------- ws_read 的 truncated 语义（2026-11-06） ----------------
    //
    // ★ 原来 truncated 直接等于 !complete，也就是「文件没读完」。
    //   于是「分页读一小段、这一页一个字节都没少」也报 truncated: true，
    //   模型看到会以为内容被砍掉一块，白白重读一遍或改用别的工具。
    //   现在 truncated 只表示「这一页被字符预算切掉了」。

    [Fact]
    public async Task Read_PagedButPageIsWhole_IsNotMarkedTruncated()
    {
        Write("a.txt", "l1\nl2\nl3\nl4\n");

        // 只要两行 → 文件没读完，但这一页是完整的
        var data = Data(await ReadPage("a.txt", offset: 1, limit: 2));

        Assert.False(data.GetProperty("complete").GetBoolean());   // 文件没读完
        Assert.True(data.GetProperty("has_more").GetBoolean());    // 后面还有
        Assert.False(data.GetProperty("truncated").GetBoolean());  // ← 但内容没被切
    }

    [Fact]
    public async Task Read_FullFile_IsNotMarkedTruncated()
    {
        Write("a.txt", "l1\nl2\n");

        var data = Data(await new WsReadTool().ExecuteAsync(_ctx, Args(new { path = "a.txt" }), default));

        Assert.True(data.GetProperty("complete").GetBoolean());
        Assert.False(data.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Read_OverPageBudget_IsMarkedTruncated()
    {
        // 100 行 × 1000 字符 ≈ 10 万字符，超过单页 8 万字符预算 → 必然被切
        var line = new string('z', 1000);
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 100; i++)
            sb.Append(line).Append('\n');

        Write("big.txt", sb.ToString());

        var data = Data(await ReadPage("big.txt", offset: 1, limit: 0));

        Assert.True(data.GetProperty("truncated").GetBoolean());
        Assert.True(data.GetProperty("has_more").GetBoolean());
        Assert.True(data.GetProperty("returned_chars").GetInt32() <= 80_000);

        // 被预算切的时候必须留得下「下一页从哪读」：lines_to 要小于总行数，
        // 这样模型才知道 offset=lines_to+1 就是下一页
        var to = data.GetProperty("lines_to").GetInt32();
        var total = data.GetProperty("total_lines").GetInt32();

        Assert.True(to >= 1 && to < total, $"lines_to={to} 应当落在 1..{total - 1} 之间");
    }

    // ---------------- ws_replace 的闸 ----------------

    [Fact]
    public async Task Replace_UnreadFile_IsRejectedWithTicketError()
    {
        Write("a.txt", "l1\nl2\n");

        var result = await Replace("a.txt", "l1", "X");

        Assert.False(result.Ok);
        Assert.Contains("读凭证", result.Error);
    }

    [Fact]
    public async Task Replace_OnReadLine_Passes()
    {
        Write("a.txt", "l1\nl2\nl3\nl4\nl5\n");

        await ReadPage("a.txt", offset: 1, limit: 2);   // 只读前两行

        var result = await Replace("a.txt", "l1", "X1");

        Assert.True(result.Ok);
        Assert.Equal("X1\nl2\nl3\nl4\nl5\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Replace_OnUnreadLine_IsRejected()
    {
        Write("a.txt", "l1\nl2\nl3\nl4\nl5\n");

        await ReadPage("a.txt", offset: 1, limit: 2);   // 第 3-5 行没读

        var result = await Replace("a.txt", "l5", "X");

        Assert.False(result.Ok);
        Assert.Contains("第 5 行", result.Error);

        // 文件必须**一个字都没变**
        Assert.Equal("l1\nl2\nl3\nl4\nl5\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Replace_AfterReadingAllPages_Passes()
    {
        Write("a.txt", "l1\nl2\nl3\nl4\nl5\n");

        await ReadPage("a.txt", offset: 1, limit: 2);
        await ReadPage("a.txt", offset: 3, limit: 2);
        await ReadPage("a.txt", offset: 5, limit: 2);

        var result = await Replace("a.txt", "l5", "X");

        Assert.True(result.Ok);
    }

    // ---------------- ws_write 的闸 ----------------

    [Fact]
    public async Task Write_NewFile_PassesWithoutTicket()
    {
        var result = await WriteFile("new.txt", "hello\n");

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(_root, "new.txt")));
    }

    [Fact]
    public async Task Write_PartialRead_DroppingUnreadContent_IsRejected()
    {
        Write("a.txt", "l1\nl2\nl3\nl4\nl5\n");

        await ReadPage("a.txt", offset: 1, limit: 2);

        // 新内容只剩前两行 —— 第 3-5 行被丢掉
        var result = await WriteFile("a.txt", "l1X\nl2X\n");

        Assert.False(result.Ok);
        Assert.Contains("未读区间", result.Error);
        Assert.Equal("l1\nl2\nl3\nl4\nl5\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Write_PartialRead_KeepingUnreadContent_Passes()
    {
        Write("a.txt", "l1\nl2\nl3\nl4\nl5\n");

        await ReadPage("a.txt", offset: 1, limit: 2);

        // 只改前两行，第 3-5 行逐字保留
        var result = await WriteFile("a.txt", "l1X\nl2X\nl3\nl4\nl5\n");

        Assert.True(result.Ok);
        Assert.Equal("l1X\nl2X\nl3\nl4\nl5\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Write_PartialRead_WithForceFlag_Passes()
    {
        Write("a.txt", "l1\nl2\nl3\nl4\nl5\n");

        await ReadPage("a.txt", offset: 1, limit: 2);

        // 带逃生门：工具层不再拦（人工审批由 ApprovalPolicy 负责，另有测试）
        var result = await WriteFile("a.txt", "l1X\n", force: true);

        Assert.True(result.Ok);
        Assert.Equal("l1X\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public async Task Write_CompleteRead_IsNotGated()
    {
        Write("a.txt", "l1\nl2\nl3\n");

        // 整读（不带 offset/limit，且文件小到一页装得下）→ 凭证是完整的
        await new WsReadTool().ExecuteAsync(_ctx, Args(new { path = "a.txt" }), default);

        var result = await WriteFile("a.txt", "完全不同\n");

        Assert.True(result.Ok);
    }
}

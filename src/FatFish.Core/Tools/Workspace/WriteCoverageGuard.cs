using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_write 全量覆盖的「未读部分没被动过」证明。
/// </summary>
/// <remarks>
/// ★ 要回答的问题：模型只读了文件的一部分，却要整份覆盖 ——
///   那些**没读过**的行，有没有被它顺手改掉或丢掉？
/// ★ 判据（故意选得简单）：
///   把每个未读连续块从原文里切出来，检查它**是否仍逐字出现在新内容里**。
///   仍在 → 那一块没有被丢掉；找不到 → 拒绝。
/// ★ 为什么不用逐行 diff：diff 要回答「哪几行改了」，
///   而我们只需要回答「未读的那几段还在不在」。
///   后者用一次子串查找就够，代价低一个量级，而且**失败方向是保守的**
///   （判不出来就拒绝，绝不会放过）。
/// ★ 已知的弱点（如实记录）：这是「还在某处」而非「还在原位置」。
///   若某个未读块本身是毫无特征的重复内容（例如连续几十行都只有 `}`），
///   它可能被别处的相同片段「撞上」而误判为保留。
///   这块闸是安全网，不是密码学证明 —— 概率低，且只在「部分读 + 整份覆盖」这条罕见路径上生效。
/// </remarks>
public static class WriteCoverageGuard
{
    /// <summary>证明结果。</summary>
    /// <param name="Proven">true = 未读部分都保留了。</param>
    /// <param name="LostBlock">证明失败时，第一块找不到的未读行区间。</param>
    public readonly record struct Result(bool Proven, LineRange LostBlock)
    {
        public static Result Ok => new(true, default);

        public static Result Fail(LineRange block) => new(false, block);
    }

    /// <summary>
    /// 证明「每一个未读块都仍逐字出现在新内容里」。
    /// </summary>
    /// <param name="oldContent">磁盘上的当前内容。</param>
    /// <param name="newContent">模型要写入的内容。</param>
    /// <param name="unreadBlocks">未读的连续行块（1-based 闭区间）。</param>
    public static Result Prove(
        string oldContent, string newContent, IReadOnlyList<LineRange> unreadBlocks)
    {
        if (unreadBlocks.Count == 0)
            return Result.Ok;

        foreach (var block in unreadBlocks)
        {
            // ★ 用与 ws_read 完全相同的切片逻辑取这一块：
            //   比较的必须是「模型本该看到的文本」，而不是另一套行尾约定。
            //   否则 CRLF 文件上会得出与实际阅读体验不一致的结论。
            var slice = WsReadTool.Slice(
                oldContent, block.From, block.To - block.From + 1, maxChars: int.MaxValue);

            if (!slice.Any)
                continue;

            if (slice.Text.Length == 0)
                continue;

            if (!newContent.Contains(slice.Text, StringComparison.Ordinal))
                return Result.Fail(block);
        }

        return Result.Ok;
    }
}

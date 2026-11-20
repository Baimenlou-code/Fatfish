using System.Text;
using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_replace —— 精确文本替换。
///
/// 对应 Python 版 workspace.py::ws_replace。
///
/// 这是「读凭证」机制的主战场：AI 最容易犯的错就是凭记忆改一个自己没看过的文件。
/// 本工具强制要求先 ws_read，且凭证绑定内容指纹 —— 文件被外部改动后凭证自动失效。
///
/// ★ 2026-11-05 新增「改动必须落在已读区间」判据（P3）。
///   起因：ws_read 支持分页之后，「读过」不再是布尔值 ——
///   只读了第 1-100 行也能拿到凭证，而 ws_replace 会照旧放行。
///   现在的判据是**最小必要**的：只看「这次实际会被改到的行」是否都读过，
///   不要求通读全文。
///     · 读全文再改 → 恒通过（老路径完全不受影响）
///     · 分页读 → 只要要改的那几行在读过的区间内就通过
///     · 要改的行在没读过的区间 → 拒绝，并告诉你从哪一行开始补读
///   ★ 为什么不像 ws_write 那样要求「未读部分没动」：
///     replace 是外科手术，它有 old 这个锚 —— 没读到的东西不可能被它顺手改掉。
///     真正需要担心的是「old 匹配到了你没看见的地方」，而按行号检查正好盖住这一点。
/// </summary>
public sealed class WsReplaceTool : ITool
{
    public string Name => "ws_replace";

    public string Description =>
        "在工作台内文件中精确替换文本（old 必须原样匹配，含缩进）。" +
        "调用前必须先 ws_read 该文件拿到读凭证。" +
        "count 省略或为 0 表示替换全部匹配。若 old 未命中会直接报错，不会静默写入。" +
        "若该文件是分页读的（没读全文），则被替换的位置必须落在你读过的行范围内，否则会被拒绝。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("path", Schema.String("相对工作台根目录的文件路径")),
            ("old", Schema.String("要被替换的原文，必须原样匹配（含缩进与换行）")),
            ("new", Schema.String("替换后的新内容")),
            ("count", Schema.Integer("替换次数；0 或省略表示替换全部", minimum: 0)),
        ],
        "path", "old", "new");

    public bool IsReadOnly => false;

    public bool HasSideEffects => true;

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("path", out var rel, out var err))
            return err;

        if (!args.TryGetRequired("old", out var oldText, out err))
            return err;

        var newText = args.GetString("new") ?? string.Empty;

        if (oldText == newText)
            return ToolResult.Failure("old 与 new 完全相同，无需替换");

        var full = context.Guard.Resolve(rel);

        if (!File.Exists(full))
            return ToolResult.Failure($"文件不存在：{rel}");

        // ★ 凭证校验：没读过 / 读后被改过，一律拒绝
        if (!context.Tickets.Check(full, out var ticketError))
            return ToolResult.Failure(ticketError);

        var original = await ReadAllTextAsync(full, ct).ConfigureAwait(false);

        // ★ 行尾对齐（2026-11-06 修的真 bug）：
        //   Windows 仓库里绝大多数 .cs / .xaml 是 CRLF，而模型习惯给 LF。
        //   原先直接做 Ordinal 匹配 → 恒为 0 命中，报错只说「请确认逐字符一致」，
        //   看不出问题出在换行符。实际表现就是「这个文件改不动，还说不清为什么」。
        AlignLineEndings(original, oldText, newText, out var alignedOld, out var alignedNew);
        oldText = alignedOld;
        newText = alignedNew;

        if (oldText == newText)
            return ToolResult.Failure("按该文件的行尾风格对齐后，old 与 new 完全相同，无需替换");

        var occurrences = CountOccurrences(original, oldText);
        if (occurrences == 0)
        {
            return ToolResult.Failure(
                $"在 {rel} 中未找到要替换的原文。"
                + (oldText.IndexOf('\n') >= 0 && UsesCrLf(original)
                    ? "（该文件用 CRLF 换行；已尝试行尾归一仍不匹配 —— 请检查缩进与空格是否逐字符一致。）"
                    : "请确认 old 与文件内容逐字符一致（包括缩进、空格、换行符）。"));
        }

        var count = args.GetInt("count", 0);
        var replaceAll = count <= 0;
        var effective = replaceAll ? occurrences : Math.Min(count, occurrences);

        // ★ P3 判据：实际会被改到的每一行，都必须在读过的区间内。
        //   读全文时 AreLinesCovered 恒为 true，所以老路径无感。
        var affected = AffectedLines(original, oldText, effective);

        if (!context.Tickets.AreLinesCovered(full, affected, out var firstUnread))
        {
            var coverage = context.Tickets.Coverage(full);

            // ★ 别把模型指回已经读过的区域（2026-11-06 修）。
            //   原式 Math.Max(1, firstUnread - 20) 在 firstUnread=20 时给出 offset=1，
            //   可 1-10 早就读过了 —— 重读一遍仍然到不了第 20 行。
            //   正确做法：从 firstUnread 所在**未读块**的起点读，那一段才是缺的。
            var hint = Math.Max(1, firstUnread);

            foreach (var block in context.Tickets.UnreadBlocks(full))
            {
                if (firstUnread >= block.From && firstUnread <= block.To)
                {
                    hint = Math.Max(1, block.From);
                    break;
                }
            }

            return ToolResult.Failure(
                $"{rel} 第 {firstUnread} 行会被改动，但那一行你没读过"
                + (coverage is null ? string.Empty : $"（已读 {coverage.ReadLines}/{coverage.TotalLines} 行）")
                + $"；请先用 ws_read 读那一带（offset={hint}）再来改，或者换一个只落在已读区间内的 old。");
        }

        string updated;
        if (replaceAll)
        {
            updated = original.Replace(oldText, newText, StringComparison.Ordinal);
        }
        else
        {
            var sb = new StringBuilder(original.Length);
            var cursor = 0;
            for (var i = 0; i < effective; i++)
            {
                var idx = original.IndexOf(oldText, cursor, StringComparison.Ordinal);
                sb.Append(original, cursor, idx - cursor);
                sb.Append(newText);
                cursor = idx + oldText.Length;
            }
            sb.Append(original, cursor, original.Length - cursor);
            updated = sb.ToString();
        }

        ct.ThrowIfCancellationRequested();

        // 备案覆盖任意深度：根一级进 _backup/，深层进 _backup/tree/ 的镜像目录。
        // 跳过（产物目录 / 超大小上限 / 本身在 _backup 内）会带原因返回，不静默。
        var capture = RootBackup.Capture(full, context.Guard);
        var backupPath = capture.Path;

        await File.WriteAllTextAsync(full, updated, new UTF8Encoding(false), ct).ConfigureAwait(false);

        // 内容已变，凭证作废，下次修改前需重新读
        context.Tickets.Forget(full);

        return ToolResult.Success(new
        {
            path = context.Guard.ToRelative(full),
            replacements = effective,
            occurrences,
            affected_lines = affected,
            bytes = new FileInfo(full).Length,
            backup = backupPath is null ? null : context.Guard.ToRelative(backupPath),
            backup_skipped = capture.Skipped,
        }, notice: replaceAll
            ? null
            : $"共找到 {occurrences} 处匹配，按 count={count} 只替换了前 {effective} 处");
    }

    /// <summary>
    /// 算出「这次实际会被改到的行号」（1-based，升序）。
    /// </summary>
    /// <remarks>
    /// ★ 做成 public static 纯函数是为了能被单测直接钉住：
    ///   「偏移量 → 行号」这一步错一个字符，判据就会放行一次本该拒绝的改动，
    ///   而且完全不报错 —— 只会安静地改到没读过的内容上。
    /// ★ 必须与下面替换逻辑用**同一个**扫描顺序（都从 cursor 往后找），
    ///   否则算出来的行号和真正被改的行会不一致。
    /// </remarks>
    public static IReadOnlyList<int> AffectedLines(string content, string oldText, int effective)
    {
        if (string.IsNullOrEmpty(oldText) || effective <= 0)
            return Array.Empty<int>();

        var lines = new List<int>(effective);
        var cursor = 0;

        for (var i = 0; i < effective; i++)
        {
            var idx = content.IndexOf(oldText, cursor, StringComparison.Ordinal);

            if (idx < 0)
                break;

            // ★ old 可能跨多行：起点行到终点行之间的每一行都会被改到。
            //   只记起点行会让闸门少检查一行 —— 终点行没读过，改动照样放行（2026-11-06 修）。
            //
            //   终点取「最后一个非换行字符」：old 若以 \r\n 结尾，那个换行是**下一行**的
            //   行首分隔符，把它算进来会让每次多行替换都误报一行（多要一次读取，很烦人）。
            var start = LineOfOffset(content, idx);
            var last = idx + oldText.Length - 1;

            while (last >= idx && (content[last] == '\n' || content[last] == '\r'))
                last--;

            int end;
            if (last >= idx)
            {
                end = LineOfOffset(content, last);
            }
            else
            {
                // old 全是换行（例如把两行合并成一行）：起始行和它后面的行都会被改到。
                // 这里只能保守地按换行个数外推 —— 宁可多要一次读取，也不能少检查一行。
                var newlines = 0;
                for (var k = idx; k < idx + oldText.Length; k++)
                {
                    if (content[k] == '\n')
                        newlines++;
                }

                end = start + Math.Max(1, newlines);
            }

            for (var line = start; line <= end; line++)
            {
                if (lines.Count == 0 || lines[lines.Count - 1] != line)
                    lines.Add(line);
            }

            cursor = idx + oldText.Length;
        }

        return lines;
    }

    /// <summary>字符偏移 → 1-based 行号。</summary>
    private static int LineOfOffset(string text, int offset)
    {
        var line = 1;

        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }

        return line;
    }

    private static async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024, useAsync: true);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把 old / new 的行尾统一到目标文件的风格。
    /// </summary>
    /// <remarks>
    /// ★ 为什么必须有（2026-11-06）：Windows 上的 .cs / .xaml 几乎都是 CRLF，
    ///   而模型给的 old 通常是 LF。精确匹配在这种情况下恒为 0 命中，
    ///   而报错只说「请确认逐字符一致」—— 用户和模型都看不出问题出在换行符。
    /// ★ 只归一行尾，不做大小写 / 空白 / 缩进模糊匹配：
    ///   那会把「精确替换」变成「猜你想改哪」。
    /// ★ 优先跟随文件风格；跟随失败但另一风格能命中时，退回能用那个 ——
    ///   混用换行的文件确实存在，硬按多数派处理会把它判成「改不动」。
    /// </remarks>
    public static void AlignLineEndings(
        string content,
        string oldText,
        string newText,
        out string alignedOld,
        out string alignedNew)
    {
        alignedOld = oldText;
        alignedNew = newText;

        // 单行 old 跟行尾无关
        if (oldText.IndexOf('\n') < 0)
            return;

        var oldLf = oldText.Replace("\r\n", "\n");
        var newLf = newText.Replace("\r\n", "\n");

        var oldCrlf = oldLf.Replace("\n", "\r\n");
        var newCrlf = newLf.Replace("\n", "\r\n");

        var preferCrlf = UsesCrLf(content);

        var firstOld = preferCrlf ? oldCrlf : oldLf;
        var firstNew = preferCrlf ? newCrlf : newLf;
        var secondOld = preferCrlf ? oldLf : oldCrlf;
        var secondNew = preferCrlf ? newLf : newCrlf;

        if (CountOccurrences(content, firstOld) > 0)
        {
            alignedOld = firstOld;
            alignedNew = firstNew;
            return;
        }

        if (CountOccurrences(content, secondOld) > 0)
        {
            alignedOld = secondOld;
            alignedNew = secondNew;
        }
    }

    /// <summary>文件里的换行是否以 CRLF 为主。</summary>
    public static bool UsesCrLf(string content)
    {
        var crlf = 0;
        var lf = 0;

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n')
                continue;

            lf++;

            if (i > 0 && content[i - 1] == '\r')
                crlf++;
        }

        return crlf * 2 > lf;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;

        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

}

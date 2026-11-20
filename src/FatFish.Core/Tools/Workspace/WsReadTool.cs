using System.Text;
using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>一次按行切片的完整结果。</summary>
/// <param name="Text">切片文本（与原文逐字一致，含 CRLF 里的 \r）。</param>
/// <param name="From">请求的起始行（1-based）。</param>
/// <param name="To">实际返回的最后一行（闭区间）；没读到任何行时 &lt; From。</param>
/// <param name="LineCount">文件总行数。</param>
/// <param name="HasMore">后面还有没返回的行。</param>
public readonly record struct LineSlice(string Text, int From, int To, int LineCount, bool HasMore)
{
    public bool Any => To >= From;

    public int ReturnedLines => Any ? To - From + 1 : 0;

    /// <summary>
    /// 这一页是否被**字符预算**切掉了（不是调用方指定的 limit，也不是「文件没读完」）。
    /// </summary>
    /// <remarks>
    /// ★ 2026-11-06 新增。原来 <c>truncated</c> 直接等于 <c>!complete</c>，
    ///   于是「分页读一小段、一个字节都没少」也报 <c>truncated: true</c> ——
    ///   模型看到会以为内容被砍掉一块，然后白白重读一遍或改用别的工具。
    ///   「文件没读完」由 <c>has_more</c> / <c>complete</c> 表达，不该混进 truncated。
    /// </remarks>
    public bool BudgetCut { get; init; }
}

/// <summary>
/// ws_read —— 读取工作台内文本文件，成功后签发「读凭证」。
///
/// 对应 Python 版 workspace.py::ws_read。
///
/// 安全要点（缺一不可）：
///   1. 路径必须经 PathGuard 解析（防 .. 与符号链接逃逸）；
///   2. 敏感文件（.env / 密钥）由审批层拦下，本工具只负责「如实报告」；
///   3. 读取成功才签发凭证；读取失败绝不签发（否则等于给了空头许可）。
///
/// ★ 2026-11-05 新增 offset / limit 分页，并改为**按实际返回的行范围签发凭证**。
///   两个动机：
///   1. 原来是「读全文然后截断到 20 万字符」，可文件稍大一点就只看到开头，
///      而错误提示居然写着「完整内容请用 ws_run_cmd 查看」—— 那是错的建议
///      （ws_run_cmd 只有 24KB，而且同样只留头）。
///   2. 更要命的是**凭证在截断之前就签发了**：只看到前 20 万字符，
///      却拿到了「可以整份覆盖」的许可。现在凭证记的是「读了哪几行」，
///      写入侧据此判断「未读部分有没有被动到」。
///
/// ★ 预算只按**字符**算，不按行数。
///   理由：花 token、撑爆上下文的是字符数；行数是间接指标 ——
///   60 字符的代码行和 600 字符的长行，同一个行数上限含义差十倍。
///   所以 limit 默认 0 = 不限，只留 offset 作为翻页刻度。
///   单页字符预算（PageChars）必须**小于** AgentLoop 的单条工具结果上限，
///   否则上面会从**中间**截断，模型拿到「头一半 + 尾一半」，中间凭空少一块 ——
///   分页要发生在这一层，而不是让上层在中间打洞。
/// </summary>
public sealed class WsReadTool : ITool
{
    /// <summary>文件体积闸：超过 <c>MaxReadChars × 4</c> **字节**直接拒读。</summary>
    /// <remarks>
    /// ★ 常量名沿用 Python 版的「字符」口径，但实际卡的是**字节**（FileInfo.Length）：
    ///   UTF-8 下一个字符最多 4 字节，所以「100 万字符」对应的字节上限是 400 万。
    ///   两个版本必须同口径，否则同一个文件会「Python 能读、C# 不能读」。
    /// </remarks>
    private const int MaxReadChars = 1_000_000;

    /// <summary>
    /// 单页返回给模型的字符预算。超出则**少返回几行整行**，绝不切断半行。
    /// </summary>
    /// <remarks>
    /// ★ 必须小于 AgentLoop.MaxToolResultChars（100_000），
    ///   否则整段结果会在上层被从中间截断，中间内容凭空消失。
    /// </remarks>
    private const int PageChars = 80_000;

    /// <summary>limit 的硬上限，仅在调用方显式指定时生效（0 = 不限）。</summary>
    private const int HardMaxLimit = 20_000;

    public string Name => "ws_read";

    public string Description =>
        "读取工作台内的文本文件内容，成功后获得该文件的『读凭证』——" +
        "之后才能用 ws_write / ws_append / ws_replace / ws_delete 修改它。" +
        "修改任何文件之前必须先读它。路径为相对工作台根目录的相对路径。" +
        "文件很大时用 offset 翻页（从第几行开始，1-based）；默认不限行数，" +
        $"只受单页约 {PageChars / 1000}K 字符的预算约束，结果里的 has_more / lines_to 告诉你下一页从哪开始。" +
        "凭证会记住你读过哪些行：读过全文才允许整份覆盖，只读过局部则只能做局部修改。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("path", Schema.String("相对工作台根目录的文件路径，例如 'src/main.py'")),
            ("offset", Schema.Integer(
                "从第几行开始读（1-based，默认 1）。上一次返回 has_more=true 时，用 lines_to + 1 作为下一页的 offset",
                1, int.MaxValue)),
            ("limit", Schema.Integer(
                $"最多读多少行。0（默认）= 不限，只受单页字符预算约束；上限 {HardMaxLimit}",
                0, HardMaxLimit)),
        ],
        "path");

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public async ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("path", out var rel, out var err))
            return err;

        var full = context.Guard.Resolve(rel);

        if (Directory.Exists(full))
            return ToolResult.Failure($"{rel} 是目录，请用 ws_list");

        if (!File.Exists(full))
        {
            // Python 版的行为：读不存在的文件直接报错，不签发凭证
            return ToolResult.Failure($"文件不存在：{rel}");
        }

        var info = new FileInfo(full);
        if (info.Length > MaxReadChars * 4L)
        {
            // ★ 分页只解决「一次返回多少」，不解决「一次读进内存」——
            //   本工具仍然要整份读入才能按行切。超大文件请用 ws_run_cmd 提取。
            return ToolResult.Failure(
                $"{rel} 过大（{info.Length:N0} 字节），超过单次读取上限。" +
                "分页（offset）不能绕过这一步；请改用 ws_run_cmd 只提取需要的片段。");
        }

        var from = Math.Max(1, args.GetInt("offset", 1));
        var limit = Math.Clamp(args.GetInt("limit", 0), 0, HardMaxLimit);

        ct.ThrowIfCancellationRequested();

        string content;
        await using (var fs = new FileStream(
            full, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024, useAsync: true))
        using (var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        var slice = Slice(content, from, limit, PageChars);

        // ★ 只有读取成功才签发凭证，而且只按【实际返回的行范围】记。
        //   这既是新增的覆盖率信息，也顺手堵掉了「截断前就发整份许可」的旧漏洞。
        var ticket = context.Tickets.Issue(full, slice.LineCount, slice.From, slice.To);

        var complete = slice.LineCount <= 0 || (slice.From == 1 && !slice.HasMore);

        var notice = BuildNotice(slice);

        return ToolResult.Success(new
        {
            path = context.Guard.ToRelative(full),
            bytes = info.Length,
            chars = content.Length,
            total_lines = slice.LineCount,

            lines_from = slice.Any ? slice.From : 0,
            lines_to = slice.Any ? slice.To : 0,
            returned_lines = slice.ReturnedLines,
            returned_chars = slice.Text.Length,
            has_more = slice.HasMore,
            complete,

            // ★ truncated 的含义收窄为「这一页被字符预算切掉了」（2026-11-06）。
            //   原来写的是 !complete —— 那表示「文件没读完」，分页读一小段也会报 true，
            //   模型看到会以为内容被砍掉一块。现在「没读完」只由 has_more / complete 表达。
            truncated = slice.BudgetCut,
            content = slice.Text,
            ticket = ticket.IssuedAt.ToString(
                "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        }, notice: notice);
    }

    private static string? BuildNotice(LineSlice slice)
    {
        if (slice.LineCount <= 0)
            return null;

        if (!slice.Any)
        {
            return slice.From > slice.LineCount
                ? $"offset={slice.From} 超出文件总行数（{slice.LineCount} 行），未读到任何内容"
                : $"未读到任何内容（从第 {slice.From} 行起）";
        }

        return slice.HasMore
            ? $"已返回第 {slice.From}-{slice.To} 行（共 {slice.LineCount} 行）；" +
              $"继续读用 offset={slice.To + 1}"
            : null;
    }

    /// <summary>
    /// 按行切片。**纯函数**，不碰磁盘 —— 这样边界情况能被单元测试直接钉住。
    /// </summary>
    /// <param name="limit">最多返回多少行；<c>0</c> 或负数表示不限。</param>
    /// <remarks>
    /// ★ 切成纯函数而不是留在 ExecuteAsync 里：分页的错法都很安静。
    ///   少返回一行、把 CRLF 里的 \r 吃掉、末尾多出一个空行 ——
    ///   这些都不会抛异常，只会让模型看到「看起来正常」的内容，
    ///   然后基于它去做 ws_replace，而匹配失败的报错完全指不到真正的原因。
    /// ★ 只按 '\n' 切、不剥 '\r'：切片必须能逐字还原原文，
    ///   否则 ws_replace 的精确匹配会在 CRLF 文件上莫名其妙地失败。
    /// ★ 字符预算只用来「少返回几行整行」，绝不切断半行 ——
    ///   半行既不能用于精确匹配，也会让行号对不上。
    /// </remarks>
    public static LineSlice Slice(string content, int from, int limit, int maxChars)
    {
        var lineCount = CountLines(content);

        from = Math.Max(1, from);

        // 0 = 不限行数：预算只由字符决定
        if (limit <= 0)
            limit = int.MaxValue;

        if (lineCount <= 0 || from > lineCount)
            return new LineSlice(string.Empty, from, from - 1, lineCount, false);

        var parts = content.Split('\n');
        var endsWithNewline = content[^1] == '\n';

        var sb = new StringBuilder();
        var to = from - 1;
        var budgetCut = false;

        for (var i = from - 1; i < lineCount && (i - (from - 1)) < limit; i++)
        {
            var piece = parts[i];
            var extra = sb.Length == 0 ? piece.Length : piece.Length + 1;

            // ★ 第一行无论多长都收 —— 否则一行超长（压成一行的 JSON）会导致永远读不到东西
            if (sb.Length > 0 && sb.Length + extra > maxChars)
            {
                // 因**字符预算**停下（不是调用方要的 limit）才叫「内容被切」。
                // truncated 只认这一种；「文件没读完」由 has_more / complete 表达。
                budgetCut = true;
                break;
            }

            if (sb.Length > 0)
                sb.Append('\n');

            sb.Append(piece);
            to = i + 1;
        }

        // 读到了最后一行、且原文以换行结尾 → 补回那个换行，保证逐字还原
        if (to == lineCount && endsWithNewline)
            sb.Append('\n');

        return new LineSlice(sb.ToString(), from, to, lineCount, to < lineCount)
        {
            BudgetCut = budgetCut,
        };
    }

    /// <summary>
    /// 行数统计：末尾换行不算多一行，与常见编辑器行号一致。
    /// </summary>
    public static int CountLines(string text)
    {
        if (text.Length == 0) return 0;

        var count = 1;
        foreach (var c in text)
        {
            if (c == '\n') count++;
        }

        return text[^1] == '\n' ? count - 1 : count;
    }
}

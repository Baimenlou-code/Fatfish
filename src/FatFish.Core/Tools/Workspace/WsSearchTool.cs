using System.Globalization;
using System.Text;
using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_search —— 在工作台内全文检索。
///
/// 对应 Python 版 workspace.py::ws_search。
///
/// ★ 规则一：**永不搜索敏感文件**。
///   起因是一个很容易被忽略的泄露路径：
///     ApprovalPolicy.NeedsApproval 只看参数里的 `path`，
///     而 ws_search 的 path 是目录（甚至空串 = 工作台根）。
///     于是 CheckSensitive("") 返回「干净」，审批闸门直接放行，
///     而检索结果会把 .env 里的 API Key 原样带回模型上下文。
///   这不是「审批不够严」，是**闸门看错了对象**。
///   所以本工具自己把敏感文件挡在外面，并把被挡的数量如实报给模型。
///   （用 ws_read 读 .env 仍然可行 —— 那条路会正常触发审批。）
///
/// ★ 规则二：**必须有时间预算**。
///   无预算的全文检索在大工作台上会把一次工具调用拖成几分钟。
///   实测本机根目录 39,397 个文件 / 12GB：
///   最初版本串行扫完全部文本文件要 72 秒，模型和用户都只能干等。
///   现在三道闸：跳过产物目录 → 并行读 → 15 秒墙钟预算。
///   预算耗尽时如实返回「扫了多少、命中多少、可能不全」，
///   让模型知道该缩小 path，而不是以为「没搜到 = 不存在」。
///
/// 匹配方式：纯子串匹配，不支持正则。
/// 理由：模型的输入不可控，正则回溯（ReDoS）能把一个查询拖成死循环；
/// 子串匹配的行为完全可预测，且覆盖了「找定义 / 找调用 / 找字符串」这些真实需求。
/// </summary>
public sealed class WsSearchTool : ITool
{
    private const int DefaultMaxResults = 60;
    private const int HardMaxResults = 500;

    /// <summary>单条命中行的展示长度上限（超出截断）。</summary>
    private const int MaxLineChars = 300;

    /// <summary>
    /// 一次检索的墙钟预算。超过就带着已有结果返回，并标注「可能不全」。
    ///
    /// 15 秒是权衡：足够扫完一个中等项目（几千文件），
    /// 又短到用户还愿意等；全库检索则会被截断并提示缩小范围。
    /// </summary>
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(15);

    /// <summary>最多候选多少个文件（防止极端目录结构把内存撑爆）。</summary>
    private const int MaxScanFiles = 20_000;

    public string Name => "ws_search";

    public string Description =>
        "在工作台内按子串全文检索（大小写不敏感，纯字符串匹配，不支持正则）。" +
        "返回 文件:行号: 内容 形式的命中列表，最多 max_results 条。" +
        "path 可限定到某个子目录或单个文件；glob 可按文件名过滤（如 *.cs）。" +
        "默认跳过 bin/obj/node_modules/venv 等产物目录、二进制文件、超大文件，" +
        "以及 .env / 私钥等敏感文件（敏感文件请改用 ws_read，它会触发审批）。" +
        "整个检索有约 15 秒上限：结果里 timed_out=true 表示没扫完，请缩小 path 再试。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("keyword", Schema.String("要查找的子串（大小写是否敏感由 case_sensitive 决定）")),
            ("path", Schema.String("检索范围：某个子目录或单个文件（相对工作台根目录）。缺省表示整个工作台")),
            ("glob", Schema.String("文件名通配过滤，例如 *.cs 或 *.md。缺省表示不过滤")),
            ("case_sensitive", Schema.Boolean("是否区分大小写，默认 false", false)),
            ("max_results", Schema.Integer(
                $"最多返回多少条命中，默认 {DefaultMaxResults}，上限 {HardMaxResults}",
                1, HardMaxResults)),
            ("include_ignored", Schema.Boolean(
                "是否连 bin/obj/node_modules/venv 等产物目录一起搜（默认 false）", false)),
        ],
        "keyword");

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public async ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("keyword", out var keyword, out var err))
            return err;

        var rel = args.GetString("path") ?? string.Empty;
        var glob = args.GetString("glob");
        var caseSensitive = args.GetBool("case_sensitive", false);
        var includeIgnored = args.GetBool("include_ignored", false);
        var maxResults = Math.Clamp(args.GetInt("max_results", DefaultMaxResults), 1, HardMaxResults);

        var full = context.Guard.Resolve(rel);

        // ---- 1. 范围解析 ----
        var isSingleFile = File.Exists(full) && !Directory.Exists(full);

        if (!isSingleFile && !Directory.Exists(full))
            return ToolResult.Failure($"路径不存在：{rel}");

        // 显式点名敏感文件 → 拒绝，并引导去走会触发审批的 ws_read
        var scopeRel = context.Guard.ToRelative(full);
        if (isSingleFile)
        {
            var verdict = context.Guard.CheckSensitive(scopeRel);
            if (verdict.IsSensitive)
            {
                return ToolResult.Failure(
                    $"拒绝检索敏感文件（{verdict.Reason}）。如需读取请用 ws_read，它会触发人工审批。");
            }
        }

        // ---- 2. 收集候选文件 ----
        // 显式写类型而不是 var：三元表达式两侧一个是集合表达式、一个是方法调用，
        // 用 var 推断不出目标类型（CS0173）。
        List<(string Full, string Rel)> files = isSingleFile
            ? new List<(string Full, string Rel)> { (full, scopeRel) }
            : EnumerateFiles(context.Guard, full, includeIgnored, ct);

        var limitedByCount = files.Count > MaxScanFiles;
        if (limitedByCount)
            files = files.GetRange(0, MaxScanFiles);

        // ---- 3. 并行扫描 ----
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // 每个槽位独立写入，互不干扰；最后按文件顺序聚合，保证结果顺序稳定。
        var slots = new FileScan[files.Count];

        var budgetExceeded = false;

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(SearchBudget);

        var options = new ParallelOptions
        {
            // 上限 8：再多只是抢 DPI，纯 I/O 密集场景下收益递减
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = budgetCts.Token,
        };

        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, files.Count),
                options,
                async (i, token) =>
                {
                    slots[i] = await ScanFileAsync(
                        context.Guard, files[i], glob, keyword, comparison, maxResults, token)
                        .ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                return ToolResult.Failure("检索已被用户取消。");

            budgetExceeded = true;
        }

        // ---- 4. 按顺序聚合 ----
        var matches = new List<string>();
        var scanned = 0;
        var skippedBinary = 0;
        var skippedLarge = 0;
        var skippedSensitive = 0;
        var truncated = false;

        foreach (var slot in slots)
        {
            scanned += slot.Scanned ? 1 : 0;
            skippedBinary += slot.Binary ? 1 : 0;
            skippedLarge += slot.Large ? 1 : 0;
            skippedSensitive += slot.Sensitive ? 1 : 0;

            if (slot.Hits is null)
                continue;

            foreach (var hit in slot.Hits)
            {
                if (matches.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                matches.Add(hit);
            }
        }

        if (matches.Count >= maxResults)
            truncated = true;

        var incomplete = budgetExceeded || limitedByCount;

        // ---- 5. 结果 ----
        var summary = BuildSummary(matches, keyword, scanned, truncated);

        var notices = new List<string>(3);

        if (budgetExceeded)
            notices.Add($"超过 {SearchBudget.TotalSeconds:0} 秒预算，只扫了 {scanned} 个文件；" +
                        "请用 path / glob 缩小范围");

        if (limitedByCount)
            notices.Add($"候选文件超过 {MaxScanFiles:N0} 个上限，只扫了前 {MaxScanFiles:N0} 个");

        if (truncated && !incomplete)
            notices.Add($"已达 {maxResults} 条上限，可能还有更多命中");

        if (skippedSensitive > 0)
            notices.Add($"已跳过 {skippedSensitive} 个敏感文件（.env / 私钥等）；如需读取请用 ws_read");

        return ToolResult.Success(new
        {
            keyword,
            path = scopeRel,
            scanned,
            hits = matches.Count,
            truncated = truncated || incomplete,
            timed_out = budgetExceeded,
            skipped_binary = skippedBinary,
            skipped_too_large = skippedLarge,
            skipped_sensitive = skippedSensitive,
            matches,
            summary,
        }, notice: notices.Count == 0 ? null : string.Join("；", notices));
    }

    // ------------------------------------------------------------------
    // 单文件扫描
    // ------------------------------------------------------------------

    /// <summary>一个文件的扫描结论。</summary>
    private readonly record struct FileScan(
        List<string>? Hits,
        bool Scanned,
        bool Binary,
        bool Large,
        bool Sensitive);

    private static async Task<FileScan> ScanFileAsync(
        PathGuard guard,
        (string Full, string Rel) file,
        string? glob,
        string keyword,
        StringComparison comparison,
        int maxResults,
        CancellationToken ct)
    {
        // 敏感文件一律跳过（见类注释里那条泄露路径）
        if (guard.CheckSensitive(file.Rel).IsSensitive)
            return new FileScan(null, false, false, false, true);

        if (!string.IsNullOrEmpty(glob) &&
            !PathGuard.GlobMatch(Path.GetFileName(file.Rel), glob))
        {
            return default;
        }

        byte[] bytes;
        try
        {
            var (data, tooLarge) = await ReadCappedAsync(
                file.Full, WorkspaceFilters.MaxSearchFileBytes, ct).ConfigureAwait(false);

            if (tooLarge)
                return new FileScan(null, false, false, true, false);

            bytes = data;
        }
        catch (FileNotFoundException)
        {
            return default;     // 枚举与读取之间被删掉了
        }
        catch (DirectoryNotFoundException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }

        var sample = bytes.AsSpan(0, Math.Min(bytes.Length, WorkspaceFilters.BinarySampleBytes));
        if (ProcessEncoding.LooksBinary(sample))
            return new FileScan(null, false, true, false, false);

        var text = ProcessEncoding.Decode(bytes);
        var hits = ScanLines(text, file.Rel, keyword, comparison, maxResults);

        return new FileScan(hits, true, false, false, false);
    }

    /// <summary>
    /// 逐行扫描。用 Span 游标而不是 <c>content.Split('\n')</c>：
    /// 4MB 的源文件 split 一次会分配几万个字符串对象，而绝大多数行根本不匹配。
    /// </summary>
    private static List<string> ScanLines(
        string text,
        string fileRel,
        string keyword,
        StringComparison comparison,
        int maxResults)
    {
        // Span 在方法内部创建：ReadOnlySpan 是 ref struct，
        // 不能跨 await 边界存活（CS4007），所以不能在外面建好再传进来。
        var keywordSpan = keyword.AsSpan();
        var span = text.AsSpan();

        var matches = new List<string>();
        var pos = 0;
        var lineNo = 0;

        while (pos <= span.Length)
        {
            if (matches.Count >= maxResults)
                break;

            var nl = span[pos..].IndexOf('\n');
            ReadOnlySpan<char> line = nl < 0 ? span[pos..] : span[pos..(pos + nl)];

            pos = nl < 0 ? span.Length + 1 : pos + nl + 1;
            lineNo++;

            if (line.Length > 0 && line[^1] == '\r')
                line = line[..^1];

            if (line.Length == 0)
                continue;

            // 先粗筛：比 keyword 短的行不可能包含它
            if (line.Length < keywordSpan.Length || !line.Contains(keywordSpan, comparison))
                continue;

            var shown = line.Length > MaxLineChars
                ? line[..MaxLineChars].ToString() + "…"
                : line.ToString();

            matches.Add($"{fileRel}:{lineNo}: {shown.Replace('\t', ' ').Trim()}");
        }

        return matches;
    }

    /// <summary>
    /// 生成人读摘要。
    ///
    /// 为什么要单独给一段文本而不是让模型自己数字数：
    /// 命中 0 条是最常见的分支，也是最容易被模型误判的分支
    /// （它常常把「没找到」说成「该功能不存在」）。摘要里明确写清
    /// 「扫了多少文件、命中几条」，能显著减少这类臆断。
    /// </summary>
    private static string BuildSummary(
        List<string> matches, string keyword, int scanned, bool truncated)
    {
        if (matches.Count == 0)
        {
            return scanned == 0
                ? $"没有可搜索的文本文件，未找到「{keyword}」。"
                : $"已扫描 {scanned} 个文本文件，未找到「{keyword}」。";
        }

        var sb = new StringBuilder();

        // 显式传 InvariantCulture：CA1305 会拦「插值 Append 用当前区域」的写法。
        sb.Append(CultureInfo.InvariantCulture,
            $"在 {scanned} 个文本文件中找到 {matches.Count} 处「{keyword}」");

        if (truncated)
            sb.Append("（已达上限，可能还有更多）");

        sb.Append('。');

        // 「出现在哪几个文件」通常比逐条列行号更有用
        var files = matches
            .Select(m => m[..Math.Max(0, m.IndexOf(':', StringComparison.Ordinal))])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count is > 0 and <= 6)
            sb.Append("涉及：").Append(string.Join("、", files)).Append('。');
        else if (files.Count > 6)
            sb.Append(CultureInfo.InvariantCulture, $"涉及 {files.Count} 个文件。");

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // 枚举
    // ------------------------------------------------------------------

    /// <summary>
    /// 深度优先枚举文件，顺带剪掉忽略目录。
    ///
    /// 用显式栈而不是递归：深目录树 + 符号链接成环时，递归会栈溢出；
    /// 显式栈能顺手加「路径深度上限」防止走进无底洞。
    /// </summary>
    private static List<(string Full, string Rel)> EnumerateFiles(
        PathGuard guard, string startDir, bool includeIgnored, CancellationToken ct)
    {
        const int MaxDepth = 24;

        var result = new List<(string Full, string Rel)>();
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((startDir, 0));

        while (stack.Count > 0 && result.Count < MaxScanFiles)
        {
            ct.ThrowIfCancellationRequested();

            var (dir, depth) = stack.Pop();

            if (depth > MaxDepth)
                continue;

            IEnumerable<FileSystemInfo> entries;

            try
            {
                entries = new DirectoryInfo(dir).GetFileSystemInfos();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.Hidden) != 0)
                    continue;

                if (entry is DirectoryInfo sub)
                {
                    if (!includeIgnored && WorkspaceFilters.IgnoredDirs.Contains(sub.Name))
                        continue;

                    stack.Push((sub.FullName, depth + 1));
                    continue;
                }

                if (entry is not FileInfo f)
                    continue;

                if (!guard.IsInside(f.FullName))
                    continue;

                result.Add((f.FullName, guard.ToRelative(f.FullName)));
            }
        }

        // 稳定顺序：让「同样输入 → 同样输出」，否则模型每次看到的结果不一样
        result.Sort((a, b) => string.CompareOrdinal(a.Rel, b.Rel));
        return result;
    }

    /// <summary>
    /// 读文件字节，超过上限时**不读**而是直接报告 tooLarge。
    /// 与「读到一半截断」不同：截断后的内容会让模型以为文件就这么多，
    /// 于是得出「这里没有那段代码」的错误结论。
    /// </summary>
    private static async Task<(byte[] Bytes, bool TooLarge)> ReadCappedAsync(
        string path, long maxBytes, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024, useAsync: true);

        long length = fs.Length;

        if (length > maxBytes)
            return (Array.Empty<byte>(), true);

        if (length == 0)
            return (Array.Empty<byte>(), false);

        var buffer = new byte[(int)length];
        var read = 0;

        while (read < buffer.Length)
        {
            var n = await fs.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n <= 0)
                break;

            read += n;
        }

        return read == buffer.Length ? (buffer, false) : (buffer[..read], false);
    }
}

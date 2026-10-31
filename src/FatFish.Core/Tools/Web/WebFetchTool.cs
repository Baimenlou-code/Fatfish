using System.Text.Json;
using FatFish.Core.Net;

namespace FatFish.Core.Tools.Web;

/// <summary>
/// web_fetch —— 抓取网页正文。
///
/// 与 web_search 的分工：搜索给「有哪些页面 + 几百字摘要」，
/// 抓取给「某一页到底写了什么」。真正需要细节时（要照着官方文档写代码、
/// 要看 changelog、要核对某个报错）只有抓正文才够。
///
/// ★ 一次最多 5 个 URL。这不是技术限制，是**上下文预算**的限制：
///   一页正文 8000 字 × 5 = 4 万字，已经吃掉半个别墅。
///   而模型真正会读的通常只有前两页 —— 多抓的那几页只是让账单更长。
///
/// ★ 按成功页面计费（官方 Fetch：每个取到正文的页面算一次）。
///   所以默认只让一个后端去抓；多个后端抓同一批 URL 是纯重复花钱。
/// </summary>
public sealed class WebFetchTool : ITool
{
    /// <summary>单次最多抓几个页面。</summary>
    private const int MaxUrls = 5;

    public string Name => "web_fetch";

    public string Description =>
        "抓取指定网页的正文（纯文本），用于阅读搜索结果里的具体页面、官方文档、" +
        "changelog、报错讨论等。一次最多 5 个 URL。" +
        "已经知道确切链接时用这个；只知道「要查什么」时用 web_search。" +
        "抓到的内容来自公开网页，可能与事实不符，引用时请注明来源。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("urls", Schema.String(
                "要抓取的 URL，多个用换行或逗号分隔（最多 5 个），必须是 http/https 完整链接")),
            ("max_chars", Schema.Integer(
                "单页正文最大字符数，默认 8000；只想要摘要时可设 2000", 500, 30000)),
        ],
        "urls");

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public async ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        var urls = ReadUrls(args);

        if (urls.Count == 0)
            return ToolResult.Failure("缺少必填参数 'urls'（需要至少一个 http/https 链接）");

        var invalid = urls.Where(u => !NetUrl.IsHttpUrl(u)).ToList();
        urls = urls.Where(u => NetUrl.IsHttpUrl(u)).ToList();

        if (urls.Count == 0)
            return ToolResult.Failure(
                "没有合法的 URL。只接受完整链接（形如 https://example.com/page）；" +
                "如果只有站点名或关键词，请先调用 web_search。" +
                (invalid.Count > 0 ? $" 无效输入：{string.Join("、", invalid.Take(3))}" : string.Empty));

        // ★ 超过上限时明确告知被丢掉的是什么，而不是静默截断：
        //   模型以为抓了 8 页、实际只抓 5 页，就会去猜剩下 3 页的内容。
        var dropped = urls.Count > MaxUrls ? urls.Skip(MaxUrls).ToList() : null;
        urls = urls.Take(MaxUrls).ToList();

        var maxChars = Math.Clamp(args.GetInt("max_chars", 8000), 500, 30_000);

        var owned = context.Net is null;
        var net = context.Net ?? NetService.FromEnvironment(WebSearchTool.TryOpenLedger(context));

        try
        {
            if (!net.AnyAvailable)
                return ToolResult.Failure("没有配置任何抓取后端（需要 TAVILY_API_KEY 或 FATFISH_API_KEY）。");

            var result = await net
                .FetchAsync(urls, maxChars, question: urls[0], ct: ct)
                .ConfigureAwait(false);

            context.Log?.Invoke(
                $"[联网] 抓取 {urls.Count} 个页面 → 成功 {result.Hits.Count(h => h.Error is null)} 个 / {result.ElapsedMs}ms");

            var notice = dropped is null
                ? null
                : $"超过 {MaxUrls} 个上限，以下链接未被抓取：{string.Join("、", dropped)}";

            return ToolResult.Success(new
            {
                count = result.Hits.Count(h => h.Error is null),
                elapsed_ms = result.ElapsedMs,
                text = result.Text,
                pages = result.Hits.Select(h => new
                {
                    url = h.Url,
                    title = h.Title,
                    ok = h.Error is null,
                    chars = h.Content.Length,
                    error = h.Error,
                }).ToList(),
                dropped = dropped,
            }, notice: notice);
        }
        catch (NetException ex)
        {
            return ToolResult.Failure(
                ex.Message + "（如果是站点拦截或链接失效，换个链接或改用 web_search，不要反复重试同一批 URL。）");
        }
        finally
        {
            if (owned)
                net.Dispose();
        }
    }

    /// <summary>
    /// 支持三种写法：JSON 数组、换行分隔、逗号分隔。
    /// </summary>
    /// <remarks>
    /// 为什么要三种：模型偶尔会把数组写成字符串（schema 说 string 它也可能给数组），
    /// 而 URL 里本身可能带逗号（查询参数）。所以 JSON 数组优先，
    /// 字符串则先按换行拆，再按空白拆，最后才按逗号拆 —— 这个顺序能正确处理
    /// <c>https://a.com/x?y=1,2</c> 这种只有一个链接的情形（没有换行、没有空格，
    /// 整串当作一个 URL 更安全）。
    /// </remarks>
    private static List<string> ReadUrls(JsonElement args)
    {
        var list = new List<string>();

        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("urls", out var el))
            return list;

        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s.Trim());
            }

            return Dedup(list);
        }

        if (el.ValueKind != JsonValueKind.String)
            return list;

        var raw = el.GetString() ?? string.Empty;

        foreach (var line in raw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var s = line.Trim();

            if (s.Length == 0)
                continue;

            // 一行里有多个 URL（用空格或逗号分隔），但整行本身不是合法 URL
            if (NetUrl.IsHttpUrl(s))
            {
                list.Add(s);
                continue;
            }

            list.AddRange(s.Split([',', '，', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0));
        }

        return Dedup(list);
    }

    private static List<string> Dedup(List<string> items)
        => items.Distinct(StringComparer.Ordinal)
            .Where(x => x.Length > 0)
            .ToList();
}

using System.Text.Json;
using FatFish.Core.Net;

namespace FatFish.Core.Tools.Web;

/// <summary>
/// web_search —— 联网搜索。
///
/// 三个设计决定值得写下来：
///
/// ① **不报批**。搜索是「读」，与 ws_read 同级；每次搜索都弹窗会让用户
///    条件反射地点「允许」，反而废掉了审批的意义（同样的道理写在
///    ApprovalPolicyTests 里：给只读工具加审批 = 训练用户不看内容就确认）。
///    但它确实会把用户的问题发给第三方，所以每次调用都进台账 —— 事后可查，
///    这比事前弹窗更实际。
///
/// ② **失败要说人话**。「HTTP 402」这种消息会让模型继续换参数重试，
///    而实际上得去充值。所以 provider 层把状态码翻成了行动建议。
///
/// ③ **结果里带一句「未经核实」**。联网内容全部来自外部网页，
///    里面完全可能有「忽略之前的指令」这种注入。明确标注来源与不确定性，
///    是成本最低、最有效的一道防线。
/// </summary>
public sealed class WebSearchTool : ITool
{
    public string Name => "web_search";

    public string Description =>
        "联网搜索公开网页，返回带来源链接的结果列表。用于回答需要最新信息、" +
        "具体事实、或你不确定的问题（新闻、版本号、价格、文档细节）。" +
        "query 支持高级语法：site:github.com、lang:zh、after:2025-06-01。" +
        "days 参数可限定「只要最近 N 天的结果」。结果来自公开网页，未必准确，" +
        "引用时说明来源；与用户提供的资料冲突时以用户资料为准。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("query", Schema.String("检索词。建议用具体的名词短语，不要写成整句提问；支持 site: / lang: / after: 语法")),
            ("count", Schema.Integer("返回几条，1~20，默认 5", 1, 20)),
            ("days", Schema.Integer("只要最近 N 天的结果；省略表示不限时间", 1, 3650)),
            ("backends", Schema.String(
                "指定搜索后端，逗号分隔（deepseek / tavily）。一般不用填，程序会按问题特征自动选")),
        ],
        "query");

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public async ValueTask<ToolResult> ExecuteAsync(
        ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (!args.TryGetRequired("query", out var query, out var err))
            return err;

        var count = Math.Clamp(args.GetInt("count", 5), 1, 20);

        int? days = null;
        if (args.TryGetProperty("days", out var daysEl)
            && daysEl.ValueKind == JsonValueKind.Number
            && daysEl.TryGetInt32(out var d) && d > 0)
            days = d;

        var backends = ParseList(args.GetString("backends"));

        var owned = context.Net is null;
        var net = context.Net ?? NetService.FromEnvironment(
            TryOpenLedger(context));

        try
        {
            if (!net.AnyAvailable)
            {
                return ToolResult.Failure(
                    "没有配置任何搜索后端。请在 .env 里设置 TAVILY_API_KEY，" +
                    "或确认 FATFISH_API_KEY / DEEPSEEK_API_KEY 可用。");
            }

            var result = await net
                .SearchAsync(query, count, days, backends, question: query, ct: ct)
                .ConfigureAwait(false);

            context.Log?.Invoke(
                $"[联网] 搜索「{query}」→ {result.Hits.Count} 条 / {result.Backends.Count} 个后端 / {result.ElapsedMs}ms");

            return ToolResult.Success(new
            {
                query,
                count = result.Hits.Count,
                backends = result.Backends,
                route = result.Route.Why,
                elapsed_ms = result.ElapsedMs,
                text = result.Text,

                // 结构化副本：模型想精确引用某个链接时不必从 text 里做字符串解析
                hits = result.Hits.Select(h => new
                {
                    title = h.Title,
                    url = h.Url,
                    snippet = h.Snippet,
                    published = string.IsNullOrEmpty(h.Published) ? null : h.Published,
                    site = string.IsNullOrEmpty(h.Site) ? null : h.Site,
                }).ToList(),
            });
        }
        catch (NetException ex)
        {
            // 联网失败不是「工具坏了」，而是「这次没有网/没额度」——
            // 明确告诉模型可以换个说法或直接说明无法联网，别让它无限重试。
            return ToolResult.Failure(
                ex.Message + "（如果这属于配置问题，请直接告知用户，不要反复重试。）");
        }
        finally
        {
            if (owned)
                net.Dispose();
        }
    }

    /// <summary>把 "tavily, deepseek" / "auto" 这类字符串拆成列表；空/auto 返回 null。</summary>
    private static List<string>? ParseList(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return null;

        var s = spec.Trim();

        if (s.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || s.Equals("all", StringComparison.OrdinalIgnoreCase))
            return null;

        var list = s.Split([',', '，', '、', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().ToLowerInvariant())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return list.Count == 0 ? null : list;
    }

    /// <summary>台账建在工作台根下；建不出来（只读目录等）就退化成「不记账」。</summary>
    internal static UsageLedger? TryOpenLedger(ToolContext context)
    {
        try
        {
            return UsageLedger.Open(context.Guard.Root);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

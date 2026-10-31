using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FatFish.Core.Net;

/// <summary>一条联网账单记录。</summary>
/// <param name="Ts">发生时间（UTC，写盘用 ISO 8601）。</param>
/// <param name="Kind">search / fetch。</param>
/// <param name="Backend">deepseek / tavily。</param>
/// <param name="Query">检索词或首个 URL（截断到 120 字符）。</param>
/// <param name="Ok">是否成功。</param>
/// <param name="N">返回条数（fetch 为成功页面数）。</param>
/// <param name="Units">计费单位（官方=次，Tavily=credit）。</param>
/// <param name="Ms">耗时。</param>
public sealed record UsageEntry(
    [property: JsonPropertyName("ts")] DateTimeOffset Ts,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("units")] double Units,
    [property: JsonPropertyName("ms")] long Ms,
    [property: JsonPropertyName("err")] string? Error = null);

/// <summary>某个后端在一个月内的用量。</summary>
public sealed record BackendUsage(
    string Backend,
    string Label,
    string Unit,
    int Calls,
    int Ok,
    int Units,
    double Cost,
    int? FreeQuota,
    string Note);

/// <summary>月度汇总。</summary>
public sealed record MonthSummary(
    string Month,
    IReadOnlyList<BackendUsage> Backends,
    int TotalCalls,
    int TotalOk,
    double TotalCost)
{
    public bool IsEmpty => TotalCalls == 0;
}

/// <summary>某后端最近 N 天的成功率（供路由降权）。</summary>
public sealed record BackendStats(int Calls, int Ok, double OkRate);

/// <summary>
/// 联网台账 —— 记「什么时候、用哪个后端、搜了什么、花了几个单位」。
///
/// ★ 为什么用 JSONL 而不是 SQLite（Python 版用的是 SQLite）：
///   1. .NET 的 SQLite 需要 Microsoft.Data.Sqlite 包，本项目的 Core 刻意
///      **零外部依赖**（csproj 里连 System.Text.Json 都不显式引用）。
///      为了一个台账把整条依赖链引进来，和「单文件发布」的目标相冲突。
///   2. 台账的数据量是「每天几十行」级别，一年也就一万多行 ——
///      全量扫一遍在毫秒级，SQLite 的索引优势在这里根本用不上。
///   3. JSONL 崩了只丢最后半行，SQLite 崩了要处理 -wal / -shm / 锁。
///   代价是没有 SQL 聚合能力。所以这里有 <see cref="Summarize"/>：
///      把常用聚合写成代码，比让模型/用户写 SQL 更省事。
///
/// ★ 与 SessionStore 的关系：两者都是「只追加的 JSONL」，但**语义不同**——
///   会话要能被恢复进上下文，台账只给人看。所以它们是两个独立的类，
///   共享的只有「追加一行 + 跳过坏行」这点常识，不值得为它抽一个基类。
/// </summary>
public sealed class UsageLedger
{
    /// <summary>价格与额度表（以官网为准，这里只用于「心里有数」，不是账单依据）。</summary>
    private static readonly Dictionary<string, (string Label, string Unit, int? FreeMonthly, double? PricePer1K, string Note)>
        Plans = new(StringComparer.Ordinal)
        {
            ["deepseek"] = ("官方 Search/Fetch", "次", null, 5.0,
                "只有成功且 results 非空才计费；5 元 / 1000 次"),

            ["tavily"] = ("Tavily", "credit", 1000, null,
                "basic=1 credit / advanced=2 credits"),
        };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();

    // 读缓存：文件长度 + 最后写入时间都没变就复用，省掉反复解析
    private List<UsageEntry>? _cache;
    private long _cacheLength = -1;
    private DateTime _cacheWriteUtc;

    private UsageLedger(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>台账文件路径。</summary>
    public string FilePath => _path;

    /// <summary>在工作台根目录下建（或接上已有的）台账。</summary>
    public static UsageLedger Open(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("工作台根目录不能为空", nameof(workspaceRoot));

        return new UsageLedger(Path.Combine(workspaceRoot, "logs", "usage", "net_usage.jsonl"));
    }

    // ------------------------------------------------------------------
    // 写
    // ------------------------------------------------------------------

    /// <summary>
    /// 记一笔。**绝不抛异常**。
    /// </summary>
    /// <remarks>
    /// 记账失败（磁盘满、目录只读、文件被占用）不该让「搜索」这个真正有用的动作失败。
    /// 这一点与 <see cref="Session.SessionStore.Append"/> 同一个原则：
    /// 副产物不许反噬主功能。
    /// </remarks>
    public void Record(
        string kind,
        string backend,
        string query,
        bool ok,
        int n,
        double units,
        long ms,
        string? error = null)
    {
        try
        {
            var entry = new UsageEntry(
                DateTimeOffset.UtcNow,
                kind,
                backend,
                Clip(query ?? string.Empty, 120),
                ok,
                n,
                units,
                ms,
                error is null ? null : Clip(error, 200));

            var line = JsonSerializer.Serialize(entry, JsonOptions);

            lock (_gate)
            {
                // ★ 崩溃可能留下「没有换行的半行」。不先补一个换行，新记录就会黏在
                //   它后面，变成一条又长又坏的行 —— 而这**一条**坏行会连带吃掉它之后的
                //   所有记录（恢复时整行解析失败，只能跳过）。
                //   这正是 SessionStore.AttachCore 里处理过的同一件事，第一次写台账时漏了。
                var needsNewline = EndsWithoutNewline(_path);

                using var fs = new FileStream(
                    _path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);

                if (needsNewline)
                {
                    var nl = Encoding.UTF8.GetBytes("\n");
                    fs.Write(nl, 0, nl.Length);
                }

                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush();

                // 缓存作废：下次读会重新扫
                _cache = null;
            }
        }
        catch (Exception)
        {
            // 记账失败静默 —— 见上面的说明
        }
    }

    // ------------------------------------------------------------------
    // 读
    // ------------------------------------------------------------------

    /// <summary>读出全部记录（坏行跳过）。</summary>
    public IReadOnlyList<UsageEntry> All()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
                return Array.Empty<UsageEntry>();

            var info = new FileInfo(_path);

            if (_cache is not null && info.Length == _cacheLength && info.LastWriteTimeUtc == _cacheWriteUtc)
                return _cache;

            var list = new List<UsageEntry>();

            try
            {
                using var fs = new FileStream(
                    _path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                using var reader = new StreamReader(fs, Encoding.UTF8);

                while (reader.ReadLine() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        var e = JsonSerializer.Deserialize<UsageEntry>(line, JsonOptions);
                        if (e is not null)
                            list.Add(e);
                    }
                    catch (JsonException)
                    {
                        // 崩溃留下的半行：跳过。前面所有记录都还在。
                    }
                }
            }
            catch (IOException)
            {
                return _cache is null ? Array.Empty<UsageEntry>() : _cache;
            }

            _cache = list;
            _cacheLength = info.Length;
            _cacheWriteUtc = info.LastWriteTimeUtc;

            return list;
        }
    }

    /// <summary>某后端最近 N 天的成功率。样本为 0 时 OkRate 记 1.0（不惩罚没跑过的后端）。</summary>
    public BackendStats BackendStats(string backend, int days = 30)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, days));

        var calls = 0;
        var ok = 0;

        foreach (var e in All())
        {
            if (e.Ts < since)
                continue;

            if (!string.Equals(e.Backend, backend, StringComparison.Ordinal))
                continue;

            calls++;
            if (e.Ok) ok++;
        }

        return new BackendStats(calls, ok, calls == 0 ? 1.0 : (double)ok / calls);
    }

    /// <summary>
    /// 月度汇总。<paramref name="month"/> 为 <c>yyyy-MM</c>，null = 本月（本地时区）。
    /// </summary>
    public MonthSummary Summarize(string? month = null)
    {
        var monthKey = string.IsNullOrWhiteSpace(month)
            ? DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            : month.Trim();

        var acc = new Dictionary<string, (int Calls, int Ok, int Units)>(StringComparer.Ordinal);

        foreach (var e in All())
        {
            // ★ 按月归属用**本地时间**：用户问「本月花了多少」时，
            //   指的是他日历上的那个月，不是 UTC 的。月初/月末几小时的差异
            //   会让「10 月 1 日凌晨的调用」落到 9 月，用户只会觉得数字算错了。
            var local = e.Ts.ToLocalTime();

            if (local.ToString("yyyy-MM", CultureInfo.InvariantCulture) != monthKey)
                continue;

            if (!acc.TryGetValue(e.Backend, out var cur))
                cur = (0, 0, 0);

            acc[e.Backend] = (
                cur.Calls + 1,
                cur.Ok + (e.Ok ? 1 : 0),
                cur.Units + (int)Math.Round(e.Units));
        }

        var rows = new List<BackendUsage>();

        foreach (var (name, (calls, ok, units)) in acc.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // ★ 用 TryGetValue 而不是 GetValueOrDefault(name, 默认元组)：
            //   后者写出来的默认值会**丢掉元素名**，于是 plan.PricePer1K 编译不过 ——
            //   C# 里元组元素名只存在于「声明处」，不会跟着值一起走。
            var plan = Plans.TryGetValue(name, out var hit)
                ? hit
                : (Label: name, Unit: "次", FreeMonthly: (int?)null, PricePer1K: (double?)null, Note: "价格未知");

            var cost = plan.PricePer1K is { } p ? units / 1000.0 * p : 0;

            rows.Add(new BackendUsage(
                name, plan.Label, plan.Unit, calls, ok, units,
                Math.Round(cost, 2), plan.FreeMonthly, plan.Note));
        }

        return new MonthSummary(
            monthKey,
            rows,
            rows.Sum(r => r.Calls),
            rows.Sum(r => r.Ok),
            Math.Round(rows.Sum(r => r.Cost), 2));
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>文件末尾是否缺换行（缺 = 上次写崩了）。任何异常都当作「不缺」。</summary>
    private static bool EndsWithoutNewline(string path)
    {
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (fs.Length == 0)
                return false;

            fs.Seek(-1, SeekOrigin.End);
            return fs.ReadByte() != '\n';
        }
        catch (Exception)
        {
            return false;
        }
    }
}

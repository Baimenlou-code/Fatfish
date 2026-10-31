using System.Text;
using FatFish.Core.Tools;

namespace FatFish.Core.Plugins;

/// <summary>
/// PluginHost —— 插件的发现、装载、热重载与诊断中枢。
///
/// <b>「自适应」的四个落点，全部在这个类里：</b>
///
/// <list type="number">
/// <item><b>自适应发现</b> —— 扫目录，不需要改代码、不需要注册表。</item>
/// <item><b>自适应能力</b> —— 插件声明 <c>requires</c>，缺能力就明确拒载并说清缺什么。</item>
/// <item><b>自适应风险</b> —— 风险档由执行面推断，清单只能加严。</item>
/// <item><b>自适应生效</b> —— 每轮取工具时查文件指纹；重载失败时保留上一份可用快照。</item>
/// </list>
///
/// <b>热插拔</b>：<see cref="SetEnabled"/> 只改设置文件里的一个布尔，然后立刻重载，
/// 模型下一刻看到的工具集就变了，不需要重启程序。
///
/// <b>设置根的「后绑定」</b>：<see cref="BindSettingsRoot"/>。宿主通常在
/// 「工作台根还没确定」的时候就被创建了（App 启动早期），所以设置根允许事后补上；
/// 不补的话启用/禁用与「记住选中的模型」整体不可用，但插件照常装载。
///
/// 线程模型：装载结果放在一个不可变 <see cref="State"/> 里，用 <c>volatile</c> 发布。
/// 读到旧快照的线程会拿到一份自洽的旧数据。写入只发生在 <see cref="Reload"/> 的锁内。
/// </summary>
public sealed class PluginHost : IToolSource, IPluginRiskTable
{
    private sealed record State(
        IReadOnlyList<PluginManifest> Manifests,
        IReadOnlyList<ITool> Tools,
        IReadOnlyList<PluginDiagnostic> Diagnostics,
        IReadOnlyDictionary<string, PluginRiskInfo> Risks,
        string Fingerprint,
        DateTimeOffset LoadedAt)
    {
        public static State Empty { get; } = new(
            Array.Empty<PluginManifest>(),
            Array.Empty<ITool>(),
            Array.Empty<PluginDiagnostic>(),
            new Dictionary<string, PluginRiskInfo>(StringComparer.Ordinal),
            string.Empty,
            DateTimeOffset.MinValue);
    }

    private readonly PluginHostOptions _options;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    /// <summary>后绑定的设置根（见 <see cref="BindSettingsRoot"/>）。</summary>
    private volatile string? _boundRoot;

    /// <summary>当前快照。只读、不可变，所以 volatile 发布足够。</summary>
    private volatile State _state = State.Empty;

    private long _lastCheckTicks;
    private int _loadCount;

    private PluginHost(PluginHostOptions options, Action<string>? log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    /// <summary>创建并完成首次装载（失败也不抛 —— 插件坏了不该拦着程序启动）。</summary>
    public static PluginHost Start(PluginHostOptions options, Action<string>? log = null)
    {
        var host = new PluginHost(options, log);
        var report = host.Reload(force: true);

        log?.Invoke("[插件] " + report.Summary);

        foreach (var d in report.Diagnostics
                     .Where(x => x.Level != PluginDiagnosticLevel.Info)
                     .Take(8))
        {
            log?.Invoke("[插件] " + d);
        }

        return host;
    }

    // ------------------------------------------------------------------
    // IToolSource
    // ------------------------------------------------------------------

    public IReadOnlyList<ITool> Snapshot()
    {
        AutoRefresh();
        return _state.Tools;
    }

    public bool IsStale => !string.Equals(ComputeFingerprint(), _state.Fingerprint, StringComparison.Ordinal);

    public string Describe()
        => _state.Tools.Count == 0
            ? "插件：未装载"
            : $"插件：{_state.Manifests.Count} 个 / {_state.Tools.Count} 个工具";

    // ------------------------------------------------------------------
    // IPluginRiskTable
    // ------------------------------------------------------------------

    public PluginRiskInfo? Lookup(string toolName)
    {
        if (string.IsNullOrEmpty(toolName))
            return null;

        return _state.Risks.TryGetValue(toolName, out var info) ? info : null;
    }

    // ------------------------------------------------------------------
    // 查询
    // ------------------------------------------------------------------

    public IReadOnlyList<PluginManifest> Manifests => _state.Manifests;

    public IReadOnlyList<PluginDiagnostic> Diagnostics => _state.Diagnostics;

    public DateTimeOffset LoadedAt => _state.LoadedAt;

    public int LoadCount => Volatile.Read(ref _loadCount);

    public IReadOnlyList<PluginDirectory> Directories => _options.Directories;

    public IReadOnlyDictionary<string, PluginRiskInfo> Risks => _state.Risks;

    /// <summary>按名找插件工具（找内置工具请用 <c>ToolRegistry.Find</c>）。</summary>
    public PluginTool? FindTool(string name)
        => _state.Tools.OfType<PluginTool>()
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>按 id 找插件（大小写不敏感）。</summary>
    public PluginManifest? FindPlugin(string id)
        => _state.Manifests.FirstOrDefault(
            m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------
    // 设置与热插拔
    // ------------------------------------------------------------------

    /// <summary>设置根：后绑定优先，其次构造时给的。</summary>
    public string? SettingsRoot => _boundRoot ?? _options.SettingsRoot;

    /// <summary>设置根是否可用。不可用时启用/禁用与模型记忆整体失效，但插件照常装载。</summary>
    public bool SupportsSettings => !string.IsNullOrWhiteSpace(SettingsRoot);

    /// <summary>
    /// 补上设置根并立刻重载（幂等；根没变化时什么也不做）。
    /// </summary>
    /// <remarks>
    /// 为什么需要它：<see cref="Start"/> 发生在 App 启动早期，那时工作台根往往还没解析出来
    /// （要读 .env、要解析 <c>FATFISH_WORKSPACE</c>）。为了这个把 PluginHost 的创建推到后面，
    /// 会让工具集在窗口出现前处于"没有插件"的状态 —— 那段时间里模型看到的工具是残缺的。
    /// 与其调整初始化顺序，不如允许事后补一个根。
    /// </remarks>
    public void BindSettingsRoot(string? root)
    {
        var normalized = string.IsNullOrWhiteSpace(root) ? null : root.Trim();

        if (string.Equals(normalized, _boundRoot, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            _boundRoot = normalized;
        }

        Reload(force: true);
    }

    /// <summary>读某个插件的设置；没有设置文件或读坏了都返回默认值。</summary>
    public PluginSettings SettingsFor(string pluginId)
    {
        if (!SupportsSettings || string.IsNullOrWhiteSpace(pluginId))
            return PluginSettings.Default;

        var (settings, _) = PluginSettingsStore.Load(SettingsRoot!, pluginId);
        return settings;
    }

    /// <summary>写回设置。返回 null 表示成功，否则是给人看的原因。</summary>
    public string? SaveSettings(string pluginId, PluginSettings settings)
    {
        if (!SupportsSettings)
            return "插件宿主没有配置设置根，设置写不进去";

        return PluginSettingsStore.Save(SettingsRoot!, pluginId, settings);
    }

    /// <summary>
    /// 启用 / 禁用某个插件，并**立刻**重载（热插拔入口）。
    /// </summary>
    /// <remarks>
    /// <b>为什么禁用要真的重载，而不是在 <c>Snapshot()</c> 里过滤：</b>
    /// 过滤只影响「这一个调用方」看到的列表，而 <see cref="IPluginRiskTable.Lookup"/>
    /// 与审批层读到的是两份独立数据。只过滤一处会出现
    /// 「工具不见了，但审批表里还留着一个永远调不到的高危条目」这种半截状态。
    /// 重载一次，三份数据一起换掉。
    ///
    /// <b>为什么立刻生效：</b>用户点「禁用」时的预期是「它现在就不该再动了」。
    /// </remarks>
    public PluginReloadReport SetEnabled(string pluginId, bool enabled)
    {
        if (!SupportsSettings)
            throw new InvalidOperationException("插件宿主没有设置根，无法启用 / 禁用插件");

        var current = SettingsFor(pluginId);

        if (SaveSettings(pluginId, current with { Enabled = enabled }) is { } error)
            throw new InvalidOperationException(error);

        var report = Reload(force: true);
        _log?.Invoke($"[插件] {pluginId} → {(enabled ? "已启用" : "已禁用")}；{report.Summary}");
        return report;
    }

    // ------------------------------------------------------------------
    // 装载
    // ------------------------------------------------------------------

    /// <summary>重新扫描并装载。</summary>
    /// <param name="force">true 表示即使指纹没变也重扫（<c>/plugin reload</c> 用）。</param>
    public PluginReloadReport Reload(bool force = false)
    {
        lock (_gate)
        {
            var fingerprint = ComputeFingerprint();
            var previous = _state;

            if (!force
                && previous.LoadedAt != DateTimeOffset.MinValue
                && string.Equals(fingerprint, previous.Fingerprint, StringComparison.Ordinal))
            {
                return new PluginReloadReport(
                    false, previous.Manifests.Count, previous.Tools.Count,
                    previous.Diagnostics.Count(d => d.Level == PluginDiagnosticLevel.Error),
                    previous.Diagnostics.Count(d => d.Level == PluginDiagnosticLevel.Warning),
                    previous.Diagnostics,
                    "清单没有变化");
            }

            var (manifests, tools, diags, risks, filesFound) = Scan();

            // ---- 用户禁用（热插拔）----
            // 放在熔断之前：先按「用户明确关掉的」剪一遍，再看剩下的是不是空的。
            var disabled = ApplyEnabledFilter(ref manifests, ref tools, ref risks, diags);

            var errors = diags.Count(d => d.Level == PluginDiagnosticLevel.Error);
            var warnings = diags.Count(d => d.Level == PluginDiagnosticLevel.Warning);

            // ---- 失败熔断 ----
            // 磁盘上有清单却一个工具都没装出来（清单写到一半、手滑删了括号），
            // 保留上一份快照，让人有修好的机会。
            //
            // ★ 但「用户把所有插件都禁用掉」也会得到 0 个工具 —— 那不是故障，
            //   所以 disabled > 0 时不熔断，否则「全部禁用」会静默失效。
            if (tools.Count == 0 && filesFound > 0 && previous.Tools.Count > 0 && disabled == 0)
            {
                var kept = diags.ToList();
                kept.Add(new PluginDiagnostic(PluginDiagnosticLevel.Error, "host",
                    "本次重载没有得到任何可用工具，已保留上一次的成功快照（修好清单后会自动恢复）"));

                _state = previous with { Diagnostics = kept };

                return new PluginReloadReport(
                    false, previous.Manifests.Count, previous.Tools.Count,
                    errors, warnings, kept,
                    $"重载失败（{errors} 个错误），继续使用上一次的快照");
            }

            _state = new State(manifests, tools, diags, risks, fingerprint, DateTimeOffset.UtcNow);
            Interlocked.Increment(ref _loadCount);

            var message = filesFound == 0
                ? "没有发现任何插件（放一个 <目录>/plugin.json 进去即可）"
                : disabled > 0
                    ? $"已装载 {manifests.Count} 个插件 / {tools.Count} 个工具（{disabled} 个已禁用）"
                    : $"已装载 {manifests.Count} 个插件 / {tools.Count} 个工具";

            return new PluginReloadReport(
                true, manifests.Count, tools.Count, errors, warnings, diags, message);
        }
    }

    /// <summary>
    /// 按设置文件里的 <c>enabled</c> 剪掉被禁用的插件。返回被禁用掉的插件个数。
    /// </summary>
    /// <remarks>
    /// 用 <c>ref</c> 而不是返回四个值：调用点就在 <see cref="Reload"/> 里紧跟着
    /// <see cref="Scan"/> 的那几行，拆成元组反而更难读。
    /// </remarks>
    private int ApplyEnabledFilter(
        ref List<PluginManifest> manifests,
        ref List<ITool> tools,
        ref Dictionary<string, PluginRiskInfo> risks,
        List<PluginDiagnostic> diags)
    {
        if (!SupportsSettings)
            return 0;

        var disabledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in manifests)
        {
            var (settings, error) = PluginSettingsStore.Load(SettingsRoot!, manifest.Id);

            if (error is not null)
            {
                // 读坏了当作启用：不能因为一个手改坏的 json 让用户的插件凭空消失。
                diags.Add(new PluginDiagnostic(PluginDiagnosticLevel.Warning, manifest.Id, error));
                continue;
            }

            if (!settings.Enabled)
                disabledIds.Add(manifest.Id);
        }

        if (disabledIds.Count == 0)
            return 0;

        foreach (var id in disabledIds)
        {
            diags.Add(new PluginDiagnostic(PluginDiagnosticLevel.Info, id,
                "已在插件设置里禁用，工具不会被注册给模型"));
        }

        // ★ 有意**不**从 Manifests 里删掉被禁用的插件：
        //   插件栏要能列出「已禁用」的那些，否则用户一旦禁用就再也点不到"启用"
        //   （只能去手改 json），这等于给出了一条单向的路。
        //   它们没有工具、没有风险条目，对模型完全不可见，这才是"禁用"的含义。
        tools = tools
            .Where(t => t is not PluginTool pt || !disabledIds.Contains(pt.Manifest.Id))
            .ToList();

        var keptRisks = new Dictionary<string, PluginRiskInfo>(StringComparer.Ordinal);

        foreach (var (name, info) in risks)
        {
            if (!disabledIds.Contains(info.PluginId))
                keptRisks[name] = info;
        }

        risks = keptRisks;

        return disabledIds.Count;
    }

    /// <summary>带节流的自动检查 —— 由 <see cref="Snapshot"/> 触发。</summary>
    private void AutoRefresh()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastCheckTicks);

        if (now - last < (long)_options.ReloadThrottle.TotalMilliseconds)
            return;

        // 谁抢到谁检查：抢不到的直接返回，避免 N 个调用同时 stat 磁盘。
        if (Interlocked.CompareExchange(ref _lastCheckTicks, now, last) != last)
            return;

        if (!IsStale)
            return;

        try
        {
            Reload(force: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 自动重载绝不能把「取工具列表」这件事变成异常 ——
            // 那会让整轮对话在 BuildToolSchema 处中断。
            _log?.Invoke("[插件] 自动重载失败：" + ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // 扫描
    // ------------------------------------------------------------------

    private (List<PluginManifest> Manifests,
             List<ITool> Tools,
             List<PluginDiagnostic> Diagnostics,
             Dictionary<string, PluginRiskInfo> Risks,
             int FilesFound) Scan()
    {
        var diags = new List<PluginDiagnostic>();
        var byId = new Dictionary<string, PluginManifest>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var filesFound = 0;

        foreach (var dir in _options.Directories)
        {
            if (string.IsNullOrWhiteSpace(dir.Path))
                continue;

            if (!Directory.Exists(dir.Path))
            {
                diags.Add(new PluginDiagnostic(PluginDiagnosticLevel.Info, "host",
                    $"插件目录不存在：{dir.Path}"));
                continue;
            }

            foreach (var file in FindManifestFiles(dir.Path))
            {
                filesFound++;

                var outcome = PluginManifestParser.ParseFile(file, dir.BuiltIn);
                diags.AddRange(outcome.Diagnostics);

                if (outcome.Manifest is not { } manifest)
                    continue;

                // ---- 能力比对 ----
                var missing = manifest.Requires
                    .Where(r => !_options.Capabilities.Contains(r))
                    .ToList();

                if (missing.Count > 0)
                {
                    diags.Add(new PluginDiagnostic(PluginDiagnosticLevel.Error, manifest.Id,
                        $"缺少宿主能力 [{string.Join(", ", missing)}]，插件未装载"));
                    continue;
                }

                // ---- 同名 id：后扫描到的覆盖先扫描到的 ----
                if (byId.TryGetValue(manifest.Id, out var shadowed))
                {
                    diags.Add(new PluginDiagnostic(PluginDiagnosticLevel.Info, manifest.Id,
                        $"已被 {file} 覆盖（原：{shadowed.ManifestPath}）"));
                }
                else
                {
                    order.Add(manifest.Id);
                }

                byId[manifest.Id] = manifest;
            }
        }

        // ---- 工具名全局唯一（含内置工具）----
        var used = new HashSet<string>(_options.ReservedToolNames, StringComparer.Ordinal);
        var tools = new List<ITool>();
        var risks = new Dictionary<string, PluginRiskInfo>(StringComparer.Ordinal);
        var manifests = new List<PluginManifest>();

        foreach (var id in order)
        {
            var manifest = byId[id];
            var accepted = new List<PluginToolSpec>();

            foreach (var spec in manifest.Tools)
            {
                if (used.Contains(spec.Name))
                {
                    diags.Add(new PluginDiagnostic(PluginDiagnosticLevel.Error, id,
                        $"工具名 '{spec.Name}' 已被占用（内置工具或另一个插件），该工具跳过"));
                    continue;
                }

                used.Add(spec.Name);
                accepted.Add(spec);
                tools.Add(new PluginTool(manifest, spec));

                risks[spec.Name] = new PluginRiskInfo(
                    id, spec.EffectiveRisk, spec.IsReadOnly, spec.RiskLabel);
            }

            if (accepted.Count == 0)
            {
                diags.Add(new PluginDiagnostic(PluginDiagnosticLevel.Error, id,
                    "该插件的全部工具都因命名冲突被跳过，插件不生效"));
                continue;
            }

            // ---- 模型槽位里写的工具名必须真的存在 ----
            // 写错一个名字，症状是「这个槽位永远不生效」：界面上照常能选、能保存，
            // 出图却一直用 .env 里的旧模型 —— 静默失效最难查，所以至少留一条警告。
            if (manifest.Settings is { } slotSpec && slotSpec.ModelSlots.Count > 0)
            {
                var knownTools = new HashSet<string>(accepted.Select(t => t.Name), StringComparer.Ordinal);

                foreach (var slot in slotSpec.ModelSlots)
                {
                    foreach (var toolName in slot.Tools.Where(t => !knownTools.Contains(t)))
                    {
                        diags.Add(new PluginDiagnostic(PluginDiagnosticLevel.Warning, id,
                            $"settings.modelSlots['{slot.Id}'] 提到的工具 '{toolName}' 不在这个插件里，这个槽位不会生效"));
                    }
                }
            }

            manifests.Add(manifest with { Tools = accepted });
        }

        return (manifests, tools, diags, risks, filesFound);
    }

    /// <summary>
    /// 列出一个目录下的全部清单文件。
    /// </summary>
    /// <remarks>
    /// 同时支持两种布局：
    ///   <c>plugins/hello/plugin.json</c>   —— 一个子目录一个插件（推荐）
    ///   <c>plugins/plugin.json</c>          —— 目录本身就是一个插件
    /// 后者不是多余的：把插件整个文件夹拷进 plugins/ 之后，
    /// 用户很可能只解压出一层，能直接跑比让人再建一层目录友善。
    /// </remarks>
    private static List<string> FindManifestFiles(string dir)
    {
        var found = new List<string>();

        try
        {
            var single = Path.Combine(dir, PluginManifestParser.ManifestFileName);

            if (File.Exists(single))
                found.Add(single);

            foreach (var sub in Directory
                         .EnumerateDirectories(dir)
                         .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(sub, PluginManifestParser.ManifestFileName);

                if (File.Exists(file))
                    found.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 目录不可读：已收集到的照常返回 —— 部分可用好过全废。
        }

        return found;
    }

    /// <summary>
    /// 指纹 = 全部清单文件的「路径 + 大小 + 修改时间」拼串。
    /// </summary>
    /// <remarks>
    /// 不用内容哈希：清单只有几百行，拼串比较已经足够快，
    /// 而读全部文件内容会在每次检查时产生不必要的磁盘 IO。
    /// </remarks>
    private string ComputeFingerprint()
    {
        var sb = new StringBuilder();

        foreach (var dir in _options.Directories)
        {
            if (string.IsNullOrWhiteSpace(dir.Path))
                continue;

            sb.Append(dir.Path).Append('|').Append(dir.BuiltIn).Append('\n');

            foreach (var file in FindManifestFiles(dir.Path))
            {
                try
                {
                    var fi = new FileInfo(file);

                    sb.Append(file).Append('|')
                      .Append(fi.Exists ? fi.Length : -1).Append('|')
                      .Append(fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0).Append('\n');
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    sb.Append(file).Append("|error\n");
                }
            }
        }

        return sb.ToString();
    }
}

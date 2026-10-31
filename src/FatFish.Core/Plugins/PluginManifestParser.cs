using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FatFish.Core.Plugins;

/// <summary>一次清单解析的结果。失败时 <see cref="Manifest"/> 为 null，原因在 <see cref="Diagnostics"/> 里。</summary>
public sealed record PluginParseOutcome(
    PluginManifest? Manifest,
    IReadOnlyList<PluginDiagnostic> Diagnostics)
{
    public bool Ok => Manifest is not null;

    public bool HasError => Diagnostics.Any(d => d.Level == PluginDiagnosticLevel.Error);
}

/// <summary>
/// <c>plugin.json</c> 的解析与校验。
///
/// 校验的总体原则：**能在装载时拦下，就不要留到运行时**。
/// 插件运行时的失败极难排查 —— 模型看到的是「工具调用失败」，
/// 用户看到的是「插件没反应」，而真正的原因（比如 URL 模板里写错了占位符名）
/// 要翻三层日志才能看到。所以这里宁可严一点，把每条规则都变成
/// 一句能照着改的中文说明。
///
/// 另一条原则：**单条工具出错不牵连整个插件**。
/// 一个插件里写着五个工具、其中两个写坏了，另外三个应该照常可用；
/// 只有「id / name / tools 这种骨架字段」坏了才整插件不加载。
/// </remarks>
public static class PluginManifestParser
{
    public const string ManifestFileName = "plugin.json";

    /// <summary>支持的宿主能力。声明了不在这张表里的能力 = 要求更高版本的宿主，拒载。</summary>
    public static readonly IReadOnlySet<string> KnownCapabilities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "workspace", "net", "python" };

    /// <summary>内置工具的前缀 —— 插件工具名**不许**用，否则可能伪装成内置工具。</summary>
    /// <remarks>
    /// 这条不是洁癖：<c>ApprovalPolicy</c> 里有针对 <c>ws_read</c> 的敏感文件特例、
    /// <c>ToolPolicyCoverageTests</c> 里有针对 <c>web_</c> / <c>net_</c> 的只读断言。
    /// 若允许插件起名 <c>ws_read2</c> 之类，绕过审批表只需要「起个像内置工具的名字」。
    /// </remarks>
    public static readonly IReadOnlyList<string> ReservedPrefixes = ["ws_", "web_", "net_"];

    private static readonly Regex IdPattern = new(
        @"^[a-z][a-z0-9-]{1,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ToolNamePattern = new(
        @"^[a-z][a-z0-9_]{2,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ParamPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]{0,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlaceholderPattern = new(
        @"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HostPattern = new(
        @"^(\*\.)?[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string EmptySchemaJson = """{"type":"object","properties":{}}""";

    // ------------------------------------------------------------------
    // 入口
    // ------------------------------------------------------------------

    public static PluginParseOutcome ParseFile(string manifestPath, bool builtIn = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        try
        {
            var json = File.ReadAllText(manifestPath, Encoding.UTF8);
            return Parse(json, manifestPath, builtIn);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? manifestPath;
            return Fail(Path.GetFileName(dir), $"读取清单失败：{ex.Message}");
        }
    }

    public static PluginParseOutcome Parse(string json, string manifestPath, bool builtIn = false)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        var fullPath = Path.GetFullPath(manifestPath);
        var pluginDir = Path.GetDirectoryName(fullPath) ?? ".";
        var source = Path.GetFileName(pluginDir);

        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                // 允许注释和尾逗号：手写的 JSON 十有八九会带这两样，
                // 为它们报「语法错误」只会让作者去猜哪里错了。
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber is { } n ? $"（第 {n + 1} 行）" : string.Empty;
            return Fail(source, $"JSON 语法错误{line}：{ex.Message}");
        }

        using (doc)
        {
            return ParseRoot(doc.RootElement, source, pluginDir, fullPath, builtIn);
        }
    }

    private static PluginParseOutcome ParseRoot(
        JsonElement root, string source, string pluginDir, string manifestPath, bool builtIn)
    {
        var diags = new List<PluginDiagnostic>();

        void Error(string msg) => diags.Add(new(PluginDiagnosticLevel.Error, source, msg));
        void Warn(string msg) => diags.Add(new(PluginDiagnosticLevel.Warning, source, msg));

        if (root.ValueKind != JsonValueKind.Object)
            return Fail(source, $"清单根必须是 JSON 对象，实际是 {root.ValueKind}");

        // ---- 显式禁用 ----
        if (root.TryGetProperty("enabled", out var enabledEl)
            && enabledEl.ValueKind == JsonValueKind.False)
        {
            diags.Add(new(PluginDiagnosticLevel.Info, source, "清单里 enabled=false，已跳过"));
            return new PluginParseOutcome(null, diags);
        }

        // ---- id ----
        var id = Str(root, "id")?.Trim();

        if (string.IsNullOrEmpty(id))
        {
            Error("缺少 id（小写字母开头，可含数字与短横线，例如 stock-quote）");
            return new PluginParseOutcome(null, diags);
        }

        if (!IdPattern.IsMatch(id))
        {
            Error($"id '{id}' 不合法：必须小写字母开头，只含小写字母 / 数字 / 短横线，长度 2~41");
            return new PluginParseOutcome(null, diags);
        }

        if (!string.Equals(id, source, StringComparison.OrdinalIgnoreCase))
        {
            // 不阻断，只提示：目录名与 id 不一致会让「按目录找插件」的人扑空。
            Warn($"目录名 '{source}' 与 id '{id}' 不一致（建议保持一致，便于排查）");
        }

        source = id;

        var name = Str(root, "name")?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Error("缺少 name（给人看的插件名）");
            return new PluginParseOutcome(null, diags);
        }

        var version = Str(root, "version")?.Trim() ?? "0.0.0";
        var description = Str(root, "description")?.Trim() ?? string.Empty;

        if (description.Length == 0)
            Warn("缺少 description；它只影响人读，不影响模型");

        // ---- requires ----
        var requires = new List<string>();
        var requiresUnsupported = false;

        if (root.TryGetProperty("requires", out var reqEl) && reqEl.ValueKind != JsonValueKind.Null)
        {
            if (reqEl.ValueKind != JsonValueKind.Array)
            {
                Error("requires 必须是字符串数组，例如 [\"net\"]");
            }
            else
            {
                foreach (var item in reqEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        Error("requires 里只能放字符串");
                        continue;
                    }

                    var cap = item.GetString()!.Trim().ToLowerInvariant();

                    if (!KnownCapabilities.Contains(cap))
                    {
                        Error($"requires 里声明了宿主不支持的能力 '{cap}'（当前支持：{string.Join(", ", KnownCapabilities.OrderBy(x => x, StringComparer.Ordinal))}）；" +
                              "这通常表示插件要求更高版本的 FatFish");

                        // ★ 必须整插件拒绝，而不是「记一条错误、继续加载」。
                        //   原因：requires 是插件对宿主能力的**硬前提**。
                        //   记下错误再继续，插件会在缺少依赖的情况下被装上去 ——
                        //   然后第一次调用才炸，而模型看到的只是「工具执行失败」。
                        //   更糟的是：卸载点（Host）看不到这条要求，因为它已经被丢掉了。
                        requiresUnsupported = true;
                        continue;
                    }

                    if (!requires.Contains(cap))
                        requires.Add(cap);
                }
            }
        }

        // ---- tools ----
        if (requiresUnsupported)
            return new PluginParseOutcome(null, diags);

        if (!root.TryGetProperty("tools", out var toolsEl) || toolsEl.ValueKind != JsonValueKind.Array)
        {
            Error("缺少 tools 数组（一个插件至少要提供一个工具）");
            return new PluginParseOutcome(null, diags);
        }

        var tools = new List<PluginToolSpec>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;

        foreach (var toolEl in toolsEl.EnumerateArray())
        {
            var spec = TryParseTool(toolEl, index, id, pluginDir, diags);

            if (spec is not null)
            {
                if (!seenNames.Add(spec.Name))
                {
                    diags.Add(new(PluginDiagnosticLevel.Error, id,
                        $"工具名 '{spec.Name}' 在本插件里重复；后一个已跳过"));
                }
                else
                {
                    tools.Add(spec);
                }
            }

            index++;
        }

        if (tools.Count == 0)
        {
            Error("没有任何工具通过校验，插件不加载");
            return new PluginParseOutcome(null, diags);
        }

        if (tools.Count < index)
            Warn($"{index} 个工具里有 {index - tools.Count} 个未通过校验（见上方错误），其余照常装载");

        var settings = TryParseSettings(root, id, Error, Warn);

        var manifest = new PluginManifest
        {
            Id = id,
            Name = name,
            Version = version,
            Description = description,
            Requires = requires,
            Tools = tools,
            DirectoryPath = pluginDir,
            ManifestPath = manifestPath,
            Settings = settings,
            BuiltIn = builtIn,
        };

        return new PluginParseOutcome(manifest, diags);
    }

    // ------------------------------------------------------------------
    // 单个工具
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析清单里的 settings 块：把这个插件在 .env 里占的键、以及去哪查模型，只声明一次。
    /// </summary>
    /// <remarks>
    /// 这一块**可以不写**：没有它的插件照样能用，只是插件栏里不会出现齿轮、
    /// 也不参与模型发现。写错了则拒载 —— 归属声明错了，界面会把别的插件的键
    /// 摆到它名下，那比没有设置页更糟。
    /// </remarks>
    private static PluginSettingsSpec? TryParseSettings(
        JsonElement root, string pluginId, Action<string> error, Action<string> warn)
    {
        if (!root.TryGetProperty("settings", out var el) || el.ValueKind == JsonValueKind.Null)
            return null;

        if (el.ValueKind != JsonValueKind.Object)
        {
            error("settings 必须是对象，例如 { \"configKeys\": [\"MY_API_KEY\"] }");
            return null;
        }

        var bad = false;

        string? Name(string field)
        {
            var raw = Str(el, field)?.Trim();

            if (string.IsNullOrEmpty(raw))
                return null;

            if (!FatFish.Core.Config.EnvValueCodec.IsValidKey(raw))
            {
                error($"settings.{field} 里的 '{raw}' 不是合法的环境变量名（字母数字下划线，不以数字开头）");
                bad = true;
                return null;
            }

            return raw;
        }

        var configKeys = new List<string>();

        if (el.TryGetProperty("configKeys", out var keysEl) && keysEl.ValueKind != JsonValueKind.Null)
        {
            if (keysEl.ValueKind != JsonValueKind.Array)
            {
                error("settings.configKeys 必须是字符串数组");
                return null;
            }

            foreach (var item in keysEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    error("settings.configKeys 里只能放字符串");
                    return null;
                }

                var key = item.GetString()!.Trim();

                if (!FatFish.Core.Config.EnvValueCodec.IsValidKey(key))
                {
                    error($"settings.configKeys 里的 '{key}' 不是合法的环境变量名");
                    return null;
                }

                if (!configKeys.Contains(key, StringComparer.Ordinal))
                    configKeys.Add(key);
            }
        }

        var keysEnv = Name("keysEnv");
        var keyEnv = Name("keyEnv");
        var baseEnv = Name("baseEnv");
        var modelEnv = Name("modelEnv");

        if (bad)
            return null;

        // ---- 模型槽位（modelSlots）----
        // 一个插件可以有多个模型：生图一个、改图一个。每个槽位声明「写回哪个 .env 键」
        // 以及「管哪些工具」；宿主执行某个工具时按工具名挑槽位（见 PluginModelSlots）。
        var slots = new List<PluginModelSlot>();

        if (el.TryGetProperty("modelSlots", out var slotsEl) && slotsEl.ValueKind != JsonValueKind.Null)
        {
            if (slotsEl.ValueKind != JsonValueKind.Array)
            {
                error("settings.modelSlots 必须是数组，例如 [{\"id\":\"generate\",\"env\":\"MY_MODEL\"}]");
                return null;
            }

            var slotIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var slotEnvs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var slotEl in slotsEl.EnumerateArray())
            {
                if (slotEl.ValueKind != JsonValueKind.Object)
                {
                    error("settings.modelSlots 里的每一项都必须是对象");
                    return null;
                }

                var slotId = Str(slotEl, "id")?.Trim();

                if (string.IsNullOrEmpty(slotId) || !IdPattern.IsMatch(slotId))
                {
                    error($"settings.modelSlots 里的 id '{slotId}' 不合法：小写字母开头，只含小写字母 / 数字 / 短横线");
                    return null;
                }

                if (!slotIds.Add(slotId))
                {
                    error($"settings.modelSlots 里有两个槽位都叫 '{slotId}' —— 设置文件只能记一个选择，名字必须唯一");
                    return null;
                }

                var slotEnv = Str(slotEl, "env")?.Trim() ?? Str(slotEl, "modelEnv")?.Trim();

                if (string.IsNullOrEmpty(slotEnv) || !FatFish.Core.Config.EnvValueCodec.IsValidKey(slotEnv))
                {
                    error($"settings.modelSlots['{slotId}'] 缺少合法的 env（要写回 .env 的哪个键）");
                    return null;
                }

                if (!slotEnvs.Add(slotEnv))
                {
                    error($"settings.modelSlots 里有两个槽位都写回 {slotEnv} —— 后写入的会覆盖前一个");
                    return null;
                }

                var slotLabel = Str(slotEl, "label")?.Trim();
                var slotTools = new List<string>();

                if (slotEl.TryGetProperty("tools", out var slotToolsEl) && slotToolsEl.ValueKind != JsonValueKind.Null)
                {
                    if (slotToolsEl.ValueKind != JsonValueKind.Array)
                    {
                        error($"settings.modelSlots['{slotId}'].tools 必须是字符串数组");
                        return null;
                    }

                    foreach (var toolItem in slotToolsEl.EnumerateArray())
                    {
                        if (toolItem.ValueKind != JsonValueKind.String)
                        {
                            error($"settings.modelSlots['{slotId}'].tools 里只能放工具名字符串");
                            return null;
                        }

                        var slotTool = toolItem.GetString()!.Trim();

                        if (slotTool.Length > 0 && !slotTools.Contains(slotTool, StringComparer.Ordinal))
                            slotTools.Add(slotTool);
                    }
                }

                slots.Add(new PluginModelSlot
                {
                    Id = slotId,
                    Label = string.IsNullOrEmpty(slotLabel) ? null : slotLabel,
                    Env = slotEnv,
                    Tools = slotTools,
                });
            }

            // modelEnv 与 modelSlots 同时写时以 modelSlots 为准：两个都生效会让同一个
            // 环境变量被覆盖两次，结果取决于遍历顺序 —— 那种不确定性没法排查。
            if (modelEnv is not null
                && !slots.Any(s => string.Equals(s.Env, modelEnv, StringComparison.OrdinalIgnoreCase)))
            {
                warn($"settings.modelEnv 写的是 '{modelEnv}'，但 modelSlots 里没有槽位用它 —— 以 modelSlots 为准，这个字段被忽略");
            }

            foreach (var slot in slots)
            {
                if (configKeys.Count > 0 && !configKeys.Contains(slot.Env, StringComparer.Ordinal))
                    warn($"settings.modelSlots['{slot.Id}'] 的 {slot.Env} 没写进 configKeys —— 界面上不会把它算作这个插件的配置");
            }
        }

        var modelsPath = Str(el, "modelsPath")?.Trim();

        if (string.IsNullOrEmpty(modelsPath))
        {
            modelsPath = "/models";
        }
        else if (!modelsPath.StartsWith('/')
                 || modelsPath.Contains(' ')
                 || modelsPath.Contains("://", StringComparison.Ordinal))
        {
            error($"settings.modelsPath 应该是相对路径，例如 /models，实际是 '{modelsPath}'");
            return null;
        }

        var defaultBase = Str(el, "defaultBase")?.Trim();

        if (!string.IsNullOrEmpty(defaultBase)
            && (!Uri.TryCreate(defaultBase, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)))
        {
            error($"settings.defaultBase 必须是 http(s) 绝对地址，实际是 '{defaultBase}'");
            return null;
        }

        if (baseEnv is null && string.IsNullOrEmpty(defaultBase))
            warn("插件声明了 settings，但没有 baseEnv / defaultBase —— 无法发现模型，只能手动填模型名");

        foreach (var name in new[] { keysEnv, keyEnv, baseEnv, modelEnv })
        {
            if (name is not null && configKeys.Count > 0 && !configKeys.Contains(name, StringComparer.Ordinal))
                warn($"settings 里的 '{name}' 没写进 configKeys —— 界面上不会把它算作这个插件的配置");
        }

        if (configKeys.Count == 0 && keysEnv is null && keyEnv is null && baseEnv is null && modelEnv is null)
            warn($"插件 {pluginId} 的 settings 是空的（这一块等于没写）");

        return new PluginSettingsSpec
        {
            ConfigKeys = configKeys,
            KeysEnv = keysEnv,
            KeyEnv = keyEnv,
            BaseEnv = baseEnv,
            ModelEnv = modelEnv,
            ModelSlots = slots,
            ModelsPath = modelsPath,
            DefaultBase = string.IsNullOrEmpty(defaultBase) ? null : defaultBase,
        };
    }

    private static PluginToolSpec? TryParseTool(
        JsonElement el, int index, string pluginId, string pluginDir, List<PluginDiagnostic> diags)
    {
        var label = $"tools[{index}]";

        void Error(string msg) => diags.Add(new(PluginDiagnosticLevel.Error, pluginId, $"{label}：{msg}"));
        void Warn(string msg) => diags.Add(new(PluginDiagnosticLevel.Warning, pluginId, $"{label}：{msg}"));

        if (el.ValueKind != JsonValueKind.Object)
        {
            Error("必须是 JSON 对象");
            return null;
        }

        // ---- name ----
        var name = Str(el, "name")?.Trim();

        if (string.IsNullOrEmpty(name))
        {
            Error("缺少 name");
            return null;
        }

        if (!ToolNamePattern.IsMatch(name))
        {
            Error($"工具名 '{name}' 不合法：小写字母开头，只含小写字母 / 数字 / 下划线，长度 3~41");
            return null;
        }

        var reserved = ReservedPrefixes.FirstOrDefault(
            p => name.StartsWith(p, StringComparison.Ordinal));

        if (reserved is not null)
        {
            Error($"工具名 '{name}' 使用了内置前缀 '{reserved}'。插件工具不能与内置工具同名或形似，" +
                  "否则审批表里针对内置工具的特例（例如 ws_read 的敏感文件规则）会被绕过");
            return null;
        }

        // ---- description ----
        var description = Str(el, "description")?.Trim();

        if (string.IsNullOrEmpty(description))
        {
            Error("缺少 description —— 模型完全靠它决定何时调用这个工具，不能省略");
            return null;
        }

        // ---- parameters ----
        if (!TryParseParameters(el, label, Error, out var schema, out var paramNames, out var required))
            return null;

        // ---- 参数名 → 环境变量名（冲突检测）----
        var envNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var envOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var envCollision = false;

        foreach (var p in paramNames)
        {
            var env = PluginNaming.EnvName(p);

            if (envOwners.TryGetValue(env, out var owner))
            {
                Error($"参数 '{p}' 与 '{owner}' 映射到同一个环境变量 {env}（只差大小写或符号），" +
                      "命令里无法区分这两个值");
                envCollision = true;
                continue;
            }

            envOwners[env] = p;
            envNames[p] = env;
        }

        if (envCollision)
            return null;

        // ---- run ----
        if (!el.TryGetProperty("run", out var runEl) || runEl.ValueKind != JsonValueKind.Object)
        {
            Error("缺少 run 对象（说明这个工具怎么执行：http / cmd / python）");
            return null;
        }

        var run = TryParseRun(runEl, label, pluginDir, paramNames, Error, Warn);

        if (run is null)
            return null;

        // ---- readOnly / risk ----
        bool? declaredReadOnly = null;

        if (el.TryGetProperty("readOnly", out var roEl))
        {
            if (roEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                declaredReadOnly = roEl.GetBoolean();
            else
                Warn("readOnly 必须是 true / false，已忽略");
        }

        var inferredReadOnly = PluginRisk.InferReadOnly(run);

        // ★ 只读是一种**能力声明**，不能凭愿望。
        //   执行面是 cmd / python 却说自己是只读的，唯一可能就是他真的以为是 ——
        //   而"以为是"的东西不该进入审批判定。
        if (declaredReadOnly == true && !inferredReadOnly)
        {
            Error($"执行面是 {run.Kind}（能执行任意命令 / 代码），不能声明 readOnly: true。" +
                  "若这个工具确实不改任何东西，请去掉 readOnly —— 它仍会照常工作，只是会被报批");
            return null;
        }

        var declaredRisk = PluginRisk.ParseDeclared(Str(el, "risk"));

        if (PluginRisk.IsInvalid(declaredRisk))
        {
            Error($"risk 只能是 auto / low / normal / high，实际是 '{Str(el, "risk")}'");
            return null;
        }

        if (declaredRisk == PluginRiskTier.Low && !inferredReadOnly)
            Warn("声明 risk=low 不起作用：执行面本身不是只读的，风险会自动抬到对应档位");

        var readOnly = inferredReadOnly && declaredReadOnly != false;

        // 声明 readOnly=false 的 GET：作者知道它会改远端状态 → 至少按「写」处理。
        var inferredRisk = inferredReadOnly && readOnly
            ? PluginRisk.Infer(run)
            : PluginRisk.Combine(PluginRisk.Infer(run), PluginRiskTier.Normal);

        var effective = PluginRisk.Combine(inferredRisk, declaredRisk);

        // ★ 关键一致性：只要生效档位高于 Low，就不能再自称只读。
        //   否则会出现「声明 high、HasSideEffects=false」的矛盾 ——
        //   而 AgentLoop 的核验判定**只看 HasSideEffects**，这一矛盾会让
        //   「作者主动加严」反而换来「跳过 AI 核验」，正好反向。
        var finalReadOnly = readOnly && effective == PluginRiskTier.Low;

        return new PluginToolSpec
        {
            Name = name,
            Description = description,
            Parameters = schema,
            Run = run,
            ParameterNames = paramNames,
            RequiredParameters = required,
            EnvironmentNames = envNames,
            IsReadOnly = finalReadOnly,
            DeclaredRisk = declaredRisk,
            EffectiveRisk = effective,
        };
    }

    // ------------------------------------------------------------------
    // parameters
    // ------------------------------------------------------------------

    private static bool TryParseParameters(
        JsonElement toolEl,
        string label,
        Action<string> error,
        out JsonElement schema,
        out IReadOnlyList<string> names,
        out IReadOnlyList<string> required)
    {
        var nameList = new List<string>();
        var requiredList = new List<string>();

        names = nameList;
        required = requiredList;

        if (!toolEl.TryGetProperty("parameters", out var el) || el.ValueKind == JsonValueKind.Null)
        {
            // 无参数工具：给一个空 object schema，而不是 null ——
            // 部分兼容端点对 parameters 缺失会报 400。
            schema = JsonDocument.Parse(EmptySchemaJson).RootElement.Clone();
            return true;
        }

        if (el.ValueKind != JsonValueKind.Object)
        {
            error("parameters 必须是 JSON Schema 对象");
            schema = default;
            return false;
        }

        if (el.TryGetProperty("type", out var typeEl)
            && (typeEl.ValueKind != JsonValueKind.String || typeEl.GetString() != "object"))
        {
            error("parameters.type 必须是 \"object\"");
            schema = default;
            return false;
        }

        if (!el.TryGetProperty("properties", out var propsEl) || propsEl.ValueKind != JsonValueKind.Object)
        {
            error("parameters.properties 必须是对象（无参数时写 {}）");
            schema = default;
            return false;
        }

        foreach (var p in propsEl.EnumerateObject())
        {
            if (!ParamPattern.IsMatch(p.Name))
            {
                error($"参数名 '{p.Name}' 不合法：字母或下划线开头，只含字母 / 数字 / 下划线，最长 41");
                schema = default;
                return false;
            }

            if (p.Value.ValueKind != JsonValueKind.Object)
            {
                error($"参数 '{p.Name}' 的 schema 必须是对象");
                schema = default;
                return false;
            }

            nameList.Add(p.Name);
        }

        if (el.TryGetProperty("required", out var reqEl) && reqEl.ValueKind != JsonValueKind.Null)
        {
            if (reqEl.ValueKind != JsonValueKind.Array)
            {
                error("parameters.required 必须是字符串数组");
                schema = default;
                return false;
            }

            foreach (var r in reqEl.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.String)
                {
                    error("parameters.required 里只能放字符串");
                    schema = default;
                    return false;
                }

                var rn = r.GetString()!;

                if (!nameList.Contains(rn, StringComparer.Ordinal))
                {
                    error($"required 里的 '{rn}' 没有在 properties 里声明");
                    schema = default;
                    return false;
                }

                if (!requiredList.Contains(rn, StringComparer.Ordinal))
                    requiredList.Add(rn);
            }
        }

        // ★ Clone() 不能省：JsonElement 指向 JsonDocument 的内存，
        //   而 Parse 结束时会 Dispose 那个文档。不 Clone 的话，
        //   清单"装载成功"，第一次调用工具才抛 ObjectDisposedException ——
        //   报错位置和真正的原因相隔十万八千里。
        schema = el.Clone();
        return true;
    }

    // ------------------------------------------------------------------
    // run
    // ------------------------------------------------------------------

    private static PluginRunSpec? TryParseRun(
        JsonElement el,
        string label,
        string pluginDir,
        IReadOnlyList<string> paramNames,
        Action<string> error,
        Action<string> warn)
    {
        var kindRaw = Str(el, "type")?.Trim().ToLowerInvariant();

        switch (kindRaw)
        {
            case "http":
                return ParseHttp(el, label, paramNames, error, warn);

            case "cmd":
                return ParseCmd(el, label, error);

            case "python":
                return ParsePython(el, pluginDir, label, error);

            case null:
                error("run.type 必填，取值 http / cmd / python");
                return null;

            default:
                error($"run.type '{kindRaw}' 不支持，只能是 http / cmd / python");
                return null;
        }
    }

    private static PluginRunSpec? ParseHttp(
        JsonElement el,
        string label,
        IReadOnlyList<string> paramNames,
        Action<string> error,
        Action<string> warn)
    {
        var url = Str(el, "url")?.Trim();

        if (string.IsNullOrEmpty(url))
        {
            error("http 执行面缺少 url");
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error($"url 必须是绝对的 http / https 地址，实际是 '{url}'");
            return null;
        }

        if (uri.UserInfo.Length > 0)
        {
            error("url 里不能内嵌账号密码（user:pass@host）");
            return null;
        }

        var method = (Str(el, "method") ?? "GET").Trim().ToUpperInvariant();

        if (method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE"))
        {
            error($"method 只支持 GET / HEAD / POST / PUT / PATCH / DELETE，实际是 '{method}'");
            return null;
        }

        // ---- hosts 白名单 ----
        var hosts = new List<string>();

        if (!el.TryGetProperty("hosts", out var hostsEl) || hostsEl.ValueKind != JsonValueKind.Array || hostsEl.GetArrayLength() == 0)
        {
            error("必须声明 hosts 白名单（例如 [\"api.example.com\"] 或 [\"*.example.com\"]）。" +
                  "插件能访问哪些域名必须写清楚 —— 不写就不许装");
            return null;
        }

        foreach (var h in hostsEl.EnumerateArray())
        {
            if (h.ValueKind != JsonValueKind.String)
            {
                error("hosts 里只能放字符串");
                return null;
            }

            var host = h.GetString()!.Trim().ToLowerInvariant();

            if (!HostPattern.IsMatch(host) || host.Contains(':'))
            {
                error($"hosts 里的 '{host}' 不合法：只写域名（不带协议 / 端口 / 路径），可用 *.example.com 通配");
                return null;
            }

            if (!hosts.Contains(host, StringComparer.Ordinal))
                hosts.Add(host);
        }

        if (!hosts.Any(h => HostMatches(uri.Host, h)))
        {
            error($"hosts 白名单没有覆盖 url 的域名 '{uri.Host}'；" +
                  $"当前声明：{string.Join(", ", hosts)}");
            return null;
        }

        // ---- 占位符 ----
        var placeholders = PlaceholderPattern.Matches(url)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var ph in placeholders)
        {
            if (!paramNames.Contains(ph, StringComparer.Ordinal))
            {
                error($"url 里的占位符 '{{{ph}}}' 没有在 parameters.properties 里声明");
                return null;
            }
        }

        // 去掉合法占位符后还剩花括号 → 写错了（比如 {code} 写成 {{code}}）
        var stripped = PlaceholderPattern.Replace(url, string.Empty);

        if (stripped.Contains('{') || stripped.Contains('}'))
        {
            error("url 里存在无法识别的占位符；只支持 {参数名} 这一种写法");
            return null;
        }

        // ---- headers ----
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (el.TryGetProperty("headers", out var hdrEl) && hdrEl.ValueKind != JsonValueKind.Null)
        {
            if (hdrEl.ValueKind != JsonValueKind.Object)
            {
                error("headers 必须是对象");
                return null;
            }

            foreach (var h in hdrEl.EnumerateObject())
            {
                if (h.Value.ValueKind != JsonValueKind.String)
                {
                    error($"header '{h.Name}' 的值必须是字符串");
                    return null;
                }

                if (h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    error($"不允许自定义 header '{h.Name}'（由运行时负责）");
                    return null;
                }

                headers[h.Name] = h.Value.GetString()!;
            }
        }

        if (headers.ContainsKey("Content-Type") && method is "GET" or "HEAD")
            warn("GET / HEAD 请求带 Content-Type 通常没有意义");

        var timeout = ReadTimeout(el, 30, 1, 120, error, out var ok);

        if (!ok)
            return null;

        return new PluginRunSpec
        {
            Kind = PluginRunKind.Http,
            Http = new PluginHttpSpec
            {
                Method = method,
                Url = url,
                Hosts = hosts,
                Placeholders = placeholders,
                Headers = headers,
                TimeoutSeconds = timeout,
            },
        };
    }

    private static PluginRunSpec? ParseCmd(JsonElement el, string label, Action<string> error)
    {
        var command = Str(el, "command");

        if (string.IsNullOrWhiteSpace(command))
        {
            error("cmd 执行面缺少 command");
            return null;
        }

        // ★ 这一条是注入防线的另一半（另一半在运行时注入环境变量）。
        //   如果允许 "{code}" 直接拼进命令，那么 code 里一个 "&" 就能追加一条命令 ——
        //   而参数是**模型**填的，模型完全可能被用户内容里的提示注入带偏。
        if (command.Contains('{') || command.Contains('}'))
        {
            error("command 里不能出现 { 或 }。参数请改用环境变量注入：" +
                  "cmd 写 %FATFISH_ARG_参数名%，powershell 写 $env:FATFISH_ARG_参数名 " +
                  "（参数名转大写，非字母数字换成下划线）");
            return null;
        }

        var shell = (Str(el, "shell") ?? "cmd").Trim().ToLowerInvariant();

        if (shell is not ("cmd" or "powershell"))
        {
            error($"shell 只支持 cmd / powershell，实际是 '{shell}'");
            return null;
        }

        var cwd = Str(el, "cwd")?.Trim() ?? string.Empty;

        if (Path.IsPathRooted(cwd))
        {
            error("cwd 必须是相对于工作台根的路径（绝对路径在换根后会失效，也会破坏沙箱）");
            return null;
        }

        var timeout = ReadTimeout(el, 60, 1, 600, error, out var ok);

        if (!ok)
            return null;

        return new PluginRunSpec
        {
            Kind = PluginRunKind.Cmd,
            Cmd = new PluginCmdSpec
            {
                Shell = shell,
                Command = command,
                WorkingDirectory = cwd,
                TimeoutSeconds = timeout,
            },
        };
    }

    private static PluginRunSpec? ParsePython(
        JsonElement el, string pluginDir, string label, Action<string> error)
    {
        var script = Str(el, "script")?.Trim();

        if (string.IsNullOrEmpty(script))
        {
            error("python 执行面缺少 script（相对插件目录的 .py 路径）");
            return null;
        }

        if (Path.IsPathRooted(script))
        {
            error("script 必须是相对插件目录的路径（绝对路径会让插件无法随目录一起搬走）");
            return null;
        }

        if (!script.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            error($"script 必须是 .py 文件，实际是 '{script}'");
            return null;
        }

        string scriptPath;

        try
        {
            scriptPath = Path.GetFullPath(Path.Combine(pluginDir, script));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error($"script 路径不合法：{ex.Message}");
            return null;
        }

        if (!IsUnder(pluginDir, scriptPath))
        {
            error($"script '{script}' 越出了插件目录（不允许 ../ 逃逸）");
            return null;
        }

        if (!File.Exists(scriptPath))
        {
            error($"脚本不存在：{scriptPath}");
            return null;
        }

        var cwd = Str(el, "cwd")?.Trim() ?? string.Empty;

        if (Path.IsPathRooted(cwd))
        {
            error("cwd 必须是相对于工作台根的路径");
            return null;
        }

        var timeout = ReadTimeout(el, 120, 1, 1800, error, out var ok);

        if (!ok)
            return null;

        return new PluginRunSpec
        {
            Kind = PluginRunKind.Python,
            Python = new PluginPythonSpec
            {
                ScriptPath = scriptPath,
                WorkingDirectory = cwd,
                TimeoutSeconds = timeout,
            },
        };
    }

    // ------------------------------------------------------------------
    // 小工具
    // ------------------------------------------------------------------

    private static int ReadTimeout(
        JsonElement el, int fallback, int min, int max, Action<string> error, out bool ok)
    {
        ok = true;

        if (!el.TryGetProperty("timeout_seconds", out var t) || t.ValueKind == JsonValueKind.Null)
            return fallback;

        if (t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out var v))
        {
            error("timeout_seconds 必须是整数");
            ok = false;
            return fallback;
        }

        if (v < min || v > max)
        {
            error($"timeout_seconds 必须在 {min}~{max} 之间，实际是 {v}");
            ok = false;
            return fallback;
        }

        return v;
    }

    /// <summary>host 是否命中白名单条目（支持 <c>*.example.com</c>，它不匹配裸域）。</summary>
    public static bool HostMatches(string host, string pattern)
    {
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(pattern))
            return false;

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];   // ".example.com"
            return host.Length > suffix.Length
                   && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnder(string parent, string child)
    {
        var normalized = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return child.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var el)
           && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static PluginParseOutcome Fail(string source, string message)
        => new(null, new[] { new PluginDiagnostic(PluginDiagnosticLevel.Error, source, message) });
}

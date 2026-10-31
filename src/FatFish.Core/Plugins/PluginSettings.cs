using System.Text.Json;
using System.Text.Json.Serialization;
using FatFish.Core.Config;

namespace FatFish.Core.Plugins;

/// <summary>
/// 插件在清单里声明的「归属我的配置」—— 让宿主知道这个插件的 key / 接口地址 /
/// 模型名分别写在 .env 的哪几个键上，以及去哪查可用模型。
/// </summary>
/// <remarks>
/// <b>为什么要有这一层：</b>
/// 在此之前，"生图插件读 LIQIU_API_KEY"这件事只存在于 draw.py 和 MainWindow 的字符串里。
/// 界面想把「这个插件的配置」单独拎出来显示，就只能再抄一遍键名 ——
/// 两份清单迟早分叉，而分叉后的症状是「界面上改了，插件不认」，两边都不报错。
/// 现在键名只有插件自己声明一次，宿主、界面、发现逻辑全部读这一份。
///
/// <b>为什么密钥不放进设置文件：</b>
/// 用户的决定是「key 库存 .env、插件内分割读取」。多一个存密钥的文件，
/// 就多一个会被备份 / 截图 / 打包带走的地方；而 .env 已经有遮挡显示、
/// 引号规则和体检。设置文件里只放**非密钥**的状态（启用与否、选了哪个模型、
/// 上次发现的模型列表），泄出去也不值钱。
/// </remarks>
/// <summary>
/// 一个「模型槽位」：这份清单里，某一类业务用哪个模型。
/// </summary>
/// <remarks>
/// <b>为什么一个插件需要多个槽位：</b>
/// 生图插件的文生图（<c>/images/generations</c>）和改图（<c>/images/edits</c>）
/// 是两个不同的接口，能吃它们的模型不是同一个集合 —— 把同一把模型名同时喂给两边，
/// 症状是「生成好好的，改图一直 400」，而报错里只说 model 不对，看不出是搞错了业务。
///
/// <b>为什么 tools 用工具名而不是"业务名"：</b>
/// 宿主只在真正执行某个工具的那一刻知道自己该覆盖哪个键（<see cref="PluginRunner"/> 拿到的
/// 就是工具名）。用工具名映射，等于让"哪个工具用哪个模型"这件事在清单里一眼可见，
/// 而不是藏在某段 if 里。
///
/// <b>tools 为空 = 兜底槽位</b>：没被别的槽位认领的工具都用它。
/// 单模型的插件（绝大多数）就只写一个 <c>modelEnv</c>，宿主会自动合成一个兜底槽位。
/// </remarks>
public sealed record PluginModelSlot
{
    /// <summary>槽位 id（小写字母开头，可含数字与短横线），设置文件里用它记选择。</summary>
    public required string Id { get; init; }

    /// <summary>给人看的名字，例如「文生图」「改图」。留空则显示 id。</summary>
    public string? Label { get; init; }

    /// <summary>选中的模型写回 .env 的哪个键（也是注入子进程的那个环境变量名）。</summary>
    public required string Env { get; init; }

    /// <summary>适用哪些工具；空表示"其余工具都归我"。</summary>
    public IReadOnlyList<string> Tools { get; init; } = Array.Empty<string>();

    public string Display => string.IsNullOrWhiteSpace(Label) ? Id : Label!;

    /// <summary>是不是兜底槽位（没指定工具）。</summary>
    public bool CoversAll => Tools.Count == 0;

    public override string ToString()
        => $"{Id}（{Env}" + (CoversAll ? "，兜底" : "，" + string.Join("/", Tools)) + "）";
}

/// <summary>
/// 一个「可选模型项」：一条具体的可用通道。
/// </summary>
/// <remarks>
/// <b>为什么不能只记模型名：</b>
/// 同一个模型 id 常常有多条通道 —— 两把中转 key 都卖 <c>grok-imagine-image-2.0</c>，
/// 而 xAI 官方也提供同名模型。它们的**质量、价格、限速、接口方言都不一样**
/// （官方只认 <c>aspect_ratio</c>+<c>resolution</c>，中转只认 <c>size</c>）。
/// 只存一个名字，用户就永远选不到第二条通道，而且「到底走了哪家、花了多少钱」查不出来。
///
/// 所以每一项都带上「哪把 key + 哪个接口地址」，界面上并列展示，选择时一起带回去。
/// </remarks>
public sealed record PluginModelOption
{
    /// <summary>模型 id（原样交给上游的名字，不含任何界面后缀）。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>提供它的 key 序号（1 起，对应多 key 键里的位置）。0 表示未知。</summary>
    public int KeyIndex { get; init; }

    /// <summary>这把 key 自带的接口地址；null 表示沿用插件级的 <c>baseEnv</c>。</summary>
    public string? Base { get; init; }

    /// <summary>接口地址里的主机名，只用于界面显示（例如 <c>api.x.ai</c>）。</summary>
    [JsonIgnore]
    public string Host => HostOf(Base);

    public static string HostOf(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return string.Empty;

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl!.Trim();
    }

    public override string ToString()
        => KeyIndex > 0
            ? $"{Model}（key #{KeyIndex}" + (Host.Length > 0 ? " · " + Host : "") + "）"
            : Model;
}

public sealed record PluginSettingsSpec
{
    /// <summary>归属本插件的全部 .env 键（界面按这个顺序摆字段）。</summary>
    public required IReadOnlyList<string> ConfigKeys { get; init; }

    /// <summary>多 key 的那个键，值用分隔符并列，例如 <c>LIQIU_API_KEYS</c>。</summary>
    public string? KeysEnv { get; init; }

    /// <summary>老的单 key 键，作为 <see cref="KeysEnv"/> 为空时的回退。</summary>
    public string? KeyEnv { get; init; }

    /// <summary>接口地址所在的键（发现模型时要用它拼 URL）。</summary>
    public string? BaseEnv { get; init; }

    /// <summary>老的单模型键：<see cref="ModelSlots"/> 为空时，宿主用它合成一个兜底槽位。</summary>
    public string? ModelEnv { get; init; }

    /// <summary>
    /// 这个插件的模型槽位（生图 / 改图 / …）。
    /// </summary>
    /// <remarks>
    /// 留空时由 <see cref="ModelEnv"/> 合成一个兜底槽位（单模型插件不必写这一块）。
    /// 写了之后 <see cref="ModelEnv"/> 只作为兼容字段，**不参与注入** —— 见 <see cref="PluginModelSlots"/>。
    /// 这条"二选一"很关键：两个都生效的话，同一个工具会被覆盖两次，而结果取决于遍历顺序。
    /// </remarks>
    public IReadOnlyList<PluginModelSlot> ModelSlots { get; init; } = Array.Empty<PluginModelSlot>();

    /// <summary>相对接口地址的模型列表路径，默认 <c>/models</c>。</summary>
    public string ModelsPath { get; init; } = "/models";

    /// <summary>地址键没配时的兜底值（从插件实现里抄来，不是猜的）。</summary>
    public string? DefaultBase { get; init; }

    /// <summary>没有 <see cref="BaseEnv"/> 的插件无法做模型发现。</summary>
    public bool CanDiscoverModels => BaseEnv is not null && !string.IsNullOrWhiteSpace(ModelsPath);

    /// <summary>实际生效的槽位（声明了用声明，没声明用 modelEnv 合成）。</summary>
    public IReadOnlyList<PluginModelSlot> EffectiveSlots => PluginModelSlots.Effective(this);

    public string Describe()
        => CanDiscoverModels
            ? $"配置 {ConfigKeys.Count} 项 ｜ 模型发现 {BaseEnv}{ModelsPath}"
            : $"配置 {ConfigKeys.Count} 项 ｜ 不做模型发现";
}

/// <summary>
/// 插件设置里**持久化**的那部分（不含任何密钥）。
/// </summary>
/// <remarks>
/// 字段少是刻意的：这里每多一个字段，就多一处「界面上显示的和实际生效的不一致」的
/// 可能。密钥仍在 .env，本文件只管「用户在这个插件上做过的选择」。
/// </remarks>
public sealed record PluginSettings
{
    /// <summary>是否启用。禁用＝它的工具不再注册给模型（热插拔靠这个字段）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 老的单模型选择；null 表示「沿用 .env 里的值」。
    /// </summary>
    /// <remarks>
    /// 有了 <see cref="SelectedModels"/> 之后它只剩兼容作用：解析模型时，
    /// 只有「第一个槽位」还会读它（老版本存下来的选择不至于凭空丢掉）。
    /// 保存时仍会写它，等于第一个槽位的镜像 —— 旧代码 / 旧日志读起来不至于断档。
    /// </remarks>
    public string? SelectedModel { get; init; }

    /// <summary>
    /// 槽位 id → 选中的模型名。没有键的槽位表示「留空，沿用 .env」。
    /// </summary>
    /// <remarks>
    /// 值的形态有两种：
    /// <list type="bullet">
    /// <item><c>gpt-image-2</c> —— 只有一条通道提供这个模型，不需要额外信息。</item>
    /// <item><c>grok-imagine-image-2.0@3</c> —— 同名模型有多条通道，<c>@3</c> 指定用第 3 把 key
    /// （以及它自带的接口地址）。见 <see cref="PluginModelSlots.SplitSelection"/>。</item>
    /// </list>
    /// 为什么不是"一个模型配一个开关"：槽位是插件声明的（生图 / 改图 / 以后可能还有别的），
    /// 宿主不能预知有几种，只能用字典。
    /// </remarks>
    public IReadOnlyDictionary<string, string> SelectedModels { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>上一次发现到的模型列表（离线时界面仍有东西可选）。</summary>
    public IReadOnlyList<string> KnownModels { get; init; } = Array.Empty<string>();

    /// <summary>上次发现成功的时间；null 表示从没成功过。</summary>
    public DateTimeOffset? ModelsFetchedAt { get; init; }

    /// <summary>上次发现失败的原因（一句话，给人看）。</summary>
    public string? LastDiscoveryError { get; init; }

    /// <summary>
    /// 模型 → 提供它的 key 序号（1 起，对应多 key 键里的位置）。
    /// </summary>
    /// <remarks>
    /// 为什么必须记这个：实测同一个中转站的两把 key 可见模型完全不同
    /// （一把只有 gpt-image-2，另一把只有 grok-imagine-*）。
    /// 只记"选了哪个模型"而不记"哪把 key 能给这个模型"，发请求时就会拿错钥匙，
    /// 得到 400/404 而不是图 —— 而且错误信息看起来像"模型名写错了"。
    /// 同名模型有多条通道时这里只留序号最小的那条，作为"没显式指定时"的默认。
    /// </remarks>
    public IReadOnlyDictionary<string, int> ModelKeys { get; init; }
        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 上次发现到的**全部通道**（模型 × key × 接口地址）。
    /// </summary>
    /// <remarks>
    /// <see cref="KnownModels"/> 是去重后的模型名，界面上"选哪个模型"不够用：
    /// 同名模型可能来自不同的 key / 不同的接口地址，必须各自成项。
    /// 老设置文件里没有这个字段 → 反序列化为空数组，界面退回 <see cref="KnownModels"/>，
    /// 不会因此丢配置。
    /// </remarks>
    public IReadOnlyList<PluginModelOption> ModelOptions { get; init; }
        = Array.Empty<PluginModelOption>();

    /// <summary>
    /// 某个槽位实际用哪个模型：<see cref="SelectedModels"/> 里没有就回退到
    /// <see cref="SelectedModel"/>（仅第一个槽位），再没有就返回 null（= 沿用 .env）。
    /// </summary>
    public string? ModelForSlot(PluginModelSlot slot, bool isFirstSlot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        if (SelectedModels.TryGetValue(slot.Id, out var selected)
            && !string.IsNullOrWhiteSpace(selected))
        {
            return selected.Trim();
        }

        return isFirstSlot && !string.IsNullOrWhiteSpace(SelectedModel)
            ? SelectedModel.Trim()
            : null;
    }

    public static PluginSettings Default { get; } = new();
}

/// <summary>
/// 插件设置的读写。<see cref="Load"/> 永远不抛：读坏了当作默认值，
/// 并让调用方在 <see cref="LoadResult.Error"/> 里拿到原因。
/// </summary>
/// <remarks>
/// <b>为什么不能抛：</b> 这个文件的读取发生在「打开插件栏 / 装配工具」的路径上。
/// 一个被手改坏的 json 若在那里抛出，用户看到的是「程序打不开插件」，
/// 而不是「这个文件的第 3 行少了个逗号」。
/// </remarks>
public static class PluginSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>设置文件位置：<c>&lt;根&gt;/config/plugins/&lt;插件id&gt;.json</c>。</summary>
    public static string PathFor(string root, string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        // id 在解析清单时已被限制为 [a-z][a-z0-9-]{1,40}，这里再挡一次路径分隔符：
        // 万一将来有调用方直接传用户输入，这里不能成为一条 ../ 逃逸通道。
        if (pluginId.IndexOfAny(['/', '\\', ':', '.']) >= 0)
            throw new ArgumentException($"插件 id 含非法字符：{pluginId}", nameof(pluginId));

        return Path.Combine(root, "config", "plugins", pluginId + ".json");
    }

    public static (PluginSettings Settings, string? Error) Load(string root, string pluginId)
    {
        string path;

        try
        {
            path = PathFor(root, pluginId);
        }
        catch (ArgumentException ex)
        {
            return (PluginSettings.Default, ex.Message);
        }

        if (!File.Exists(path))
            return (PluginSettings.Default, null);

        try
        {
            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<PluginSettings>(json, Json);
            return settings is null
                ? (PluginSettings.Default, "设置文件是空的 / 只有 null")
                : (settings, null);
        }
        catch (JsonException ex)
        {
            return (PluginSettings.Default, "设置文件不是合法 JSON：" + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (PluginSettings.Default, "读设置文件失败：" + ex.Message);
        }
    }

    /// <summary>写回设置。先写临时文件再替换，避免写一半断电留下半个 json。</summary>
    public static string? Save(string root, string pluginId, PluginSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            var path = PathFor(root, pluginId);
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "写设置文件失败：" + ex.Message;
        }
    }
}

/// <summary>一把插件 key（**值与来源都在这里，绝不打进日志**）。</summary>
/// <param name="Value">原始值，只用于发请求。</param>
/// <param name="Source">来自哪个键（多 key 键还是老的单 key 键）。</param>
/// <param name="Index">在列表里的序号（从 1 开始，日志里只用它）。</param>
/// <param name="Base">
/// 这把 key 自带的接口地址；null 表示跟插件级的 <c>baseEnv</c> 走。
/// </param>
public sealed record PluginKey(string Value, string Source, int Index, string? Base = null)
{
    /// <summary>给人看的掩码：前 6 后 4，中间固定三个点。</summary>
    public string Mask => PluginKeySet.Mask(Value);

    public override string ToString() => $"#{Index} {Mask}（{Source}）";
}

/// <summary>
/// 多 key 的解析与挑选。
/// </summary>
/// <remarks>
/// <b>分隔符为什么用逗号而不是换行：</b> .env 的值不能跨行（<see cref="EnvFile"/>
/// 明确不支持多行值），而 key 本身不含逗号。分号不用是因为部分 key 里出现过 '-' 以外的
/// 符号时肉眼难辨；逗号最不容易误读。
///
/// <b>key 为什么要能自带接口地址：</b> 一个插件同时接「中转站」和「官方 API」时，
/// 两家的地址和方言都不同（官方只认 <c>aspect_ratio</c>+<c>resolution</c>，中转只认 <c>size</c>）。
/// 把地址写进 key 条目（<c>xai-xxx@https://api.x.ai/v1</c>），
/// 就不用为新供应商再加一个环境变量和一套配置项 —— 一条 key 就是一条完整通道。
///
/// <b>为什么不做轮换：</b> 用户要的是「库里有多把、能选/能回退」，
/// 不是「自动轮着用」。自动轮换会让「这张图花了谁的账号」变得查不出来，
/// 而 524 这类失败常常其实已经出图 —— 换 key 重试等于重复扣费。
/// 这里只提供「按顺序挑一把能用的」，重试策略留给调用方显式决定。
/// </remarks>
public sealed class PluginKeySet
{
    public static readonly char[] Separators = [',', ';'];

    private PluginKeySet(IReadOnlyList<PluginKey> keys) => Keys = keys;

    public IReadOnlyList<PluginKey> Keys { get; }

    public bool IsEmpty => Keys.Count == 0;

    public PluginKey? First => Keys.Count > 0 ? Keys[0] : null;

    public static PluginKeySet Empty { get; } = new(Array.Empty<PluginKey>());

    /// <summary>从环境变量里凑出 key 列表：先看多 key 键，空了再看老的单 key 键。</summary>
    public static PluginKeySet FromEnvironment(PluginSettingsSpec? spec)
    {
        if (spec is null)
            return Empty;

        var multi = Read(spec.KeysEnv);

        if (!string.IsNullOrWhiteSpace(multi))
        {
            var fromList = Parse(multi, spec.KeysEnv!);

            if (!fromList.IsEmpty)
                return fromList;
        }

        var single = Read(spec.KeyEnv);

        return string.IsNullOrWhiteSpace(single)
            ? Empty
            : Parse(single, spec.KeyEnv ?? "KEY");
    }

    public static PluginKeySet Parse(string? raw, string source)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Empty;

        var list = new List<PluginKey>();

        foreach (var piece in raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 值里可能被引号包着（.env 允许），这里再剥一次；
            // 不剥的话 Authorization 头会带着引号发出去，服务端只会回 401。
            var value = piece.Trim().Trim('"', '\'').Trim();

            if (value.Length == 0)
                continue;

            // 「key@接口地址」：只有右边看起来真的是 http(s) URL 时才拆，
            // 免得某家 key 里恰好带 @ 就被误当成分隔符（key 值绝不能被动过）。
            string? keyBase = null;
            int at = value.LastIndexOf('@');

            if (at > 0 && at < value.Length - 1)
            {
                var tail = value[(at + 1)..].Trim();

                if (tail.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || tail.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    keyBase = tail.TrimEnd('/');
                    value = value[..at].Trim();
                }
            }

            if (value.Length == 0)
                continue;

            list.Add(new PluginKey(value, source, list.Count + 1, keyBase));
        }

        return list.Count == 0 ? Empty : new PluginKeySet(list);
    }

    /// <summary>
    /// 掩码：短 key 全遮，长 key 前 6 后 4。
    /// </summary>
    /// <remarks>
    /// 这条规则存在的唯一目的，是让「在日志里打一把 key」这件事**做不出来**。
    /// 排查 key 问题时真正需要的只是「是不是这一把」，而不是完整值。
    /// </remarks>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(空)";

        if (value.Length <= 12)
            return new string('*', value.Length);

        return value[..6] + "***" + value[^4..];
    }

    private static string? Read(string? envName)
        => string.IsNullOrWhiteSpace(envName)
            ? null
            : Environment.GetEnvironmentVariable(envName)?.Trim();

    /// <summary>把一枚 key 库写回 .env 的形态（逗号分隔）。界面保存时用。</summary>
    public static string Join(IEnumerable<string> values)
        => string.Join(",", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
}

/// <summary>
/// 模型槽位的解析与注入。
/// </summary>
/// <remarks>
/// <b>为什么把这段独立出来：</b>「这次执行的是哪个工具 → 该覆盖哪个环境变量 → 用哪把 key」
/// 这三步在宿主里只该有一处实现。放在这里就能被单测直接验证 ——
/// 而它的失败方式（拿错钥匙、写错键名）在真实调用里表现为 400，
/// 从错误信息完全看不出根因。
/// </remarks>
public static class PluginModelSlots
{
    /// <summary>没有声明槽位时合成的那个槽位的 id（设置文件里会看到它）。</summary>
    public const string DefaultSlotId = "default";

    /// <summary>选择值里「模型名」与「key 序号」之间的分隔符。</summary>
    public const char ChannelSeparator = '@';

    /// <summary>
    /// 实际生效的槽位：清单里声明了就用声明的；没声明但有 <c>modelEnv</c> 时，
    /// 合成一个覆盖全部工具的兜底槽位（老清单不必改也能用）。
    /// </summary>
    public static IReadOnlyList<PluginModelSlot> Effective(PluginSettingsSpec? spec)
    {
        if (spec is null)
            return Array.Empty<PluginModelSlot>();

        if (spec.ModelSlots.Count > 0)
            return spec.ModelSlots;

        return string.IsNullOrWhiteSpace(spec.ModelEnv)
            ? Array.Empty<PluginModelSlot>()
            : [new PluginModelSlot
            {
                Id = DefaultSlotId,
                Env = spec.ModelEnv!,
                Tools = Array.Empty<string>(),
            }];
    }

    /// <summary>
    /// 这个工具该用哪个槽位：先按工具名精确命中；没命中就用兜底槽位；
    /// 都没有则返回 null（= 不覆盖任何模型变量，让 .env 的值原样生效）。
    /// </summary>
    public static PluginModelSlot? For(PluginSettingsSpec? spec, string? toolName)
    {
        var slots = Effective(spec);

        if (slots.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            foreach (var slot in slots)
            {
                if (slot.Tools.Any(t => string.Equals(t, toolName, StringComparison.OrdinalIgnoreCase)))
                    return slot;
            }
        }

        return slots.FirstOrDefault(s => s.CoversAll);
    }

    /// <summary>某个槽位当前选中的模型（可能为 null＝沿用 .env）。</summary>
    public static string? ModelFor(PluginSettingsSpec? spec, PluginSettings settings, PluginModelSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(settings);

        var slots = Effective(spec);
        var isFirst = slots.Count > 0 && string.Equals(slots[0].Id, slot.Id, StringComparison.OrdinalIgnoreCase);

        return settings.ModelForSlot(slot, isFirst);
    }

    /// <summary>
    /// 拆开「模型名@key序号」这种带通道的选择值。
    /// </summary>
    /// <remarks>
    /// 只有 <c>@</c> 右边是个正整数时才认 —— 模型名里出现 <c>@</c>（例如某个版本号写法）
    /// 不该被误拆成一个不存在的 key 序号。<c>@</c> 右边不是整数就原样当模型名用。
    /// </remarks>
    public static (string Model, int? KeyIndex) SplitSelection(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
            return (string.Empty, null);

        var value = selection.Trim();
        int at = value.LastIndexOf(ChannelSeparator);

        if (at <= 0 || at >= value.Length - 1)
            return (value, null);

        return int.TryParse(value[(at + 1)..].Trim(), out var index) && index > 0
            ? (value[..at].Trim(), index)
            : (value, null);
    }

    /// <summary>把「模型 + 通道」拼成存进设置文件 / 环境变量的那个值。</summary>
    public static string JoinSelection(string model, int keyIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return keyIndex > 0 ? model.Trim() + ChannelSeparator + keyIndex : model.Trim();
    }

    /// <summary>
    /// 算出发给子进程的环境变量覆盖项：槽位的模型键，以及（必要时）提供这个模型的
    /// 那把 key 覆盖到 <c>keyEnv</c> 上、它自带的接口地址覆盖到 <c>baseEnv</c> 上。
    /// </summary>
    /// <remarks>
    /// 覆盖 <c>keyEnv</c> 而不是"另起一个变量"的原因：插件脚本读的一直是它。
    /// 不覆盖的话，选了只由 #2 提供的模型却仍拿 #1 的钥匙去发请求，得到 400/404。
    ///
    /// <b>为什么要连 baseEnv 一起换：</b> 同名模型可能一边在中转、一边在官方。
    /// 只换钥匙不换地址，等于拿官方的钥匙去打中转的地址 —— 报错会是 404，
    /// 而真正的原因（地址错了）在报错里完全看不出来。
    ///
    /// <b>返回值可能为空</b>：用户没选、或这个工具不归任何槽位时，
    /// 一个变量都不该覆盖 —— 让 .env 的原值生效才是"未选择则沿用上次"的含义。
    /// </remarks>
    public static IReadOnlyDictionary<string, string> BuildEnvOverrides(
        PluginSettingsSpec? spec,
        PluginSettings settings,
        string? toolName,
        PluginKeySet? keys = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var slot = For(spec, toolName);

        if (slot is null)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var selection = ModelFor(spec, settings, slot);

        if (string.IsNullOrWhiteSpace(selection))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var (model, explicitKey) = SplitSelection(selection);

        if (string.IsNullOrWhiteSpace(model))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [slot.Env] = model,
        };

        if (spec is null || string.IsNullOrWhiteSpace(spec.KeyEnv))
            return map;

        // 显式指定了 key 序号（用户在界面里选了「同名模型的另一条通道」）→ 以它为准；
        // 没指定就退回老的「模型 → 序号最小那把 key」映射。
        int? keyIndex = explicitKey;

        if (keyIndex is null && settings.ModelKeys.TryGetValue(model, out var mapped))
            keyIndex = mapped;

        if (keyIndex is not { } index)
            return map;

        var set = keys ?? PluginKeySet.FromEnvironment(spec);
        var chosen = set.Keys.FirstOrDefault(k => k.Index == index);

        if (chosen is null)
            return map;

        map[spec.KeyEnv!] = chosen.Value;

        if (!string.IsNullOrWhiteSpace(chosen.Base) && !string.IsNullOrWhiteSpace(spec.BaseEnv))
            map[spec.BaseEnv!] = chosen.Base!;

        return map;
    }
}

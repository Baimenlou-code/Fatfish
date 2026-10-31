namespace FatFish.Core.Config;

/// <summary>一个配置项的编辑形态。决定窗口里长什么样。</summary>
public enum EnvFieldKind
{
    /// <summary>普通单行文本。</summary>
    Text,

    /// <summary>密钥：默认遮挡显示，可点「眼睛」看明文。</summary>
    Secret,

    /// <summary>三态开关：未设置 / 开 / 关（对应空、true、false）。</summary>
    Bool,

    /// <summary>下拉选择（可配「允许手填」以容纳新模型名）。</summary>
    Choice,

    /// <summary>整数。</summary>
    Integer,

    /// <summary>小数（如温度、QPS）。</summary>
    Decimal,

    /// <summary>http(s) 地址。</summary>
    Url,
}

/// <summary>一个配置项的定义。</summary>
/// <param name="Key">.env 里的键名，必须与内核读取时用的**完全一致**。</param>
/// <param name="Label">给人看的名字。不该出现下划线，也不该是键名的拼音。</param>
/// <param name="Help">一句话说明「这是什么、改了会怎样」。会显示在输入框下方。</param>
/// <param name="Default">不填时程序实际使用的值（从代码里的默认值抄来，不是猜的）。</param>
/// <param name="Warning">这一项踩过的坑。有值时会用黄字单独提示。</param>
/// <param name="Advanced">默认折叠在「高级」里：不常改、改错代价大。</param>
public sealed record EnvField
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    /// <summary>所属分组（见 <see cref="EnvSchema.Groups"/>）。</summary>
    public required string Group { get; init; }

    public required EnvFieldKind Kind { get; init; }

    public string Help { get; init; } = "";

    public string? Default { get; init; }

    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    /// <summary>允许手填下拉框以外的值（模型名常常要试新版本）。</summary>
    public bool AllowCustomChoice { get; init; }

    public double? Min { get; init; }

    public double? Max { get; init; }

    /// <summary>留空会导致程序起不来 / 功能不可用。</summary>
    public bool Required { get; init; }

    public string? Warning { get; init; }

    /// <summary>默认折叠。</summary>
    public bool Advanced { get; init; }

    public string? Placeholder { get; init; }

    /// <summary>同一作用的旧键名（Python 版或历史版本用的）。</summary>
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
}

/// <summary>一个分组（= 界面上的一段）。</summary>
public sealed record EnvGroup(string Name, string Caption, string Summary);

public enum EnvIssueLevel
{
    /// <summary>这样配程序跑不起来 / 功能必然失败。</summary>
    Error,

    /// <summary>能跑，但很可能不是你想要的。</summary>
    Warning,

    /// <summary>纯提示：当前状态说明。</summary>
    Note,
}

/// <summary>体检结果的一条。</summary>
public sealed record EnvIssue(EnvIssueLevel Level, string Message, string? Key = null);

/// <summary>
/// .env 的「配置图谱」：有哪些键、各自什么类型、怎么校验、有哪些经典坑。
///
/// <b>为什么要把这层单独抽出来（而不是直接在 XAML 里摆控件）：</b>
///   1. 键名必须与内核读取处一致。散落在 XAML 里的话，改内核键名时
///      没人会想起去改界面，症状是「界面上填了，程序不认」；
///   2. 校验规则要能**单测**。窗口里的红色小字没法断言，
///      而「温度填 200」这类输入必须在写进文件前拦下；
///   3. 体检逻辑要能被别处复用（比如启动自检以后想加一条）。
///
/// <b>键名与默认值的来源</b>：<see cref="Llm.LlmOptions"/>、<see cref="Safety.VerifyOptions"/>、
/// <see cref="Net.NetOptions"/> 的 <c>FromEnvironment()</c>。改那边的默认值时要回来同步 ——
/// 这里写错的表现是「界面上显示默认 8192，实际程序用 131072」，比不显示更坏。
/// </summary>
public static class EnvSchema
{
    public const string GroupLlm = "llm";
    public const string GroupVerify = "verify";
    public const string GroupNet = "net";
    public const string GroupImage = "image";
    public const string GroupOther = "other";

    /// <summary>分组顺序 = 界面上的显示顺序。按「改这一项的概率」从高到低排。</summary>
    public static readonly IReadOnlyList<EnvGroup> Groups =
    [
        new(GroupLlm, "对话模型", "程序用哪个模型说话、花哪个账号的钱。改这里必须重启才生效。"),
        new(GroupVerify, "双人核验", "审查员 AI 的配置。默认与主模型同源；换成别家模型才是真正的「异源核验」。"),
        new(GroupNet, "联网搜索", "搜索 / 抓网页用哪个后端。留空＝只走单后端（行为与旧版一致）。"),
        new(GroupImage, "AI 生图", "image_generate 插件（gpt-image-2）的连接配置。插件读的就是这一组键。"),
        new(GroupOther, "其他 / 自定义", "不认识的键也会列在这里，可以照常改；新增自定义键请用底部按钮。"),
    ];

    public static readonly IReadOnlyList<EnvField> Fields =
    [
        // ---------------- 对话模型 ----------------
        new EnvField
        {
            Key = "FATFISH_API_KEY",
            Label = "主模型 API Key",
            Group = GroupLlm,
            Kind = EnvFieldKind.Secret,
            Required = true,
            Placeholder = "sk-…",
            Help = "程序的钥匙。没有它连一轮对话都发不出去。",
            Warning = "改这一项等于换账号：余额、账单、限速都会跟着换。",
        },
        new EnvField
        {
            Key = "DEEPSEEK_API_KEY",
            Label = "旧写法 API Key（兼容）",
            Group = GroupLlm,
            Kind = EnvFieldKind.Secret,
            Advanced = true,
            Help = "历史遗留键。两个都填时 FATFISH_API_KEY 优先，此键只在它为空时才生效。",
            Warning = "两个键若填了不同的值，实际花的是 FATFISH_API_KEY 的账号，" +
                      "而余额页可能显示的是另一个 —— 这是本仓库真实踩过的坑。",
            Aliases = ["FATFISH_API_KEY"],
        },
        new EnvField
        {
            Key = "FATFISH_BASE_URL",
            Label = "接口地址",
            Group = GroupLlm,
            Kind = EnvFieldKind.Url,
            Default = "https://api.deepseek.com",
            Placeholder = "https://api.deepseek.com",
            Help = "兼容 OpenAI 协议的地址。可以只写到域名，也可以写完整 /chat/completions。",
        },
        new EnvField
        {
            Key = "FATFISH_MODEL",
            Label = "模型名",
            Group = GroupLlm,
            Kind = EnvFieldKind.Choice,
            Default = "deepseek-flash",
            AllowCustomChoice = true,
            Choices = ["deepseek-flash", "deepseek-v4-pro", "deepseek-v4-flash",
                       "deepseek-v3.2", "deepseek-v3.1", "deepseek-r1",
                       "deepseek-chat", "deepseek-coder"],
            Help = "下拉里是常见项，也可以直接手填。以 /models 接口实际返回为准。",
        },
        new EnvField
        {
            Key = "FATFISH_TEMPERATURE",
            Label = "温度",
            Group = GroupLlm,
            Kind = EnvFieldKind.Decimal,
            Default = "0.7",
            Min = 0,
            Max = 2,
            Help = "越低越稳定、越高越发散。写代码建议 0.3～0.7。",
        },
        new EnvField
        {
            Key = "FATFISH_MAX_TOKENS",
            Label = "单轮回复上限（token）",
            Group = GroupLlm,
            Kind = EnvFieldKind.Integer,
            Default = "131072",
            Min = 256,
            Max = 1048576,
            Help = "调小会从中间截断长回答，而截断后的内容看起来像是完整的 —— 改之前先想清楚。",
            Aliases = ["MAX_REPLY_TOKENS", "DEEPSEEK_MAX_TOKENS"],
        },
        new EnvField
        {
            Key = "FATFISH_TIMEOUT",
            Label = "单轮超时（秒）",
            Group = GroupLlm,
            Kind = EnvFieldKind.Integer,
            Default = "300",
            Min = 10,
            Max = 3600,
            Advanced = true,
            Help = "流式输出期间按「收到数据的间隔」重新计时，所以不会把长回答误杀。",
        },

        // ---------------- 双人核验 ----------------
        new EnvField
        {
            Key = "VERIFY_MODE",
            Label = "核验范围",
            Group = GroupVerify,
            Kind = EnvFieldKind.Choice,
            Default = "auto",
            Choices = ["auto", "all", "off"],
            Help = "auto＝只有写文件/跑命令这类有副作用的动作才送审；all＝连只读也送审（token 翻倍）；off＝关闭审查员。",
        },
        new EnvField
        {
            Key = "VERIFIER_MODEL",
            Label = "审查员模型",
            Group = GroupVerify,
            Kind = EnvFieldKind.Choice,
            AllowCustomChoice = true,
            Choices = ["deepseek-v4-pro", "deepseek-flash", "deepseek-v3.2", "deepseek-r1"],
            Help = "留空＝沿用主模型。填一个**别家**模型才算异源核验：同一个模型审自己等于自己签字。",
            Warning = "填了别家模型但没填 VERIFIER_BASE_URL，请求会发到主模型的地址上去（然后 404）。",
        },
        new EnvField
        {
            Key = "VERIFIER_BASE_URL",
            Label = "审查员接口地址",
            Group = GroupVerify,
            Kind = EnvFieldKind.Url,
            Help = "留空＝沿用主模型的地址。",
        },
        new EnvField
        {
            Key = "VERIFIER_API_KEY",
            Label = "审查员 API Key",
            Group = GroupVerify,
            Kind = EnvFieldKind.Secret,
            Help = "留空＝沿用主模型的 key。审查员每轮都调用一次，用独立 key 才能单独看到它的消耗。",
        },
        new EnvField
        {
            Key = "VERIFIER_TEMPERATURE",
            Label = "审查员温度",
            Group = GroupVerify,
            Kind = EnvFieldKind.Decimal,
            Default = "0.2",
            Min = 0,
            Max = 2,
            Advanced = true,
            Help = "审核要比创作更保守，默认就给得低。",
        },
        new EnvField
        {
            Key = "VERIFIER_MAX_TOKENS",
            Label = "审查员回复上限",
            Group = GroupVerify,
            Kind = EnvFieldKind.Integer,
            Default = "2000",
            Min = 256,
            Advanced = true,
            Help = "裁决是短 JSON，2000 足够。调大会让每次核验更贵。",
        },
        new EnvField
        {
            Key = "VERIFY_STRICT",
            Label = "裁决不可解析时拦截",
            Group = GroupVerify,
            Kind = EnvFieldKind.Bool,
            Default = "true",
            Help = "开＝审查员答非所问就当作「不放行」（安全）；关＝放行（宽松）。",
        },
        new EnvField
        {
            Key = "VERIFY_MAX_RETRIES",
            Label = "驳回后重试次数",
            Group = GroupVerify,
            Kind = EnvFieldKind.Integer,
            Default = "2",
            Min = 0,
            Max = 10,
            Advanced = true,
            Help = "被驳回几次后放弃本轮。0＝第一次被驳回就停。",
        },
        new EnvField
        {
            Key = "VERIFY_MAX_SUPPLEMENTS",
            Label = "免费补充说明次数",
            Group = GroupVerify,
            Kind = EnvFieldKind.Integer,
            Default = "4",
            Min = 0,
            Max = 20,
            Advanced = true,
            Help = "审查员要求「补充说明」时不消耗重试额度，但有次数上限，防止无限循环。",
        },
        new EnvField
        {
            Key = "VERIFY_FINAL_ANSWER",
            Label = "最终回答也送审",
            Group = GroupVerify,
            Kind = EnvFieldKind.Bool,
            Default = "false",
            Advanced = true,
            Help = "开＝每条回答发给你之前先让审查员读一遍。更稳，但每轮多一次调用。",
        },
        new EnvField
        {
            Key = "VERIFY_FAIL_MODE",
            Label = "审查员不可用时",
            Group = GroupVerify,
            Kind = EnvFieldKind.Choice,
            Default = "open",
            Choices = ["open", "closed"],
            Advanced = true,
            Help = "open＝照常执行（不因审查员宕机卡住你）；closed＝一律拦下（更安全，但网络抖一下就干不了活）。",
        },

        // ---------------- 联网搜索 ----------------
        new EnvField
        {
            Key = "TAVILY_API_KEY",
            Label = "Tavily API Key",
            Group = GroupNet,
            Kind = EnvFieldKind.Secret,
            Help = "第二个搜索后端。填了之后 NET_BACKENDS 才可能凑出「多后端」。",
        },
        new EnvField
        {
            Key = "NET_BACKENDS",
            Label = "启用哪些后端",
            Group = GroupNet,
            Kind = EnvFieldKind.Text,
            Placeholder = "留空 / auto / deepseek,tavily",
            Help = "留空＝单后端（旧行为）；auto＝所有配了 key 的都上；也可写死列表。",
            Warning = "默认留空是故意的：老部署只配了 TAVILY_API_KEY 时，" +
                      "写 auto 会悄悄开始走官方按次计费的接口 —— 账单变了却找不到原因。",
        },
        new EnvField
        {
            Key = "NET_BACKEND_ROUTER",
            Label = "路由策略",
            Group = GroupNet,
            Kind = EnvFieldKind.Choice,
            Default = "rule",
            Choices = ["rule", "off"],
            Advanced = true,
            Help = "rule＝按问题语言/类型挑后端；off＝每次都问全部后端（更慢更贵）。",
        },
        new EnvField
        {
            Key = "NET_MERGE",
            Label = "结果融合",
            Group = GroupNet,
            Kind = EnvFieldKind.Choice,
            Default = "rrf",
            Choices = ["rrf", "score", "none"],
            Advanced = true,
            Help = "rrf＝倒数排名融合（两个后端都命中的结果优先）；score＝按分数加权；none＝不融合。",
        },
        new EnvField
        {
            Key = "FATFISH_NET_QPS",
            Label = "官方接口限速（QPS）",
            Group = GroupNet,
            Kind = EnvFieldKind.Decimal,
            Default = "5",
            Min = 0.1,
            Max = 100,
            Advanced = true,
            Help = "官方限制 Search / Fetch 各 5 QPS（账号级）。调高只会换来 429。",
        },

        // ---------------- AI 生图 ----------------
        new EnvField
        {
            Key = "LIQIU_API_KEYS",
            Label = "生图 Key 池（多把）",
            Group = GroupImage,
            Kind = EnvFieldKind.Secret,
            Placeholder = "sk-aaa,sk-bbb",
            Help = "多把 key 用英文逗号分隔，**第一个是默认**。锁、额度分摊、不同账号能看到的模型不一样时用这个。",
            Warning = "两把 key 的可见模型可能完全不重叠（实测：一把只有 gpt-image-2，另一把只有 grok-imagine-*）。" +
                      "选了某个模型后，程序会自动改用提供它的那把 key。",
        },
        new EnvField
        {
            Key = "LIQIU_EDIT_MODEL",
            Label = "改图模型（image_edit）",
            Group = GroupImage,
            Kind = EnvFieldKind.Choice,
            AllowCustomChoice = true,
            Choices = ["gpt-image-2"],
            Help = "以图生图（/images/edits）用的模型，和上面的「生图模型」是**两个槽位**。" +
                   "留空＝沿用生图模型。",
            Warning = "不是所有模型都支持 /images/edits：填错了会在改图时报 400，出图却一切正常。",
        },

        new EnvField
        {
            Key = "LIQIU_API_KEY",
            Label = "生图 API Key",
            Group = GroupImage,
            Kind = EnvFieldKind.Secret,
            Placeholder = "填入 onliqiu 的 key",
            Help = "image_generate 插件用它调 /v1/images/generations。不填插件会直接报「未配置 key」。",
            Warning = "这张图和聊天用的是两个账号：生图的费用不体现在 DeepSeek 余额里。",
        },
        new EnvField
        {
            Key = "LIQIU_BASE_URL",
            Label = "生图接口地址",
            Group = GroupImage,
            Kind = EnvFieldKind.Url,
            Default = "https://onliqiu.com/v1",
            Placeholder = "https://onliqiu.com/v1",
            Help = "写到最后一段 /v1 即可，插件会自己拼 /images/generations。",
        },
        new EnvField
        {
            Key = "LIQIU_IMAGE_MODEL",
            Label = "生图模型",
            Group = GroupImage,
            Kind = EnvFieldKind.Choice,
            Default = "gpt-image-2 default",
            AllowCustomChoice = true,
            Choices = ["gpt-image-2 default"],
            Help = "接口文档里的 model 值。注意这个名字**带空格**，别手动去掉。",
        },
        new EnvField
        {
            Key = "LIQIU_IMAGE_SIZE",
            Label = "默认尺寸",
            Group = GroupImage,
            Kind = EnvFieldKind.Choice,
            Default = "1024x1024",
            Choices = ["auto", "1024x1024", "1536x1024", "1024x1536", "4096x4096"],
            Help = "模型调用时没指定尺寸才用这个。4096x4096 是 4K，单张和耗时都明显更大。",
        },
        new EnvField
        {
            Key = "LIQIU_IMAGE_QUALITY",
            Label = "默认质量",
            Group = GroupImage,
            Kind = EnvFieldKind.Choice,
            Default = "auto",
            Choices = ["auto", "low", "medium", "high"],
            Help = "质量越高越贵越慢。草稿用 low，成图用 high。",
        },
        new EnvField
        {
            Key = "LIQIU_IMAGE_DIR",
            Label = "图片保存目录",
            Group = GroupImage,
            Kind = EnvFieldKind.Text,
            Default = "art",
            Help = "相对工作台根目录。插件会自动创建；不允许写到工作台外面。",
        },
    ];

    public static EnvField? Find(string key)
        => Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>某个分组下的字段（保持声明顺序）。</summary>
    public static IEnumerable<EnvField> InGroup(string group)
        => Fields.Where(f => f.Group == group);

    /// <summary>
    /// 校验一个值。返回 null 表示通过，否则返回给人看的错误说明。
    /// </summary>
    /// <remarks>
    /// 用 if 链而不是 switch-when：C# 的模式变量作用域只到那个 case 分支，
    /// 写成 <c>case X when int.TryParse(v, out var i)</c> 之后，
    /// 后面的 <c>case X when i &lt; min</c> 根本看不到 <c>i</c>（编译期就是 CS0103）。
    /// 这类错误编译器会挡，但写的时候很容易「以为能过」。
    /// </remarks>
    public static string? Validate(EnvField field, string value)
    {
        var v = (value ?? string.Empty).Trim();

        if (v.Length == 0)
            return field.Required ? $"{field.Label}不能为空" : null;

        if (field.Kind == EnvFieldKind.Integer)
        {
            if (!int.TryParse(v, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var i))
                return "要填整数";

            if (field.Min is not null && i < field.Min)
                return $"不能小于 {field.Min:0}";

            if (field.Max is not null && i > field.Max)
                return $"不能大于 {field.Max:0}";

            return null;
        }

        if (field.Kind == EnvFieldKind.Decimal)
        {
            // 小数点固定用 . ：.env 是给程序读的，不该跟着系统区域设置变成逗号
            if (!double.TryParse(v, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
                return "要填数字（小数点用 .）";

            if (field.Min is not null && d < field.Min)
                return $"不能小于 {field.Min:0.##}";

            if (field.Max is not null && d > field.Max)
                return $"不能大于 {field.Max:0.##}";

            return null;
        }

        if (field.Kind == EnvFieldKind.Url)
        {
            if (!Uri.TryCreate(v, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return "要填 http:// 或 https:// 开头的完整地址";

            return null;
        }

        if (field.Kind == EnvFieldKind.Bool && !IsBoolText(v))
            return "只能是 true / false（或留空）";

        if (field.Kind == EnvFieldKind.Choice &&
            !field.AllowCustomChoice &&
            !field.Choices.Contains(v))
            return "只能从下拉里选：" + string.Join(" / ", field.Choices);

        return null;
    }

    private static bool IsBoolText(string v)
        => v is "1" or "0" ||
           v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
           v.Equals("false", StringComparison.OrdinalIgnoreCase) ||
           v.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
           v.Equals("no", StringComparison.OrdinalIgnoreCase) ||
           v.Equals("on", StringComparison.OrdinalIgnoreCase) ||
           v.Equals("off", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 配置体检：在保存前把「能跑但会出问题」的组合找出来。
/// </summary>
/// <remarks>
/// 这里只做**跨键**的检查（单键校验在 <see cref="EnvSchema.Validate"/>）。
/// 跨键问题才是真正难查的那类：每一个键单看都合法，
/// 组合起来才表现为「余额对不上」「审查员 404」「生图一直失败」。
/// 这些条目全部来自实际踩过的坑，不是凭空想的检查项。
/// </remarks>
public static class EnvHealth
{
    public static IReadOnlyList<EnvIssue> Inspect(EnvDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var issues = new List<EnvIssue>();
        var map = doc.Snapshot();

        string? Get(string key) => map.TryGetValue(key, out var v) ? v : null;

        // ---- 1. 重复键：第一个生效，后面的「改了也没用」 ----
        foreach (var key in doc.DuplicateKeys)
            issues.Add(new EnvIssue(EnvIssueLevel.Warning, "这个键在文件里出现了多次，只有第一次生效", key));

        // ---- 2. API Key ----
        var fatfish = Get("FATFISH_API_KEY")?.Trim();
        var deepseek = Get("DEEPSEEK_API_KEY")?.Trim();

        if (string.IsNullOrEmpty(fatfish) && string.IsNullOrEmpty(deepseek))
        {
            issues.Add(new EnvIssue(EnvIssueLevel.Error,
                "一个 API Key 都没有，程序启动后会停在「未找到 API Key」", "FATFISH_API_KEY"));
        }
        else if (!string.IsNullOrEmpty(fatfish) && !string.IsNullOrEmpty(deepseek) && fatfish != deepseek)
        {
            issues.Add(new EnvIssue(EnvIssueLevel.Warning,
                "FATFISH_API_KEY 与 DEEPSEEK_API_KEY 的值不同：实际生效的是前者，" +
                "余额页/账单可能显示的是另一个账号", "FATFISH_API_KEY"));
        }

        // ---- 3. 单键校验 ----
        foreach (var field in EnvSchema.Fields)
        {
            var raw = Get(field.Key);

            if (raw is null)
            {
                if (field.Required)
                    issues.Add(new EnvIssue(EnvIssueLevel.Error, $"{field.Label}未填写", field.Key));

                continue;
            }

            var err = EnvSchema.Validate(field, raw);

            if (err is not null)
                issues.Add(new EnvIssue(EnvIssueLevel.Error, $"{field.Label}：{err}", field.Key));
        }

        // ---- 4. 审查员：换了模型却没换地址 ----
        var verifierModel = Get("VERIFIER_MODEL")?.Trim();
        var verifierBase = Get("VERIFIER_BASE_URL")?.Trim();

        if (!string.IsNullOrEmpty(verifierModel) && string.IsNullOrEmpty(verifierBase))
        {
            issues.Add(new EnvIssue(EnvIssueLevel.Warning,
                "指定了审查员模型但没指定审查员地址：请求会发到主模型的地址上，" +
                "多数情况下得到 404 / 400", "VERIFIER_BASE_URL"));
        }

        // ---- 5. 全量送审 + 同源审查员 = 双倍开销 ----
        var mode = Get("VERIFY_MODE")?.Trim().ToLowerInvariant();

        if (mode == "all" && string.IsNullOrEmpty(verifierModel))
        {
            issues.Add(new EnvIssue(EnvIssueLevel.Warning,
                "核验范围是 all，但审查员仍是主模型：每轮对话会多花一次同等的 token", "VERIFY_MODE"));
        }

        // ---- 6. 多后端却没第二个 key ----
        var backends = Get("NET_BACKENDS")?.Trim();

        if (!string.IsNullOrEmpty(backends) &&
            backends.Contains("tavily", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(Get("TAVILY_API_KEY")))
        {
            issues.Add(new EnvIssue(EnvIssueLevel.Warning,
                "指定了 tavily 后端但没填 TAVILY_API_KEY，这个后端会被跳过", "TAVILY_API_KEY"));
        }

        // ---- 7. 生图插件 ----
        if (string.IsNullOrWhiteSpace(Get("LIQIU_API_KEY")))
        {
            issues.Add(new EnvIssue(EnvIssueLevel.Note,
                "生图 API Key 未配置：image_generate 插件会在调用时提示「缺少 LIQIU_API_KEY」", "LIQIU_API_KEY"));
        }

        // ---- 8. 认不出来的键 ----
        var unknown = doc.Keys
            .Where(k => EnvSchema.Find(k) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unknown.Count > 0)
        {
            issues.Add(new EnvIssue(EnvIssueLevel.Note,
                "有 " + unknown.Count + " 个键不在已知清单里（会原样保留，可能属于插件或旧版）：" +
                string.Join(", ", unknown.Take(8)) + (unknown.Count > 8 ? " …" : "")));
        }

        // ---- 9. 写坏的行 ----
        var bad = doc.Lines.Count(l => l.Kind == EnvLineKind.Invalid);

        if (bad > 0)
        {
            issues.Add(new EnvIssue(EnvIssueLevel.Warning,
                "有 " + bad + " 行认不出来（不是注释也不是 KEY=VALUE），保存时会原样保留，但程序不会读它们"));
        }

        return issues;
    }

    public static int Count(IReadOnlyList<EnvIssue> issues, EnvIssueLevel level)
        => issues.Count(i => i.Level == level);
}

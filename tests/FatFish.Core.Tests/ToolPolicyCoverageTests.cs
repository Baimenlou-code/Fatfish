using FatFish.Core.Safety;
using FatFish.Core.Tools;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 审批表的**覆盖率**检查 —— 用反射枚举全部工具，而不是手工列清单。
///
/// 为什么必须有这一组：
/// 手写清单的测试会跟着实现一起漏。加一个工具时，「顺便把它加进审批表」
/// 这一步没有任何编译期或运行期提示 —— 漏了不报错、不崩、不写日志，
/// 只会静默地少弹一个窗。这正是 docs/Tools.md 第三节记录的「缺陷一」，
/// 也是这次新增 <c>ws_bg_cmd</c> 时差点重演的那一幕。
///
/// 所以这里不列名字，而是问程序集「你有哪些工具」，再逐个核对策略。
/// 以后再加工具，这些测试会自动覆盖到它；漏登记就红。
/// </summary>
public class ToolPolicyCoverageTests
{
    private readonly PathGuard _guard = new(Path.GetTempPath());

    /// <summary>反射找出全部工具实例（要求有无参构造 —— 下面有一条测试专门保证这点）。</summary>
    private static List<ITool> AllTools()
    {
        var list = new List<ITool>();

        foreach (var type in typeof(ITool).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(ITool).IsAssignableFrom(type))
                continue;

            var ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor is null)
                continue;

            list.Add((ITool)ctor.Invoke(null));
        }

        return list;
    }

    /// <summary>
    /// 反射方案的前提：每个工具都能无参构造。
    /// 这条挂了，别的覆盖率测试就变成了「只覆盖了一部分」，而它们不会告诉你漏了谁。
    /// </summary>
    /// <summary>
    /// 允许「没有无参构造」的例外清单 —— 目前只有动态工具。
    /// </summary>
    /// <remarks>
    /// ★ 为什么不改成「有参构造就跳过」：那等于给所有工具开了一个后门 ——
    ///   任何忘记写无参构造的新工具都会悄悄逃出下面全部覆盖率检查，
    ///   而它们失败时的表现是「测试是绿的，但漏了一批工具」。
    ///   显式列出例外，新增一个动态工具就必须回来改这一行，改的时候会被迫想一遍
    ///   「它需不需要单独的风险校验」。
    /// </remarks>
    private static readonly string[] DynamicToolTypes = ["FatFish.Core.Plugins.PluginTool"];

    [Fact]
    public void EveryTool_HasParameterlessConstructor()
    {
        var missing = typeof(ITool).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ITool).IsAssignableFrom(t))
            .Where(t => !DynamicToolTypes.Contains(t.FullName, StringComparer.Ordinal))
            .Where(t => t.GetConstructor(Type.EmptyTypes) is null)
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// 白名单里的类型必须真实存在。
    ///
    /// 这条守的是「类改名之后白名单静默失效」：<see cref="DynamicToolTypes"/> 里
    /// 写的是一个字符串，改类名不会有任何编译错误，而例外规则会从此不再生效 ——
    /// 于是那条 <see cref="EveryTool_HasParameterlessConstructor"/> 又开始变红了，
    /// 但红的原因是「白名单没命中」，而不是「真的有工具缺构造」。
    /// </summary>
    [Fact]
    public void DynamicToolAllowList_RefersToRealTypes()
    {
        Assert.All(DynamicToolTypes, name =>
            Assert.NotNull(typeof(ITool).Assembly.GetType(name, throwOnError: false)));
    }

    [Fact]
    public void AllTools_AreDiscovered()
    {
        var tools = AllTools();

        Assert.True(tools.Count >= 22, $"应当至少有 22 个工具，实际发现 {tools.Count} 个");

        // ★ 2026-10-29：加入联网工具后，这条从 Assert.StartsWith("ws_") 放宽成前缀白名单。
        //   原写法的本意是「防止工具名拼错 / 乱起名」，而不是「所有工具都必须属于工作台」。
        //   联网工具叫 ws_web_search 会让人误以为它操作工作台里的东西，
        //   所以更准确的约束是「名字必须以所属领域开头」。
        Assert.All(tools, t => Assert.Matches(@"^(ws|web|net)_[a-z0-9_]+$", t.Name));
    }

    /// <summary>
    /// 联网工具**必须**是只读的。
    /// </summary>
    /// <remarks>
    /// 这条不是形式主义，它锁住了一个明确的决策：
    /// 搜索 / 抓取 / 查余额都不改本地状态，因此不报批（每次搜索都弹窗会把用户
    /// 训练成不看内容就点「允许」）。如果将来有人给 web_fetch 加上「顺便存到本地」，
    /// 这条会立刻变红，逼着他去想「这该不该报批」—— 而不是悄悄多出一个免审的写操作。
    /// </remarks>
    [Fact]
    public void NetTools_AreReadOnly()
    {
        var netTools = AllTools().Where(t => t.Name.StartsWith("web_", StringComparison.Ordinal)
                                             || t.Name.StartsWith("net_", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(netTools);
        Assert.All(netTools, t => Assert.True(t.IsReadOnly, $"{t.Name} 应当是只读的"));
        Assert.All(netTools, t => Assert.False(t.HasSideEffects, $"{t.Name} 声称有副作用"));
    }

    /// <summary>
    /// ★ 核心规则：**有副作用 ⇒ 必须被归类**。
    ///
    /// 一个工具要么登记在 <c>ApprovalRequired</c>（写类，要问人），
    /// 要么登记在 <c>HighRiskTools</c>（能越界执行，要问人且不许一键放行），
    /// 要么登记在 <c>LowRiskTools</c>（低副可逆，只问审查员）。
    /// 三张表都不在，就是敞开的门。
    /// </summary>
    /// <remarks>
    /// 2026-10-29 加第三档时，这条测试的**形状**没变，只是多了一个合法去处 ——
    /// 这正是它存在的意义：新增分类必须显式改这里，而不是靠某个工具类自己声明的
    /// HasSideEffects=false 悄悄绕过。
    /// </remarks>
    [Fact]
    public void EverySideEffectTool_RequiresApproval()
    {
        var policy = new ApprovalPolicy(_guard);

        var unguarded = AllTools()
            .Where(t => t.HasSideEffects)
            .Where(t => !policy.ApprovalRequired.Contains(t.Name)
                        && !policy.HighRiskTools.Contains(t.Name)
                        && !policy.LowRiskTools.Contains(t.Name))
            .Select(t => t.Name)
            .ToList();

        Assert.True(unguarded.Count == 0,
            "以下工具声称有副作用却没有被归入任何一档：" + string.Join(", ", unguarded));
    }

    /// <summary>
    /// 只读工具不该出现在两张审批表里 —— 反过来错了也是问题：
    /// 给只读工具加审批，只会训练用户在没看清时点「允许」。
    /// </summary>
    [Fact]
    public void ReadOnlyTools_AreNotRegisteredForApproval()
    {
        var policy = new ApprovalPolicy(_guard);

        var wrong = AllTools()
            .Where(t => t.IsReadOnly && !t.HasSideEffects)
            .Where(t => policy.ApprovalRequired.Contains(t.Name)
                        || policy.HighRiskTools.Contains(t.Name)
                        || policy.LowRiskTools.Contains(t.Name))
            .Select(t => t.Name)
            .ToList();

        Assert.True(wrong.Count == 0,
            "以下只读工具被登记成需要审批：" + string.Join(", ", wrong));
    }

    /// <summary>
    /// 「能执行任意命令」的工具永远不能被一键放行 ——
    /// 包括将来新增的：只要进了 HighRiskTools，就必须同时在 NeverAutoApprove 里。
    /// </summary>
    [Fact]
    public void EveryHighRiskTool_ForbidsOneKeyApproval()
    {
        var policy = new ApprovalPolicy(_guard);

        var missing = policy.HighRiskTools
            .Where(name => !policy.NeverAutoApprove.Contains(name))
            .ToList();

        Assert.True(missing.Count == 0,
            "以下高危工具允许一键放行：" + string.Join(", ", missing));
    }

    /// <summary>
    /// ★ 有副作用的工具必须同时进入**两道闸**。
    ///
    /// 上面那条守的是人工报批；这条守的是 AI 核验。
    /// 一个工具有副作用、也会报批，但如果它不在核验范围里，
    /// 就变成「只问用户、不问审查员」—— 而用户的注意力是有限资源：
    /// 弹窗多了他会不看内容就点「允许」，那道闸就名存实亡了。
    /// </summary>
    [Fact]
    public void EverySideEffectTool_IsVerified()
    {
        var options = new VerifyOptions
        {
            Mode = VerifyMode.Auto,
            Endpoint = new VerifierEndpoint { ApiKey = "test" },
        };

        var unverified = AllTools()
            .Where(t => t.HasSideEffects && !options.ShouldVerify(t.Name, t.HasSideEffects))
            .Select(t => t.Name)
            .ToList();

        Assert.True(unverified.Count == 0,
            "以下工具有副作用却不进 AI 核验范围：" + string.Join(", ", unverified));
    }

    /// <summary>
    /// auto 模式下只读工具不送审 —— 这是刻意的省 token 决定，不是遗漏。
    ///
    /// 若哪天有人把 <see cref="VerifyMode.Auto"/> 的语义改成「全都审」，
    /// 这条会变红，逼着他去 README 里把「成本」那条也一起改掉。
    /// </summary>
    [Fact]
    public void ReadOnlyTools_AreNotVerifiedInAutoMode()
    {
        var options = new VerifyOptions
        {
            Mode = VerifyMode.Auto,
            Endpoint = new VerifierEndpoint { ApiKey = "test" },
        };

        var wrong = AllTools()
            .Where(t => t.IsReadOnly && !t.HasSideEffects)
            .Where(t => options.ShouldVerify(t.Name, t.HasSideEffects))
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(wrong);
    }

    /// <summary>关掉核验时，任何工具都不该再送审（哪怕它声明了副作用）。</summary>
    [Fact]
    public void VerifyMode_Off_DisablesEverything()
    {
        var options = new VerifyOptions
        {
            Mode = VerifyMode.Off,
            Endpoint = new VerifierEndpoint { ApiKey = "test" },
        };

        Assert.All(AllTools(), t => Assert.False(options.ShouldVerify(t.Name, t.HasSideEffects)));
    }

    /// <summary>
    /// 三张表里的每个名字都必须对应真实存在的工具。
    ///
    /// 这条抓的是「错字」：把 <c>ws_bg_python</c> 写成 <c>ws_bg_pyhton</c>，
    /// 编译通过、运行时毫无反应、工具静默免审 —— 只差一个字母，没有任何提示。
    /// </summary>
    [Fact]
    public void PolicyNames_AllReferToRealTools()
    {
        var policy = new ApprovalPolicy(_guard);
        var real = AllTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        var declared = policy.ApprovalRequired
            .Concat(policy.HighRiskTools)
            .Concat(policy.LowRiskTools)
            .Concat(policy.NeverAutoApprove)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var typos = declared.Where(n => !real.Contains(n)).ToList();

        Assert.True(typos.Count == 0,
            "以下名字在审批表里但没有对应工具（多半是拼错）：" + string.Join(", ", typos));
    }

    /// <summary>
    /// 三个风险档位必须互斥。
    /// </summary>
    /// <remarks>
    /// 若一个工具同时出现在「要报批」和「低风险免报批」里，
    /// 行为取决于判定顺序 —— 而顺序是**实现细节**，用户看到的是
    /// 「同一个操作，有时弹窗有时不弹」。这条测试把「到底归哪档」变成必须明确的选择。
    /// </remarks>
    [Fact]
    public void RiskTiers_AreMutuallyExclusive()
    {
        var policy = new ApprovalPolicy(_guard);

        var overlaps = policy.LowRiskTools
            .Where(n => policy.ApprovalRequired.Contains(n) || policy.HighRiskTools.Contains(n))
            .ToList();

        Assert.True(overlaps.Count == 0,
            "以下工具同时被归入低风险与高/中风险档：" + string.Join(", ", overlaps));
    }

    /// <summary>
    /// 低风险档的工具**必须**仍然进 AI 核验。
    ///
    /// 这一条守的正是「不打断用户 ≠ 没人看着」：ws_mkdir / ws_cd 不弹窗，
    /// 所以审查员是它们唯一的第二双眼睛。如果哪天有人把它们从核验范围里摘出去，
    /// 它们就成了真正无人监管的写操作 —— 而且用户永远不会知道。
    /// </summary>
    [Fact]
    public void LowRiskTools_AreStillVerified()
    {
        var options = new VerifyOptions
        {
            Mode = VerifyMode.Auto,
            Endpoint = new VerifierEndpoint { ApiKey = "test" },
        };

        var policy = new ApprovalPolicy(_guard);

        var unverified = policy.LowRiskTools
            .Where(n => !options.ShouldVerify(n, hasSideEffects: true))
            .ToList();

        Assert.True(unverified.Count == 0,
            "以下低风险工具既不报批、又不进核验：" + string.Join(", ", unverified));
    }

    /// <summary>
    /// 工具名不能重复 —— 重复注册时 <see cref="ToolRegistry"/> 会抛异常，
    /// 但那是在装配阶段；这里提前在类型层面发现。
    /// </summary>
    [Fact]
    public void ToolNames_AreUnique()
    {
        var dupes = AllTools()
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(dupes);
    }

    /// <summary>
    /// 真实注册表能装下全部工具且不抛异常。
    /// 这条同时验证「所有工具的 ParametersSchema 都能正确序列化」——
    /// schema 写错（比如键重复）会在这里炸，而不是等到第一次请求模型时。
    /// </summary>
    [Fact]
    public void Registry_AcceptsAllTools_AndBuildsSchema()
    {
        var registry = new ToolRegistry(AllTools());
        var schema = registry.BuildToolSchema();

        Assert.Equal(AllTools().Count, registry.Count);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, schema.ValueKind);
        Assert.True(schema.GetArrayLength() >= 22);
    }
}

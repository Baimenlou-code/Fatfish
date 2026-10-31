using System.Text.Json;
using FatFish.Core.Plugins;

namespace FatFish.Core.Safety;

/// <summary>审批判定的形态。</summary>
public enum ApprovalKind
{
    /// <summary>不需要人工确认（各档位豁免掉的都归这里）。</summary>
    None = 0,

    /// <summary>弹常规审批窗，用户点「允许 / 拒绝」。</summary>
    Manual = 1,

    /// <summary>凭密码放行 —— 不弹审批窗，改问密码。</summary>
    Password = 2,
}

/// <summary>审批判定结果。</summary>
/// <param name="Need">是否需要走人工那一关（审批窗或密码）。</param>
/// <param name="Reason">为什么要拦，给用户看的。</param>
/// <param name="Kind">用哪种方式过这一关。</param>
public readonly record struct ApprovalVerdict(bool Need, string Reason, ApprovalKind Kind = ApprovalKind.None)
{
    public static ApprovalVerdict NotNeeded => new(false, string.Empty, ApprovalKind.None);

    public static ApprovalVerdict Required(string reason)
        => new(true, reason, ApprovalKind.Manual);

    /// <summary>敏感文件凭密码放行（档位 4 专用）。</summary>
    public static ApprovalVerdict Password(string reason)
        => new(true, reason, ApprovalKind.Password);
}

/// <summary>
/// 审批档次 —— 用户可以在顶栏「审批」栏里随时切换。
///
/// <para>
/// <b>顺序有意义</b>：数值越大越宽松。判定代码里只做 <c>level &gt;= X</c> 这类比较，
/// 不要写 <c>level == X</c> 的平行分支 —— 否则将来加档位时必然漏改一处，
/// 而漏改的表现是「某个档位悄悄比它上一档更严」或更松，很难发现。
/// </para>
/// </summary>
public enum ApprovalLevel
{
    /// <summary>最严：每个动作都要人工确认，连 ws_read / ws_list 也不例外。</summary>
    Strict = 0,

    /// <summary>读免批：只读与低风险动作自动放行；写、删、执行仍需人工确认。</summary>
    ReadFree = 1,

    /// <summary>除敏感文件外免批：写入 / 删除 / 执行命令都自动放行，只有敏感文件要人工确认。</summary>
    WriteFree = 2,

    /// <summary>敏感文件凭密码放行：全部动作自动放行；碰敏感文件时用输入密码代替人工确认。</summary>
    SensitiveByPassword = 3,
}

/// <summary>「哪些读操作也要报批」的范围（对应 APPROVE_SCOPE）。</summary>
public enum ApproveScope
{
    /// <summary>只有写操作报批（默认）。</summary>
    Writes,

    /// <summary>连只读也报批。</summary>
    All,
}

/// <summary>「一键放行」的适用范围（对应 AUTO_APPROVE_SCOPE）。</summary>
public enum AutoApproveMode
{
    /// <summary>任何动作都不给一键放行，必须逐个确认。</summary>
    None,

    /// <summary>除 NeverAutoApprove 名单外，都允许一键放行。</summary>
    All,

    /// <summary>仅按 NeverAutoApprove 名单排除（中间档）。</summary>
    Selective,
}

/// <summary>
/// ApprovalPolicy —— 「要不要人工报批」的唯一判定入口。
///
/// 直接对应 Python 版 fatfish_core/policy.py 的：
///   APPROVAL_REQUIRED / APPROVE_SCOPE / AUTO_APPROVE_SCOPE /
///   HIGH_RISK_TOOLS / NEVER_AUTO_APPROVE /
///   _approval_needed() / _never_auto_approve()
///
/// 设计与 Python 版完全一致的核心思想：
///   ★ 判定收敛在**一个地方**，避免「多处各写一份」导致规则漂移。
///
/// 与 Python 版的差异：
///   ✗ 不含 _python_is_readonly()（那 120 行 AST 分析）。
///     理由：C# 内核不再解释执行 Python，该优化失去对象。
///     效果是「更保守」——少省几次审查员调用，但一道闸门都不少。
///
/// 命名注意：属性刻意叫 <see cref="Scope"/> / <see cref="AutoMode"/>，
/// 而不是 Scope / AutoApproveMode 同名 —— 避免 C# "Color Color" 歧义
/// （属性名与类型名相同时，`AutoApproveMode.All` 的解析会变得脆弱）。
/// </summary>
public sealed class ApprovalPolicy
{
    /// <summary>写类工具：默认需要人工报批。</summary>
    /// <remarks>
    /// <c>ws_bg_kill</c> 放在这里而不是 <see cref="HighRiskTools"/>：
    /// 它不执行任意命令，它做的是 <c>Kill(entireProcessTree: true)</c> ——
    /// 把某个正在写文件的任务连根掐掉。影响面取决于那个任务当时在干什么，
    /// 值得用户看一眼，但原因文案不该是「执行命令或代码」。
    /// </remarks>
    public HashSet<string> ApprovalRequired { get; init; } = new(StringComparer.Ordinal)
    {
        "ws_append", "ws_replace", "ws_delete", "ws_write", "ws_bg_kill",
    };

    /// <summary>
    /// 低风险可逆类工具：**有副作用，但不打断用户**。
    ///
    /// 对应 Python 版「L1 低副可逆 → 仅 AI 核验」那一档（ws_mkdir / ws_cd / ws_cd_approve）。
    /// 三个工具都只动「工作台的形状」（建个空目录、换个根），不改任何已有内容，
    /// 且全部被沙箱圈住 —— 建目录建不到边界外，换根另有独立的批准机制。
    ///
    /// ★ 为什么不干脆把它们的 HasSideEffects 设成 false 来「免审」：
    ///   （1）那是撒谎。ws_mkdir 会留下一个真实存在的目录，说它「无副作用」是错的，
    ///       而一个会说谎的声明会让所有依赖它的判定一起失真 —— 首当其冲就是 AI 核验范围。
    ///   （2）这两件事本该分开：**「要不要问人」和「要不要问审查员」不是同一个问题**。
    ///       建目录的成本低到不值得每次弹窗（弹窗多了用户就不看内容了），
    ///       但它仍然是「模型单方面改变磁盘」的行为，够格让审查员看一眼。
    ///       把两者混成一个开关，最后一定是二选一：要么用户被弹窗淹没，要么审查员看不到。
    /// </summary>
    public HashSet<string> LowRiskTools { get; init; } = new(StringComparer.Ordinal)
    {
        "ws_mkdir", "ws_cd", "ws_cd_approve",

        // 插件重载：改变的是「本进程能调用哪些工具」，不落盘、可逆。
        // 与 mkdir / cd 同一档的理由也一样 —— 它每改一次都要弹窗的话，
        // 调试插件的人会被自己的弹窗淹没；但它确实改变了能力边界，
        // 所以仍要进 AI 核验，不能变成无人监管的开关。
        "ws_plugin_reload",
    };

    /// <summary>高风险执行类工具。</summary>
    /// <remarks>
    /// ★ 2026-10-29：新增 <c>ws_bg_cmd</c> / <c>ws_bg_python</c> 时一并登记在这里。
    ///
    /// 后台工具**不是**「更安全的 ws_run_cmd」——它只是「更晚执行、更久运行」，
    /// 照样能删工作台外面的文件、照样能往外发数据。如果因为「它只是启动一下」
    /// 就把它放进免审区，那闸门等于开了一个后门：把任何危险命令包进后台任务即可。
    ///
    /// 这里的教训是结构性的：<b>新增一个「有副作用」的工具，必须回来改这张表。</b>
    /// 漏改不会报错、不会崩，只会静默地少弹一个窗 —— 与本文档第三节记录的
    /// 「缺陷一」完全同构。为此加了一条反射测试
    /// <c>ToolPolicyCoverageTests.EverySideEffectTool_RequiresApproval</c>：
    /// 它枚举程序集里所有 <c>ITool</c> 实现，逐个断言
    /// 「有副作用 ⇒ 命中 ApprovalRequired / HighRiskTools / LowRiskTools 三者之一」。
    /// 以后再忘，测试会直接红。
    /// </remarks>
    public HashSet<string> HighRiskTools { get; init; } = new(StringComparer.Ordinal)
    {
        "ws_run_python", "ws_run_cmd", "ws_bg_python", "ws_bg_cmd",
    };

    /// <summary>禁止「一键放行」的工具 —— 必须由用户亲手确认。</summary>
    public HashSet<string> NeverAutoApprove { get; init; } = new(StringComparer.Ordinal)
    {
        "ws_bg_python", "ws_run_python", "ws_run_cmd",
        "ws_delete", "ws_bg_cmd", "ws_bg_kill",
    };

    /// <summary>读操作的报批范围（对应 APPROVE_SCOPE）。</summary>
    public ApproveScope Scope { get; init; } = ApproveScope.Writes;

    /// <summary>
    /// 插件风险表 —— 让本类认识「运行时才出现的工具名」。
    /// </summary>
    /// <remarks>
    /// 插件工具名不可能写进上面三张静态 HashSet：
    /// 那些表在构造时固定，而插件是扫描磁盘之后才存在的；更麻烦的是
    /// <c>ToolPolicyCoverageTests.PolicyNames_AllReferToRealTools</c> 要求
    /// 「表里每个名字都必须对应程序集里真实存在的工具」—— 插件名一旦写进静态表，
    /// 那条测试会把它当成拼错的工具名而变红。
    ///
    /// 所以静态表继续管内置工具，插件走这个动态查询。
    /// 为 null 表示「本进程没有插件」（测试与只读守护进程都是这种情况）。
    /// </remarks>
    public IPluginRiskTable? Plugins { get; init; }

    /// <summary>一键放行的适用范围（对应 AUTO_APPROVE_SCOPE）。</summary>
    public AutoApproveMode AutoMode { get; init; } = AutoApproveMode.All;

    /// <summary>
    /// 当前审批档次。<b>可在运行时切换</b>（顶栏「审批」栏）。
    /// </summary>
    /// <remarks>
    /// ★ 为什么用 <c>Volatile</c> 而不是普通字段：
    ///   写入发生在 UI 线程（用户点下拉框），读取发生在 AgentLoop 的后台线程（逐个工具调用）。
    ///   普通字段在这种跨线程场景下没有可见性保证 —— 用户切到「最严」之后，
    ///   后台线程可能还在用旧值继续放行，而且这种延迟**不会报错、只会偶发**，
    ///   正是最难查的那类问题。枚举底层是 int，Volatile.Read/Write 足够。
    ///
    /// ★ 为什么不做成 <c>init</c> 属性（与 Scope / AutoMode 一致）：
    ///   那两个是启动期的配置，改一次要重启；审批档位是**操作过程中的临时决定**
    ///   （「这一批我盯着，别弹了」），必须能即时生效。
    /// </remarks>
    public ApprovalLevel Level
    {
        get => (ApprovalLevel)Volatile.Read(ref _level);
        set => Volatile.Write(ref _level, (int)value);
    }

    private int _level = (int)ApprovalLevel.Strict;

    private readonly PathGuard? _guard;
    private readonly WorkspaceSession? _session;

    public ApprovalPolicy(PathGuard guard)
    {
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    /// <summary>
    /// 跟随可切换的工作台会话 —— 换根之后守卫自动跟着换。
    /// </summary>
    /// <remarks>
    /// 本类对守卫的依赖其实只有 <see cref="PathGuard.CheckSensitive"/>，
    /// 而它只看**相对路径**、与根无关，所以即便绑的是旧根也不会判错。
    /// 之所以仍然接会话：将来只要有人给判定加一条「根相关」的规则
    /// （比如「根目录下的 .env 一律算敏感」），旧根就会立刻变成错的，
    /// 而那时候报错的方式是「偶发地漏一次报批」——最难查的那种。
    /// </remarks>
    public ApprovalPolicy(WorkspaceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        _guard = session.Guard;
    }

    /// <summary>当前该用的守卫（会话优先）。</summary>
    private PathGuard CurrentGuard =>
        _session?.Guard ?? _guard
        ?? throw new InvalidOperationException("ApprovalPolicy 未绑定任何守卫");

    /// <summary>
    /// 判断一次工具调用是否需要人工放行，以及要过哪一关。
    /// </summary>
    /// <param name="toolName">工具名，如 ws_write。</param>
    /// <param name="args">工具参数（用于识别敏感路径）。</param>
    public ApprovalVerdict NeedsApproval(string toolName, IReadOnlyDictionary<string, JsonElement>? args = null)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        var level = Level;

        // ---- ① 最严档：先于一切判断，什么都不豁免 ----
        //   连 ws_read / ws_list 都要点一次。这是用户明确要的「全都要审批」。
        if (level == ApprovalLevel.Strict)
            return ApprovalVerdict.Required("审批档位=严格：所有动作都需要人工确认");

        // ---- ② 敏感文件：所有非严格档位里都是最硬的一条线 ----
        //   ★ 必须查「path」参数而不是只查 ws_read：
        //     向 .env 写 / 追加 / 替换 / 删除同样属于「碰密钥」。
        //     （这一点在 Python 版里是踩过坑后补上的，C# 版直接继承。）
        var path = GetString(args, "path");
        if (!string.IsNullOrEmpty(path))
        {
            var sens = CurrentGuard.CheckSensitive(path);
            if (sens.IsSensitive)
            {
                var reason = sens.Reason ?? "敏感文件";

                // 档位 4：凭密码代替人工确认
                return level >= ApprovalLevel.SensitiveByPassword
                    ? ApprovalVerdict.Password(reason)
                    : ApprovalVerdict.Required(reason);
            }
        }

        // ---- ③ 读免批档：写 / 执行 / 插件写 仍然报批 ----
        if (level == ApprovalLevel.ReadFree)
        {
            // ★ 必须先判高危，再判写类。
            //   ws_run_cmd / ws_run_python 只登记在 HighRiskTools 里，不在 ApprovalRequired 里。
            //   原实现只查 ApprovalRequired，于是这两个工具会走到最后的 NotNeeded ——
            //   也就是说「能突破工作台边界」的那一类工具，反而是唯一不需要审批的。
            if (HighRiskTools.Contains(toolName))
                return ApprovalVerdict.Required("执行命令或代码，可能改动工作台之外的内容");

            if (ApprovalRequired.Contains(toolName))
                return ApprovalVerdict.Required(string.Empty);

            // 插件工具：风险档由 PluginHost 按执行面推断
            // ★ 放在静态表之后而不是之前：万一将来命名冲突的防护失效，
            //   内置规则仍然优先 —— 一条已知的危险规则，胜过一条插件自称的档案。
            if (Plugins?.Lookup(toolName) is { } pluginRisk)
            {
                if (pluginRisk.Tier != PluginRiskTier.Low)
                {
                    return pluginRisk.Tier == PluginRiskTier.High
                        ? ApprovalVerdict.Required($"插件 {pluginRisk.PluginId} 的执行型工具，可运行任意命令 / 代码")
                        : ApprovalVerdict.Required($"插件 {pluginRisk.PluginId} 的写操作");
                }
            }

            // 剩下的是只读工具（ws_read / ws_list / ws_search / ws_where / web_* …）与低风险工具
            // ★ 环境变量 APPROVE_SCOPE=all 仍然压过本档位：
            //   那是启动期就定下的「连读都要审」的更严配置，不该被一个临时档位悄悄放开。
            if (Scope == ApproveScope.All)
                return ApprovalVerdict.Required("approve_scope=all（只读也报批）");

            return ApprovalVerdict.NotNeeded;
        }

        // ---- ④ 除敏感文件外免批 / ⑤ 凭密码放行敏感文件 ----
        //   走到这里说明「不是敏感文件」，按用户选择一律放行。
        //
        //   ★ 这里确实把 ws_run_cmd / ws_delete 也放行了 —— 这是用户的明确选择，
        //     不是漏判。护栏没有全丢：
        //       · 双人核验仍会逐批送审（且会被告知「本批无人批准，请从严」，见 VerifySession）；
        //       · 敏感文件仍然拦（档位 4 要密码）；
        //       · 工作台沙箱与「一键放行」的黑名单照旧生效。
        //   如果你只想要「写免批、执行仍报批」，用档位 ③（读免批）+ 一键放行更合适。
        return ApprovalVerdict.NotNeeded;
    }

    /// <summary>
    /// 该动作是否禁止「一键放行」（必须用户亲手确认）。
    ///
    /// ★ 敏感文件无论**读还是写**、无论哪个档位都不豁免：
    ///   只查 ws_read 是不够的 —— 向 .env 写 / 追加 / 替换同样属于「碰密钥」。
    ///   （这一点在 Python 版里是踩过坑后补上的，C# 版直接继承。）
    /// </summary>
    public bool NeverAutoApproves(string toolName, IReadOnlyDictionary<string, JsonElement>? args = null)
    {
        var path = GetString(args, "path");
        if (!string.IsNullOrEmpty(path) && CurrentGuard.CheckSensitive(path).IsSensitive)
            return true;

        // ★ 插件里「能跑命令 / 代码」的工具，与内置 ws_run_cmd 同等对待：
        //   永远不给一键放行。这个判断来自 PluginHost 的执行面推断，
        //   插件自己声明 risk=low 改不了它（见 PluginRisk.Combine）。
        if (Plugins?.Lookup(toolName) is { Tier: PluginRiskTier.High })
            return true;

        return AutoMode switch
        {
            // 什么都不放行：每次都亲手确认
            AutoApproveMode.None => true,

            // 「全部可放行」也要被那张硬性拒绝名单拦住：
            // ws_run_cmd / ws_delete 这类即使已一键放行，也必须亲手点。
            // ★ 原实现这里直接 return false，等于把 NeverAutoApprove 整张名单作废了 ——
            //   与属性注释写的「除 NeverAutoApprove 名单外都允许」正好相反。
            AutoApproveMode.All => NeverAutoApprove.Contains(toolName),

            // 中间档：高危工具一律不给一键放行，比名单更保守
            _ => NeverAutoApprove.Contains(toolName) || HighRiskTools.Contains(toolName),
        };
    }

    /// <summary>
    /// 敏感文件报批时的具体原因（供 UI 显示「为什么拦你」）。
    /// </summary>
    public string DescribeSensitivity(string relativePath)
    {
        var verdict = CurrentGuard.CheckSensitive(relativePath);
        return verdict.IsSensitive ? verdict.Reason ?? "敏感文件" : string.Empty;
    }

    /// <summary>档位的短名字（顶栏下拉框用）。</summary>
    public static string LevelName(ApprovalLevel level) => level switch
    {
        ApprovalLevel.Strict => "最严 · 全部审批",
        ApprovalLevel.ReadFree => "读免批",
        ApprovalLevel.WriteFree => "除敏感外免批",
        ApprovalLevel.SensitiveByPassword => "敏感文件凭密码",
        _ => level.ToString(),
    };

    /// <summary>
    /// 当前档位对「免掉了什么」的完整描述。
    ///
    /// ★ 这段文字会**原样送进审查员的提示词**（见 VerifySession.BuildExtraNote）：
    ///   人工那一关被免掉之后，审查员就是唯一的外部复核，
    ///   它必须知道「现在没有任何人在逐批点确认」，否则它会按旧人格假设
    ///   「后面还有用户兜底」而放松标准。
    /// </summary>
    public string DescribeBypass() => Level switch
    {
        ApprovalLevel.Strict => string.Empty,
        ApprovalLevel.ReadFree =>
            "审批档位=读免批：只读与低风险动作自动放行；写 / 删除 / 执行命令仍逐批人工确认。",
        ApprovalLevel.WriteFree =>
            "审批档位=除敏感文件外免批：**写入、删除、执行命令 / 代码、后台任务全部自动放行**，" +
            "仅敏感文件仍逐批人工确认。",
        ApprovalLevel.SensitiveByPassword =>
            "审批档位=敏感文件凭密码：**全部动作自动放行**，敏感文件仅凭口令放行（口令由用户输入，" +
            "不是逐条审阅内容）。",
        _ => "审批档位=未知。",
    };

    private static string? GetString(IReadOnlyDictionary<string, JsonElement>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var el))
            return null;

        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}

using System.Text.Json;
using FatFish.Core.Llm;
using FatFish.Core.Tools;

namespace FatFish.Core.Safety;

/// <summary>一批送审动作所属的业务域。</summary>
public enum VerifyDomain
{
    /// <summary>动工作台本身：建目录、换根（换位置）、重载插件（换可用工具集）。</summary>
    WorkspaceShape,

    /// <summary>改工作台里的文件：写 / 追加 / 替换 / 删除。</summary>
    FileWrite,

    /// <summary>执行命令或代码（含后台任务）。</summary>
    Exec,

    /// <summary>联网检索 / 抓取。</summary>
    Net,

    /// <summary>插件工具（能力面未知，按执行面推断）。</summary>
    Plugin,

    /// <summary>开 / 控子代理。</summary>
    Subagent,

    /// <summary>一批里混了多个域。</summary>
    Mixed,
}

/// <summary>
/// 一个业务域的审查配置：**送多少上下文** + **查什么**。
/// </summary>
/// <remarks>
/// ★ 为什么按业务域裁剪材料（2026-11-05）：
///   原来每一批都送同一套素材（含 16 条 × 600 字的近期对话），
///   不管这批是要删一个文件还是要搜一条资料。两个后果：
///     1. **贵**：那段对话动辄上万字符；
///     2. **更容易编**：给审查员的素材越多越杂，它越容易把不相干的东西串成一个故事。
///
/// ★ 但「一律不给上下文」是过头了（第一版就是这么写的，被用户纠正）：
///   多轮任务里「用户到底要干什么」经常不在最后一句里 ——
///   先「看看目录结构」、再「现在把那个配置改一下」，单看末句判不出对不对。
///   所以不是「给不给」，而是**给几条**：
///     · 判「改动是否服务于目标」的域 → 给最近 4 条（约一两轮）
///     · 联网 → 2 条；动工作台本身 → 0 条（看原声就够）
///     · 子代理 / 混合批次 → 给满（16 条）
///   ★ 注意 <see cref="WantContext"/> 只是 <see cref="ContextMessages"/> 的派生值，
///     不要两边各写一份 —— 那种重复迟早会对不上。
/// </remarks>
public sealed record VerifyProfile
{
    public required VerifyDomain Domain { get; init; }

    /// <summary>给日志/诊断看的短名。</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 送最近几条对话作为上下文。<c>0</c> = 不送。
    /// </summary>
    /// <remarks>
    /// 上限参考 <c>AgentLoop.RecentContext()</c> 的 16 条；这里只做**更少**的选择。
    /// </remarks>
    public int ContextMessages { get; init; }

    /// <summary>要不要送「用户真实操作记录」。涉及授权判断的域需要。</summary>
    public bool WantUserEvents { get; init; } = true;

    /// <summary>该域专属的审查要点，追加在送审上下文里。</summary>
    public string Instruction { get; init; } = string.Empty;

    /// <summary>要不要送近期对话（<see cref="ContextMessages"/> 的派生值）。</summary>
    public bool WantContext => ContextMessages > 0;
}

/// <summary>
/// 业务域判定与分域配置。
/// </summary>
public static class VerifyProfiles
{
    /// <summary>按工具名归域。未知名字一律按插件处理（能力面未知，宁可多查）。</summary>
    public static VerifyDomain Of(string toolName) => toolName switch
    {
        "ws_mkdir" or "ws_cd" or "ws_cd_approve" or "ws_plugin_reload"
            or "ws_site_new" or "ws_site_list" or "ws_site_enter" or "ws_site_home" or "ws_site_drop"
            => VerifyDomain.WorkspaceShape,

        "ws_write" or "ws_append" or "ws_replace" or "ws_delete"
            => VerifyDomain.FileWrite,

        "ws_run_cmd" or "ws_run_python" or "ws_bg_cmd" or "ws_bg_python" or "ws_bg_kill"
            => VerifyDomain.Exec,

        "web_search" or "web_fetch" or "net_balance"
            => VerifyDomain.Net,

        "subagent_run" or "subagent_say" or "subagent_stop"
            or "subagent_status" or "subagent_rollback"
            => VerifyDomain.Subagent,

        _ => VerifyDomain.Plugin,
    };

    /// <summary>整批归域：全同则取该域，否则 <see cref="VerifyDomain.Mixed"/>。</summary>
    public static VerifyDomain Classify(IReadOnlyList<(string Name, JsonElement Args)> actions)
    {
        VerifyDomain? single = null;

        foreach (var (name, _) in actions)
        {
            var domain = Of(name);

            if (single is null)
            {
                single = domain;
                continue;
            }

            if (single != domain)
                return VerifyDomain.Mixed;
        }

        return single ?? VerifyDomain.Mixed;
    }

    /// <summary>取某域的审查配置。</summary>
    public static VerifyProfile For(VerifyDomain domain) => domain switch
    {
        // 执行类：要判断「这条命令是否服务于用户的目标」，所以需要最近几轮
        VerifyDomain.Exec => new VerifyProfile
        {
            Domain = domain,
            Name = "执行命令/代码",
            ContextMessages = 4,
            WantUserEvents = true,
            Instruction =
                "【本批是「执行命令 / 代码」】只核对三件事："
                + "① 命令是否对应用户要的目标；"
                + "② 是否可能触碰工作台之外（绝对路径、cd ..、网络、删除、格式化）；"
                + "③ 是否与执行者自述的计划一致。"
                + "不要推测命令的输出，也不要假设它「大概只会读」—— 你没有运行过它。",
        },

        // 文件类：既要看目标范围，也要看「读过没有」
        VerifyDomain.FileWrite => new VerifyProfile
        {
            Domain = domain,
            Name = "改工作台文件",
            ContextMessages = 4,
            WantUserEvents = true,
            Instruction =
                "【本批是「改工作台文件」】只核对三件事："
                + "① 目标文件是否在用户要求的范围内；"
                + "② 执行者是否真的读过要改的部分（见「读凭证覆盖率」）；"
                + "③ 有没有可能丢掉它没看过的内容。"
                + "不要凭文件名想象文件内容，也不要把「它说改好了」当成事实。",
        },

        // 联网：几乎没有本地风险，主要怕把私密内容带出去；意图看最后一轮就够
        VerifyDomain.Net => new VerifyProfile
        {
            Domain = domain,
            Name = "联网检索",
            ContextMessages = 2,
            WantUserEvents = false,
            Instruction =
                "【本批是「联网检索 / 抓取」】只核对：检索词或 URL 是否会把工作台里的私密内容"
                + "（路径、密钥、内部名、用户身份）带到外部，以及是否属于用户意图。"
                + "检索结果本身未经核实，不要把它当成事实前提。",
        },

        // 子代理：另一份上下文、自主循环，风险在「一段看不见的连续动作」
        VerifyDomain.Subagent => new VerifyProfile
        {
            Domain = domain,
            Name = "子代理",
            ContextMessages = 16,
            WantUserEvents = true,
            Instruction =
                "【本批是「开子代理」】子代理带着自己的循环去读文件、按权限写盘、跑命令，"
                + "中间过程用户看不到。只核对：任务描述是否清晰到不会失控、权限是否最小、"
                + "以及用户是否知道它会自主执行。",
        },

        // 工作台形状：建目录 / 换根 / 重载插件。可逆、不碰已有内容，看原声就够
        VerifyDomain.WorkspaceShape => new VerifyProfile
        {
            Domain = domain,
            Name = "建目录 / 换根 / 重载插件",
            ContextMessages = 0,
            WantUserEvents = true,
            Instruction =
                "【本批动的是工作台本身】（建目录 / 换工作台根 / 重载插件）只核对："
                + "目标是否落在工作台内、是否可逆。这类动作默认放行，"
                + "除非它把根换到用户从没提过的地方，或重载后引入了不该有的能力。",
        },

        // 插件：能力面由执行面推断，不看自称；但需要上下文判断「是否在用户意图内」
        VerifyDomain.Plugin => new VerifyProfile
        {
            Domain = domain,
            Name = "插件工具",
            ContextMessages = 4,
            WantUserEvents = true,
            Instruction =
                "【本批含插件工具】插件「能干什么」由执行面推断，不看它自称的 risk。"
                + "只核对：它实际会做的动作（跑命令 / 写盘 / 外联）是否在用户意图内。",
        },

        // 混合批次：给全，但要求逐项分域核对
        _ => new VerifyProfile
        {
            Domain = VerifyDomain.Mixed,
            Name = "混合动作",
            ContextMessages = 16,
            WantUserEvents = true,
            Instruction =
                "【本批混合了多类动作】按各自类型分别核对；"
                + "只要有一项越出用户意图就拦。不要因为其中一项无害就顺带放过其它项。",
        },
    };

    /// <summary>
    /// 按域把整段近期对话裁到该域需要的条数（保留**最近**的若干条）。
    /// </summary>
    /// <remarks>
    /// ★ 为什么在这里裁而不是改 <c>AgentLoop.RecentContext()</c>：
    ///   那个方法的职责是「从历史里摘出一段」，摘多少由调用方决定；
    ///   而「某个域该看几条」是本文件的知识。放这边，改阈值不用碰内核。
    /// ★ 输入是**逐条消息**（每条自带自己的换行），不是拼好的长串。
    ///   2026-11-06 修正：原先按 <c>'\n'</c> 切「最后 N 行」，而一条多行回答
    ///   会被拆成几十「条」—— 配 4 条时实际可能只送出最后一条回答的尾巴，
    ///   用户的原话根本不在里面。那个 bug 恰好废掉了「给上下文」的初衷。
    ///   <c>List</c>/<c>IReadOnlyList</c> 依赖 Directory.Build.props 的 ImplicitUsings，无需新增 using。
    /// </remarks>
    public static string LimitContext(IReadOnlyList<string> messages, VerifyProfile profile)
    {
        if (profile.ContextMessages <= 0 || messages is null || messages.Count == 0)
            return string.Empty;

        var from = Math.Max(0, messages.Count - profile.ContextMessages);
        var picked = new List<string>(messages.Count - from);

        for (var i = from; i < messages.Count; i++)
            picked.Add(messages[i]);

        return string.Join("\n", picked);
    }

    /// <summary>
    /// 生成「读凭证覆盖率」补充材料（只对文件类动作有意义）。
    /// </summary>
    /// <remarks>
    /// ★ 为什么由主程序注入而不是让审查员自己查：审查员没有读凭证库的访问权，
    ///   而覆盖率恰恰是判断「这次改动会不会丢内容」最硬的依据。
    /// ★ 文本要短：它会被拼进「送审上下文」那一节，那一节有 800 字符预算。
    /// </remarks>
    public static string CoverageNote(IReadOnlyList<ToolCall> calls, ToolContext context)
    {
        try
        {
            var notes = new List<string>();

            foreach (var call in calls)
            {
                if (Of(call.Name) != VerifyDomain.FileWrite)
                    continue;

                string? rel;

                try
                {
                    rel = call.ParseArguments().TryGetProperty("path", out var p) ? p.GetString() : null;
                }
                catch (Exception)
                {
                    rel = null;
                }

                if (string.IsNullOrWhiteSpace(rel))
                    continue;

                ReadCoverage? coverage;

                try
                {
                    coverage = context.Tickets.Coverage(context.Guard.Resolve(rel));
                }
                catch (Exception)
                {
                    coverage = null;
                }

                notes.Add(coverage is null
                    ? $"{call.Name} {rel}：未读过"
                    : $"{call.Name} {rel}：{coverage}");
            }

            return notes.Count == 0
                ? string.Empty
                : "读凭证覆盖率（主程序注入，可信）：" + string.Join("；", notes);
        }
        catch (Exception)
        {
            // 补充材料生成失败绝不能影响核验本身
            return string.Empty;
        }
    }
}

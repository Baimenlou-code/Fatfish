using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FatFish.Core.Safety;

namespace FatFish.App;

/// <summary>
/// 顶栏「审批」栏 —— 四个档次的开关，直接改 <see cref="ApprovalPolicy.Level"/>。
///
/// <para>
/// <b>为什么放在顶栏而不是设置窗口里：</b>
/// 审批档位是「这一批活我盯不盯」的临时决定，不是一次性配置。
/// 每改一次都要开设置 → 保存 → 重启的话，用户根本不会去调，
/// 结果就是一直用默认的最严档、被弹窗烦到麻木，或者干脆关掉整个机制。
/// </para>
///
/// <para>
/// <b>2026-10-30 改版：做成和插件栏一样的栏目。</b>
/// 原先是一个「审批」文字标签 + 原生 <c>ComboBox</c>，装在顶栏中列。
/// 两个问题：① ComboBox 会落回系统 Aero 模板（白脸 + 立体箭头），夜间模式下就是一块白，
/// 而要把 ComboBox 换肤得额外维护一整套模板（而且它还要管弹层）；
/// ② 它横在中列，占掉一整块横向空间。
/// 现在改成<b>图标按钮 + 点开菜单</b>，与插件栏完全同构：
///   · 按钮复用 <c>ToolButton</c> 样式（平时透明、悬停浮出）—— 与左边那排图标一致；
///   · 菜单复用 CeTheme 里的 <c>ContextMenu</c> / <c>MenuItem</c> 样式 —— 已经是主题化的，
///     换肤自动跟随，不需要为「下拉框」再写一份模板；
///   · 图标颜色直接表达「现在有多严」：红＝最严、黄＝读免批、蓝＝除敏感外免批、紫＝凭密码。
/// </para>
///
/// <para>
/// <b>档位与「一键放行」的关系：</b>
/// 两者是独立的开关，但会叠加。切到「最严」时本类顺手把「一键放行」也收回 ——
/// 否则界面上写着「全部审批」，实际却因为会话里还留着放行标记而一路放行，
/// 那种不一致比没有这个栏更糟。
/// </para>
/// </summary>
public partial class MainWindow
{
    /// <summary>顶栏「审批」按钮。null 表示还没装上。</summary>
    private Button? _approvalButton;

    /// <summary>按钮里的图标（颜色 = 当前档位）。</summary>
    private TextBlock? _approvalIcon;

    /// <summary>
    /// 最近一次弹出的档位菜单。
    /// </summary>
    /// <remarks>
    /// 唯一用途是给 GUI 冒烟读：菜单是弹层（独立 Popup），整窗截图里看不到它，
    /// 只能直接读它的背景 / 描边 / 勾选状态，才能证明「夜间模式下它不是白底」。
    /// </remarks>
    internal ContextMenu? LastApprovalMenu { get; private set; }

    /// <summary>当前生效的审批档位（策略还没就绪时为 null）。诊断 / GUI 冒烟用。</summary>
    internal ApprovalLevel? CurrentApprovalLevel => _policy?.Level;

    /// <summary>档位 → 顶栏图标色。颜色本身就是「当前有多严」的指示器。</summary>
    private static string ApprovalIconKey(ApprovalLevel level) => level switch
    {
        ApprovalLevel.Strict => "IconRed",
        ApprovalLevel.ReadFree => "IconAmber",
        ApprovalLevel.WriteFree => "IconBlue",
        _ => "IconViolet",
    };

    /// <summary>在顶栏插入「审批」图标按钮（位置：插件按钮左边，没有插件按钮就在侧边栏按钮前面）。</summary>
    private void InstallApprovalBar()
    {
        if (_approvalButton is not null)
            return;

        try
        {
            if (SidebarButton?.Parent is not Panel toolbar)
                return;

            var icon = new TextBlock
            {
                Text = "\uE72E",          // Segoe MDL2：锁 —— 这一栏管的是「哪些动作要你点头」
                FontFamily = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var button = new Button
            {
                Name = "ApprovalButton",
                Content = icon,
            };

            if (TryFindResource("ToolButton") is Style style)
                button.Style = style;

            button.Click += OnApprovalClick;

            // 与插件栏同一套插法：插件按钮先装好（OnContentRendered 里的顺序），先插它左边。
            var plugins = toolbar.Children.OfType<Button>()
                .FirstOrDefault(b => string.Equals(b.Name, "PluginsButton", StringComparison.Ordinal));

            UIElement anchor = plugins ?? (UIElement)SidebarButton;
            int index = toolbar.Children.IndexOf(anchor);

            toolbar.Children.Insert(index < 0 ? 0 : index, button);

            _approvalButton = button;
            _approvalIcon = icon;

            // 默认档位 = 除敏感外免批（用户 2026-10-30 定）。图标与提示先对上，再生效。
            SyncApprovalBar(ApprovalLevel.WriteFree);
            ApplyApprovalLevel(ApprovalLevel.WriteFree, announce: false);

            // 读回上次的两个偏好（开子代理是否要审批、每轮回合上限）。
            // 读不到 / 读坏了都退回默认 —— 而且默认在更严的那一侧。
            var prefs = ApprovalPrefs.Load(ApprovalPrefsPath);
            ApplySubagentGate(prefs.RequireSubagentApproval, announce: false, persist: false);
            ApplyRoundLimit(prefs.RoundLimit, announce: false, persist: false);

            Log("[审批] 顶栏审批栏已就绪（图标按钮，点开选档位）");
        }
        catch (Exception ex)
        {
            Log("审批栏安装失败：" + ex.GetType().Name + " - " + ex.Message);
        }
    }

    /// <summary>点「审批」按钮：从按钮下方弹出四档菜单，当前档打勾。</summary>
    private void OnApprovalClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom,
        };

        ApprovalLevel current = _policy?.Level ?? ApprovalLevel.WriteFree;

        foreach (ApprovalLevel level in new[]
                 {
                     ApprovalLevel.Strict,
                     ApprovalLevel.ReadFree,
                     ApprovalLevel.WriteFree,
                     ApprovalLevel.SensitiveByPassword,
                 })
        {
            var picked = level;

            var item = new MenuItem
            {
                Header = ApprovalPolicy.LevelName(picked),
                IsCheckable = true,
                IsChecked = picked == current,
                ToolTip = ApprovalLevelHelp(picked),
            };

            item.Click += (_, _) => ApplyApprovalLevel(picked, announce: true);

            menu.Items.Add(item);
        }

        // ---- 档位之下：一个独立的开关（与敏感文件同级的那条硬线）----
        menu.Items.Add(new Separator());

        var gate = new MenuItem
        {
            Header = "开子代理需要审批",
            IsCheckable = true,
            IsChecked = _policy?.RequireApprovalForSubagents ?? true,
            ToolTip = "勾上（默认）：每次 subagent_run 都要你点一次允许，且不接受「本次会话放行」。\n" +
                      "取消：开子代理不再弹窗，但内核的 AI 核验照旧，动作照样进日志。\n" +
                      "为什么它单独一条：子代理是另一个模型、另一份上下文，" +
                      "一次放行换来的是一整段你看不见的连续动作 —— 而档位里的「免批」，" +
                      "原本是针对「你盯着看的一次动作」。",
        };

        gate.Click += (_, _) => ApplySubagentGate(gate.IsChecked, announce: true);
        menu.Items.Add(gate);

        // ---- 再往下：每轮回合上限（子菜单）----
        //   放这里而不是另起一栏：它和上面两条一样，是「这一批活我放多开」的设置，
        //   而不是 .env 那种要重启的配置。
        var limitMenu = new MenuItem
        {
            Header = "每轮回合上限",
            ToolTip = "一次输入里，模型最多能和工具来回几轮。到顶就停（当前轮的结论会保留）。\n" +
                      "调高 / 不限会让长任务一次跑完，但也会让一次跑偏的对话花掉更多 token。",
        };

        var currentLimit = _policy?.RoundLimit ?? ApprovalPolicy.DefaultRoundLimit;

        foreach (var choice in ApprovalPolicy.RoundLimitChoices)
        {
            var picked = choice;

            var item = new MenuItem
            {
                Header = ApprovalPolicy.RoundLimitName(picked),
                IsCheckable = true,
                IsChecked = picked == currentLimit,
                ToolTip = picked <= 0
                    ? "不设上限：只有模型自己结束、你中断、或出错时才停。\n" +
                      "长任务用这个；但一次跑偏的对话也会一路烧下去。"
                    : $"跑到第 {picked} 轮就停，已完成的改动保留。\n" +
                      "可在运行中直接改，下一轮立刻按新值判断。",
            };

            item.Click += (_, _) => ApplyRoundLimit(picked, announce: true);
            limitMenu.Items.Add(item);
        }

        menu.Items.Add(limitMenu);

        LastApprovalMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>把按钮的图标色与悬停提示同步成某个档位（图标走资源引用，换肤跟着变）。</summary>
    private void SyncApprovalBar(ApprovalLevel level)
    {
        _approvalIcon?.SetResourceReference(TextBlock.ForegroundProperty, ApprovalIconKey(level));

        if (_approvalButton is null)
            return;

        _approvalButton.ToolTip =
            "审批档位：" + ApprovalPolicy.LevelName(level) + "\n" +
            "点开＝选档位（立即生效，不需要重启程序）。\n" +
            "· 最严 · 全部审批：每个动作都弹窗，包括 ws_read / ws_list\n" +
            "· 读免批：只读与低风险动作自动放行；写 / 删除 / 执行命令仍弹窗\n" +
            "· 除敏感外免批：写入、删除、执行命令、后台任务全部自动放行；仅敏感文件弹窗\n" +
            "· 敏感文件凭密码：全部自动放行；碰敏感文件时输入口令代替弹窗\n" +
            "图标颜色＝当前档位：红＝最严　黄＝读免批　蓝＝除敏感外免批　紫＝凭密码\n" +
            "免掉人工批准时，送审材料里会明确告知审查员「本批无人逐条确认，请从严」，\n" +
            "审查员因此会按更严的标准把关 —— 但它的结论只是意见，别把档位当成「关掉核验」。\n" +
            "菜单底部另有两个子项：\n" +
            "· 「开子代理需要审批」（默认开，与敏感文件同级）\n" +
            "· 「每轮回合上限」：15 / 25 / 35 / 50 / 100 / 不限，默认 25 轮 ——\n" +
            "  改完立刻对运行中的对话生效（下一次轮次开始就按新值判断）。";
    }

    /// <summary>菜单每一项的悬停说明（写清「这一档会放行什么」）。</summary>
    private static string ApprovalLevelHelp(ApprovalLevel level) => level switch
    {
        ApprovalLevel.Strict =>
            "最严 · 全部审批\n" +
            "每个动作都弹窗，包括只读的 ws_read / ws_list。\n" +
            "切到这一档会同时撤掉本会话的「一键放行」。",
        ApprovalLevel.ReadFree =>
            "读免批\n" +
            "只读与低风险动作自动放行；写 / 删除 / 执行命令仍然弹窗。",
        ApprovalLevel.WriteFree =>
            "除敏感外免批\n" +
            "写入、删除、执行命令、后台任务全部自动放行；\n" +
            "只有敏感文件（.env / 密钥）仍弹窗。",
        _ =>
            "敏感文件凭密码\n" +
            "全部自动放行；碰敏感文件时输入口令代替弹窗。\n" +
            "口令只在宿主进程里，模型拿不到 —— 所以它无法自我授权。",
    };

    /// <summary>
    /// 切换「开子代理需人工确认」并落盘。
    /// </summary>
    /// <remarks>
    /// 落盘位置与字号 / 窗口位置同一处（logs/ui/），因为它也是「这台机器上的偏好」，
    /// 而不是 .env 那种要重启才生效的配置。读失败时按「开」处理 —— 失败即更严。
    /// </remarks>
    private void ApplySubagentGate(bool require, bool announce, bool persist = true)
    {
        if (_policy is null)
        {
            if (announce)
                Log("[审批] 内核尚未就绪，子代理开关没有生效");

            return;
        }

        _policy.RequireApprovalForSubagents = require;

        // 只有「用户真的点了」才落盘。启动时读回也写一次的话，每次开程序都动一下日志目录，
        // 而且一旦读到坏文件，会顺手把默认值覆盖上去，把真正的原因抹掉。
        if (persist)
            SaveApprovalPrefs();

        if (!announce)
            return;

        Log(require
            ? "[审批] 开子代理 = 需要人工确认（且不接受「本次会话放行」）"
            : "[审批] 开子代理 = 不再弹窗（AI 核验照旧，动作仍进日志）");

        StatusText = require ? "[审批] 子代理启动需确认" : "[审批] 子代理启动免批";
    }

    /// <summary>
    /// 切换「每轮回合上限」并落盘（0 = 不限）。
    /// </summary>
    /// <remarks>
    /// ★ 改的是 <see cref="ApprovalPolicy.RoundLimit"/>，而 AgentLoop 在每一轮开头
    ///   都会重新读一次 —— 所以正跑着的对话从下一轮起就按新值判断，
    ///   不需要重启，也不会打断已经在跑的那一轮。
    /// ★ 调低不会「砍掉」当前轮：上限判断发生在轮次<b>开始之前</b>，
    ///   已经开始的这一轮会完整跑完（工具调用不会半途中止）。
    /// </remarks>
    private void ApplyRoundLimit(int limit, bool announce, bool persist = true)
    {
        if (_policy is null)
        {
            if (announce)
                Log("[审批] 内核尚未就绪，回合上限没有生效");

            return;
        }

        _policy.RoundLimit = limit;

        if (persist)
            SaveApprovalPrefs();

        if (!announce)
            return;

        Log(limit <= 0
            ? "[审批] 每轮回合上限 = 不限（只有模型结束 / 你中断 / 出错时才会停）"
            : $"[审批] 每轮回合上限 = {limit} 轮");

        StatusText = "[审批] 上限 " + ApprovalPolicy.RoundLimitName(limit);
    }

    /// <summary>审批栏偏好的落盘路径（logs/ui/approval.json）。</summary>
    private string ApprovalPrefsPath
        => Path.Combine(_guard?.Root ?? ".", "logs", "ui", ApprovalPrefs.FileName);

    /// <summary>
    /// 把当前两个偏好一起写盘。
    /// </summary>
    /// <remarks>
    /// ★ 必须<b>一起</b>写：文件里有两个字段，各写各的会覆盖对方 ——
    ///   表现是「改了回合上限，子代理开关自己变回默认」，而且只在下次启动才被发现。
    /// ★ 记不住不影响本次生效；但必须留痕，否则重启后「自己变回去了」查不出来。
    /// </remarks>
    private void SaveApprovalPrefs()
    {
        if (_policy is null)
            return;

        var snapshot = new ApprovalPrefs.Snapshot(
            _policy.RequireApprovalForSubagents,
            _policy.RoundLimit);

        var error = ApprovalPrefs.Save(ApprovalPrefsPath, snapshot);

        if (error is not null)
            Log("[审批] 偏好没能记住（本次仍生效）：" + error);
    }

    /// <summary>真正切档位：改策略、必要时收回「一键放行」、刷新界面与日志。</summary>
    private void ApplyApprovalLevel(ApprovalLevel level, bool announce)
    {
        // 先同步界面：即使策略还没就绪（初始化中），图标也得对上用户刚点的那个档位。
        SyncApprovalBar(level);

        if (_policy is null)
        {
            if (announce)
                Log("[审批] 内核尚未就绪，档位没有生效（等初始化完成后再切一次）");
            return;
        }

        _policy.Level = level;

        // 「最严」与「一键放行」不能并存：界面写着全部审批、实际却一路放行，是最坏的组合。
        if (level == ApprovalLevel.Strict && _agent is { AutoApprovedForSession: true })
        {
            _agent.SetAutoApprovedForSession(false);

            if (announce)
                Log("[审批] 切到最严档，已同时取消本会话的「一键放行」");
        }

        if (!announce)
            return;

        string name = ApprovalPolicy.LevelName(level);

        Log("[审批] " + name + " — " + _policy.DescribeBypass());
        StatusText = "[审批] " + name;

        if (level != ApprovalLevel.Strict)
        {
            // 在日志里明说「核验仍在，但审查员现在是你唯一的外部复核」。
            // 提示不能只在 tooltip 里 —— 切档位的人不会去看 tooltip。
            Log("[审批] 提醒：该档位会免掉部分人工确认，送审材料中已标注「免于人工批准」，审查员会按更严标准把关。");
        }
    }
}

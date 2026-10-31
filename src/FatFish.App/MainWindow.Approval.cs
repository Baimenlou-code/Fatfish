using System;
using System.Linq;
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
            "审查员因此会按更严的标准把关 —— 但它的结论只是意见，别把档位当成「关掉核验」。";
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

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FatFish.Core.Tools;

namespace FatFish.App;

/// <summary>
/// 审批对话框 —— 「双闸门」里第一道闸门（人工报批）的界面。
///
/// 设计取舍：
///   · 用独立 Window 而不是 MessageBox：
///     MessageBox 只能 Yes/No/Cancel 三个语义固定的按钮，
///     而这里需要「拒绝 / 允许 / 本次会话都放行」三态，
///     还要把工具参数原样展示给用户（用等宽字体，方便核对路径）。
///   · 默认焦点在「拒绝」上：误按回车不会造成破坏。
///   · 敏感文件时禁用「本会话都放行」——这跟 ApprovalPolicy.NeverAutoApproves 的判定一致，
///     界面层不自己发明规则，避免两处规则漂移。
/// </summary>
public partial class ApprovalWindow : Window
{
    private static readonly JsonSerializerOptions ArgsFormat = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>是否允许执行。</summary>
    public bool Approved { get; private set; }

    /// <summary>用户是否勾选了「本次会话内不再询问」。</summary>
    public bool AllowForSession { get; private set; }

    public ApprovalWindow(ApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        InitializeComponent();

        // ★ 档位 4（敏感文件凭密码）：不弹常规审批窗，改用口令确认。
        //
        //   为什么在构造函数里就把 Content 换掉，而不是新开一个窗口：
        //     · 调用方（MainWindow.ShowApprovalDialog）只认 ApprovalWindow 的
        //       Approved / AllowForSession 两个结果，换窗口就要改调用方；
        //     · 审批窗口从来不是"被看见"就完事的，它只需要拿到一个布尔结果，
        //       所以把界面替换掉、复用同一套结果字段，是最小改动。
        //   口令校验放这里而不是策略层：策略层只负责说「需要口令」，
        //   真正"对不对"必须由持有口令的人（本进程）在用户输入之后判断。
        if (request.Kind == FatFish.Core.Safety.ApprovalKind.Password)
        {
            Content = BuildPasswordGate(request);
            SizeToContent = SizeToContent.WidthAndHeight;
            return;
        }

        ToolText.Text = request.Tool;
        ReasonText.Text = request.Reason;

        ArgsBox.Text = FormatArgs(request.Args);

        if (!request.AllowsOneKey)
        {
            SessionCheck.IsEnabled = false;
            SessionCheck.IsChecked = false;
            SessionCheck.ToolTip = "该操作不允许一键放行（涉及敏感文件或高风险命令），必须逐个确认。";
        }
        else
        {
            SessionCheck.ToolTip = "勾选后，本次运行期间同类操作不再询问。程序重启后自动失效。";
        }

        // 默认焦点给「拒绝」，避免手快按回车就放行
        Loaded += (_, _) => DenyButton.Focus();
    }

    /// <summary>
    /// 口令闸门（审批档位 4：敏感文件凭密码放行）。
    ///
    /// <para>
    /// 为什么是「口令」而不是「再点一次允许」：用户在档位 4 里表达的是
    /// 「别每写一次 .env 都弹窗，但我要求这个动作仍然有人为它负责」。
    /// 口令把「随手点允许」这个廉价动作换成了「必须知道一个不该被模型知道的东西」——
    /// 而模型确实不知道它：口令只在宿主进程里，从不进入提示词、不进工具结果。
    /// 所以模型无法自我授权。
    /// </para>
    ///
    /// <para>
    /// 口令来源：<c>FATFISH_SENSITIVE_PASSWORD</c>，未配置时用约定值 <c>183461</c>。
    /// ★ 明文比较 <c>string.Equals(..., StringComparison.Ordinal)</c> 在这里可以接受：
    ///   它是本机 GUI 的一次性输入框，不是网络接口，没有密码学意义上的时序侧信道可利用；
    ///   真正的防线是「模型拿不到口令」，而不是「口令比对是否恒定时间」。
    /// </para>
    /// </summary>
    private StackPanel BuildPasswordGate(ApprovalRequest request)
    {
        string expected = Environment.GetEnvironmentVariable("FATFISH_SENSITIVE_PASSWORD") is { Length: > 0 } custom
            ? custom
            : "183461";

        var box = new PasswordBox
        {
            Height = 26,
            Margin = new Thickness(0, 10, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var hint = new TextBlock
        {
            Text = "口令不正确，再试一次。",
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        // ★ 资源引用而不是 Brushes.Firebrick：写死的色在夜间模式下仍然是那个色
        //   （用户报「审批没改」，这类写死的地方就是元凶之一）。Danger 两套主题都有值。
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Danger");

        var okButton = new Button
        {
            Content = "确认",
            Width = 84,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };

        var cancelButton = new Button
        {
            Content = "取消",
            Width = 84,
            Height = 28,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel { Margin = new Thickness(18), Width = 420 };

        panel.Children.Add(new TextBlock
        {
            Text = "🔑 敏感文件 · 需要口令",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
        });

        // ★ 同上：DimGray 写死的话，夜间模式下这段说明就糊在黑底上（#696969 / #2A2A2A）。
        var reason = new TextBlock
        {
            Text = request.Reason + "\n动作：" + request.Tool,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        reason.SetResourceReference(TextBlock.ForegroundProperty, "FgMuted");
        panel.Children.Add(reason);

        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);

        okButton.Click += (_, _) =>
        {
            if (string.Equals(box.Password, expected, StringComparison.Ordinal))
            {
                Approved = true;
                AllowForSession = false;      // 口令只放行这一次，不累积成「本会话全放行」
                DialogResult = true;
                Close();
            }
            else
            {
                hint.Visibility = Visibility.Visible;
                box.Clear();
                box.Focus();
            }
        };

        cancelButton.Click += (_, _) =>
        {
            Approved = false;
            AllowForSession = false;
            DialogResult = false;
            Close();
        };

        Loaded += (_, _) => box.Focus();

        return panel;
    }

    private static string FormatArgs(IReadOnlyDictionary<string, JsonElement> args)
    {
        if (args.Count == 0)
            return "{}";

        try
        {
            var plain = new Dictionary<string, JsonElement>(args.Count, StringComparer.Ordinal);
            foreach (var (k, v) in args)
                plain[k] = v;

            return JsonSerializer.Serialize(plain, ArgsFormat);
        }
        catch (JsonException)
        {
            // 序列化失败也要让用户看到点什么，否则等于「让你确认但不说是什么」
            return string.Join(
                Environment.NewLine,
                args.Select(kv => $"{kv.Key}: {kv.Value.GetRawText()}"));
        }
    }

    private void OnAllow(object sender, RoutedEventArgs e)
    {
        Approved = true;
        AllowForSession = SessionCheck.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void OnDeny(object sender, RoutedEventArgs e)
    {
        Approved = false;
        AllowForSession = false;
        DialogResult = false;
        Close();
    }
}

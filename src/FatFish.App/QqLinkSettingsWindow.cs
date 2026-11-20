using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using FatFish.App.Services;

namespace FatFish.App;

/// <summary>
/// QQ 桥接设置窗口：白名单（用户 / 群）、行为提示词、权限档。
///
/// <para>
/// <b>为什么纯代码构建、不写 XAML：</b>这个窗口只有五个控件，XAML 版会多出一个
/// <c>x:Class</c> 文件、一处 <c>InitializeComponent</c>、以及一份「改了代码忘了改 xaml」
/// 的可能性。控件一多（比如插件字段动态渲染）才值得上 XAML。
/// </para>
///
/// <para>
/// 保存后立刻调 <paramref name="onSaved"/>：服务侧会重读 json，**不需要重启程序**，
/// 下一条 QQ 消息就按新设置判定。
/// </para>
/// </summary>
public sealed class QqLinkSettingsWindow : Window
{
    private readonly string _root;
    private readonly Action? _onSaved;

    private readonly TextBox _users = new();
    private readonly TextBox _groups = new();
    private readonly TextBox _prompt = new();
    private readonly ComboBox _permission = new();

    public QqLinkSettingsWindow(string root, QqLinkSettings current, Action? onSaved)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _onSaved = onSaved;

        Title = "QQ 桥接设置";
        Width = 680;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _users.Text = string.Join(Environment.NewLine, current.AllowUsers);
        _groups.Text = string.Join(Environment.NewLine, current.AllowGroups);
        _prompt.Text = current.Prompt;

        Content = BuildLayout(current);
    }

    private UIElement BuildLayout(QqLinkSettings current)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(Heading("允许的用户（QQ 号，一行一个）"));
        panel.Children.Add(Hint("私聊只有这些人能用；群里也会要求发言人在这份名单里。留空 = 不限制。"));
        panel.Children.Add(Multiline(_users, 100));

        panel.Children.Add(Heading("允许的群（群号，一行一个）"));
        panel.Children.Add(Hint("留空 = 所有群都响应。填了就只响应这些群。"));
        panel.Children.Add(Multiline(_groups, 80));

        panel.Children.Add(Heading("行为提示词"));
        panel.Children.Add(Hint("每轮都会拼进给模型的提醒，例如「在群里要热情一点、主动接话，但别刷屏」。"));
        panel.Children.Add(Multiline(_prompt, 140));

        panel.Children.Add(Heading("权限档"));
        panel.Children.Add(Hint("与审批三档一致：只读 / 默认（现状）/ 高（审批自动放行，慎用）。"));

        int selected = current.PermissionLevel switch
        {
            QqPermission.Low => 0,
            QqPermission.High => 2,
            _ => 1,
        };

        foreach ((string tag, string label) in new[]
                 {
                     ("low", "低 —— 只读，不做任何写操作"),
                     ("normal", "默认 —— 单文件可逆改动可做，删除/批量/执行到电脑上"),
                     ("high", "高 —— 审批自动放行（等于把 QQ 当终端，慎用）"),
                 })
        {
            _permission.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        }

        _permission.SelectedIndex = selected;
        _permission.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(_permission);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var cancel = new Button { Content = "取消", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();

        var save = new Button { Content = "保存", Width = 90, IsDefault = true };
        save.Click += (_, _) => SaveAndClose();

        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        panel.Children.Add(buttons);

        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 10, 0, 2),
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = System.Windows.Media.Brushes.Gray,
        FontSize = 11,
        Margin = new Thickness(0, 0, 0, 4),
    };

    private static TextBox Multiline(TextBox box, double height)
    {
        box.AcceptsReturn = true;
        box.TextWrapping = TextWrapping.Wrap;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        box.Height = height;
        box.Margin = new Thickness(0, 0, 0, 6);
        box.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        return box;
    }

    /// <summary>按行 + 逗号 + 分号切分，去空白、去重、保序。</summary>
    private static IReadOnlyList<string> SplitIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();

        foreach (string raw in text.Split(
                     new[] { '\r', '\n', ',', '，', ';', '；', ' ', '\t' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string value = raw.Trim();

            if (value.Length > 0 && seen.Add(value))
                list.Add(value);
        }

        return list;
    }

    private void SaveAndClose()
    {
        try
        {
            var settings = new QqLinkSettings
            {
                AllowUsers = SplitIds(_users.Text),
                AllowGroups = SplitIds(_groups.Text),
                Prompt = _prompt.Text.Trim(),
                Permission = (_permission.SelectedItem as ComboBoxItem)?.Tag as string ?? "normal",
            };

            settings.Save(_root);
            _onSaved?.Invoke();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "保存失败：" + ex.GetType().Name + " - " + ex.Message,
                "QQ 桥接设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

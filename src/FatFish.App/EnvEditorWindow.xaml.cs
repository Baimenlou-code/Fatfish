using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FatFish.Core.Config;

namespace FatFish.App;

/// <summary>
/// .env 可视化编辑器（代码后置）。
///
/// <b>本类的定位：把控件摆出来、把输入收集起来。</b>
/// 解析、校验、写回、备份、体检全部在 <see cref="FatFish.Core.Config"/> 里，
/// 且有单测覆盖。窗口里的规则一旦多起来就没人测得了 ——
/// 「界面上填了、程序不认」这类问题都是这么长出来的。
///
/// <b>三条贯穿全窗口的规则：</b>
///   1. <b>没改就不动</b>：保存时只写「当前值 ≠ 打开时的值」那些键。
///      其余行原样写回（连注释、空行、<c>export</c> 前缀、引号风格都不动）。
///      这是 <see cref="EnvDocument"/> 保证的，本窗口只负责别乱调 Set。
///   2. <b>留空 = 删掉这个键</b>：这些配置的读取方全都把空值当作「没配」，
///      所以删掉与写成空是等价的，而删掉更不容易让人以为「我配了但没生效」。
///   3. <b>保存必先备份</b>：.env 是程序能不能启动的前提，
///      写坏了就没有界面可用来改回来了。
/// </summary>
public partial class EnvEditorWindow : Window
{
    private readonly string _path;
    private readonly string _backupDir;

    /// <summary>打开时的键值快照。保存时用它判断「哪些真的被改了」。</summary>
    private Dictionary<string, string?> _original = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>表单里各键的当前文本（用户敲进去的，还没保存）。</summary>
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<FieldRow> _rows = new();
    private readonly List<SecretPair> _secrets = new();
    private readonly List<GroupBlock> _groups = new();

    /// <summary>搜索过滤用的单元：一个容器 + 它的可搜索文本。</summary>
    private readonly List<(FrameworkElement Container, string Text)> _searchUnits = new();

    private long _fingerprint;
    private bool _dirty;
    private bool _syncing;
    private bool _suppressEvents;

    /// <summary>用户点了「保存并重启」。主窗口读这个标志决定要不要重启。</summary>
    public bool RestartRequested { get; private set; }

    public EnvEditorWindow(string envPath, string? backupDirectory = null)
    {
        InitializeComponent();

        _path = Path.GetFullPath(envPath);
        _backupDir = backupDirectory ?? Path.Combine(Path.GetDirectoryName(_path) ?? ".", "_backup");

        PathText.Text = _path;

        SearchBox.TextChanged += (_, _) => ApplyFilter();
        RevealCheck.Checked += (_, _) => ApplyReveal(true);
        RevealCheck.Unchecked += (_, _) => ApplyReveal(false);
        AdvancedCheck.Checked += (_, _) => RebuildForm();
        AdvancedCheck.Unchecked += (_, _) => RebuildForm();

        Closing += OnClosing;

        LoadFromDisk();
    }

    // ==================================================================
    // 载入
    // ==================================================================

    private void LoadFromDisk()
    {
        var doc = EnvDocument.Load(_path);

        _original = doc.Snapshot()
            .ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.OrdinalIgnoreCase);
        _values.Clear();

        foreach (var (k, v) in _original)
            _values[k] = v;

        _fingerprint = Fingerprint(_path);
        _dirty = false;

        RebuildForm();
        RefreshIssues(doc);
        RefreshRawPreview();

        StatusText.Text = File.Exists(_path)
            ? $"已载入 {_original.Count} 个键。改完点「保存」，然后重启程序生效。"
            : "文件还不存在，保存时会新建一份。";

        UpdateTitle();
    }

    // ==================================================================
    // 表单构建
    // ==================================================================

    private void RebuildForm()
    {
        // 重建时先把用户已经敲进去的值抓一遍，否则切「高级项」会把输入清空
        CaptureCurrentValues(includeCustom: false);

        _suppressEvents = true;

        try
        {
            FormHost.Children.Clear();
            _rows.Clear();
            _secrets.Clear();
            _groups.Clear();
            _searchUnits.Clear();

            var known = EnvSchema.Fields
                .Select(f => f.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var unknownKeys = _original.Keys
                .Where(k => !known.Contains(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var showAdvanced = AdvancedCheck.IsChecked == true;

            foreach (var group in EnvSchema.Groups)
            {
                var bodies = new List<FrameworkElement>();

                foreach (var field in EnvSchema.InGroup(group.Name))
                {
                    if (field.Advanced && !showAdvanced)
                        continue;

                    bodies.Add(BuildFieldRow(field));
                }

                if (group.Name == EnvSchema.GroupOther)
                {
                    foreach (var key in unknownKeys)
                        bodies.Add(BuildUnknownRow(key));

                    foreach (var kv in _values
                                 .Where(kv => !known.Contains(kv.Key))
                                 .Select(kv => kv.Key)
                                 .Where(k => !unknownKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToList())
                    {
                        // 上一轮「添加自定义键」留下来、还没保存的键
                        unknownKeys.Add(kv);
                        bodies.Add(BuildUnknownRow(kv));
                    }

                    bodies.Add(BuildAddCustomButton());
                }

                if (bodies.Count == 0)
                    continue;

                FormHost.Children.Add(BuildSectionHeader(group, bodies));
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        ApplyFilter();
    }

    /// <summary>分组标题 + 组内所有行。标题里带「本组有几个必填项没填」这种有用信息。</summary>
    private StackPanel BuildSectionHeader(EnvGroup group, List<FrameworkElement> bodies)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 10, 0, 2) };

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var caption = new TextBlock
        {
            Text = group.Caption,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = B("FgPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = group.Summary,
        };

        var summary = new TextBlock
        {
            Text = group.Summary,
            FontSize = 10.5,
            Foreground = B("FgMuted"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        Grid.SetColumn(caption, 0);
        Grid.SetColumn(summary, 1);
        grid.Children.Add(caption);
        grid.Children.Add(summary);

        var line = new Border
        {
            Height = 1,
            Background = B("Line"),
            Margin = new Thickness(0, 2, 0, 4),
        };

        wrap.Children.Add(grid);
        wrap.Children.Add(line);

        foreach (var b in bodies)
            wrap.Children.Add(b);

        // 分组本身不进搜索单元：搜索命中的是「某一行」，
        // 而整组是否显示由「组里有没有命中」决定（见 ApplyFilter）。
        _groups.Add(new GroupBlock(wrap, bodies));

        return wrap;
    }

    private Grid BuildFieldRow(EnvField field)
    {
        var value = _values.TryGetValue(field.Key, out var v) ? v : null;

        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };

        left.Children.Add(new TextBlock
        {
            Text = field.Label,
            FontSize = 11.5,
            Foreground = B("FgPrimary"),
            TextWrapping = TextWrapping.Wrap,
            ToolTip = field.Required ? "必填项：留空会导致程序起不来或功能不可用。" : null,
        });

        left.Children.Add(new TextBlock
        {
            Text = field.Key,
            FontFamily = F("MonoFont"),
            FontSize = 10,
            Foreground = B("FgMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "键名：程序就是按这个名字读配置的。" +
                      (field.Aliases.Count > 0
                          ? "\n也认旧写法：" + string.Join(" / ", field.Aliases)
                          : ""),
        });

        var right = new StackPanel();

        var (editor, read) = CreateEditor(field, value ?? string.Empty);
        right.Children.Add(editor);

        if (!string.IsNullOrEmpty(field.Help))
        {
            right.Children.Add(new TextBlock
            {
                Text = field.Help,
                FontSize = 10.5,
                Foreground = B("FgMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        if (!string.IsNullOrEmpty(field.Warning))
        {
            right.Children.Add(new TextBlock
            {
                Text = "⚠ " + field.Warning,
                FontSize = 10.5,
                Foreground = B("Warn"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        if (field.Default is not null)
        {
            right.Children.Add(new TextBlock
            {
                Text = "留空即使用默认值：" + field.Default,
                FontSize = 10,
                Foreground = B("FgMuted"),
                Margin = new Thickness(0, 2, 0, 0),
                ToolTip = "这里的默认值是从代码里抄的（LlmOptions / VerifyOptions / NetOptions）。" +
                          "\n留空时程序用的就是它 —— 不是猜的。",
            });
        }

        var error = new TextBlock
        {
            Foreground = B("Danger"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        right.Children.Add(error);

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);

        _rows.Add(new FieldRow(field, grid, read, error));

        _searchUnits.Add((grid, field.Key + " " + field.Label + " " + field.Help + " " + field.Group));

        return grid;
    }

    /// <summary>不在已知清单里的键：仍然可以改，只是没有专门的编辑器和说明。</summary>
    private Grid BuildUnknownRow(string key)
    {
        var field = new EnvField
        {
            Key = key,
            Label = key,
            Group = EnvSchema.GroupOther,
            Kind = EnvFieldKind.Text,
            Help = "这一项不在已知清单里：可能是插件在用，也可能是旧版本留下的。" +
                   "本窗口不认识它，但**不会删它** —— 你改了才写，不改就原样留着。",
        };

        var value = _values.TryGetValue(key, out var v) ? v : null;

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = key,
            FontFamily = F("MonoFont"),
            FontSize = 11,
            Foreground = B("FgPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "自定义键：" + key + "\n键名区分大小写吗？不区分 —— 环境变量在 Windows 上不分大小写。",
        };

        var right = new StackPanel();

        var box = new TextBox
        {
            Text = value ?? string.Empty,
            MinHeight = 26,
            FontFamily = F("MonoFont"),
            FontSize = 11.5,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        box.TextChanged += (_, _) =>
        {
            if (_suppressEvents)
                return;

            _values[key] = box.Text;
            MarkDirty();
        };

        var err = new TextBlock
        {
            Foreground = B("Danger"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        right.Children.Add(box);
        right.Children.Add(err);

        Grid.SetColumn(label, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(label);
        grid.Children.Add(right);

        _rows.Add(new FieldRow(field, grid, () => box.Text, err));

        _searchUnits.Add((grid, key + " " + field.Help));

        return grid;
    }

    private Button BuildAddCustomButton()
    {
        var btn = new Button
        {
            Content = "＋ 添加自定义键",
            MinHeight = 26,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 6, 0, 2),
            FontSize = 11,
            ToolTip = "加一个不在清单里的键（键名只能是字母/数字/下划线，不以数字开头）。" +
                      "\n典型用途：给插件加它自己的配置、或保留一段旧版脚本还在读的键。" +
                      "\n加完之后仍然要点「保存」才会写进文件。",
        };

        btn.Click += (_, _) =>
        {
            var name = "NEW_KEY";
            var i = 2;

            while (_values.ContainsKey(name))
                name = "NEW_KEY_" + i++;

            _values[name] = string.Empty;
            RebuildForm();

            StatusText.Text = "已加了一行「" + name + "」：把左边的键改成你要的名字，右边填值，然后保存。";
        };

        return btn;
    }

    // ==================================================================
    // 各类型的编辑器
    // ==================================================================

    private (FrameworkElement Ui, Func<string> Read) CreateEditor(EnvField field, string value)
    {
        switch (field.Kind)
        {
            case EnvFieldKind.Secret:
                return CreateSecretEditor(field, value);

            case EnvFieldKind.Bool:
                return CreateBoolEditor(field, value);

            case EnvFieldKind.Choice:
                return CreateChoiceEditor(field, value);

            default:
            {
                var mono = field.Kind is EnvFieldKind.Url;

                var box = new TextBox
                {
                    Text = value,
                    MinHeight = 26,
                    FontFamily = mono ? F("MonoFont") : F("UiFont"),
                    FontSize = 11.5,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };

                if (value.Length == 0 && !string.IsNullOrEmpty(field.Placeholder))
                {
                    box.ToolTip = "示例：" + field.Placeholder;
                }

                box.TextChanged += (_, _) =>
                {
                    if (_suppressEvents)
                        return;

                    _values[field.Key] = box.Text;
                    MarkDirty();
                };

                return (box, () => box.Text);
            }
        }
    }

    /// <summary>
    /// 密钥编辑器：PasswordBox 与 TextBox 叠在同一格里，靠勾选「显示密钥明文」切换。
    /// </summary>
    /// <remarks>
    /// 为什么不用一个 TextBox + 手动打码：那样光标位置、全选、粘贴都会和真实值对不上，
    /// 而密钥最常见的操作恰恰是「整个粘贴进来」—— 打码的输入框在粘贴时最容易出错。
    /// 两个原生控件各自管自己的显示，切换只是 Visibility，不存在半遮半掩的状态。
    /// </remarks>
    private (FrameworkElement Ui, Func<string> Read) CreateSecretEditor(EnvField field, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var password = new PasswordBox
        {
            Password = value,
            MinHeight = 26,
            FontFamily = F("MonoFont"),
            FontSize = 11.5,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "已填内容被遮挡，避免截图/录屏泄露。\n点右边的眼睛可以看明文。",
        };

        var plain = new TextBox
        {
            Text = value,
            MinHeight = 26,
            FontFamily = F("MonoFont"),
            FontSize = 11.5,
            VerticalContentAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        password.PasswordChanged += (_, _) =>
        {
            if (_syncing)
                return;

            _syncing = true;
            plain.Text = password.Password;
            _syncing = false;

            if (_suppressEvents)
                return;

            _values[field.Key] = password.Password;
            MarkDirty();
        };

        plain.TextChanged += (_, _) =>
        {
            if (_syncing)
                return;

            _syncing = true;
            password.Password = plain.Text;
            _syncing = false;

            if (_suppressEvents)
                return;

            _values[field.Key] = plain.Text;
            MarkDirty();
        };

        var eye = new ToggleButton
        {
            Content = "👁",
            Width = 28,
            MinHeight = 26,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "切换这一项的明文/遮挡显示。\n状态不会保存，下次打开窗口又是遮挡的。",
        };

        eye.Checked += (_, _) => ToggleSecret(password, plain, true);
        eye.Unchecked += (_, _) => ToggleSecret(password, plain, false);

        Grid.SetColumn(password, 0);
        Grid.SetColumn(plain, 0);
        Grid.SetColumn(eye, 1);

        grid.Children.Add(password);
        grid.Children.Add(plain);
        grid.Children.Add(eye);

        _secrets.Add(new SecretPair(password, plain, eye));

        return (grid, () => password.Password);
    }

    private static void ToggleSecret(PasswordBox password, TextBox plain, bool reveal)
    {
        password.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        plain.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyReveal(bool reveal)
    {
        foreach (var pair in _secrets)
        {
            pair.Eye.IsChecked = reveal;
            ToggleSecret(pair.Password, pair.Plain, reveal);
        }
    }

    private (FrameworkElement Ui, Func<string> Read) CreateBoolEditor(EnvField field, string value)
    {
        var combo = new ComboBox
        {
            MinHeight = 26,
            FontSize = 11.5,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "三态：留空＝程序用自己的默认值；开＝true；关＝false。\n（.env 里也认 1/0、yes/no、on/off 这些写法，这里按标准写法写回。）",
        };

        combo.Items.Add("未设置（用默认）");
        combo.Items.Add("开（true）");
        combo.Items.Add("关（false）");

        var raw = value.Trim().ToLowerInvariant();

        var index = raw switch
        {
            "" => 0,
            "1" or "true" or "yes" or "on" => 1,
            "0" or "false" or "no" or "off" => 2,
            _ => -1,
        };

        if (index < 0)
        {
            // 认不出来的写法：原样保留，别擅自「修好」它
            combo.Items.Add("原值：" + value);
            combo.SelectedIndex = 3;
        }
        else
        {
            combo.SelectedIndex = index;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (_suppressEvents)
                return;

            _values[field.Key] = combo.SelectedIndex switch
            {
                1 => "true",
                2 => "false",
                3 => value,
                _ => string.Empty,
            };

            MarkDirty();
        };

        return (combo, () => combo.SelectedIndex switch
        {
            1 => "true",
            2 => "false",
            3 => value,
            _ => string.Empty,
        });
    }

    private (FrameworkElement Ui, Func<string> Read) CreateChoiceEditor(EnvField field, string value)
    {
        var combo = new ComboBox
        {
            MinHeight = 26,
            FontSize = 11.5,
            IsEditable = field.AllowCustomChoice,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = field.AllowCustomChoice
                ? "下拉里是常见取值，也可以直接手填（新模型名常常要先填上去试）。"
                : "只能从下拉里选。",
        };

        foreach (var c in field.Choices)
            combo.Items.Add(c);

        combo.Text = value;

        // 不可编辑时 SelectedValue 才有意义；可编辑时以 Text 为准
        if (!field.AllowCustomChoice)
        {
            var idx = -1;

            for (var i = 0; i < combo.Items.Count; i++)
            {
                if (string.Equals(combo.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            combo.SelectedIndex = idx;
        }

        void Commit()
        {
            if (_suppressEvents)
                return;

            _values[field.Key] = combo.Text;
            MarkDirty();
        }

        combo.SelectionChanged += (_, _) => Commit();
        combo.LostFocus += (_, _) => Commit();

        // 可编辑时，输入过程中也要能被「保存」读到最新文本
        combo.AddHandler(TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => Commit()));

        return (combo, () => combo.Text);
    }

    // ==================================================================
    // 校验 / 体检 / 预览
    // ==================================================================

    private void RefreshIssues(EnvDocument doc)
    {
        var issues = EnvHealth.Inspect(doc);

        IssueHost.Children.Clear();

        if (issues.Count == 0)
        {
            IssueHost.Children.Add(new TextBlock
            {
                Text = "✅ 没发现明显问题。",
                FontSize = 11,
                Foreground = B("FgMuted"),
            });

            IssueSummary.Text = "全部正常";
            IssueSummary.Foreground = B("FgMuted");

            return;
        }

        var errors = EnvHealth.Count(issues, EnvIssueLevel.Error);
        var warns = EnvHealth.Count(issues, EnvIssueLevel.Warning);
        var notes = EnvHealth.Count(issues, EnvIssueLevel.Note);

        IssueSummary.Text = $"⛔ {errors} 项错误 · ⚠ {warns} 项提醒 · ℹ {notes} 条说明";
        IssueSummary.Foreground = errors > 0 ? B("Danger") : warns > 0 ? B("Warn") : B("FgMuted");

        foreach (var issue in issues)
        {
            var (icon, brush) = issue.Level switch
            {
                EnvIssueLevel.Error => ("⛔", B("Danger")),
                EnvIssueLevel.Warning => ("⚠", B("Warn")),
                _ => ("ℹ", B("FgMuted")),
            };

            IssueHost.Children.Add(new TextBlock
            {
                Text = icon + " " + issue.Message,
                FontSize = 11,
                Foreground = brush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1),
                ToolTip = issue.Key is null ? null : "相关键：" + issue.Key,
            });
        }
    }

    /// <summary>
    /// 刷新「原文」页签：把当前表单内容套进文档，渲染成保存后的样子。
    /// </summary>
    /// <remarks>
    /// 这里刻意用**真实保存路径**去生成预览（同一份 <see cref="EnvDocument"/> 逻辑），
    /// 而不是另写一个「看起来差不多」的拼装函数 —— 两个实现一定会分叉，
    /// 而分叉的表现是「预览里好好的，保存后注释没了」。
    /// </remarks>
    private void RefreshRawPreview()
    {
        try
        {
            var doc = EnvDocument.Load(_path);

            ApplyValuesTo(doc);

            RawBox.Text = doc.Serialize();
        }
        catch (Exception ex)
        {
            RawBox.Text = "（无法生成预览：" + ex.Message + "）";
        }
    }

    /// <summary>
    /// 把表单里「与打开时不同」的值写进文档对象（不落盘）。
    /// </summary>
    /// <remarks>
    /// 真正的规则在 <see cref="EnvPatch.Apply"/>（Core 里，有单测）。
    /// 窗口这边只负责把「每个控件现在念什么」收集成一个字典 ——
    /// 规则不写在窗口里，是因为窗口代码测不了，而这里写错的代价是「保存一次，注释全丢」。
    /// </remarks>
    private IReadOnlyList<string> ApplyValuesTo(EnvDocument doc)
    {
        var current = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _rows)
            current[row.Field.Key] = row.Read();

        return EnvPatch.Apply(doc, _original, current);
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateTitle();

        // StartsWith(char) 没有 StringComparison 重载；
        // 这里也不需要——单字符比较不存在区域性差异。
        if (!StatusText.Text.StartsWith('●'))
            StatusText.Text = "● 有未保存的修改";
    }

    private void UpdateTitle()
    {
        Title = "配置 — .env" + (_dirty ? "  ●" : string.Empty);
    }

    // ==================================================================
    // 搜索
    // ==================================================================

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();

        SearchHint.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (container, text) in _searchUnits)
        {
            var hit = q.Length == 0 ||
                      text.Contains(q, StringComparison.OrdinalIgnoreCase);

            container.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
        }

        // 分组只在「组内一行都没命中」时隐藏。
        // 反过来的写法（分组也参与文本匹配）会出一个很难查的 bug：
        // 搜「token」时，标题不含 token 的那一整组连人带命中项一起消失。
        foreach (var group in _groups)
        {
            var anyVisible = group.Bodies.Any(b => b.Visibility == Visibility.Visible);

            group.Wrap.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ==================================================================
    // 保存
    // ==================================================================

    private void OnSave(object sender, RoutedEventArgs e) => SaveCore(restart: false);

    private void OnSaveAndRestart(object sender, RoutedEventArgs e) => SaveCore(restart: true);

    private bool SaveCore(bool restart)
    {
        CaptureCurrentValues(includeCustom: true);

        // ---------- 1. 逐项校验 ----------
        var errors = new List<(FieldRow Row, string Message)>();

        foreach (var row in _rows)
        {
            var value = (row.Read() ?? string.Empty).Trim();

            string? err = row.Field.Kind == EnvFieldKind.Text && !EnvValueCodec.IsValidKey(row.Field.Key)
                ? "键名不合法（只能字母/数字/下划线，且不以数字开头）"
                : EnvSchema.Validate(row.Field, value);

            row.Error.Text = err ?? string.Empty;
            row.Error.Visibility = err is null ? Visibility.Collapsed : Visibility.Visible;

            if (err is not null)
                errors.Add((row, row.Field.Label + "：" + err));
        }

        if (errors.Count > 0)
        {
            StatusText.Text = $"⛔ 有 {errors.Count} 项填得不对，已标红：{errors[0].Message}";
            StatusText.Foreground = B("Danger");

            errors[0].Row.Ui.BringIntoView();

            return false;
        }

        // ---------- 2. 文件被外部改过？ ----------
        if (Fingerprint(_path) != _fingerprint)
        {
            var choice = MessageBox.Show(this,
                ".env 在你打开这个窗口之后被别的程序改过了。\n\n" +
                "「是」=  重新载入磁盘上的版本（你在本窗口里的修改会丢）\n" +
                "「否」=  仍然用本窗口的内容覆盖写入\n" +
                "「取消」= 什么都不做，先回去看看",
                "文件已被外部修改",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Cancel)
            {
                StatusText.Text = "已取消保存：文件被外部修改过。";
                return false;
            }

            if (choice == MessageBoxResult.Yes)
            {
                LoadFromDisk();
                StatusText.Text = "已重新载入磁盘上的版本。";
                return false;
            }
        }

        // ---------- 3. 写入 ----------
        try
        {
            var doc = EnvDocument.Load(_path);

            var changed = ApplyValuesTo(doc);

            var backup = doc.Save(_path, _backupDir);

            _fingerprint = Fingerprint(_path);
            _original = doc.Snapshot()
                .ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.OrdinalIgnoreCase);

            _dirty = false;
            UpdateTitle();

            RefreshIssues(doc);
            RefreshRawPreview();

            var what = changed.Count == 0
                ? "内容没有变化，文件按原样保留"
                : $"写入 {changed.Count} 项：{string.Join("、", changed.Take(6))}" +
                  (changed.Count > 6 ? " …" : string.Empty);

            StatusText.Foreground = B("FgMuted");
            StatusText.Text = backup is null
                ? $"✅ 已保存（原本没有文件，这次是新建）。{what}。重启程序后生效。"
                : $"✅ 已保存：{what}。原文件备份在 {backup}  —— 改错了就把它改名回 .env。重启后生效。";

            if (restart)
            {
                RestartRequested = true;
                Close();
            }

            return true;
        }
        catch (Exception ex)
        {
            StatusText.Foreground = B("Danger");
            StatusText.Text = "⛔ 保存失败：" + ex.Message + "（文件没有被改动）";
            return false;
        }
    }

    /// <summary>把表单上的当前值收进 <see cref="_values"/>（供重建表单、切换高级项时保留输入）。</summary>
    private void CaptureCurrentValues(bool includeCustom)
    {
        if (!includeCustom && _rows.Count == 0)
            return;

        foreach (var row in _rows)
        {
            try
            {
                _values[row.Field.Key] = row.Read();
            }
            catch (InvalidOperationException)
            {
                // 控件还没初始化完（WPF 在极早期读 ComboBox.Text 会抛）—— 跳过即可
            }
        }
    }

    // ==================================================================
    // 杂项
    // ==================================================================

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard())
            return;

        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_dirty && !ConfirmDiscard())
            e.Cancel = true;
    }

    private bool ConfirmDiscard()
    {
        if (!_dirty)
            return true;

        var r = MessageBox.Show(this,
            "有未保存的修改。确定放弃吗？\n\n磁盘上的 .env 不会被改动。",
            "放弃修改",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return r == MessageBoxResult.Yes;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;

            if (!File.Exists(_path))
            {
                StatusText.Text = "文件还不存在，已打开它应该在的目录：" + dir;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + _path + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "打开目录失败：" + ex.Message;
        }
    }

    private void OnCopyRaw(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(RawBox.Text);
            StatusText.Text = "已把预览内容复制到剪贴板。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 文件的「指纹」：大小 + 最后修改时间。
    /// </summary>
    /// <remarks>
    /// 不读内容做哈希：这个文件通常只有几 KB，但也可能被人塞了几百行注释；
    /// 而且这里的目的只是「察觉有人动过」，不是做完整性校验。
    /// 大小 + 时间戳的漏判窗口极窄，代价却几乎没有。
    /// </remarks>
    private static long Fingerprint(string path)
    {
        try
        {
            if (!File.Exists(path))
                return 0;

            var info = new FileInfo(path);

            return info.Length * 1_000_000 + info.LastWriteTimeUtc.Ticks % 1_000_000;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    // ==================================================================
    // 小的辅助类型
    // ==================================================================

    private Brush B(string key) => (Brush)FindResource(key);

    private FontFamily F(string key) => (FontFamily)FindResource(key);

    /// <summary>表单里的一行：定义 + 容器 + 读取函数 + 错误提示。</summary>
    private sealed record FieldRow(EnvField Field, FrameworkElement Ui, Func<string> Read, TextBlock Error);

    private sealed record SecretPair(PasswordBox Password, TextBox Plain, ToggleButton Eye);

    /// <summary>一个分组：外层容器 + 组内各行（搜索时按「组里有没有命中」决定整组显隐）。</summary>
    private sealed record GroupBlock(StackPanel Wrap, IReadOnlyList<FrameworkElement> Bodies);
}

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace FatFish.App.ViewModels;

/// <summary>
/// 一条消息。
///
/// 对应 Python 版 chat_window.py 里「气泡」的数据部分。
/// 刻意保持极简：渲染相关的东西（颜色、圆角、对齐）全部交给 XAML 模板，
/// 这样换主题不需要动 C# 代码。
/// </summary>
public sealed class MessageItem : INotifyPropertyChanged
{
    private string _text;

    public MessageItem(string id, string text, string kind = "ai")
    {
        Id = id;
        _text = text;
        Kind = kind;
        CreatedAt = DateTime.Now;
    }

    public string Id { get; }

    /// <summary>消息类型：user / ai / system。模板可据此换配色。</summary>
    public string Kind { get; }

    /// <summary>产生时间。CE 风格的日志面板每行都带时间戳，消息行也保持一致。</summary>
    public DateTime CreatedAt { get; }

    /// <summary>形如 18:02:13。11px 灰色小字，靠右显示在消息头。</summary>
    /// <remarks>
    /// 固定用 InvariantCulture：这里要的是「时:分:秒」的机器格式，
    /// 不能因为系统区域设置变成别的分隔符（有的区域用「.」）。
    /// </remarks>
    public string TimeText => CreatedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>显示名。放在消息头左侧，粗体。</summary>
    public string NameText => Kind switch
    {
        "user" => "你",
        "system" => "系统",
        _ => "肥鱼",
    };


    /// <summary>正文。流式期间会高频变化，所以必须发通知。</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;

            _text = value;
            OnPropertyChanged();
        }
    }

    /// <summary>用于流式追加的缓冲区。</summary>
    /// <remarks>
    /// 为什么不直接写 <c>Text += delta</c>：
    ///   string 是不可变的，每次 += 都要重新分配并拷贝整个字符串。
    ///   一条 3000 字的回复拆成 300 个分片，累积拷贝量是 O(n²)。
    ///   这里用 StringBuilder 累积，只在需要通知界面时才 ToString() 一次。
    ///   虽然每次仍是全量赋值，但省掉了中间的分配风暴。
    /// </remarks>
    private readonly StringBuilder _buffer = new();

    /// <summary>追加流式文本。</summary>
    public void AppendText(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return;

        _buffer.Append(delta);
        Text = _buffer.ToString();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

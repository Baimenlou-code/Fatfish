using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace FatFish.App.ViewModels;

/// <summary>
/// 底部「工具」页的一条记录。
///
/// 为什么单独建类型而不是拼字符串：
///   工具调用有「开始」和「结束」两个时刻，中间隔了几百毫秒到几秒。
///   如果用字符串，结束时要回头去找并替换那一行，很别扭；
///   用可通知对象，结束时直接改属性，界面自己就刷新了。
/// </summary>
public sealed class ToolLogItem : INotifyPropertyChanged
{
    private string _state = "运行中";
    private string _detail = "";
    private long _elapsedMs;
    private bool _ok;

    public ToolLogItem(string toolName, string arguments)
    {
        ToolName = toolName;
        Arguments = arguments;
        CreatedAt = DateTime.Now;
    }

    public string ToolName { get; }

    /// <summary>模型传进来的原始参数（单行化后的 JSON）。</summary>
    public string Arguments { get; }

    public DateTime CreatedAt { get; }

    public string TimeText => CreatedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public bool Ok
    {
        get => _ok;
        private set => Set(ref _ok, value);
    }

    /// <summary>运行中 / 成功 / 失败。</summary>
    public string State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    /// <summary>耗时或错误信息。</summary>
    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public long ElapsedMs
    {
        get => _elapsedMs;
        private set => Set(ref _elapsedMs, value);
    }

    /// <summary>调用成功。</summary>
    public void Finish(long elapsedMs, string? summary = null)
    {
        Ok = true;
        ElapsedMs = elapsedMs;
        State = "成功";
        Detail = summary is { Length: > 0 } ? $"{elapsedMs}ms · {summary}" : $"{elapsedMs}ms";
    }

    /// <summary>调用失败。</summary>
    public void Fail(string error, long elapsedMs = 0)
    {
        Ok = false;
        ElapsedMs = elapsedMs;
        State = "失败";
        Detail = error;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

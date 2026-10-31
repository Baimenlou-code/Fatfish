using System.Windows;

namespace FatFish.App;

/// <summary>
/// 配置编辑器窗口的「尺寸自适应」（与 <c>EnvEditorWindow.xaml.cs</c> 同一个 partial 类）。
/// </summary>
/// <remarks>
/// <b>为什么要有这个文件：</b>
/// 用户反馈「留给可编辑的栏目不够宽」，于是把窗口默认尺寸从 1000×720 放大到 1360×860 ——
/// 输入框宽度随之从约 630px 变成约 1100px（实测，见 docs/EnvEditor.md 第九节）。
///
/// 但一个**写死的**大默认值会在小屏上变成新的问题：1360×860 放到 1366×728 的工作区里，
/// 右边缘和下边缘都会伸出屏幕外 —— 用户反而看不全、也抓不到那一侧。
/// 所以这里在窗口 Loaded 时（有工作区信息了）做一次「只缩不放」的修正：
/// <list type="bullet">
///   <item>大屏：什么都不动，保持放大的默认尺寸；</item>
///   <item>小屏：缩到工作区以内，并把位置夹回去，保证标题栏和四个边都在屏幕里；</item>
///   <item>绝不放大 —— 窗口比默认值还小时说明用户自己调过，不该被强行拉大。</item>
/// </list>
///
/// 为什么不复用 <c>FatFish.Core.Host.WindowPlacementStore</c>：那套逻辑是给主窗口「记住位置」
/// 用的（读写 logs/ui/window.json、跨显示器夹取）。本窗口目前不需要记忆，
/// 只需要一次性的「放得下」，引进来反而要往那个有单测的文件上加参数。
/// </remarks>
public partial class EnvEditorWindow
{
    /// <summary>窗口四周至少留出的空白（DIP），别贴着屏幕边缘。</summary>
    private const double ScreenMargin = 40;

    /// <summary>Loaded 时调用：把窗口夹进当前工作区。</summary>
    private void OnPlacementLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            FitIntoWorkArea();
        }
        catch (Exception)
        {
            // 尺寸修正失败绝不该阻止窗口打开 —— 大不了就是默认尺寸
        }
    }

    /// <summary>只缩不放地夹进工作区，并保持整窗可见。</summary>
    private void FitIntoWorkArea()
    {
        var area = SystemParameters.WorkArea;   // 已是 DIP，且扣掉了任务栏

        if (area.Width <= 0 || area.Height <= 0)
            return;

        // ★ 下限用 MinWidth / MinHeight 而不是 0：宁可略微超出，也不要缩到控件挤成一团。
        var maxWidth = Math.Max(MinWidth, area.Width - ScreenMargin * 2);
        var maxHeight = Math.Max(MinHeight, area.Height - ScreenMargin * 2);

        if (Width > maxWidth)
            Width = maxWidth;

        if (Height > maxHeight)
            Height = maxHeight;

        // 改完尺寸后把窗口推回工作区内：CenterOwner 是按「改之前」的尺寸算的位置，
        // 不推的话右下角会露到屏幕外。
        Left = Math.Clamp(Left, area.Left + 8, Math.Max(area.Left + 8, area.Right - Width - 8));
        Top = Math.Clamp(Top, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - Height - 8));
    }
}

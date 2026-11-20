namespace FatFish.App;

/// <summary>
/// 各滚动框的「跟随最新」入口（2026-11-06）。
/// </summary>
/// <remarks>
/// ★ 字段放这里而不是各自的 partial：几个框的跟随状态互不相干，
///   集中一处便于看出「哪些框开了跟随、哪些还没」。
/// ★ 惰性挂载：滚动容器要等模板应用后才存在，所以第一次调用可能取不到 ——
///   但对象照样建出来（列表靠 ScrollIntoView，不依赖容器），
///   容器缺失或掉线由 ScrollTail.ToEnd 自己重试绑定。
/// </remarks>
public partial class MainWindow
{
    private ScrollTail? _processTail;
    private ScrollTail? _subTalkTail;
    private ScrollTail? _subTraceTail;
    private ScrollTail? _logTail;
    private ScrollTail? _toolTail;

    private void FollowProcess() => (_processTail ??= ScrollTail.Attach(_processScroll))?.ToEnd();

    private void FollowSubTalk() => (_subTalkTail ??= ScrollTail.Attach(SubTalkBox))?.ToEnd();

    private void FollowSubTrace() => (_subTraceTail ??= ScrollTail.Attach(SubTraceBox))?.ToEnd();

    /// <summary>日志列表跟随最新（2026-11-06 补）。</summary>
    private void FollowLog() => (_logTail ??= ScrollTail.Attach(LogList))?.ToEnd();

    /// <summary>工具调用列表跟随最新（2026-11-06 补）。</summary>
    private void FollowTool() => (_toolTail ??= ScrollTail.Attach(ToolList))?.ToEnd();
}

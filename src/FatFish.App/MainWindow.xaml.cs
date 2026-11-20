using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FatFish.App.Services;
using FatFish.App.Theme;
using FatFish.App.ViewModels;
using FatFish.Core.Agent;
using FatFish.Core.Config;
using FatFish.Core.Diagnostics;
using FatFish.Core.Host;
using FatFish.Core.Llm;
using FatFish.Core.Net;
using FatFish.Core.Plugins;
using FatFish.Core.Safety;
using FatFish.Core.Session;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Web;
using FatFish.Core.Tools.Workspace;

namespace FatFish.App;

/// <summary>
/// 主窗口代码后置。
///
/// 本版（批次 1）职责：
///   · 装配内核：PathGuard / ReadTicket / ApprovalPolicy / ToolRegistry / AgentLoop
///   · 加载 .env，创建 DeepSeek 客户端
///   · 把用户输入交给 AgentLoop，在后台线程跑「模型 ↔ 工具」闭环
///   · 通过 UiDispatcher 把流式文本、工具事件、审批请求搬回 UI 线程
///
/// 线程模型（很重要，改代码前务必理解）：
///   ┌─────────────┐   Post(UiEvent)    ┌──────────────┐  Dispatcher.Invoke  ┌──────┐
///   │ AgentLoop   │ ─────────────────► │ UiDispatcher │ ──────────────────► │ 界面 │
///   │ 后台线程     │   （无锁 Channel）  │   泵线程      │    （批量出队）      │ 线程 │
///   └─────────────┘                    └──────────────┘                     └──────┘
///   审批则是反向的：后台线程 await TaskCompletionSource，UI 线程弹窗后 SetResult。
///
/// ★ 命名注意：本类有 StatusText 属性，所以 XAML 里状态栏那个 TextBlock
///   绝对不能写 x:Name="StatusText"（会与属性同名的字段冲突，报 CS0102）。
/// </summary>
public partial class MainWindow : Window, INotifyPropertyChanged, IAgentObserver
{
    private readonly ObservableCollection<MessageItem> _messages = new();

    /// <summary>底部「日志」页签的行。纯字符串，超过 400 行裁剪最早的。</summary>
    private readonly ObservableCollection<string> _logs = new();

    /// <summary>底部「工具调用」页签的行。</summary>
    private readonly ObservableCollection<ToolLogItem> _toolLog = new();

    /// <summary>
    /// 已经开始、还没回填结果的工具调用。
    /// 键用工具名：编排层是串行调用同一批工具，同名并发几乎不会发生。
    /// </summary>
    private readonly Dictionary<string, ToolLogItem> _pendingTools = new();

    private UiDispatcher? _ui;
    private ToolRegistry? _registry;
    private PathGuard? _guard;
    private WorkspaceSession? _workspaceSession;

    /// <summary>
    /// 车间登记簿（库房 + 若干临时车间）。左栏「车间」清单与五个 ws_site_* 工具共用这一份，
    /// 所以必须挂在字段上，不能只在 BootKernel 里当局部变量用。
    /// </summary>
    private WorkspaceSites? _sites;
    private ToolContext? _context;
    private ApprovalPolicy? _policy;
    private DeepSeekClient? _llm;
    private AgentLoop? _agent;
    private BackgroundJobManager? _jobs;
    private NetService? _net;

    /// <summary>插件宿主（自适应装载 / 热重载）。为 null 表示本窗口没启用插件。</summary>
    private PluginHost? _plugins;

    /// <summary>双人核验：第二位 AI 审查员（一道闸），人工报批是另一道。</summary>
    private VerifierService? _verifier;

    private VerifySession? _verify;
    private VerifyOptions? _verifyOptions;

    /// <summary>用户原声记录 —— 送审材料里唯一「模型无法伪造」的一段。</summary>
    private UserEventLog? _userEvents;

    /// <summary>
    /// 联网用量台账。热重载会重建 NetService，但台账必须复用 ——
    /// 换一个实例等于把本进程已花的钱从账上抹掉，而它正是用来对账的。
    /// </summary>
    private UsageLedger? _ledger;

    /// <summary>
    /// 会话落盘。为 null 表示落盘不可用 —— 只影响「能否恢复上次对话」，不影响当前对话。
    /// </summary>
    private SessionStore? _session;

    private CancellationTokenSource? _runCts;

    /// <summary>
    /// 当前正在流式写入的气泡 id。
    /// 由后台线程（Observer 回调）写入，UI 线程（EndStream）读取并清空，
    /// 所以标 volatile：赋值本身是原子的，但需要保证跨线程可见性。
    /// </summary>
    private volatile string? _streamId;

    // ------------------------------------------------------------------
    // 流式增量：即时落地，不再「攒着按节拍放」
    //
    // 2026-11-04 重写。上一版为了掩盖「每写一次控件都要整段重排」的成本，
    // 加了打字机节拍（16ms 一帧、每帧最多 24 字、积压多时封顶 200ms）——
    // 那是拿延迟去粉饰成本，方向错了：要的是「字到了就出现」，不是「匀速但更慢」。
    //
    // 现在只有一条规则：**有分片就立刻画**。
    //   · 同一批事件（UiDispatcher 一次批量出队）里的分片先拼成一段，只写一次控件；
    //   · 写控件走 TextBox.AppendText（增量追加）而不是 Text=全文（整段重建）；
    //   · 让每次追加真正变便宜的，是 MessageList 打开了虚拟化
    //     （见 MainWindow.xaml 里那段 ★ 注释）——否则每追加一个字都要为整段对话重排。
    // ------------------------------------------------------------------
    private readonly System.Text.StringBuilder _pendingDelta = new();

    private string? _pendingDeltaId;

    /// <summary>已经排了一次「立刻画」，不重复排队（同一批分片因此合并成一次）。</summary>
    private bool _flushQueued;

    /// <summary>消息 id → 正文 TextBox。流式时直接写控件，绕开绑定与整段重建。</summary>
    private readonly Dictionary<string, TextBox> _bodyBoxes = new();

    private ScrollViewer? _msgScroller;


    private int _streamSeq;

    private string _workspaceText = "未加载";
    private string _kernelStatus = "初始化中…";
    private string _statusText = "就绪";
    private string _toolCountText = string.Empty;
    private string _sendButtonText = "发送";
    private string _modelText = string.Empty;
    private string _usageText = "—";
    private string _messageCountText = "无记录";
    private string _balanceText = "余额 —";

    /// <summary>阅读外观（正文字号 / 段间距）。改动即落盘，见 AppearanceStore。</summary>
    private AppearanceSettings _appearance = AppearanceSettings.Default;
    private bool _isRunning;

    /// <summary>
    /// 程序版本，取自程序集（由 Directory.Build.props 的 &lt;Version&gt; 决定）。
    /// </summary>
    /// <remarks>
    /// ★ 为什么不再手写：标题栏与「关于」里原本各自硬编码着 "2.0"，
    ///   升到 2.1.1 之后界面仍然自称 2.0 —— 版本号这种一次性信息写死在 UI 里，
    ///   迟早会和 csproj 对不上，而且对不上的时候不会报错，只会误导用户。
    /// </remarks>
    public static string AppVersion
    {
        get
        {
            var v = typeof(MainWindow).Assembly.GetName().Version;
            return v is null ? "?" : v.Major + "." + v.Minor + "." + v.Build;
        }
    }

    public MainWindow()
    {
        InitializeComponent();

        Title = "肥鱼 FatFish " + AppVersion;

        MessageList.ItemsSource = _messages;
        LogList.ItemsSource = _logs;
        ToolList.ItemsSource = _toolLog;

        DataContext = this;

        FileTree.MouseDoubleClick += OnTreeDoubleClick;

        // Ctrl + 滚轮 = 调字号（大多数人第一次尝试的调法就是它）
        MessageList.PreviewMouseWheel += OnMessageScrollWheel;   // ScrollViewer 现在在 ItemsControl 模板里

        // 字号快捷键单独挂一个处理器：OnWindowKeyDown 已经负责 Ctrl+N，
        // 再往里塞字号判断会让那个方法变成「什么都干」。
        PreviewKeyDown += OnAppearanceKeyDown;
        AttachFileReadMenu();
        PreviewKeyDown += OnWindowKeyDown;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;

        // 流式渲染探针：只在 FATFISH_STREAM_PROBE=1 时启用，平时一行都不执行
        StreamProbe.Attach(this);
    }

    // ---------- 绑定属性 ----------

    public string WorkspaceText
    {
        get => _workspaceText;
        private set => SetField(ref _workspaceText, value);
    }

    public string KernelStatus
    {
        get => _kernelStatus;
        private set => SetField(ref _kernelStatus, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string ToolCountText
    {
        get => _toolCountText;
        private set => SetField(ref _toolCountText, value);
    }

    /// <summary>运行中显示「停止」，否则显示「发送」。</summary>
    public string SendButtonText
    {
        get => _sendButtonText;
        private set => SetField(ref _sendButtonText, value);
    }

    /// <summary>工具栏右侧展示的模型名。</summary>
    public string ModelText
    {
        get => _modelText;
        private set => SetField(ref _modelText, value);
    }

    /// <summary>状态栏的用量格，形如 in/out/total。</summary>
    public string UsageText
    {
        get => _usageText;
        private set => SetField(ref _usageText, value);
    }

    /// <summary>对话标题条右侧的消息条数。</summary>
    public string MessageCountText
    {
        get => _messageCountText;
        private set => SetField(ref _messageCountText, value);
    }

    /// <summary>状态栏的余额格，形如「DS ¥60.99  TV 812 (81%)」。</summary>
    /// <remarks>
    /// 余额放在状态栏而不是弹窗里：它是个「顺带看一眼」的数字，
    /// 看不到的时候没人会专门去查，而看不到的结果就是某天搜索突然全失败。
    /// </remarks>
    public string BalanceText
    {
        get => _balanceText;
        private set => SetField(ref _balanceText, value);
    }

    /// <summary>消息正文字号。模板里 BodyText 的 FontSize 绑这个。</summary>
    public double MessageFontSize => _appearance.FontSize;

    /// <summary>消息外边距：上下各占段间距的一半，左右沿用原来的 11 / 12。</summary>
    /// <remarks>
    /// 为什么上下平分：段间距的直觉是「两条消息之间的空隙」。
    /// 若全给上边或下边，最后一条消息底下就会多出一截空白，
    /// 滚到底时看起来像内容被截断。
    /// </remarks>
    public Thickness MessageMargin => new(
        11,
        Math.Round(_appearance.BlockSpacing / 2),
        12,
        Math.Round(_appearance.BlockSpacing / 2));

    // ---------- 生命周期 ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ui = new UiDispatcher(Dispatcher, HandleUiEvent);

        // 窗口位置要在装配内核之前恢复：这是纯 UI 的事，
        // 而且万一内核装配失败，用户至少还能看到一个位置正常的窗口。
        RestoreWindowPlacement();

        // 外观和窗口位置一样，要在内核装配之前恢复：万一后面初始化失败，
        // 用户至少还能用自己习惯的字号去看那条错误信息。
        LoadAppearance();

        try
        {
            BootKernel();
            var ok = BootAgent();

            KernelStatus = ok ? "内核已就绪" : "缺 API Key";
            // ★ 2026-10-30：这里原先是「就绪。Enter 发送，Shift+Enter 换行，Esc 停止。」
            //   用户要求去掉输入区的常驻按键提示，状态栏这句同理 —— 键位说明留给
            //   输入框的 ToolTip 与「帮助 → 快捷键」，状态栏只说状态。
            StatusText = ok
                ? "就绪。"
                : "未配置 API Key（FATFISH_API_KEY）—— 只能使用工具，无法对话。";

            // 走资源引用而不是直接赋 Brush：换肤时这个状态灯也要跟着变色
            // 注：Shapes 不放 using —— 会和 System.IO.Path 撞名（CS0104），所以这里写全限定名
            KernelDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                ok ? "IconGreen" : "IconRed");

            // 夜间模式：启动配色由 .env 的 UI_THEME 决定（dark = 黑底白字），没配就是白底。
            // 放在 BootKernel 之后 —— 那时 .env 才刚被读进来。
            ThemeManager.Apply(ThemeManager.Parse(EnvFile.Get("UI_THEME", "light")));
            SyncThemeUi(announce: false);

            // 首屏给一句提示，顺便让「消息行」这个核心视觉元素立刻可见，
            // 否则新用户打开只看到一片空白，不知道面板是怎么组织的。
            AppendMessage("system",
                "肥鱼 2.0 已就绪。左侧是工作台文件树（双击文件用系统程序打开），"
                + "底部是日志与工具调用记录；写文件前会弹审批窗口。");
        }
        catch (Exception ex)
        {
            KernelStatus = "初始化失败";
            StatusText = ex.GetType().Name + ": " + ex.Message;
            AppendMessage("system", "初始化失败：\n" + ex);
        }

        InputBox.Focus();

        // 余额异步刷新：它要发两次 HTTP 请求，绝不能挡在启动路径上。
        _ = RefreshBalanceAsync();

        // 之后每 60 秒刷一次（实现在 MainWindow.Balance.cs）。
        // 只靠「启动一次 + 点击才查」的话，聊一晚上那格数字都不会动。
        StartBalanceTimer();
    }

    // ---------- 窗口位置 ----------

    /// <summary>工作台根目录（装配早期就要用，所以抽出来）。</summary>
    private static string ResolveWorkspaceRoot()
    {
        var root = Environment.GetEnvironmentVariable("FATFISH_WORKSPACE");

        return string.IsNullOrWhiteSpace(root) ? Directory.GetCurrentDirectory() : root;
    }

    /// <summary>
    /// 恢复上次的窗口位置，并保证它一定落在某块屏幕里。
    /// </summary>
    /// <remarks>
    /// ★ 只恢复不校验的实现会留下一个永久的坑：用户在外接屏上把窗口拖到右边，
    ///   拔掉外接屏后启动 —— 窗口在屏幕外，任务栏有图标却点不动，
    ///   看起来完全像「程序起不来」。所以这里用的是 <c>WindowPlacementStore.Load</c>，
    ///   它内部会把坐标夹回可见范围（规则见 Core/Host/WindowStateStore.cs）。
    /// </remarks>
    private void RestoreWindowPlacement()
    {
        try
        {
            var root = ResolveWorkspaceRoot();

            // Win32 的屏幕坐标是物理像素，WPF 的 Left/Top 是 DIP —— 必须换算，
            // 否则在 150% 缩放下「屏幕」会被算大 1.5 倍。
            var scale = 1.0;
            var source = PresentationSource.FromVisual(this);

            if (source?.CompositionTarget is not null)
                scale = source.CompositionTarget.TransformToDevice.M11;

            var screens = ScreenEnumerator.WorkAreas(scale);

            // 主屏工作区用 WPF 自己的 SystemParameters（它已经是 DIP，且一定扣掉了任务栏）
            var primary = new ScreenRect(
                SystemParameters.WorkArea.X, SystemParameters.WorkArea.Y,
                SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);

            var placement = WindowPlacementStore.Load(root, screens, primary);

            Left = placement.Left;
            Top = placement.Top;
            Width = placement.Width;
            Height = placement.Height;

            if (placement.Maximized)
                WindowState = System.Windows.WindowState.Maximized;

            Log($"窗口位置 {placement}（检测到 {screens.Count} 块屏幕）");
        }
        catch (Exception ex)
        {
            // 位置恢复失败绝不该阻止启动
            Log("窗口位置恢复失败（使用默认位置）：" + ex.Message);
        }
    }

    /// <summary>保存窗口位置。最大化时存的是「还原后」的尺寸。</summary>
    private void SaveWindowPlacement()
    {
        try
        {
            // RestoreBounds 在最大化/最小化状态下返回的是还原后的大小 —— 正是我们要存的。
            // 存成「最大化时的 1920×1040」的话，用户下次取消最大化就只能得到一块全屏。
            var b = RestoreBounds;

            if (b.IsEmpty || double.IsNaN(b.Width) || b.Width <= 0 || b.Height <= 0)
                return;

            WindowPlacementStore.Save(ResolveWorkspaceRoot(), new WindowPlacement(
                b.Left, b.Top, b.Width, b.Height,
                WindowState == System.Windows.WindowState.Maximized));
        }
        catch (Exception)
        {
            // 记不住位置不该是错误
        }
    }

    // ---------- 余额 ----------

    private async void OnBalanceClick(object sender, MouseButtonEventArgs e)
        => await RefreshBalanceAsync().ConfigureAwait(true);

    /// <summary>查余额并更新状态栏。任何失败都只显示「余额 ?」，不弹窗、不打断对话。</summary>
    private async Task RefreshBalanceAsync()
    {
        try
        {
            var svc = new BalanceService();

            // 走缓存：Tavily 的 /usage 限流很紧，点一次查一次很快就会 429。
            // 想知道「刚才那笔花了没有」的话，看本机台账（同一份结果里就有）比特意去刷官方接口更可靠。
            var snap = await svc.FetchAsync().ConfigureAwait(true);

            var (_, _, overall) = snap.Grade(svc);

            BalanceText = BalanceService.LevelIcon(overall) + " " + BalanceService.OneLine(snap);
        }
        catch (Exception ex)
        {
            BalanceText = "余额 ?";
            Log("余额查询失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 窗口正在关闭 —— 保存位置必须在这里做。
    /// </summary>
    /// <remarks>
    /// ★ 不能放在 <see cref="OnClosed"/> 里：那一刻窗口句柄已经销毁，
    ///   <c>RestoreBounds</c> 返回空矩形，于是保存静默失败（被 catch 吞掉），
    ///   下次启动又回到默认位置。整个过程没有任何报错 ——
    ///   典型的「功能看起来有、其实从来没生效」。
    ///   实测证据：GUI 冒烟测试正常退出（退出码 0），window.json 却不存在。
    /// </remarks>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowPlacement();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _runCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }

        _llm?.Dispose();
        _net?.Dispose();
        _verifier?.Dispose();

        // 先冲一次会话文件，再关别的 —— 反过来的话，最后几条消息可能还没落盘就没机会了
        _session?.Dispose();

        // 把还在跑的后台任务一并掐掉，否则关掉窗口后它们会变成孤儿进程继续占 CPU
        _jobs?.Dispose();

        // 「过程」页的真控制台是独立进程：收掉它，别在后台留一个看不见的 cmd
        ShutdownTraceConsole();

        if (_ui is not null)
            await _ui.DisposeAsync();
    }

    // ---------- 内核装配 ----------

    private void BootKernel()
    {
        var root = ResolveWorkspaceRoot();

        // 工作台会话：根可切换（ws_cd / ws_cd_approve）。
        // 注意别和上面那个 _session 搞混 —— 那是「会话落盘」，这是「工作台根」。
        _workspaceSession = new WorkspaceSession(root);

        // 换根事件在后台线程触发（工具跑在 AgentLoop 的任务里），
        // 所以只投递事件、不碰控件。
        _workspaceSession.RootChanged += (oldRoot, newRoot) =>
        {
            _guard = _workspaceSession.Guard;
            _ui?.Post(new UiEvent.WorkspaceChanged(oldRoot, newRoot));
        };

        _guard = _workspaceSession.Guard;
        var tickets = _workspaceSession.Tickets;

        // 后台任务管理器。Finished 在后台线程触发，所以走 _ui.Post 回 UI 线程播报。
        _jobs = new BackgroundJobManager();
        _jobs.Finished += snap => _ui?.Post(new UiEvent.PushSystem(FormatJobReport(snap)));

        // 联网服务：HttpClient 与限速桶都是「该被复用」的东西，整个窗口共用一个实例。
        // 台账建不出来（只读目录等）就退化成不记账 —— 搜索本身不受影响。
        UsageLedger? ledger = null;

        try
        {
            ledger = UsageLedger.Open(_guard.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("联网台账无法创建（搜索不受影响）：" + ex.Message);
        }

        // ★ 顺序硬约束（2026-10-30 修）：.env 必须在 NetService 构造之前加载。
        //   NetOptions.FromEnvironment() 只认「进程环境变量」，而 .env 原本要等到
        //   BootAgent() 才被 LoadNearest 灌进来 —— 于是联网层永远拿空 key 构造，
        //   症状是 web_search / web_fetch 一直回「没有配置任何搜索后端」，
        //   而 .env 里 TAVILY_API_KEY / FATFISH_API_KEY 明明齐全。
        //   BootAgent 复用这里的结果，不再重复载入。
        // ★ 2026-11-17 修：这里原来是 EnvFile.LoadNearest —— 它只读 .env、不看 FATFISH_PRESET，
        //   于是每次启动都退回 .env 的裸值，用户必须手动点「配置 ▸ 预设」才切回来（用户报的原话：
        //   「每次启动不读取预设配置，每次都要手动切预设」）。启动与热重载必须同一套语义。
        var startup = EnvRuntime.ApplyStartup(_guard.Root);

        foreach (var warning in startup.Warnings)
            Log("[配置] ⚠ " + warning);

        // 一行说清「这次到底按哪套配置跑」：文件 + 叠了哪个预设 + 装进去几项。
        Log("[配置] " + (startup.EnvPath ?? "（未找到 .env）")
            + (startup.PresetName is null ? string.Empty : "｜预设：" + startup.PresetName)
            + (startup.EnvPath is null ? string.Empty : $"｜装载 {startup.AppliedCount} 项"));

        var netOptions = NetOptions.FromEnvironment();

        // 「到底认了哪几家后端」写进日志（key 只露尾 4 位）。
        // 没有这一行时，「配置写了却没生效」只能靠猜。
        Log("[联网] " + netOptions.Describe());

        _ledger = ledger;

        _net = new NetService(netOptions, ledger);

        // ---------- 插件（自适应发现 / 能力比对 / 风险推断）----------
        // ★ 顺序不能变：内置工具 → 插件宿主 → 审批策略 → 注册表。
        //   宿主需要内置名单才能拒绝重名；策略需要宿主才能认识插件工具名。
        var builtin = new ITool[]
        {
            new WsWhereTool(),
            new WsReadTool(),
            new WsListTool(),
            new WsSearchTool(),
            new WsWriteTool(),
            new WsAppendTool(),
            new WsReplaceTool(),
            new WsDeleteTool(),
            new WsMkdirTool(),
            new WsForgetTool(),
            new WsCdTool(),
            new WsCdApproveTool(),
            new WsSiteNewTool(),
            new WsSiteListTool(),
            new WsSiteEnterTool(),
            new WsSiteHomeTool(),
            new WsSiteDropTool(),
            new WsRunCmdTool(),
            new WsRunPythonTool(),
            new WsBgCmdTool(),
            new WsBgPythonTool(),
            new WsBgListTool(),
            new WsBgTailTool(),
            new WsBgKillTool(),
            new WebSearchTool(),
            new WebFetchTool(),
            new NetBalanceTool(),
            new WsPluginListTool(),
            new WsPluginInfoTool(),
            new WsPluginReloadTool(),
        };

        var pluginDirs = new List<PluginDirectory>
        {
            new(Path.Combine(AppContext.BaseDirectory, "plugins"), BuiltIn: true),
            new(Path.Combine(_guard.Root, "plugins"), BuiltIn: false),
        };

        var pluginDirOverride = Environment.GetEnvironmentVariable("FATFISH_PLUGIN_DIR");

        if (!string.IsNullOrWhiteSpace(pluginDirOverride))
        {
            pluginDirs.Insert(0, new PluginDirectory(
                Path.GetFullPath(pluginDirOverride.Trim()), BuiltIn: false));
        }

        // 发布版（单文件 exe 就放在工作台根）会让 BaseDirectory 与工作台根重合，
        // 同一份 plugins 被扫两遍：工具不会重复注册（按 id 覆盖），但日志会多一条
        // 「已被覆盖」、filesFound 翻倍，排查时白费眼力。这里按路径去重。
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pluginDirs = pluginDirs.Where(d => seenDirs.Add(Path.GetFullPath(d.Path))).ToList();

        _plugins = PluginHost.Start(
            new PluginHostOptions
            {
                Directories = pluginDirs,
                ReservedToolNames = builtin.Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
            },
            log: msg => Log(msg));

        _policy = new ApprovalPolicy(_workspaceSession) { Plugins = _plugins };

        // 车间登记簿（旧称「工地」）：库房 + 若干临时车间，落盘在 <库房根>\_sites.json。
        // 必须在 ToolContext 之前建好 —— 五个 ws_site_* 工具和左栏「车间」清单都靠它认名字。
        var sites = new WorkspaceSites(_workspaceSession);

        _sites = sites;   // 左栏要读它，所以挂到字段上（局部变量活不到 LoadShops）

        _context = new ToolContext
        {
            // Guard / Tickets 是「初始绑定」；Workspace 会让它们跟着会话走，
            // 这样 ws_cd 换根之后所有工具读 context.Guard 拿到的都是新根。
            Guard = _guard,
            Tickets = tickets,
            Workspace = _workspaceSession,
            Sites = sites,
            Approval = _policy,
            Jobs = _jobs,
            Net = _net,
            Plugins = _plugins,
            Log = msg => _ui?.Post(new UiEvent.PushSystem("[内核] " + msg)),
        };

        _registry = new ToolRegistry(builtin, _plugins);

        WorkspaceText = "厂里：" + _guard.Root;
        ToolCountText = "工具 " + _registry.Count + " 个";

        Log("厂里根目录（库房）" + _guard.Root);
        Log("插件 " + _plugins.Describe());

        // 直接从注册表取名，不再手写一份清单：
        // 手写的清单加工具时一定会忘，而那正是「日志说 3 个、实际 6 个」的来源。
        Log("已注册工具：" + string.Join(
            ", ", _registry.All.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)));

        // 库房 = 家（默认作业区）。它不跟着换根跑：模型进了车间，库房照样显示家里的文件。
        LoadTree(_workspaceSession.DefaultRoot);
        LoadShops();      // 左栏「车间」栏

        // 预设菜单要在启动时就建好：否则第一次打开「配置 ▸ 预设」是空白的，
        // 用户会以为功能没生效（而它只是还没被触发）。
        RebuildPresetMenu();
    }

    /// <summary>装配 LLM 与编排层。返回是否成功（缺 key 时返回 false 而不是抛异常）。</summary>
    private bool BootAgent()
    {
        if (_guard is null || _registry is null || _context is null || _policy is null)
            return false;

        // 配置在 BootKernel 里已经装好了（.env + 激活的预设，见 EnvRuntime.ApplyStartup）。
        // 这里不再 Load 一次：Load 是「已存在不覆盖」且按文件去重，再调只会空转 ——
        // 却仍返回「解析到 N 项」，让日志说一句「载入 N 项」而实际一项没变。
        var envPath = EnvFile.ResolvePath(_guard.Root);

        // 与 Python 版同序：FATFISH_API_KEY 优先，DEEPSEEK_API_KEY 作为旧写法兼容。
        // 用 TryResolveApiKey 而不是自己 EnvFile.Get 某个键：
        // 两个键的优先级只在一个地方定义，UI / CLI / 内核不会各说各话。
        if (!LlmOptions.TryResolveApiKey(out _, out _))
        {
            AppendMessage("system",
                "未找到 API Key。\n" +
                (envPath is null
                    ? "在工作台根目录放一个 .env，写上 FATFISH_API_KEY=sk-xxxx 即可。"
                    : $"已加载 {envPath}，但其中没有 FATFISH_API_KEY" +
                      "（旧写法 DEEPSEEK_API_KEY 也认）。"));
            return false;
        }

        var options = LlmOptions.FromEnvironment();
        _llm = new DeepSeekClient(options);

        // ---------- 双人核验 ----------
        // 审查员与主模型分开配置（VERIFIER_*），留空则沿用主模型。
        // 配成别家模型即形成「异源核验」—— 同一个模型审自己，等于自己给自己签字。
        _verifyOptions = VerifyOptions.FromEnvironment(options);

        _verifier = new VerifierService(
            _verifyOptions,
            log: msg => _ui?.Post(new UiEvent.PushSystem("🧿 " + msg)),
            logDirectory: Path.Combine(_guard.Root, "logs", "verify"));

        _verify = new VerifySession(_verifier, msg => Log(msg));
        _userEvents = new UserEventLog();

        _agent = new AgentLoop(
            _llm, _registry, _context, _policy, new AgentOptions(),
            _verify, _userEvents);

        Log("双人核验 " + _verifyOptions.Describe());
        SyncVerifyMenu();

        AttachSession();

        // 启动消息里点名激活的预设：「到底按哪套配置跑的」不该靠点菜单去猜。
        if (EnvRuntime.ActivePresetName(_guard.Root) is { } activePreset)
            AppendMessage("system",
                "启动配置：预设「" + activePreset + "」"
                + (envPath is null ? string.Empty : "（" + Path.GetFileName(envPath) + "）"));

        ModelText = options.Model;

        StatusText = "模型：" + options.Model
            + "｜核验：" + (_verifyOptions.IsEnabled ? _verifyOptions.Mode.ToString() : "关")
            + "｜工作台：" + _guard.Root;
        Log("模型 " + options.Model + "｜配置 " + (envPath ?? "（未找到 .env）"));
        return true;
    }

    /// <summary>
    /// 把编排层的历史接到磁盘上，并尝试恢复上一次的对话。
    ///
    /// ★ 任何失败都在这里吞掉：落不了盘也要能聊天。
    ///   磁盘满、目录只读、文件被杀软锁住都会发生，而它们都只该影响「下次能不能接着聊」。
    /// </summary>
    private void AttachSession()
    {
        if (_agent is null || _guard is null)
            return;

        try
        {
            if (SessionStore.LoadLatest(_guard.Root) is { Messages.Count: > 0 } restored)
            {
                // 接着写原来的文件：一次对话被劈成两个文件，会让人看不出「上次聊到哪」
                _session = SessionStore.Attach(
                    restored.FilePath, restored.SessionId, restored.TotalEntries);

                var count = _agent.RestoreHistory(restored.Messages);

                // 把恢复的对话也画到界面上。
                // 只恢复内存不恢复界面的话，用户看到空白聊天区会以为自己记错了，
                // 然后重复问一遍 —— 而模型其实已经记得。
                foreach (var m in restored.Messages)
                    AppendMessage(m.Role == "user" ? "user" : "ai", m.Content ?? string.Empty);

                AppendMessage("system",
                    $"已恢复上次会话的 {count} 条对话。" +
                    $"（{Path.GetFileName(restored.FilePath)}）");
                Log($"恢复会话 {count} 条 ← {restored.FilePath}");
            }
            else
            {
                _session = SessionStore.Create(_guard.Root);
                Log("会话文件 " + _session.FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendMessage("system", "会话无法落盘（对话不受影响）：" + ex.Message);
            _session = null;
        }

        if (_session is null)
            return;

        // 订阅点在内核「消息真正进入历史之后」那一处，
        // 所以这里记下的序列与模型看到的完全一致。
        _agent.Traced += entry => _session?.Append(
            entry.Message, entry.ToolName, entry.ToolOk, entry.ElapsedMs,
            entry.ToolNames, entry.Usage);

        // Ctrl+N / 菜单「新建会话」→ ClearChat → ResetHistory → 这里开新文件。
        // 旧文件不删，它是一段已经结束的对话。
        _agent.HistoryCleared += () => _session?.StartNew();
    }

    // ---------- 发送 ----------

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        // 运行中再次点击 = 停止
        if (_isRunning)
        {
            _runCts?.Cancel();
            StatusText = "正在停止…";
            return;
        }

        if (_agent is null || _ui is null)
        {
            AppendMessage("system", "内核尚未就绪。");
            return;
        }

        var text = InputBox.Text.Trim();
        if (text.Length == 0)
            return;

        InputBox.Clear();

        var agent = _agent;   // 局部引用：避免 lambda 捕获可空字段导致的可空性警告
        var dispatcher = _ui;

        _runCts = new CancellationTokenSource();
        SetRunning(true);
        dispatcher.Post(new UiEvent.PushUser(text));

        var token = _runCts.Token;

        try
        {
            var result = await Task.Run(
                () => agent.RunAsync(text, this, token),
                token).ConfigureAwait(true);

            var tail = result.Usage is null
                ? $"完成（{result.Rounds} 轮 / {result.ToolCallCount} 次工具）"
                : $"完成（{result.Rounds} 轮 / {result.ToolCallCount} 次工具）｜{result.Usage}";

            if (result.HitRoundLimit)
                tail += "｜⚠️ 已达轮次上限";

            _ui.Post(new UiEvent.SetStatus(tail));
        }
        catch (OperationCanceledException)
        {
            _ui.Post(new UiEvent.SetStatus("已停止"));
        }
        catch (LlmException ex)
        {
            _ui.Post(new UiEvent.Error("模型调用失败：" + ex.Message));
        }
        catch (Exception ex)
        {
            _ui.Post(new UiEvent.Error(ex.GetType().Name + ": " + ex.Message));
        }
        finally
        {
            EndStream();
            _runCts?.Dispose();
            _runCts = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running)
    {
        _isRunning = running;
        SendButtonText = running ? "停止" : "发送";

        // ★ 这里必须显式重设 IsEnabled。
        //   因为 OnSendClick 里先 InputBox.Clear() 再 SetRunning(true)：
        //   Clear 触发 TextChanged，那一刻 _isRunning 还是 false，
        //   按钮被置为禁用；若不在这里重新打开，运行中就点不到「停止」了。
        SendButton.IsEnabled = running || !string.IsNullOrWhiteSpace(InputBox.Text);

        if (running)
            StatusText = "正在思考…";
    }

    private void EndStream()
    {
        if (_streamId is null || _ui is null)
            return;

        _ui.Post(new UiEvent.StreamEnd(_streamId));
        _streamId = null;
    }

    // ---------- UI 事件处理（由 UiDispatcher 保证在 UI 线程） ----------

    private void HandleUiEvent(UiEvent evt)
    {
        switch (evt)
        {
            case UiEvent.PushUser e:
                AppendMessage("user", e.Text);
                break;

            case UiEvent.PushAi e:
                AppendMessage("ai", e.Text);
                break;

            case UiEvent.PushSystem e:
                AppendMessage("system", e.Text);
                break;

            case UiEvent.StreamBegin e:
                FlushStreamDelta();                 // 上一段没画完的一次性落地，再开新气泡
                _messages.Add(new MessageItem(e.StreamId, string.Empty, "ai"));
                UpdateMessageCount();
                ScrollToBottom();
                break;

            case UiEvent.StreamDelta e:
                {
                    // 只入缓冲 + 排一次「立刻画」：同一批分片会在这里合并成一段
                    _pendingDelta.Append(e.Text);
                    _pendingDeltaId = e.StreamId;
                    StreamProbe.NoteDelta(e.Text.Length);
                    QueueFlush();

                    break;
                }

            case UiEvent.StreamEnd:
                // 网络侧收完：把缓冲里剩下的（通常只有几个字）一次画掉
                FlushStreamDelta();
                ScrollToBottom();

                if (StreamProbe.On)
                {
                    var probeTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1.5),
                    };
                    probeTimer.Tick += (_, _) =>
                    {
                        probeTimer.Stop();
                        StreamProbe.Report("stream-end");
                    };
                    probeTimer.Start();
                }

                break;

            case UiEvent.SetStatus e:
                StatusText = e.Text;
                break;

            case UiEvent.WorkspaceChanged e:
                {
                    // 根变了：状态栏、_guard 跟着换；**库房那棵树不换**（它永远是家），
                    // 只刷新车间栏的 ▸ 与「现在在哪」。
                    // _guard 在「打开文件夹 / 加载 .env / 会话落盘目录」等处还被用到，
                    // 在这里统一重指，比让每处都改成 _workspaceSession.Guard 更不容易漏。
                    _guard = _workspaceSession?.Guard ?? _guard;

                    WorkspaceText = "厂里：" + e.NewRoot;
                    LoadShops();      // ▸ 标记换人了；库房树故意不动

                    AppendMessage("system",
                        $"🏭 厂里已搬家：{e.OldRoot} → {e.NewRoot}（读凭证已清空）");

                    StatusText = "厂里已搬家";
                    break;
                }

            case UiEvent.ApprovalRequested e:
                ShowApprovalDialog(e);
                break;

            case UiEvent.Error e:
                StatusText = e.Text;
                AppendMessage("system", "❌ " + e.Text);
                break;
        }
    }

    /// <summary>还挂着的审批窗（非模态，并行工具调用时可能不止一个）。</summary>
    private readonly List<ApprovalWindow> _approvals = new();

    private void ShowApprovalDialog(UiEvent.ApprovalRequested e)
    {
        try
        {
            // ★ 非模态（Show 而不是 ShowDialog）。
            //   模态的问题不是「要你确认」，而是**整个程序被它冻住**：窗口在等你看，
            //   你却在等窗口结束 —— 连「停止本回合」和滚动聊天都点不了。
            //   现在它只是浮在上面，结果通过 Decided 事件回话。
            var dlg = new ApprovalWindow(e.Request) { Owner = this };

            var settled = false;

            void Settle(bool approved, bool allowForSession)
            {
                if (settled)
                    return;

                settled = true;
                _approvals.Remove(dlg);
                e.Decision.TrySetResult(new ApprovalDecision(approved, allowForSession));

                if (dlg.IsVisible)
                    dlg.Close();
            }

            dlg.Decided += (approved, allowForSession) => Settle(approved, allowForSession);

            // 直接关窗 = 拒绝（以前模态下关窗 ShowDialog 返回 null，也走拒绝，行为一致）
            dlg.Closed += (_, _) => Settle(false, false);

            // 回合被取消（Esc / 停止，或内核自己放弃）时把这张单子也了结掉。
            // 修的就是这个老毛病：模态下内核已经不等了，窗口还杵在那儿要你点。
            if (e.Cancel.CanBeCanceled)
            {
                e.Cancel.Register(() => Dispatcher.BeginInvoke(
                    new Action(() => Settle(false, false))));
            }

            _approvals.Add(dlg);
            dlg.Show();
            dlg.Activate();
        }
        catch (Exception ex)
        {
            e.Decision.TrySetResult(new ApprovalDecision(false, false));
            AppendMessage("system", "审批对话框异常：" + ex.Message);
        }
    }

    private MessageItem? FindMessage(string id)
    {
        // 流式气泡永远在末尾附近，倒序找比正序快得多
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            if (_messages[i].Id == id)
                return _messages[i];
        }

        return null;
    }

    private void AppendMessage(string kind, string text)
    {
        _messages.Add(new MessageItem(Guid.NewGuid().ToString("N"), text, kind));
        UpdateMessageCount();
        ScrollToBottom();
    }

    private bool _scrollQueued;

    /// <summary>
    /// 是否要「跟随到底」：新内容出现时自动滚到最新一行。
    /// </summary>
    /// <remarks>
    /// ★ 这个标志只能由「用户主动滚动」改变（见 OnMessageScrollChanged）。
    ///   绝不能在每条分片里现算「离底部多远」—— 流式追加本身就把
    ///   ScrollableHeight 一路推高，现算的结果永远是「用户已经离开了」，
    ///   于是跟随会在输出两三行之后悄悄失效。
    /// </remarks>
    private bool _stickToBottom = true;

    /// <summary>ScrollChanged 只订阅一次。</summary>
    private bool _scrollHooked;

    /// <summary>消息列表自带的 ScrollViewer（在 ItemsControl 的模板里，只能从视觉树取）。</summary>
    private ScrollViewer? MessageScroller
    {
        get
        {
            if (_msgScroller is not null)
                return _msgScroller;

            _msgScroller = FindDescendant<ScrollViewer>(MessageList);

            // 模板里的 ScrollViewer 要等布局完成才存在，所以订阅放在取用处，不能放构造函数。
            if (_msgScroller is not null && !_scrollHooked)
            {
                _scrollHooked = true;
                _msgScroller.ScrollChanged += OnMessageScrollChanged;
            }

            return _msgScroller;
        }
    }

    /// <summary>
    /// 排一次「立刻画」。
    /// </summary>
    /// <remarks>
    /// 这是**唯一**的合并手段：没有定时器、没有节拍、没有每帧字数上限。
    /// 一批分片只会排到一次，剩下的延迟就是「这个回调什么时候被调度到」，
    /// 量级在 1ms 以内 —— 属于看不出来的部分，不是人为加的。
    /// </remarks>
    private void QueueFlush()
    {
        if (_flushQueued)
            return;

        _flushQueued = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _flushQueued = false;
            FlushStreamDelta();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>把缓冲里的字全部写进气泡。不切分、不限速。</summary>
    private void FlushStreamDelta()
    {
        if (_pendingDelta.Length == 0)
            return;

        var chunk = _pendingDelta.ToString();
        _pendingDelta.Clear();

        var id = _pendingDeltaId;
        var sw = StreamProbe.On ? Stopwatch.StartNew() : null;

        if (id is not null)
        {
            // 1) 数据层始终同步：会话落盘、虚拟化回收后重新实例化，都要拿到完整正文
            FindMessage(id)?.AppendSilent(chunk);

            // 2) 控件层增量追加：AppendText 不会把已有文字丢掉重建
            if (_bodyBoxes.TryGetValue(id, out var box))
            {
                try
                {
                    box.AppendText(chunk);
                }
                catch (InvalidOperationException)
                {
                    // 气泡刚好被移除：下次 Loaded 时会从 Text 绑定补全
                }
            }
        }

        if (sw is not null)
            StreamProbe.NoteFlush(chunk.Length, sw.Elapsed.TotalMilliseconds);

        ScrollToBottom();
    }

    // ---------- 正文 TextBox 的登记（流式时直接写控件） ----------

    private void OnBodyTextLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not MessageItem item)
            return;

        _bodyBoxes[item.Id] = box;

        // 正在流式的那条刚被实例化（虚拟化回收、或用户滚回来）：把已收到的内容补上
        if (item.Id == _pendingDeltaId && box.Text.Length != item.Text.Length)
            box.Text = item.Text;
    }

    private void OnBodyTextUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not MessageItem item)
            return;

        if (_bodyBoxes.TryGetValue(item.Id, out var current) && ReferenceEquals(current, box))
            _bodyBoxes.Remove(item.Id);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T hit)
                return hit;

            var deeper = FindDescendant<T>(child);
            if (deeper is not null)
                return deeper;
        }

        return null;
    }

    /// <summary>
    /// 把视图滚到最新一行 —— 只在新内容把底部推出视野、且用户没有主动上翻时做。
    /// </summary>
    /// <remarks>
    /// ★ 2026-11-04 修：「跟随」曾经在这里丢掉过，原因记下来以免重犯。
    ///   旧写法在滚动回调里现算「离底部是否 &lt; 48px」，可流式追加本身就一直把
    ///   ScrollableHeight 推高，两三行之后这个差必然超过 48，于是被判成
    ///   「用户往上翻了」—— 从此不再跟随，而且不报错、不打日志。
    ///   现在判断交给 OnMessageScrollChanged（只认用户主动滚动），这里只管照办。
    /// </remarks>
    private void ScrollToBottom()
    {
        if (!_stickToBottom)
            return;

        // 流式时每秒可能触发上百次，用标志位合并成一次滚动请求
        if (_scrollQueued)
            return;

        _scrollQueued = true;

        // 优先级从 Background 提到 Render：Background 排在输入与渲染之后，
        // 流式密集时会被后续刷新一路挤掉，表现为「滚得一顿一顿」。
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _scrollQueued = false;

                var scroller = MessageScroller;
                if (scroller is null || !_stickToBottom)
                    return;

                scroller.ScrollToEnd();
            }),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    /// <summary>
    /// 只在「用户主动滚动」时更新跟随意图。
    /// </summary>
    /// <remarks>
    /// ★ 必须区分两种滚动：用户拖的（VerticalChange ≠ 0）和内容长高导致的
    ///   （只有 ExtentHeightChange）。后者若也参与判断，流式输出会把自己判成
    ///   「用户已经离开底部」，跟随就此永久失效 —— 那正是上一版的毛病。
    /// </remarks>
    private void OnMessageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 && Math.Abs(e.VerticalChange) < 0.5)
        {
            // ★ 2026-11-06 补：长高后补一次贴底（与 ScrollTail.OnScrollChanged 同一处理）。
            //   正文 TextBox 异步加载、换行重算、虚拟化重估 extent 都会把底部推下去，
            //   而那时已经没有「追加消息」这个动作了 —— 没人补滚，视图就停在半路，
            //   表现为「明明没碰滚轮，却得自己往下拖」。
            if (_stickToBottom && e.ExtentHeightChange > 0)
                ScrollToBottom();

            return;
        }

        _stickToBottom = e.ExtentHeight - e.VerticalOffset - e.ViewportHeight < 48;
    }

    // ---------- 输入区 ----------

    /// <summary>
    /// 输入框按键处理（挂在 PreviewKeyDown 上）。
    ///
    /// ★ 为什么必须是 Preview 而不是 KeyDown —— 这个坑踩过一次，记在这：
    ///   TextBox 内部靠「类处理器」（TextEditor.OnKeyDown / TextBoxBase.OnKeyDown）处理 Enter。
    ///   WPF 的执行顺序是「类处理器先于同一元素上的实例处理器」，而 AcceptsReturn="True"
    ///   时类处理器会插入换行并置 e.Handled = true。
    ///   结果就是挂在冒泡 KeyDown 上的处理器永远收不到 Enter —— 表现为
    ///   「按回车只换行、从不发送」，而且不报错、不打日志，极难定位。
    ///   隧道阶段（Preview）跑在类处理器之前，在这里拦下并置 Handled，换行插入就不会发生。
    /// </summary>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // Esc：运行中才拦（没在跑的时候 Esc 留给其他用途，不打扰）
        if (e.Key == Key.Escape)
        {
            if (_isRunning)
            {
                e.Handled = true;
                _runCts?.Cancel();
                StatusText = "正在停止…";
            }

            return;
        }

        // 中文输入法正在组合时，按键会报成 Key.ImeProcessed 而不是 Key.Enter，
        // 所以这个判断天然把「Enter 选字/上屏」让给了输入法，不会误发送。
        if (e.Key != Key.Enter)
            return;

        // Shift+Enter：放行（不置 Handled），交给 TextBox 自己插入换行
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;

        e.Handled = true;

        if (_isRunning)
        {
            // 运行中按 Enter 不做任何事，尤其不能落到 OnSendClick 里变成「停止」——
            // 那会让人以为自己只是按了个回车，回合却被取消了。
            StatusText = "运行中：Enter 不发送，Shift+Enter 换行，Esc 停止";
            return;
        }

        OnSendClick(sender, new RoutedEventArgs());
    }

    private void OnInputTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        SendButton.IsEnabled = _isRunning || !string.IsNullOrWhiteSpace(InputBox.Text);
    }

    // ---------- IAgentObserver 实现（均在后台线程被调用） ----------

    void IAgentObserver.OnTextDelta(string text)
    {
        if (_ui is null)
            return;

        if (_streamId is null)
        {
            _streamId = "s" + Interlocked.Increment(ref _streamSeq);
            _ui.Post(new UiEvent.StreamBegin(_streamId));
        }

        _ui.Post(new UiEvent.StreamDelta(_streamId, text));
    }

    void IAgentObserver.OnReasoningDelta(string text)
    {
        // 思考链实时送给右侧「过程」面板（MainWindow.Process.cs）。
        // 之前这里是空实现，导致新版看不到思考过程 —— 旧 Python 版是有的。
        // AppendReasoning 内部会自己切回 UI 线程，并做 80ms 批量刷新。
        AppendReasoning(text);
    }

    void IAgentObserver.OnRoundBegin(int round)
    {
        if (round > 0)
        {
            // 上一轮已经结束（通常是因为要调工具），收尾再开新气泡
            EndStream();
        }
    }

    void IAgentObserver.OnToolCallStarted(string toolName, System.Text.Json.JsonElement args)
    {
        var item = new ToolLogItem(toolName, CompactJson(args));

        // ObservableCollection 有线程亲和性，只能在 UI 线程增删。
        // （ToolLogItem 自己的属性变更由 WPF 绑定引擎自动封送，不用管。）
        OnUi(() =>
        {
            _pendingTools[toolName] = item;
            _toolLog.Add(item);
            UpdateToolCount();
            FollowTool();
        });

        _ui?.Post(new UiEvent.SetStatus($"调用工具 {toolName}…"));
        Log("→ 调用 " + toolName + " " + item.Arguments);
    }

    void IAgentObserver.OnToolCallFinished(string toolName, ToolResult result)
    {
        if (_pendingTools.Remove(toolName, out var item))
        {
            if (result.Ok)
                item.Finish(result.ElapsedMs);
            else
                item.Fail(result.Error ?? "未知错误", result.ElapsedMs);
        }

        Log(result.Ok
            ? $"← {toolName} 完成 {result.ElapsedMs}ms"
            : $"← {toolName} 失败 {result.Error}");

        _ui?.Post(new UiEvent.SetStatus(result.Ok
            ? $"{toolName} 完成（{result.ElapsedMs}ms）"
            : $"{toolName} 失败：{result.Error}"));
    }

    void IAgentObserver.OnUsage(LlmUsage usage)
    {
        var text = $"in {usage.PromptTokens} / out {usage.CompletionTokens} / 计 {usage.TotalTokens}";
        OnUi(() => UsageText = text);
        _ui?.Post(new UiEvent.SetStatus("用量 " + text));
    }

    void IAgentObserver.OnStatus(string text)
    {
        _ui?.Post(new UiEvent.SetStatus(text));
    }

    async ValueTask<bool> IAgentObserver.RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        if (_ui is null)
            return false;

        var tcs = new TaskCompletionSource<ApprovalDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _ui.Post(new UiEvent.ApprovalRequested(request, tcs, ct));

        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        ApprovalDecision decision;
        try
        {
            decision = await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (decision.AllowForSession)
        {
            _agent?.SetAutoApprovedForSession(true);
            _ui.Post(new UiEvent.PushSystem("已开启本次会话自动放行（敏感文件仍需逐个确认）"));
        }

        return decision.Approved;
    }

    // ---------- 日志与面板 ----------

    /// <summary>
    /// 往底部「日志」页签追加一行，自动带时间戳。
    /// 可在任意线程调用 —— 内部会封送到 UI 线程。
    /// </summary>
    private void Log(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Log(text)));
            return;
        }

        _logs.Add(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text);

        // 只留最近 400 行：日志是排障用的，不需要无限堆积
        while (_logs.Count > 400)
            _logs.RemoveAt(0);

        // ★ 必须放在截断之后：RemoveAt(0) 会把视口位置整体上移，
        //   先贴底会被它冲掉，表现为「日志刚滚到底又弹回去」。
        FollowLog();
    }

    /// <summary>在 UI 线程执行；已在 UI 线程则直接跑。</summary>
    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            Dispatcher.BeginInvoke(action);
    }

    private void UpdateMessageCount()
        => MessageCountText = _messages.Count == 0 ? "无记录" : _messages.Count + " 条";

    private void UpdateToolCount()
    {
        var registered = _registry?.Count ?? 0;
        ToolCountText = $"工具 {registered} 个 · 调用 {_toolLog.Count} 次";
    }

    /// <summary>
    /// 后台任务结束时播报的那一行。
    ///
    /// 四种结束方式分开写，是因为它们对用户的含义完全不同：
    /// 完成 = 结果可以用了；失败 = 去 tail 看错误；超时 = 任务太重或卡住了；
    /// 已中止 = 是你（或模型）主动停的，别再当故障排查。
    /// 把它们合并成一句「任务结束」，用户就得自己去猜。
    /// </summary>
    private static string FormatJobReport(BackgroundJobSnapshot snap)
    {
        var (tag, extra) = snap.Status switch
        {
            "done" => ("✅ 后台任务完成", string.Empty),
            "failed" => ("❌ 后台任务失败", snap.Error is null ? string.Empty : "：" + snap.Error),
            "timeout" => ("⏰ 后台任务超时", string.Empty),
            "killed" => ("⛔ 后台任务已中止", string.Empty),
            _ => ("后台任务 " + snap.Status, string.Empty),
        };

        return $"{tag}｜{snap.Id} {snap.Desc}" +
               $"（{snap.ElapsedMs / 1000.0:0.0}s，输出 {WorkspaceFilters.HumanSize(snap.OutputBytes)}）{extra}";
    }

    /// <summary>把工具参数压成单行，便于在列表里横向展示。</summary>
    private static string CompactJson(System.Text.Json.JsonElement args)
    {
        try
        {
            var raw = args.GetRawText().Replace("\r", " ").Replace("\n", " ");
            return raw.Length <= 150 ? raw : string.Concat(raw.AsSpan(0, 150), "…");
        }
        catch (InvalidOperationException)
        {
            // JsonElement 为 default（工具调用没带参数）时 GetRawText 会抛
            return "{}";
        }
    }

    // ---------- 工作台文件树 ----------

    /// <summary>
    /// 文件树也复用工具层那份忽略名单（<see cref="WorkspaceFilters.IgnoredDirs"/>）。
    ///
    /// 理由不是省事，是**避免误导**：
    /// 如果树里能看到 venv/、而 ws_search 跳过了它，
    /// 模型会对「明明在树里看得见的东西」给出「找不到」的结论，
    /// 用户也会以为是搜索坏了。两边用同一份名单，看到的和搜到的才是一回事。
    /// </summary>
    private static bool IsIgnoredDir(string name) => WorkspaceFilters.IgnoredDirs.Contains(name);

    /// <summary>车间树节点 → 对应的登记项（节点 Tag 留给路径，展开时才用得上）。</summary>
    private readonly Dictionary<TreeViewItem, WsSite> _shopNodes = new();

    /// <summary>去掉末尾分隔符，用于比较两个路径是不是同一处。</summary>
    private static string TrimSep(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 刷新左栏「车间」栏：每个车间自己是一棵树，展开才去读它的目录。
    ///
    /// ★ 节点 Tag 保持「路径」不动，因为 <see cref="OnTreeNodeExpanded"/> 靠它枚举子目录；
    ///   「这个节点代表哪个车间」另存 <see cref="_shopNodes"/> ——
    ///   展开、双击、右键三条路径各取所需，谁也不去改别人的 Tag。
    ///
    /// ★ 本方法只读登记簿 + 贴标签，不改任何状态；搬家在 <see cref="EnterShop"/>。
    /// </summary>
    private void LoadShops()
    {
        ShopTree.Items.Clear();
        _shopNodes.Clear();

        IReadOnlyList<WsSite> all = _sites?.All ?? Array.Empty<WsSite>();

        var current = TrimSep(_workspaceSession?.Root ?? _guard?.Root ?? string.Empty);
        var hereSite = all.FirstOrDefault(s =>
            string.Equals(TrimSep(s.Path), current, StringComparison.OrdinalIgnoreCase));

        if (all.Count == 0)
        {
            ShopHint.Text = "0 处 ｜ 点 ＋ 新建";
            return;
        }

        foreach (var site in all)
        {
            var here = string.Equals(TrimSep(site.Path), current, StringComparison.OrdinalIgnoreCase);
            var missing = Directory.Exists(site.Path) ? string.Empty : "（目录已不在）";

            var label = (here ? "▸ " : string.Empty) + site.Name
                        + (string.IsNullOrWhiteSpace(site.Note) ? string.Empty : " ｜ " + site.Note)
                        + missing;

            // MakeNode 会塞一个 null 占位、并挂上「第一次展开才枚举」的处理器
            var node = MakeNode(site.Path, isDir: true, label: label);

            node.FontWeight = here ? FontWeights.SemiBold : FontWeights.Normal;

            if (missing.Length > 0)
                node.Foreground = (Brush)FindResource("FgMuted");

            node.ToolTip = site.Path
                           + (string.IsNullOrWhiteSpace(site.Note) ? string.Empty : "\n" + site.Note)
                           + "\n建于 " + site.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                           + "\n双击＝搬进这个车间（会清空读凭证）"
                           + "\n右键＝进入 / 在资源管理器打开 / 注销登记";

            node.ContextMenu = BuildShopMenu(site);

            _shopNodes[node] = site;
            ShopTree.Items.Add(node);
        }

        ShopHint.Text = hereSite is null
            ? $"{all.Count} 处 ｜ 现在在家"
            : $"{all.Count} 处 ｜ 在车间「{hereSite.Name}」";
    }

    /// <summary>
    /// 车间项右键菜单：进入 / 在资源管理器打开 / 注销登记。
    ///
    /// ★ 第三项写的是「注销登记」不是「删除」：它与 ws_site_drop 同一条规矩 ——
    ///   只摘登记，不碰文件。真要删目录请走 ws_delete（那才有读全检查、备份与审批）。
    /// </summary>
    private ContextMenu BuildShopMenu(WsSite site)
    {
        var menu = new ContextMenu();

        var enter = new MenuItem { Header = "进这个车间" };
        enter.Click += (_, _) => EnterShop(site);

        var reveal = new MenuItem { Header = "在资源管理器里打开" };
        reveal.Click += (_, _) => RevealInExplorer(site.Path);

        var drop = new MenuItem { Header = "注销登记（不删文件）" };
        drop.Click += (_, _) => DropShop(site);

        menu.Items.Add(enter);
        menu.Items.Add(reveal);
        menu.Items.Add(new Separator());
        menu.Items.Add(drop);

        return menu;
    }

    /// <summary>
    /// 把厂里搬到某个车间。
    ///
    /// ★ 为什么这里可以紧接着调 ApprovePending：点这一下的人就是用户本人。
    ///   模型调用时之所以要两步（ws_cd → ws_cd_approve），防的是「模型自己替用户点头」；
    ///   而这里是用户亲手点的。但**出了默认作业区仍然要先弹确认** ——
    ///   那条线不能因为入口从工具变成鼠标就消失。
    /// </summary>
    private void EnterShop(WsSite site)
    {
        var session = _workspaceSession;
        if (session is null)
            return;

        if (!Directory.Exists(site.Path))
        {
            MessageBox.Show(this,
                $"这个车间的地已经不在了：\n{site.Path}\n\n" +
                "可以让肥鱼用 ws_site_drop 把这条失效登记清掉。",
                "车间", MessageBoxButton.OK, MessageBoxImage.Warning);

            LoadShops();
            return;
        }

        var result = session.ChangeTo(site.Path);

        if (result.NeedsApproval)
        {
            var answer = MessageBox.Show(this,
                $"要把厂里搬到「{site.Name}」吗？\n\n目标：{site.Path}\n" +
                $"它在默认作业区（{session.DefaultRoot}）之外，所以要你亲口确认。\n\n" +
                "搬家会清空所有读凭证。",
                "确认搬家", MessageBoxButton.OKCancel, MessageBoxImage.Question);

            if (answer != MessageBoxResult.OK)
                return;

            result = session.ApprovePending();
        }

        if (!result.Ok)
        {
            MessageBox.Show(this, result.Message, "车间", MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadShops();
            return;
        }

        AppendMessage("system", $"🏭 进车间：「{site.Name}」{site.Path}（读凭证已清空）");
    }

    /// <summary>在资源管理器里定位到车间目录。</summary>
    private void RevealInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                MessageBox.Show(this, $"目录已不在：\n{path}", "车间",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "打不开资源管理器：" + ex.Message, "车间",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>注销车间登记（只摘登记，不删文件）。</summary>
    private void DropShop(WsSite site)
    {
        if (_sites is null)
            return;

        var answer = MessageBox.Show(this,
            $"把「{site.Name}」从车间清单里去掉吗？\n\n" +
            $"目录和里面的文件都会原样保留：\n{site.Path}\n\n" +
            "要真的删目录请用 ws_delete（它才有读全检查、备份与单独审批）。",
            "注销登记", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
            return;

        if (!_sites.Drop(site.Name, out var error))
        {
            MessageBox.Show(this, error, "注销登记", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        AppendMessage("system", $"🏭 已注销车间登记：「{site.Name}」（文件保留）");
        LoadShops();
    }

    /// <summary>
    /// 车间栏双击：点在车间名上＝搬进去；点在车间里的文件/目录上＝老规矩（展开 / 默认程序打开）。
    /// </summary>
    private void OnShopTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ShopTree.SelectedItem is not TreeViewItem node)
            return;

        if (_shopNodes.TryGetValue(node, out var site))
        {
            e.Handled = true;
            EnterShop(site);
            return;
        }

        if (node.Tag is not string path)
            return;

        try
        {
            if (Directory.Exists(path))
                node.IsExpanded = true;
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, "打不开：" + path + "\n" + ex.Message, "车间",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>＋ 新建车间：起名 + 备注 → 建目录 → 登记 → 直接进去（与 ws_site_new 完全一致）。</summary>
    private void OnNewShopClick(object sender, RoutedEventArgs e)
    {
        var sites = _sites;
        if (sites is null || _workspaceSession is null)
            return;

        var (ok, name, note) = AskNewShop();
        if (!ok)
            return;

        var site = sites.Create(name, note, out var error);

        if (site is null)
        {
            MessageBox.Show(this, error, "新建车间", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadShops();
        EnterShop(site);
    }

    /// <summary>
    /// 极简输入框：车间名 + 备注。
    /// 不引入新的 XAML 窗口：一个字段数得过来的表单，为它单独加一个 .xaml 只是多一份要同步的东西。
    /// </summary>
    private (bool Ok, string? Name, string? Note) AskNewShop()
    {
        var win = new Window
        {
            Title = "新建车间",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };

        var panel = new StackPanel { Margin = new Thickness(16) };

        panel.Children.Add(new TextBlock
        {
            Text = "车间名（留空自动生成）",
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
        });

        var nameBox = new TextBox { FontSize = 12, Padding = new Thickness(4) };
        panel.Children.Add(nameBox);

        panel.Children.Add(new TextBlock
        {
            Text = "备注：这处车间干什么用",
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 4),
        });

        var noteBox = new TextBox { FontSize = 12, Padding = new Thickness(4) };
        panel.Children.Add(noteBox);

        panel.Children.Add(new TextBlock
        {
            Text = @"同名不能重复；目录会建在 <库房>\sites\ 下；建完直接进去。",
            FontSize = 10,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("FgMuted"),
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var okButton = new Button
        {
            Content = "建",
            Padding = new Thickness(14, 2, 14, 2),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true,
        };

        var cancelButton = new Button
        {
            Content = "取消",
            Padding = new Thickness(14, 2, 14, 2),
            IsCancel = true,
        };

        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);

        win.Content = panel;

        okButton.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => nameBox.Focus();

        return win.ShowDialog() == true
            ? (true, nameBox.Text, noteBox.Text)
            : (false, null, null);
    }

    private void LoadTree(string root)
    {
        FileTree.Items.Clear();

        StorehouseHint.Text = root;

        if (!Directory.Exists(root))
            return;

        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name))
            name = root;

        var node = MakeNode(root, isDir: true, label: name);
        node.IsExpanded = true;
        FileTree.Items.Add(node);
    }

    private TreeViewItem MakeNode(string path, bool isDir, string? label = null)
    {
        var item = new TreeViewItem
        {
            Tag = path,
            Header = BuildHeader(label ?? Path.GetFileName(path), isDir),

            // 树里只显示文件名，遇到 README / .env 这种同名文件就分不清了；
            // 长路径也会被横向滚动藏起来。所以每个节点都自带完整路径的悬停说明。
            ToolTip = BuildNodeTip(path, isDir),
        };

        if (isDir)
        {
            // 塞一个 null 占位：让节点显示展开箭头，真正内容等第一次展开才枚举。
            // 否则工作台里几个大目录会在启动时把窗口卡住。
            item.Items.Add(null);
            item.Expanded += OnTreeNodeExpanded;
        }

        return item;
    }

    /// <summary>
    /// 文件树节点的悬停说明：完整路径 + 类型 + 大小 / 修改时间 + 双击行为。
    ///
    /// 为什么在「建节点时」拼好，而不是悬停时再取：
    ///   节点的数据只是一个路径字符串（<c>Tag</c>），没有可绑定的对象；
    ///   而大小、时间属于文件系统的瞬时状态，悬停是高频事件，
    ///   每次悬停都去碰磁盘没有必要。建节点时取一次即可。
    ///
    /// 为什么整段包在 try 里：
    ///   权限不足、文件刚好被删、网络盘掉线，都可能在读属性时抛异常。
    ///   悬停说明只是「锦上添花」，绝不能因为它让整棵树建不出来 ——
    ///   那种故障看起来会是「侧边栏莫名其妙空了」，很难联想到 ToolTip。
    /// </summary>
    private static string BuildNodeTip(string path, bool isDir)
    {
        var sb = new StringBuilder(path);

        try
        {
            if (isDir)
            {
                var di = new DirectoryInfo(path);
                sb.Append("\n目录");
                if (di.Exists)
                    sb.Append(" · 修改于 ").Append(di.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                sb.Append("\n单击左侧三角展开 / 折叠；双击同样可以展开。");
            }
            else
            {
                var fi = new FileInfo(path);
                sb.Append("\n文件");
                if (fi.Exists)
                {
                    sb.Append(" · ").Append(WorkspaceFilters.HumanSize(fi.Length));
                    sb.Append(" · 修改于 ").Append(fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
                }
                sb.Append("\n双击：用系统默认程序打开。");
            }
        }
        catch (Exception)
        {
            // 只丢掉「大小 / 时间」这一层信息，路径照常显示
            sb.Append("\n（读取详细信息失败：可能已删除，或没有访问权限）");
        }

        return sb.ToString();
    }

    private StackPanel BuildHeader(string text, bool isDir)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        var icon = new TextBlock
        {
            Text = isDir ? "\uE8B7" : "\uE8A5",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
        };

        // 图标色走资源引用：换肤时文件树的图标也跟着变
        icon.SetResourceReference(TextBlock.ForegroundProperty, isDir ? "IconAmber" : "IconGray");
        panel.Children.Add(icon);

        panel.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return panel;
    }

    private void OnTreeNodeExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string dirPath)
            return;

        // 占位还在才加载；已经换成真实子节点就跳过，避免重复枚举
        if (item.Items.Count != 1 || item.Items[0] is not null)
            return;

        item.Items.Clear();
        PopulateDirectory(item, dirPath);
    }

    private void PopulateDirectory(TreeViewItem parent, string dirPath)
    {
        try
        {
            var di = new DirectoryInfo(dirPath);

            foreach (var sub in di.GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (IsIgnoredDir(sub.Name))
                    continue;

                if ((sub.Attributes & FileAttributes.Hidden) != 0)
                    continue;

                parent.Items.Add(MakeNode(sub.FullName, isDir: true));
            }

            foreach (var file in di.GetFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                if ((file.Attributes & FileAttributes.Hidden) != 0)
                    continue;

                parent.Items.Add(MakeNode(file.FullName, isDir: false));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 没权限就静默跳过，不打断浏览
        }
        catch (IOException ex)
        {
            Log("读取目录失败：" + ex.Message);
        }
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTree.SelectedItem is not TreeViewItem node || node.Tag is not string path)
            return;

        try
        {
            if (Directory.Exists(path))
                node.IsExpanded = true;
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("打开失败：" + ex.Message);
        }
    }

    // ---------- 菜单 / 工具栏命令 ----------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ClearChat("已新建会话");
            return;
        }

        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ReloadConfig();
        }
    }

    // ---------- 配置热重载 / 预设（2026-11-06 新增） ----------

    /// <summary>热重载进行中（防止菜单连点导致并发重建）。</summary>
    private bool _reloading;

    /// <summary>
    /// 热重载：重读 .env + 激活的预设，再把「依赖这些值的对象」全部重建。
    /// </summary>
    /// <remarks>
    /// ★ 只把环境变量改掉是不够的。<see cref="DeepSeekClient"/>、<see cref="NetService"/>、
    ///   <see cref="VerifierService"/> 都是在构造时把配置**抄进去**的，
    ///   所以「环境变量更新了、请求还发往旧地址」是这个功能最典型的假成功。
    ///   重建范围必须与 <see cref="BootAgent"/> 一致 —— 那边加了一个连接，这里就要跟一处。
    ///
    /// ★ 正在生成的回合不打断：<see cref="AgentLoop.SwapLlm"/> 只影响下一次请求。
    ///   中途换客户端会让同一条回答前后来自两个模型，比不换更糟。
    /// </remarks>
    private bool ReloadConfig()
    {
        if (_guard is null || _context is null)
            return false;

        if (_reloading)
            return false;

        _reloading = true;

        try
        {
            var result = EnvRuntime.Reload(_guard.Root);

            foreach (var warning in result.Warnings)
                Log("[配置] ⚠ " + warning);

            RebuildConnections();
            RebuildPresetMenu();

            Log("[配置] " + result.Describe());

            if (result.Touched)
            {
                AppendMessage("system",
                    "配置已热重载：" + result.Describe()
                    + (result.Changed.Count > 0
                        ? "\n变更：" + string.Join("、", result.Changed.Take(8))
                          + (result.Changed.Count > 8 ? " …" : string.Empty)
                        : string.Empty));
            }
            else
            {
                StatusText = "配置已重读，没有变化";
            }

            return true;
        }
        catch (Exception ex)
        {
            Log("[配置] 热重载失败：" + ex.Message);
            AppendMessage("system", "配置热重载失败（连接保持不变）：" + ex.Message);
            return false;
        }
        finally
        {
            _reloading = false;
        }
    }

    /// <summary>按当前环境变量重建三处连接。没有可用 key 时保持旧客户端，而不是把程序弄哑。</summary>
    private void RebuildConnections()
    {
        // 1) 联网：HttpClient / 限速桶本来就随实例走；但台账（UsageLedger）是账本，
        //    必须复用同一个实例 —— 换一个等于把已花的钱从账上抹掉。
        var netOptions = NetOptions.FromEnvironment();

        _net = new NetService(netOptions, _ledger);

        if (_context is not null)
            _context.Net = _net;

        Log("[联网] " + netOptions.Describe());

        // 2) 主模型
        if (!LlmOptions.TryResolveApiKey(out _, out _))
        {
            AppendMessage("system", "配置里没有可用的 API Key，模型连接保持不变（其它改动已生效）。");
            return;
        }

        var options = LlmOptions.FromEnvironment();
        var llm = new DeepSeekClient(options);

        _llm = llm;
        _agent?.SwapLlm(llm);

        ModelText = options.Model;

        Log("[配置] 模型 " + options.Describe());

        // 3) 审查员：VERIFIER_* 常常跟着预设一起换（切到 grok 后仍要有人审）
        _verifyOptions = VerifyOptions.FromEnvironment(options);

        _verifier = new VerifierService(
            _verifyOptions,
            log: msg => _ui?.Post(new UiEvent.PushSystem("\uD83E\uDDFF " + msg)),
            logDirectory: Path.Combine(_guard?.Root ?? ".", "logs", "verify"));

        _verify = new VerifySession(_verifier, msg => Log(msg));
        _agent?.SwapVerify(_verify);

        SyncVerifyMenu();

        Log("[配置] 双人核验 " + _verifyOptions.Describe());

        StatusText = "模型：" + options.Model
            + "｜核验：" + (_verifyOptions.IsEnabled ? _verifyOptions.Mode.ToString() : "关")
            + "｜工作台：" + (_guard?.Root ?? "未加载");
    }

    /// <summary>重建「配置 ▸ 预设」子菜单。勾选状态只在这一处生成，切完必须重建。</summary>
    private void RebuildPresetMenu()
    {
        PresetMenu.Items.Clear();

        if (_guard is null)
            return;

        var store = new ConfigPresetStore(_guard.Root);
        var active = EnvRuntime.ActivePresetName(_guard.Root);

        var none = new MenuItem
        {
            Header = "（不激活预设，只用 .env）",
            IsCheckable = true,
            IsChecked = active is null,
            ToolTip = "回到「只用 .env」。预设带进来的键会在重载时被清掉，回到 .env 的值或程序默认值。",
        };

        none.Click += (_, _) => ActivatePreset(null);
        PresetMenu.Items.Add(none);
        PresetMenu.Items.Add(new Separator());

        var presets = store.List();

        if (presets.Count == 0)
        {
            PresetMenu.Items.Add(new MenuItem
            {
                Header = "（还没有预设 —— 用下面「把当前配置存为预设」建一个）",
                IsEnabled = false,
            });

            return;
        }

        foreach (var preset in presets)
        {
            var item = new MenuItem
            {
                Header = preset.Name + "　—　" + preset.Describe(),
                IsCheckable = true,
                IsChecked = string.Equals(preset.Name, active, StringComparison.OrdinalIgnoreCase),
                ToolTip = preset.FilePath + "\n最后修改 " + preset.Modified.ToString("yyyy-MM-dd HH:mm"),
            };

            var name = preset.Name;
            item.Click += (_, _) => ActivatePreset(name);

            PresetMenu.Items.Add(item);
        }
    }

    private void OnReloadConfig(object sender, RoutedEventArgs e) => ReloadConfig();

    /// <summary>激活（或取消）一个预设：先写进 .env，再热重载。</summary>
    private void ActivatePreset(string? name)
    {
        if (_guard is null)
            return;

        try
        {
            var envPath = EnvRuntime.SetActivePreset(_guard.Root, name, out var backup);

            Log("[配置] " + (name is null ? "已取消预设" : "激活预设「" + name + "」")
                + " → " + envPath + (backup is null ? string.Empty : "（已备份 " + backup + "）"));

            if (!ReloadConfig())
                return;

            StatusText = name is null ? "已取消预设，回到 .env" : "已切换到预设：" + name;
        }
        catch (Exception ex)
        {
            Log("[配置] 切换预设失败：" + ex.Message);
            AppendMessage("system", "切换预设失败：" + ex.Message);
        }
    }

    /// <summary>把当前真正生效的那套连接配置存成预设。名字取「模型名 (主机名)」，同名先问。</summary>
    private void OnSavePresetAs(object sender, RoutedEventArgs e)
    {
        if (_guard is null)
            return;

        var captured = ConfigPresetStore.CaptureFromEnvironment();

        if (captured.Count == 0)
        {
            MessageBox.Show(this,
                "当前没有任何可保存的连接配置（连 FATFISH_API_KEY 都没有）。\n先让程序跑起来再存。",
                "没有可保存的配置", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        var store = new ConfigPresetStore(_guard.Root);
        var name = SuggestPresetName(captured);

        if (store.Exists(name))
        {
            var overwrite = MessageBox.Show(this,
                "预设「" + name + "」已经存在，覆盖它吗？\n（旧文件会先备份到 _backup）",
                "覆盖预设", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (overwrite != MessageBoxResult.Yes)
                return;
        }

        try
        {
            var path = store.Save(name, captured);

            Log("[配置] 已保存预设「" + name + "」→ " + path);
            AppendMessage("system", "已保存预设「" + name + "」：" + path);
            StatusText = "已保存预设：" + name;

            RebuildPresetMenu();
        }
        catch (Exception ex)
        {
            Log("[配置] 保存预设失败：" + ex.Message);
            AppendMessage("system", "保存预设失败：" + ex.Message);
        }
    }

    /// <summary>预设默认名：<c>模型名 (主机名)</c>。主机名让人一眼看出花的是哪家的钱。</summary>
    private static string SuggestPresetName(IReadOnlyDictionary<string, string?> values)
    {
        var model = values.TryGetValue("FATFISH_MODEL", out var m) && !string.IsNullOrWhiteSpace(m)
            ? m!.Trim()
            : "未命名";

        var host = string.Empty;

        if (values.TryGetValue("FATFISH_BASE_URL", out var b) && !string.IsNullOrWhiteSpace(b))
        {
            try
            {
                host = new Uri(b!.Trim()).Host;
            }
            catch (UriFormatException)
            {
                host = string.Empty;
            }
        }

        var name = string.IsNullOrWhiteSpace(host) ? model : model + " (" + host + ")";

        try
        {
            return ConfigPresetStore.Sanitize(name);
        }
        catch (ArgumentException)
        {
            return "预设";
        }
    }

    private void OnOpenPresetDir(object sender, RoutedEventArgs e)
    {
        if (_guard is null)
            return;

        try
        {
            var dir = new ConfigPresetStore(_guard.Root).Directory;
            Directory.CreateDirectory(dir);

            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("打开预设目录失败：" + ex.Message);
        }
    }

    /// <summary>「我改的为什么没生效」—— 把这次真正读的是哪份文件、哪些键来自预设列出来。</summary>
    private void OnShowConfigSource(object sender, RoutedEventArgs e)
    {
        if (_guard is null)
            return;

        var (values, envPath, presetName, warnings) = EnvRuntime.Compose(_guard.Root);

        var fromPreset = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (presetName is not null)
        {
            var preset = new ConfigPresetStore(_guard.Root).Load(presetName);

            if (preset is not null)
                foreach (var key in preset.Keys)
                    fromPreset.Add(key);
        }

        var sb = new StringBuilder();
        sb.AppendLine("配置文件：" + (envPath ?? "（未找到）"));
        sb.AppendLine("激活预设：" + (presetName ?? "（无）"));
        sb.AppendLine("界面显示的模型：" + (ModelText ?? "（未初始化）"));
        sb.AppendLine();

        foreach (var key in new[]
                 {
                     "FATFISH_MODEL", "FATFISH_BASE_URL", "FATFISH_API_KEY",
                     "VERIFIER_MODEL", "VERIFIER_BASE_URL", "VERIFIER_API_KEY",
                 })
        {
            var raw = values.TryGetValue(key, out var v) ? v : null;

            var shown = key.EndsWith("KEY", StringComparison.Ordinal)
                ? MaskKey(raw)
                : string.IsNullOrWhiteSpace(raw) ? "（未设置）" : raw;

            sb.AppendLine(key + " = " + shown + (fromPreset.Contains(key) ? "   ← 预设" : string.Empty));
        }

        foreach (var warning in warnings)
            sb.AppendLine("⚠ " + warning);

        MessageBox.Show(this, sb.ToString(), "配置来源");
    }

    private static string MaskKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "（未设置）"
            : value.Length <= 8 ? "****" : "***" + value[^4..];

    private void OnNewSession(object sender, RoutedEventArgs e) => ClearChat("已新建会话");

    // ---------- 双人核验 ----------

    /// <summary>
    /// 切换核验模式（AUTO / ALL / OFF）。
    ///
    /// ★ 立刻生效：<see cref="VerifyOptions.Mode"/> 是唯一可写字段，
    ///   而 <see cref="AgentLoop"/> 每轮都现读它 —— 不需要重建 AgentLoop，
    ///   也不会打断正在进行的回合。
    /// </summary>
    private void OnVerifyMode(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string tag || _verifyOptions is null)
            return;

        _verifyOptions.Mode = tag switch
        {
            "all" => VerifyMode.All,
            "off" => VerifyMode.Off,
            _ => VerifyMode.Auto,
        };

        SyncVerifyMenu();

        var on = _verifyOptions.IsEnabled;

        StatusText = on
            ? $"双人核验：{_verifyOptions.Mode}"
            : "双人核验已关闭（人工报批仍生效）";

        Log("双人核验 → " + (on ? _verifyOptions.Mode.ToString() : "Off"));

        AppendMessage("system", on
            ? $"🧿 双人核验已切换为 {_verifyOptions.Mode}：危险动作会先交给审查员 AI 复核。"
            : "⚠️ 双人核验已关闭：只剩人工报批一道闸。");
    }

    /// <summary>把菜单勾选状态同步到当前模式（启动时也可能被 .env 改过）。</summary>
    private void SyncVerifyMenu()
    {
        if (_verifyOptions is null)
            return;

        var current = _verifyOptions.Mode switch
        {
            VerifyMode.All => "all",
            VerifyMode.Off => "off",
            _ => "auto",
        };

        foreach (var child in VerifyMenu.Items.OfType<MenuItem>())
            child.IsChecked = (child.Tag as string) == current;
    }

    private void OnClearChat(object sender, RoutedEventArgs e) => ClearChat("已清空对话记录");

    private void ClearChat(string note)
    {
        _messages.Clear();

        // 只清对话历史，system prompt 保留（ResetHistory 的语义就是如此）
        _agent?.ResetHistory();

        // 用户原声也跟着清：它记录的是「当前这段对话里用户亲手下过什么命令」，
        // 换了一段对话之后再拿去判断授权，会把上一段的授权误当成这一段的。
        _userEvents?.Clear();

        UpdateMessageCount();
        UsageText = "—";
        StatusText = note;
        Log(note);

        InputBox.Focus();
    }

    private void OnClearInput(object sender, RoutedEventArgs e)
    {
        InputBox.Clear();
        InputBox.Focus();
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (!_isRunning)
        {
            StatusText = "当前没有正在进行的回合";
            return;
        }

        _runCts?.Cancel();
        StatusText = "正在停止…";
    }

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        if (_guard is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _guard.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("打开目录失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 打开配置编辑器。
    /// </summary>
    /// <remarks>
    /// 三个刻意的选择：
    ///   1. <b>编辑的是「实际生效的那份 .env」</b>，不是「工作台根目录下的 .env」。
    ///      本仓库的 C# 版放在 workspace\FatFishCS 下，而 workspace\ 自己也有一份旧 .env；
    ///      向上搜索会在 workspace 那层停住，于是「根目录下的 .env」和「真正被读的那份」
    ///      完全可能是两个文件。用 EnvFile.LoadNearest 解析，与启动时同一套逻辑。
    ///   2. <b>文件不存在也照样打开</b>：编辑器的「保存」会新建一份。
    ///      以前这里会弹一个「没有 .env」的框然后什么都不做 —— 想从零配一次的人就卡在这。
    ///   3. <b>重启由本窗口负责</b>：编辑器只置一个标志，因为「重启程序」是宿主的事，
    ///      一个编辑配置的窗口不该知道怎么拉起进程。
    /// </remarks>
    private void OnOpenEnv(object sender, RoutedEventArgs e)
    {
        if (_guard is null)
            return;

        // ★ 非模态：编辑器开着的时候，主窗口照常能用（看会话、跑工具、按 Esc）。
        //   以前用 ShowDialog，改一次 .env 的那几分钟里整个程序是冻住的。
        if (_envWindow is not null)
        {
            _envWindow.Activate();
            return;
        }

        var (resolved, _) = EnvFile.LoadNearest(_guard.Root);
        var path = resolved ?? Path.Combine(_guard.Root, ".env");

        var dlg = new EnvEditorWindow(path, Path.Combine(_guard.Root, "_backup"))
        {
            Owner = this,
        };

        dlg.Closed += (_, _) =>
        {
            _envWindow = null;

            if (dlg.RestartRequested)
            {
                Relaunch();
                return;
            }

            // 以前这里是 `ShowDialog() == true`，可编辑器从来没设过 DialogResult，
            // 所以这段日志**一次也没打出来过**。现在读它自己的 Saved 标志。
            if (dlg.Saved)
            {
                Log("配置已保存：" + path + "（重启后生效）");
                StatusText = "配置已保存，重启后生效";
            }
        };

        _envWindow = dlg;
        dlg.Show();
        dlg.Activate();
    }

    /// <summary>还开着的 .env 编辑器（非模态，只留一份）。</summary>
    private EnvEditorWindow? _envWindow;

    /// <summary>
    /// 重启自己。
    /// </summary>
    /// <remarks>
    /// 命令行怎么拼由 <see cref="RelaunchCommand"/> 决定（有单测）——
    /// 这个程序既可能被 `dotnet run` 拉起（argv[0] 是 dll），也可能是发布后的 exe，
    /// 两种情况下 argv 的形状不同，拼错了不会报错，只会「点了没反应」。
    /// </remarks>
    private void Relaunch()
    {
        try
        {
            var plan = RelaunchCommand.Build(Environment.ProcessPath, Environment.GetCommandLineArgs());

            Process.Start(new ProcessStartInfo(plan.FileName, plan.ToCommandLine())
            {
                UseShellExecute = true,
            });

            Log("正在重启：" + plan.Describe());

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log("重启失败：" + ex.Message + "　请手动关闭并重新打开程序。");
            MessageBox.Show(this,
                "自动重启失败：\n" + ex.Message + "\n\n请手动关闭窗口再重新打开。配置已经保存好了。",
                "重启失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        if (_messages.Count == 0)
        {
            StatusText = "没有可复制的对话";
            return;
        }

        var sb = new StringBuilder();

        foreach (var m in _messages)
            sb.Append('[').Append(m.TimeText).Append("] ").Append(m.NameText).Append('：').AppendLine(m.Text);

        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText = $"已复制 {_messages.Count} 条消息到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText = "复制失败：" + ex.Message;
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "肥鱼 FatFish " + AppVersion + "（C# / .NET 10 重写版）\n\n"
            + "界面风格参照 Cheat Engine 7.5。\n"
            + "内核：" + KernelStatus + "\n"
            + "工作台：" + (_guard?.Root ?? "未加载") + "\n"
            + "模型：" + (string.IsNullOrEmpty(ModelText) ? "未配置" : ModelText) + "\n\n"
            + "原 Python 版仍在 E:\\FATFISH 正常运行，本版为并行迁移分支。",
            "关于肥鱼", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnShowShortcuts(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Enter            发送\n"
            + "Shift + Enter    换行\n"
            + "Esc              停止当前回合\n"
            + "Ctrl + N         新建会话\n"
            + "Ctrl + + / -     正文字号放大 / 缩小"
            + "Ctrl + 滚轮      正文字号缩放（指针在对话区）"
            + "Ctrl + 0         字号恢复默认\n\n"
            + "双击左侧文件树里的文件，用系统默认程序打开。\n"
            + "拖动面板之间的分隔条可以调整宽度与高度。",
            "快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---------- 面板折叠 ----------

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        var show = SidebarColumn.ActualWidth < 1;
        SetSidebarVisible(show);

        if (sender is MenuItem item)
            item.IsChecked = show;
    }

    private void SetSidebarVisible(bool show)
    {
        SidebarColumn.Width = new GridLength(show ? 220 : 0);
        SideSplitterColumn.Width = new GridLength(show ? 5 : 0);
        SidePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SideSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleBottom(object sender, RoutedEventArgs e)
    {
        var show = BottomPanelRow.ActualHeight < 1;
        SetBottomVisible(show);

        if (sender is MenuItem item)
            item.IsChecked = show;
    }

    private void SetBottomVisible(bool show)
    {
        BottomPanelRow.Height = new GridLength(show ? 160 : 0);
        BottomSplitterRow.Height = new GridLength(show ? 5 : 0);
        BottomPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BottomSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 外观：字号 / 段间距 ----------

    /// <summary>从 logs/ui/appearance.json 读回上次的字号与段间距。</summary>
    private void LoadAppearance()
    {
        try
        {
            _appearance = AppearanceStore.Load(ResolveWorkspaceRoot());
            NotifyAppearanceChanged();
            Log("外观 " + _appearance);
        }
        catch (Exception ex)
        {
            // AppearanceStore.Load 自己已经吞掉 IO 异常，这里只兜最后一层：
            // 读不回外观绝不该拦住启动。
            _appearance = AppearanceSettings.Default;
            Log("外观设置载入失败（用默认值）：" + ex.Message);
        }
    }

    private void NotifyAppearanceChanged()
    {
        // 别写成 OnPropertyChanged(...)：Window 继承自 DependencyObject，
        // 那个同名 protected 方法只收 DependencyPropertyChangedEventArgs，
        // 传字符串会 CS1503 编译失败（这里已经踩过一次）。
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MessageFontSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MessageMargin)));
    }

    /// <summary>Ctrl + 加号 / 减号 / 0 —— 缩放与重置字号。</summary>
    private void OnAppearanceKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        switch (e.Key)
        {
            case Key.OemPlus:
            case Key.Add:
                e.Handled = true;
                NudgeFontSize(+AppearanceSettings.FontStep);
                break;

            case Key.OemMinus:
            case Key.Subtract:
                e.Handled = true;
                NudgeFontSize(-AppearanceSettings.FontStep);
                break;

            case Key.D0:
            case Key.NumPad0:
                e.Handled = true;
                ApplyAppearance(_appearance with { FontSize = AppearanceSettings.DefaultFontSize }, "字号已恢复默认");
                break;
        }
    }

    /// <summary>Ctrl + 滚轮调字号（指针在消息区上）。</summary>
    private void OnMessageScrollWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        // 必须吃掉事件：否则 ScrollViewer 会先滚一大段，
        // 用户的感受是「字放大了，但列表跑到别处去了」。
        e.Handled = true;
        NudgeFontSize(e.Delta > 0 ? +AppearanceSettings.FontStep : -AppearanceSettings.FontStep);
    }

    private void OnFontSizeUp(object sender, RoutedEventArgs e) => NudgeFontSize(+AppearanceSettings.FontStep);

    private void OnFontSizeDown(object sender, RoutedEventArgs e) => NudgeFontSize(-AppearanceSettings.FontStep);

    private void OnFontSizeReset(object sender, RoutedEventArgs e)
        => ApplyAppearance(_appearance with { FontSize = AppearanceSettings.DefaultFontSize }, "字号已恢复默认");

    private void OnBlockSpacingUp(object sender, RoutedEventArgs e) => NudgeBlockSpacing(+AppearanceSettings.BlockStep);

    private void OnBlockSpacingDown(object sender, RoutedEventArgs e) => NudgeBlockSpacing(-AppearanceSettings.BlockStep);

    private void OnBlockSpacingReset(object sender, RoutedEventArgs e)
        => ApplyAppearance(_appearance with { BlockSpacing = AppearanceSettings.DefaultBlockSpacing }, "段间距已恢复默认");

    private void NudgeFontSize(double delta)
        => ApplyAppearance(_appearance with { FontSize = _appearance.FontSize + delta }, null);

    private void NudgeBlockSpacing(double delta)
        => ApplyAppearance(_appearance with { BlockSpacing = _appearance.BlockSpacing + delta }, null);

    /// <summary>应用一份新外观：夹到合法范围 → 存盘 → 通知界面。</summary>
    /// <remarks>
    /// 存盘在这里做，而不是「关闭时统一写」：字号是即时反馈的东西，
    /// 用户调完很可能直接拖走窗口（甚至断电），下次打开还是旧字号会显得很没记性。
    /// </remarks>
    private void ApplyAppearance(AppearanceSettings next, string? note)
    {
        var before = _appearance;
        _appearance = next.Normalized();

        AppearanceStore.Save(ResolveWorkspaceRoot(), _appearance);
        NotifyAppearanceChanged();

        var hitEdge = before.FontSize == _appearance.FontSize
                      && before.BlockSpacing == _appearance.BlockSpacing;

        StatusText = (note ?? "外观已更新") + "：" + _appearance + (hitEdge ? "（已到边界）" : string.Empty);

        Log("外观 → " + _appearance);
    }

    // ---------- 夜间模式 ----------

    /// <summary>
    /// 工具栏「月亮／太阳」按钮与「视图 → 夜间模式」共用的切换入口。
    /// </summary>
    /// <remarks>
    /// 只调 ThemeManager 就够：它就地改<b>共享画刷</b>的颜色，所有引用这些键的控件（所有窗口）
    /// 立刻重绘。所以这里不重建任何控件，也不影响正在跑的回合。
    /// </remarks>
    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        SyncThemeUi(announce: true);
    }

    /// <summary>
    /// 把「当前是什么主题」同步到按钮图标、菜单打勾与状态栏。
    /// </summary>
    /// <remarks>
    /// 图标字形：E708 = 月亮（正在夜间），E706 = 太阳（正在日间）。
    /// 前景色用 IconViolet / IconAmber 这两个<b>键</b>，它们本身也跟着换肤走 ——
    /// 所以这里不写死任何色值。
    /// </remarks>
    private void SyncThemeUi(bool announce)
    {
        var dark = ThemeManager.Current == AppTheme.Dark;

        ThemeIcon.Text = dark ? "\uE706" : "\uE708";
        ThemeIcon.Foreground = (Brush)FindResource(dark ? "IconAmber" : "IconViolet");
        ThemeMenu.IsChecked = dark;

        ThemeButton.ToolTip =
            (dark ? "夜间模式（当前：黑底白字）" : "夜间模式（当前：白底黑字）")
            + "\n点一下切到另一种配色。运行时立即生效：不重启、不改布局，"
            + "对话内容与正在跑的回合都不受影响。";

        if (announce)
            StatusText = dark ? "已切到夜间：黑底白字" : "已切到日间：白底黑字";
    }

    // ---------- INotifyPropertyChanged ----------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

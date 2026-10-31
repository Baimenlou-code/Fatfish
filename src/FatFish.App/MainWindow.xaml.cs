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
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();

        MessageList.ItemsSource = _messages;
        LogList.ItemsSource = _logs;
        ToolList.ItemsSource = _toolLog;

        DataContext = this;

        FileTree.MouseDoubleClick += OnTreeDoubleClick;
        PreviewKeyDown += OnWindowKeyDown;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
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

    // ---------- 生命周期 ----------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ui = new UiDispatcher(Dispatcher, HandleUiEvent);

        // 窗口位置要在装配内核之前恢复：这是纯 UI 的事，
        // 而且万一内核装配失败，用户至少还能看到一个位置正常的窗口。
        RestoreWindowPlacement();

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
        var (envPath, envCount) = EnvFile.LoadNearest(_guard.Root);

        Log("配置 " + (envPath ?? "（未找到 .env）")
            + (envPath is null ? string.Empty : $"（{envCount} 项）"));

        var netOptions = NetOptions.FromEnvironment();

        // 「到底认了哪几家后端」写进日志（key 只露尾 4 位）。
        // 没有这一行时，「配置写了却没生效」只能靠猜。
        Log("[联网] " + netOptions.Describe());

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

        _plugins = PluginHost.Start(
            new PluginHostOptions
            {
                Directories = pluginDirs,
                ReservedToolNames = builtin.Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
            },
            log: msg => Log(msg));

        _policy = new ApprovalPolicy(_workspaceSession) { Plugins = _plugins };

        _context = new ToolContext
        {
            // Guard / Tickets 是「初始绑定」；Workspace 会让它们跟着会话走，
            // 这样 ws_cd 换根之后所有工具读 context.Guard 拿到的都是新根。
            Guard = _guard,
            Tickets = tickets,
            Workspace = _workspaceSession,
            Approval = _policy,
            Jobs = _jobs,
            Net = _net,
            Plugins = _plugins,
            Log = msg => _ui?.Post(new UiEvent.PushSystem("[内核] " + msg)),
        };

        _registry = new ToolRegistry(builtin, _plugins);

        WorkspaceText = "工作台：" + _guard.Root;
        ToolCountText = "工具 " + _registry.Count + " 个";

        Log("工作台根目录 " + _guard.Root);
        Log("插件 " + _plugins.Describe());

        // 直接从注册表取名，不再手写一份清单：
        // 手写的清单加工具时一定会忘，而那正是「日志说 3 个、实际 6 个」的来源。
        Log("已注册工具：" + string.Join(
            ", ", _registry.All.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal)));

        LoadTree(_guard.Root);
    }

    /// <summary>装配 LLM 与编排层。返回是否成功（缺 key 时返回 false 而不是抛异常）。</summary>
    private bool BootAgent()
    {
        if (_guard is null || _registry is null || _context is null || _policy is null)
            return false;

        var (envPath, loaded) = EnvFile.LoadNearest(_guard.Root);

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

        if (loaded > 0)
            AppendMessage("system", $"已从 {Path.GetFileName(envPath)} 载入 {loaded} 项配置。");

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
                _messages.Add(new MessageItem(e.StreamId, string.Empty, "ai"));
                UpdateMessageCount();
                ScrollToBottom();
                break;

            case UiEvent.StreamDelta e:
                {
                    var target = FindMessage(e.StreamId);
                    if (target is not null)
                    {
                        // 用 StringBuilder 累积再赋值，避免每次 string += 产生 O(n²) 拷贝
                        target.AppendText(e.Text);
                        ScrollToBottom();
                    }

                    break;
                }

            case UiEvent.StreamEnd:
                ScrollToBottom();
                break;

            case UiEvent.SetStatus e:
                StatusText = e.Text;
                break;

            case UiEvent.WorkspaceChanged e:
                {
                    // 根变了：状态栏、文件树、_guard 全部跟着换。
                    // _guard 在「打开文件夹 / 加载 .env / 会话落盘目录」等处还被用到，
                    // 在这里统一重指，比让每处都改成 _workspaceSession.Guard 更不容易漏。
                    _guard = _workspaceSession?.Guard ?? _guard;

                    WorkspaceText = "工作台：" + e.NewRoot;
                    LoadTree(e.NewRoot);

                    AppendMessage("system",
                        $"📁 工作台已切换：{e.OldRoot} → {e.NewRoot}（读凭证已清空）");

                    StatusText = "工作台已切换";
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

    private void ShowApprovalDialog(UiEvent.ApprovalRequested e)
    {
        try
        {
            var dlg = new ApprovalWindow(e.Request) { Owner = this };
            var ok = dlg.ShowDialog();

            e.Decision.TrySetResult(new ApprovalDecision(
                Approved: ok == true && dlg.Approved,
                AllowForSession: dlg.AllowForSession));
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

    private void ScrollToBottom()
    {
        // 流式输出时每秒可能触发上百次，用标志位合并成一次滚动请求
        if (_scrollQueued)
            return;

        _scrollQueued = true;

        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _scrollQueued = false;
                MessageScroll.ScrollToEnd();
            }),
            System.Windows.Threading.DispatcherPriority.Background);
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

        _ui.Post(new UiEvent.ApprovalRequested(request, tcs));

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

    private void LoadTree(string root)
    {
        FileTree.Items.Clear();

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
        }
    }

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

        var (resolved, _) = EnvFile.LoadNearest(_guard.Root);
        var path = resolved ?? Path.Combine(_guard.Root, ".env");

        var dlg = new EnvEditorWindow(path, Path.Combine(_guard.Root, "_backup"))
        {
            Owner = this,
        };

        var saved = dlg.ShowDialog();

        if (dlg.RestartRequested)
        {
            Relaunch();
            return;
        }

        if (saved == true)
        {
            Log("配置已保存：" + path + "（重启后生效）");
            StatusText = "配置已保存，重启后生效";
        }
    }

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
            "肥鱼 FatFish 2.0（C# / .NET 10 重写版）\n\n"
            + "界面风格参照 Cheat Engine 7.5。\n"
            + "内核：" + KernelStatus + "\n"
            + "工作台：" + (_guard?.Root ?? "未加载") + "\n"
            + "模型：" + (string.IsNullOrEmpty(ModelText) ? "未配置" : ModelText) + "\n\n"
            + "本版为 2.0.1；Python 版已归档到 python 分支。",
            "关于肥鱼", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnShowShortcuts(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Enter            发送\n"
            + "Shift + Enter    换行\n"
            + "Esc              停止当前回合\n"
            + "Ctrl + N         新建会话\n\n"
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

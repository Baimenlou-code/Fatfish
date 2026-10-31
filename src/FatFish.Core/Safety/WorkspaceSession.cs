namespace FatFish.Core.Safety;

/// <summary>一次工作台切换的结果。</summary>
/// <param name="Ok">是否真的切过去了。</param>
/// <param name="NeedsApproval">是否因为「移出默认作业区」而挂起待批准。</param>
/// <param name="Message">给人（和模型）看的说明。失败时就是失败原因。</param>
/// <param name="OldRoot">切换前的根；没切时为 null。</param>
/// <param name="NewRoot">切换后的根；没切时为 null。</param>
public sealed record WorkspaceSwitchResult(
    bool Ok,
    bool NeedsApproval,
    string Message,
    string? OldRoot = null,
    string? NewRoot = null)
{
    public static WorkspaceSwitchResult Fail(string message) => new(false, false, message);

    public static WorkspaceSwitchResult Pending(string message) => new(false, true, message);

    public static WorkspaceSwitchResult Switched(string oldRoot, string newRoot, string message)
        => new(true, false, message, oldRoot, newRoot);
}

/// <summary>
/// WorkspaceSession —— 可切换的工作台根目录。
///
/// 对应 Python 版 workspace.py 的 <c>_WORKSPACE_DIR</c> / <c>_PENDING_CD</c> /
/// <c>set_workspace</c> / <c>approve_pending_cd</c> / <c>is_inside_default</c>。
///
/// ★ 为什么不是「把 PathGuard.Root 改成可写」：
///   <see cref="PathGuard"/> 的全部价值来自「构造后不可变」—— 它把 Root 缓存成
///   <c>_rootWithSep</c> 用于前缀比较，一旦中途改根，缓存就会和实际不一致，
///   于是出现「IsInside 用旧根、Resolve 用新根」这种要命的错配，
///   而且是**静默**的：越界检查会时对时错，取决于调用顺序。
///
///   所以这里反过来：Guard 依然不可变，**换根 = 换一个新 Guard**。
///   <see cref="Guard"/> 是一个 volatile 读的引用，切换是一次原子替换。
///
/// ★ 两层边界，别混为一谈：
///   · <see cref="Guard"/> 的边界 = 「当前根」，防 AI 用 .. / 符号链接逃出去；
///   · <see cref="DefaultRoot"/> = 「默认作业区」，即启动时的根。它是**策略**边界，
///     不是安全边界 —— 它的作用只是决定「换到这个目录需不需要用户点头」。
///     换根之后，新根仍然有自己的 .. 检查，两者互不替代。
/// </summary>
public sealed class WorkspaceSession
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    /// <summary>串行化切换动作（同一时刻只允许一次切换生效）。</summary>
    private readonly object _gate = new();

    // volatile：读多写极少，且切换必须让其它线程立刻看见。
    // 不能用属性 + volatile，所以用字段承载，属性只做转发。
    private volatile PathGuard _guard;
    private volatile ReadTicketStore _tickets;

    private string? _pending;

    /// <param name="root">初始工作台根目录（必须已存在）。</param>
    /// <param name="defaultRoot">
    /// 默认作业区。不传时等于 <paramref name="root"/>。
    /// 它只影响「换根要不要批准」，不影响路径校验。
    /// </param>
    public WorkspaceSession(string root, string? defaultRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var full = Path.GetFullPath(root);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"工作台根目录不存在：{full}");

        _guard = new PathGuard(full);
        _tickets = new ReadTicketStore(_guard);

        DefaultRoot = NormalizeSeparators(defaultRoot is null ? _guard.Root : defaultRoot);
    }

    /// <summary>当前工作台根目录（规范化后，无尾部分隔符；盘符根如 <c>C:\</c> 保留）。</summary>
    public string Root => _guard.Root;

    /// <summary>默认作业区 —— 换到它之外的目录需要用户批准。</summary>
    public string DefaultRoot { get; }

    /// <summary>当前生效的路径守卫。</summary>
    /// <remarks>
    /// 每次换根都会换成**新实例**，所以调用方不要把它缓存进字段长期持有 ——
    /// 缓存下来的那一刻就等于「钉死在那个根上了」。工具与编排层都通过
    /// <see cref="Tools.ToolContext.Guard"/> 每次现取，正是为了避开这一点。
    /// </remarks>
    public PathGuard Guard => _guard;

    /// <summary>当前生效的读凭证库。换根后是全新的空库。</summary>
    public ReadTicketStore Tickets => _tickets;

    /// <summary>挂起中的切换目标（等待 <see cref="ApprovePending"/>）；没有则为 null。</summary>
    public string? PendingRoot
    {
        get
        {
            lock (_gate)
                return _pending;
        }
    }

    /// <summary>根目录发生变化（旧根, 新根）。在**锁外**触发，订阅者可以放心回调本类。</summary>
    public event Action<string, string>? RootChanged;

    /// <summary>判断一个绝对路径是否位于默认作业区内（含自身）。</summary>
    public bool IsInsideDefault(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        string full;
        try
        {
            full = NormalizeSeparators(fullPath);
        }
        catch
        {
            return false;
        }

        if (string.Equals(full, DefaultRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = DefaultRoot.EndsWith(Sep) ? DefaultRoot : DefaultRoot + Sep;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 切换工作台根目录。
    ///
    /// 规则（与 Python 版一致）：
    ///   切到默认作业区之内 → 直接生效；
    ///   切到默认作业区之外 → 不执行，只登记为待批准，等用户确认后调 <see cref="ApprovePending"/>。
    /// </summary>
    public WorkspaceSwitchResult ChangeTo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return WorkspaceSwitchResult.Fail("路径为空");

        string target;
        try
        {
            target = NormalizeSeparators(Expand(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return WorkspaceSwitchResult.Fail($"路径无法解析：{ex.Message}");
        }

        if (!Directory.Exists(target))
        {
            return WorkspaceSwitchResult.Fail(
                File.Exists(target) ? $"不是目录：{target}" : $"路径不存在：{target}");
        }

        // 同根：不重装 Guard（那会让刚签的读凭证无故失效）
        if (string.Equals(target, _guard.Root, StringComparison.OrdinalIgnoreCase))
        {
            return new WorkspaceSwitchResult(
                true, false, $"当前已在该目录：{target}", _guard.Root, target);
        }

        if (!IsInsideDefault(target))
        {
            lock (_gate)
                _pending = target;

            return WorkspaceSwitchResult.Pending(
                "⚠️ 该操作会把工作台移出默认作业区，需用户批准后才能执行。\n" +
                $"目标：{target}\n" +
                $"默认作业区：{DefaultRoot}\n" +
                "请先向用户说明目标目录与理由，用户同意后再调用 ws_cd_approve 放行。");
        }

        return Commit(target, approved: false);
    }

    /// <summary>批准并执行挂起的切换请求。</summary>
    public WorkspaceSwitchResult ApprovePending()
    {
        string? target;
        lock (_gate)
        {
            target = _pending;
            _pending = null;
        }

        if (target is null)
            return WorkspaceSwitchResult.Fail("当前没有待批准的切换请求");

        if (!Directory.Exists(target))
            return WorkspaceSwitchResult.Fail($"目标已不存在或不是目录：{target}");

        return Commit(target, approved: true);
    }

    /// <summary>恢复默认作业区。</summary>
    public WorkspaceSwitchResult Reset()
    {
        if (!Directory.Exists(DefaultRoot))
            return WorkspaceSwitchResult.Fail($"默认作业区已不存在：{DefaultRoot}");

        if (string.Equals(DefaultRoot, _guard.Root, StringComparison.OrdinalIgnoreCase))
            return new WorkspaceSwitchResult(true, false, $"当前已在默认作业区：{DefaultRoot}",
                DefaultRoot, DefaultRoot);

        return Commit(DefaultRoot, approved: false);
    }

    /// <summary>撤销挂起请求（用户明确说「不换了」时用）。</summary>
    public bool CancelPending()
    {
        lock (_gate)
        {
            var had = _pending is not null;
            _pending = null;
            return had;
        }
    }

    /// <summary>当前状态的一行描述（供 ws_where / 状态栏复用）。</summary>
    public string Describe()
    {
        var pending = PendingRoot;
        var inside = IsInsideDefault(_guard.Root) ? "默认作业区内" : "已移出默认作业区";
        return pending is null
            ? $"{_guard.Root}（{inside}）"
            : $"{_guard.Root}（{inside}）｜待批准：{pending}";
    }

    // ------------------------------------------------------------------

    /// <summary>真正执行切换：换 Guard、换凭证库、清挂起，最后在锁外发事件。</summary>
    private WorkspaceSwitchResult Commit(string target, bool approved)
    {
        string oldRoot;

        lock (_gate)
        {
            oldRoot = _guard.Root;

            // 新 Guard 自带一次存在性校验；走到这里理论上不会抛，兜住只是为了给出可读消息
            PathGuard guard;
            try
            {
                guard = new PathGuard(target);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException)
            {
                return WorkspaceSwitchResult.Fail(ex.Message);
            }

            _guard = guard;

            // ★ 换根必须换凭证库，不能只 Clear()。
            //   凭证里存的是**绝对路径**：换根之后同一个相对路径指向完全不同的文件，
            //   而 IsValid 只比对内容指纹 —— 两个内容相同的文件会被认成同一个，
            //   于是「读过 A 根下的 notes.txt」就能直接改 B 根下的 notes.txt。
            //   换库之后，旧凭证连查找都查不到。
            _tickets = new ReadTicketStore(guard);

            _pending = null;
        }

        var message = (approved ? "已批准，工作台切换到：" : "工作台已切换到：") + target;
        RootChanged?.Invoke(oldRoot, target);

        return WorkspaceSwitchResult.Switched(oldRoot, target, message);
    }

    /// <summary>
    /// 展开用户可能写进来的路径写法：引号、<c>~</c>、<c>%ENV%</c>、相对路径。
    /// </summary>
    /// <remarks>
    /// 相对路径按**当前根**解析，而不是按进程 cwd ——
    /// Python 版用的是 os.path.abspath（即进程 cwd）。两者在 CLI 里恰好相同
    /// （启动时 cwd == 根），但在 GUI 里进程 cwd 是程序目录、根是用户选的目录，
    /// 那时按 cwd 解析会让 `ws_cd data` 跳到程序目录下的 data，与模型和用户的直觉都不符。
    /// </remarks>
    private string Expand(string raw)
    {
        // 模型偶尔会把路径连引号一起带进来（'E:\x'、"E:\x"），Python 版也做了这层清洗
        var s = raw.Trim().Trim('\'', '"', '`', '“', '”', '‘', '’');

        s = Environment.ExpandEnvironmentVariables(s);

        if (s == "~" || s == "~/" || s == "~\\")
            s = UserHome;
        else if (s.StartsWith("~/", StringComparison.Ordinal) || s.StartsWith("~\\", StringComparison.Ordinal))
            s = Path.Combine(UserHome, s[2..]);

        return Path.IsPathRooted(s)
            ? Path.GetFullPath(s)
            : Path.GetFullPath(s, _guard.Root);
    }

    private static string UserHome =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// 去掉末尾分隔符，但**保留盘符根**（<c>C:\</c> 不能变成 <c>C:</c> ——
    /// 后者在 Windows 上是「C 盘的当前目录」，语义完全不同）。
    /// </summary>
    private static string NormalizeSeparators(string path)
    {
        var full = Path.GetFullPath(path);

        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root) &&
            !string.Equals(full, root, StringComparison.OrdinalIgnoreCase) &&
            full.EndsWith(Sep))
        {
            full = full.TrimEnd(Sep);
        }

        return full;
    }
}

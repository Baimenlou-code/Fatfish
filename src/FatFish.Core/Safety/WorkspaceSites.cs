using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FatFish.Core.Safety;

/// <summary>
/// 一个「车间」—— 临时工作台。
/// </summary>
/// <remarks>
/// ★ 为什么要有这个概念：
///   <see cref="WorkspaceSession"/> 换根是**单槽**的 —— 同一时刻只有一个根。
///   干活时真正需要的却是「一个常用的家里，外加临时几处外边」：
///   处理一份微信收到的文档、看一眼某个旧项目、跑一次数据分析，
///   每处都开一个新进程、或每次都手打一长串绝对路径再走审批，都太贵。
///
/// ★ 所以车间不是「第二个根」，而是**给某个目录起个名字并记住它**：
///   进车间仍然走 <see cref="WorkspaceSession.ChangeTo"/>，
///   也就是说边界检查、待批准、读凭证清空，一条规则都没有被绕过。
///   本类只负责「记住有哪些、叫什么、在哪」，不碰任何路径校验。
/// </remarks>
public sealed class WsSite
{
    /// <summary>车间名（短名，唯一，用来 enter / drop）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>绝对路径。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>备注：这处车间是干什么的。</summary>
    public string? Note { get; set; }

    /// <summary>创建时间（本地时区）。</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 车间登记簿 —— 一个库房（<see cref="WorkspaceSession.DefaultRoot"/>）
/// 外加若干临时车间。
///
/// 落盘位置：<c>&lt;库房&gt;\_sites.json</c>。
/// 选这个位置而不是程序目录的理由：登记簿描述的是「这台机器上这个工作台里有哪些车间」，
/// 换一台机器或换一个工作台，那份清单本来就不该跟过去；而且用户能直接打开看一眼、手改一行。
///
/// ★ 这里**不做**任何权限判断：
///   「能不能切过去」是 <see cref="WorkspaceSession"/> 的事（默认作业区之外要用户批准）。
///   如果把边界逻辑抄一份到这里，两处迟早会不一致 —— 而不一致的方向一定是「更宽松的那份生效」。
/// </summary>
public sealed class WorkspaceSites
{
    /// <summary>车间默认落在库房下的这个子目录里。</summary>
    public const string FolderName = "sites";

    /// <summary>登记簿文件名（下划线前缀 = 内部文件，与 _backup 同惯例）。</summary>
    public const string RegistryFileName = "_sites.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // 不转义中文：登记簿是给人看的，车间名几乎一定是中文
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly WorkspaceSession _session;
    private readonly List<WsSite> _sites = new();

    public WorkspaceSites(WorkspaceSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Reload();
    }

    /// <summary>登记簿文件的绝对路径。</summary>
    public string RegistryPath => System.IO.Path.Combine(_session.DefaultRoot, RegistryFileName);

    /// <summary>新车间的默认父目录。</summary>
    public string SitesRoot => System.IO.Path.Combine(_session.DefaultRoot, FolderName);

    /// <summary>上次读取登记簿时的错误（文件损坏等）。null 表示一切正常。</summary>
    public string? LoadError { get; private set; }

    /// <summary>当前登记的全部车间（按创建时间正序）。</summary>
    public IReadOnlyList<WsSite> All
    {
        get
        {
            lock (_gate)
                return _sites.OrderBy(s => s.CreatedAt).ToArray();
        }
    }

    /// <summary>从磁盘重读（用户手改了登记簿时用）。</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _sites.Clear();
            LoadError = null;

            try
            {
                if (!File.Exists(RegistryPath))
                    return;

                var json = File.ReadAllText(RegistryPath);
                var loaded = JsonSerializer.Deserialize<List<WsSite>>(json, Json);

                if (loaded is null)
                    return;

                // 手改出来的脏数据不该让整个功能失效：逐条筛，坏的丢掉
                foreach (var s in loaded)
                {
                    if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Path))
                        continue;

                    _sites.Add(s);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                LoadError = $"登记簿读取失败（当作空清单继续）：{ex.Message}";
            }
        }
    }

    /// <summary>
    /// 开一处新车间：建目录 + 登记。不负责切换（切换由工具层做，好让结果里能明写新根）。
    /// </summary>
    /// <param name="name">车间名；留空则自动生成 <c>site_MMdd_HHmm</c>。</param>
    /// <param name="note">备注，可为 null。</param>
    /// <param name="error">失败原因。</param>
    public WsSite? Create(string? name, string? note, out string error)
    {
        error = string.Empty;

        var clean = string.IsNullOrWhiteSpace(name)
            ? $"site_{DateTime.Now:MMdd_HHmm}"
            : name.Trim();

        var bad = ValidateName(clean);
        if (bad is not null)
        {
            error = bad;
            return null;
        }

        var path = System.IO.Path.Combine(SitesRoot, clean);

        lock (_gate)
        {
            if (_sites.Any(s => string.Equals(s.Name, clean, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"已存在同名车间：{clean}（换个名字，或先 ws_site_drop 注销旧的）";
                return null;
            }
        }

        if (File.Exists(path))
        {
            error = $"目标路径已被一个文件占用：{path}";
            return null;
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"建目录失败：{ex.Message}";
            return null;
        }

        var site = new WsSite
        {
            Name = clean,
            Path = System.IO.Path.GetFullPath(path),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAt = DateTimeOffset.Now,
        };

        lock (_gate)
        {
            _sites.Add(site);
        }

        Save();
        return site;
    }

    /// <summary>
    /// 按名字找车间。也接受「路径」作为键 —— 模型有时手里只有路径。
    /// </summary>
    public WsSite? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;

        var k = key.Trim();

        lock (_gate)
        {
            var byName = _sites.FirstOrDefault(s =>
                string.Equals(s.Name, k, StringComparison.OrdinalIgnoreCase));

            if (byName is not null)
                return byName;

            return _sites.FirstOrDefault(s =>
                string.Equals(Normalize(s.Path), Normalize(k), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// 注销一处车间 —— **只删登记，不删任何文件**。
    /// </summary>
    /// <remarks>
    /// ★ 为什么不做「连目录一起删」：<c>ws_delete</c> 有它自己的一套规矩
    ///   （必须先读、只删空目录、自动备份、每次单独审批）。
    ///   在这里偷偷复制一份删除逻辑，等于给「删文件」开了一条绕开审批的后门 ——
    ///   而它偏偏又是「点一下就没了」的那种。
    /// </remarks>
    public bool Drop(string? key, out string error)
    {
        error = string.Empty;

        var site = Find(key);
        if (site is null)
        {
            error = $"没有这个车间：{key}";
            return false;
        }

        if (string.Equals(Normalize(site.Path), Normalize(_session.Root), StringComparison.OrdinalIgnoreCase))
        {
            error = $"当前正站在车间「{site.Name}」里。先 ws_site_home 回库房，再注销——" +
                    "否则注销之后这个根就成了登记簿外的孤儿。";
            return false;
        }

        lock (_gate)
        {
            _sites.RemoveAll(s => string.Equals(s.Name, site.Name, StringComparison.OrdinalIgnoreCase));
        }

        Save();
        return true;
    }

    /// <summary>给模型/用户看的一行概况。</summary>
    public string Describe()
    {
        var all = All;
        var current = _session.Root;

        var here = all.FirstOrDefault(s =>
            string.Equals(Normalize(s.Path), Normalize(current), StringComparison.OrdinalIgnoreCase));

        var where = here is null ? "库房" : $"车间「{here.Name}」";
        return $"{where}（{current}）｜库房：{_session.DefaultRoot}｜登记车间 {all.Count} 处";
    }

    // ------------------------------------------------------------------

    private static string? ValidateName(string name)
    {
        if (name.Length > 60)
            return "车间名太长（上限 60 字）";

        if (name is "." or "..")
            return "车间名不能是 . 或 ..";

        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains('/') || name.Contains('\\'))
        {
            return "车间名不能包含路径分隔符或以下字符：" +
                   new string(System.IO.Path.GetInvalidFileNameChars());
        }

        // 前后空格/点会被 Windows 悄悄吃掉，导致「名字和目录名对不上」
        if (name != name.Trim() || name.EndsWith('.'))
            return "车间名不能以点结尾或带首尾空格";

        return null;
    }

    private static string Normalize(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).TrimEnd(
                System.IO.Path.DirectorySeparatorChar);
        }
        catch
        {
            return path.Trim();
        }
    }

    /// <summary>写盘。先写临时文件再整体替换 —— 中途断电也不会留下半截 JSON。</summary>
    private void Save()
    {
        List<WsSite> snapshot;
        lock (_gate)
            snapshot = _sites.OrderBy(s => s.CreatedAt).ToList();

        try
        {
            var dir = System.IO.Path.GetDirectoryName(RegistryPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = RegistryPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, Json));
            File.Move(tmp, RegistryPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去不影响本次会话里的登记（内存里已经有了），
            // 但下次启动会丢 —— 所以要说出来，而不是默默咽下
            LoadError = $"登记簿写入失败（本次有效，重启后可能丢失）：{ex.Message}";
        }
    }
}

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FatFish.Core.Llm;

namespace FatFish.Core.Session;

/// <summary>
/// 一次「恢复」的结果。
/// </summary>
public sealed record SessionLoadResult
{
    /// <summary>读的是哪个文件。</summary>
    public required string FilePath { get; init; }

    /// <summary>这个会话的 id。</summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// 恢复出来的、**可以回填给模型**的消息。
    ///
    /// 只有 user 与「不带 tool_calls 的 assistant」两类 ——
    /// 中间轮的工具往返全部丢弃，理由见 <see cref="SessionStore.LoadLatest"/>。
    /// </summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>文件里一共有几条记录（含被丢弃的 tool 行）。</summary>
    public int TotalEntries { get; init; }

    /// <summary>解析失败而被跳过的行数（崩溃时最后一行可能只写了一半）。</summary>
    public int SkippedLines { get; init; }

    /// <summary>是否丢弃了末尾那条「问了但没答完」的 user 消息。</summary>
    public bool DroppedDanglingUser { get; init; }

    /// <summary>文件最后修改时间，用于在界面上显示「恢复到什么时候」。</summary>
    public DateTimeOffset LastWrite { get; init; }
}

/// <summary>
/// 会话落盘。
///
/// 解决的问题只有一个：**关掉窗口，对话就没了。**
///
/// ============================ 为什么是 JSONL ============================
///
/// 整份 JSON 快照（每次保存重写全文）在长会话上会变成 O(n²) 的 IO：
/// 第 200 轮要重写前 199 轮的全部内容，而工具输出单条可达 30KB。
/// 崩溃/断电时还可能留下一个**结构完整但内容截断**的文件 —— 解析直接失败，
/// 整段会话全丢。
///
/// 一行一条记录则相反：写入是追加（O(1)），崩溃最多丢最后半行，
/// 前面的内容逐行可解析。坏行跳过即可，不需要「修复文件」。
///
/// ============================ 为什么恢复时要剥掉工具往返 ============================
///
/// 完整回填历史看起来最忠实，实际会炸：
///
///   1. 一条 tool 消息的回填上限是 30,000 字符（AgentOptions.MaxToolResultChars）。
///      恢复 20 轮带工具的对话，光是历史就可能几十万字符 —— 下一次提问直接超窗。
///   2. AgentLoop.TrimHistory 按**条数**裁剪，不按 token。历史一进来就超限，
///      它会立刻开始裁，而裁剪下刀点是 user 边界，可能把刚恢复的内容整段切掉。
///   3. 协议上 tool 消息必须紧跟对应的 tool_calls（同一个 assistant 消息里）。
///      只恢复一半（比如 tool 在、assistant 的 tool_calls 被过滤）会被服务端拒绝。
///
/// 所以恢复的是**对话本身**：用户说了什么、模型最终答了什么。
/// 中间「模型读了哪个文件、跑了什么命令」留在文件里给人看，但不进模型上下文。
/// 这也正是「重开窗口接着聊」应有的语义 —— 人记得结论，不记得每次工具调用的原始输出。
/// </summary>
public sealed class SessionStore : IDisposable
{
    /// <summary>记录格式版本。将来改字段时靠它判断兼容性。</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// 单条记录写入文件时的正文上限。
    ///
    /// 注意这和「回填给模型」不是一回事：tool 结果回填上限是 30,000（保留全部细节给模型推理），
    /// 而日志是给人看的 —— 4,000 字符足够看清「它读到了什么」，又不会让日志文件一天涨到几百 MB。
    /// </summary>
    public const int MaxTextChars = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _dir;

    private StreamWriter? _writer;
    private bool _broken;
    private string _sessionId = string.Empty;
    private string _filePath = string.Empty;

    /// <summary>进程内会话序号，用于让文件名严格递增（见 <see cref="StartNew"/>）。</summary>
    private static int _sequence;

    private SessionStore(string sessionDir)
    {
        _dir = sessionDir;
    }

    /// <summary>当前会话 id（每次换文件都变）。</summary>
    public string SessionId => _sessionId;

    /// <summary>当前会话文件的绝对路径。</summary>
    public string FilePath => _filePath;

    /// <summary>落盘目录。</summary>
    public string SessionDir => _dir;

    /// <summary>已写成功多少条。</summary>
    public int EntryCount { get; private set; }

    /// <summary>
    /// 落盘是否已经不可用。
    ///
    /// 一旦写失败（磁盘满、权限、文件被占用）就置位，之后**不再尝试**。
    /// 理由是每行都 try/catch 会让一次坏盘变成几万次系统调用，
    /// 而对话本身不该因为写日志失败而变慢 —— 更不能中断。
    /// </summary>
    public bool IsBroken => _broken;

    /// <summary>第一次失败的错误信息（给界面/控制台提示一次）。</summary>
    public string? LastError { get; private set; }

    // ------------------------------------------------------------------
    // 创建 / 切换
    // ------------------------------------------------------------------

    /// <summary>
    /// 在工作台下建一个落盘器并立即开一个新会话文件。
    ///
    /// 目录是 <c>&lt;root&gt;/logs/sessions/</c>，与 Python 版 <c>logs/</c> 同处一层，
    /// 但单独放进 <c>sessions/</c> —— 免得和其它日志混在一起。
    /// </summary>
    public static SessionStore Create(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("工作台根不能为空", nameof(root));

        var dir = Path.Combine(root, "logs", "sessions");
        Directory.CreateDirectory(dir);

        var store = new SessionStore(dir);
        store.StartNew();
        return store;
    }

    /// <summary>
    /// 开一个新会话文件（Ctrl+N / <c>/new</c> 会调到这里）。
    ///
    /// 旧文件不会被删、也不会被截断 —— 它是一段已经结束的对话，留着。
    /// </summary>
    public void StartNew()
    {
        lock (_gate)
        {
            CloseWriterUnsafe();

            var now = DateTimeOffset.Now;
            var sid = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                      + "-" + RandomSuffix();

            // ★ 文件名必须自带**严格递增**的时间序，因为 LoadLatest 就是靠文件名排序的。
            //
            // 只用秒级时间戳时，同一秒内开两个会话（Ctrl+N 连按两次、或测试里连开）
            // 会产生两个「时间相同」的文件，排序结果就取决于随机后缀 ——
            // 于是「恢复最近一次」可能恢复成上一次，而且表现为「对话莫名其妙少了几轮」。
            //
            // 毫秒 + 进程内递增序号：同一毫秒内也能定序；
            // 跨进程重启后序号归零，但那时时间戳必然不同，不会冲突。
            var seq = Interlocked.Increment(ref _sequence);

            var name = string.Format(
                CultureInfo.InvariantCulture,
                "chat_{0:yyyyMMdd_HHmmss_fff}_{1:D4}_{2}.jsonl",
                now, seq, RandomSuffix());

            var path = Path.Combine(_dir, name);

            _sessionId = sid;
            _filePath = path;
            _broken = false;
            LastError = null;
            EntryCount = 0;

            try
            {
                // 先看一眼能不能写：目录在、权限对，就别等到第一条消息才发现。
                var stream = new FileStream(
                    path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096, FileOptions.SequentialScan);

                _writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MarkBroken($"无法创建会话文件：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 接上一个已存在的会话文件，继续往后追加（恢复对话时用）。
    ///
    /// 为什么需要它：恢复的语义是「那段对话还在继续」。
    /// 若恢复后另开一个新文件，用户看到的是同一次对话被劈成两半，
    /// 而「上次聊到哪」这件事在文件列表里变得看不出来。
    /// </summary>
    public static SessionStore Attach(string filePath, string sessionId, int existingEntries = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
            throw new ArgumentException("会话文件路径必须包含目录", nameof(filePath));

        var store = new SessionStore(dir);
        store.AttachCore(filePath, sessionId, existingEntries);
        return store;
    }

    private void AttachCore(string filePath, string sessionId, int existingEntries)
    {
        lock (_gate)
        {
            CloseWriterUnsafe();

            _filePath = filePath;
            _sessionId = sessionId;
            _broken = false;
            LastError = null;
            EntryCount = existingEntries;

            try
            {
                // ★ 用 ReadWrite 而不是 Append：Append 模式拿不到读权限，
                //   而下面要检查「文件末尾是不是半行」。取消写位置用 Seek 到末尾来代替。
                var stream = new FileStream(
                    filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096, FileOptions.SequentialScan);

                var needsNewline = false;

                if (stream.Length > 0)
                {
                    stream.Seek(-1, SeekOrigin.End);
                    needsNewline = stream.ReadByte() != '\n';
                }

                stream.Seek(0, SeekOrigin.End);

                _writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false };

                // ★ 上次崩溃留下的半行必须先用换行隔离。
                //   否则新记录会直接黏在那半行后面，变成一条又长又坏的 JSON ——
                //   下次恢复时整段（坏行 + 之后的所有记录）一起被丢掉。
                if (needsNewline)
                    _writer.WriteLine();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MarkBroken($"无法续写会话文件：{ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------
    // 写入
    // ------------------------------------------------------------------

    /// <summary>
    /// 追加一条记录。
    ///
    /// ★ 绝不抛异常。写不进去只置 <see cref="_broken"/>，
    ///   因为「日志写不了」不该让用户聊不了天。
    /// </summary>
    public void Append(
        ChatMessage message,
        string? toolName = null,
        bool? toolOk = null,
        long? elapsedMs = null,
        IReadOnlyList<string>? toolNames = null,
        LlmUsage? usage = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            if (_broken || _writer is null)
                return;

            // 协议里 content 允许为 null（assistant 只调工具时）。
            // 这种行对「人读」有价值（能看出模型在这一轮只调了工具，一个字都没说），
            // 所以照写，只是正文为空。
            var text = message.Content ?? string.Empty;

            var row = new Dictionary<string, object?>(12)
            {
                ["v"] = FormatVersion,
                ["ts"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                ["sid"] = _sessionId,
                ["role"] = message.Role,
            };

            if (toolName is not null)
                row["tool"] = toolName;

            if (toolOk is not null)
                row["ok"] = toolOk;

            if (elapsedMs is not null)
                row["ms"] = elapsedMs;

            if (toolNames is { Count: > 0 })
                row["tools"] = toolNames;

            if (usage is not null)
            {
                row["tokens_in"] = usage.PromptTokens;
                row["tokens_out"] = usage.CompletionTokens;
            }

            if (message.ToolCalls is { Count: > 0 } calls)
            {
                // 记下 id 与参数：排查「模型为什么这么调」时，参数比正文更有用。
                row["calls"] = calls.Select(c => new Dictionary<string, object?>(3)
                {
                    ["id"] = c.Id,
                    ["name"] = c.Name,
                    ["args"] = Clip(c.Arguments, out _),
                }).ToList();
            }

            var clipped = Clip(text, out var truncated);
            if (truncated)
                row["truncated"] = true;

            row["text"] = clipped;

            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
                _writer.Flush();
                EntryCount++;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                MarkBroken($"写入会话文件失败：{ex.Message}");
            }
        }
    }

    private void MarkBroken(string error)
    {
        _broken = true;
        LastError ??= error;

        try
        {
            CloseWriterUnsafe();
        }
        catch
        {
            // 关不掉就算了，进程退出时会释放句柄
        }
    }

    private void CloseWriterUnsafe()
    {
        if (_writer is null)
            return;

        try
        {
            _writer.Flush();
            _writer.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 关闭失败不影响后续：句柄由 GC 兜底
        }
        finally
        {
            _writer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CloseWriterUnsafe();
        }
    }

    // ------------------------------------------------------------------
    // 恢复
    // ------------------------------------------------------------------

    /// <summary>
    /// 恢复**最近一次**会话。
    ///
    /// 默认取最近 40 条（user/assistant 成对，约 20 轮）。
    /// 这个数字不是随手定的：一轮对话按 2,000 字符估，40 条约 8 万字符 ≈ 4~5 万 token，
    /// 加系统提示与本次提问仍在 128K 窗口内。再多就会开始挤占回答空间，
    /// 而 TrimHistory 又是按条数裁的，容易在不知情时把恢复的内容整段切掉。
    /// </summary>
    /// <param name="root">工作台根目录。</param>
    /// <param name="maxMessages">最多恢复多少条（<=0 表示不限）。</param>
    /// <returns>没有可恢复的会话时返回 null。</returns>
    public static SessionLoadResult? LoadLatest(string root, int maxMessages = 40)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        var dir = Path.Combine(root, "logs", "sessions");
        if (!Directory.Exists(dir))
            return null;

        try
        {
            // ★ 按**文件名**倒序，而不是按修改时间。
            //
            // 文件名里编了「毫秒时间戳 + 递增序号」（见 StartNew），字典序 == 时间序，
            // 而且比 LastWriteTime 稳定：文件被复制、被工具 touch、或因备份而刷新时间戳时，
            // 修改时间会骗人，文件名不会。
            //
            // 逐个试而不是只取第一个：半途创建失败、或只写了几行就崩掉的会话文件，
            // 解析出来可能是空的 —— 那种情况下应该继续往前找上一个。
            var files = new DirectoryInfo(dir)
                .EnumerateFiles("chat_*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.Name, StringComparer.Ordinal);

            foreach (var f in files)
            {
                var result = TryLoadFile(f, maxMessages);
                if (result is not null && result.Messages.Count > 0)
                    return result;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>读一个具体的会话文件（测试与「打开历史」用）。</summary>
    public static SessionLoadResult? LoadFile(string path, int maxMessages = 40)
    {
        if (!File.Exists(path))
            return null;

        return TryLoadFile(new FileInfo(path), maxMessages);
    }

    private static SessionLoadResult? TryLoadFile(FileInfo file, int maxMessages)
    {
        try
        {
            var entries = new List<(string Role, string Text, bool HasTools, string Sid)>(64);
            var skipped = 0;

            using (var stream = new FileStream(
                       file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                       bufferSize: 4096, FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (line.Length == 0)
                        continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var rootEl = doc.RootElement;

                        if (rootEl.ValueKind != JsonValueKind.Object)
                        {
                            skipped++;
                            continue;
                        }

                        var role = rootEl.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                            ? r.GetString() ?? string.Empty
                            : string.Empty;

                        if (role.Length == 0)
                        {
                            skipped++;
                            continue;
                        }

                        var sid = rootEl.TryGetProperty("sid", out var s) && s.ValueKind == JsonValueKind.String
                            ? s.GetString() ?? string.Empty
                            : string.Empty;

                        var text = rootEl.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                            ? t.GetString() ?? string.Empty
                            : string.Empty;

                        var hasTools = rootEl.TryGetProperty("tools", out var tools)
                                       && tools.ValueKind == JsonValueKind.Array
                                       && tools.GetArrayLength() > 0;

                        entries.Add((role, text, hasTools, sid));
                    }
                    catch (JsonException)
                    {
                        // 崩溃时最后一行可能只写了一半 —— 跳过，不影响前面的内容。
                        skipped++;
                    }
                }
            }

            if (entries.Count == 0)
                return null;

            var sessionId = entries.FirstOrDefault(e => e.Role == "user").Sid;
            if (string.IsNullOrEmpty(sessionId))
                sessionId = entries[0].Sid;

            // ---- 过滤：只留能安全回填的两类 ----
            var messages = new List<ChatMessage>(entries.Count);

            foreach (var e in entries)
            {
                if (e.Role == "user")
                {
                    if (e.Text.Length > 0)
                        messages.Add(ChatMessage.User(e.Text));
                }
                else if (e.Role == "assistant")
                {
                    // 带 tools 的是「中间轮」，模型在这一轮只说了半句就转去调工具。
                    // 回填它会让模型看到一串没有下文的半句话。
                    if (e.HasTools || e.Text.Length == 0)
                        continue;

                    messages.Add(ChatMessage.Assistant(e.Text));
                }
                // system / tool / 其它一律不入上下文
            }

            // ---- 丢掉末尾悬空的 user ----
            // 场景：用户问完，模型跑到一半窗口被关了。那半句话留在文件里，
            // 恢复后若原样回填，历史就变成 user → user，模型会以为用户在重复提问。
            var droppedDangling = false;
            while (messages.Count > 0 && messages[^1].Role == "user")
            {
                messages.RemoveAt(messages.Count - 1);
                droppedDangling = true;
            }

            // ---- 只留最近 N 条，并且从 user 开始 ----
            // 从中间切断会出现「历史以 assistant 开头」，虽然协议允许，
            // 但模型容易把上一句回答当成正在继续，导致答非所问。
            if (maxMessages > 0 && messages.Count > maxMessages)
                messages = messages.GetRange(messages.Count - maxMessages, maxMessages);

            while (messages.Count > 0 && messages[0].Role != "user")
                messages.RemoveAt(0);

            return new SessionLoadResult
            {
                FilePath = file.FullName,
                SessionId = sessionId,
                Messages = messages,
                TotalEntries = entries.Count,
                SkippedLines = skipped,
                DroppedDanglingUser = droppedDangling,
                LastWrite = file.LastWriteTime,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static string Clip(string? text, out bool truncated)
    {
        if (string.IsNullOrEmpty(text))
        {
            truncated = false;
            return string.Empty;
        }

        if (text.Length <= MaxTextChars)
        {
            truncated = false;
            return text;
        }

        truncated = true;
        return text[..MaxTextChars]
               + $"…[已截断 {text.Length - MaxTextChars} 字符]";
    }

    private static string RandomSuffix()
        => Random.Shared.Next(0x1000, 0xFFFF).ToString("x4", CultureInfo.InvariantCulture);
}

using System.Text;

namespace FatFish.Core.Config;

/// <summary>.env 里一行的种类。</summary>
public enum EnvLineKind
{
    /// <summary>空行（可能带空格）。原样保留。</summary>
    Blank,

    /// <summary>注释行（<c>#</c> 开头）。原样保留 —— 注释是人的笔记，一个字符都不能丢。</summary>
    Comment,

    /// <summary><c>KEY=VALUE</c>（可带 <c>export </c> 前缀）。</summary>
    Entry,

    /// <summary>认不出来的行（写坏了、或者别的工具的语法）。原样保留，绝不丢弃。</summary>
    Invalid,
}

/// <summary>
/// .env 里的一行。
/// </summary>
/// <remarks>
/// <b>设计要点：<see cref="Raw"/> 是「真相」，<see cref="Value"/> 是「编辑后的值」。</b>
/// 没被改过的行永远原样写回（连引号风格、<c>export </c> 前缀、行尾空格都不动），
/// 只有改过的行才按 <see cref="EnvValueCodec.Encode"/> 重新拼。
///
/// 为什么值得这么做：这份文件是用户手写的，里面有他们自己的注释、分组、对齐方式。
/// 一个「读进来再写回去」的编辑器，如果做不到「没改就不动」，
/// 第一次保存就会把整份文件的排版碾平 —— 而用户不会立刻发现，
/// 等到某天需要看自己的注释时才惊觉它们没了。
/// </remarks>
public sealed class EnvLine
{
    public required EnvLineKind Kind { get; init; }

    /// <summary>原始整行文本（不含换行符）。</summary>
    public required string Raw { get; init; }

    /// <summary>键名（仅 <see cref="EnvLineKind.Entry"/> 有值）。</summary>
    public string Key { get; init; } = "";

    /// <summary>解码后的值（去掉引号、去掉 <c>export </c> 之后的真实值）。</summary>
    public string Value { get; set; } = "";

    /// <summary>原始行是否带 <c>export </c> 前缀。写回时保留这个习惯。</summary>
    public bool Exported { get; init; }

    /// <summary>是否被编辑过。没改过就原样写回 <see cref="Raw"/>。</summary>
    public bool Changed { get; set; }

    public bool IsEntry => Kind == EnvLineKind.Entry;

    /// <summary>给界面显示用：值太长时截断。</summary>
    public string PreviewValue(int max = 60)
        => Value.Length <= max ? Value : Value[..max] + "…";
}

/// <summary>
/// .env 的可编辑文档模型：<b>解析 → 改 → 写回，未改动的部分逐字节不变</b>。
///
/// <b>为什么需要它而不是「读字典 + 拼字符串」：</b>
/// 拼字符串的做法会丢掉三样东西，而这三样恰好都是用户在乎的：
///   1. 注释（「这行 key 是谁给的、什么时候加的」）；
///   2. 顺序（分组是人用空行和注释分出来的，重排之后整份文件失去可读性）；
///   3. 历史遗留写法（<c>export </c> 前缀、单引号、旧键名）。
/// 丢掉之后没有任何提示，用户下次打开只会觉得「怎么变样了」。
///
/// <b>解析规则与 <see cref="EnvFile"/> 对齐</b>（否则「编辑器里看到的值」和
/// 「程序实际用的值」会悄悄不一致 —— 这种问题极难查，因为两边都不报错）：
///   · 以 <c>#</c> 开头的行是注释；
///   · 支持 <c>export KEY=VALUE</c>；
///   · 按**第一个** <c>=</c> 切分，值里可以有 <c>=</c>；
///   · 单/双引号包裹的值会去掉引号；双引号内支持 <c>\"</c> 与 <c>\\</c>；
///   · 未加引号时**不剥离**行尾 <c>#</c> 注释（很多 key 本身含 <c>#</c>）；
///   · 同名键重复时，**第一次出现**的值生效（与 <see cref="EnvFile"/> 的实际行为一致）。
/// </summary>
public sealed class EnvDocument
{
    private readonly List<EnvLine> _items = new();

    /// <summary>文件使用的换行符（写回时沿用，不在 Windows 上把 LF 文件改成 CRLF）。</summary>
    public string NewLine { get; private set; } = "\r\n";

    /// <summary>文件是否带 UTF-8 BOM。写回时原样保留。</summary>
    public bool HasBom { get; private set; }

    /// <summary>文件末尾是否有换行。写回时原样保留。</summary>
    public bool EndsWithNewLine { get; private set; } = true;

    public IReadOnlyList<EnvLine> Lines => _items;

    public IEnumerable<EnvLine> Entries => _items.Where(l => l.IsEntry);

    /// <summary>全部键（按出现顺序，含重复）。</summary>
    public IReadOnlyList<string> Keys => _items.Where(l => l.IsEntry).Select(l => l.Key).ToList();

    /// <summary>重复出现的键。第一个生效，其余是「看着改了其实没生效」的陷阱。</summary>
    public IReadOnlyList<string> DuplicateKeys => _items
        .Where(l => l.IsEntry)
        .GroupBy(l => l.Key, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .ToList();

    // ------------------------------------------------------------------
    // 构造
    // ------------------------------------------------------------------

    public static EnvDocument Empty() => Parse(string.Empty);

    /// <summary>从文本解析。不会抛异常 —— 半行、坏行都进 <see cref="EnvLineKind.Invalid"/>。</summary>
    public static EnvDocument Parse(string? text)
    {
        text ??= string.Empty;

        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        // 换行符探测：出现 CRLF 就用 CRLF；只有 LF 就用 LF。
        var crlf = 0;
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '\r' && text[i + 1] == '\n')
                crlf++;
        }

        var lf = text.Count(c => c == '\n');
        var newLine = (crlf == 0 && lf > 0) ? "\n" : "\r\n";

        var endsWithNewLine = text.EndsWith('\n') || text.EndsWith('\r');

        var doc = new EnvDocument
        {
            NewLine = newLine,
            EndsWithNewLine = text.Length == 0 || endsWithNewLine,
        };

        if (text.Length > 0)
        {
            var parts = text.Replace("\r\n", "\n").Split('\n');

            // 末尾的 "" 是「文件以换行结尾」的副产物，不是一条空行
            var count = parts.Length;
            if (endsWithNewLine && count > 0 && parts[^1].Length == 0)
                count--;

            for (var i = 0; i < count; i++)
                doc._items.Add(ParseLine(parts[i]));
        }

        return doc;
    }

    /// <summary>从磁盘读取。文件不存在返回空文档（编辑器据此走「新建」流程）。</summary>
    public static EnvDocument Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Empty();

        var bytes = File.ReadAllBytes(path);

        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = hasBom
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);

        var doc = Parse(text);
        doc.HasBom = hasBom;
        return doc;
    }

    private static EnvLine ParseLine(string raw)
    {
        var trimmed = raw.Trim();

        if (trimmed.Length == 0)
            return new EnvLine { Kind = EnvLineKind.Blank, Raw = raw };

        if (trimmed[0] == '#')
            return new EnvLine { Kind = EnvLineKind.Comment, Raw = raw };

        var body = trimmed;
        var exported = false;

        if (body.StartsWith("export ", StringComparison.Ordinal))
        {
            exported = true;
            body = body[7..].TrimStart();
        }

        var eq = body.IndexOf('=');

        if (eq <= 0)
            return new EnvLine { Kind = EnvLineKind.Invalid, Raw = raw };

        var key = body[..eq].Trim();

        if (!EnvValueCodec.IsValidKey(key))
            return new EnvLine { Kind = EnvLineKind.Invalid, Raw = raw };

        return new EnvLine
        {
            Kind = EnvLineKind.Entry,
            Raw = raw,
            Key = key,
            Value = EnvValueCodec.Decode(body[(eq + 1)..].Trim()),
            Exported = exported,
        };
    }

    // ------------------------------------------------------------------
    // 读
    // ------------------------------------------------------------------

    /// <summary>取第一个出现的值（与 <see cref="EnvFile"/> 的生效顺序一致）。键名大小写不敏感。</summary>
    public string? Get(string key)
        => _items.FirstOrDefault(l => l.IsEntry && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    public bool Contains(string key) => Get(key) is not null;

    /// <summary>全部键值快照（第一个生效）。</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in _items.Where(l => l.IsEntry))
            map.TryAdd(line.Key, line.Value);

        return map;
    }

    // ------------------------------------------------------------------
    // 改
    // ------------------------------------------------------------------

    /// <summary>
    /// 设置一个键的值：已存在就**原地**改（保住位置和周边注释），不存在就追加到末尾。
    /// </summary>
    /// <exception cref="ArgumentException">键名非法，或值里含换行。</exception>
    public void Set(string key, string value)
    {
        if (!EnvValueCodec.IsValidKey(key))
            throw new ArgumentException($"键名不合法：'{key}'（只能是字母/数字/下划线，且不以数字开头）", nameof(key));

        // 提前触发「含换行」的校验：宁可在这里抛，也不要写出一份自己都读不回来的文件
        _ = EnvValueCodec.Encode(value);

        var line = _items.FirstOrDefault(
            l => l.IsEntry && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));

        if (line is null)
        {
            _items.Add(new EnvLine
            {
                Kind = EnvLineKind.Entry,
                Raw = string.Empty,
                Key = key,
                Value = value,
                Changed = true,
            });

            return;
        }

        line.Value = value;
        line.Changed = true;
    }

    /// <summary>删除某个键（所有出现）。返回删掉的条数。</summary>
    public int Remove(string key)
        => _items.RemoveAll(l => l.IsEntry && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>在末尾追加一行注释（用于「这一段是编辑器加的」这类标注）。</summary>
    public void AppendComment(string text)
    {
        var body = text.StartsWith('#') ? text : "# " + text;
        _items.Add(new EnvLine { Kind = EnvLineKind.Comment, Raw = body });
    }

    public void AppendBlank() => _items.Add(new EnvLine { Kind = EnvLineKind.Blank, Raw = string.Empty });

    // ------------------------------------------------------------------
    // 写
    // ------------------------------------------------------------------

    public string Serialize()
    {
        var sb = new StringBuilder();

        for (var i = 0; i < _items.Count; i++)
        {
            sb.Append(Render(_items[i]));

            if (i < _items.Count - 1 || EndsWithNewLine)
                sb.Append(NewLine);
        }

        return sb.ToString();
    }

    private static string Render(EnvLine line)
    {
        if (!line.IsEntry)
            return line.Raw;

        // 没改过的行原样写回：连引号风格和行尾空格都不动
        if (!line.Changed)
            return line.Raw;

        return (line.Exported ? "export " : "") + line.Key + "=" + EnvValueCodec.Encode(line.Value);
    }

    /// <summary>
    /// 原子写回：先备份 → 写临时文件 → 替换。
    /// </summary>
    /// <param name="path">目标文件。</param>
    /// <param name="backupDirectory">备份目录；默认与目标同目录（备份最需要的是「不用找」）。</param>
    /// <returns>备份文件路径；原本没有文件时为 null。</returns>
    /// <remarks>
    /// 为什么要「临时文件 + Move」而不是直接覆盖：
    /// 写到一半断电 / 被杀进程，直接覆盖会留下一份截断的 .env ——
    /// 而 .env 是程序能不能启动的前提，截断之后连「进界面改回来」都做不到。
    /// </remarks>
    public string? Save(string path, string? backupDirectory = null)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full) ?? ".";

        Directory.CreateDirectory(dir);

        string? backup = null;

        if (File.Exists(full))
        {
            var bdir = string.IsNullOrWhiteSpace(backupDirectory) ? dir : Path.GetFullPath(backupDirectory);
            Directory.CreateDirectory(bdir);

            // 备份名：.env.20261029_231500.bak。同一秒保存两次时追加序号，
            // 而不是覆盖上一份备份 —— 备份的意义就是「每一次都有」。
            var stamp = DateTime.Now.ToString(
                "yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var name = Path.GetFileName(full) + "." + stamp + ".bak";
            var candidate = Path.Combine(bdir, name);

            var n = 1;
            while (File.Exists(candidate) && n < 100)
            {
                candidate = Path.Combine(bdir,
                    Path.GetFileName(full) + "." + stamp + "_" + n + ".bak");
                n++;
            }

            File.Copy(full, candidate, overwrite: false);
            backup = candidate;
        }

        var temp = full + ".tmp";
        File.WriteAllText(temp, Serialize(), new UTF8Encoding(HasBom));
        File.Move(temp, full, overwrite: true);

        return backup;
    }
}

/// <summary>
/// .env 值的编码 / 解码 —— <b>读写两边共用同一份规则</b>。
/// </summary>
/// <remarks>
/// 这是整个编辑器最容易出「静默错误」的地方：
/// 写的时候用了自己的规则、读的时候用另一套，结果「编辑器里显示 sk-abc123」，
/// 程序拿到的却是 <c>"sk-abc123"</c>（带引号）或 <c>sk-abc123 # 注释</c>。
/// 两边都不报错，只表现为「key 明明填了却说没配置」。
///
/// 所以 <see cref="EnvFile"/> 的解析也改成调用这里 —— 一个规则，一处实现，
/// 测试只需要守住这一份（见 <c>EnvValueCodecTests</c> 的往返用例）。
/// </remarks>
public static class EnvValueCodec
{
    /// <summary>键名合法性：字母/数字/下划线，不以数字开头。</summary>
    public static bool IsValidKey(string key)
    {
        if (string.IsNullOrEmpty(key) || char.IsDigit(key[0]))
            return false;

        foreach (var c in key)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    /// <summary>把 <c>KEY=</c> 后面的原文解码成真实值。</summary>
    public static string Decode(string raw)
    {
        if (raw.Length < 2)
            return raw;

        var first = raw[0];

        if ((first == '"' || first == '\'') && raw[^1] == first)
        {
            var inner = raw[1..^1];

            // 双引号内允许 \" 与 \\ 转义
            if (first == '"' && inner.Contains('\\'))
                inner = inner.Replace("\\\"", "\"").Replace("\\\\", "\\");

            return inner;
        }

        // 未加引号：行尾的 # 注释不剥离（很多 .env 里 key 本身含 #）
        return raw;
    }

    /// <summary>这个值不加引号写出去，还能原样读回来吗？</summary>
    public static bool NeedsQuoting(string value)
    {
        if (value.Length == 0)
            return false; // KEY= 就是空值，合法

        // 首尾空白会被 Trim 掉
        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
            return true;

        // 以引号开头：不包起来的话，读取方会把这对引号当成「包裹符」剥掉
        if (value[0] == '"' || value[0] == '\'')
            return true;

        return false;
    }

    /// <summary>把真实值编码成可以写进 .env 的形式。</summary>
    /// <exception cref="ArgumentException">值里含换行（.env 不支持多行值）。</exception>
    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Contains('\n') || value.Contains('\r'))
            throw new ArgumentException("值里不能有换行符（.env 不支持多行值）", nameof(value));

        if (!NeedsQuoting(value))
            return value;

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}

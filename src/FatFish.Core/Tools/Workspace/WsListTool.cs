using System.Text;
using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// ws_list —— 列目录。
///
/// 对应 Python 版 workspace.py::ws_list。
///
/// 与 ws_read 的分工：ws_read 读一个文件的内容，ws_list 回答「这里有什么」。
/// 模型在动手改代码前几乎总要「看一下目录结构」，没有这个工具就只能靠 ws_run_cmd dir，
/// 那既不安全（多一道审批）也没必要。
///
/// ★ 输出为什么是纯文本而不是 JSON 数组：
///   每条记录的字段名（path / type / size）会在每一次后续请求里重复出现，
///   列 100 个文件就是几百个无谓 token。用「ls -F」那种约定（目录带尾斜杠）
///   同样没有歧义，但 token 只有 JSON 的几分之一。
/// </summary>
public sealed class WsListTool : ITool
{
    private const int DefaultMaxEntries = 400;
    private const int HardMaxEntries = 2000;

    public string Name => "ws_list";

    public string Description =>
        "列出工作台内某个目录的内容（目录以 / 结尾，文件后面跟体积）。" +
        "path 缺省表示工作台根目录；depth 控制递归层数（1=只列直接子项，默认）。" +
        "默认跳过 bin/obj/node_modules/.git 等构建产物与隐藏项。" +
        "查看某个文件的内容请用 ws_read。";

    public JsonElement ParametersSchema => Schema.Object(
        [
            ("path", Schema.String("要列出的目录（相对工作台根目录）。缺省或空字符串表示根目录")),
            ("depth", Schema.Integer("递归层数：1=只列直接子项（默认），最大 4", 1, 4)),
            ("include_hidden", Schema.Boolean("是否包含隐藏的文件与目录", false)),
            ("include_ignored", Schema.Boolean("是否包含 bin/obj/node_modules 等构建产物目录", false)),
            ("max_entries", Schema.Integer(
                $"最多返回多少条，默认 {DefaultMaxEntries}，上限 {HardMaxEntries}", 1, HardMaxEntries)),
        ]);

    public bool IsReadOnly => true;

    public bool HasSideEffects => false;

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var rel = args.GetString("path") ?? string.Empty;
        var depth = Math.Clamp(args.GetInt("depth", 1), 1, 4);
        var includeHidden = args.GetBool("include_hidden", false);
        var includeIgnored = args.GetBool("include_ignored", false);
        var maxEntries = Math.Clamp(args.GetInt("max_entries", DefaultMaxEntries), 1, HardMaxEntries);

        var full = context.Guard.Resolve(rel);

        if (File.Exists(full) && !Directory.Exists(full))
            return ValueTask.FromResult(ToolResult.Failure(
                $"{rel} 是文件，不是目录。要看内容请用 ws_read。"));

        if (!Directory.Exists(full))
            return ValueTask.FromResult(ToolResult.Failure($"目录不存在：{rel}"));

        var root = context.Guard.ToRelative(full);
        var sb = new StringBuilder(4096);

        var dirs = 0;
        var files = 0;
        var skipped = 0;
        var truncated = false;

        // 首行给出「列的是哪里」，避免模型把子目录内容误当成根目录
        sb.Append(root == "." ? "." : root).Append('/').Append('\n');

        Walk(
            full,
            level: 0,
            depth,
            includeHidden,
            includeIgnored,
            maxEntries,
            sb,
            ref dirs,
            ref files,
            ref skipped,
            ref truncated,
            ct);

        if (dirs == 0 && files == 0)
            sb.Append("  （空目录）\n");

        var listing = WorkspaceFilters.Normalize(sb.ToString());

        var notice = truncated
            ? $"已达 {maxEntries} 条上限，结果被截断；可用 max_entries 或缩小 path 继续查看"
            : skipped > 0
                ? $"已跳过 {skipped} 个不可访问的目录"
                : null;

        return ValueTask.FromResult(ToolResult.Success(new
        {
            path = root,
            depth,
            dirs,
            files,
            truncated,
            listing,
        }, notice: notice));
    }

    /// <summary>
    /// 递归遍历。
    ///
    /// 用 ref 传递计数而不是返回值，是为了让「超过 maxEntries 立刻停下」
    /// 能在任意深度生效 —— 否则要等整棵树走完再截断，大目录上会很慢。
    /// </summary>
    private static void Walk(
        string dir,
        int level,
        int maxDepth,
        bool includeHidden,
        bool includeIgnored,
        int maxEntries,
        StringBuilder sb,
        ref int dirs,
        ref int files,
        ref int skipped,
        ref bool truncated,
        CancellationToken ct)
    {
        if (truncated || ct.IsCancellationRequested)
            return;

        DirectoryInfo info;
        FileSystemInfo[] children;

        try
        {
            info = new DirectoryInfo(dir);

            // 一次枚举取全部，再排「目录优先」——
            // 两次枚举（GetDirectories + GetFiles）在机械硬盘上要多扫一遍目录。
            children = info.GetFileSystemInfos();
        }
        catch (UnauthorizedAccessException)
        {
            skipped++;
            return;
        }
        catch (DirectoryNotFoundException)
        {
            skipped++;
            return;
        }
        catch (IOException)
        {
            skipped++;
            return;
        }

        var ordered = children
            .Where(c => includeHidden || (c.Attributes & FileAttributes.Hidden) == 0)
            .Where(c => includeIgnored || !WorkspaceFilters.IgnoredDirs.Contains(c.Name))
            .OrderByDescending(c => c is DirectoryInfo)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var child in ordered)
        {
            if (ct.IsCancellationRequested)
                return;

            if (dirs + files >= maxEntries)
            {
                truncated = true;
                return;
            }

            var indent = new string(' ', level * 2);

            if (child is DirectoryInfo d)
            {
                dirs++;
                sb.Append(indent).Append(d.Name).Append('/').Append('\n');

                if (level + 1 < maxDepth)
                    Walk(d.FullName, level + 1, maxDepth, includeHidden, includeIgnored,
                        maxEntries, sb, ref dirs, ref files, ref skipped, ref truncated, ct);
            }
            else if (child is FileInfo f)
            {
                files++;

                // 体积对齐到固定宽度，让列表纵向扫读时数字成列
                var size = WorkspaceFilters.HumanSize(SafeLength(f));
                sb.Append(indent)
                  .Append(f.Name)
                  .Append(' ')
                  .Append(size.PadLeft(7))
                  .Append('\n');
            }
        }
    }

    /// <summary>
    /// 取文件体积。枚举与读取之间文件可能被删掉，这里不让它抛异常。
    /// </summary>
    private static long SafeLength(FileInfo f)
    {
        try
        {
            return f.Length;
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }
}

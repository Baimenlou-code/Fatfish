using System.Globalization;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools.Workspace;

/// <summary>
/// 「变更前备案」—— 覆盖 / 追加 / 替换 / 删除之前，先把原文件复制一份到 _backup/。
/// </summary>
/// <remarks>
/// ★ 2026-11-06 起覆盖**任意深度**的文件，不再只管根一级。
///   旧理由（只备份根一级）是「子目录里的东西有 git 或自己的版本管着」。
///   实测不成立：本仓库 src/ 下的文件改坏之后，git 里也没有中间态
///   （上一次 commit 可能是几天前），而 _backup/ 是最后一道后悔药。
///
/// ★ 布局：
///     根一级 → _backup/{名}.{时间戳}{扩展名}.bak      （老命名，保持不变）
///     深层   → _backup/tree/{原目录}/{同上}           （镜像目录，按路径就能找回来）
///   为什么不压平到一层：src/ 下有几十个同名文件（每个子项目都有 README.md、
///   Program.cs、.csproj），压平后备份名互相撞车，只能靠 _1/_2 序号区分，
///   等于「有备份但找不到」。
///
/// ★ 三条跳过规则（必须返回原因，不能静默）：
///   1. 文件本身在 _backup/ 里 —— 否则备份会层层套娃；
///   2. 位于构建/依赖产物目录（WorkspaceFilters.IgnoredDirs）—— 删了能重新生成；
///   3. 超过 MaxBytes —— 备份日志/二进制会把 _backup/ 变成第二个工作台。
///   跳过只影响「有没有后悔药」，不影响操作本身能否执行。
///
/// ★ 保留策略：同一个文件最多留 KeepPerFile 份，按修改时间淘汰最旧的。
///   不设上限的话，一个被反复修改的文件能悄悄占满磁盘。
///
/// ★ 时间戳插在扩展名之前，是为了让同类文件的备份在资源管理器里排在一起。
///   同一秒内多次备份加 _1 / _2 序号，绝不覆盖更早的备份。
/// </remarks>
public static class RootBackup
{
    /// <summary>备份区目录名（位于工作台根下，自身不受备案规则约束）。</summary>
    public const string DirName = "_backup";

    /// <summary>深层文件的备份镜像目录名（_backup/tree/...）。</summary>
    public const string TreeDirName = "tree";

    /// <summary>同一个文件最多保留几份备份。</summary>
    public const int KeepPerFile = 5;

    /// <summary>超过此体积的文件不备案。</summary>
    public const long MaxBytes = 4L * 1024 * 1024;

    /// <summary>一次备案的结果。</summary>
    /// <param name="Path">备份文件绝对路径；没有备份时为 null。</param>
    /// <param name="Skipped">跳过备案的原因（会显示给模型）；「新建文件无需备份」时为 null。</param>
    public readonly record struct Outcome(string? Path, string? Skipped)
    {
        /// <summary>文件原本不存在 —— 新建没有旧内容可损，不算跳过。</summary>
        public static Outcome NotNeeded => new(null, null);

        /// <summary>已备案。</summary>
        public static Outcome Saved(string path) => new(path, null);

        /// <summary>按规则跳过（原因必须可读）。</summary>
        public static Outcome Skip(string reason) => new(null, reason);
    }

    /// <summary>
    /// 给「已存在的文件」做一份变更前备案。
    /// </summary>
    /// <returns>备案结果；文件不存在时返回 <see cref="Outcome.NotNeeded"/>。</returns>
    /// <exception cref="IOException">复制失败（磁盘满、目标被占用）—— 调用方应据此中止操作。</exception>
    public static Outcome Capture(string fullPath, PathGuard guard)
    {
        if (!File.Exists(fullPath))
            return Outcome.NotNeeded;

        var rel = guard.ToRelative(fullPath);

        if (IsUnderBackupDir(rel))
            return Outcome.Skip($"本身在 {DirName}/ 内，不重复备案");

        if (IsGenerated(rel))
            return Outcome.Skip("位于构建/依赖产物目录，可重新生成");

        var size = new FileInfo(fullPath).Length;
        if (size > MaxBytes)
            return Outcome.Skip(
                $"体积 {WorkspaceFilters.HumanSize(size)} 超过备案上限 "
                + $"（{WorkspaceFilters.HumanSize(MaxBytes)}）");

        var targetDir = BackupDirFor(rel, guard);
        Directory.CreateDirectory(targetDir);

        // 用 InvariantCulture：备份文件名必须是稳定的 ASCII 时间戳，
        // 不能随系统区域设置变成其他历法或分隔符（CA1305 的正解）
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var baseName = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);

        var target = Path.Combine(targetDir, $"{baseName}.{stamp}{ext}.bak");

        var seq = 1;
        while (File.Exists(target))
            target = Path.Combine(targetDir, $"{baseName}.{stamp}_{seq++}{ext}.bak");

        File.Copy(fullPath, target, overwrite: false);
        Prune(targetDir, baseName, ext);

        return Outcome.Saved(target);
    }

    /// <summary>
    /// 算出某个文件该备案到哪个目录：根一级进 _backup/，深层的进 _backup/tree/ 的镜像目录。
    /// </summary>
    public static string BackupDirFor(string relativePath, PathGuard guard)
    {
        if (guard.IsRootLevelFile(relativePath))
            return Path.Combine(guard.Root, DirName);

        var dir = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));

        return string.IsNullOrEmpty(dir)
            ? Path.Combine(guard.Root, DirName, TreeDirName)
            : Path.Combine(guard.Root, DirName, TreeDirName, dir);
    }

    /// <summary>相对路径是否落在备份区自身之内。</summary>
    private static bool IsUnderBackupDir(string rel)
        => rel.Equals(DirName, StringComparison.OrdinalIgnoreCase)
           || rel.StartsWith(DirName + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>相对路径是否位于「可再生成」的目录（构建产物、依赖、缓存）。</summary>
    private static bool IsGenerated(string rel)
    {
        var dir = Path.GetDirectoryName(rel.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(dir))
            return false;

        foreach (var segment in dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (WorkspaceFilters.IgnoredDirs.Contains(segment))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 淘汰同一文件的旧备份，只留最近 <see cref="KeepPerFile"/> 份。
    /// </summary>
    /// <remarks>
    /// ★ 清理失败不抛异常：那是「省磁盘」，不是「保住数据」。
    ///   为了删一份旧备份而让整次写入失败，方向反了。
    /// </remarks>
    private static void Prune(string dir, string baseName, string ext)
    {
        try
        {
            var prefix = baseName + ".";
            var suffix = ext + ".bak";

            var stale = new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f =>
                    f.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && f.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(KeepPerFile)
                .ToList();

            foreach (var file in stale)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException)
                {
                    // 正被别的进程占用 —— 下次再清
                }
                catch (UnauthorizedAccessException)
                {
                    // 只读或被安全软件锁住 —— 下次再清
                }
            }
        }
        catch (IOException)
        {
            // 目录不可枚举 —— 放弃清理，不影响已完成的备份
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}

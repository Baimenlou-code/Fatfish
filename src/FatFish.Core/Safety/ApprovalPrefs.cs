using System.Text.Json;

namespace FatFish.Core.Safety;

/// <summary>
/// 审批栏里那些「运行时偏好」的落盘（工作台 <c>logs/ui/approval.json</c>）。
/// </summary>
/// <remarks>
/// ★ 为什么不用 .env：.env 是「改完要重启」的启动期配置，
///   而这两项（开子代理是否要审批、每轮回合上限）都是「正用着的时候顺手改一下」的设置 ——
///   必须立刻生效、且下次启动还记得。
///
/// ★ 为什么单独抽成 Core 里的类：原先这段读写各写了一半在
///   <c>MainWindow.Approval.cs</c> 里，一行测试都没有。
///   而它的失败方式是<b>静默</b>的：文件读坏了就退回默认值，
///   用户只会看到「重启之后自己变回去了」，查不出原因。
///
/// ★ 失败方向一律取「更严 / 更保守」：
///   读不出来 → 开子代理仍需审批、上限回到 25（不是「不限」）。
/// </remarks>
public static class ApprovalPrefs
{
    /// <summary>落盘文件名（放在工作台的 logs/ui/ 下）。</summary>
    public const string FileName = "approval.json";

    /// <summary>开子代理是否要人工审批的字段名。</summary>
    public const string SubagentGateKey = "require_subagent_approval";

    /// <summary>每轮回合上限的字段名。</summary>
    public const string RoundLimitKey = "round_limit";

    /// <summary>两个偏好的快照。</summary>
    /// <param name="RequireSubagentApproval">开子代理是否需要人工确认。</param>
    /// <param name="RoundLimit">每轮回合上限；0 = 不限。</param>
    public readonly record struct Snapshot(bool RequireSubagentApproval, int RoundLimit)
    {
        /// <summary>默认：开子代理要审批（与敏感文件同级的硬线），上限 25。</summary>
        public static Snapshot Default => new(true, ApprovalPolicy.DefaultRoundLimit);
    }

    /// <summary>读出偏好；任何异常都退回默认（更严的那一侧）。</summary>
    public static Snapshot Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return Snapshot.Default;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return Snapshot.Default;

            // 缺字段 / 类型不对 → 用默认。只有明确写了 false 才算关掉开关。
            var gate = !root.TryGetProperty(SubagentGateKey, out var g)
                       || g.ValueKind != JsonValueKind.False;

            var limit = Snapshot.Default.RoundLimit;

            // ★ 过一遍白名单：手改 json 写出 99999 会让一次跑飞的对话
            //   烧掉整晚的 token，而「读回来就用」正是它生效的路径。
            if (root.TryGetProperty(RoundLimitKey, out var l)
                && l.TryGetInt32(out var value)
                && ApprovalPolicy.IsValidRoundLimit(value))
            {
                limit = value;
            }

            return new Snapshot(gate, limit);
        }
        catch (Exception)
        {
            // 坏 JSON / 无权限 / 被占用：一律回默认。
            return Snapshot.Default;
        }
    }

    /// <summary>写入偏好。</summary>
    /// <returns>成功返回 null；失败返回原因（调用方负责记日志）。</returns>
    public static string? Save(string path, Snapshot prefs)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(
                new
                {
                    require_subagent_approval = prefs.RequireSubagentApproval,
                    round_limit = prefs.RoundLimit,
                },
                new JsonSerializerOptions { WriteIndented = true });

            // 先写临时文件再替换：写一半断电时，旧文件仍是完整可读的。
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);

            return null;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}

namespace FatFish.Core.Config;

/// <summary>
/// 把「表单当前值」安全地打回文档 —— <b>只碰真正变过的键</b>。
/// </summary>
/// <remarks>
/// 这段逻辑本来是写在窗口里的，后来挪了出来。原因很实在：
/// 它是「没改就不动」这条承诺的最后一道实现，
/// 而窗口里的代码**没法被单测覆盖**（测试项目是 net10.0，引不了 WPF）。
/// 一旦这里写错，症状是「保存一次，文件里所有注释都消失了」——
/// 用户会以为是自己误删的，因为界面上什么都没提示。
///
/// <b>规则（按顺序判断）：</b>
///   1. 键原本不存在、现在仍是空 → 跳过（不要凭空造一个空键）；
///   2. 键原本存在、值与打开时完全一致 → 跳过（保住原行：注释、引号、空格）；
///   3. 现在是空 → 删除这个键（这几项配置的读取方都把空当作「没配」，
///      所以删掉与写空等价，而删掉不会让人误以为「我配了但没生效」）；
///   4. 其余 → 写进去（已存在的键原地改，保持它在文件里的位置）。
/// </remarks>
public static class EnvPatch
{
    /// <summary>
    /// 应用改动。
    /// </summary>
    /// <param name="doc">目标文档（通常是刚从磁盘读出来的最新版本）。</param>
    /// <param name="baseline">打开编辑器时的键值快照。</param>
    /// <param name="current">表单上的当前值。</param>
    /// <returns>实际发生变化的键（用于告诉用户「改了哪几项」）。</returns>
    public static IReadOnlyList<string> Apply(
        EnvDocument doc,
        IReadOnlyDictionary<string, string?> baseline,
        IReadOnlyDictionary<string, string?> current)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);

        var changed = new List<string>();

        foreach (var (key, rawValue) in current)
        {
            var value = (rawValue ?? string.Empty).Trim();

            var hadKey = baseline.TryGetValue(key, out var baselineValue);
            var baselineText = (baselineValue ?? string.Empty).Trim();

            // 规则 1：原本没有、现在还是空 —— 不制造空键
            if (!hadKey && value.Length == 0)
                continue;

            // 规则 2：没动过 —— 一个字节都不碰
            if (hadKey && string.Equals(value, baselineText, StringComparison.Ordinal))
                continue;

            if (value.Length == 0)
            {
                // 规则 3：清空 = 取消配置
                if (doc.Remove(key) > 0)
                    changed.Add(key);

                continue;
            }

            doc.Set(key, value);
            changed.Add(key);
        }

        return changed;
    }
}

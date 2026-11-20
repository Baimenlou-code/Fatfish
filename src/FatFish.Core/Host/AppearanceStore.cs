using System.Text.Json;
using System.Text.Json.Serialization;

namespace FatFish.Core.Host;

/// <summary>
/// 阅读相关的外观设置：正文字号与段间距。
/// </summary>
/// <remarks>
/// <b>为什么放在 Core 而不是 App：</b>和 <see cref="WindowPlacement"/> 同理 ——
/// 这是一段纯数据 + 纯 IO 的持久化逻辑，不该为了读写一个 json 把整个内核绑到 WPF 上。
/// App 层负责把这里的数字翻译成 <c>FontSize</c> / <c>Thickness</c>。
///
/// <b>为什么不写 .env：</b>.env 是「配置与密钥」，用户可能手工编辑、也可能整份备份走；
/// 而字号是「这台机器上的阅读习惯」，改一次就该立刻落盘、丢了也不影响任何功能。
/// 把它塞进 .env 会让「配置改了要不要重启」这个问题凭空多一条。
/// </remarks>
/// <param name="FontSize">消息正文字号（磅）。</param>
/// <param name="BlockSpacing">相邻消息之间的垂直间距（设备无关像素）。</param>
public sealed record AppearanceSettings(
    [property: JsonPropertyName("fontSize")] double FontSize = AppearanceSettings.DefaultFontSize,
    [property: JsonPropertyName("blockSpacing")] double BlockSpacing = AppearanceSettings.DefaultBlockSpacing)
{
    /// <summary>默认正文 13pt —— 与改动前 XAML 里写死的值一致，老用户不会觉得「升级后字变了」。</summary>
    public const double DefaultFontSize = 13;

    public const double MinFontSize = 10;

    public const double MaxFontSize = 26;

    /// <summary>一次「放大 / 缩小」跨多少。</summary>
    public const double FontStep = 1;

    /// <summary>默认段间距 16px —— 正好等于改动前 Margin="11,7,12,9" 的上下合计（7+9）。</summary>
    public const double DefaultBlockSpacing = 16;

    public const double MinBlockSpacing = 0;

    public const double MaxBlockSpacing = 48;

    public const double BlockStep = 4;

    public static AppearanceSettings Default { get; } = new();

    /// <summary>
    /// 夹到合法范围。手改 json 写成 900 或 NaN 都不该让界面崩掉。
    /// </summary>
    public AppearanceSettings Normalized()
        => new(
            Math.Clamp(double.IsFinite(FontSize) ? FontSize : DefaultFontSize, MinFontSize, MaxFontSize),
            Math.Clamp(double.IsFinite(BlockSpacing) ? BlockSpacing : DefaultBlockSpacing, MinBlockSpacing, MaxBlockSpacing));

    public override string ToString()
        => $"字号 {FontSize:0.#}｜段间距 {BlockSpacing:0.#}";
}

/// <summary>
/// 外观设置的读写。文件位置与 <see cref="WindowPlacementStore"/> 同一目录：
/// <c>&lt;工作台&gt;\logs\ui\appearance.json</c>。
/// </summary>
/// <remarks>
/// 读写失败一律静默：记不住字号是小事，绝不该拦住窗口启动
/// （与 <see cref="WindowPlacementStore"/> / <c>SessionStore.Append</c> 同一原则）。
/// </remarks>
public static class AppearanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string workspaceRoot)
        => Path.Combine(workspaceRoot, "logs", "ui", "appearance.json");

    /// <summary>读设置；文件不存在 / 坏掉 / 没权限都返回默认值。</summary>
    public static AppearanceSettings Load(string workspaceRoot)
    {
        try
        {
            var path = PathFor(workspaceRoot);

            if (!File.Exists(path))
                return AppearanceSettings.Default;

            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<AppearanceSettings>(json, JsonOptions);

            return (parsed ?? AppearanceSettings.Default).Normalized();
        }
        catch (Exception)
        {
            return AppearanceSettings.Default;
        }
    }

    public static void Save(string workspaceRoot, AppearanceSettings settings)
    {
        try
        {
            var path = PathFor(workspaceRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // 先写临时文件再替换：断电时留下的半截 JSON 会让下次启动回到默认字号，
            // 而「我也没改过它啊」是最难想到的解释。
            var tmp = path + ".tmp";

            File.WriteAllText(tmp, JsonSerializer.Serialize(settings.Normalized(), JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            // 同上：记不住外观不是错误
        }
    }
}

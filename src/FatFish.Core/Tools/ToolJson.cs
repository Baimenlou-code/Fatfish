using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FatFish.Core.Tools;

/// <summary>
/// 全局 JSON 序列化约定。
///
/// 关键点：Encoder 必须是 UnsafeRelaxedJsonEscaping，否则中文会被转成 \uXXXX，
/// 既费 token 又影响 LLM 理解（Python 版靠 json.dumps(ensure_ascii=False) 达到同样效果）。
/// </summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions DefaultOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static readonly JsonSerializerOptions Indented = new(DefaultOptions)
    {
        WriteIndented = true,
    };
}

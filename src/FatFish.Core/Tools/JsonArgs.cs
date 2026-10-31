using System.Text.Json;

namespace FatFish.Core.Tools;

/// <summary>
/// JSON 参数取值助手。
///
/// 目的：工具实现里不要到处写 `args.GetProperty("x").GetString()`——
/// 那种写法在字段缺失时会抛 KeyNotFoundException，错误信息还很难懂。
/// 这里统一成「带默认值、带类型校验、错误信息友好」的取值方式。
/// </summary>
public static class JsonArgs
{
    public static string? GetString(this JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object)
            return null;
        if (!args.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText(),
        };
    }

    public static int GetInt(this JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return fallback;

        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var v) => v,
            JsonValueKind.String when int.TryParse(el.GetString(), out var v) => v,
            _ => fallback,
        };
    }

    public static bool GetBool(this JsonElement args, string name, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return fallback;

        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => el.GetString() is "1" or "true" or "yes" or "on",
            _ => fallback,
        };
    }

    /// <summary>取必填字符串；缺失或空白时给出明确的错误消息。</summary>
    public static bool TryGetRequired(this JsonElement args, string name, out string value, out ToolResult error)
    {
        value = args.GetString(name) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = ToolResult.Failure($"缺少必填参数 '{name}'");
            return false;
        }

        error = default!;
        return true;
    }
}

/// <summary>
/// JSON Schema 构建助手 —— 让工具参数声明保持可读。
/// </summary>
public static class Schema
{
    /// <summary>构造一个 object 类型的参数 schema。</summary>
    public static JsonElement Object(
        IEnumerable<(string Name, JsonElement Schema)> properties,
        params string[] required)
    {
        var props = new Dictionary<string, object>();
        foreach (var (name, schema) in properties)
            props[name] = schema;

        var root = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = props,
        };

        if (required.Length > 0)
            root["required"] = required;

        return JsonSerializer.SerializeToElement(root, ToolJson.DefaultOptions);
    }

    public static JsonElement String(string description, string[]? enumValues = null)
    {
        var d = new Dictionary<string, object>
        {
            ["type"] = "string",
            ["description"] = description,
        };

        if (enumValues is { Length: > 0 })
            d["enum"] = enumValues;

        return JsonSerializer.SerializeToElement(d, ToolJson.DefaultOptions);
    }

    public static JsonElement Integer(string description, int? minimum = null, int? maximum = null)
    {
        var d = new Dictionary<string, object>
        {
            ["type"] = "integer",
            ["description"] = description,
        };

        if (minimum is not null) d["minimum"] = minimum.Value;
        if (maximum is not null) d["maximum"] = maximum.Value;

        return JsonSerializer.SerializeToElement(d, ToolJson.DefaultOptions);
    }

    public static JsonElement Boolean(string description, bool @default = false)
    {
        var d = new Dictionary<string, object>
        {
            ["type"] = "boolean",
            ["description"] = description,
            ["default"] = @default,
        };

        return JsonSerializer.SerializeToElement(d, ToolJson.DefaultOptions);
    }
}

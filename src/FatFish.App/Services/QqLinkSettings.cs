using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FatFish.App.Services;

/// <summary>QQ 通道的权限档 —— 与宿主审批三档数值对齐（Low &lt; Normal &lt; High）。</summary>
public enum QqPermission
{
    /// <summary>只读：不允许任何写盘 / 执行动作。</summary>
    Low = 0,

    /// <summary>默认：现状行为（单文件可逆改动可做，删除/批量/执行命令一律到电脑上）。</summary>
    Normal = 1,

    /// <summary>高：审批自动放行。**危险**，等于把 QQ 当成完整终端。</summary>
    High = 2,
}

/// <summary>
/// QQ 桥接的可调参数：白名单、行为提示词、权限档。
///
/// <para>
/// 存在 <c>workspace/qq_bridge/link_settings.json</c>，而不是 <c>.env</c>：
/// </para>
/// <list type="bullet">
/// <item><c>.env</c> 是宿主启动时读进环境变量的，改一次要重启；这三项是运行期就该能改的；</item>
/// <item>白名单是列表、提示词是多行文本，塞进 <c>.env</c> 的「一键一值」里很难看，也容易写坏；</item>
/// <item>Python 桥接进程侧如果以后也要读，放工作台内的 json 比读环境变量直接。</item>
/// </list>
///
/// <para>
/// <b>空列表 = 不限制</b>：升级前没有任何白名单，若默认拒绝，装上新版会突然收不到任何消息。
/// 「空 = 放行」是刻意的向后兼容选择。
/// </para>
/// </summary>
public sealed record QqLinkSettings
{
    /// <summary>允许私聊使唤肥鱼的 QQ 号。**只管私聊**，不参与群消息判定（见 <see cref="Allows"/>）。</summary>
    [JsonPropertyName("allow_users")]
    public IReadOnlyList<string> AllowUsers { get; init; } = Array.Empty<string>();

    /// <summary>允许响应的群号。空 = 所有群。</summary>
    [JsonPropertyName("allow_groups")]
    public IReadOnlyList<string> AllowGroups { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 每轮注入给模型的行为提醒（热情程度、称呼、禁区…）。
    /// 它拼在安全边界之后，优先级高于默认口吻，但低于安全边界。
    /// </summary>
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";

    /// <summary>权限档：low / normal / high。</summary>
    [JsonPropertyName("permission")]
    public string Permission { get; init; } = "normal";

    /// <summary>把字符串档位解析成枚举；不认识的值按 normal 处理（宁可保守，不要因为拼错变成 high）。</summary>
    [JsonIgnore]
    public QqPermission PermissionLevel => Permission?.Trim().ToLowerInvariant() switch
    {
        "low" => QqPermission.Low,
        "high" => QqPermission.High,
        _ => QqPermission.Normal,
    };

    // ------------------------------------------------------------------ 存取

    public static string PathFor(string root)
        => Path.Combine(root, "workspace", "qq_bridge", "link_settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读设置。文件不存在 / 坏 JSON 一律返回默认值 —— 设置读不出来不该让 QQ 上线失败。</summary>
    public static QqLinkSettings Load(string root)
    {
        try
        {
            string path = PathFor(root);

            if (!File.Exists(path))
                return new QqLinkSettings();

            string json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<QqLinkSettings>(json, Options) ?? new QqLinkSettings();
        }
        catch
        {
            return new QqLinkSettings();
        }
    }

    /// <summary>
    /// 原子保存（先写 .tmp 再替换），避免半截 JSON 让下一次读取退化成默认值。
    /// <para>保存后会顺手把白名单同步进桥接的 <c>config.json</c>，见 <see cref="SyncToBridgeConfig"/>。</para>
    /// </summary>
    public void Save(string root)
    {
        string path = PathFor(root);
        string? dir = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);

        SyncToBridgeConfig(root, this);
    }

    /// <summary>
    /// 把白名单写回桥接自己的 <c>config.json</c>。
    ///
    /// <para>
    /// 为什么需要：桥接（Python）收到消息时先按 <c>trigger.whitelist_groups</c> /
    /// <c>link.allow_groups</c> 拦一道，**拦掉的消息根本到不了宿主**，宿主这边的白名单就形同虚设。
    /// 2026-11-01 的实测就是：设置页存了群 975346342，但 config.json 里还是旧群 160265587，
    /// 日志里全是「跳过 群975346342：群不在白名单」，一条都没进来。
    /// </para>
    ///
    /// <para>
    /// 两边各存一份是历史遗留。这里做**单向同步**（设置 → config.json），
    /// 不反向读，避免两个来源互相覆盖出更难查的问题。
    /// </para>
    ///
    /// <para>失败只吞不抛：桥接没装、config.json 被占用，都不该让「保存设置」这个动作失败。</para>
    /// </summary>
    private static void SyncToBridgeConfig(string root, QqLinkSettings settings)
    {
        try
        {
            string path = Path.Combine(root, "workspace", "qq_bridge", "config.json");

            if (!File.Exists(path))
                return;

            if (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is not JsonObject rootObj)
                return;

            if (rootObj["link"] is not JsonObject link)
            {
                link = new JsonObject();
                rootObj["link"] = link;
            }

            // 每次都给新节点：JsonNode 已有 parent 时直接改写会抛异常。
            link.Remove("allow_users");
            link["allow_users"] = ToJsonArray(settings.AllowUsers);

            link.Remove("allow_groups");
            link["allow_groups"] = ToJsonArray(settings.AllowGroups);

            if (rootObj["trigger"] is not JsonObject trigger)
            {
                trigger = new JsonObject();
                rootObj["trigger"] = trigger;
            }

            // 桥接的老白名单：非空即「只允许这些群」，必须跟群名单保持一致，否则群消息进不来。
            trigger.Remove("whitelist_groups");
            trigger["whitelist_groups"] = ToJsonArray(settings.AllowGroups);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, rootObj.ToJsonString(Options), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // 同步失败不影响宿主侧设置本身。
        }
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        JsonArray arr = new();

        foreach (string value in values)
        {
            string trimmed = Norm(value);

            if (trimmed.Length > 0)
                arr.Add(trimmed);
        }

        return arr;
    }

    // ------------------------------------------------------------------ 判定

    private static string Norm(string? s) => (s ?? "").Trim();

    /// <summary>
    /// 这条消息该不该进模型。
    ///
    /// <para>规则（都遵守「空 = 不限制」）：</para>
    /// <list type="bullet">
    /// <item>群消息：只看 <see cref="AllowGroups"/>；为空表示所有群都放行。</item>
    /// <item>私聊：只看 <see cref="AllowUsers"/>；为空表示所有私聊都放行。</item>
    /// </list>
    ///
    /// <para>
    /// <b>群消息不再要求发言人在 AllowUsers 里</b>（2026-11-01 改）。
    /// 之前那样写，等于「群里只有主人能被回应」，其他人说话全被静默丢弃 ——
    /// 而群场景本来就是要让肥鱼偶尔对别人的话插一句。用户名单的语义是「谁能私聊使唤肥鱼」，
    /// 不应该顺带把群成员一起挡掉。
    /// </para>
    /// </summary>
    public bool Allows(string? userId, string? targetId, bool isGroup)
    {
        // ★ 2026-11-01 修：这里原先只收一个 who，而宿主解析出来的 who 是**昵称**
        //    （QqLinkService.TryReadRecord 里 FirstNonEmpty(nickname, user)）。
        //    拿昵称去比对白名单里的 QQ 号永远不相等 → 所有消息被 SafeDelete 静默删掉，
        //    现象就是「私聊/群里发了没反应」，日志只有一行 [QQ] 白名单拦截。
        //    现在 userId / targetId 分开传：群比群号，私聊比 QQ 号。
        if (isGroup)
        {
            string group = Norm(targetId);
            return AllowGroups.Count == 0
                || (group.Length > 0 && AllowGroups.Any(g => Norm(g) == group));
        }

        string user = Norm(userId);
        return AllowUsers.Count == 0
            || (user.Length > 0 && AllowUsers.Any(u => Norm(u) == user));
    }

    /// <summary>给模型看的权限约束。normal 不加任何话（它就是要保持现状）。</summary>
    [JsonIgnore]
    public string PermissionRules => PermissionLevel switch
    {
        QqPermission.Low =>
            "\n【本轮权限档：只读】这条通道当前是只读。任何写文件 / 删除 / 执行命令的请求，" +
            "一律回「QQ 通道现在是只读档，这个操作请到电脑上做」，不要尝试绕路（换别的工具也算）。\n",

        QqPermission.High =>
            "\n【本轮权限档：高】用户已显式授予较高权限：审批会自动放行，写文件、执行命令都允许。" +
            "但仍然：删除 / 清空 / 递归 / 批量改动前必须先说明并等对方确认一句话；" +
            "密钥类文件照旧不碰（内容会进聊天记录）。\n",

        _ => "",
    };
}

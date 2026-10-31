using FatFish.Core.Plugins;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 清单解析与风险推断的测试。
///
/// 这一组守的是**装载期的边界**：能在这里拦下的规则，都不要留到运行时。
/// 每条失败用例都对应一个具体的「如果放进去会发生什么」：
///   · 命令里允许插值   → 一个含 &amp; 的参数就能追加一条命令；
///   · http 不写 hosts  → 插件可以往任意地址发数据；
///   · cmd 自称只读     → 它能跳过 AI 核验（AgentLoop 只看 HasSideEffects）；
///   · 声明 risk=low    → 它能跳过人工报批。
/// 所以这些用例的断言都尽量具体到「哪条诊断」，而不只是「失败了」。
/// </summary>
public sealed class PluginManifestTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fatfish-plugin-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginManifestTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响测试结论
        }
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private PluginParseOutcome Parse(string json, string id = "demo")
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "plugin.json");
        File.WriteAllText(path, json);

        return PluginManifestParser.ParseFile(path);
    }

    private static string HttpTool(
        string url = "https://api.example.com/x/{q}",
        string hosts = """["api.example.com"]""",
        string parameters = """{"type":"object","properties":{"q":{"type":"string"}}}""",
        string extra = "")
        => $$"""
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            {
              "name": "demo_get",
              "description": "查一下",
              "parameters": {{parameters}},
              "run": { "type": "http", "url": "{{url}}", "hosts": {{hosts}} }{{extra}}
            }
          ]
        }
        """;

    private static string CmdTool(string command = "echo hi", string extra = "")
        => $$"""
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            {
              "name": "demo_cmd",
              "description": "跑一条命令",
              "run": { "type": "cmd", "command": "{{command}}" }{{extra}}
            }
          ]
        }
        """;

    private static string Messages(PluginParseOutcome outcome)
        => string.Join("\n", outcome.Diagnostics.Select(d => d.Message));

    // ------------------------------------------------------------------
    // 正常路径
    // ------------------------------------------------------------------

    [Fact]
    public void MinimalHttpPlugin_Loads()
    {
        var outcome = Parse(HttpTool());

        Assert.True(outcome.Ok, Messages(outcome));

        var tool = Assert.Single(outcome.Manifest!.Tools);

        Assert.Equal("demo_get", tool.Name);
        Assert.True(tool.IsReadOnly);
        Assert.Equal(PluginRiskTier.Low, tool.EffectiveRisk);
        Assert.Equal("q", Assert.Single(tool.ParameterNames));
        Assert.Equal("q", Assert.Single(tool.Run.Http!.Placeholders));
    }

    [Fact]
    public void EmptyParameters_GetObjectSchema()
    {
        var outcome = Parse(CmdTool());

        Assert.True(outcome.Ok, Messages(outcome));

        var tool = outcome.Manifest!.Tools[0];

        Assert.Equal(System.Text.Json.JsonValueKind.Object, tool.Parameters.ValueKind);
        Assert.Empty(tool.ParameterNames);
    }

    [Fact]
    public void ParametersAreDetachedFromTheParsedDocument()
    {
        // ★ 这条守的是 JsonElement.Clone()：不 Clone 的话，Parse 结束 Dispose 文档，
        //   这里访问会抛 ObjectDisposedException —— 而那时清单看起来「装载成功」。
        var outcome = Parse(HttpTool());

        var schema = outcome.Manifest!.Tools[0].Parameters;

        // 强制 GC 只是为了制造「文档早已释放」的确定性，不是必需的
        GC.Collect();
        GC.WaitForPendingFinalizers();

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("q", out _));
    }

    [Fact]
    public void EnabledFalse_SkipsWithoutError()
    {
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "enabled": false,
          "tools": []
        }
        """;

        var outcome = Parse(json);

        Assert.False(outcome.Ok);
        Assert.False(outcome.HasError);
    }

    // ------------------------------------------------------------------
    // 身份与命名
    // ------------------------------------------------------------------

    [Fact]
    public void InvalidId_IsRejected()
    {
        var json = """{ "id": "Demo_Plugin", "name": "x", "tools": [] }""";

        var outcome = Parse(json);

        Assert.False(outcome.Ok);
        Assert.Contains("id", Messages(outcome));
    }

    [Theory]
    [InlineData("ws_read2")]
    [InlineData("web_search2")]
    [InlineData("net_balance2")]
    public void ReservedPrefix_IsRejected(string name)
    {
        var json = $$"""
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            { "name": "{{name}}", "description": "d", "run": { "type": "cmd", "command": "echo hi" } }
          ]
        }
        """;

        var outcome = Parse(json);

        Assert.False(outcome.Ok);
        Assert.Contains("内置前缀", Messages(outcome));
    }

    [Fact]
    public void MissingDescription_IsRejected()
    {
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "tools": [ { "name": "demo_x", "run": { "type": "cmd", "command": "echo hi" } } ]
        }
        """;

        var outcome = Parse(json);

        Assert.False(outcome.Ok);
        Assert.Contains("description", Messages(outcome));
    }

    // ------------------------------------------------------------------
    // 注入防线
    // ------------------------------------------------------------------

    [Fact]
    public void CmdPlaceholderInCommand_IsRejected()
    {
        // {text} 会被模型填的参数替换 —— 这就是命令注入的入口
        var outcome = Parse(CmdTool("echo {text}"));

        Assert.False(outcome.Ok);
        Assert.Contains("FATFISH_ARG_", Messages(outcome));
    }

    [Fact]
    public void CmdCannotDeclareReadOnly()
    {
        var outcome = Parse(CmdTool(extra: ", \"readOnly\": true"));

        Assert.False(outcome.Ok);
        Assert.Contains("readOnly", Messages(outcome));
    }

    [Theory]
    [InlineData("low")]
    [InlineData("normal")]
    public void CmdDeclaredRisk_IsRaisedToHigh(string declared)
    {
        // ★ 最重要的自适应规则：声明只能加严，不能放宽。
        var outcome = Parse(CmdTool(extra: $", \"risk\": \"{declared}\""));

        Assert.True(outcome.Ok, Messages(outcome));

        var tool = outcome.Manifest!.Tools[0];

        Assert.Equal(PluginRiskTier.High, tool.EffectiveRisk);
        Assert.False(tool.IsReadOnly);
        Assert.True(tool.HasSideEffects);
    }

    [Fact]
    public void HttpGetDeclaredHigh_BecomesSideEffecting()
    {
        // 作者主动声明高危 → 它必须同时不再是只读。
        // 否则会出现「high 风险 + HasSideEffects=false」的矛盾，
        // 而核验判定只看 HasSideEffects —— 加严反而换来跳过核验。
        var outcome = Parse(HttpTool(extra: ", \"risk\": \"high\""));

        Assert.True(outcome.Ok, Messages(outcome));

        var tool = outcome.Manifest!.Tools[0];

        Assert.Equal(PluginRiskTier.High, tool.EffectiveRisk);
        Assert.False(tool.IsReadOnly);
        Assert.True(tool.HasSideEffects);
    }

    [Fact]
    public void HttpPost_IsNotReadOnly()
    {
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            {
              "name": "demo_post",
              "description": "写点什么",
              "parameters": { "type": "object", "properties": { "q": { "type": "string" } } },
              "run": {
                "type": "http",
                "method": "POST",
                "url": "https://api.example.com/x",
                "hosts": ["api.example.com"]
              }
            }
          ]
        }
        """;

        var outcome = Parse(json);

        Assert.True(outcome.Ok, Messages(outcome));

        var tool = outcome.Manifest!.Tools[0];

        // POST 会改远端状态 —— 与 web_fetch 那种「只读抓取」不是一回事
        Assert.False(tool.IsReadOnly);
        Assert.Equal(PluginRiskTier.Normal, tool.EffectiveRisk);
    }

    // ------------------------------------------------------------------
    // http 白名单
    // ------------------------------------------------------------------

    [Fact]
    public void HttpWithoutHosts_IsRejected()
    {
        var outcome = Parse(HttpTool(hosts: "[]"));

        Assert.False(outcome.Ok);
        Assert.Contains("hosts", Messages(outcome));
    }

    [Fact]
    public void HttpHostNotCoveringUrl_IsRejected()
    {
        var outcome = Parse(HttpTool(hosts: """["other.example.com"]"""));

        Assert.False(outcome.Ok);
        Assert.Contains("白名单没有覆盖", Messages(outcome));
    }

    [Fact]
    public void HttpHostWildcard_CoversSubdomain()
    {
        var outcome = Parse(HttpTool(hosts: """["*.example.com"]"""));

        Assert.True(outcome.Ok, Messages(outcome));
    }

    [Fact]
    public void HttpPlaceholderNotDeclared_IsRejected()
    {
        var outcome = Parse(HttpTool(url: "https://api.example.com/x/{nope}"));

        Assert.False(outcome.Ok);
        Assert.Contains("nope", Messages(outcome));
    }

    [Fact]
    public void HttpCredentialInUrl_IsRejected()
    {
        var outcome = Parse(HttpTool(url: "https://user:pass@api.example.com/x"));

        Assert.False(outcome.Ok);
        Assert.Contains("账号密码", Messages(outcome));
    }

    [Fact]
    public void HttpCustomHostHeader_IsRejected()
    {
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            {
              "name": "demo_get",
              "description": "查一下",
              "run": {
                "type": "http",
                "url": "https://api.example.com/x",
                "hosts": ["api.example.com"],
                "headers": { "Host": "evil.com" }
              }
            }
          ]
        }
        """;

        var outcome = Parse(json);

        Assert.False(outcome.Ok);
        Assert.Contains("Host", Messages(outcome));
    }

    // ------------------------------------------------------------------
    // python
    // ------------------------------------------------------------------

    [Fact]
    public void PythonScriptOutsidePluginDir_IsRejected()
    {
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            { "name": "demo_py", "description": "d", "run": { "type": "python", "script": "../evil.py" } }
          ]
        }
        """;

        var outcome = Parse(json);

        Assert.False(outcome.Ok);
        Assert.Contains("越出", Messages(outcome));
    }

    [Fact]
    public void PythonScriptMissing_IsRejected()
    {
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            { "name": "demo_py", "description": "d", "run": { "type": "python", "script": "missing.py" } }
          ]
        }
        """;

        var outcome = Parse(json);

        Assert.False(outcome.Ok);
        Assert.Contains("脚本不存在", Messages(outcome));
    }

    [Fact]
    public void PythonScriptInside_IsAccepted()
    {
        var dir = Path.Combine(_root, "demo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "run.py"), "print('hi')");

        var json = """
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            { "name": "demo_py", "description": "d", "run": { "type": "python", "script": "run.py" } }
          ]
        }
        """;

        var outcome = Parse(json);

        Assert.True(outcome.Ok, Messages(outcome));
        Assert.Equal(PluginRiskTier.High, outcome.Manifest!.Tools[0].EffectiveRisk);
        Assert.False(outcome.Manifest!.Tools[0].IsReadOnly);
    }

    // ------------------------------------------------------------------
    // 部分失败
    // ------------------------------------------------------------------

    [Fact]
    public void OneBadTool_DoesNotKillTheRest()
    {
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            { "name": "demo_ok", "description": "好的", "run": { "type": "cmd", "command": "echo hi" } },
            { "name": "demo_bad", "description": "坏的", "run": { "type": "cmd", "command": "echo {x}" } }
          ]
        }
        """;

        var outcome = Parse(json);

        Assert.True(outcome.Ok, Messages(outcome));

        var tool = Assert.Single(outcome.Manifest!.Tools);

        Assert.Equal("demo_ok", tool.Name);
        Assert.True(outcome.HasError);   // 错误仍然被记录，只是没有拖垮整个插件
    }

    [Fact]
    public void DuplicateToolName_KeepsTheFirst()
    {
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            { "name": "demo_x", "description": "第一个", "run": { "type": "cmd", "command": "echo one" } },
            { "name": "demo_x", "description": "第二个", "run": { "type": "cmd", "command": "echo two" } }
          ]
        }
        """;

        var outcome = Parse(json);

        Assert.True(outcome.Ok, Messages(outcome));

        var tool = Assert.Single(outcome.Manifest!.Tools);

        Assert.Equal("第一个", tool.Description);
    }

    [Fact]
    public void ParameterNameCollision_IsRejected()
    {
        // aB 与 ab 都映射到 FATFISH_ARG_AB —— 命令里无法区分这两个值
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "tools": [
            {
              "name": "demo_x",
              "description": "d",
              "parameters": {
                "type": "object",
                "properties": { "aB": { "type": "string" }, "ab": { "type": "string" } }
              },
              "run": { "type": "cmd", "command": "echo %FATFISH_ARG_AB%" }
            }
          ]
        }
        """;

        var outcome = Parse(json);

        Assert.False(outcome.Ok);
        Assert.Contains("环境变量", Messages(outcome));
    }

    // ------------------------------------------------------------------
    // 其它
    // ------------------------------------------------------------------

    [Fact]
    public void UnknownCapability_IsRejected()
    {
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "requires": ["quantum"],
          "tools": [ { "name": "demo_x", "description": "d", "run": { "type": "cmd", "command": "echo hi" } } ]
        }
        """;

        var outcome = Parse(json);

        Assert.False(outcome.Ok);
        Assert.Contains("quantum", Messages(outcome));
    }

    [Fact]
    public void BrokenJson_ReportsLineNumber()
    {
        var outcome = Parse("""{ "id": "demo", "tools": [ } """);

        Assert.False(outcome.Ok);
        Assert.Contains("JSON 语法错误", Messages(outcome));
    }

    [Fact]
    public void EmptyTools_IsRejected()
    {
        var outcome = Parse("""{ "id": "demo", "name": "演示", "tools": [] }""");

        Assert.False(outcome.Ok);
        Assert.Contains("没有任何工具", Messages(outcome));
    }

    [Fact]
    public void UnknownRunType_IsRejected()
    {
        var json = """
        {
          "id": "demo",
          "name": "演示",
          "tools": [ { "name": "demo_x", "description": "d", "run": { "type": "wasm" } } ]
        }
        """;

        var outcome = Parse(json);

        Assert.False(outcome.Ok);
        Assert.Contains("wasm", Messages(outcome));
    }
}

/// <summary>风险推断与合并的纯逻辑测试（不碰磁盘）。</summary>
public class PluginRiskTests
{
    private static PluginRunSpec Http(string method) => new()
    {
        Kind = PluginRunKind.Http,
        Http = new PluginHttpSpec
        {
            Method = method,
            Url = "https://api.example.com/x",
            Hosts = ["api.example.com"],
            Placeholders = [],
            Headers = new Dictionary<string, string>(),
            TimeoutSeconds = 10,
        },
    };

    private static PluginRunSpec Cmd() => new()
    {
        Kind = PluginRunKind.Cmd,
        Cmd = new PluginCmdSpec
        {
            Shell = "cmd",
            Command = "echo hi",
            WorkingDirectory = string.Empty,
            TimeoutSeconds = 10,
        },
    };

    [Theory]
    [InlineData("GET", PluginRiskTier.Low, true)]
    [InlineData("HEAD", PluginRiskTier.Low, true)]
    [InlineData("POST", PluginRiskTier.Normal, false)]
    [InlineData("DELETE", PluginRiskTier.Normal, false)]
    public void HttpRisk_DependsOnMethod(string method, PluginRiskTier tier, bool readOnly)
    {
        Assert.Equal(tier, PluginRisk.Infer(Http(method)));
        Assert.Equal(readOnly, PluginRisk.InferReadOnly(Http(method)));
    }

    [Fact]
    public void Cmd_IsAlwaysHighAndNeverReadOnly()
    {
        Assert.Equal(PluginRiskTier.High, PluginRisk.Infer(Cmd()));
        Assert.False(PluginRisk.InferReadOnly(Cmd()));
    }

    [Fact]
    public void Combine_OnlyRaises()
    {
        // 声明更松 → 无效
        Assert.Equal(PluginRiskTier.High, PluginRisk.Combine(PluginRiskTier.High, PluginRiskTier.Low));
        Assert.Equal(PluginRiskTier.Low, PluginRisk.Combine(PluginRiskTier.Low, PluginRiskTier.Low));

        // 声明更严 → 生效
        Assert.Equal(PluginRiskTier.High, PluginRisk.Combine(PluginRiskTier.Low, PluginRiskTier.High));

        // 没声明 → 用推断
        Assert.Equal(PluginRiskTier.Normal, PluginRisk.Combine(PluginRiskTier.Normal, null));
    }

    [Theory]
    [InlineData("auto", null)]
    [InlineData(null, null)]
    [InlineData("low", PluginRiskTier.Low)]
    [InlineData("normal", PluginRiskTier.Normal)]
    [InlineData("high", PluginRiskTier.High)]
    public void ParseDeclared_RecognizesValues(string? raw, PluginRiskTier? expected)
    {
        var parsed = PluginRisk.ParseDeclared(raw);

        Assert.False(PluginRisk.IsInvalid(parsed));
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void ParseDeclared_UnknownValueIsFlagged()
    {
        var parsed = PluginRisk.ParseDeclared("extreme");

        Assert.True(PluginRisk.IsInvalid(parsed));
    }

    [Theory]
    [InlineData("a.example.com", "*.example.com", true)]
    [InlineData("a.b.example.com", "*.example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("evilexample.com", "*.example.com", false)]
    [InlineData("API.Example.COM", "api.example.com", true)]
    public void HostMatching_IsCaseInsensitiveAndWildcardAware(
        string host, string pattern, bool expected)
    {
        Assert.Equal(expected, PluginManifestParser.HostMatches(host, pattern));
    }
}

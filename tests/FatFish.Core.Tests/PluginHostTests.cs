using System.Text.Json;
using FatFish.Core.Plugins;
using FatFish.Core.Safety;
using FatFish.Core.Tools;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// PluginHost 的装载、热重载、熔断与执行测试。
///
/// 这一组关注的是**运行期行为**，与 <see cref="PluginManifestTests"/> 的装载期校验互补：
///   · 冲突的插件能否只坏自己、不牵连别人；
///   · 改完文件后能不能自动生效（自适应）；
///   · 重载失败时会不会把用户正在用的工具一起弄没（熔断）；
///   · 插件工具进了注册表之后，审批与核验的判定是否与内置工具一致。
/// </summary>
public sealed class PluginHostTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "fatfish-host-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _plugins;

    public PluginHostTests()
    {
        _plugins = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_plugins);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private string WritePlugin(string id, string json, string baseDir = "", string? script = null)
    {
        var dir = Path.Combine(string.IsNullOrEmpty(baseDir) ? _plugins : baseDir, id);
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, PluginManifestParser.ManifestFileName), json);

        if (script is not null)
            File.WriteAllText(Path.Combine(dir, "run.py"), script);

        return dir;
    }

    private PluginHost Start(params string[] reserved)
        => PluginHost.Start(new PluginHostOptions
        {
            Directories = [new PluginDirectory(_plugins, BuiltIn: false)],
            ReservedToolNames = reserved.ToHashSet(StringComparer.Ordinal),
            ReloadThrottle = TimeSpan.Zero,   // 测试里不要节流
        });

    private static string CmdJson(string id, string toolName, string command = "echo hi")
        => $$"""
        {
          "id": "{{id}}",
          "name": "测试插件",
          "tools": [
            {
              "name": "{{toolName}}",
              "description": "测试用工具",
              "run": { "type": "cmd", "command": "{{command}}" }
            }
          ]
        }
        """;

    private static string CmdJsonWithText(string id, string toolName)
        => $$"""
        {
          "id": "{{id}}",
          "name": "测试插件",
          "tools": [
            {
              "name": "{{toolName}}",
              "description": "回显 text",
              "parameters": {
                "type": "object",
                "properties": { "text": { "type": "string" } },
                "required": ["text"]
              },
              "run": { "type": "cmd", "command": "echo GOT=%FATFISH_ARG_TEXT%" }
            }
          ]
        }
        """;

    /// <summary>空对象 schema。用 using 包住文档，避免 CA2000 与测试进程里的文档泄漏。</summary>
    private static JsonElement EmptySchema()
    {
        using var doc = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return doc.RootElement.Clone();
    }

    private ToolContext NewContext()
    {
        var guard = new PathGuard(_root);

        return new ToolContext
        {
            Guard = guard,
            Tickets = new ReadTicketStore(guard),
            Approval = new ApprovalPolicy(guard) { Level = ApprovalLevel.ReadFree },
        };
    }

    // ------------------------------------------------------------------
    // 装载
    // ------------------------------------------------------------------

    [Fact]
    public void EmptyDirectory_LoadsNothing_ButDoesNotThrow()
    {
        var host = Start();

        Assert.Empty(host.Manifests);
        Assert.Empty(host.Snapshot());
        Assert.Contains("未装载", host.Describe());
    }

    [Fact]
    public void SinglePlugin_IsLoaded()
    {
        WritePlugin("demo", CmdJson("demo", "demo_cmd"));

        var host = Start();

        var manifest = Assert.Single(host.Manifests);

        Assert.Equal("demo", manifest.Id);
        Assert.False(manifest.BuiltIn);

        var tool = Assert.Single(host.Snapshot());

        Assert.Equal("demo_cmd", tool.Name);
        Assert.True(tool.HasSideEffects);
        Assert.False(tool.IsReadOnly);
    }

    [Fact]
    public void ToolNameCollidingWithBuiltin_IsSkipped()
    {
        WritePlugin("demo", CmdJson("demo", "demo_cmd"));

        // 假装内置工具已经叫这个名字
        var host = Start("demo_cmd");

        Assert.Empty(host.Snapshot());
        Assert.Contains(host.Diagnostics, d => d.Message.Contains("已被占用"));
    }

    [Fact]
    public void TwoPluginsWithSameToolName_OnlyFirstWins()
    {
        // ★ 目录顺序决定优先级；这里用字母序保证确定性（a 先于 b）
        WritePlugin("a-one", CmdJson("a-one", "demo_shared"));
        WritePlugin("b-two", CmdJson("b-two", "demo_shared"));

        var host = Start();

        var tool = Assert.Single(host.Snapshot());

        // 第二个插件的唯一工具被跳过 → 整个插件不生效，也不出现在 Manifests 里
        Assert.Equal("a-one", Assert.Single(host.Manifests).Id);
        Assert.Equal(PluginRiskTier.High, host.Lookup("demo_shared")!.Tier);
        Assert.Equal("demo_shared", tool.Name);
    }

    [Fact]
    public void MissingCapability_SkipsPlugin()
    {
        var json = """
        {
          "id": "demo",
          "name": "测试插件",
          "requires": ["net"],
          "tools": [
            { "name": "demo_cmd", "description": "d", "run": { "type": "cmd", "command": "echo hi" } }
          ]
        }
        """;

        WritePlugin("demo", json);

        var host = PluginHost.Start(new PluginHostOptions
        {
            Directories = [new PluginDirectory(_plugins, BuiltIn: false)],
            Capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "workspace" },
        });

        Assert.Empty(host.Manifests);
        Assert.Contains(host.Diagnostics, d => d.Message.Contains("缺少宿主能力"));
    }

    [Fact]
    public void SameId_LaterDirectoryOverrides()
    {
        var builtinDir = Path.Combine(_root, "builtin");
        var userDir = Path.Combine(_root, "user");

        WritePlugin("same", CmdJson("same", "same_builtin"), builtinDir);
        WritePlugin("same", CmdJson("same", "same_user"), userDir);

        var host = PluginHost.Start(new PluginHostOptions
        {
            Directories =
            [
                new PluginDirectory(builtinDir, BuiltIn: true),
                new PluginDirectory(userDir, BuiltIn: false),
            ],
        });

        var tool = Assert.Single(host.Snapshot());

        Assert.Equal("same_user", tool.Name);
        Assert.Contains(host.Diagnostics, d => d.Message.Contains("覆盖"));
    }

    [Fact]
    public void BrokenManifest_DoesNotPreventOtherPlugins()
    {
        WritePlugin("good", CmdJson("good", "good_tool"));
        WritePlugin("bad", "{ this is not json");

        var host = Start();

        Assert.Single(host.Snapshot());
        Assert.Contains(host.Diagnostics, d => d.Level == PluginDiagnosticLevel.Error);
    }

    // ------------------------------------------------------------------
    // 热重载（自适应）
    // ------------------------------------------------------------------

    [Fact]
    public void IsStale_AfterManifestChange()
    {
        WritePlugin("demo", CmdJson("demo", "demo_one"));

        var host = Start();

        Assert.False(host.IsStale);

        // 长度不同，保证指纹一定变化（避免依赖时间戳精度）
        WritePlugin("demo", CmdJson("demo", "demo_one_renamed"));

        Assert.True(host.IsStale);
    }

    [Fact]
    public void Snapshot_AutoReloadsAfterChange()
    {
        WritePlugin("demo", CmdJson("demo", "demo_one"));

        var host = Start();

        Assert.Equal("demo_one", Assert.Single(host.Snapshot()).Name);

        WritePlugin("demo", CmdJson("demo", "demo_two_renamed"));

        // 没有调用 Reload —— Snapshot 自己发现指纹变了
        Assert.Equal("demo_two_renamed", Assert.Single(host.Snapshot()).Name);
    }

    [Fact]
    public void BrokenReload_KeepsPreviousSnapshot()
    {
        WritePlugin("demo", CmdJson("demo", "demo_one"));

        var host = Start();

        Assert.Single(host.Snapshot());

        // 模拟「清单编辑到一半」
        File.WriteAllText(
            Path.Combine(_plugins, "demo", PluginManifestParser.ManifestFileName),
            "{ \"id\": \"demo\", ");

        // ★ 关键断言：工具没有消失。
        Assert.Equal("demo_one", Assert.Single(host.Snapshot()).Name);

        Assert.Contains(host.Diagnostics, d => d.Message.Contains("保留上一次"));
    }

    [Fact]
    public void FixedAfterBrokenReload_RecoversAutomatically()
    {
        WritePlugin("demo", CmdJson("demo", "demo_one"));

        var host = Start();

        var path = Path.Combine(_plugins, "demo", PluginManifestParser.ManifestFileName);

        File.WriteAllText(path, "{ broken");
        Assert.Equal("demo_one", Assert.Single(host.Snapshot()).Name);

        WritePlugin("demo", CmdJson("demo", "demo_fixed"));

        Assert.Equal("demo_fixed", Assert.Single(host.Snapshot()).Name);
    }

    [Fact]
    public void ExplicitReload_ReportsDiagnostics()
    {
        WritePlugin("demo", CmdJson("demo", "demo_one"));

        var host = Start();
        var report = host.Reload(force: true);

        Assert.True(report.Reloaded);
        Assert.Equal(1, report.Plugins);
        Assert.Equal(1, report.Tools);
        Assert.Equal(0, report.Errors);
    }

    // ------------------------------------------------------------------
    // 风险表
    // ------------------------------------------------------------------

    [Fact]
    public void Lookup_ReturnsInferredRisk()
    {
        WritePlugin("demo", CmdJson("demo", "demo_cmd"));

        var host = Start();

        var risk = host.Lookup("demo_cmd");

        Assert.NotNull(risk);
        Assert.Equal("demo", risk.PluginId);
        Assert.Equal(PluginRiskTier.High, risk.Tier);
        Assert.False(risk.ReadOnly);
    }

    [Fact]
    public void Lookup_UnknownToolReturnsNull()
    {
        var host = Start();

        Assert.Null(host.Lookup("nope"));
        Assert.Null(host.Lookup(string.Empty));
    }

    // ------------------------------------------------------------------
    // 与注册表、审批、核验的联动
    // ------------------------------------------------------------------

    [Fact]
    public void Registry_IncludesPluginTools()
    {
        WritePlugin("demo", CmdJson("demo", "demo_cmd"));

        var host = Start();
        var registry = new ToolRegistry(Array.Empty<ITool>(), host);

        Assert.True(registry.Contains("demo_cmd"));
        Assert.NotNull(registry.Find("demo_cmd"));
        Assert.Equal(1, registry.Count);

        var schema = registry.BuildToolSchema().GetRawText();

        Assert.Contains("demo_cmd", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registry_CanInvokePluginTool()
    {
        // ★ 这条守的是一个真实发生过的缺陷：注册表的 Find / Count / BuildToolSchema
        //   都改成了「静态字典 + 动态源」，唯独 InvokeAsync 仍然只查静态字典。
        //   症状是工具能列出、能发给模型，模型也真的调了，但一执行就「未知工具」——
        //   而「列得出来」这一点会把人往「插件没装载」的方向带。
        WritePlugin("demo", CmdJsonWithText("demo", "demo_cmd"));

        var host = Start();
        var registry = new ToolRegistry(Array.Empty<ITool>(), host);

        var args = JsonSerializer.SerializeToElement(new { text = "via-registry" });

        var result = await registry.InvokeAsync("demo_cmd", NewContext(), args);

        Assert.True(result.Ok, result.Error);
        Assert.Contains("via-registry",
            result.Data!.Value.GetProperty("stdout").GetString());
    }

    [Fact]
    public void Registry_StaticToolsStillWinOnNameConflict()
    {
        // 正常情况下 PluginHost 已经拒绝了重名；这里再造一个绕过 Host 的工具，
        // 验证「注册表查找优先内置」这道兜底。
        WritePlugin("demo", CmdJson("demo", "demo_cmd"));

        var host = Start();
        var builtin = new FakeTool("demo_cmd");
        var registry = new ToolRegistry(new ITool[] { builtin }, host);

        Assert.Same(builtin, registry.Find("demo_cmd"));
    }

    [Fact]
    public void Approval_HighRiskPluginToolNeedsApproval()
    {
        WritePlugin("demo", CmdJson("demo", "demo_cmd"));

        var host = Start();
        var guard = new PathGuard(_root);
        var policy = new ApprovalPolicy(guard) { Plugins = host, Level = ApprovalLevel.ReadFree };

        var verdict = policy.NeedsApproval("demo_cmd");

        Assert.True(verdict.Need);
        Assert.Contains("插件", verdict.Reason);

        // 与内置 ws_run_cmd 同等：永不允许一键放行
        Assert.True(policy.NeverAutoApproves("demo_cmd"));
    }

    [Fact]
    public void Approval_LowRiskPluginToolDoesNotNeedApproval()
    {
        var json = """
        {
          "id": "demo",
          "name": "测试插件",
          "tools": [
            {
              "name": "demo_get",
              "description": "查一下",
              "parameters": { "type": "object", "properties": { "q": { "type": "string" } } },
              "run": {
                "type": "http",
                "url": "https://api.example.com/x/{q}",
                "hosts": ["api.example.com"]
              }
            }
          ]
        }
        """;

        WritePlugin("demo", json);

        var host = Start();
        var policy = new ApprovalPolicy(new PathGuard(_root)) { Plugins = host, Level = ApprovalLevel.ReadFree };

        Assert.False(policy.NeedsApproval("demo_get").Need);
        Assert.False(policy.NeverAutoApproves("demo_get"));
    }

    [Fact]
    public void Verify_PluginSideEffectToolsAreCovered()
    {
        WritePlugin("demo", CmdJson("demo", "demo_cmd"));

        var host = Start();

        var options = new VerifyOptions
        {
            Mode = VerifyMode.Auto,
            Endpoint = new VerifierEndpoint { ApiKey = "test" },
        };

        var tool = host.Snapshot()[0];

        // 这条守的正是「插件不能靠自述只读来跳过 AI 核验」
        Assert.True(options.ShouldVerify(tool.Name, tool.HasSideEffects));
    }

    // ------------------------------------------------------------------
    // 执行
    // ------------------------------------------------------------------

    [Fact]
    public async Task Execute_CmdPlugin_ReturnsStdout()
    {
        WritePlugin("demo", CmdJsonWithText("demo", "demo_cmd"));

        var host = Start();
        var tool = host.Snapshot()[0];

        var args = JsonSerializer.SerializeToElement(new { text = "hello fish" });

        var result = await tool.ExecuteAsync(NewContext(), args, default);

        Assert.True(result.Ok, result.Error);

        var stdout = result.Data!.Value.GetProperty("stdout").GetString();

        Assert.Contains("GOT=hello fish", stdout);
    }

    [Fact]
    public async Task Execute_MissingRequiredParameter_Fails()
    {
        WritePlugin("demo", CmdJsonWithText("demo", "demo_cmd"));

        var host = Start();
        var tool = host.Snapshot()[0];

        var result = await tool.ExecuteAsync(
            NewContext(), JsonSerializer.SerializeToElement(new { }), default);

        Assert.False(result.Ok);
        Assert.Contains("text", result.Error);
    }

    [Fact]
    public async Task Execute_ParameterWithNewline_IsRejected()
    {
        WritePlugin("demo", CmdJsonWithText("demo", "demo_cmd"));

        var host = Start();
        var tool = host.Snapshot()[0];

        var args = JsonSerializer.SerializeToElement(new { text = "a\nb" });

        var result = await tool.ExecuteAsync(NewContext(), args, default);

        Assert.False(result.Ok);
        Assert.Contains("换行", result.Error);
    }

    [Fact]
    public async Task Execute_HttpUrlOutsideWhitelist_IsRejected()
    {
        // 清单里声明的是 api.example.com，但这里手工构造一个指向别处的工具，
        // 模拟「解析时白名单被绕过」这种不该发生的情况 —— 运行时要再挡一次。
        var spec = new PluginToolSpec
        {
            Name = "demo_get",
            Description = "d",
            Parameters = EmptySchema(),
            Run = new PluginRunSpec
            {
                Kind = PluginRunKind.Http,
                Http = new PluginHttpSpec
                {
                    Method = "GET",
                    Url = "https://evil.example.net/x",
                    Hosts = ["api.example.com"],
                    Placeholders = [],
                    Headers = new Dictionary<string, string>(),
                    TimeoutSeconds = 5,
                },
            },
            ParameterNames = Array.Empty<string>(),
            RequiredParameters = Array.Empty<string>(),
            EnvironmentNames = new Dictionary<string, string>(),
            IsReadOnly = true,
            EffectiveRisk = PluginRiskTier.Low,
        };

        var manifest = new PluginManifest
        {
            Id = "demo",
            Name = "demo",
            Version = "1.0.0",
            Description = string.Empty,
            Requires = Array.Empty<string>(),
            Tools = [spec],
            DirectoryPath = _root,
            ManifestPath = Path.Combine(_root, "plugin.json"),
        };

        var tool = new PluginTool(manifest, spec);

        var result = await tool.ExecuteAsync(
            NewContext(), JsonSerializer.SerializeToElement(new { }), default);

        Assert.False(result.Ok);
        Assert.Contains("白名单", result.Error);
    }

    /// <summary>一个永远返回成功的最小工具，用于测试注册表优先级。</summary>
    private sealed class FakeTool(string name) : ITool
    {
        public string Name => name;

        public string Description => "fake";

        public JsonElement ParametersSchema => EmptySchema();

        public bool IsReadOnly => true;

        public bool HasSideEffects => false;

        public ValueTask<ToolResult> ExecuteAsync(
            ToolContext context, JsonElement args, CancellationToken ct)
            => ValueTask.FromResult(ToolResult.Success());
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using FatFish.Core.Net;
using FatFish.Core.Plugins;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 插件设置（settings 块 / key 库 / 模型发现 / 设置文件）的测试。
/// </summary>
/// <remarks>
/// 这里刻意**不**碰真实网络：HTTP 部分用一个只会回一句话的 TcpListener 冒充端点。
/// 理由：真实接口要花钱、要 key、还会因为上游抖动而随机失败 ——
/// 那种测试最后会被人加 Skip，等于没测。
///
/// <b>关于「设置块写坏了会怎样」的约定：</b>工具照常装载，只有设置块被丢弃，
/// 并在诊断里留一条 Error。理由是解析器一贯的原则 ——
/// 一个插件里写错一处不该让整组工具消失（模型会突然说"没有这个工具"，
/// 而用户只看到一行一闪而过的日志）。
/// </remarks>
public sealed class PluginSettingsTests
{
    // ---------------------------------------------------------------- 清单

    private static string WriteManifest(string dir, string json)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "plugin.json");
        File.WriteAllText(path, json, Encoding.UTF8);
        return path;
    }

    private static string MinimalManifest(string id, string extra = "")
        => $$"""
           {
             "id": "{{id}}",
             "name": "演示插件",
             "version": "1.0.0",
             "description": "测试用",
             "tools": [
               {
                 "name": "demo_tool",
                 "description": "只是声明一个工具，不会被真的调用",
                 "parameters": { "type": "object", "properties": {} },
                 "run": {
                   "type": "http",
                   "url": "https://api.example.com/v1/ping",
                   "hosts": ["api.example.com"]
                 }
               }
             ]{{extra}}
           }
           """;

    private static PluginParseOutcome ParseWithSettings(string id, string settingsJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ff-ps-" + Guid.NewGuid().ToString("n"), id);

        try
        {
            var path = WriteManifest(dir, MinimalManifest(id, ", \"settings\": " + settingsJson));
            return PluginManifestParser.ParseFile(path);
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(dir)!);
        }
    }

    [Fact]
    public void Settings_WithoutBlock_IsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ff-ps-" + Guid.NewGuid().ToString("n"), "demo-one");

        try
        {
            var path = WriteManifest(dir, MinimalManifest("demo-one"));
            var outcome = PluginManifestParser.ParseFile(path);

            Assert.True(outcome.Ok);
            Assert.Null(outcome.Manifest!.Settings);
        }
        finally
        {
            TryDelete(Path.GetDirectoryName(dir)!);
        }
    }

    [Fact]
    public void Settings_ReadsEveryField()
    {
        var outcome = ParseWithSettings("demo-two", """
            {
              "configKeys": ["DEMO_API_KEY", "DEMO_API_KEYS", "DEMO_BASE_URL", "DEMO_MODEL"],
              "keysEnv": "DEMO_API_KEYS",
              "keyEnv": "DEMO_API_KEY",
              "baseEnv": "DEMO_BASE_URL",
              "modelEnv": "DEMO_MODEL",
              "modelsPath": "/models",
              "defaultBase": "https://api.example.com/v1"
            }
            """);

        Assert.True(outcome.Ok);

        var settings = outcome.Manifest!.Settings;

        Assert.NotNull(settings);
        Assert.Equal(4, settings!.ConfigKeys.Count);
        Assert.Equal("DEMO_API_KEYS", settings.KeysEnv);
        Assert.Equal("DEMO_API_KEY", settings.KeyEnv);
        Assert.Equal("DEMO_BASE_URL", settings.BaseEnv);
        Assert.Equal("DEMO_MODEL", settings.ModelEnv);
        Assert.Equal("/models", settings.ModelsPath);
        Assert.True(settings.CanDiscoverModels);
    }

    [Fact]
    public void Settings_InvalidEnvName_IsReportedButToolsStillLoad()
    {
        // 键名里带短横线：环境变量名不允许。记 Error、丢掉 settings 块，
        // 但工具照常装载 —— 否则一个设置块的笔误会让整组画图工具消失。
        var outcome = ParseWithSettings("demo-three", """{ "configKeys": ["DEMO-KEY"] }""");

        Assert.True(outcome.Ok);
        Assert.Null(outcome.Manifest!.Settings);
        Assert.Contains(outcome.Diagnostics, d => d.Level == PluginDiagnosticLevel.Error);
    }

    [Fact]
    public void Settings_ModelsPathMustBeRelative()
    {
        var outcome = ParseWithSettings("demo-four", """{ "modelsPath": "https://evil.example.com/models" }""");

        Assert.Null(outcome.Manifest!.Settings);
        Assert.Contains(outcome.Diagnostics, d => d.Level == PluginDiagnosticLevel.Error);
    }

    [Fact]
    public void Settings_BadDefaultBase_IsReported()
    {
        var outcome = ParseWithSettings("demo-five", """{ "defaultBase": "ftp://api.example.com" }""");

        Assert.Null(outcome.Manifest!.Settings);
        Assert.Contains(outcome.Diagnostics, d => d.Level == PluginDiagnosticLevel.Error);
    }

    // ---------------------------------------------------------------- key 库

    [Fact]
    public void KeySet_ParsesList_AndStripsQuotes()
    {
        var set = PluginKeySet.Parse("  \"sk-aaa\" , sk-bbb ,, sk-ccc  ", "DEMO_API_KEYS");

        Assert.Equal(3, set.Keys.Count);
        Assert.Equal("sk-aaa", set.Keys[0].Value);
        Assert.Equal("sk-bbb", set.Keys[1].Value);
        Assert.Equal(3, set.Keys[2].Index);
    }

    [Fact]
    public void KeySet_PrefersListOverSingleKey()
    {
        var spec = new PluginSettingsSpec
        {
            ConfigKeys = ["DEMO_KEY", "DEMO_KEYS"],
            KeysEnv = "DEMO_KEYS",
            KeyEnv = "DEMO_KEY",
        };

        var oldKeys = Environment.GetEnvironmentVariable("DEMO_KEYS");
        var oldKey = Environment.GetEnvironmentVariable("DEMO_KEY");

        try
        {
            Environment.SetEnvironmentVariable("DEMO_KEYS", "sk-1,sk-2");
            Environment.SetEnvironmentVariable("DEMO_KEY", "sk-solo");

            var set = PluginKeySet.FromEnvironment(spec);

            Assert.Equal(2, set.Keys.Count);
            Assert.Equal("sk-1", set.Keys[0].Value);

            // 列表清空 → 回退到老的单 key 键（老配置不改也要能用）
            Environment.SetEnvironmentVariable("DEMO_KEYS", null);

            var fallback = PluginKeySet.FromEnvironment(spec);

            Assert.Single(fallback.Keys);
            Assert.Equal("sk-solo", fallback.Keys[0].Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEMO_KEYS", oldKeys);
            Environment.SetEnvironmentVariable("DEMO_KEY", oldKey);
        }
    }

    [Fact]
    public void KeySet_Mask_KeepsOnlyEdges()
    {
        Assert.Equal("(空)", PluginKeySet.Mask(null));
        Assert.Equal("******", PluginKeySet.Mask("sk-abc"));
        Assert.Equal("sk-abc***7b72", PluginKeySet.Mask("sk-abcdefghijklmnop7b72"));
        Assert.DoesNotContain("defghij", PluginKeySet.Mask("sk-abcdefghijklmnop7b72"));
    }

    // ---------------------------------------------------------------- 设置文件

    [Fact]
    public void Store_RoundTrips()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-store-" + Guid.NewGuid().ToString("n"));

        try
        {
            var (initial, error) = PluginSettingsStore.Load(root, "liqiu-image");

            Assert.Null(error);
            Assert.True(initial.Enabled);
            Assert.Null(initial.SelectedModel);

            var saved = new PluginSettings
            {
                Enabled = false,
                SelectedModel = "flux-1.1-pro",
                KnownModels = ["gpt-image-2 default", "flux-1.1-pro"],
                ModelsFetchedAt = DateTimeOffset.Now,
            };

            Assert.Null(PluginSettingsStore.Save(root, "liqiu-image", saved));

            var (loaded, error2) = PluginSettingsStore.Load(root, "liqiu-image");

            Assert.Null(error2);
            Assert.False(loaded.Enabled);
            Assert.Equal("flux-1.1-pro", loaded.SelectedModel);
            Assert.Equal(2, loaded.KnownModels.Count);
            Assert.NotNull(loaded.ModelsFetchedAt);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Store_RejectsTraversalId()
    {
        var root = Path.GetTempPath();

        Assert.Throws<ArgumentException>(() => PluginSettingsStore.PathFor(root, "../evil"));
        Assert.Throws<ArgumentException>(() => PluginSettingsStore.PathFor(root, "a/b"));
    }

    [Fact]
    public void Store_CorruptFile_FallsBackToDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-store-" + Guid.NewGuid().ToString("n"));

        try
        {
            var path = PluginSettingsStore.PathFor(root, "liqiu-image");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ 这不是 json");

            var (settings, error) = PluginSettingsStore.Load(root, "liqiu-image");

            Assert.NotNull(error);
            Assert.True(settings.Enabled);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ---------------------------------------------------------------- 地址与解析

    [Theory]
    [InlineData("https://onliqiu.com/v1", "https://onliqiu.com/v1/models")]
    [InlineData("https://onliqiu.com/v1/", "https://onliqiu.com/v1/models")]
    [InlineData("https://onliqiu.com/v1/images/generations", "https://onliqiu.com/v1/models")]
    [InlineData("https://api.deepseek.com", "https://api.deepseek.com/models")]
    public void BuildUrl_NormalizesBase(string baseUrl, string expected)
    {
        Assert.Equal(expected, ModelCatalog.BuildUrl(baseUrl, "/models")!.ToString());
    }

    [Fact]
    public void ParseModels_HandlesBothShapes()
    {
        var openAi = """{"data":[{"id":"gpt-image-2 default"},{"id":"flux-1.1-pro"},{"id":"gpt-image-2 default"}]}""";
        var alt = """{"models":["b-model","a-model"]}""";

        Assert.Equal(["flux-1.1-pro", "gpt-image-2 default"], ModelCatalog.ParseModels(openAi));
        Assert.Equal(["a-model", "b-model"], ModelCatalog.ParseModels(alt));
    }

    // ---------------------------------------------------------------- 发现流程

    [Fact]
    public async Task Fetch_SkipsDeadKeyAndUsesNext()
    {
        // 第一把 key 回 401，第二把回 200：应该用第二把，并报告 UsedKey=2。
        using var server = new FakeEndpoint(request =>
            request.Contains("Bearer sk-dead")
                ? (401, """{"error":{"message":"invalid api key"}}""")
                : (200, """{"data":[{"id":"gpt-image-2 default"}]}"""));

        var keys = PluginKeySet.Parse("sk-dead,sk-alive", "DEMO_KEYS");

        var result = await ModelCatalog.FetchAsync(keys, server.BaseUrl, "/models", 5);

        Assert.True(result.Ok);
        Assert.Equal(2, result.UsedKey);
        Assert.Single(result.Models);
        Assert.Equal("gpt-image-2 default", result.Models[0]);
    }

    [Fact]
    public async Task Fetch_ServerError_DoesNotTryNextKey()
    {
        // 500 换 key 也一样失败：不该白试第二把（真实环境里那是一次无谓的等待）。
        using var server = new FakeEndpoint(_ => (500, "upstream boom"));

        var keys = PluginKeySet.Parse("sk-1,sk-2,sk-3", "DEMO_KEYS");

        var result = await ModelCatalog.FetchAsync(keys, server.BaseUrl, "/models", 5);

        Assert.False(result.Ok);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task Fetch_EmptyKeySet_FailsWithoutRequest()
    {
        var result = await ModelCatalog.FetchAsync(PluginKeySet.Empty, "https://api.example.com/v1", "/models");

        Assert.False(result.Ok);
        Assert.Contains("key", result.Error!);
    }

    // ---------------------------------------------------------------- 工具

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响结论
        }
    }

    /// <summary>
    /// 只用 TcpListener 说的最小 HTTP 服务。
    /// </summary>
    /// <remarks>
    /// 不用 <see cref="HttpListener"/>：在 Windows 上它需要 URL ACL，
    /// 非管理员的机器上会直接抛"拒绝访问"，把测试变成"看谁的环境配得对"。
    /// 这里只回一句话，用 socket 足够。
    /// </remarks>
    private sealed class FakeEndpoint : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, (int Status, string Body)> _respond;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeEndpoint(Func<string, (int Status, string Body)> respond)
        {
            _respond = respond;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";

        /// <summary>收到的请求数（用来断言"没有白试下一把 key"）。</summary>
        public int Requests { get; private set; }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                using (client)
                {
                    try
                    {
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                        var builder = new StringBuilder();

                        // 只读到空行为止：够拿到请求行与 Authorization 头
                        while (true)
                        {
                            var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);

                            if (line is null || line.Length == 0)
                                break;

                            builder.AppendLine(line);
                        }

                        // 一次只服务一个连接，计数不需要原子操作
                        Requests++;

                        var (status, body) = _respond(builder.ToString());
                        var bytes = Encoding.UTF8.GetBytes(body);

                        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                                   "Content-Type: application/json\r\n" +
                                   $"Content-Length: {bytes.Length}\r\n" +
                                   "Connection: close\r\n\r\n";

                        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _cts.Token).ConfigureAwait(false);
                        await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // 客户端提前断开是正常现象，忽略
                    }
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // 关闭时的取消异常不算失败
            }

            _cts.Dispose();
        }
    }
}

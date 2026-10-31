using System.Net;
using System.Net.Sockets;
using System.Text;
using FatFish.Core.Net;
using FatFish.Core.Plugins;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 逐把 key 查模型并合并（<see cref="ModelCatalogAll"/>）。
/// </summary>
/// <remarks>
/// 这组测试守的是 2026-10-30 实测出来的那个事实：
/// **同一个中转站的两把 key，可见模型可以完全不重叠。**
///   #1 → gpt-image-2 / gpt-image-2 default
///   #2 → grok-imagine-image / -2.0 / -quality
/// 只查第一把，用户就永远选不到另一半；只合并清单不记「模型靠哪把 key」，
/// 选了 grok 又会拿 gpt 那把钥匙去请求，得到 400。
/// </remarks>
public sealed class ModelCatalogAllTests
{
    /// <summary>按 Authorization 头里的 key 分别回不同模型清单的假端点。</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Dictionary<string, string> _byKey;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeServer(Dictionary<string, string> byKey)
        {
            _byKey = byKey;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1";

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                using (client)
                {
                    try
                    {
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                        var head = new StringBuilder();

                        while (true)
                        {
                            var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);

                            if (line is null || line.Length == 0)
                                break;

                            head.AppendLine(line);
                        }

                        var body = "{\"data\":[]}";
                        var status = 200;

                        foreach (var (key, models) in _byKey)
                        {
                            if (!head.ToString().Contains("Bearer " + key, StringComparison.Ordinal))
                                continue;

                            body = models;
                            break;
                        }

                        // 没匹配到任何 key = 401（模拟"这把钥匙无效"）
                        if (body == "{\"data\":[]}")
                        {
                            status = 401;
                            body = "{\"error\":{\"message\":\"invalid api key\"}}";
                        }

                        var bytes = Encoding.UTF8.GetBytes(body);

                        var headText = $"HTTP/1.1 {status} Error\r\n" +
                                       "Content-Type: application/json\r\n" +
                                       $"Content-Length: {bytes.Length}\r\n" +
                                       "Connection: close\r\n\r\n";

                        await stream.WriteAsync(Encoding.ASCII.GetBytes(headText), _cts.Token).ConfigureAwait(false);
                        await stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // 客户端提前断开是常态
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

    [Fact]
    public async Task MergesModelsFromEveryKey_AndRemembersWhichKeyServesEach()
    {
        using var server = new FakeServer(new Dictionary<string, string>
        {
            ["sk-gpt"] = """{"data":[{"id":"gpt-image-2"},{"id":"gpt-image-2 default"}]}""",
            ["sk-grok"] = """{"data":[{"id":"grok-imagine-image"},{"id":"grok-imagine-image-2.0"}]}""",
        });

        var keys = PluginKeySet.Parse("sk-gpt,sk-grok", "DEMO_KEYS");

        var result = await ModelCatalogAll.FetchAsync(keys, server.BaseUrl, "/models", 5);

        Assert.True(result.Ok);
        Assert.Equal(4, result.Models.Count);
        Assert.Contains("grok-imagine-image-2.0", result.Models);

        // 「模型 → key 序号」必须登记对：grok 系列只能由 #2 提供
        Assert.Equal(1, result.ModelKeys["gpt-image-2 default"]);
        Assert.Equal(2, result.ModelKeys["grok-imagine-image"]);
        Assert.Equal(2, result.ModelKeys["grok-imagine-image-2.0"]);
    }

    [Fact]
    public async Task OneBadKey_DoesNotHideTheOtherKeysModels()
    {
        using var server = new FakeServer(new Dictionary<string, string>
        {
            ["sk-grok"] = """{"data":[{"id":"grok-imagine-image"}]}""",
        });

        // 第一把是无效的 → 401；第二把有效
        var keys = PluginKeySet.Parse("sk-dead,sk-grok", "DEMO_KEYS");

        var result = await ModelCatalogAll.FetchAsync(keys, server.BaseUrl, "/models", 5);

        Assert.True(result.Ok);
        Assert.Single(result.Models);
        Assert.Equal(2, result.ModelKeys["grok-imagine-image"]);

        // 失败的那把要留下原因，界面才能解释"为什么比预期的少"
        Assert.Contains(result.Errors, e => e.StartsWith("#1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllKeysDead_ReportsEveryFailure()
    {
        using var server = new FakeServer(new Dictionary<string, string>());

        var keys = PluginKeySet.Parse("sk-a,sk-b", "DEMO_KEYS");

        var result = await ModelCatalogAll.FetchAsync(keys, server.BaseUrl, "/models", 5);

        Assert.False(result.Ok);
        Assert.Empty(result.Models);
        Assert.Equal(2, result.PerKey.Count);
        Assert.Equal(2, result.Errors.Count);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace FatFish.App.Services;

/// <summary>
/// 插件栏里的一个条目。
/// </summary>
public sealed class PluginSlot
{
    /// <summary>唯一标识，与 Python 版 fatgui/plugins.py 的 key 保持一致。</summary>
    public string Key { get; init; } = "";

    /// <summary>状态图标前缀，用于快速扫视。</summary>
    public string Icon { get; init; } = "";

    /// <summary>显示名，短一点（6 字以内最佳）。</summary>
    public string Name { get; init; } = "";

    /// <summary>一句话说明。</summary>
    public string Description { get; init; } = "";

    /// <summary>当前是否在运行。</summary>
    public bool Running { get; set; }

    /// <summary>给 UI 显示的一句人话详情。</summary>
    public string Detail { get; set; } = "";

    public string StateText => Running ? "● 运行中" : "○ 已停止";
}

/// <summary>
/// 插件栏后端：注册表 + 生命周期管理 + 菜单构建。
///
/// 这是 Python 版 <c>workspace/fatgui/plugins.py</c> 在 C# 端的对应实现，语义保持一致：
///
///   1. <b>探测只做 socket 连接</b>（毫秒级，带超时），绝不在 UI 线程枚举进程
///      —— 以前用 PowerShell 查 Win32_Process 单次约 700ms，会把界面冻住。
///   2. <b>启停全程异步</b>，绝不阻塞 UI 线程。
///   3. <b>子进程绝不走 PIPE</b>：一律 <c>CreateNoWindow</c> + 不重定向，
///      输出由 bat 自己写日志文件。
///      背景：PIPE 会随本进程退出而关闭 → 子进程再写就是 EPIPE →
///      若子进程有 "kept alive" 逻辑会无限刷日志（实测 265 MB/分钟）。
///   4. 状态变了就重新探测，不吃缓存（Python 版那 1.5 秒 TTL 缓存是为了
///      避免鼠标划过时连探两遍；C# 这里每次都是点击触发，没必要缓存）。
///   5. <b>停止时不调用任何外部进程</b>：不用 wmic（Win11 已默认移除）、
///      不用 netstat、不用 powershell、不用 taskkill.exe。
///      端口→PID 走 <see cref="PortOwner"/>（iphlpapi!GetExtendedTcpTable），
///      杀树走 <c>Process.Kill(entireProcessTree: true)</c>，全程只有 Win32 调用。
///
/// 目前注册两个条目：
///   · <c>qq_bridge</c>    QQ 桥接：NapCat(:3000) + 桥接 server(:5700) + 本体跟随模式
///   · <c>image_plugin</c> 生图插件：plugins/liqiu-image，可用性由 LIQIU_API_KEY 决定
/// </summary>
public sealed class PluginSlotService
{
    private const int NapCatPort = 3000;
    private const int BridgePort = 5700;

    private readonly string _root;

    /// <param name="root">工作台根目录。</param>
    public PluginSlotService(string root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    public string BridgeDir => Path.Combine(_root, "workspace", "qq_bridge");

    private string WorkspaceDir => Path.Combine(_root, "workspace");
    private string LogDir => Path.Combine(_root, "logs");
    private string ImagePluginDir => Path.Combine(_root, "plugins", "liqiu-image");

    /// <summary>
    /// QQ「本体上线 / 下线」回调。
    ///
    /// <para>
    /// 桥接进程起来<b>不等于</b>能用：桥接还要看本体侧的心跳（<c>data/link/alive.txt</c>），
    /// 心跳过期它就把所有消息静默掉 —— 界面上显示「运行中」，群里发消息却石沉大海，
    /// 这是最难查的一种故障（没有报错、没有日志，就是不回）。
    /// </para>
    /// <para>为 null 表示调用方不管链接层（例如离线测试）。</para>
    /// </summary>
    public Action<bool>? QqLinkOnline { get; set; }

    /// <summary>查询链接层当前状态（菜单里显示用）。返回 (是否在线, 一句话状态)。</summary>
    public Func<(bool Online, string Text)>? QqLinkState { get; set; }

    /// <summary>读一次链接层状态；回调没装或抛异常都当成「不在线」。</summary>
    private (bool Online, string Text) ReadLinkState()
    {
        if (QqLinkState is null)
            return (false, "");

        try
        {
            return QqLinkState();
        }
        catch
        {
            return (false, "");
        }
    }

    // ---------------------------------------------------------------- 探测

    /// <summary>探测所有插件当前状态。</summary>
    public async Task<List<PluginSlot>> ProbeAsync()
    {
        var list = new List<PluginSlot>();

        // ---- QQ 桥接：两个端口都在监听 **且** 本体跟随模式在线，才算真的可用 ----
        bool napcat = await PortOpenAsync(NapCatPort).ConfigureAwait(true);
        bool bridge = await PortOpenAsync(BridgePort).ConfigureAwait(true);

        (bool linkOnline, string linkText) = ReadLinkState();
        bool linkManaged = QqLinkState is not null;

        string qqDetail = bridge && napcat
            ? "桥接 + NapCat 在线（:5700 / :3000）"
            : bridge
                ? "桥接在跑但 NapCat 掉线（:3000 未监听）"
                : napcat
                    ? "NapCat 在跑但桥接未起（:5700 未监听）"
                    : "全部停止";

        if (linkManaged && bridge && napcat)
            qqDetail += " ｜ " + (linkOnline ? "跟随模式在线 ✓" : "跟随模式未开（点一下上线）");

        list.Add(new PluginSlot
        {
            Key = "qq_bridge",
            Icon = "\uD83D\uDCE1",
            Name = "QQ 桥接",
            Description = "NapCat(:3000) + 桥接(:5700) + 跟随模式",
            Running = bridge && napcat && (!linkManaged || linkOnline),
            Detail = qqDetail,
        });

        // ---- 生图插件：清单在不在 + key 配没配 ----
        bool hasManifest = File.Exists(Path.Combine(ImagePluginDir, "plugin.json"));
        bool hasKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LIQIU_API_KEY"));

        string imageDetail = !hasManifest
            ? "未找到 plugins/liqiu-image/plugin.json"
            : hasKey
                ? "已接入 · paint / edit / inspect / describe / open"
                : "已接入，但缺 LIQIU_API_KEY（文件 → 编辑配置）";

        list.Add(new PluginSlot
        {
            Key = "image_plugin",
            Icon = "\uD83C\uDFA8",
            Name = "生图插件",
            Description = "gpt-image-2 · 画 / 改 / 体检 / 代看",
            Running = hasManifest && hasKey,
            Detail = imageDetail,
        });

        return list;
    }

    // ---------------------------------------------------------------- 启停

    /// <summary>
    /// 点一行：已运行则停止，否则启动。
    /// <paramref name="emit"/> 用来把进度写进日志面板与状态栏。
    /// </summary>
    public async Task<bool> ToggleAsync(PluginSlot slot, Action<string> emit)
    {
        if (slot is null)
        {
            emit("插件条目为空");
            return false;
        }

        switch (slot.Key)
        {
            case "qq_bridge":
                return slot.Running
                    ? await StopQqAsync(emit).ConfigureAwait(true)
                    : await StartQqAsync(emit).ConfigureAwait(true);

            case "image_plugin":
                // 生图插件不是「进程」，是 agent 工具集：随程序启动装载，
                // 可用性由 .env 的 LIQIU_API_KEY 决定，没有启停动作。
                emit(slot.Running
                    ? "生图插件由配置管理：想停就删掉 .env 里的 LIQIU_API_KEY 再重启。"
                    : "生图插件缺 key：菜单「文件 → 编辑配置 .env」填 LIQIU_API_KEY，保存后重启。");
                return true;

            default:
                emit("未知插件：" + slot.Key);
                return false;
        }
    }

    /// <summary>兼容旧命令：/qq on | /qq off | /qq status。</summary>
    public async Task<bool> TryHandleCommandAsync(string text, Action<string> emit)
    {
        string t = (text ?? "").Trim();
        if (!t.StartsWith("/qq", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";

        var slots = await ProbeAsync().ConfigureAwait(true);
        PluginSlot? qq = slots.FirstOrDefault(s => s.Key == "qq_bridge");
        if (qq is null)
        {
            emit("插件表里没有 qq_bridge");
            return true;
        }

        switch (arg)
        {
            case "on":
            case "start":
                if (qq.Running)
                {
                    emit("QQ 桥接已在运行： " + qq.Detail);
                    return true;
                }

                // 进程已在、只是跟随模式没开 → StartQqAsync 内部会补上线。
                await StartQqAsync(emit).ConfigureAwait(true);
                return true;

            case "off":
            case "stop":
                if (!qq.Running)
                {
                    emit("QQ 桥接本来就没在运行");
                    return true;
                }
                await StopQqAsync(emit).ConfigureAwait(true);
                return true;

            default:
                emit("QQ 桥接： " + qq.Detail);
                return true;
        }
    }

    private async Task<bool> StartQqAsync(Action<string> emit)
    {
        string snowBat = Path.Combine(BridgeDir, "_run_snowluma.bat");
        string bridgeBat = Path.Combine(BridgeDir, "_run_bridge.bat");

        if (!File.Exists(snowBat) || !File.Exists(bridgeBat))
        {
            emit("缺少启动脚本，期望存在：" + snowBat + " 与 " + bridgeBat);
            return false;
        }

        bool napcat = await PortOpenAsync(NapCatPort).ConfigureAwait(true);
        bool bridge = await PortOpenAsync(BridgePort).ConfigureAwait(true);

        if (napcat && bridge)
        {
            emit("进程已经在跑，无需重复启动");

            // ★ 这里必须也上线：可能是「进程在、跟随模式没开」的情况，
            //   用户点这一下要的就是「让它真的能用」。
            QqLinkOnline?.Invoke(true);
            return true;
        }

        // ① NapCat
        if (napcat)
        {
            emit("NapCat 已在运行，跳过");
        }
        else
        {
            emit("启动 snowluma（NapCat :3000）…");
            if (!StartBatDetached(snowBat, BridgeDir, out string err))
            {
                emit("snowluma 启动失败：" + err);
                return false;
            }

            emit("等待 NapCat 监听 3000 …");
            if (!await WaitPortAsync(NapCatPort, 45).ConfigureAwait(true))
            {
                emit("NapCat 45 秒未就绪，见 workspace/qq_bridge/snowluma/logs/console.out");
                return false;
            }

            emit("NapCat :3000 就绪");
        }

        // ② 桥接本体（工作目录是 workspace，脚本自己会重定向日志）
        if (bridge)
        {
            emit("桥接已在运行，跳过");
        }
        else
        {
            emit("启动桥接 server（:5700）…");
            if (!StartBatDetached(bridgeBat, WorkspaceDir, out string err2))
            {
                emit("桥接启动失败：" + err2);
                return false;
            }

            emit("等待桥接监听 5700 …");
            if (!await WaitPortAsync(BridgePort, 40).ConfigureAwait(true))
            {
                emit("桥接 40 秒未就绪，见 logs/bridge_console.out");
                return false;
            }

            emit("桥接 :5700 就绪");
        }

        emit("QQ 桥接已启动");

        // 进程通了只是「桥接在了」；还要本体侧上线，桥接才肯把消息投过来。
        QqLinkOnline?.Invoke(true);

        return true;
    }

    private async Task<bool> StopQqAsync(Action<string> emit)
    {
        // ★ 先下线跟随模式：撤掉心跳，桥接立刻知道本体走了，
        //   不会再往 inbox 里投消息（否则那些消息会在盘里堆积、无人处理）。
        QqLinkOnline?.Invoke(false);

        var attempted = new HashSet<int>();
        var killed = new List<int>();

        // 最多三轮：第一轮杀掉监听者；若端口还在（树没杀干净、或 bat 里又拉起一个），
        // 重新查表再补一刀。全程只读 TCP 表，不起任何外部进程。
        for (int round = 0; round < 3; round++)
        {
            var targets = new HashSet<int>();
            foreach (int pid in PortOwner.ListeningPids(BridgePort))
                targets.Add(pid);
            foreach (int pid in PortOwner.ListeningPids(NapCatPort))
                targets.Add(pid);

            targets.Remove(Environment.ProcessId);      // 端口万一被自己占着，别自杀
            targets.ExceptWith(attempted);              // 杀过的既不重复报，也不重复杀

            if (targets.Count == 0)
                break;

            foreach (int pid in targets)
            {
                attempted.Add(pid);
                string name = PortOwner.Describe(pid);
                if (PortOwner.KillTree(pid))
                {
                    killed.Add(pid);
                    emit("已停止 " + name);
                }
                else
                {
                    emit("停止失败 " + name + "（可能不是本用户启动，需管理员权限）");
                }
            }

            await Task.Delay(400).ConfigureAwait(true);
        }

        // 等端口真正释放（最多 5 秒）
        bool released = false;
        for (int i = 0; i < 10; i++)
        {
            bool a = await PortOpenAsync(BridgePort).ConfigureAwait(true);
            bool b = await PortOpenAsync(NapCatPort).ConfigureAwait(true);
            if (!a && !b)
            {
                released = true;
                break;
            }
            await Task.Delay(500).ConfigureAwait(true);
        }

        if (killed.Count > 0)
            emit(released ? "QQ 桥接已停止（终止 " + killed.Count + " 个进程）" : "已终止进程，但端口仍被占用");
        else if (attempted.Count > 0)
            emit("未能停止（权限不足或进程拒绝退出）");
        else
            emit("本来就没在运行");

        return true;
    }

    // ---------------------------------------------------------------- 底层工具

    /// <summary>TCP 探测：只判断有没有人在监听，不发送任何数据。</summary>
    private static async Task<bool> PortOpenAsync(int port, int timeoutMs = 250)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitPortAsync(int port, int seconds)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
        while (DateTime.UtcNow < end)
        {
            if (await PortOpenAsync(port).ConfigureAwait(true))
                return true;
            await Task.Delay(600).ConfigureAwait(true);
        }
        return false;
    }

    /// <summary>
    /// 用 cmd /c 跑一个 bat，独立进程、无窗口、不重定向。
    /// 输出与重定向由 bat 自己负责 —— 这里若接管 PIPE，本程序退出时管道关闭，
    /// 子进程会收到 EPIPE 并可能无限重试刷日志。
    /// </summary>
    private static bool StartBatDetached(string batPath, string workingDir, out string error)
    {
        error = "";
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(batPath);

            using var proc = Process.Start(psi);
            return proc is not null;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- 菜单

    /// <summary>
    /// 构建插件栏菜单（点顶栏「插件」按钮时调用）。
    /// 每次打开都重新探测，因此状态总是当下的。
    /// </summary>
    public async Task<ContextMenu> BuildMenuAsync(Action<string> emit)
    {
        var menu = new ContextMenu { MinWidth = 320 };
        MenuItem? refreshItem = null;

        async Task RefillAsync()
        {
            if (refreshItem is null)
            {
                refreshItem = new MenuItem { Header = "⟳ 刷新状态" };
                refreshItem.Click += async (_, _) => await RefillAsync().ConfigureAwait(true);
            }

            menu.Items.Clear();

            menu.Items.Add(new MenuItem
            {
                Header = "插件栏 · 点一行 = 启动 / 停止",
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
            });
            menu.Items.Add(new Separator());

            List<PluginSlot> slots = await ProbeAsync().ConfigureAwait(true);

            foreach (PluginSlot slot in slots)
            {
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = slot.Icon + "  " + slot.Name + "    " + slot.StateText,
                    FontWeight = FontWeights.SemiBold,
                });
                panel.Children.Add(new TextBlock
                {
                    Text = slot.Detail,
                    FontSize = 11,
                    Opacity = 0.72,
                    Margin = new Thickness(0, 1, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });

                var item = new MenuItem { Header = panel };
                PluginSlot captured = slot;
                item.Click += async (_, _) =>
                {
                    menu.IsOpen = false;
                    emit(captured.Name + " → " + (captured.Running ? "停止…" : "启动…"));
                    await ToggleAsync(captured, emit).ConfigureAwait(true);
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(refreshItem);

            menu.Items.Add(new MenuItem
            {
                Header = "命令兼容：/qq on ｜ /qq off ｜ /qq status",
                IsEnabled = false,
                FontSize = 11,
                Opacity = 0.6,
            });
        }

        await RefillAsync().ConfigureAwait(true);
        return menu;
    }
}

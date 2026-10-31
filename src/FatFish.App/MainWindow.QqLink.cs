using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FatFish.App.Services;
using FatFish.Core.Agent;
using FatFish.Core.Llm;
using FatFish.Core.Safety;
using FatFish.Core.Tools;

namespace FatFish.App;

/// <summary>
/// QQ 跟随模式的界面接线（partial 扩展）。
///
/// <para>
/// 这一层只做三件事，真正的协议全在 <see cref="QqLinkService"/> 里：
/// </para>
/// <list type="number">
/// <item>把「跑一轮 AgentLoop」这件事交给宿主（<see cref="RunQqTurnAsync"/>）；</item>
/// <item>把 QQ 回合的进度显示在聊天区 / 过程面板 / 日志里；</item>
/// <item>让 <c>/qq on</c> 与插件栏那个条目同时管「进程」和「链接层」。</item>
/// </list>
///
/// <para>
/// <b>为什么不复用 MainWindow 自己当 observer：</b>
/// 主窗口的 <c>RequestApprovalAsync</c> 会弹模态窗。QQ 回合里人很可能在手机上，
/// 弹窗只会把整轮永久卡住 —— 旧版专门为这件事做了「按人分流」。这里采用同一策略：
/// QQ 回合一律不弹窗，需人工确认的动作直接拒绝并说明去电脑上操作。
/// </para>
/// </summary>
public partial class MainWindow
{
    private QqLinkService? _qqLink;
    private bool _qqLinkInstalled;

    /// <summary>
    /// 装好链接层（不自动上线）。由 <see cref="OnContentRendered"/> 调用。
    /// </summary>
    private void InstallQqLink()
    {
        if (_qqLinkInstalled)
            return;

        string? root = _workspaceSession?.Root;
        if (string.IsNullOrWhiteSpace(root))
        {
            Log("[QQ] 工作台根未知，链接层未安装");
            return;
        }

        try
        {
            _qqLink = new QqLinkService(root, RunQqTurnAsync, msg => Log(msg));
            _qqLinkInstalled = true;
            Log("[QQ] 链接层已就绪 —— /qq on 上线，/qq off 下线，或点顶栏「插件」里的 QQ 条目");
        }
        catch (Exception ex)
        {
            Log("[QQ] 链接层安装失败：" + ex.GetType().Name + " - " + ex.Message);
        }
    }

    /// <summary>
    /// 统一创建插件栏后端，并把「链接层」接进去。
    ///
    /// <para>
    /// 两个调用点（顶栏菜单、<c>/qq</c> 命令）都必须走这里 —— 否则会出现
    /// 「菜单里能上线、命令里不能」这种半截接线，而它只会在用户换了个入口时才暴露。
    /// </para>
    /// </summary>
    private PluginSlotService CreatePluginService(string root)
    {
        var svc = new PluginSlotService(root);

        svc.QqLinkOnline = SetQqLinkOnline;
        svc.QqLinkState = () => (_qqLink?.Online ?? false, _qqLink?.StatusLine ?? "链接层未安装");

        return svc;
    }

    /// <summary>链接层上下线（幂等，可在任意线程调用）。</summary>
    private void SetQqLinkOnline(bool online)
    {
        QqLinkService? svc = _qqLink;

        if (svc is null)
        {
            Log("[QQ] 链接层未安装，无法切换上线状态");
            return;
        }

        try
        {
            if (online)
            {
                svc.Start();
                EmitPluginMessage(svc.Online
                    ? "跟随模式已上线 —— 群里 @我 或私聊就会回"
                    : "跟随模式上线失败，见日志");
            }
            else
            {
                svc.Stop();
                EmitPluginMessage("跟随模式已下线");
            }
        }
        catch (Exception ex)
        {
            EmitPluginMessage("链接层切换失败：" + ex.GetType().Name + " - " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 跑一轮

    /// <summary>
    /// 链接层取到一条 QQ 消息后，在这里跑一轮完整的 AgentLoop
    /// （工具、审批、双人核验全都在 —— 与命令行里是同一个我）。
    /// </summary>
    private async Task<QqTurnReply> RunQqTurnAsync(QqTurnRequest req, CancellationToken ct)
    {
        AgentLoop? agent = _agent;

        if (agent is null)
            return new QqTurnReply("内核还没就绪，稍等一下再问我。", true);

        // ★ 串行保护：AgentLoop 只有一份历史。若用户正在手动跑一轮，
        //   并发进来会让两边历史交错（模型看到半截对话，工具结果错位）。
        //   旧版靠主循环天然串行；这里用「等它跑完，最多 90 秒」来复现同一语义。
        for (int i = 0; i < 90 && _isRunning; i++)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        if (_isRunning)
            return new QqTurnReply("我这边正忙着跑别的任务，过会儿再叫我。", true);

        string who = (req.IsGroup ? "【QQ群】" : "【QQ私聊】") + req.Who;
        OnUi(() => AppendMessage("user", who + "：" + Shorten(req.Text, 400)));

        var observer = new QqTurnObserver(this);

        AgentRunResult result;

        try
        {
            result = await Task.Run(() => agent.RunAsync(req.Text, observer, ct), ct)
                                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log("[QQ] 回合失败：" + ex.GetType().Name + " - " + ex.Message);
            return new QqTurnReply("（我这边出错了：" + ex.Message + "）", true);
        }

        string final = result.FinalText ?? "";

        OnUi(() => AppendMessage("assistant", final));

        Log($"[QQ] 回合完成：{result.Rounds} 轮 / {result.ToolCallCount} 次工具"
            + (result.Usage is null ? "" : $" ｜ {result.Usage}"));

        return new QqTurnReply(final, !string.IsNullOrWhiteSpace(final));
    }

    private static string Shorten(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    // ---------------------------------------------------------------- 观察者

    /// <summary>
    /// QQ 回合专用观察者：文字进聊天区、思考链进过程面板、工具进日志，
    /// <b>审批一律不弹窗</b>。
    /// </summary>
    private sealed class QqTurnObserver : IAgentObserver
    {
        private readonly MainWindow _window;
        private readonly StringBuilder _text = new();

        public QqTurnObserver(MainWindow window) => _window = window;

        public void OnTextDelta(string text) => _text.Append(text);

        public void OnReasoningDelta(string text) => _window.AppendReasoning(text);

        public void OnToolCallStarted(string toolName, JsonElement args)
            => _window.Log("[QQ] → 调用 " + toolName);

        public void OnToolCallFinished(string toolName, ToolResult result)
            => _window.Log(result.Ok
                ? $"[QQ] ← {toolName} 完成 {result.ElapsedMs}ms"
                : $"[QQ] ← {toolName} 失败 {result.Error}");

        public void OnStatus(string text) => _window.Log("[QQ] " + text);

        /// <summary>
        /// QQ 回合不弹人工审批窗。
        ///
        /// <para>
        /// 两条理由，缺一都不成立：
        /// ① 人可能在手机上，弹窗会把整轮永久卡住（旧版踩过，回合直接死在那儿）；
        /// ② 结果会发回 QQ —— 对敏感文件来说，"批准"等于把密钥送进聊天记录。
        /// </para>
        /// <para>
        /// 所以这里统一拒绝，并在日志里说清是哪个动作、为什么。
        /// 模型收到"用户拒绝"后会改用说明，而不是反复重试。
        /// </para>
        /// </summary>
        public ValueTask<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
        {
            _window.Log("[QQ] ✋ 拒绝需人工确认的动作：" + request.Tool
                        + "（" + request.Reason + "）—— QQ 上不批准，请到电脑上操作");

            return ValueTask.FromResult(false);
        }
    }
}

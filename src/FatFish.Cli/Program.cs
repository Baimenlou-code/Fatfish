using System.Text;
using FatFish.Core.Agent;
using FatFish.Core.Config;
using FatFish.Core.Diagnostics;
using FatFish.Core.Llm;
using FatFish.Core.Net;
using FatFish.Core.Plugins;
using FatFish.Core.Safety;
using FatFish.Core.Session;
using FatFish.Core.Tools;
using FatFish.Core.Tools.Web;
using FatFish.Core.Tools.Workspace;

namespace FatFish.Cli;

/// <summary>
/// 命令行入口。
///
/// 用法：
///   fatfish                      → 交互式对话（自动接着上次的会话）
///   fatfish "你好"                → 单次提问，答完退出
///   fatfish --probe              → 自检：只验证配置与连通性，不进入对话
///   fatfish --selftest           → 本地自检：不花一分钱，检查配置/目录/权限
///   fatfish --allow              → 交互模式下自动批准所有写操作（慎用）
///   fatfish --fresh              → 不恢复上次会话，从空白开始
///
/// 这是验证内核的最短路径：没有 WPF、没有线程调度，一眼能看出问题出在哪一层。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var autoApprove = args.Contains("--allow");
        var probeOnly = args.Contains("--probe");
        var fresh = args.Contains("--fresh");
        var selfTest = args.Contains("--selftest");

        var prompt = string.Join(
            ' ',
            args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)));

        try
        {
            return await RunAsync(prompt, probeOnly, autoApprove, fresh, selfTest).ConfigureAwait(false);
        }
        catch (LlmException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[模型调用失败] {ex.Message}");
            Console.ResetColor();
            return 2;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[致命错误] {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();
            return 1;
        }
    }

    private static async Task<int> RunAsync(
        string prompt, bool probeOnly, bool autoApprove, bool fresh, bool selfTest)
    {
        // ---------- 1. 工作台 ----------
        var root = Environment.GetEnvironmentVariable("FATFISH_WORKSPACE");
        if (string.IsNullOrWhiteSpace(root))
            root = Directory.GetCurrentDirectory();

        // ---------- 1.5 工作台会话 ----------
        // 根目录在运行时可切换（ws_cd / ws_cd_approve）。
        // DefaultRoot = 启动时的根；它是「换根要不要批准」的判据，**不是**安全边界 ——
        // 安全边界是当前 Guard，换根之后由新 Guard 继续守。
        var wsSession = new WorkspaceSession(root);

        var guard = wsSession.Guard;
        var tickets = wsSession.Tickets;

        Console.WriteLine("工作台 : " + guard.Root);

        // ---------- 1.5 本地自检 ----------
        // ★ 放在最前面、且在配置检查之前：自检要能解释「为什么后面会失败」。
        //   它不联网、不花钱，所以可以无条件跑一遍 —— 但只有 --selftest 才打印，
        //   否则每次启动都刷一屏，用户会习惯性忽略它（那自检就白做了）。
        if (selfTest)
        {
            var report = StartupCheck.Run(guard.Root);

            Console.WriteLine();
            Console.WriteLine(report.ToText());

            var fails = report.Items.Count(i => i.Status == CheckStatus.Fail);
            var warns = report.Items.Count(i => i.Status == CheckStatus.Warn);

            Console.WriteLine();
            Console.WriteLine(fails == 0 && warns == 0
                ? "✅ 自检通过"
                : $"自检完成：{fails} 项失败，{warns} 项警告");

            return fails > 0 ? 4 : 0;
        }

        // ---------- 1.6 插件（自适应发现 / 能力比对 / 风险推断）----------
        // ★ 这段的顺序不是随手排的：
        //   插件宿主需要「内置工具名单」才能拒绝重名；
        //   审批策略需要插件宿主才能认识插件工具名；
        //   注册表需要插件宿主才能把动态工具一起交出去。
        //   所以顺序只能是：内置工具 → 插件宿主 → 审批策略 → 注册表。
        var builtin = new ITool[]
        {
            new WsWhereTool(),
            new WsReadTool(),
            new WsListTool(),
            new WsSearchTool(),
            new WsWriteTool(),
            new WsAppendTool(),
            new WsReplaceTool(),
            new WsDeleteTool(),
            new WsMkdirTool(),
            new WsForgetTool(),
            new WsCdTool(),
            new WsCdApproveTool(),
            new WsRunCmdTool(),
            new WsRunPythonTool(),
            new WsBgCmdTool(),
            new WsBgPythonTool(),
            new WsBgListTool(),
            new WsBgTailTool(),
            new WsBgKillTool(),
            new WebSearchTool(),
            new WebFetchTool(),
            new NetBalanceTool(),
            new WsPluginListTool(),
            new WsPluginInfoTool(),
            new WsPluginReloadTool(),
        };

        var pluginDirs = new List<PluginDirectory>
        {
            // 程序目录：随程序发布的插件，升级时会被覆盖
            new(Path.Combine(AppContext.BaseDirectory, "plugins"), BuiltIn: true),

            // 工作台根：用户自己的插件，升级不影响；同名时覆盖上面的
            new(Path.Combine(guard.Root, "plugins"), BuiltIn: false),
        };

        // 环境变量优先：调试单个插件时不必来回拷文件
        var pluginDirOverride = Environment.GetEnvironmentVariable("FATFISH_PLUGIN_DIR");

        if (!string.IsNullOrWhiteSpace(pluginDirOverride))
        {
            pluginDirs.Insert(0, new PluginDirectory(
                Path.GetFullPath(pluginDirOverride.Trim()), BuiltIn: false));
        }

        var plugins = PluginHost.Start(
            new PluginHostOptions
            {
                Directories = pluginDirs,
                ReservedToolNames = builtin.Select(t => t.Name).ToHashSet(StringComparer.Ordinal),
            },
            log: msg =>
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  " + msg);
                Console.ResetColor();
            });

        var policy = new ApprovalPolicy(wsSession) { Plugins = plugins };

        // ---------- 2. 配置 ----------
        var (envPath, count) = EnvFile.LoadNearest(root);

        if (envPath is null)
        {
            WriteWarn("未找到 .env 文件。请在当前目录或上层放一个，写上 FATFISH_API_KEY=sk-xxxx");
            return 3;
        }

        Console.WriteLine($"配置   : {envPath}（载入 {count} 项）");

        // 与 Python 版同序：FATFISH_API_KEY 优先，DEEPSEEK_API_KEY 作为旧写法兼容。
        if (!LlmOptions.TryResolveApiKey(out _, out _))
        {
            WriteWarn(".env 里没有 FATFISH_API_KEY（旧写法 DEEPSEEK_API_KEY 也认）");
            return 3;
        }

        var options = LlmOptions.FromEnvironment();
        Console.WriteLine("模型   : " + options.Describe());

        // ---------- 3. 组装内核 ----------
        // 后台任务管理器：ws_bg_* 五个工具的状态中枢。
        // Finished 事件在后台线程触发，所以播报必须自己保证线程安全（见 AnnounceJob）。
        using var jobs = new BackgroundJobManager();
        jobs.Finished += AnnounceJob;

        // 联网服务 + 台账。
        // ★ HttpClient 在 NetService 内部是进程级共享的，所以整个进程只需要一个实例 ——
        //   这也正是「工具不该自己 new」的原因：三个工具各建一份 = 三份连接池 + 三套限速桶。
        UsageLedger? ledger = null;

        try
        {
            ledger = UsageLedger.Open(guard.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteWarn("联网台账无法创建（联网功能不受影响，只是不记账）：" + ex.Message);
        }

        using var net = new NetService(NetOptions.FromEnvironment(), ledger);

        var context = new ToolContext
        {
            // Guard / Tickets 是「初始绑定」，Workspace 会让它们跟着会话走：
            // ws_cd 换根之后，任何工具读 context.Guard 拿到的都是新根。
            Guard = guard,
            Tickets = tickets,
            Workspace = wsSession,
            Approval = policy,
            Jobs = jobs,
            Net = net,
            Plugins = plugins,
            Log = msg =>
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  " + msg);
                Console.ResetColor();
            },
        };

        var registry = new ToolRegistry(builtin, plugins);

        using var llm = new DeepSeekClient(options);

        // ---------- 3.5 双人核验（第二位 AI 审查员）----------
        // ★ 与主模型**分开**的端点：留空时回落到主模型配置，但允许配成别家模型。
        //   同一个模型审自己，等于自己给自己签字 —— 它的系统性偏差会被原样复制一遍。
        var verifyOptions = VerifyOptions.FromEnvironment(options);

        using var verifier = new VerifierService(
            verifyOptions,
            log: msg =>
            {
                // 审查员的每一句话都用暗紫色，和主模型的正文区分开：
                // 混在正常输出里时，用户会以为那是肥鱼自己说的。
                Console.ForegroundColor = ConsoleColor.DarkMagenta;
                Console.WriteLine("  " + msg);
                Console.ResetColor();
            },
            logDirectory: Path.Combine(guard.Root, "logs", "verify"));

        var verifySession = new VerifySession(verifier);
        var userEvents = new UserEventLog();

        var observer = new ConsoleObserver(autoApprove);

        var agent = new AgentLoop(
            llm,
            registry,
            context,
            policy,
            new AgentOptions { MaxRounds = 25 },
            verifySession,
            userEvents);

        Console.WriteLine("工具   : " + string.Join(", ", registry.All.Select(t => t.Name)));
        Console.WriteLine("插件   : " + plugins.Describe());
        Console.WriteLine("核验   : " + verifyOptions.Describe());
        Console.WriteLine(new string('-', 60));

        // ---------- 4. probe：只验证连通 ----------
        if (probeOnly)
        {
            Console.WriteLine("探针模式：发送一条最小请求，验证 API 连通与 SSE 解析…\n");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var turn = await llm.CompleteAsync(
                new[] { ChatMessage.User("只需要回复两个字：连通") },
                tools: null,
                onTextDelta: t => Console.Write(t)).ConfigureAwait(false);

            sw.Stop();

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine($"✅ 连通正常｜耗时 {sw.ElapsedMilliseconds}ms｜回复「{turn.Text.Trim()}」");

            if (turn.Usage is not null)
                Console.WriteLine($"   用量 {turn.Usage}");

            return 0;
        }

        // ---------- 4.5 会话落盘 ----------
        // ★ 落盘失败绝不能影响对话：磁盘满、目录只读、文件被占用都会发生。
        //   所以全部包在 try 里，失败就退化成「不记日志」，而不是「不能聊天」。
        //   也正因为它只影响「能不能恢复」，所以放在 probe 之后 —— probe 不该产生会话文件。
        SessionStore? session = null;

        try
        {
            if (!fresh && SessionStore.LoadLatest(guard.Root) is { Messages.Count: > 0 } restored)
            {
                // 接着写原来的文件。恢复的语义是「那段对话还在继续」，
                // 另开新文件会让一次对话看起来像两次。
                session = SessionStore.Attach(
                    restored.FilePath, restored.SessionId, restored.TotalEntries);

                var restoredCount = agent.RestoreHistory(restored.Messages);

                Console.WriteLine(
                    $"会话   : 恢复 {restoredCount} 条 ← {Path.GetFileName(restored.FilePath)}");
            }
            else
            {
                session = SessionStore.Create(guard.Root);
                Console.WriteLine("会话   : " + Path.GetFileName(session.FilePath));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteWarn("会话无法落盘（对话不受影响）：" + ex.Message);
        }

        using var sessionScope = session;

        if (session is not null)
        {
            // 订阅点是 AgentLoop 里「消息真正进入历史之后」那一处，
            // 所以这里看到的序列与模型看到的完全一致，不会出现「日志有、历史没有」的错位。
            agent.Traced += entry => session.Append(
                entry.Message, entry.ToolName, entry.ToolOk, entry.ElapsedMs,
                entry.ToolNames, entry.Usage);

            // /new：开新文件，旧文件留着
            agent.HistoryCleared += session.StartNew;
        }

        // ---------- 5. 单次问答 ----------
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            var result = await agent.RunAsync(prompt, observer).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine(new string('-', 60));
            Console.WriteLine($"轮次 {result.Rounds}｜工具 {result.ToolCallCount} 次" +
                              (result.Usage is null ? string.Empty : $"｜{result.Usage}"));

            return 0;
        }

        // ---------- 6. 交互模式 ----------
        Console.WriteLine("输入内容后回车发送；/new 开新会话；/ws 看工作台；/plugin 看插件；/balance 查余额；/tools 列工具；空行或 Ctrl+C 退出。");
        Console.WriteLine();

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("🐟 > ");
            Console.ResetColor();

            var line = Console.ReadLine();

            if (line is null || string.IsNullOrWhiteSpace(line))
                break;

            // 本地命令不经过模型，所以必须在这里记进「用户原声」——
            // 审查员据此判断「用户是否已亲自授权」，而模型无法伪造这条记录。
            if (line.Trim().StartsWith('/'))
                userEvents.Record("用户命令：" + line.Trim());

            if (line.Trim() is "exit" or "quit" or "/q")
                break;

            if (line.Trim() is "/new")
            {
                // ResetHistory 会触发 HistoryCleared，落盘器据此开一个新文件；
                // 旧文件不删，它是一段已经结束的对话。
                agent.ResetHistory();
                Console.WriteLine("已开始新会话（上一次的记录还在 logs/sessions 里）。");
                Console.WriteLine();
                continue;
            }

            if (line.Trim() is "/balance" or "/bal")
            {
                // 直接查余额，不经过模型 —— 这类「看一眼数字」的动作没必要花 token。
                var svc = new BalanceService();
                var snap = await svc.FetchAsync().ConfigureAwait(false);

                // 分级必须用同一个 svc：阈值可能被 .env 覆盖（NETBAL_DS_WARN 等），
                // 换一个实例虽然也会读同样的环境变量，但「为什么这里要再 new 一个」会让人困惑。
                var (dsLv, tvLv, overLv) = snap.Grade(svc);

                Console.WriteLine();
                Console.WriteLine($"  DeepSeek : {(snap.DeepSeek.Ok ? $"¥{snap.DeepSeek.Total:0.00}" : "查询失败")} " +
                                  $"{BalanceService.LevelIcon(dsLv)} {BalanceService.LevelText(dsLv)}" +
                                  (snap.DeepSeek.Error is null ? string.Empty : $"（{snap.DeepSeek.Error}）"));

                Console.WriteLine($"  Tavily   : {(snap.Tavily.Ok && snap.Tavily.Left is { } left ? $"{left} 剩余（{snap.Tavily.LeftPct:0.#}%）" : "查询失败")} " +
                                  $"{BalanceService.LevelIcon(tvLv)} {BalanceService.LevelText(tvLv)}" +
                                  (snap.Tavily.PlanLimit > 0 ? $"（{snap.Tavily.Plan}，额度 {snap.Tavily.PlanLimit}）" : string.Empty) +
                                  (snap.Tavily.Error is null ? string.Empty : $"（{snap.Tavily.Error}）"));

                Console.WriteLine($"  综合     : {BalanceService.LevelIcon(overLv)} {BalanceService.LevelText(overLv)}");
                Console.WriteLine();
                continue;
            }

            if (line.Trim() is "/tools")
            {
                Console.WriteLine();
                foreach (var t in registry.All.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    var flags = (t.IsReadOnly ? "只读" : "写/执行") +
                                (policy.NeedsApproval(t.Name).Need ? " 需审批" : string.Empty);

                    Console.WriteLine($"  {t.Name,-14} {flags,-12} {FirstLine(t.Description)}");
                }

                Console.WriteLine($"  共 {registry.Count} 个工具");
                Console.WriteLine();
                continue;
            }

            if (TryParseWsCommand(line.Trim(), out var wsArg))
            {
                // 本地切换不经过模型 —— 与 ws_cd 工具走的是同一个 session，
                // 所以两条路径的规则（是否需批准、换根清凭证）必然一致。
                if (wsArg.Length == 0)
                {
                    Console.WriteLine("  " + wsSession.Describe());
                }
                else if (wsArg.Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("  " + wsSession.Reset().Message);
                }
                else if (wsArg.Equals("approve", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("  " + wsSession.ApprovePending().Message);
                }
                else if (wsArg.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(wsSession.CancelPending() ? "  已撤销挂起的切换请求" : "  当前没有挂起请求");
                }
                else if (wsArg.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
                {
                    var r = wsSession.ChangeTo(wsArg[3..].Trim());

                    if (r.NeedsApproval)
                    {
                        // 这条分支不要直接打印 r.Message：那句话是**给模型**的
                        // （「请先向用户说明…再调用 ws_cd_approve」），
                        // 而现在说话的就是用户本人，念出来只会莫名其妙。
                        Console.WriteLine($"  ⚠️ 目标 {wsSession.PendingRoot} 在默认作业区之外，需要确认");
                        Console.WriteLine($"     默认作业区：{wsSession.DefaultRoot}");
                        Console.WriteLine("     输入 /ws approve 放行，或 /ws cancel 撤销（暂不切换）");
                    }
                    else
                    {
                        Console.WriteLine("  " + r.Message);

                        if (r.Ok)
                            Console.WriteLine("  （注意：模型不会自动知道根变了，必要时让它调 ws_where 确认）");
                    }
                }
                else
                {
                    Console.WriteLine("  用法：/ws ｜ /ws cd <目录> ｜ /ws approve ｜ /ws cancel ｜ /ws reset");
                }

                Console.WriteLine();
                continue;
            }

            if (TryParsePluginCommand(line.Trim(), out var pluginArg))
            {
                if (pluginArg.Length == 0 || pluginArg.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    PrintPlugins(plugins);
                }
                else if (pluginArg.Equals("reload", StringComparison.OrdinalIgnoreCase))
                {
                    // 本地重载不经过模型 —— 与 ws_plugin_reload 工具走的是同一个宿主，
                    // 所以两条路径看到的诊断、保留旧快照的策略必然一致。
                    var report = plugins.Reload(force: true);

                    Console.WriteLine("  " + report.Summary);

                    foreach (var d in report.Diagnostics
                                 .Where(d => d.Level != PluginDiagnosticLevel.Info))
                    {
                        Console.WriteLine("    " + d);
                    }
                }
                else if (pluginArg.StartsWith("info ", StringComparison.OrdinalIgnoreCase))
                {
                    PrintPluginDetail(plugins, pluginArg[5..].Trim());
                }
                else
                {
                    Console.WriteLine("  用法：/plugin ｜ /plugin reload ｜ /plugin info <插件id 或 工具名>");
                }

                Console.WriteLine();
                continue;
            }

            if (line.Trim().StartsWith("/verify", StringComparison.OrdinalIgnoreCase))
            {
                var arg = line.Trim()[7..].Trim().ToLowerInvariant();

                switch (arg)
                {
                    case "on" or "auto":
                        verifyOptions.Mode = VerifyMode.Auto;
                        Console.WriteLine("  双人核验已开启：AUTO（仅有副作用的动作送审）");
                        break;

                    case "all":
                        verifyOptions.Mode = VerifyMode.All;
                        Console.WriteLine("  双人核验已开启：ALL（连只读也送审，token 消耗显著上升）");
                        break;

                    case "off":
                        verifyOptions.Mode = VerifyMode.Off;
                        Console.WriteLine("  双人核验已关闭（人工报批仍然生效，但只剩一道闸）");
                        break;

                    case "ping":
                        if (!verifyOptions.IsEnabled)
                        {
                            WriteWarn("  核验当前是关闭的。先 /verify on，再 ping。");
                            break;
                        }

                        Console.WriteLine("  正在自检审查员连通性（真实调用一次，会产生少量费用）…");

                        var probe = new ReviewRequest
                        {
                            // 用一个只读动作做探针：核验失败时它也不会造成任何副作用。
                            Actions = new[]
                            {
                                ("ws_list",
                                    System.Text.Json.JsonSerializer.SerializeToElement(
                                        new { path = "" })),
                            },
                            UserGoal = "连通性自检",
                            AiPlan = "我只是想 ping 一下审查员，确认它在线，不会真的执行任何动作。",
                        };

                        var pingResult = await verifier.ReviewAsync(probe).ConfigureAwait(false);

                        Console.WriteLine($"  {pingResult.SummaryLine()}（{pingResult.ElapsedSeconds:0.00}s）");
                        break;

                    default:
                        Console.WriteLine();
                        Console.WriteLine("  " + verifyOptions.Describe());
                        Console.WriteLine("  上限：" + verifyOptions.LimitsLabel());
                        Console.WriteLine($"  本轮：打回 {verifySession.Retries}/{verifyOptions.MaxRetries}，" +
                                          $"补充 {verifySession.Supplements}/{verifyOptions.MaxSupplements}");
                        Console.WriteLine($"  审查员：{verifyOptions.Endpoint.Describe()}");
                        Console.WriteLine("  用法：/verify on | all | off | ping");
                        break;
                }

                Console.WriteLine();
                continue;
            }

            Console.WriteLine();

            var result = await agent.RunAsync(line, observer).ConfigureAwait(false);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [{result.Rounds} 轮 / {result.ToolCallCount} 次工具" +
                              (result.Usage is null ? string.Empty : $" / {result.Usage}") + "]");
            Console.ResetColor();
            Console.WriteLine();
        }

        return 0;
    }

    private static readonly object ConsoleGate = new();

    /// <summary>
    /// 播报后台任务结束。
    ///
    /// ★ 这个方法会在**后台线程**上被调用（任务收尾时），而主线程可能正在流式打印
    /// 模型的回答。不加锁的话两段输出会交错成乱码 —— 而且只在「回答正好说到一半时
    /// 任务结束」才出现，几乎不可能复现。所以整段输出加一把锁。
    /// </summary>
    private static void AnnounceJob(BackgroundJobSnapshot snap)
    {
        lock (ConsoleGate)
        {
            var (color, tag) = snap.Status switch
            {
                "done" => (ConsoleColor.Green, "完成"),
                "failed" => (ConsoleColor.Red, "失败"),
                "timeout" => (ConsoleColor.Yellow, "超时"),
                "killed" => (ConsoleColor.DarkYellow, "已中止"),
                _ => (ConsoleColor.Gray, snap.Status),
            };

            try
            {
                Console.WriteLine();
                Console.ForegroundColor = color;
                Console.WriteLine(
                    $"[后台{tag}] {snap.Id} {snap.Desc}" +
                    $"（{snap.ElapsedMs / 1000.0:0.0}s，输出 {WorkspaceFilters.HumanSize(snap.OutputBytes)}）");

                if (snap.Error is not null)
                    Console.WriteLine("    " + snap.Error);

                Console.ResetColor();
            }
            catch (IOException)
            {
                // 控制台被关掉了：没什么可做的
            }
        }
    }

    /// <summary>
    /// 解析本地 <c>/ws</c> 命令。返回 false 表示这不是 <c>/ws</c>。
    /// </summary>
    /// <remarks>
    /// 单独抽出来是为了「整词匹配」：直接 <c>StartsWith("/ws")</c> 会把
    /// 将来可能出现的 <c>/wsx</c> 之类也吞掉，而那种错误表现为
    /// 「输入被吃掉了、什么也没打印」，很难联想到这里。
    /// </remarks>
    private static bool TryParseWsCommand(string text, out string arg)
    {
        const string prefix = "/ws";

        arg = string.Empty;

        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // /ws 后面必须是结尾或空白：/wsfoo 不算命令
        if (text.Length > prefix.Length && !char.IsWhiteSpace(text[prefix.Length]))
            return false;

        arg = text[prefix.Length..].Trim();
        return true;
    }

    /// <summary>解析本地 <c>/plugin</c> 命令（整词匹配，理由同 <see cref="TryParseWsCommand"/>）。</summary>
    private static bool TryParsePluginCommand(string text, out string arg)
    {
        const string prefix = "/plugin";

        arg = string.Empty;

        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (text.Length > prefix.Length && !char.IsWhiteSpace(text[prefix.Length]))
            return false;

        arg = text[prefix.Length..].Trim();
        return true;
    }

    /// <summary>
    /// <c>/plugin</c>：打印插件清单。
    /// </summary>
    /// <remarks>
    /// 默认不打印 Info 级诊断：其中一条是「程序目录下的 plugins 不存在」，
    /// 而绝大多数用户本来就没有内置插件 —— 每次进来都提醒一遍，
    /// 会让人习惯性忽略整个诊断区，包括那些真正重要的错误。
    /// </remarks>
    private static void PrintPlugins(PluginHost host)
    {
        Console.WriteLine();

        if (host.Manifests.Count == 0)
        {
            Console.WriteLine("  还没有插件。放一个 plugins/<名字>/plugin.json 进去即可（见 docs/Plugins.md）。");
        }
        else
        {
            foreach (var m in host.Manifests)
            {
                Console.WriteLine($"  {m.Id,-16} {m.Name} v{m.Version}" + (m.BuiltIn ? "  [内置]" : string.Empty));

                foreach (var t in m.Tools)
                    Console.WriteLine($"      {t.Name,-22} {t.RiskLabel}");
            }
        }

        var notable = host.Diagnostics
            .Where(d => d.Level != PluginDiagnosticLevel.Info)
            .ToList();

        if (notable.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  诊断：");

            foreach (var d in notable)
                Console.WriteLine("    " + d);
        }

        Console.WriteLine();
        Console.WriteLine("  目录：");

        foreach (var d in host.Directories)
            Console.WriteLine($"    {(d.BuiltIn ? "内置" : "用户")}  {d.Path}");

        Console.WriteLine();
    }

    /// <summary><c>/plugin info</c>：插件或工具详情。</summary>
    private static void PrintPluginDetail(PluginHost host, string key)
    {
        var plugin = host.FindPlugin(key);

        if (plugin is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  {plugin.Name}（{plugin.Id} v{plugin.Version}）" +
                              (plugin.BuiltIn ? " [内置]" : string.Empty));
            Console.WriteLine("  " + plugin.Description);
            Console.WriteLine("  目录：" + plugin.DirectoryPath);

            if (plugin.Requires.Count > 0)
                Console.WriteLine("  需要：" + string.Join(", ", plugin.Requires));

            Console.WriteLine();

            foreach (var t in plugin.Tools)
            {
                Console.WriteLine($"    {t.Name,-22} {t.RiskLabel}");
                Console.WriteLine("        " + t.Run.Describe());
            }

            Console.WriteLine();
            return;
        }

        var tool = host.FindTool(key);

        if (tool is not null)
        {
            var spec = tool.Spec;

            Console.WriteLine();
            Console.WriteLine($"  {spec.Name}（插件 {tool.PluginId}）");
            Console.WriteLine("  " + spec.Description);
            Console.WriteLine("  风险：" + spec.RiskLabel);
            Console.WriteLine("  执行：" + spec.Run.Describe());
            Console.WriteLine("  参数：" + (spec.ParameterNames.Count == 0
                ? "（无）"
                : string.Join(", ", spec.ParameterNames)));

            if (spec.RequiredParameters.Count > 0)
                Console.WriteLine("  必填：" + string.Join(", ", spec.RequiredParameters));

            Console.WriteLine();
            return;
        }

        Console.WriteLine($"  没有找到插件或工具 '{key}'");
        Console.WriteLine();
    }

    /// <summary>取描述的第一句（<c>/tools</c> 列表里一行显示不下整段说明）。</summary>
    private static string FirstLine(string text)
    {
        // 手写循环而不是 IndexOfAny：
        // 后者每调用一次都要新建一个 char[]，分析器会提示用 static readonly 或 SearchValues；
        // 而这里的数据量最多几十个字符，一个 for 循环既没有分配也没有歧义。
        var end = -1;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '\n' or '。')
            {
                end = i;
                break;
            }
        }

        var line = end > 0 ? text[..(end + 1)] : text;
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    private static void WriteWarn(string msg)    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(msg);
        Console.ResetColor();
    }

    /// <summary>把 AgentLoop 的事件打到控制台。</summary>
    private sealed class ConsoleObserver : IAgentObserver
    {
        private readonly bool _autoApprove;

        public ConsoleObserver(bool autoApprove) => _autoApprove = autoApprove;

        public void OnTextDelta(string text)
        {
            Console.Write(text);
        }

        public void OnRoundBegin(int round)
        {
            if (round > 0)
                Console.WriteLine();
        }

        /// <summary>
        /// 状态行。核验相关的提示（送审中 / 裁决 / 打回）都从这里出来。
        ///
        /// ★ 用暗紫色而不是常规色：这些是**审查员**说的话，不是肥鱼说的。
        ///   混在同一片白色输出里，用户会分不清「谁在拒绝我」——
        ///   而这两者给人的感受完全不同：被自己拒绝 vs 被另一个 AI 拒绝。
        /// </summary>
        public void OnStatus(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.WriteLine("  " + text);
            Console.ResetColor();
        }

        public void OnToolCallStarted(string toolName, System.Text.Json.JsonElement args)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write($"  ⚙ {toolName} ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(args.GetRawText().Length > 120
                ? args.GetRawText()[..120] + "…"
                : args.GetRawText());
            Console.ResetColor();
        }

        public void OnToolCallFinished(string toolName, ToolResult result)
        {
            Console.ForegroundColor = result.Ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(result.Ok ? "    ✔ " : "    ✘ ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(result.Ok
                ? $"{result.ElapsedMs}ms"
                : result.Error);
            Console.ResetColor();
        }

        public ValueTask<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
        {
            if (_autoApprove)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    ⚠ 自动放行：{request.Tool}（--allow）");
                Console.ResetColor();
                return ValueTask.FromResult(true);
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠ 需要确认：{request.Tool}");
            Console.WriteLine($"    原因：{request.Reason}");

            foreach (var (k, v) in request.Args)
            {
                var raw = v.GetRawText();
                Console.WriteLine($"    {k} = {(raw.Length > 160 ? raw[..160] + "…" : raw)}");
            }

            Console.Write("    允许吗？[y/N] ");
            Console.ResetColor();

            var answer = Console.ReadLine();

            return ValueTask.FromResult(
                answer is not null && answer.Trim().ToLowerInvariant() is "y" or "yes");
        }
    }
}

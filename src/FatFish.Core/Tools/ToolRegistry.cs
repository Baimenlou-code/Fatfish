using System.Diagnostics;
using System.Text.Json;
using FatFish.Core.Safety;

namespace FatFish.Core.Tools;

/// <summary>
/// ToolRegistry —— 工具注册表。
///
/// 职责：
///   1. 持有全部工具实现，提供按名查找；
///   2. 生成 OpenAI 兼容的 tools 数组（对应 Python 版 TOOL_SCHEMAS）；
///   3. 统一执行入口：解析参数 → 审批判定 → 执行 → 计时 → 统一异常兜底。
///
/// ★ 统一执行入口的意义：任何工具都不可能「忘记判审批」或「漏 try/catch」，
///   因为工具自身拿不到执行权，只能被 Registry 调用。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.Ordinal);

    /// <summary>
    /// 可选的动态工具源（插件）。
    /// </summary>
    /// <remarks>
    /// ★ 为什么是「每次问源」而不是「装载时塞进字典」：
    ///   插件支持热重载，而这个字典是构造时固定、没有并发保护的。
    ///   若装载时往里写，就必须给每次工具查找加锁，还要处理
    ///   「一个工具正在执行时被替换」—— 两者都会带来难以复现的竞态。
    ///   改成「注册表只读 + 源内部持有一个不可变快照」之后，两边都不需要锁：
    ///   读到旧快照的线程拿到的是一份自洽的旧数据，不会看到半新半旧。
    ///
    ///   代价是 <see cref="Find"/> 多一次线性查找。工具总数在几十个量级，
    ///   而它每轮对话只被调用几十次 —— 相比一次 LLM 请求的四位数毫秒，
    ///   这点开销可以忽略。
    /// </remarks>
    private readonly IToolSource? _source;

    public ToolRegistry(IEnumerable<ITool> tools, IToolSource? source = null)
    {
        foreach (var tool in tools)
        {
            if (!_tools.TryAdd(tool.Name, tool))
                throw new InvalidOperationException($"工具名重复注册：{tool.Name}");
        }

        _source = source;
    }

    /// <summary>当前动态工具（无插件时为空）。</summary>
    private IReadOnlyList<ITool> Extra => _source?.Snapshot() ?? Array.Empty<ITool>();

    public IReadOnlyCollection<ITool> All => _tools.Values.Concat(Extra).ToList();

    public ITool? Find(string name)
    {
        if (_tools.TryGetValue(name, out var direct))
            return direct;

        return Extra.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
    }

    public bool Contains(string name)
        => _tools.ContainsKey(name)
           || Extra.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    public int Count => _tools.Count + Extra.Count;

    /// <summary>
    /// 生成 OpenAI / DeepSeek 兼容的 tools 数组。
    /// 输出形状：
    ///   [ { "type": "function", "function": { "name": ..., "description": ..., "parameters": {...} } } ]
    /// </summary>
    public JsonElement BuildToolSchema()
    {
        // 只取一次快照：拼 schema 时若逐项访问 All，每访问一次都会重新问源，
        // 极端情况下（正好赶上重载）会出现「长度按旧快照算、内容按新快照取」的错配。
        var all = All;
        var list = new List<Dictionary<string, object>>(all.Count);

        foreach (var tool in all.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            list.Add(new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.ParametersSchema,
                },
            });
        }

        return JsonSerializer.SerializeToElement(list, ToolJson.DefaultOptions);
    }

    /// <summary>
    /// 统一执行入口。
    /// </summary>
    /// <remarks>
    /// 审批不在本方法内触发 —— 审批属于**编排层**（AgentLoop）的职责，
    /// 因为只有它知道「本轮是否已一键放行」。本方法只负责：
    ///   查表 → 参数校验 → 执行 → 计时 → 异常兜底。
    /// </remarks>
    public async ValueTask<ToolResult> InvokeAsync(
        string name,
        ToolContext context,
        JsonElement args,
        CancellationToken ct = default)
    {
        // ★ 必须走 Find，不能直接查 _tools：插件工具只活在动态源里。
        //   这里漏过一次，症状极有迷惑性 —— 工具能被 ws_plugin_list 列出、
        //   能被 build 进 schema 发给模型，模型也真的调了，但一执行就「未知工具」。
        //   因为「列得出来」这一点，第一反应会去怀疑插件装载，而不是分发入口。
        var tool = Find(name);

        if (tool is null)
            return ToolResult.Failure($"未知工具：{name}");

        if (args.ValueKind != JsonValueKind.Object)
            return ToolResult.Failure($"参数必须是 JSON 对象，实际是 {args.ValueKind}");

        var sw = Stopwatch.StartNew();

        try
        {
            var result = await tool.ExecuteAsync(context, args, ct).ConfigureAwait(false);
            return result with { ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Failure($"已取消（耗时 {sw.ElapsedMilliseconds}ms）")
                with { ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (PathEscapeException ex)
        {
            return ToolResult.Failure(ex.Message) with { ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (UnauthorizedAccessException ex)
        {
            return ToolResult.Failure($"权限不足：{ex.Message}") with { ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (FileNotFoundException ex)
        {
            return ToolResult.Failure($"文件不存在：{ex.FileName ?? ex.Message}")
                with { ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (DirectoryNotFoundException ex)
        {
            return ToolResult.Failure($"目录不存在：{ex.Message}") with { ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (IOException ex)
        {
            return ToolResult.Failure($"IO 错误：{ex.Message}") with { ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            // 兜底：绝不让工具异常穿透到 AgentLoop，否则整轮对话会中断
            return ToolResult.Failure($"{ex.GetType().Name}: {ex.Message}")
                with { ElapsedMs = sw.ElapsedMilliseconds };
        }
    }
}

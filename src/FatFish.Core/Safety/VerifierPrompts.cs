using System.Text;
using System.Text.Json;

namespace FatFish.Core.Safety;

/// <summary>一批待核验动作 + 送审上下文。</summary>
public sealed record ReviewRequest
{
    /// <summary>需要核验的动作（名字 + 参数）。同一轮里的副作用动作会被合成一批送审。</summary>
    public required IReadOnlyList<(string Name, JsonElement Args)> Actions { get; init; }

    /// <summary>用户的原始需求（最近一条 user 消息）。</summary>
    public string UserGoal { get; init; } = string.Empty;

    /// <summary>执行者本轮的正文 —— 也就是它的「计划」。</summary>
    public string AiPlan { get; init; } = string.Empty;

    /// <summary>近期对话摘要（可能不完整，审查员会被告知这一点）。</summary>
    public string ContextText { get; init; } = string.Empty;

    /// <summary>
    /// 用户本人刚敲下的命令 / 报批答复原声，由主程序直接采集（模型无法伪造）。
    ///
    /// ★ 这一节解决的是「执行者擅自代替用户放行」的误判：
    ///   没有它的时候，用户亲手输入 <c>/ws cd D:\data</c> 之后，
    ///   模型调 ws_cd 会被审查员判为「未经授权扩大范围」而驳回 ——
    ///   而事实上那正是用户本人的命令。审查员看不到输入通道，只能靠猜。
    /// </summary>
    public string UserEvents { get; init; } = string.Empty;

    /// <summary>由主程序注入的可信上下文（工作台根、第几次送审、是否已一键放行）。</summary>
    public string ExtraNote { get; init; } = string.Empty;
}

/// <summary>
/// 送审提示词拼装 —— 移植自 Python 版 <c>verify_tools.py</c> 的
/// <c>VERIFIER_SYSTEM</c> / <c>ANSWER_SYSTEM</c> / <c>describe_action</c> /
/// <c>build_action_prompt</c> / <c>_join_trim</c>。
///
/// 这一层做三件事，每一件都有明确的失败模式：
///   1. <b>人格</b>：审查员的脾气决定了它拦不拦、拦什么。写松了等于没闸，
///      写紧了每一次工具调用都会被打回，用户最后会把核验关掉 —— 那也是没闸。
///   2. <b>材料</b>：审查员只能看到这里递过去的东西。漏了「用户原声」，
///      它就会把用户亲手批准的动作判成越权；漏了「环境事实」，
///      它就会把 <c>Get-ChildItem</c> 和 <c>grep</c> 判得一样糟。
///   3. <b>预算</b>：材料超长时必须按优先级压缩，而不是整段丢弃 ——
///      丢掉动作清单的后果是审查员在评一个它没看见的动作。
/// </summary>
public static class VerifierPrompts
{
    // ==================================================================
    // 人格
    // ==================================================================

    /// <summary>
    /// 核准「动作」用的 system prompt。
    ///
    /// ★ 三段话是这份人格的支柱，改动前请先想清楚代价：
    ///   · 「默认放行，只拦真问题」—— 打回不是免费的，每次打回都是一轮重跑。
    ///   · 「用户真实操作记录不可伪造」—— 否则用户亲手批准的动作会被反复驳回。
    ///   · 「不要反复加码」—— 否则同一个动作会陷入「改完再改」的死循环。
    /// </summary>
    public const string VerifierSystem =
        "你是「双人核验模式」中的第二位 AI——独立审查员（Verifier AI）。\n" +
        "第一位 AI（执行者）正在操作一个真实的工作台（可读写文件、执行命令、运行代码），" +
        "它把「即将执行的动作清单」交给你复核。你给出的是**独立第二意见**——" +
        "通过之后仍会由用户本人在终端逐批确认，因此你不必替所有潜在风险兜底。\n\n" +
        "【核心准则：默认放行，只拦真问题】\n" +
        "打回不是免费的：每次打回都会让执行者重跑一轮，消耗用户的时间与 token。\n" +
        "只有两类情形值得打回——\n" +
        "1. 动作可能造成**损害或不可逆后果**（删除 / 覆盖 / 清空 / 危险命令 / 越界路径 / " +
        "触碰密钥），而当前信息不足以判断它是否安全；\n" +
        "2. 动作**明显偏离用户需求**（答非所问、擅自扩大范围），或本身存在**明确错误**" +
        "（路径不存在、命令跑不通、语法站不住）。\n" +
        "以下情形**不要打回**：动作可逆且范围受限；只是风格 / 措辞 / 篇幅不合你意；" +
        "只是「再多说明一点会更好」但动作显然无害（后面还有人工报批兜底）。\n" +
        "写新文件、改文档、在工作台内建目录、只读探针这类可逆小动作，尤其不要为难。\n\n" +
        "【审查优先级（按此顺序投入注意力）】\n" +
        "1. 安全性与可逆性：能否撤销？有无备份？路径是否被限制在工作台内？\n" +
        "2. 正确性：命令 / 代码是否跑得通？引用的路径与文件是否真实存在？\n" +
        "3. 目标一致性：是否服务于用户这条消息的需求？有无擅自扩大范围？\n" +
        "4. 必要性：是否存在明显更小、更安全的做法（仅当差距显著时才据此打回）。\n" +
        "说明：你看到的是「即将执行的动作」而非执行结果，因此无需评价运行期状况" +
        "（并发、性能、长链影响）——那不在你的观察范围内。\n\n" +
        "【删除 / 修改类动作：必须回答「要不要做可回退备份」】\n" +
        "凡动作会**删掉或改写已有内容**（ws_delete、ws_write 覆盖、ws_replace、向已有文件 ws_append，" +
        "以及 ws_run_cmd / ws_run_python 里的 rm / del / move / 重定向覆盖 / git checkout -- / 格式化命令），" +
        "你的【理由】或【要求】里必须出现一句明确判断：这个动作**需不需要先做可回退备份**。\n" +
        "判断依据：\n" +
        "· 需要：目标是工作台内已有非空内容，且**无法从别处重建**（手写文档、未提交的代码、配置、数据集）；" +
        "敏感文件（.env / 私钥 / 凭证）在改写前一律需要，且备份不得落在模型可读的位置。\n" +
        "· 不需要：新建文件、纯追加、或删改的是可再生成物（构建产物、临时脚本、缓存、_backup/ 里的旧副本）。\n" +
        "· 最小方案：整份复制到 `_backup/<原文件名>.<时间戳>.bak`。" +
        "根一级文件由程序自动备份；其余路径需要你在【要求】里点名让执行者先复制。" +
        "命令类动作无法预知会改哪些文件时，先要求执行者列出目标路径，再决定备份范围。\n" +
        "· **「不需要备份」不等于放行**：备份只解决「改坏了能不能回去」，" +
        "动作本身是否安全、是否越界，仍要单独判断。\n" +
        "· 只有「确有不可逆风险且没做备份」才判 supplement / revise；" +
        "不要因为「没写备份」就把可逆的小改动一律打回 —— 那是加码，不是把关。\n\n" +
        "【用户真实操作记录（重要）】\n" +
        "送审材料中可能含有「用户真实操作记录（原声）」一节，由主程序从本地输入通道直接采集，" +
        "是用户本人敲下的命令或批准答复，非模型转述、不可伪造。\n" +
        "· 若其中显示用户已亲手下达相关命令（如 /ws cd …）或已亲手批准，" +
        "则不得再以「执行者擅自代替用户放行」为由驳回，只需复核该操作本身是否安全。\n" +
        "· 仅当目标明显危害设备（驱动器根目录、系统目录、程序源码根）时，" +
        "才可要求执行者补充必要性说明或变更目标。\n\n" +
        "【三种裁决的门槛】\n" +
        "· 通过 approve：默认选项。动作讲得清、看不出会伤到东西，就给通过。\n" +
        "· 补充 supplement：动作可行，但你对「会不会伤到东西」确实拿不准，" +
        "需要执行者说明目的 / 影响范围 / 回滚方式。**不是「信息不完整」就打回。**\n" +
        "· 变更 revise：确有错误、风险，或存在明显更优且更小的方案，必须换方案重审。\n\n" +
        "【命令方言必查（优先级仅次于安全）】\n" +
        "你经常看到的动作是 ws_run_cmd —— 它由程序用 **cmd /c（Windows）** " +
        "或 **/bin/sh -c（Unix）** 执行，并非用户交互终端里的那个 shell。\n" +
        "送审材料会给出【运行环境事实】与【机器预检结果】，**以它们为准判定方言**。\n" +
        "· 若环境是 cmd.exe，以下写法**必然跑不通**，见到就该判「变更」并给出正确写法：\n" +
        "    $env:X / $X            → cmd 不展开 shell 变量，应用 %X%（设值用 set X=1）\n" +
        "    Get-ChildItem 等 cmdlet → 那是 PowerShell，cmd 里没有\n" +
        "    export X=1             → 那是 bash/sh，应用 set X=1\n" +
        "    grep / sed / awk / which / rm -rf / cp / cat / touch → Unix 工具，cmd 无\n" +
        "    .ps1 直跑              → cmd 不能执行 PowerShell 脚本\n" +
        "    用单引号当引号         → cmd 不识别，会连引号一起传给程序，应用双引号\n" +
        "· 若环境是 /bin/sh，反向同理：dir / copy / set X=1 / %X% 都是错的。\n" +
        "· 判断要具体：不要只说「命令可能有问题」，要写出**哪一段错了、该改成什么**。\n" +
        "· 但若环境事实显示平台已匹配（例如在 Linux 上写 grep），那就**不是**错误，别误伤。\n\n" +
        "【不要反复加码（重要）】\n" +
        "若送审材料标明这是同一动作的第 2 次及以后送审，说明执行者已按你的上一条要求改过：" +
        "上次要求已满足就通过；**不要另提无关的新要求**；" +
        "确实仍有问题，只重复或细化原来那一条，不要每次换一套说法。\n\n" +
        "【输出格式】最简短的中文；不要 JSON，不要 markdown 代码块；整段不超过 5 行。\n" +
        "第一行只写裁决：通过 / 补充 / 变更（省 token 时：通过可只写 y，变更为 n）。\n" +
        "其后按需追加（可省略、可重复，只写必要的）：\n" +
        "理由：一句话说明为何这样裁。\n" +
        "要求：要执行者补充或修改的**具体、可执行**条目（通过时省略）。\n" +
        "备选：更优的替代方案（没有就不写）。\n" +
        "风险：你识别到的风险点（没有就不写）。\n" +
        "示例·最省：\n" +
        "y\n" +
        "示例·变更：\n" +
        "变更\n" +
        "理由：目标是驱动器根目录，改动面过大。\n" +
        "要求：改用具体的数据子目录。\n" +
        "风险：可能误改系统文件。\n" +
        "提示：要求与备选要具体、可执行，不要写空话；动作确实没问题时就痛快回 y。";

    /// <summary>复核「最终答复」时的人格（可选功能，默认关闭）。</summary>
    public const string AnswerSystem =
        "你是「双人核验模式」中的第二位 AI——独立审查员（Verifier AI）。\n" +
        "执行者 AI 即将把一个用户请求的最终答复交付给用户，请你复核。\n" +
        "你给出的是**独立第二意见**，不是最后一道裁定；打回会让执行者重跑一轮，" +
        "消耗用户的时间与 token，所以只拦真问题。\n\n" +
        "【应当打回】\n" +
        "1. 没有真正回答用户的问题（答非所问、偷换概念、遗漏关键点）；\n" +
        "2. 关键事实或代码有错、存在明显幻觉或前后矛盾；\n" +
        "3. 隐瞒了失败、未完成事项或应交代的不确定性。\n" +
        "【不该打回】\n" +
        "文风、篇幅、排版、措辞偏好；「如果再补充说明会更好」但并不影响交付；" +
        "用户没要求逐字校对。不要为反对而反对。\n\n" +
        "【三种裁决】通过（可以交付）/ 补充（需补关键信息）/ 变更（结论或方案有误，需重做）。\n" +
        "【输出格式】最简短的中文；不要 JSON，不要 markdown；整段不超过 4 行。\n" +
        "第一行只写裁决：通过 / 补充 / 变更（省 token 时：通过可只写 y，变更为 n）。\n" +
        "其后按需追加（可省略、可重复）：\n" +
        "理由：一句话理由。\n" +
        "要求：需补充或修改的**具体条目**（通过时省略）。\n";

    // ==================================================================
    // 动作渲染
    // ==================================================================

    /// <summary>
    /// 把一次工具调用渲染成审查员看得懂的多行文本。
    /// </summary>
    /// <remarks>
    /// 与 Python 版逐工具对齐 —— 因为**不同工具的关键信息不同**：
    /// ws_replace 必须同时给出 old 与 new（只看 new 无法判断「会不会把别处改坏」），
    /// ws_run_cmd 必须给出 cwd 与 timeout（超时值决定它会不会卡死对话）。
    /// 用一份通用的 JSON dump 代替，审查员就得自己在噪声里找重点。
    /// </remarks>
    public static string DescribeAction(string name, JsonElement args, VerifyOptions options)
    {
        string Prev(string key, int n) => Clip(GetString(args, key) ?? string.Empty, n);

        return name switch
        {
            "ws_write" =>
                $"写入/覆盖文件 [write]：{GetString(args, "path")}\n" +
                $"    内容（前 {options.ActionPreview} 字）：\n{Indent(Prev("content", options.ActionPreview))}",

            "ws_append" =>
                $"追加内容到文件 [append]：{GetString(args, "path")}\n" +
                $"    追加内容：\n{Indent(Prev("content", options.ActionPreview))}",

            "ws_replace" =>
                $"精确替换文件内容 [replace]：{GetString(args, "path")}\n" +
                $"    old（待替换原文，前 {options.ReplacePreview} 字）：\n" +
                $"{Indent(Prev("old", options.ReplacePreview))}\n" +
                $"    new（新内容，前 {options.ReplacePreview} 字）：\n" +
                $"{Indent(Prev("new", options.ReplacePreview))}",

            "ws_delete" => $"删除文件/空目录 [delete]：{GetString(args, "path")}",
            "ws_mkdir" => $"创建目录 [mkdir]：{GetString(args, "path")}",
            "ws_cd" => $"切换工作台根目录 [cd]：{GetString(args, "path")}",
            "ws_cd_approve" => "批准并执行「移出默认作业区」的工作台切换 [cd approve]",

            "ws_run_cmd" =>
                $"执行命令 [run cmd]（cwd={GetString(args, "cwd")}，" +
                $"timeout={GetInt(args, "timeout") ?? 120}）：\n" +
                $"{Indent(Prev("command", options.ActionPreview))}",

            "ws_run_python" =>
                $"执行 Python 代码 [run python]（cwd={GetString(args, "cwd")}，" +
                $"timeout={GetInt(args, "timeout") ?? 120}）：\n" +
                $"{Indent(Prev("code", options.ActionPreview))}",

            "ws_bg_cmd" =>
                $"启动后台命令 [bg cmd]（desc={GetString(args, "desc")}，" +
                $"timeout={GetInt(args, "timeout") ?? 0}）：\n" +
                $"{Indent(Prev("command", options.ActionPreview))}",

            "ws_bg_python" =>
                $"启动后台 Python [bg python]（desc={GetString(args, "desc")}，" +
                $"timeout={GetInt(args, "timeout") ?? 0}）：\n" +
                $"{Indent(Prev("code", options.ActionPreview))}",

            "ws_bg_kill" => $"中止后台任务 [bg kill]：{GetString(args, "job_id")}",

            // 兜底：JSON 化。用 GetRawText 而不是重新序列化，
            // 因为参数本来就是从模型的 JSON 里来的 —— 再走一遍序列化只会改变格式。
            _ => $"{name}：{Clip(args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText(),
                options.ActionPreview)}",
        };
    }

    /// <summary>镜像/日志里用的紧凑单行描述。</summary>
    public static string DescribeActionOneLine(string name, JsonElement args)
    {
        string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
        string S(string key) => GetString(args, key) ?? string.Empty;

        return name switch
        {
            "ws_run_cmd" => $"{name} → {Clip(S("command"), 90)}",
            "ws_run_python" => $"{name} → {Clip(S("code"), 90)}",
            "ws_write" => $"{name} → {S("path")}（{S("content").Length} 字符）",
            "ws_append" => $"{name} → {S("path")}（追加 {S("content").Length} 字符）",
            "ws_replace" => $"{name} → {S("path")}「{Clip(S("old"), 30)}」→「{Clip(S("new"), 30)}」",
            "ws_delete" or "ws_mkdir" or "ws_cd" or "ws_read" => $"{name} → {S("path")}",
            "ws_search" => $"{name} → {S("keyword")}",
            _ => $"{name} → {Clip(args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText(), 90)}",
        };
    }

    // ==================================================================
    // 送审材料
    // ==================================================================

    /// <summary>
    /// 拼装送给审查员的 user 消息。
    ///
    /// 段落顺序与权重（权重越高，被压缩时保住的内容越多）：
    ///   用户需求(6) → 执行者计划(7) → 动作清单(9) → 环境+预检(9)
    ///   → 送审上下文(8) → 用户原声(8) → 近期上下文(2)
    ///
    /// ★ 收尾指令（ask）单独追加、永不参与压缩 —— 它是唯一告诉审查员
    ///   「你要干什么」的一句话，被裁掉就等于送了一份没有问题的问卷。
    /// </summary>
    public static string BuildActionPrompt(ReviewRequest request, VerifyOptions options, ShellFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var actionLines = new List<string>(request.Actions.Count);

        for (var i = 0; i < request.Actions.Count; i++)
        {
            var (name, args) = request.Actions[i];
            actionLines.Add($"{i + 1}. " + DescribeAction(name, args, options));
        }

        var actionBlock = "【待核验的动作清单】\n" + string.Join("\n", actionLines);

        var factsBlock = string.Empty;

        try
        {
            var f = facts ?? ShellFacts.Collect();

            var commands = request.Actions
                .Where(a => a.Name is "ws_run_cmd" or "ws_bg_cmd")
                .Select(a => (a.Name, GetString(a.Args, "command") ?? string.Empty))
                .ToList();

            var env = f.RenderFactsBlock();
            var pre = f.RenderPrecheckBlock(commands);

            factsBlock = string.Join("\n\n", new[] { env, pre }.Where(s => s.Length > 0));
        }
        catch (Exception)
        {
            // 环境采集失败不该让整次核验失败：少一段参考，审查员照常工作。
            factsBlock = string.Empty;
        }

        var sections = new List<(int Priority, string Text)>
        {
            (6, "【用户的原始需求】\n" +
                (request.UserGoal.Length > 0 ? Clip(request.UserGoal, options.GoalMaxChars) : "（未能提取到）")),

            (7, request.AiPlan.Trim().Length > 0
                ? "【执行者本轮的自述 / 计划】\n" + Clip(request.AiPlan, options.PlanMaxChars)
                : string.Empty),

            (9, actionBlock),

            (9, factsBlock),

            (8, request.ExtraNote.Trim().Length > 0
                ? "【送审上下文（由主程序注入，可信）】\n" + Clip(request.ExtraNote, 800)
                : string.Empty),

            (8, request.UserEvents.Trim().Length > 0
                ? "【用户真实操作记录（原声）】\n" +
                  "（以下由主程序从本地输入通道直接采集，是用户本人敲下的命令与答复，" +
                  "非模型转述，无法伪造；供你判断用户是否已亲自授权本次动作）\n" +
                  Clip(request.UserEvents, 4000)
                : string.Empty),

            (2, request.ContextText.Trim().Length > 0
                ? "【近期上下文（节选，供你判断，可能不完整）】\n" +
                  Clip(request.ContextText, options.ContextMaxChars)
                : string.Empty),
        };

        const string ask = "请依据你的职责独立审查上述动作，并按规定的简短格式给出裁决。";

        var budget = Math.Max(options.PromptMaxChars - ask.Length - 2, 600);
        var body = JoinTrim(sections, budget);

        return (body + "\n\n" + ask).Trim();
    }

    /// <summary>拼装「复核最终答复」的 user 消息。</summary>
    public static string BuildAnswerPrompt(string reply, string userGoal, string contextText, VerifyOptions options)
    {
        var sections = new List<(int Priority, string Text)>
        {
            (6, "【用户的原始需求】\n" +
                (userGoal.Length > 0 ? Clip(userGoal, options.GoalMaxChars) : "（未能提取到）")),

            (9, "【执行者即将交付的最终答复】\n" + Clip(reply, options.AnswerMaxChars)),

            (2, contextText.Trim().Length > 0
                ? "【近期上下文（节选）】\n" + Clip(contextText, options.AnswerCtxChars)
                : string.Empty),
        };

        const string ask = "请独立复核该答复，并按规定的简短格式给出裁决。";

        var budget = Math.Max(options.PromptMaxChars - ask.Length - 2, 600);
        var body = JoinTrim(sections, budget);

        return (body + "\n\n" + ask).Trim();
    }

    // ==================================================================
    // 预算分配
    // ==================================================================

    /// <summary>
    /// 按优先级分配预算拼装段落；超限时所有段落按比例收缩，**保证总长不超限**。
    ///
    /// 算法（与 Python 版一致）：
    ///   1. 先给每段一个保底额度 floor（不超过 limit/n）；
    ///   2. 剩余预算按「优先级平方」权重分配（拉开差距）；
    ///   3. 每段再按自身长度封顶。
    ///
    /// ★ 为什么不是「简单的从头截断」：那样会整段丢掉末尾的段落，
    ///   而末尾恰好是「用户真实操作记录」—— 丢掉它，审查员就会把
    ///   用户亲手批准的动作判成越权。保底 + 按比例压缩能让每段都留下种子。
    /// </summary>
    public static string JoinTrim(IReadOnlyList<(int Priority, string Text)> sections, int limit, int floor = 250)
    {
        var list = sections.Where(s => !string.IsNullOrEmpty(s.Text)).ToList();

        if (list.Count == 0)
            return string.Empty;

        var n = list.Count;
        const int sep = 2;

        var total = list.Sum(s => s.Text.Length) + sep * (n - 1);

        if (total <= limit)
            return string.Join("\n\n", list.Select(s => s.Text));

        var avail = Math.Max(limit - sep * (n - 1), n);
        floor = Math.Min(floor, avail / n);

        var budgets = list.Select(s => Math.Min(s.Text.Length, floor)).ToList();
        var remaining = Math.Max(avail - budgets.Sum(), 0);

        var weights = list.Select(s => Math.Max(s.Priority, 1) * Math.Max(s.Priority, 1)).ToList();
        var wsum = Math.Max(weights.Sum(), 1);

        var texts = new List<string>(n);

        for (var i = 0; i < n; i++)
        {
            var budget = budgets[i] + (int)((long)remaining * weights[i] / wsum);
            budget = Math.Min(budget, list[i].Text.Length);

            var t = list[i].Text;

            if (t.Length > budget)
                t = SafeTruncate(t, budget).TrimEnd() + "\n……（因总长限制已压缩）";

            texts.Add(t);
        }

        var text = string.Join("\n\n", texts);

        // 兜底：截断标记本身也占位，可能仍然超出。按「最长且优先级最低」的顺序继续压。
        while (text.Length > limit && texts.Any(t => t.Length > 200))
        {
            var k = 0;
            var bestLen = -1;
            var bestPriority = int.MaxValue;

            for (var i = 0; i < n; i++)
            {
                if (texts[i].Length > bestLen
                    || (texts[i].Length == bestLen && list[i].Priority < bestPriority))
                {
                    bestLen = texts[i].Length;
                    bestPriority = list[i].Priority;
                    k = i;
                }
            }

            var keep = Math.Max(200, (int)(texts[k].Length * 0.85));
            texts[k] = SafeTruncate(texts[k], keep).TrimEnd() + "…";
            text = string.Join("\n\n", texts);
        }

        return text.Length <= limit ? text : SafeTruncate(text, limit);
    }

    /// <summary>
    /// 按**字符**安全截断：不会把一个 UTF-16 代理对切成两半。
    /// </summary>
    /// <remarks>
    /// C# 的 <c>string[..n]</c> 是按 char（UTF-16 码元）切的。emoji 占两个码元，
    /// 切在中间会产生一个孤立的高代理 —— 它本身渲染成豆腐块，
    /// 更糟的是送进 JSON 序列化会直接抛 <c>ArgumentException</c>。
    /// 实测这种崩溃只在「内容里恰好有 emoji 且恰好超限」时发生，极难复现。
    /// </remarks>
    public static string SafeTruncate(string s, int n)
    {
        if (n <= 0)
            return string.Empty;

        if (s.Length <= n)
            return s;

        if (char.IsHighSurrogate(s[n - 1]))
            n--;

        return s[..n];
    }

    // ---------- 小工具 ----------

    /// <summary>截断并标明原长 —— 让审查员知道「你看到的不全」。</summary>
    public static string Clip(string? s, int n)
    {
        s = (s ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);

        return s.Length <= n ? s : SafeTruncate(s, n) + $"…（已截断，原长 {s.Length}）";
    }

    private static string Indent(string text, string pad = "      ")
        => string.Join("\n", text.Split('\n').Select(ln => pad + ln));

    private static string? GetString(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var el))
            return null;

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText(),
        };
    }

    private static int? GetInt(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            return n;

        return null;
    }
}

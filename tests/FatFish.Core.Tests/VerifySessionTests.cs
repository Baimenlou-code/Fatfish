using FatFish.Core.Safety;
using Xunit;

namespace FatFish.Core.Tests;

/// <summary>
/// 核验记账与决策测试。
///
/// 这是整条链上**唯一有状态**的部分：打回几次、补充几轮、超限后拦还是放。
/// 写错的后果只有两种，都很糟：
///   · 太松 → 无限循环，模型永远在被打回，用户只能关掉核验；
///   · 太紧 → 一次就打回到底，正常的「补一句说明」也过不去。
/// 两种都不会报错，只会让用户觉得「这个功能没用」。
/// </summary>
public class VerifySessionTests
{
    private static VerifySession NewSession(
        int maxRetries = 2,
        int maxSupplements = 4,
        bool strict = true,
        VerifyFailMode failMode = VerifyFailMode.Open)
    {
        var options = new VerifyOptions
        {
            Mode = VerifyMode.Auto,
            Strict = strict,
            MaxRetries = maxRetries,
            MaxSupplements = maxSupplements,
            FailMode = failMode,
            Endpoint = new VerifierEndpoint { ApiKey = "test-key" },
        };

        var service = new VerifierService(options, (_, _, _) => Task.FromResult("y"));

        return new VerifySession(service);
    }

    private static VerifyOutcome Outcome(string verdict, bool failed = false, bool skipped = false)
        => new()
        {
            Verdict = new Verdict { Value = verdict, Reason = "测试用理由" },
            Failed = failed,
            Skipped = skipped,
        };

    // ==================================================================
    // 基本走向
    // ==================================================================

    [Fact]
    public void Approve_Proceeds()
    {
        var session = NewSession();

        var r = session.Decide(Outcome("approve"), 1);

        Assert.Equal(VerifyDecision.Proceed, r.Decision);
        Assert.Equal(0, session.Retries);
        Assert.Equal(0, session.Supplements);
    }

    [Fact]
    public void Skipped_Proceeds()
    {
        var session = NewSession();

        var r = session.Decide(Outcome("approve", skipped: true), 0);

        Assert.Equal(VerifyDecision.Proceed, r.Decision);
        Assert.Contains("跳过", r.Outcome.SummaryLine());
    }

    [Fact]
    public void Revise_SendsBackAndCountsRetry()
    {
        var session = NewSession();

        var r = session.Decide(Outcome("revise"), 1);

        Assert.Equal(VerifyDecision.SentBack, r.Decision);
        Assert.Equal(1, session.Retries);
        Assert.Equal(0, session.Supplements);
        Assert.Contains("第 1/2 次", r.Note);
        Assert.Contains("双人核验·未通过", r.Feedback);
    }

    // ==================================================================
    // supplement 的免费额度
    // ==================================================================

    /// <summary>
    /// supplement 连续 4 次都走免费额度，**不占用 revise 的重试次数**。
    ///
    /// 为什么这条很重要：如果 supplement 也算重试，「执行者老实承认信息不足」
    /// 就变成了一种惩罚 —— 它下次会倾向于编一段漂亮说明来换取通过，
    /// 而审查员被一段漂亮的谎言说服，恰恰是最危险的结局。
    /// </summary>
    [Fact]
    public void Supplement_UsesFreeQuota_WithoutConsumingRetries()
    {
        var session = NewSession(maxSupplements: 4, maxRetries: 2);

        for (var i = 1; i <= 4; i++)
        {
            var r = session.Decide(Outcome("supplement"), 1);

            Assert.Equal(VerifyDecision.SentBack, r.Decision);
            Assert.Equal(i, session.Supplements);
            Assert.Equal(0, session.Retries);
        }
    }

    [Fact]
    public void Supplement_AfterQuotaExhausted_ConsumesRetry()
    {
        var session = NewSession(maxSupplements: 2, maxRetries: 2);

        session.Decide(Outcome("supplement"), 1);   // suppl 1
        session.Decide(Outcome("supplement"), 1);   // suppl 2

        var r = session.Decide(Outcome("supplement"), 1);   // 额度用尽 → 计入 retry

        Assert.Equal(VerifyDecision.SentBack, r.Decision);
        Assert.Equal(2, session.Supplements);
        Assert.Equal(1, session.Retries);
        Assert.Contains("第 1/2 次", r.Note);
    }

    [Fact]
    public void Supplement_ZeroQuota_ImmediatelyCountsAsRetry()
    {
        var session = NewSession(maxSupplements: 0, maxRetries: 1);

        var r = session.Decide(Outcome("supplement"), 1);

        Assert.Equal(VerifyDecision.SentBack, r.Decision);
        Assert.Equal(0, session.Supplements);
        Assert.Equal(1, session.Retries);
    }

    // ==================================================================
    // 超限
    // ==================================================================

    [Fact]
    public void Strict_WhenLimitReached_Blocks()
    {
        var session = NewSession(maxRetries: 1, strict: true);

        session.Decide(Outcome("revise"), 1);               // retry 1

        var r = session.Decide(Outcome("revise"), 1);       // 超限

        Assert.Equal(VerifyDecision.Blocked, r.Decision);
        Assert.Contains("拦截", r.Note);
        Assert.Contains("已超出核验重试上限", r.Feedback);
        Assert.Contains("不要原样重试", r.Feedback);
    }

    [Fact]
    public void Lenient_WhenLimitReached_Proceeds()
    {
        var session = NewSession(maxRetries: 1, strict: false);

        session.Decide(Outcome("revise"), 1);

        var r = session.Decide(Outcome("revise"), 1);

        Assert.Equal(VerifyDecision.Proceed, r.Decision);
        Assert.Contains("宽松", r.Note);
    }

    [Fact]
    public void MaxRetriesZero_FirstReviseIsAlreadyAtLimit()
    {
        var session = NewSession(maxRetries: 0, strict: true, maxSupplements: 0);

        var r = session.Decide(Outcome("revise"), 1);

        Assert.Equal(VerifyDecision.Blocked, r.Decision);
    }

    // ==================================================================
    // 服务故障不计次
    // ==================================================================

    /// <summary>
    /// ★ fail-open 时放行，且**不消耗任何额度**。
    ///
    /// Python 版把失败结果计入了 supplement 额度，于是「服务抖一下」被记成
    /// 「执行者做得不对」：4 次补充额度 + 2 次重试额度被网络问题白白烧掉，
    /// 模型还会收到「请补充说明」的指令，然后认真地补一堆没用的说明。
    /// 严格模式下最后更会变成「整批拦截」，而执行者其实什么都没做错。
    /// </summary>
    [Fact]
    public void FailOpen_ProceedsWithoutConsumingQuota()
    {
        var session = NewSession(failMode: VerifyFailMode.Open);

        for (var i = 0; i < 5; i++)
        {
            var r = session.Decide(Outcome("approve", failed: true), 1);

            Assert.Equal(VerifyDecision.Proceed, r.Decision);
            Assert.Contains("fail-open", r.Note);
        }

        Assert.Equal(0, session.Retries);
        Assert.Equal(0, session.Supplements);
    }

    [Fact]
    public void FailClosed_BlocksWithoutConsumingQuota()
    {
        var session = NewSession(failMode: VerifyFailMode.Closed);

        var r = session.Decide(Outcome("supplement", failed: true), 1);

        Assert.Equal(VerifyDecision.Blocked, r.Decision);
        Assert.Contains("fail-closed", r.Note);
        Assert.Equal(0, session.Retries);
        Assert.Equal(0, session.Supplements);

        // 再来一次仍然是 Blocked，不会因为「额度用尽」变成别的走向
        Assert.Equal(VerifyDecision.Blocked, session.Decide(Outcome("supplement", failed: true), 1).Decision);
    }

    // ==================================================================
    // 作用域
    // ==================================================================

    [Fact]
    public void BeginTurn_ResetsCounters()
    {
        var session = NewSession();

        session.Decide(Outcome("revise"), 1);
        session.Decide(Outcome("supplement"), 1);

        Assert.Equal(1, session.Retries);
        Assert.Equal(1, session.Supplements);

        session.BeginTurn();

        Assert.Equal(0, session.Retries);
        Assert.Equal(0, session.Supplements);
    }

    // ==================================================================
    // 送审上下文
    // ==================================================================

    [Fact]
    public void ExtraNote_FirstAttempt_HasNoRepeatHint()
    {
        var note = NewSession().BuildExtraNote(@"E:\work", autoApprovedThisTurn: false, "all");

        Assert.Contains(@"E:\work", note);
        Assert.Contains("第 1 次送审", note);
        Assert.DoesNotContain("不要另提无关的新要求", note);
    }

    [Fact]
    public void ExtraNote_SecondAttempt_TellsVerifierNotToEscalate()
    {
        var session = NewSession();
        session.Decide(Outcome("revise"), 1);

        var note = session.BuildExtraNote(@"E:\work", autoApprovedThisTurn: false, "all");

        Assert.Contains("第 2 次送审", note);
        Assert.Contains("不要另提无关的新要求", note);
    }

    /// <summary>
    /// 一键放行时**必须如实告诉审查员**「你已经没有人类兜底了」。
    ///
    /// 审查员人格里写着「后面还有用户确认」，那句话在一键放行下不再成立。
    /// 不告诉它，它会按「反正有人兜底」的默认标准放松审查 ——
    /// 而这时候它已经是唯一的一道外部复核。
    /// </summary>
    [Fact]
    public void ExtraNote_AutoApprove_WarnsVerifier()
    {
        var note = NewSession().BuildExtraNote(@"E:\work", autoApprovedThisTurn: true, "all");

        Assert.Contains("一键放行", note);
        Assert.Contains("唯一的外部复核", note);
        Assert.Contains("从严把关", note);
    }

    [Fact]
    public void ExtraNote_AutoApproveWritesScope_DescribesNarrowerCoverage()
    {
        var note = NewSession().BuildExtraNote(@"E:\work", autoApprovedThisTurn: true, "writes");

        Assert.Contains("删除 / 执行类仍会逐批人工确认", note);
    }

    // ==================================================================
    // 回填文本
    // ==================================================================

    [Fact]
    public void Feedback_ListsDemandsAndRisks()
    {
        var outcome = new VerifyOutcome
        {
            Verdict = new Verdict
            {
                Value = "revise",
                Reason = "覆盖整个文件风险高",
                Demands = new[] { "改用 ws_replace 局部修改", "说明备份位置" },
                Risks = new[] { "全量覆盖不可逆" },
                Alternatives = new[] { "先 ws_read 再 ws_replace" },
            },
        };

        var text = outcome.ToFeedback();

        Assert.Contains("双人核验·未通过", text);
        Assert.Contains("改用 ws_replace 局部修改", text);
        Assert.Contains("说明备份位置", text);
        Assert.Contains("全量覆盖不可逆", text);
        Assert.Contains("先 ws_read 再 ws_replace", text);
        Assert.Contains("不要原样重试", text);
    }

    [Fact]
    public void Feedback_WithoutDemands_AsksForPurposeAndRollback()
    {
        var text = new VerifyOutcome
        {
            Verdict = new Verdict { Value = "supplement", Reason = "info" },
        }.ToFeedback();

        Assert.Contains("目的、影响范围与回滚方式", text);
    }
}

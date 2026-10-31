// Cases chain logic: stage-to-step mapping, current-stage marking, the
// list-page strip from case refs, and the agent summary (what it did plus
// what it tried and was stopped from doing, with rule ids and reasons).
// Test data is local and never depends on the demo numbers. No live calls.
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Eval.Ui;

public sealed class CaseChainTests
{
    private static readonly DateTimeOffset At0 =
        new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static Case CaseAt(CaseStage stage, string? blockedRule = null, string? blockedReason = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Case(
            "CASE-T", "SO-T", "ITEM-T", "ui-test", stage, now, now,
            "PR-T", "PO-T", null, null, [], [], blockedRule, blockedReason);
    }

    [Theory]
    [InlineData(CaseStage.Created, "Sales order")]
    [InlineData(CaseStage.ShortfallComputed, "Sales order")]
    [InlineData(CaseStage.PrSubmitted, "Purchase requisition")]
    [InlineData(CaseStage.PrApproved, "Purchase requisition")]
    [InlineData(CaseStage.VendorScreened, "Purchase order")]
    [InlineData(CaseStage.PoPlaced, "Purchase order")]
    [InlineData(CaseStage.PoAcknowledged, "Purchase order")]
    [InlineData(CaseStage.AsnRecorded, "ASN")]
    [InlineData(CaseStage.ReceiptPosted, "Receipt")]
    [InlineData(CaseStage.DockChecked, "Receipt")]
    [InlineData(CaseStage.InvoiceMatched, "Invoices")]
    [InlineData(CaseStage.PaymentRequested, "Payments")]
    [InlineData(CaseStage.PaymentPartial, "Payments")]
    [InlineData(CaseStage.Resolved, "Payments")]
    [InlineData(CaseStage.Closed, "Payments")]
    public void EveryLiveStageMapsToAStep(CaseStage stage, string label)
    {
        Assert.Equal(label, CaseChain.StepFor(stage));
    }

    [Fact]
    public void CancelledMapsToNoStep()
    {
        Assert.Null(CaseChain.StepFor(CaseStage.Cancelled));
    }

    [Fact]
    public void MarkCurrentMarksExactlyOne()
    {
        var steps = CaseChain.Strip(CaseAt(CaseStage.PoPlaced));

        var current = steps.Where(s => s.IsCurrent).ToList();

        var single = Assert.Single(current);
        Assert.Equal("Purchase order", single.Label);
    }

    [Fact]
    public void MarkCurrentMarksNoneWhenCancelled()
    {
        var steps = CaseChain.Strip(CaseAt(CaseStage.Cancelled));

        Assert.Empty(steps.Where(s => s.IsCurrent));
    }

    [Fact]
    public void StripCoversOrderToPaymentInOrder()
    {
        var steps = CaseChain.Strip(CaseAt(CaseStage.Created));

        Assert.Equal(
            ["Sales order", "Purchase requisition", "Purchase order", "ASN", "Receipt", "Invoices", "Payments"],
            steps.Select(s => s.Label).ToList());
    }

    [Fact]
    public void StripCarriesCaseRefs()
    {
        var steps = CaseChain.Strip(CaseAt(CaseStage.PoPlaced));

        Assert.Equal("SO-T", steps[0].DocumentNumber);
        Assert.Equal("PR-T", steps[1].DocumentNumber);
        Assert.Equal("PO-T", steps[2].DocumentNumber);
        Assert.Null(steps[3].DocumentNumber);
    }

    private static CaseEvent Event(
        string id, CaseEventKind kind, string summary, string? ruleId = null,
        decimal? amount = null, string? currency = null, int minutesAfter = 0) =>
        new(id, "CASE-T", At0.AddMinutes(minutesAfter), RecordSource.P2pAgent, kind, summary, ruleId,
            null, null, null, amount, currency);

    [Fact]
    public void SummaryDidListsTimelineInOrderWithMoney()
    {
        var events = new[]
        {
            Event("E-2", CaseEventKind.PoCreated, "PO placed with V-T.", null, 100m, "TST", 5),
            Event("E-1", CaseEventKind.PrCreated, "PR raised for the shortfall."),
        };

        var summary = AgentSummary.Build(CaseAt(CaseStage.PoPlaced), events, [], []);

        Assert.Equal(2, summary.Did.Count);
        Assert.Contains("PR raised", summary.Did[0]);
        Assert.Contains("PO placed", summary.Did[1]);
        Assert.Contains("100 TST", summary.Did[1]);
    }

    [Fact]
    public void SummaryStoppedCarriesRuleIdAndReason()
    {
        var events = new[]
        {
            Event("E-1", CaseEventKind.VendorBlocked, "V-X fails the origin cap.", "CORP-02"),
            Event("E-2", CaseEventKind.HoldSet, "Carton held at the dock.", "BAT-LI-03"),
        };

        var summary = AgentSummary.Build(CaseAt(CaseStage.DockChecked), events, [], []);

        Assert.Equal(2, summary.Stopped.Count);
        Assert.Equal("CORP-02", summary.Stopped[0].RuleId);
        Assert.Contains("origin cap", summary.Stopped[0].Reason);
        Assert.Equal("BAT-LI-03", summary.Stopped[1].RuleId);
    }

    [Fact]
    public void SummaryStoppedIncludesHoldsWaitsAndNowBlocked()
    {
        var holds = new[]
        {
            new HoldDto("H-1", "DOCK", "mark missing", "BAT-LI-03", "CASE-T",
                "p2p-agent", At0, HoldStatus.Set, "Receipt", "RCP-T", null, null, "CTN-T"),
        };
        var decisions = new[]
        {
            UiTestSupport.PendingDecision("D-1", "CASE-T", DecisionKind.DockReview, "Receipt", "RCP-T")
                with { RequestedTo = "approver", RuleId = "BAT-LI-03", Reason = "Confirm the relabel." },
        };

        var summary = AgentSummary.Build(
            CaseAt(CaseStage.DockChecked, "BAT-LI-03", "waiting on the relabel review"),
            [], decisions, holds);

        Assert.Equal(3, summary.Stopped.Count);
        var hold = summary.Stopped[0];
        Assert.Equal("BAT-LI-03", hold.RuleId);
        Assert.Contains("DOCK", hold.Reason);
        var wait = summary.Stopped[1];
        Assert.Contains("Waiting on approver", wait.What);
        Assert.Equal("BAT-LI-03", wait.RuleId);
        var now = summary.Stopped[2];
        Assert.Equal("Now blocked", now.What);
        Assert.Equal("BAT-LI-03", now.RuleId);
        Assert.Contains("relabel review", now.Reason);
    }

    [Fact]
    public void SummaryWaitWithoutRuleStillExplains()
    {
        var decisions = new[]
        {
            UiTestSupport.PendingDecision("D-1", "CASE-T", DecisionKind.PrApproval, "PR", "PR-T")
                with { RequestedTo = "approver", RuleId = null, Reason = null },
        };

        var summary = AgentSummary.Build(CaseAt(CaseStage.PrSubmitted), [], decisions, []);

        var wait = Assert.Single(summary.Stopped);
        Assert.Null(wait.RuleId);
        Assert.Contains("approver", wait.Reason);
    }

    [Fact]
    public void EmptyCaseSummarizesEmpty()
    {
        var summary = AgentSummary.Build(CaseAt(CaseStage.Created), [], [], []);

        Assert.Empty(summary.Did);
        Assert.Empty(summary.Stopped);
    }
}

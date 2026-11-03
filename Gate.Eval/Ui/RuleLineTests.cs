// Rule-line tests: every decision card's Rule line shows the rule id,
// pack version, and name plus the reason; a plain approval with no rule
// shows the routing in words (whose limit it exceeds, who it goes to,
// who covers). Data is local (SeededErp plus stored documents).
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class RuleLineTests
{
    [Fact]
    public async Task RuleLineShowsIdVersionNameAndReason()
    {
        var views = Views(SeededErp.Create());
        var decision = UiTestSupport.PendingDecision("D-R1", "CASE-R1") with
        {
            RuleId = "CORP-02",
            Reason = "Share exceeded.",
        };

        var line = await views.RuleLineAsync(decision);

        Assert.StartsWith("CORP-02 (", line, StringComparison.Ordinal);
        Assert.Contains("Share exceeded.", line, StringComparison.Ordinal);
        Assert.DoesNotContain("— —", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownRuleRendersHonestly()
    {
        var views = Views(SeededErp.Create());
        var decision = UiTestSupport.PendingDecision("D-R2", "CASE-R2") with
        {
            RuleId = "NOT-A-RULE",
            Reason = "Custom check.",
        };

        Assert.Equal("NOT-A-RULE — Custom check.", await views.RuleLineAsync(decision));
    }

    [Fact]
    public async Task PlainApprovalShowsLimitAndCovering()
    {
        var erp = SeededErp.Create();
        var pr = await erp.CreatePrAsync(
            "planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, 90m, "EA")],
            Guid.NewGuid().ToString("N"));
        var views = Views(erp);
        var decision = UiTestSupport.PendingDecision("D-R3", "CASE-R3") with
        {
            Kind = DecisionKind.PrApproval,
            DocumentType = "PR",
            DocumentNumber = pr.PrNumber,
            RequestedTo = "approver",
            Amount = 6000m,
            Currency = "USD",
        };

        var line = await views.RuleLineAsync(decision);

        Assert.Contains("planner's $5,000.00 limit", line, StringComparison.Ordinal);
        Assert.Contains("it goes to approver", line, StringComparison.Ordinal);
        Assert.Contains("delegate covering", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainApprovalUnderLimitRoutesWithoutOverClaim()
    {
        var erp = SeededErp.Create();
        var pr = await erp.CreatePrAsync(
            "planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, 90m, "EA")],
            Guid.NewGuid().ToString("N"));
        var views = Views(erp);
        var decision = UiTestSupport.PendingDecision("D-R4", "CASE-R4") with
        {
            Kind = DecisionKind.PrApproval,
            DocumentType = "PR",
            DocumentNumber = pr.PrNumber,
            RequestedTo = "approver",
            Amount = 100m,
            Currency = "USD",
            Reason = "Segregation check.",
        };

        var line = await views.RuleLineAsync(decision);

        Assert.Contains("Routed to approver", line, StringComparison.Ordinal);
        Assert.Contains("Segregation check.", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Over planner's", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewWithoutRulePassesReasonThrough()
    {
        var views = Views(SeededErp.Create());
        var decision = UiTestSupport.PendingDecision("D-R5", "CASE-R5") with
        {
            Kind = DecisionKind.DockReview,
            Reason = "Carton unreadable.",
        };

        Assert.Equal("Carton unreadable.", await views.RuleLineAsync(decision));
    }

    private static CaseViewService Views(InMemoryErpAdapter erp)
    {
        var dir = UiTestSupport.NewDir("gate-ui-ruleline");
        return new CaseViewService(
            erp,
            new CaseCacheStore(Path.Combine(dir, "cache")),
            new DockPhotoService(Path.Combine(dir, "img"), Path.Combine(dir, "reads")),
            UiTestLogger<CaseViewService>.Instance);
    }
}

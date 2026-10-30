// Decision completion from the Decisions surface against the in-memory
// fake ERP (unit tests only; the app uses EF Core). Covers the ERP-backed
// approval path, bank verification, review hold release, and failure
// paths. No live calls.
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class DecisionOutcomeTests
{
    private static InMemoryErpAdapter SeededErp()
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("TST");
        erp.SeedAutoApproveThreshold(10m);
        erp.SeedVendorPrice("V-TEST", "ITEM-1", 10m);
        erp.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        erp.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));
        erp.SeedUser(new UserDto("stranger", "clerk", 1_000_000m, "TST"));
        return erp;
    }

    private static DecisionOutcomeService Outcomes(InMemoryErpAdapter erp, CaseCacheStore store) =>
        new(erp, store, UiTestLogger<DecisionOutcomeService>.Instance);

    [Fact]
    public async Task ApprovePrRecordsApprovalAndClosesDecision()
    {
        var erp = SeededErp();
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        var file = UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber),
        ]);
        await store.WriteAsync(file);

        var outcome = await Outcomes(erp, store).ApplyAsync("CASE-1", "D-1", true, "looks good", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        var approvals = await erp.GetApprovalsAsync("PR", pr.PrNumber);
        Assert.Single(approvals);
        Assert.True(approvals[0].Approved);
        Assert.Equal("looks good", approvals[0].Comment);
        var reread = await store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        var decided = reread.Decisions.Single(d => d.DecisionId == "D-1");
        Assert.Equal(DecisionStatus.Approved, decided.Status);
        Assert.Equal("approver", decided.DecidedBy);
        Assert.NotNull(decided.DecidedAt);
        Assert.Equal("looks good", decided.Comment);
        Assert.Contains(reread.Events, e => e.Kind == CaseEventKind.PrApproved);
    }

    [Fact]
    public async Task RejectPrRecordsRejection()
    {
        var erp = SeededErp();
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber),
        ]));

        var outcome = await Outcomes(erp, store).ApplyAsync("CASE-1", "D-1", false, "over budget", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        var reread = await store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Rejected, reread.Decisions.Single().Status);
        Assert.Contains(reread.Events, e => e.Kind == CaseEventKind.PrRejected);
    }

    [Fact]
    public async Task WrongApproverFailsWithContext()
    {
        var erp = SeededErp();
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber),
        ]));

        var outcome = await Outcomes(erp, store).ApplyAsync("CASE-1", "D-1", true, null, "stranger");

        Assert.False(outcome.Ok);
        Assert.Contains("stranger", outcome.Message);
        var reread = await store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Pending, reread.Decisions.Single().Status);
    }

    [Fact]
    public async Task AlreadyDecidedIsRefused()
    {
        var erp = SeededErp();
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        var pending = UiTestSupport.PendingDecision("D-1", "CASE-1");
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            pending with { Status = DecisionStatus.Approved, DecidedBy = "approver" },
        ]));

        var outcome = await Outcomes(erp, store).ApplyAsync("CASE-1", "D-1", true, null, "approver");

        Assert.False(outcome.Ok);
        Assert.Contains("already", outcome.Message);
    }

    [Fact]
    public async Task MissingDocumentReferenceFails()
    {
        var erp = SeededErp();
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PoApproval, null, null),
        ]));

        var outcome = await Outcomes(erp, store).ApplyAsync("CASE-1", "D-1", true, null, "approver");

        Assert.False(outcome.Ok);
        Assert.Contains("no document reference", outcome.Message);
    }

    [Fact]
    public async Task BankVerificationApproveVerifiesVendor()
    {
        var erp = SeededErp();
        erp.SeedVendorBank(new VendorBankDto("V-TEST", "BANK-X", false, null));
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.BankVerification, "Vendor", "V-TEST"),
        ]));

        var outcome = await Outcomes(erp, store).ApplyAsync("CASE-1", "D-1", true, "called the bank", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        var bank = await erp.GetVendorBankAsync("V-TEST");
        Assert.NotNull(bank);
        Assert.True(bank.Verified);
        var reread = await store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Approved, reread.Decisions.Single().Status);
    }

    [Fact]
    public async Task BankVerificationRejectLeavesMasterUnverified()
    {
        var erp = SeededErp();
        erp.SeedVendorBank(new VendorBankDto("V-TEST", "BANK-X", false, null));
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.BankVerification, "Vendor", "V-TEST"),
        ]));

        var outcome = await Outcomes(erp, store).ApplyAsync("CASE-1", "D-1", false, "could not reach", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        var bank = await erp.GetVendorBankAsync("V-TEST");
        Assert.NotNull(bank);
        Assert.False(bank.Verified);
    }

    [Fact]
    public async Task ReviewApprovalReleasesMatchingHold()
    {
        var erp = SeededErp();
        var hold = await erp.SetHoldAsync(
            "DOCK", "test hold", "RULE-1", "CASE-1", "agent",
            "Receipt", "RCP-1", null, null, null, Guid.NewGuid().ToString("N"));
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        var review = UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.DockReview, "Receipt", "RCP-1")
            with { RuleId = "RULE-1" };
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [review]));

        var outcome = await Outcomes(erp, store).ApplyAsync("CASE-1", "D-1", true, "cleared", "approver");

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Contains(hold.HoldId, outcome.Message);
        Assert.Empty(await erp.GetOpenHoldsAsync("RCP-1"));
        var reread = await store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Contains(reread.Events, e => e.Kind == CaseEventKind.ReviewClosed);
    }

    [Fact]
    public async Task ReviewApprovalWithNoHoldWarnsButCloses()
    {
        var erp = SeededErp();
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.ApReview, "Invoice", "V-1/INV-1"),
        ]));

        var outcome = await Outcomes(erp, store).ApplyAsync("CASE-1", "D-1", true, null, "approver");

        Assert.True(outcome.Ok, outcome.Message);
        Assert.NotEmpty(outcome.Warnings);
        var reread = await store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Approved, reread.Decisions.Single().Status);
    }
}

// CORP-02 exception grant flow tests (offline, no live calls): a CORP-02
// award block raises an ExceptionApproval card to the sourcing-policy matrix
// owner; a named-approver grant with reason re-screens with the exception
// and continues the award; a deny keeps the rejection; every refusal fails
// loudly. Test-double data only: invented items, vendors, and small history,
// never demo numbers.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class CorpExceptionFlowTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp,
        RecordingModelClient Models,
        RecordingComplianceEngine Compliance,
        CaseRunner Runner,
        string CaseDir);

    private static string TempDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    private static Harness Create()
    {
        var erp = SeededErp.Create();
        erp.SeedItem(new ItemDto("BAT-T9", "Test cell", true, 10, "EA"));
        erp.SeedVendor(new VendorDto("V-T1", "Test Vendor One", "CN", false, null, "NET0"));
        erp.SeedVendor(new VendorDto("V-T2", "Test Vendor Two", "MX", false, null, "NET0"));
        erp.SeedVendor(new VendorDto("V-T3", "Test Vendor Three", "CN", false, null, "NET0"));
        erp.SeedVendorBank(new VendorBankDto("V-T1", "BANK-T1", true, DateTimeOffset.UtcNow));
        erp.SeedVendorBank(new VendorBankDto("V-T2", "BANK-T2", true, DateTimeOffset.UtcNow));
        erp.SeedVendorBank(new VendorBankDto("V-T3", "BANK-T3", true, DateTimeOffset.UtcNow));
        erp.SeedVendorPrice("V-T1", "BAT-T9", 11m);
        erp.SeedVendorPrice("V-T2", "BAT-T9", 12m);
        erp.SeedVendorPrice("V-T3", "BAT-T9", 11m);
        erp.SeedVendorDocuments("V-T1", FullDocs("V-T1"));
        erp.SeedVendorDocuments("V-T2", FullDocs("V-T2"));
        erp.SeedVendorDocuments("V-T3", []);
        // (65 + 10) / (100 + 10) = 68%: over the 60% cap for CN vendors.
        erp.SeedVolumeHistory("BAT-T9",
        [
            new VolumeHistory("Seed CN", "CN", 65),
            new VolumeHistory("Seed Other", "OTHER", 35),
        ]);
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir("gate-corp-exception-test");
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir,
            splitProposals: new SplitProposals { IsOn = false });
        return new Harness(erp, models, compliance, runner, dir);
    }

    private static IReadOnlyList<VendorDocumentDto> FullDocs(string vendorId) =>
    [
        new VendorDocumentDto(vendorId, "UN38.3", $"un383-{vendorId}", true),
        new VendorDocumentDto(vendorId, "SDS", $"sds-{vendorId}", true),
        new VendorDocumentDto(vendorId, "RoHS", $"rohs-{vendorId}", true),
        new VendorDocumentDto(vendorId, "REACH", $"reach-{vendorId}", true),
    ];

    private static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";

    private static async Task<string> OpenCaseAsync(
        Harness h, IReadOnlyList<string> candidates, string scenario = "test")
    {
        var input = new OrderWebhookInput(
            scenario,
            $"SHOP-{Guid.NewGuid():N}",
            "planner",
            new Dictionary<string, IReadOnlyList<string>> { ["BAT-T9"] = candidates },
            $"idem-{Guid.NewGuid():N}",
            [new SalesOrderLine("BAT-T9", 10)],
            "USD");
        var cases = await h.Runner.OnOrderWebhookAsync(input);
        return Assert.Single(cases);
    }

    private static async Task<string> ScreenToExceptionParkAsync(Harness h, string caseId)
    {
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        var outcome = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);
        return caseId;
    }

    [Fact]
    public async Task CorpBlockRaisesCardToMatrixOwnerWithRuleAndVendor()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, ["V-T1"]);
        await ScreenToExceptionParkAsync(h, caseId);

        var file = CaseStore.Load(h.CaseDir, caseId)!;
        var card = Assert.Single(file.Decisions, d =>
            d.Kind == DecisionKind.ExceptionApproval && d.Status == DecisionStatus.Pending);
        Assert.Equal("Terry Owner", card.RequestedTo);
        Assert.Equal("CORP-02", card.RuleId);
        Assert.Equal("Vendor", card.DocumentType);
        Assert.Equal("V-T1", card.DocumentNumber);
        Assert.Contains("V-T1", card.Title, StringComparison.Ordinal);
        Assert.Contains("CORP-02", card.Title, StringComparison.Ordinal);
        Assert.Contains("V-T1", card.Reason, StringComparison.Ordinal);
        Assert.Contains("CORP-02", card.Reason, StringComparison.Ordinal);
        var raised = Assert.Single(file.Events, e =>
            e.Summary.Contains("exception approval card", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("CORP-02", raised.RuleId);
        Assert.Contains("V-T1", raised.Summary, StringComparison.Ordinal);
        Assert.Contains("Terry Owner", raised.Summary, StringComparison.Ordinal);
        var vendor = (await h.Erp.GetVendorsAsync()).Single(v => v.VendorId == "V-T1");
        Assert.False(vendor.IsBlocked);
    }

    [Fact]
    public async Task NoCardWhenAPassingVendorExists()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, ["V-T1", "V-T2"]);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        h.Models.QueueText("C# awards the passing vendor.");
        h.Models.QueueToolCalls("create", ("c5", AgentTools.CreatePo, Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c6", AgentTools.SubmitPoApproval, Args(caseId)));
        var outcome = await h.Runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitSupplierAck, outcome.Step);
        var file = CaseStore.Load(h.CaseDir, caseId)!;
        Assert.DoesNotContain(file.Decisions, d => d.Kind == DecisionKind.ExceptionApproval);
        Assert.Equal("V-T2", file.Case.AwardedVendorId);
    }

    [Fact]
    public async Task GrantRescreensAndContinuesTheAward()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, ["V-T1"]);
        await ScreenToExceptionParkAsync(h, caseId);
        var file = CaseStore.Load(h.CaseDir, caseId)!;
        var card = file.Decisions.Single(d => d.Kind == DecisionKind.ExceptionApproval);

        var granted = await h.Runner.ResolveDecisionAsync(
            caseId, card.DecisionId, true, "Terry Owner", "test exception reason");

        Assert.Equal(DecisionStatus.Approved, granted.Status);
        var vendor = (await h.Erp.GetVendorsAsync()).Single(v => v.VendorId == "V-T1");
        Assert.False(vendor.IsBlocked);
        var afterGrant = CaseStore.Load(h.CaseDir, caseId)!;
        Assert.Contains(afterGrant.Events, e =>
            e.Summary.Contains("granted by Terry Owner", StringComparison.Ordinal)
            && e.Summary.Contains("V-T1", StringComparison.Ordinal)
            && e.RuleId == "CORP-02");

        h.Models.QueueText("C# awards the excused vendor.");
        h.Models.QueueToolCalls("create", ("c5", AgentTools.CreatePo, Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c6", AgentTools.SubmitPoApproval, Args(caseId)));
        var outcome = await h.Runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitSupplierAck, outcome.Step);
        var awarded = CaseStore.Load(h.CaseDir, caseId)!;
        Assert.Equal("V-T1", awarded.Case.AwardedVendorId);
        var award = awarded.Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains("V-T1", award.Summary, StringComparison.Ordinal);
        Assert.Contains("Terry Owner", award.Summary, StringComparison.Ordinal);
        Assert.Contains("CORP-02", award.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DenyKeepsTheRejection()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, ["V-T1"]);
        await ScreenToExceptionParkAsync(h, caseId);
        var file = CaseStore.Load(h.CaseDir, caseId)!;
        var card = file.Decisions.Single(d => d.Kind == DecisionKind.ExceptionApproval);

        var denied = await h.Runner.ResolveDecisionAsync(
            caseId, card.DecisionId, false, "Terry Owner", "no capacity for exceptions");

        Assert.Equal(DecisionStatus.Rejected, denied.Status);
        var vendor = (await h.Erp.GetVendorsAsync()).Single(v => v.VendorId == "V-T1");
        Assert.False(vendor.IsBlocked);
        var afterDeny = CaseStore.Load(h.CaseDir, caseId)!;
        Assert.Contains(afterDeny.Events, e =>
            e.Summary.Contains("denied by Terry Owner", StringComparison.Ordinal)
            && e.Summary.Contains("V-T1", StringComparison.Ordinal)
            && e.RuleId == "CORP-02");

        h.Models.QueueText("C# retries the blocked award.");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.AdvanceAsync(caseId));
        Assert.Contains("No compliant vendor", ex.Message, StringComparison.Ordinal);
        Assert.Null(CaseStore.Load(h.CaseDir, caseId)!.Case.AwardedVendorId);
    }

    [Fact]
    public async Task GrantWithBlankApproverIsRefused()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, ["V-T1"]);
        await ScreenToExceptionParkAsync(h, caseId);
        var card = CaseStore.Load(h.CaseDir, caseId)!.Decisions
            .Single(d => d.Kind == DecisionKind.ExceptionApproval);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.ResolveDecisionAsync(caseId, card.DecisionId, true, "  ", "test reason"));

        Assert.Contains("no approver", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            DecisionStatus.Pending,
            CaseStore.Load(h.CaseDir, caseId)!.Decisions
                .Single(d => d.DecisionId == card.DecisionId).Status);
    }

    [Fact]
    public async Task GrantWithBlankReasonIsRefused()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, ["V-T1"]);
        await ScreenToExceptionParkAsync(h, caseId);
        var card = CaseStore.Load(h.CaseDir, caseId)!.Decisions
            .Single(d => d.Kind == DecisionKind.ExceptionApproval);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.ResolveDecisionAsync(caseId, card.DecisionId, true, "Terry Owner", " "));

        Assert.Contains("no reason", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            DecisionStatus.Pending,
            CaseStore.Load(h.CaseDir, caseId)!.Decisions
                .Single(d => d.DecisionId == card.DecisionId).Status);
    }

    [Fact]
    public async Task MissingMatrixRowRefusesRouting()
    {
        var h = Create();
        h.Compliance.Snapshot = MemoFixtures.TestKb() with { ApprovalMatrix = [] };
        var caseId = await OpenCaseAsync(h, ["V-T1"]);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.AdvanceAsync(caseId));

        Assert.Contains("sourcing-policy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrantWithOtherBlocksIsRefusedAndStaysPending()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, ["V-T3"]);
        await ScreenToExceptionParkAsync(h, caseId);
        var card = CaseStore.Load(h.CaseDir, caseId)!.Decisions
            .Single(d => d.Kind == DecisionKind.ExceptionApproval);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.ResolveDecisionAsync(caseId, card.DecisionId, true, "Terry Owner", "test reason"));

        Assert.Contains("still blocks", ex.Message, StringComparison.Ordinal);
        Assert.Contains("BAT-LI-01", ex.Message, StringComparison.Ordinal);
        Assert.Equal(
            DecisionStatus.Pending,
            CaseStore.Load(h.CaseDir, caseId)!.Decisions
                .Single(d => d.DecisionId == card.DecisionId).Status);
        Assert.False((await h.Erp.GetVendorsAsync()).Single(v => v.VendorId == "V-T3").IsBlocked);
    }

    [Fact]
    public async Task NonCorpExceptionCardIsRefused()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, ["V-T2"]);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        h.Models.QueueText("C# awards the passing vendor.");
        h.Models.QueueToolCalls("create", ("c5", AgentTools.CreatePo, Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c6", AgentTools.SubmitPoApproval, Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);

        var file = CaseStore.Load(h.CaseDir, caseId)!;
        var fake = new Decision(
            $"{caseId}-d900", caseId, DecisionKind.ExceptionApproval,
            "ELEC-01 exception for vendor V-T2", DecisionStatus.Pending,
            "Terry Owner", DateTimeOffset.UtcNow, null, null,
            "Vendor", "V-T2", null, null, "ELEC-01", "test", [], null);
        CaseStore.Save(h.CaseDir, file with { Decisions = file.Decisions.Append(fake).ToList() });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.ResolveDecisionAsync(caseId, fake.DecisionId, true, "Terry Owner", "test reason"));

        Assert.Contains("only CORP-02", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RescreenWithValidGrantPassesOverCapAward()
    {
        var h = Create();
        var pr = await h.Erp.CreatePrAsync(
            "planner", "test", "planner",
            [new PurchaseRequisitionLine("BAT-T9", 10, "EA")],
            $"rescreen-{Guid.NewGuid():N}");
        var core = new P2pAgent(h.Erp, h.Models, h.Compliance);
        var grant = new CorpExceptionGrant(
            "CORP-02", "V-T1", "Terry Owner", DateTimeOffset.UtcNow, "test exception reason");

        var excused = await core.RescreenVendorWithExceptionAsync(pr.PrNumber, "V-T1", grant);

        Assert.True(excused.Passed);
        var corp = excused.Verdicts.Single(v => v.RuleId == "CORP-02");
        Assert.Equal(VerdictKind.Pass, corp.Kind);
        Assert.Contains("Terry Owner", corp.Reason, StringComparison.Ordinal);
        Assert.Contains("test exception reason", corp.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RescreenWithInvalidGrantStillBlocks()
    {
        var h = Create();
        var pr = await h.Erp.CreatePrAsync(
            "planner", "test", "planner",
            [new PurchaseRequisitionLine("BAT-T9", 10, "EA")],
            $"rescreen-{Guid.NewGuid():N}");
        var core = new P2pAgent(h.Erp, h.Models, h.Compliance);
        var now = DateTimeOffset.UtcNow;

        foreach (var grant in new[]
        {
            new CorpExceptionGrant("CORP-02", "V-T1", "  ", now, "test reason"),
            new CorpExceptionGrant("CORP-02", "V-T1", "Terry Owner", now, ""),
            new CorpExceptionGrant("CORP-02", "V-T2", "Terry Owner", now, "test reason"),
            new CorpExceptionGrant("CORP-01", "V-T1", "Terry Owner", now, "test reason"),
        })
        {
            var excused = await core.RescreenVendorWithExceptionAsync(pr.PrNumber, "V-T1", grant);
            Assert.False(excused.Passed);
            Assert.Contains(excused.Verdicts, v =>
                v.RuleId == "CORP-02" && v.Kind == VerdictKind.Block);
        }
    }
}

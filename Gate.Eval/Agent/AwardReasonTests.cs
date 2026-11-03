// Award-reason tests (offline, scripted model): the SupplierAwarded event
// names every losing candidate plus each blocking rule id from its
// screening. Spark writes that one-line prose on the tool path (C# blocks
// reasons with missing loser tokens so the model can retry); the C#-executed
// fallback pick records the same loser detail deterministically.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class AwardReasonTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp, CaseRunner Runner, RecordingModelClient Models, string CaseDir);

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "gate-award-test", Guid.NewGuid().ToString("N"));

    private static Harness Create()
    {
        var erp = SeededErp.Create();
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir();
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir,
            splitProposals: new SplitProposals { IsOn = false });
        return new Harness(erp, runner, models, dir);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-A", "V-B"],
            [SeededErp.Screw] = ["V-C"]
        };

    private static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";

    private static string AwardArgs(string caseId, string vendorId) =>
        $$"""{"case_id":"{{caseId}}","vendor_id":"{{vendorId}}"}""";

    private static async Task<string> OpenApprovedPrCaseAsync(
        Harness h, IReadOnlyDictionary<string, IReadOnlyList<string>>? candidates = null)
    {
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", candidates ?? Candidates()));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);
        var file = CaseStore.Load(h.CaseDir, caseId)!;
        var pr = file.Decisions.Single(d => d.Kind == DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(caseId, pr.DecisionId, true, pr.RequestedTo, "ok");
        return caseId;
    }

    private static CaseFile Load(Harness h, string caseId) =>
        CaseStore.Load(h.CaseDir, caseId) ?? throw new InvalidOperationException($"Case {caseId} vanished.");

    [Fact]
    public async Task ToolAwardNamesTheLoserAndItsBlockingRule()
    {
        var h = Create();
        var caseId = await OpenApprovedPrCaseAsync(h);
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        h.Models.QueueToolCalls("Award V-B; V-A blocked by CORP-02.",
            ("c4", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("create", ("c5", AgentTools.CreatePo, Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c6", AgentTools.SubmitPoApproval, Args(caseId)));

        await h.Runner.AdvanceAsync(caseId);

        var award = Load(h, caseId).Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains("V-B", award.Summary, StringComparison.Ordinal);
        Assert.Contains("V-A", award.Summary, StringComparison.Ordinal);
        Assert.Contains(ComplianceEngine.CorpOriginCapId, award.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusedAwardNamesWhatWasTriedAndWhatCameNext()
    {
        var h = Create();
        var caseId = await OpenApprovedPrCaseAsync(h);
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        h.Models.QueueToolCalls("Award V-A.", ("c4", AgentTools.AwardSupplier, AwardArgs(caseId, "V-A")));
        h.Models.QueueToolCalls("Award V-B; V-A blocked by CORP-02.",
            ("c5", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("create", ("c6", AgentTools.CreatePo, Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c7", AgentTools.SubmitPoApproval, Args(caseId)));

        await h.Runner.AdvanceAsync(caseId);

        var file = Load(h, caseId);
        var refused = file.Events.Single(e => e.Kind == CaseEventKind.AgentRefused);
        Assert.Contains("V-A", refused.Summary, StringComparison.Ordinal);
        Assert.Contains("refused", refused.Summary, StringComparison.Ordinal);
        Assert.Equal("V-A", refused.DocumentNumber);
        var award = file.Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains("V-B", award.Summary, StringComparison.Ordinal);
        Assert.Contains("Tried V-A first", award.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolAwardWithReasonMissingTheLoserIsBlockedThenRetrySucceeds()
    {
        var h = Create();
        var caseId = await OpenApprovedPrCaseAsync(h);
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        h.Models.QueueToolCalls("V-B is great.",
            ("c4", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("Award V-B; V-A blocked by CORP-02.",
            ("c5", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("create", ("c6", AgentTools.CreatePo, Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c7", AgentTools.SubmitPoApproval, Args(caseId)));

        await h.Runner.AdvanceAsync(caseId);

        var turns = CaseTranscript.ReadTurns(h.CaseDir, caseId);
        var refused = turns[3].Calls[0];
        Assert.Equal(ToolCallVerdict.Blocked, refused.Verdict);
        Assert.Contains("V-A", refused.Detail, StringComparison.Ordinal);
        Assert.Contains(ComplianceEngine.CorpOriginCapId, refused.Detail, StringComparison.Ordinal);
        Assert.Equal(ToolCallVerdict.Executed, turns[4].Calls[0].Verdict);
        var award = Load(h, caseId).Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains("V-A", award.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolAwardWithReasonMissingTheRuleIdIsBlocked()
    {
        var h = Create();
        var caseId = await OpenApprovedPrCaseAsync(h);
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        h.Models.QueueToolCalls("Award V-B over V-A.",
            ("c4", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("Award V-B; V-A blocked by CORP-02.",
            ("c5", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("create", ("c6", AgentTools.CreatePo, Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c7", AgentTools.SubmitPoApproval, Args(caseId)));

        await h.Runner.AdvanceAsync(caseId);

        var turns = CaseTranscript.ReadTurns(h.CaseDir, caseId);
        Assert.Equal(ToolCallVerdict.Blocked, turns[3].Calls[0].Verdict);
        Assert.Contains(ComplianceEngine.CorpOriginCapId, turns[3].Calls[0].Detail, StringComparison.Ordinal);
        Assert.Equal(ToolCallVerdict.Executed, turns[4].Calls[0].Verdict);
    }

    [Fact]
    public async Task ToolAwardWithABlankReasonIsBlocked()
    {
        var h = Create();
        var caseId = await OpenApprovedPrCaseAsync(h);
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        h.Models.QueueToolCalls("   ", ("c4", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("Award V-B; V-A blocked by CORP-02.",
            ("c5", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("create", ("c6", AgentTools.CreatePo, Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c7", AgentTools.SubmitPoApproval, Args(caseId)));

        await h.Runner.AdvanceAsync(caseId);

        var turns = CaseTranscript.ReadTurns(h.CaseDir, caseId);
        Assert.Equal(ToolCallVerdict.Blocked, turns[3].Calls[0].Verdict);
        Assert.Contains("one-line reason", turns[3].Calls[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallbackPickRecordsLosersHonestly()
    {
        var erp = SeededErp.Create();
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir();
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        var cases = await runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        await runner.AdvanceAsync(caseId);
        var raised = CaseStore.Load(dir, caseId)!;
        var pr = raised.Decisions.Single(d => d.Kind == DecisionKind.PrApproval);
        await runner.ResolveDecisionAsync(caseId, pr.DecisionId, true, pr.RequestedTo, "ok");

        await runner.AdvanceAsync(caseId);

        var file = CaseStore.Load(dir, caseId)!;
        Assert.Equal("V-B", file.Case.AwardedVendorId);
        var award = file.Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains("takes the award", award.Summary, StringComparison.Ordinal);
        Assert.Contains("V-A", award.Summary, StringComparison.Ordinal);
        Assert.Contains(ComplianceEngine.CorpOriginCapId, award.Summary, StringComparison.Ordinal);
        var turns = CaseTranscript.ReadTurns(dir, caseId);
        Assert.Contains(turns.SelectMany(t => t.Calls),
            c => c.Verdict == ToolCallVerdict.ModelDeferred && c.Name == AgentTools.AwardSupplier);
    }

    [Fact]
    public async Task SingleCandidateAwardNeedsNoLoserTokens()
    {
        var h = Create();
        var onlyWinner = new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-B"],
            [SeededErp.Screw] = ["V-C"]
        };
        var caseId = await OpenApprovedPrCaseAsync(h, onlyWinner);
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        h.Models.QueueToolCalls("Only V-B was sourced.",
            ("c4", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("create", ("c5", AgentTools.CreatePo, Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c6", AgentTools.SubmitPoApproval, Args(caseId)));

        await h.Runner.AdvanceAsync(caseId);

        var award = Load(h, caseId).Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains("Only V-B was sourced.", award.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassingLoserMustBeNamed()
    {
        var h = Create();
        h.Erp.SeedVendor(new VendorDto("V-D", "Extra Cells Inc", "MX", false, null, "NET0"));
        h.Erp.SeedVendorDocuments("V-D",
            [new VendorDocumentDto("V-D", "UN38.3", "un383-d", true), new VendorDocumentDto("V-D", "SDS", "sds-d", true), new VendorDocumentDto("V-D", "RoHS", "rohs-d", true), new VendorDocumentDto("V-D", "REACH", "reach-d", true)]);
        var bothPass = new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-B", "V-D"],
            [SeededErp.Screw] = ["V-C"]
        };
        var caseId = await OpenApprovedPrCaseAsync(h, bothPass);
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        h.Models.QueueToolCalls("Award V-B.",
            ("c4", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("Award V-B; V-D passed but costs more.",
            ("c5", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("create", ("c6", AgentTools.CreatePo, Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c7", AgentTools.SubmitPoApproval, Args(caseId)));

        await h.Runner.AdvanceAsync(caseId);

        var turns = CaseTranscript.ReadTurns(h.CaseDir, caseId);
        Assert.Equal(ToolCallVerdict.Blocked, turns[3].Calls[0].Verdict);
        Assert.Contains("V-D", turns[3].Calls[0].Detail, StringComparison.Ordinal);
        Assert.Equal(ToolCallVerdict.Executed, turns[4].Calls[0].Verdict);
        var award = Load(h, caseId).Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains("V-D", award.Summary, StringComparison.Ordinal);
    }
}

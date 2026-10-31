// Policy-approval tests: update vs new-rule routing by conflict shape,
// contradiction and NotMappable refusals, rejections, stale cards, KB
// drift, approver edits, evidence, and pack overrides.
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoApprovalTests
{
    private static PolicyApprovalInput Approve(
        string approver = "Terry Owner",
        IReadOnlyList<string>? evidence = null,
        IReadOnlyDictionary<string, string>? edited = null,
        string? packId = null,
        string? ruleId = null,
        NewRuleSpec? spec = null,
        string? comment = null,
        DateTimeOffset? effectiveAt = null,
        RuleApplicability applicability = RuleApplicability.ProspectiveOnly) =>
        new(true, approver, effectiveAt ?? DateTimeOffset.UtcNow.AddDays(30),
            applicability, comment, edited, packId, ruleId, spec,
            evidence ?? ["MemoApprovalTests.PublishPath"]);

    private static async Task<(MemoTestKit.Harness H, Decision Card)> CapAtApprovalAsync(
        string memoId = "MEMO-T1", string sparkJson = "")
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h, memoId);
        h.Models.QueueText(string.IsNullOrEmpty(sparkJson) ? MemoTestKit.CapProposalJson() : sparkJson);
        var outcome = await h.Runner.AdvanceAsync(memoId);
        Assert.Equal(MemoNextStep.AwaitApproval, outcome.Step);
        var card = MemoStore.Load(h.CacheDir, memoId)!.Decisions
            .Single(d => d.Kind == DecisionKind.PolicyApproval);
        return (h, card);
    }

    [Fact]
    public async Task ApproveTighteningPublishesARuleUpdate()
    {
        var (h, card) = await CapAtApprovalAsync();

        var result = await h.Runner.ResolvePolicyApprovalAsync("MEMO-T1", card.DecisionId, Approve());

        Assert.Equal(DecisionStatus.Approved, result.Decision.Status);
        Assert.NotNull(result.Activation);
        Assert.Equal("CORP-02", result.Activation.RuleId);
        Assert.Equal("CORP", result.Activation.PackId);
        Assert.Equal("v0.2", result.Activation.PriorPackVersion);
        Assert.Equal("v0.3", result.Activation.NewPackVersion);
        Assert.Equal("MEMO-T1", result.Activation.MemoId);
        Assert.Contains("PublishRuleUpdate:CORP:CORP-02", h.Compliance.MemoCalls);
        Assert.Contains("0.40", h.Compliance.PriorPacks["CORP"], StringComparison.Ordinal);
        var file = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        Assert.Equal(result.Activation, Assert.Single(file.Activations));
        Assert.Contains(file.Events, e =>
            e.Summary.Contains("published as CORP-02", StringComparison.Ordinal));
        var outcome = await h.Runner.AdvanceAsync("MEMO-T1");
        Assert.Equal(MemoNextStep.Done, outcome.Step);
        Assert.True(outcome.Complete);
    }

    [Fact]
    public async Task ApproveNewRulePublishesWithTheApproverSpec()
    {
        var (h, card) = await CapAtApprovalAsync("MEMO-T3", MemoTestKit.DocumentProposalJson());

        var result = await h.Runner.ResolvePolicyApprovalAsync("MEMO-T3", card.DecisionId,
            Approve(spec: new NewRuleSpec("COO declaration required.", RuleTier.Tier2, "COO-Declaration")));

        Assert.NotNull(result.Activation);
        Assert.Equal("CORP", result.Activation.PackId);
        Assert.Equal("CORP-MEMO-T3", result.Activation.RuleId);
        Assert.Equal("v0.3", result.Activation.NewPackVersion);
        Assert.Contains("PublishNewRule:CORP:CORP-MEMO-T3", h.Compliance.MemoCalls);
        Assert.Contains("COO declaration required.", h.Compliance.PriorPacks["CORP"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApproveNewRuleNeedsTheApproverSpec()
    {
        var (h, card) = await CapAtApprovalAsync("MEMO-T3", MemoTestKit.DocumentProposalJson());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync("MEMO-T3", card.DecisionId, Approve()));

        Assert.Contains("title, tier", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Compliance.Published);
        Assert.Equal(DecisionStatus.Pending,
            MemoStore.Load(h.CacheDir, "MEMO-T3")!.Decisions.Single(d => d.Kind == DecisionKind.PolicyApproval).Status);
    }

    [Fact]
    public async Task ExplicitRuleIdOverridesTheMintedOne()
    {
        var (h, card) = await CapAtApprovalAsync("MEMO-T3", MemoTestKit.DocumentProposalJson());

        var result = await h.Runner.ResolvePolicyApprovalAsync("MEMO-T3", card.DecisionId,
            Approve(ruleId: "CORP-09",
                spec: new NewRuleSpec("COO declaration required.", RuleTier.Tier2, "COO-Declaration")));

        Assert.Equal("CORP-09", result.Activation!.RuleId);
    }

    [Fact]
    public async Task ContradictionRefusesPublish()
    {
        var (h, card) = await CapAtApprovalAsync("MEMO-T4", MemoTestKit.ContradictingPrePayJson());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync("MEMO-T4", card.DecisionId, Approve()));

        Assert.Contains("contradicts", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BAT-LI-03", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Compliance.Published);
        Assert.Equal(DecisionStatus.Pending,
            MemoStore.Load(h.CacheDir, "MEMO-T4")!.Decisions.Single(d => d.Kind == DecisionKind.PolicyApproval).Status);
    }

    [Fact]
    public async Task ApproveNotMappableIsRefused()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h, "MEMO-T2");
        h.Models.QueueText(MemoTestKit.NotMappableJson());
        await h.Runner.ProposeAsync("MEMO-T2");
        var question = MemoStore.Load(h.CacheDir, "MEMO-T2")!.Decisions.Single();
        h.Models.QueueText(MemoTestKit.NotMappableResolvedJson());
        await h.Runner.ResolvePolicyQuestionAsync(
            "MEMO-T2", question.DecisionId, true, "Terry Owner", "50%");
        await h.Runner.AdvanceAsync("MEMO-T2");
        var card = MemoStore.Load(h.CacheDir, "MEMO-T2")!.Decisions
            .Single(d => d.Kind == DecisionKind.PolicyApproval);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync("MEMO-T2", card.DecisionId, Approve()));

        Assert.Contains("NotMappable", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Compliance.Published);
    }

    [Fact]
    public async Task RejectRecordsTheRefusal()
    {
        var (h, card) = await CapAtApprovalAsync();
        var input = Approve() with { Approved = false, Comment = "Too tight, too fast." };

        var result = await h.Runner.ResolvePolicyApprovalAsync("MEMO-T1", card.DecisionId, input);

        Assert.Equal(DecisionStatus.Rejected, result.Decision.Status);
        Assert.Null(result.Activation);
        Assert.Empty(h.Compliance.Published);
        var file = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        Assert.Contains(file.Events, e =>
            e.Summary.Contains("refused by Terry Owner", StringComparison.Ordinal));
        var outcome = await h.Runner.AdvanceAsync("MEMO-T1");
        Assert.Equal(MemoNextStep.Done, outcome.Step);
    }

    [Fact]
    public async Task StaleCardRefuses()
    {
        var (h, card) = await CapAtApprovalAsync();
        h.Models.QueueText(MemoTestKit.CapProposalJson(cap: "0.45"));
        await h.Runner.ProposeAsync("MEMO-T1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync("MEMO-T1", card.DecisionId, Approve()));

        Assert.Contains("stale", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Compliance.Published);
    }

    [Fact]
    public async Task KbDriftRefusesApproval()
    {
        var (h, card) = await CapAtApprovalAsync();
        h.Compliance.Snapshot = MemoFixtures.TestKb(version: "kbv10", hash: "hash10");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync("MEMO-T1", card.DecisionId, Approve()));

        Assert.Contains("KB version change", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Compliance.Published);
    }

    [Fact]
    public async Task EditedParamsWithinRegistryShip()
    {
        var (h, card) = await CapAtApprovalAsync();
        var edited = new Dictionary<string, string>
        {
            ["cap"] = "0.45",
            ["windowMonths"] = "12",
            ["partScope"] = "critical",
        };

        var result = await h.Runner.ResolvePolicyApprovalAsync(
            "MEMO-T1", card.DecisionId, Approve(edited: edited, comment: "Phase it."));

        Assert.NotNull(result.Activation);
        Assert.Contains("0.45", h.Compliance.PriorPacks["CORP"], StringComparison.Ordinal);
        Assert.Contains(MemoStore.Load(h.CacheDir, "MEMO-T1")!.Events, e =>
            e.Summary.Contains("Approver-edited", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EditedParamsOutsideRegistryRefuse()
    {
        var (h, card) = await CapAtApprovalAsync();
        var edited = new Dictionary<string, string>
        {
            ["cap"] = "9.0",
            ["windowMonths"] = "12",
            ["partScope"] = "critical",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync(
                "MEMO-T1", card.DecisionId, Approve(edited: edited)));

        Assert.Contains("Approver edit rejected", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Compliance.Published);
    }

    [Fact]
    public async Task MissingTestEvidenceRefuses()
    {
        var (h, card) = await CapAtApprovalAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync(
                "MEMO-T1", card.DecisionId, Approve(evidence: [])));

        Assert.Contains("untested", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Compliance.Published);
    }

    [Fact]
    public async Task BackdatedEffectiveWithoutReasonRefuses()
    {
        var (h, card) = await CapAtApprovalAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync("MEMO-T1", card.DecisionId,
                Approve(effectiveAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))));

        Assert.Contains("backdat", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Compliance.Published);
    }

    [Fact]
    public async Task ImmediateHoldOnVendorRuleRefuses()
    {
        var (h, card) = await CapAtApprovalAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync("MEMO-T1", card.DecisionId,
                Approve(applicability: RuleApplicability.ImmediateHold, comment: "Urgent.")));

        Assert.Contains("ImmediateHold", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Compliance.Published);
    }

    [Fact]
    public async Task WrongPackOverrideIsRefusedByTheEngine()
    {
        var (h, card) = await CapAtApprovalAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ResolvePolicyApprovalAsync(
                "MEMO-T1", card.DecisionId, Approve(packId: "ELEC")));

        Assert.Contains("belongs to pack 'CORP'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Compliance.Published);
        Assert.Equal(DecisionStatus.Pending,
            MemoStore.Load(h.CacheDir, "MEMO-T1")!.Decisions.Single(d => d.Kind == DecisionKind.PolicyApproval).Status);
    }

    [Fact]
    public async Task ResolvingADecidedCardReturnsItUnchanged()
    {
        var (h, card) = await CapAtApprovalAsync();
        var first = await h.Runner.ResolvePolicyApprovalAsync("MEMO-T1", card.DecisionId, Approve());

        var replay = await h.Runner.ResolvePolicyApprovalAsync("MEMO-T1", card.DecisionId, Approve());

        Assert.Equal(first.Decision.DecisionId, replay.Decision.DecisionId);
        Assert.Equal(first.Decision.Status, replay.Decision.Status);
        Assert.Equal(first.Activation, replay.Activation);
        Assert.Single(h.Compliance.Published);
    }
}

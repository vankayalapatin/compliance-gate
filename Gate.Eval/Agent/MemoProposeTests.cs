// Propose-round tests (scripted Spark, fake engine over real cores): the
// prompt carries memo plus pinned snapshot plus registry; the runner stamps
// the canonical hash, validates via the engine, and stores the record;
// engine-invalid proposals store as rejected and park Done.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoProposeTests
{
    [Fact]
    public async Task PromptCarriesMemoSnapshotAndRegistry()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.CapProposalJson());

        await h.Runner.ProposeAsync("MEMO-T1");

        var request = Assert.Single(h.Models.Requests);
        Assert.Contains("no more than 40% of critical battery cells", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("kbv9", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("hash9", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("BAT-9", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("origin-cap", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("Terry Owner", request.Prompt, StringComparison.Ordinal);
        foreach (var type in MemoRuleTypes.All)
        {
            Assert.Contains(type, request.Prompt, StringComparison.Ordinal);
        }

        Assert.Contains("JSON only", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("LoadKbSnapshot", h.Compliance.MemoCalls);
        Assert.Contains("ComputeProposalHash", h.Compliance.MemoCalls);
        Assert.Contains("ValidateProposal", h.Compliance.MemoCalls);
    }

    [Fact]
    public async Task ValidProposalIsHashedValidatedAndStored()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.CapProposalJson());

        var file = await h.Runner.ProposeAsync("MEMO-T1");

        var record = Assert.Single(file.Proposals);
        Assert.Equal("MEMO-T1-p1", record.Proposal.ProposalId);
        Assert.True(ProposalHasher.Verify(record.Proposal));
        Assert.True(record.Validation.Ok);
        Assert.Empty(record.Validation.Errors);
        Assert.Equal("Terry Owner", record.PolicyOwner);
        var tighten = Assert.Single(record.Validation.Conflicts);
        Assert.Equal("CORP-02", tighten.RuleId);
        Assert.Equal(RuleConflictKind.Tightens, tighten.Kind);
        Assert.Null(record.WhatIf);
        Assert.Empty(file.Decisions);
        var timeline = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        Assert.Contains(timeline.Events, e =>
            e.Summary.Contains("OriginShareCap", StringComparison.Ordinal)
            && e.Summary.Contains("CORP-02", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EngineInvalidProposalIsStoredAsRejectedAndParksDone()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.CapProposalJson(cap: "4.0"));

        var file = await h.Runner.ProposeAsync("MEMO-T1");

        var record = Assert.Single(file.Proposals);
        Assert.False(record.Validation.Ok);
        Assert.Contains(record.Validation.Errors, e => e.Contains("cap", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(file.Events, e => e.Summary.Contains("REJECTED", StringComparison.Ordinal));
        var (step, _) = MemoPicker.Pick(file);
        Assert.Equal(MemoNextStep.Done, step);
        var outcome = await h.Runner.AdvanceAsync("MEMO-T1");
        Assert.Equal(MemoNextStep.Done, outcome.Step);
        Assert.True(outcome.Complete);
        Assert.Equal(1, h.Models.CallCount);
    }

    [Fact]
    public async Task HallucinatedCitationIsRejectedByTheEngine()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.CapProposalJson().Replace(
            "kb:sourcingPolicies#origin-cap@kbv9", "kb:sourcingPolicies#nope@kbv9"));

        var file = await h.Runner.ProposeAsync("MEMO-T1");

        var record = Assert.Single(file.Proposals);
        Assert.False(record.Validation.Ok);
        Assert.Contains(record.Validation.Errors, e =>
            e.Contains("kb:sourcingPolicies#nope@kbv9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedCompletionTimelinesAndThrowsWithoutStoring()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText("{oops");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.ProposeAsync("MEMO-T1"));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
        var file = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        Assert.Empty(file.Proposals);
        Assert.Contains(file.Events, e =>
            e.Summary.Contains("rejected before validation", StringComparison.Ordinal));
        // Retryable: the next advance re-picks Propose.
        Assert.Equal(MemoNextStep.Propose, MemoPicker.Pick(file).Step);
    }

    [Fact]
    public async Task UnknownRuleTypeIsRejectedByTheEngine()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);
        h.Models.QueueText(MemoTestKit.CapProposalJson().Replace(
            "\"ruleType\": \"OriginShareCap\"", "\"ruleType\": \"VibesCap\""));

        var file = await h.Runner.ProposeAsync("MEMO-T1");

        Assert.False(Assert.Single(file.Proposals).Validation.Ok);
        Assert.Contains(Assert.Single(file.Proposals).Validation.Errors, e =>
            e.Contains("VibesCap", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SparkFailureIsNotedAndRethrown()
    {
        var erp = SeededErp.Create();
        var throwing = new ThrowingModelClient();
        var compliance = new RecordingComplianceEngine();
        var cacheDir = MemoTestKit.TempDir("gate-memo-test");
        var inboxDir = Path.Combine(MemoTestKit.TempDir("gate-memo-inbox"), "inbox");
        var runner = new MemoRunner(erp, throwing, compliance, cacheDir);
        var name = MemoTestKit.WriteInboxMemo(inboxDir, "MEMO-T1", "Cap.", "Cap the share at 40%.");
        await runner.IngestMemoAsync(inboxDir, name);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.AdvanceAsync("MEMO-T1"));

        var file = MemoStore.Load(cacheDir, "MEMO-T1")!;
        Assert.Empty(file.Proposals);
        Assert.Contains(file.Events, e =>
            e.Summary.Contains("failed", StringComparison.Ordinal));
        Assert.Equal(2, throwing.CallCount);
    }
}

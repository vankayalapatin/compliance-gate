// Rollback tests: the UI rollback action restores a prior pack version as a
// new version, stores the activation, and timelines it; refusals are loud.
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoRollbackTests
{
    private static async Task<MemoTestKit.Harness> PublishedAsync(string memoId = "MEMO-T1")
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h, memoId);
        h.Models.QueueText(MemoTestKit.CapProposalJson());
        await h.Runner.AdvanceAsync(memoId);
        var card = MemoStore.Load(h.CacheDir, memoId)!.Decisions
            .Single(d => d.Kind == DecisionKind.PolicyApproval);
        await h.Runner.ResolvePolicyApprovalAsync(memoId, card.DecisionId,
            new PolicyApprovalInput(true, "Terry Owner",
                DateTimeOffset.UtcNow.AddDays(30), RuleApplicability.ProspectiveOnly,
                null, null, null, null, null, ["MemoRollbackTests.PublishedAsync"]));
        return h;
    }

    [Fact]
    public async Task RollbackRestoresThePriorVersionAsANewVersion()
    {
        var h = await PublishedAsync();

        var activation = await h.Runner.RequestRollbackAsync(
            "MEMO-T1", "v2", "The 40% cap blocked every award path.", "Terry Owner");

        Assert.Equal("CORP", activation.PackId);
        Assert.Equal("CORP-02", activation.RuleId);
        Assert.Equal("v3", activation.PriorPackVersion);
        Assert.Equal("v4", activation.NewPackVersion);
        Assert.Equal("MEMO-T1", activation.MemoId);
        Assert.Equal("Terry Owner", activation.Approver);
        Assert.Contains("PublishRollback:CORP:v2", h.Compliance.MemoCalls);
        Assert.Contains("0.60", h.Compliance.PriorPacks["CORP"], StringComparison.Ordinal);
        var file = MemoStore.Load(h.CacheDir, "MEMO-T1")!;
        Assert.Equal(2, file.Activations.Count);
        Assert.Contains(file.Events, e =>
            e.Summary.Contains("rolled back to v2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RollbackHonorsEffectiveAndApplicabilityOverrides()
    {
        var h = await PublishedAsync();
        var effective = DateTimeOffset.UtcNow.AddDays(7);

        var activation = await h.Runner.RequestRollbackAsync(
            "MEMO-T1", "v2", "Too tight.", "Terry Owner",
            effectiveAt: effective, applicability: RuleApplicability.ProspectiveWithReview);

        Assert.Equal(effective, activation.EffectiveAt);
        Assert.Equal(RuleApplicability.ProspectiveWithReview, activation.Applicability);
    }

    [Fact]
    public async Task RollbackWithoutAPublicationIsRefused()
    {
        var h = MemoTestKit.Create();
        await MemoTestKit.IngestAsync(h);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.RequestRollbackAsync("MEMO-T1", "v2", "Why.", "Terry Owner"));

        Assert.Contains("no publication to roll back", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RollbackOfAnUnknownVersionIsRefused()
    {
        var h = await PublishedAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.RequestRollbackAsync("MEMO-T1", "v9.9", "Why.", "Terry Owner"));

        Assert.Contains("v9.9", ex.Message, StringComparison.Ordinal);
        Assert.Single(MemoStore.Load(h.CacheDir, "MEMO-T1")!.Activations);
    }

    [Fact]
    public async Task RollbackNeedsReasonAndApprover()
    {
        var h = await PublishedAsync();

        var noReason = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.RequestRollbackAsync("MEMO-T1", "v2", " ", "Terry Owner"));
        Assert.Contains("reason", noReason.Message, StringComparison.OrdinalIgnoreCase);

        var noApprover = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runner.RequestRollbackAsync("MEMO-T1", "v2", "Why.", " "));
        Assert.Contains("approver", noApprover.Message, StringComparison.OrdinalIgnoreCase);
    }
}

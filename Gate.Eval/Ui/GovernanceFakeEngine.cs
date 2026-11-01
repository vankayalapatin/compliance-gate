// Governance-lane fake engine: seam-only KB plus live-rule reads with a
// publish tap. Test-double data only: invented spaces, versions, and rule
// ids, never the demo numbers.
using Gate.Contracts;

namespace Gate.Eval.Ui;

internal sealed class GovernanceFakeEngine : IComplianceEngine
{
    public KbSnapshot Snapshot { get; set; }

    public Dictionary<string, KbSnapshot> SnapshotsByVersion { get; } = new(StringComparer.Ordinal);

    public List<KbSpace> Spaces { get; } = [];

    public List<LivePackRule> LiveRules { get; } = [];

    public List<(string BaseVersion, IReadOnlyList<KbEntryChange> Changes, string ApprovedBy,
        string Reason, string ActivationId, string? ApprovedFor)> Publishes { get; } = [];

    public Func<string, IReadOnlyList<KbEntryChange>, string, string, string, KbPublication>? PublishHook { get; set; }

    public GovernanceFakeEngine(KbSnapshot? snapshot = null)
    {
        Snapshot = snapshot ?? MemoFixtures.TestKb();
    }

    public Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
        VendorDto vendor, ItemDto item, IReadOnlyList<VendorDocumentDto> documents,
        ProposedAward award, IReadOnlyList<VolumeHistory> history, CancellationToken ct = default)
    {
        _ = vendor;
        _ = item;
        _ = documents;
        _ = award;
        _ = history;
        _ = ct;
        throw new NotSupportedException("GovernanceFakeEngine carries no vendor verdicts.");
    }

    public Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        PrePayState state, CancellationToken ct = default)
    {
        _ = state;
        _ = ct;
        throw new NotSupportedException("GovernanceFakeEngine carries no pre-pay verdicts.");
    }

    public Task<KbSnapshot> LoadKbSnapshotAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult(Snapshot);
    }

    public Task<KbSnapshot> LoadKbSnapshotAtAsync(string kbVersion, CancellationToken ct = default)
    {
        _ = ct;
        if (SnapshotsByVersion.TryGetValue(kbVersion, out var snapshot))
        {
            return Task.FromResult(snapshot);
        }

        if (string.Equals(kbVersion, Snapshot.KbVersion, StringComparison.Ordinal))
        {
            return Task.FromResult(Snapshot);
        }

        throw new InvalidOperationException($"Unknown KB version '{kbVersion}'.");
    }

    public Task<IReadOnlyList<KbSpace>> ListKbSpacesAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult<IReadOnlyList<KbSpace>>(Spaces);
    }

    public Task<IReadOnlyList<LivePackRule>> ListLivePackRulesAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.FromResult<IReadOnlyList<LivePackRule>>(LiveRules);
    }

    public Task<KbPublication> PublishKbUpdateAsync(
        string baseVersion,
        IReadOnlyList<KbEntryChange> changes,
        string approvedBy,
        string reason,
        string activationId,
        DateTimeOffset publishedAt,
        string? approvedFor = null,
        CancellationToken ct = default)
    {
        _ = ct;
        _ = publishedAt;
        Publishes.Add((baseVersion, changes, approvedBy, reason, activationId, approvedFor));
        if (PublishHook is not null)
        {
            return Task.FromResult(PublishHook(baseVersion, changes, approvedBy, reason, activationId));
        }

        throw new InvalidOperationException("Fake engine carries no publish hook.");
    }
}

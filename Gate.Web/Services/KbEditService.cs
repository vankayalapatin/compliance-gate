// KB add/edit publishes from the Governance space views. The acting user
// approves as the space owner: directly, or as their active dated delegate
// resolved from the ERP delegation window into ApprovedFor. Every refusal
// (unknown space, blank author or reason, non-owner, expired delegation,
// stale base, writer rejection, seam failure) surfaces with context.
//
// Delegated publishes flow through the seam: the resolved ApprovedFor
// (the space owner) travels in PublishKbUpdateAsync's approvedFor slot,
// so the writer can match the owner while checking no-self-approval
// against the acting approver.
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed record KbEditOutcome(
    bool Ok,
    string Message,
    string? NewVersion,
    string? Owner,
    string? ApprovedFor);

public sealed class KbEditService
{
    private readonly IComplianceEngine _compliance;
    private readonly IErpAdapter _erp;
    private readonly ILogger<KbEditService> _log;

    public KbEditService(IComplianceEngine compliance, IErpAdapter erp, ILogger<KbEditService> log)
    {
        _compliance = compliance;
        _erp = erp;
        _log = log;
    }

    /// <summary>Publishes one KB entry add or edit. Today is injectable so
    // tests pin the delegation window; null means the current UTC date.</summary>
    public async Task<KbEditOutcome> PublishAsync(
        string space,
        string section,
        string entryId,
        KbChangeKind kind,
        string entryJson,
        string reason,
        string actingUser,
        DateOnly? today = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actingUser))
        {
            return new KbEditOutcome(false,
                "Acting user is required; pick who approves before publishing.", null, null, null);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return new KbEditOutcome(false,
                "A reason is required; every KB version records why it changed.", null, null, null);
        }

        if (string.IsNullOrWhiteSpace(entryJson))
        {
            return new KbEditOutcome(false,
                "Entry content is required; add and edit both ship the full entry.", null, null, null);
        }

        IReadOnlyList<KbSpace> spaces;
        try
        {
            spaces = await _compliance.ListKbSpacesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "KB publish spaces lookup failed.");
            return new KbEditOutcome(false, $"KB spaces failed to load: {ex.Message}", null, null, null);
        }

        var match = spaces.FirstOrDefault(s =>
            string.Equals(s.Space, space, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return new KbEditOutcome(false,
                $"Unknown knowledge space '{space}'. Known spaces: {string.Join(", ", spaces.Select(s => s.Space))}.",
                null, null, null);
        }

        IReadOnlyList<DelegationDto> delegations;
        try
        {
            delegations = await _erp.GetDelegationsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "KB publish delegation lookup failed.");
            return new KbEditOutcome(false, $"ERP delegations failed to load: {ex.Message}", null, match.Owner, null);
        }

        var day = today ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var approval = GovernanceDelegation.TryResolve(
            match.Owner, actingUser.Trim(), delegations, day);
        if (approval is null)
        {
            _log.LogWarning(
                "KB publish by {Acting} refused: not the {Space} owner ({Owner}) or their active delegate.",
                actingUser, match.Space, match.Owner);
            return new KbEditOutcome(false,
                $"Refused: {actingUser.Trim()} is not the {match.Space} owner ({match.Owner}) " +
                "or their active dated delegate.",
                null, match.Owner, null);
        }

        KbSnapshot current;
        try
        {
            current = await _compliance.LoadKbSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "KB publish base snapshot failed to load.");
            return new KbEditOutcome(false, $"Current KB snapshot failed to load: {ex.Message}", null, match.Owner, null);
        }

        var now = DateTimeOffset.UtcNow;
        var change = new KbEntryChange(
            section, entryId, kind, entryJson, approval.ActingApprover, now);
        var activationId = $"KB-{current.KbVersion}-{section}-{entryId}-{now:yyyyMMddHHmmss}";
        try
        {
            var publication = await _compliance.PublishKbUpdateAsync(
                current.KbVersion, [change], approval.ActingApprover,
                reason.Trim(), activationId, now, approval.ApprovedFor, ct).ConfigureAwait(false);
            _log.LogInformation(
                "KB {Prior} -> {New} published by {By} for space {Space} ({Section}#{Entry}).",
                current.KbVersion, publication.Activation.KbVersion,
                approval.ActingApprover, match.Space, section, entryId);
            var approvedAs = approval.IsDirect
                ? $"approved by {approval.ActingApprover} as the {match.Space} owner"
                : $"approved by {approval.ActingApprover} for {match.Owner} (active delegate)";
            return new KbEditOutcome(true,
                $"Published {KbDisplay.Version(publication.Activation.KbVersion)} (prior {KbDisplay.Version(current.KbVersion)}): " +
                $"{section}#{entryId} {kind.ToString().ToLowerInvariant()} {approvedAs}.",
                publication.Activation.KbVersion, match.Owner, approval.ApprovedFor);
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex, "KB publish by {By} for space {Space} ({Section}#{Entry}) refused.",
                approval.ActingApprover, match.Space, section, entryId);
            return new KbEditOutcome(false, $"Publish refused: {ex.Message}", null, match.Owner, null);
        }
    }
}

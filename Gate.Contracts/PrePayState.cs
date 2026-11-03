// Pre-pay gate input (ELEC-04). Blocks while any compliance hold,
// quarantine, dock-pending lot, or open human review from the earlier
// gates is still open on the lot or shipment. The holds-only input
// under-read the rule; this state carries all four (see
// docs/DECISIONS.md 30 and 56).
namespace Gate.Contracts;

/// <summary>One still-open human review from the vendor or dock gate:
/// damage, illegible mark, or ASN-missing carton. The agent maps its pending
/// review Decisions to these rows when calling the pre-pay gate.</summary>
public sealed record OpenReview(
    string ReviewId,
    string RuleId,
    string CaseId,
    string? CartonId,
    string? LotRef,
    DateTimeOffset OpenedAt,
    string Reason);

public sealed record PrePayState(
    IReadOnlyList<HoldDto> OpenHolds,
    IReadOnlyList<string> QuarantinedLotRefs,
    IReadOnlyList<OpenReview> OpenReviews,
    IReadOnlyList<string> DockPendingLotRefs);

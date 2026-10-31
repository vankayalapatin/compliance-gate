// Compliance verdicts. C# decides, the model explains, every block cites
// a rule id plus a stored public source.
namespace Gate.Contracts;

public enum VerdictKind
{
    Pass,
    Block,
    Hold,
    HumanReview
}

public sealed record Verdict(
    string RuleId,
    VerdictKind Kind,
    string Reason,
    string Source);

/// <summary>Shared verdict rule ids both sides compare by constant, never
/// by mirrored literals. Compliance emits them; the agent filters on them.</summary>
public static class ComplianceVerdictIds
{
    /// <summary>ERP vendor-master block (blocked flag plus reason).</summary>
    public const string VendorBlock = "VENDOR-BLOCK";
}

public interface IComplianceEngine
{
    /// <summary>Evaluates a vendor for a proposed award. The engine computes
    /// every share itself as (country history + award) / (total history +
    /// award) from the trailing-12-month rows; callers never pass shares.
    /// Document packs (BAT-LI, ELEC) apply to critical parts only; for
    /// non-critical parts those rules return not-applicable passes so the
    /// straight-through path needs no pack documents.</summary>
    Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
        VendorDto vendor,
        ItemDto item,
        IReadOnlyList<VendorDocumentDto> documents,
        ProposedAward award,
        IReadOnlyList<VolumeHistory> history,
        CancellationToken ct = default);

    /// <summary>Pre-pay gate over holds plus quarantine plus open reviews
    /// (ELEC-04). Blocks unless all three are empty.</summary>
    Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        PrePayState state,
        CancellationToken ct = default);
}

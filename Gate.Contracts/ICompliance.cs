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

public interface IComplianceEngine
{
    /// <summary>Evaluates a vendor for a proposed award. The engine computes
    /// every share itself as (country history + award) / (total history +
    /// award) from the trailing-12-month rows; callers never pass shares.</summary>
    Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
        VendorDto vendor,
        IReadOnlyList<VendorDocumentDto> documents,
        ProposedAward award,
        IReadOnlyList<VolumeHistory> history,
        CancellationToken ct = default);

    Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        IReadOnlyList<HoldDto> openHolds,
        CancellationToken ct = default);
}

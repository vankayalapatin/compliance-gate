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
    Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
        VendorDto vendor,
        IReadOnlyList<VendorDocumentDto> documents,
        decimal volumeShare,
        CancellationToken ct = default);

    Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        IReadOnlyList<HoldDto> openHolds,
        CancellationToken ct = default);
}

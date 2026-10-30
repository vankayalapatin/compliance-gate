// Recording compliance engine: the real engine with a tap on the pre-pay
// gate. Records every PrePayState call and counts legacy holds-only calls
// (the runner must never make one).
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

internal sealed class RecordingComplianceEngine : IComplianceEngine
{
    private readonly ComplianceEngine _inner = new();

    public List<PrePayState> PrePayStates { get; } = new();

    public int HoldsOnlyCalls { get; private set; }

    public Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
        VendorDto vendor, ItemDto item, IReadOnlyList<VendorDocumentDto> documents,
        ProposedAward award, IReadOnlyList<VolumeHistory> history, CancellationToken ct = default) =>
        _inner.EvaluateVendorAsync(vendor, item, documents, award, history, ct);

    public Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        IReadOnlyList<HoldDto> openHolds, CancellationToken ct = default)
    {
        HoldsOnlyCalls++;
        return _inner.EvaluatePaymentAsync(openHolds, ct);
    }

    public Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        PrePayState state, CancellationToken ct = default)
    {
        PrePayStates.Add(state);
        return _inner.EvaluatePaymentAsync(state, ct);
    }
}

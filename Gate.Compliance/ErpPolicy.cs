// ERP control-point policy implementation. The ERP gathers its own state
// and calls one check per control point; this class runs the company rules
// over those inputs and returns pass or a cited refusal. Sourcing runs the
// vendor gate (CORP-02 computed for the award quantity on the line);
// payable runs ELEC-04 over open reviews (the whole payment waits on a
// review), while held-value release runs ELEC-04 over holds, quarantine,
// and open reviews. The ERP-block marker stays ERP-owned: a blocked vendor
// never reaches here.
using Gate.Contracts;

namespace Gate.Compliance;

public sealed class ErpPolicy : IErpPolicy
{
    private readonly ComplianceEngine _engine;

    public ErpPolicy(ComplianceEngine? engine = null)
    {
        _engine = engine ?? new ComplianceEngine();
    }

    public async Task<PolicyCheckResult> CheckSourcingAsync(
        SourcingCheckInput input, CancellationToken ct = default)
    {
        var vendor = new VendorDto(
            input.VendorId, input.VendorName, input.Country,
            false, null, input.PaymentTermsId);
        var item = new ItemDto(
            input.ItemId, input.ItemDescription, input.IsCritical, 0, string.Empty);
        CorpExceptionApproval? exception = input.ExceptionGrant is null
            ? null
            : new CorpExceptionApproval(
                input.ExceptionGrant.RuleId, input.ExceptionGrant.VendorId,
                input.ExceptionGrant.Approver, input.ExceptionGrant.At,
                input.ExceptionGrant.Reason);
        var award = new ProposedAward(input.ItemId, input.AwardQuantity, input.Country);
        var verdicts = await _engine.EvaluateVendorWithExceptionAsync(
            vendor, item, input.Documents, award, input.History, exception, ct, input.OpenOrders)
            .ConfigureAwait(false);
        var block = verdicts.FirstOrDefault(v =>
            v.Kind == VerdictKind.Block
            && !string.Equals(v.RuleId, ComplianceVerdictIds.VendorBlock, StringComparison.Ordinal));
        if (block is null)
        {
            return PolicyCheckResult.Pass();
        }

        var rule = _engine.FindRule(block.RuleId);
        return PolicyCheckResult.Block(new PolicyRefusal(
            block.RuleId, rule.Pack, rule.PackVersion, block.Reason));
    }

    public async Task<PolicyCheckResult> CheckPayableAsync(
        PayableCheckInput input, CancellationToken ct = default)
    {
        var verdicts = await _engine.EvaluatePayableAsync(
            new PrePayState(input.OpenHolds, input.QuarantinedLotRefs, input.OpenReviews), ct)
            .ConfigureAwait(false);
        return ToResult(verdicts);
    }

    public async Task<PolicyCheckResult> CheckReleasableAsync(
        PayableCheckInput input, CancellationToken ct = default)
    {
        var verdicts = await _engine.EvaluatePaymentAsync(
            new PrePayState(input.OpenHolds, input.QuarantinedLotRefs, input.OpenReviews), ct)
            .ConfigureAwait(false);
        return ToResult(verdicts);
    }

    private PolicyCheckResult ToResult(IReadOnlyList<Verdict> verdicts)
    {
        var block = verdicts.FirstOrDefault(v => v.Kind == VerdictKind.Block);
        if (block is null)
        {
            return PolicyCheckResult.Pass();
        }

        var rule = _engine.FindRule(block.RuleId);
        return PolicyCheckResult.Block(new PolicyRefusal(
            block.RuleId, rule.Pack, rule.PackVersion, block.Reason));
    }
}

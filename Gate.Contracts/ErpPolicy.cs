// ERP control-point policy checks. The ERP stays strict and simple: at each
// control point it gathers its own state, calls one check, and refuses with
// the rule id, pack version, and plain reason the check returns. The ERP
// itself carries no rules: this contract lives in Gate.Contracts, the checks
// are implemented compliance-side (Gate.Compliance), and the platform inputs
// (exception grants, open reviews) come from the agent platform through
// IErpPolicyContext. Wired in Gate.Web. People and agents get the same check.
namespace Gate.Contracts;

/// <summary>One logged rule exception granted for an award: rule, vendor,
// named approver, timestamp, and reason. Anything less still blocks.</summary>
public sealed record PolicyExceptionGrant(
    string RuleId,
    string VendorId,
    string Approver,
    DateTimeOffset At,
    string Reason)
{
    public bool IsValidFor(string ruleId, string vendorId) =>
        RuleId == ruleId
        && VendorId == vendorId
        && !string.IsNullOrWhiteSpace(Approver)
        && !string.IsNullOrWhiteSpace(Reason);
}

/// <summary>One policy refusal: the blocking rule id, its pack and pack
// version, and the plain reason. The ERP surfaces Display verbatim.</summary>
public sealed record PolicyRefusal(
    string RuleId,
    string Pack,
    string PackVersion,
    string Reason)
{
    public string Display => $"{RuleId} ({Pack} {PackVersion}): {Reason}";
}

public sealed record PolicyCheckResult(bool Allowed, PolicyRefusal? Refusal)
{
    public static PolicyCheckResult Pass() => new(true, null);

    public static PolicyCheckResult Block(PolicyRefusal refusal) => new(false, refusal);
}

/// <summary>Sourcing input for one award line. The ERP reads every field
// itself (vendor, item, documents, trailing volume) except the exception
// grant, which arrives from the platform context. CORP-02 is computed for
// AwardQuantity: the quantity on this PO line, not history alone.</summary>
public sealed record SourcingCheckInput(
    string VendorId,
    string VendorName,
    string Country,
    string PaymentTermsId,
    string ItemId,
    string ItemDescription,
    bool IsCritical,
    decimal AwardQuantity,
    IReadOnlyList<VendorDocumentDto> Documents,
    IReadOnlyList<VolumeHistory> History,
    PolicyExceptionGrant? ExceptionGrant);

/// <summary>Payable input for one payment or held-line release. The ERP reads
// holds and quarantine itself from the receipts behind Invoices; open
// reviews arrive from the platform context. Open reviews block the whole
// payment; holds and quarantine hold only their own value, which never
// enters a draft and is releasable only once its taint closes.</summary>
public sealed record PayableCheckInput(
    IReadOnlyList<InvoiceKey> Invoices,
    IReadOnlyList<HoldDto> OpenHolds,
    IReadOnlyList<string> QuarantinedLotRefs,
    IReadOnlyList<OpenReview> OpenReviews);

/// <summary>Well-known rule ids the ERP control points name. Policy seam
// keys (like document types), not business data.</summary>
public static class ErpPolicyRules
{
    public const string CorpOriginCap = "CORP-02";
}

/// <summary>Control-point policy checks. Deterministic C# over the inputs;
// the model only ever explains a refusal it is shown.</summary>
public interface IErpPolicy
{
    Task<PolicyCheckResult> CheckSourcingAsync(SourcingCheckInput input, CancellationToken ct = default);

    /// <summary>Whole-payment gate for payment draft, approve, and post:
    /// open reviews block the whole payment; holds and quarantine hold
    /// only their own value (held lines never enter a draft).</summary>
    Task<PolicyCheckResult> CheckPayableAsync(PayableCheckInput input, CancellationToken ct = default);

    /// <summary>Held-value gate for held-line release: any quarantine, open
    /// hold, or open review on the receipt refuses the release.</summary>
    Task<PolicyCheckResult> CheckReleasableAsync(PayableCheckInput input, CancellationToken ct = default);
}

/// <summary>Platform-side inputs the ERP cannot read itself: logged rule
// exceptions granted on case decisions, and open dock/damage reviews on
// case decisions. Implemented by the agent platform, wired in Gate.Web.
// A missing grant or an empty review list is a valid answer, never an error.</summary>
public interface IErpPolicyContext
{
    Task<PolicyExceptionGrant?> FindExceptionGrantAsync(
        string ruleId, string vendorId, string? prNumber, CancellationToken ct = default);

    Task<IReadOnlyList<OpenReview>> FindOpenReviewsAsync(
        IReadOnlyList<InvoiceKey> invoices, CancellationToken ct = default);
}

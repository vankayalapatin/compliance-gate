// Agent-side case store shapes. Cases live outside ERP (agent-side), one per
// sales-order shortfall item, linked to the SO plus its PR/PO/ASN/receipt/
// invoice/payment chain. The agent picks its next tool from case state; the
// UI renders Decisions, Cases, and Audit from these same DTOs.
// No business values here: ids, quantities, limits, and names arrive at runtime.
namespace Gate.Contracts;

public enum CaseStage
{
    Created,
    ShortfallComputed,
    PrSubmitted,
    PrApproved,
    VendorScreened,
    PoPlaced,
    PoAcknowledged,
    AsnRecorded,
    ReceiptPosted,
    DockChecked,
    InvoiceMatched,
    PaymentRequested,
    PaymentPartial,
    Resolved,
    Closed,
    Cancelled
}

/// <summary>One shortfall-item chain: SO + item plus its document refs and
/// current block. Quantities stay on the ERP documents; this is identity,
/// stage, links, and block summary only.</summary>
public sealed record Case(
    string CaseId,
    string SoNumber,
    string ItemId,
    string ScenarioName,
    CaseStage Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PrNumber,
    string? PoNumber,
    string? AsnNumber,
    string? ReceiptNumber,
    IReadOnlyList<InvoiceKey> Invoices,
    IReadOnlyList<string> PaymentNumbers,
    string? BlockedByRuleId,
    string? BlockedReason);

public enum CaseEventKind
{
    OrderReceived,
    ShortfallComputed,
    PrCreated,
    PrSubmitted,
    PrApproved,
    PrRejected,
    VendorScreened,
    VendorBlocked,
    PoCreated,
    PoApproved,
    VendorAcknowledged,
    AsnRecorded,
    ReceiptPosted,
    DockEvaluated,
    HoldSet,
    HoldReleased,
    ReviewOpened,
    ReviewClosed,
    InvoiceCreated,
    InvoiceMatched,
    PaymentRequested,
    PaymentPosted,
    CustomerNotified,
    Note
}

/// <summary>One append-only timeline entry. Actor is a user id, vendor id, or
/// RecordSource value. EvidenceRef points at a photo, model-cache entry, or
/// explanation; the payload itself stays in its own store.</summary>
public sealed record CaseEvent(
    string EventId,
    string CaseId,
    DateTimeOffset Timestamp,
    string Actor,
    CaseEventKind Kind,
    string Summary,
    string? RuleId,
    string? DocumentType,
    string? DocumentNumber,
    string? EvidenceRef,
    decimal? Amount,
    string? Currency);

public enum DecisionKind
{
    PrApproval,
    PoApproval,
    InvoiceApproval,
    PaymentApproval,
    DockReview,
    DamageReview,
    ExceptionApproval,
    /// <summary>AP clerk or finance verifies a supplier-requested bank change
    /// out of band (VerifyVendorBankAsync). The master never auto-updates.</summary>
    BankVerification,
    /// <summary>AP clerk owns a held invoice exception: missing vendor or PO,
    /// price hold, or other AP catch. Resolve or reject.</summary>
    ApReview
}

public enum DecisionStatus
{
    Pending,
    Approved,
    Rejected
}

/// <summary>One human decision card. ERP approvals link via DocumentType plus
/// DocumentNumber; dock and damage reviews cite RuleId plus evidence refs.
/// All money carries its currency.</summary>
public sealed record Decision(
    string DecisionId,
    string CaseId,
    DecisionKind Kind,
    string Title,
    DecisionStatus Status,
    string RequestedTo,
    DateTimeOffset RequestedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DocumentType,
    string? DocumentNumber,
    decimal? Amount,
    string? Currency,
    string? RuleId,
    string? Reason,
    IReadOnlyList<string> EvidenceRefs,
    string? Comment);

/// <summary>One case file: the JSON root the agent writes and the UI reads.
/// Both sides serialize with web defaults; one file per case.</summary>
public sealed record CaseFile(
    Case Case,
    IReadOnlyList<CaseEvent> Events,
    IReadOnlyList<Decision> Decisions);

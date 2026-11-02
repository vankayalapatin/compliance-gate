// Canonical plain-DTO P2P model. D365/NetSuite map to these same shapes.
// Money is decimal with a currency code on the header. No floats.
// Write methods take only what the caller actually knows; ERP reads limits,
// amounts, due dates, statuses, vouchers, and bank snapshots itself.
namespace Gate.Contracts;

/// <summary>Identifies records created by the p2p agent (native audit column, not a foreign key).</summary>
public static class RecordSource
{
    public const string P2pAgent = "p2p-agent";
    public const string Human = "human";
    public const string Erp = "erp";
    public const string Vendor = "vendor";
}

public enum SalesOrderStatus
{
    Created,
    Confirmed,
    PartiallyDelivered,
    Delivered,
    Invoiced,
    Closed,
    Cancelled
}

public enum PrStatus
{
    Draft,
    Submitted,
    Approved,
    Rejected,
    Closed,
    Cancelled
}

public enum PoApprovalStatus
{
    Draft,
    InReview,
    Approved,
    Confirmed,
    Cancelled
}

public enum PoDocStatus
{
    Open,
    PartiallyReceived,
    Received,
    PartiallyInvoiced,
    Invoiced,
    Closed,
    Cancelled
}

public enum ReceiptStatus
{
    Posted
}

public enum InvoiceStatus
{
    Received,
    Matched,
    Approved,
    Posted,
    PartiallyPaid,
    Paid,
    Closed
}

public enum PaymentStatus
{
    Draft,
    Submitted,
    Approved,
    Posted
}

public enum HoldStatus
{
    Set,
    Released
}

public enum LineDisposition
{
    Accepted,
    Held
}

public enum InventoryStatus
{
    Available,
    Quarantined,
    Rejected,
    Consumed
}

public sealed record ItemDto(
    string ItemId,
    string Description,
    bool IsCritical,
    int UnitsPerCarton,
    string Unit);

public sealed record PaymentTermsDto(
    string TermsId,
    int NetDays);

public sealed record VendorBankDto(
    string VendorId,
    string AccountRef,
    bool Verified,
    DateTimeOffset? VerifiedAt);

public sealed record UserDto(
    string UserId,
    string Role,
    decimal SigningLimit,
    string LimitCurrency);

public sealed record DelegationDto(
    string FromUser,
    string ToUser,
    DateOnly Start,
    DateOnly End);

public sealed record VendorDto(
    string VendorId,
    string Name,
    string CountryOfOrigin,
    bool IsBlocked,
    string? BlockReason,
    string PaymentTermsId,
    /// <summary>Vendor contact email from the master. The agent sends
    /// invoice queries here; a missing address blocks sending.</summary>
    string? Email = null);

/// <summary>One routed-but-unacted approval: a submitted document waiting
/// on its approver. Seeded decisions project 1:1 from these rows.</summary>
public sealed record PendingApprovalDto(
    string DocumentType,
    string DocumentNumber,
    string RoutedTo,
    string SubmittedBy,
    decimal DocumentAmount);

public sealed record VendorDocumentDto(
    string VendorId,
    string Kind,
    string Reference,
    bool Present);

/// <summary>The proposed lot handed to vendor evaluation: what part, how
/// many units, and which country the award would go to.</summary>
public sealed record ProposedAward(
    string ItemId,
    decimal Quantity,
    string Country);

/// <summary>One row of trailing-12-month posted received volume in cells.
/// Raw history only; shares are always computed from these rows, never stored.
/// The read returns one row per posted receipt line.</summary>
public sealed record VolumeHistory(
    string Source,
    string Country,
    decimal Cells);

public sealed record PurchaseRequisitionLine(
    string ItemId,
    decimal Quantity,
    string Unit);

public sealed record PurchaseRequisition(
    string PrNumber,
    PrStatus Status,
    string Requester,
    string Currency,
    decimal EstimatedAmount,
    string Source,
    string CreatedBy,
    string IdempotencyKey,
    IReadOnlyList<PurchaseRequisitionLine> Lines,
    DateTimeOffset? CreatedAt = null,
    string? ApprovedBy = null,
    DateTimeOffset? ApprovedAt = null);

public sealed record PurchaseOrderLine(
    string PoLineRef,
    int LineNumber,
    string ItemId,
    decimal QtyOrdered,
    decimal QtyReceived,
    decimal QtyInvoiced,
    decimal QtyQuarantined,
    decimal QtyRejected,
    decimal UnitPrice,
    decimal LineAmount);

public sealed record PurchaseOrder(
    string PoNumber,
    string PrNumber,
    string VendorId,
    string PaymentTermsId,
    PoApprovalStatus ApprovalStatus,
    PoDocStatus DocStatus,
    string Currency,
    string Source,
    string CreatedBy,
    string IdempotencyKey,
    IReadOnlyList<PurchaseOrderLine> Lines,
    DateTimeOffset? CreatedAt = null,
    string? ApprovedBy = null,
    DateTimeOffset? ApprovedAt = null);

public sealed record VendorConfirmLine(
    string PoLineRef,
    decimal ConfirmedQty,
    string? BackorderNote);

public sealed record VendorConfirmation(
    string PoNumber,
    DateOnly ConfirmDate,
    string Source,
    string CreatedBy,
    string IdempotencyKey,
    IReadOnlyList<VendorConfirmLine> Lines);

public sealed record AsnCarton(
    string CartonId,
    string ItemId,
    int UnitsPerCarton,
    string LotRef);

public sealed record AsnDto(
    string AsnNumber,
    string PoNumber,
    string Source,
    string CreatedBy,
    string IdempotencyKey,
    IReadOnlyList<AsnCarton> Cartons);

public sealed record ReceiptLine(
    string PoLineRef,
    decimal QtyReceived,
    decimal QtyQuarantined,
    decimal QtyRejected,
    string LotRef,
    string? ConditionNote);

public sealed record ShipmentReceipt(
    string ReceiptNumber,
    string PoNumber,
    string SupplierPackingSlipNumber,
    ReceiptStatus Status,
    string Source,
    string CreatedBy,
    string IdempotencyKey,
    IReadOnlyList<ReceiptLine> Lines,
    DateTimeOffset? CreatedAt = null,
    string? PostedBy = null,
    DateTimeOffset? PostedAt = null,
    DateOnly? PostingDate = null);

public sealed record InvoiceLineInput(
    string? PoLineRef,
    decimal Quantity,
    decimal UnitPrice);

public sealed record VendorInvoiceLine(
    string? PoLineRef,
    decimal Quantity,
    decimal UnitPrice,
    LineDisposition Disposition,
    string? HoldReason);

public sealed record LineMatchResult(
    string? PoLineRef,
    bool Matched,
    string Message);

public sealed record InvoiceKey(
    string VendorId,
    string SupplierInvoiceNumber);

public sealed record VendorInvoice(
    string SupplierInvoiceNumber,
    string VendorId,
    string? PoNumber,
    InvoiceStatus Status,
    string? Voucher,
    string Currency,
    string Source,
    string CreatedBy,
    string IdempotencyKey,
    IReadOnlyList<VendorInvoiceLine> Lines,
    IReadOnlyList<LineMatchResult> MatchResults,
    DateTimeOffset? CreatedAt = null,
    string? ApprovedBy = null,
    DateTimeOffset? ApprovedAt = null,
    string? PostedBy = null,
    DateTimeOffset? PostedAt = null,
    DateOnly? DueDate = null);

public sealed record PaymentLine(
    string VendorId,
    string SupplierInvoiceNumber,
    decimal Amount);

public sealed record VendorBankSnapshot(
    string AccountRef,
    bool Verified);

public sealed record Payment(
    string PaymentNumber,
    PaymentStatus Status,
    string Currency,
    VendorBankSnapshot PayeeBank,
    DateOnly DueDate,
    string Source,
    string CreatedBy,
    string IdempotencyKey,
    IReadOnlyList<PaymentLine> Lines,
    DateTimeOffset? CreatedAt = null,
    string? ApprovedBy = null,
    DateTimeOffset? ApprovedAt = null,
    string? PostedBy = null,
    DateTimeOffset? PostedAt = null);

public sealed record Settlement(
    string VendorId,
    string SupplierInvoiceNumber,
    string PaymentNumber,
    decimal SettledAmount,
    decimal RemainingBalance);

public sealed record HeldRelease(
    string VendorId,
    string SupplierInvoiceNumber,
    string? PoLineRef,
    decimal Quantity,
    string ReleasedBy,
    DateTimeOffset ReleasedAt);

/// <summary>ERP-native hold: reason code plus free text. Rule id and case link
/// travel in the adapter payload, never as ERP foreign keys. Document, line,
/// lot, and carton references name what the hold blocks.</summary>
public sealed record HoldDto(
    string HoldId,
    string ReasonCode,
    string Note,
    string? RuleId,
    string CaseId,
    string SetBy,
    DateTimeOffset SetAt,
    HoldStatus Status,
    string? DocumentType,
    string? DocumentNumber,
    string? PoLineRef,
    string? LotRef,
    string? CartonId);

public sealed record HoldRelease(
    string HoldId,
    string ReleasedBy,
    DateTimeOffset ReleasedAt,
    string? ApprovalRef);

public sealed record ApprovalDto(
    string DocumentType,
    string DocumentNumber,
    string RoutedTo,
    string? ActedBy,
    bool ActingAsDelegate,
    DateTimeOffset Timestamp,
    bool Approved,
    string? Comment,
    decimal DocumentAmount,
    decimal LimitChecked);

public sealed record InventoryTransaction(
    string ItemId,
    string ReceiptNumber,
    string LotRef,
    InventoryStatus Status,
    decimal Quantity);

public sealed record SalesOrder(
    string SoNumber,
    SalesOrderStatus Status,
    string Currency,
    IReadOnlyList<SalesOrderLine> Lines);

public sealed record SalesOrderLine(
    string ItemId,
    decimal Quantity);

public sealed record BomLine(
    string ComponentItemId,
    decimal QtyPer);

/// <summary>ERP-computed consequence preview for one pending decision:
/// what approval would post, the due date, the payment run it joins, who
/// approves next, and what stays held with its value. Read-only.</summary>
public sealed record ConsequencePreview(
    string DocumentType,
    string DocumentNumber,
    IReadOnlyList<string> Postings,
    DateOnly? DueDate,
    string? PaymentRun,
    string? NextApprover,
    decimal HeldValue,
    string? HeldReason,
    string? Currency);

/// <summary>Company identity for headers, addresses, and sender context.
/// The software is ComplianceGate; this is the company running it.</summary>
public sealed record CompanyInfoDto(
    string EntityCode,
    string LegalName,
    string Currency,
    string BillToAddress,
    string ShipToAddress,
    string SenderEmail);

public sealed record SiteDto(
    string SiteId,
    string Name,
    string Address,
    IReadOnlyList<string> Capabilities);

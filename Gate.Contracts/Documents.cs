// Canonical plain-DTO P2P model. D365/NetSuite map to these same shapes.
// Money is decimal with a currency code on the header. No floats.
namespace Gate.Contracts;

/// <summary>Identifies records created by the p2p agent (native audit column, not a foreign key).</summary>
public static class RecordSource
{
    public const string P2pAgent = "p2p-agent";
    public const string Human = "human";
    public const string Erp = "erp";
}

public enum DocStatus
{
    Draft,
    Submitted,
    Approved,
    Posted,
    Closed,
    Cancelled
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

public sealed record VendorDto(
    string VendorId,
    string Name,
    string CountryOfOrigin,
    bool IsBlocked,
    string? BlockReason);

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
/// Raw history only; shares are always computed from these rows, never stored.</summary>
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
    DocStatus Status,
    string Requester,
    string Source,
    IReadOnlyList<PurchaseRequisitionLine> Lines);

public sealed record PurchaseOrderLine(
    int LineNumber,
    string ItemId,
    decimal QtyOrdered,
    decimal QtyReceived,
    decimal QtyInvoiced,
    decimal QtyQuarantined,
    decimal UnitPrice,
    string PoLineRef);

public sealed record PurchaseOrder(
    string PoNumber,
    string PrNumber,
    string VendorId,
    DocStatus Status,
    string Currency,
    string Source,
    IReadOnlyList<PurchaseOrderLine> Lines);

public sealed record ReceiptLine(
    string PoLineRef,
    decimal QtyReceived,
    decimal QtyQuarantined,
    string Condition);

public sealed record ShipmentReceipt(
    string ReceiptNumber,
    string PoNumber,
    DocStatus Status,
    string Source,
    IReadOnlyList<ReceiptLine> Lines);

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

public sealed record VendorInvoice(
    string InvoiceNumber,
    string VendorId,
    string? PoNumber,
    DocStatus Status,
    string Currency,
    string Source,
    IReadOnlyList<VendorInvoiceLine> Lines,
    IReadOnlyList<LineMatchResult> MatchResults);

public sealed record PaymentLine(
    string InvoiceNumber,
    decimal Amount);

public sealed record Payment(
    string PaymentNumber,
    DocStatus Status,
    string Currency,
    string Source,
    IReadOnlyList<PaymentLine> Lines);

/// <summary>ERP-native hold: reason code plus free text. Rule id and case link
/// travel in the adapter payload, never as ERP foreign keys.</summary>
public sealed record HoldDto(
    string HoldId,
    string ReasonCode,
    string Note,
    string? RuleId,
    string CaseId,
    string SetBy,
    DateTimeOffset SetAt);

public sealed record ApprovalDto(
    string DocumentType,
    string DocumentNumber,
    string RoutedTo,
    string? ActedBy,
    bool ActingAsDelegate,
    DateTimeOffset Timestamp,
    bool Approved,
    string? Comment,
    decimal LimitChecked);

public sealed record InventoryLot(
    string ItemId,
    string ReceiptLineRef,
    InventoryStatus Status,
    decimal Quantity);

public sealed record SalesOrder(
    string SoNumber,
    DocStatus Status,
    IReadOnlyList<SalesOrderLine> Lines);

public sealed record SalesOrderLine(
    string ItemId,
    decimal Quantity);

public sealed record BomLine(
    string ComponentItemId,
    decimal QtyPer);

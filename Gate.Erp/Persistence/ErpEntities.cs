// Mini-ERP tables, D365 P2P mirrors with simple names. Design: docs/ERP-DESIGN.md.
// Scalar columns only (string/int/long/decimal/bool): dates and enums are
// stored as text and converted in the adapter, so the model needs no value
// converters and the migration mirrors it column for column. No navigation
// properties; journals are queried by document keys. No business values here.
namespace Gate.Erp;

public sealed class ConfigRow
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class SequenceRow
{
    public string Name { get; set; } = string.Empty;
    public long Next { get; set; }
}

public sealed class IdempotencyRow
{
    public string Key { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

public sealed class ItemRow
{
    public string ItemId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCritical { get; set; }
    public int UnitsPerCarton { get; set; }
    public string Unit { get; set; } = string.Empty;
}

public sealed class SalesOrderRow
{
    public string SoNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
}

public sealed class SalesOrderLineRow
{
    public string SoNumber { get; set; } = string.Empty;
    public int LineNo { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
}

public sealed class BomRow
{
    public string ItemId { get; set; } = string.Empty;
    public string ComponentItemId { get; set; } = string.Empty;
    public decimal QtyPer { get; set; }
}

public sealed class InventoryRow
{
    public string RowId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string ReceiptRef { get; set; } = string.Empty;
    public string LotRef { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
}

public sealed class VendorRow
{
    public string VendorId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CountryOfOrigin { get; set; } = string.Empty;
    public bool IsBlocked { get; set; }
    public string? BlockReason { get; set; }
    public string PaymentTermsId { get; set; } = string.Empty;
}

public sealed class VendorDocumentRow
{
    public string RowId { get; set; } = string.Empty;
    public string VendorId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Reference { get; set; } = string.Empty;
    public bool Present { get; set; }
}

public sealed class PaymentTermsRow
{
    public string TermsId { get; set; } = string.Empty;
    public int NetDays { get; set; }
}

public sealed class VendorBankRow
{
    public string VendorId { get; set; } = string.Empty;
    public string AccountRef { get; set; } = string.Empty;
    public bool Verified { get; set; }
    public string? VerifiedAt { get; set; }
}

public sealed class VendorPriceRow
{
    public string VendorId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
}

public sealed class LastPriceRow
{
    public string ItemId { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
}

public sealed class LegacyShareRow
{
    public string VendorId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public decimal Share { get; set; }
}

public sealed class UserRow
{
    public string UserId { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public decimal SigningLimit { get; set; }
    public string LimitCurrency { get; set; } = string.Empty;
}

public sealed class DelegationRow
{
    public string RowId { get; set; } = string.Empty;
    public string FromUser { get; set; } = string.Empty;
    public string ToUser { get; set; } = string.Empty;
    public string Start { get; set; } = string.Empty;
    public string End { get; set; } = string.Empty;
}

public sealed class VolumeSeedRow
{
    public string RowId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public decimal Cells { get; set; }
}

public sealed class PrRow
{
    public string PrNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Requester { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public decimal EstimatedAmount { get; set; }
    public string Source { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class PrLineRow
{
    public string PrNumber { get; set; } = string.Empty;
    public int LineNo { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;
}

public sealed class PoRow
{
    public string PoNumber { get; set; } = string.Empty;
    public string PrNumber { get; set; } = string.Empty;
    public string VendorId { get; set; } = string.Empty;
    public string PaymentTermsId { get; set; } = string.Empty;
    public string ApprovalStatus { get; set; } = string.Empty;
    public string DocStatus { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class PoLineRow
{
    public string PoNumber { get; set; } = string.Empty;
    public string PoLineRef { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string ItemId { get; set; } = string.Empty;
    public decimal QtyOrdered { get; set; }
    public decimal QtyReceived { get; set; }
    public decimal QtyInvoiced { get; set; }
    public decimal QtyQuarantined { get; set; }
    public decimal QtyRejected { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineAmount { get; set; }
}

public sealed class PoNoteRow
{
    public string RowId { get; set; } = string.Empty;
    public string PoNumber { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

public sealed class ConfirmationRow
{
    public string PoNumber { get; set; } = string.Empty;
    public string ConfirmDate { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class ConfirmationLineRow
{
    public string PoNumber { get; set; } = string.Empty;
    public string PoLineRef { get; set; } = string.Empty;
    public decimal ConfirmedQty { get; set; }
    public string? BackorderNote { get; set; }
}

public sealed class AsnRow
{
    public string AsnNumber { get; set; } = string.Empty;
    public string PoNumber { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class AsnCartonRow
{
    public string CartonId { get; set; } = string.Empty;
    public string AsnNumber { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public int UnitsPerCarton { get; set; }
    public string LotRef { get; set; } = string.Empty;
}

public sealed class ReceiptRow
{
    public string ReceiptNumber { get; set; } = string.Empty;
    public string PoNumber { get; set; } = string.Empty;
    public string SupplierPackingSlipNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class ReceiptLineRow
{
    public string RowId { get; set; } = string.Empty;
    public string ReceiptNumber { get; set; } = string.Empty;
    public string PoLineRef { get; set; } = string.Empty;
    public decimal QtyReceived { get; set; }
    public decimal QtyQuarantined { get; set; }
    public decimal QtyRejected { get; set; }
    public string LotRef { get; set; } = string.Empty;
    public string? ConditionNote { get; set; }
}

public sealed class LotMapRow
{
    public string LotRef { get; set; } = string.Empty;
    public string PoNumber { get; set; } = string.Empty;
    public string PoLineRef { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
}

public sealed class HoldRow
{
    public string HoldId { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string? RuleId { get; set; }
    public string CaseId { get; set; } = string.Empty;
    public string SetBy { get; set; } = string.Empty;
    public string SetAt { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    public string? DocumentNumber { get; set; }
    public string? PoLineRef { get; set; }
    public string? LotRef { get; set; }
    public string? CartonId { get; set; }
}

public sealed class HoldReleaseRow
{
    public string RowId { get; set; } = string.Empty;
    public string HoldId { get; set; } = string.Empty;
    public string ReleasedBy { get; set; } = string.Empty;
    public string ReleasedAt { get; set; } = string.Empty;
    public string? ApprovalRef { get; set; }
}

public sealed class InvoiceRow
{
    public string VendorId { get; set; } = string.Empty;
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    public string? PoNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Voucher { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? PostedAt { get; set; }
}

public sealed class InvoiceLineRow
{
    public string RowId { get; set; } = string.Empty;
    public string VendorId { get; set; } = string.Empty;
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    public string? PoLineRef { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public string Disposition { get; set; } = string.Empty;
    public string? HoldReason { get; set; }
}

public sealed class MatchResultRow
{
    public string RowId { get; set; } = string.Empty;
    public string VendorId { get; set; } = string.Empty;
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    public string? PoLineRef { get; set; }
    public bool Matched { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class HeldReleaseRow
{
    public string RowId { get; set; } = string.Empty;
    public string VendorId { get; set; } = string.Empty;
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    public string? PoLineRef { get; set; }
    public decimal Quantity { get; set; }
    public string ReleasedBy { get; set; } = string.Empty;
    public string ReleasedAt { get; set; } = string.Empty;
}

public sealed class PaymentRow
{
    public string PaymentNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string PayeeAccountRef { get; set; } = string.Empty;
    public bool PayeeVerified { get; set; }
    public string DueDate { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
}

public sealed class PaymentLineRow
{
    public string RowId { get; set; } = string.Empty;
    public string PaymentNumber { get; set; } = string.Empty;
    public string VendorId { get; set; } = string.Empty;
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed class SettlementRow
{
    public string RowId { get; set; } = string.Empty;
    public string VendorId { get; set; } = string.Empty;
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    public string PaymentNumber { get; set; } = string.Empty;
    public decimal SettledAmount { get; set; }
    public decimal RemainingBalance { get; set; }
}

public sealed class PaidAmountRow
{
    public string VendorId { get; set; } = string.Empty;
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed class ApprovalRow
{
    public string RowId { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string RoutedTo { get; set; } = string.Empty;
    public string? ActedBy { get; set; }
    public bool ActingAsDelegate { get; set; }
    public string Timestamp { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public string? Comment { get; set; }
    public decimal DocumentAmount { get; set; }
    public decimal LimitChecked { get; set; }
}

public sealed class RequesterRow
{
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string Requester { get; set; } = string.Empty;
}

public sealed class RoutedToRow
{
    public string DocumentType { get; set; } = string.Empty;
    public string DocumentNumber { get; set; } = string.Empty;
    public string RoutedTo { get; set; } = string.Empty;
}

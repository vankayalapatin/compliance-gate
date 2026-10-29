// Data-only ERP adapter. Reads return raw ERP data; explosion, shortfall
// math, and invoice matching run in the agent. Screening snapshots live
// compliance-side, never behind this interface.
namespace Gate.Contracts;

public interface IErpAdapter
{
    // Reads (raw ERP data only).
    Task<SalesOrder?> GetSalesOrderAsync(string soNumber, CancellationToken ct = default);
    Task<IReadOnlyList<BomLine>> GetBomAsync(string itemId, CancellationToken ct = default);
    Task<decimal> GetStockAsync(string itemId, CancellationToken ct = default);
    Task<IReadOnlyList<PurchaseOrder>> GetOpenPurchaseOrdersAsync(string itemId, CancellationToken ct = default);
    Task<IReadOnlyList<VendorDto>> GetVendorsAsync(string itemId, CancellationToken ct = default);
    Task<IReadOnlyList<VendorDocumentDto>> GetVendorDocumentsAsync(string vendorId, CancellationToken ct = default);
    Task<string?> GetVendorCountryAsync(string vendorId, CancellationToken ct = default);
    /// <summary>Legacy precomputed per-vendor share. New paths read raw
    /// history below and let compliance compute; kept for interface stability.</summary>
    Task<decimal> GetVendorVolumeShareAsync(string vendorId, string itemId, CancellationToken ct = default);
    /// <summary>Raw trailing-12-month posted received volume in cells for an
    /// item, one row per source. Shares are computed from these rows.</summary>
    Task<IReadOnlyList<VolumeHistory>> GetVolumeHistoryAsync(string itemId, CancellationToken ct = default);
    Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken ct = default);
    Task<ShipmentReceipt?> GetReceiptAsync(string receiptNumber, CancellationToken ct = default);
    Task<VendorInvoice?> GetInvoiceAsync(string invoiceNumber, CancellationToken ct = default);
    Task<IReadOnlyList<ApprovalDto>> GetApprovalsAsync(string documentType, string documentNumber, CancellationToken ct = default);

    // Writes (agent submits into ERP workflows and waits for outcomes).
    Task<PurchaseRequisition> CreatePrAsync(string requester, string source, IReadOnlyList<PurchaseRequisitionLine> lines, CancellationToken ct = default);
    Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default);
    Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, CancellationToken ct = default);
    Task RecordApprovalAsync(ApprovalDto approval, CancellationToken ct = default);
    Task<PurchaseOrder> CreatePoAsync(string prNumber, string vendorId, string currency, string source, CancellationToken ct = default);
    Task UpdateSalesOrderStatusAsync(string soNumber, DocStatus status, CancellationToken ct = default);
    Task BlockVendorAsync(string vendorId, string reason, CancellationToken ct = default);
    Task AddPoNoteAsync(string poNumber, string note, CancellationToken ct = default);
    Task<ShipmentReceipt> PostReceiptAsync(string poNumber, string source, IReadOnlyList<ReceiptLine> lines, CancellationToken ct = default);
    Task<HoldDto> SetHoldAsync(string reasonCode, string note, string? ruleId, string caseId, string setBy, CancellationToken ct = default);
    Task ReleaseHoldAsync(string holdId, string releasedBy, CancellationToken ct = default);
    Task<VendorInvoice> CreateInvoiceAsync(VendorInvoice invoice, CancellationToken ct = default);
    Task RecordMatchResultAsync(string invoiceNumber, IReadOnlyList<LineMatchResult> results, CancellationToken ct = default);
    Task<Payment> CreatePaymentAsync(string currency, string source, IReadOnlyList<PaymentLine> lines, CancellationToken ct = default);
}

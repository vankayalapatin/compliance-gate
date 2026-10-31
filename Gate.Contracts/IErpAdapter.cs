// Data-only ERP adapter. Reads return raw ERP data; explosion, shortfall
// math, and invoice matching run in the agent. Screening snapshots live
// compliance-side, never behind this interface.
//
// Write-method rule: methods take only what the caller actually knows
// (intent references, supplier values, audit actor, idempotency key).
// ERP reads limits, document amounts, due dates, statuses, vouchers,
// bank snapshots, and delegate flags itself and records them.
namespace Gate.Contracts;

public interface IErpAdapter
{
    // Reads (raw ERP data only).
    Task<SalesOrder?> GetSalesOrderAsync(string soNumber, CancellationToken ct = default);
    Task<IReadOnlyList<BomLine>> GetBomAsync(string itemId, CancellationToken ct = default);
    Task<decimal> GetStockAsync(string itemId, CancellationToken ct = default);
    Task<IReadOnlyList<PurchaseOrder>> GetOpenPurchaseOrdersAsync(string itemId, CancellationToken ct = default);
    /// <summary>Pipeline list queries for what-if gather: every doc in an
    /// open state, header plus lines. Draft POs are approval Draft/InReview;
    /// unreceived ASNs have no receipt posted. Defaults throw until the ERP
    /// layer implements them.</summary>
    Task<IReadOnlyList<PurchaseRequisition>> GetOpenPurchaseRequisitionsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GetOpenPurchaseRequisitionsAsync is implemented by the ERP layer.");
    Task<IReadOnlyList<PurchaseOrder>> GetDraftPurchaseOrdersAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GetDraftPurchaseOrdersAsync is implemented by the ERP layer.");
    Task<IReadOnlyList<VendorInvoice>> GetOpenInvoicesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GetOpenInvoicesAsync is implemented by the ERP layer.");
    Task<IReadOnlyList<Payment>> GetOpenPaymentsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GetOpenPaymentsAsync is implemented by the ERP layer.");
    Task<IReadOnlyList<AsnDto>> GetUnreceivedAsnsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GetUnreceivedAsnsAsync is implemented by the ERP layer.");
    Task<IReadOnlyList<VendorDto>> GetVendorsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<VendorDocumentDto>> GetVendorDocumentsAsync(string vendorId, CancellationToken ct = default);
    Task<string?> GetVendorCountryAsync(string vendorId, CancellationToken ct = default);
    /// <summary>Legacy precomputed per-vendor share. New paths read raw
    /// history below and let compliance compute; kept for interface stability.</summary>
    Task<decimal> GetVendorVolumeShareAsync(string vendorId, string itemId, CancellationToken ct = default);
    /// <summary>Raw trailing-12-month posted received volume in cells for an
    /// item, union of seeded openings plus one row per posted receipt line.
    /// Shares are computed from these rows.</summary>
    Task<IReadOnlyList<VolumeHistory>> GetVolumeHistoryAsync(string itemId, CancellationToken ct = default);
    Task<ItemDto?> GetItemAsync(string itemId, CancellationToken ct = default);
    Task<PaymentTermsDto?> GetPaymentTermsAsync(string termsId, CancellationToken ct = default);
    Task<VendorBankDto?> GetVendorBankAsync(string vendorId, CancellationToken ct = default);
    Task<UserDto?> GetUserAsync(string userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default);
    Task<decimal> GetAutoApproveThresholdAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DelegationDto>> GetDelegationsAsync(CancellationToken ct = default);
    Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken ct = default);
    Task<ShipmentReceipt?> GetReceiptAsync(string receiptNumber, CancellationToken ct = default);
    Task<VendorInvoice?> GetInvoiceAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default);
    Task<Payment?> GetPaymentAsync(string paymentNumber, CancellationToken ct = default);
    Task<VendorConfirmation?> GetVendorConfirmationAsync(string poNumber, CancellationToken ct = default);
    Task<AsnDto?> GetAsnAsync(string asnNumber, CancellationToken ct = default);
    Task<IReadOnlyList<InventoryTransaction>> GetInventoryTransactionsAsync(string itemId, CancellationToken ct = default);
    Task<IReadOnlyList<Settlement>> GetSettlementsAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default);
    Task<IReadOnlyList<HoldDto>> GetOpenHoldsAsync(string? documentNumber, CancellationToken ct = default);
    Task<IReadOnlyList<HoldRelease>> GetHoldHistoryAsync(string holdId, CancellationToken ct = default);
    Task<IReadOnlyList<ApprovalDto>> GetApprovalsAsync(string documentType, string documentNumber, CancellationToken ct = default);

    // Writes: caller supplies intent + audit + idempotency key only.
    // ERP assigns numbers (SO/PR/PO at creation; receipt, voucher, payment
    // at posting), reads amounts/limits/due dates/bank/delegations itself.
    Task<PurchaseRequisition> CreatePrAsync(string requester, string source, string createdBy, IReadOnlyList<PurchaseRequisitionLine> lines, string idempotencyKey, CancellationToken ct = default);
    Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default);
    Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, string submittedBy, string idempotencyKey, CancellationToken ct = default);
    Task<ApprovalDto> RecordApprovalAsync(string documentType, string documentNumber, string actedBy, bool approved, string? comment, string idempotencyKey, CancellationToken ct = default);
    Task<PurchaseOrder> CreatePoAsync(string prNumber, string vendorId, string source, string createdBy, string idempotencyKey, CancellationToken ct = default);
    Task<VendorConfirmation> RecordVendorConfirmationAsync(string poNumber, DateOnly confirmDate, string source, string createdBy, IReadOnlyList<VendorConfirmLine> lines, string idempotencyKey, CancellationToken ct = default);
    Task<AsnDto> CreateAsnAsync(string asnNumber, string poNumber, string source, string createdBy, IReadOnlyList<AsnCarton> cartons, string idempotencyKey, CancellationToken ct = default);
    Task UpdateSalesOrderStatusAsync(string soNumber, SalesOrderStatus status, string actedBy, string idempotencyKey, CancellationToken ct = default);
    /// <summary>Creates a sales order from an incoming order webhook. The ERP
    /// mints the SO number from its sequence; status starts Created. Repeat
    /// deliveries reuse the webhook idempotency key and return the stored
    /// order. Implemented by the ERP layer.</summary>
    Task<SalesOrder> CreateSalesOrderAsync(string currency, IReadOnlyList<SalesOrderLine> lines, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        throw new NotSupportedException("CreateSalesOrderAsync is implemented by the ERP layer.");
    Task BlockVendorAsync(string vendorId, string reason, string blockedBy, string idempotencyKey, CancellationToken ct = default);
    Task UnblockVendorAsync(string vendorId, string unblockedBy, string idempotencyKey, CancellationToken ct = default);
    Task AddPoNoteAsync(string poNumber, string note, string createdBy, string idempotencyKey, CancellationToken ct = default);
    Task ClosePoAsync(string poNumber, string closedBy, string idempotencyKey, CancellationToken ct = default);
    Task<ShipmentReceipt> PostReceiptAsync(string poNumber, string supplierPackingSlipNumber, string source, string createdBy, IReadOnlyList<ReceiptLine> lines, string idempotencyKey, CancellationToken ct = default);
    Task QuarantineLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default);
    Task ReleaseLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default);
    Task ConsumeLotAsync(string lotRef, string soNumber, decimal quantity, string actedBy, string idempotencyKey, CancellationToken ct = default);
    Task<HoldDto> SetHoldAsync(string reasonCode, string note, string? ruleId, string caseId, string setBy, string? documentType, string? documentNumber, string? poLineRef, string? lotRef, string? cartonId, string idempotencyKey, CancellationToken ct = default);
    Task ReleaseHoldAsync(string holdId, string releasedBy, string? approvalRef, string idempotencyKey, CancellationToken ct = default);
    Task<VendorInvoice> CreateInvoiceAsync(string supplierInvoiceNumber, string vendorId, string? poNumber, string source, string createdBy, IReadOnlyList<InvoiceLineInput> lines, string idempotencyKey, CancellationToken ct = default);
    Task RecordMatchResultAsync(string vendorId, string supplierInvoiceNumber, IReadOnlyList<LineMatchResult> results, string idempotencyKey, CancellationToken ct = default);
    Task<VendorInvoice> PostInvoiceAsync(string vendorId, string supplierInvoiceNumber, string postedBy, string idempotencyKey, CancellationToken ct = default);
    Task ReleaseHeldInvoiceLineAsync(string vendorId, string supplierInvoiceNumber, string? poLineRef, decimal quantity, string releasedBy, string idempotencyKey, CancellationToken ct = default);
    Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, string source, string createdBy, string idempotencyKey, CancellationToken ct = default);
    /// <summary>Payment run paying invoices due up to a date: invoices due
    /// after payUpToDate are skipped. Null means no date limit. Implemented
    /// by the ERP layer; the default pays everything.</summary>
    Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, DateOnly? payUpToDate, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        CreatePaymentDraftAsync(invoices, source, createdBy, idempotencyKey, ct);
    Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, CancellationToken ct = default);
    Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default);
    Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default);
}

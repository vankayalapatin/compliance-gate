// UI-lane test double: wraps any IErpAdapter, serves company info,
// consequence previews, and sites from settable test values, and forwards
// everything else untouched. The in-memory fake does not implement the
// company reads, so UI tests that need them wrap the fake in this double.
// Test-double data only; nothing depends on company-data numbers.
using Gate.Contracts;

namespace Gate.Eval.Ui;

internal sealed class CompanyInfoErpDouble : IErpAdapter
{
    private readonly IErpAdapter _inner;

    public CompanyInfoErpDouble(IErpAdapter inner, CompanyInfoDto? company = null)
    {
        _inner = inner;
        Company = company;
    }

    public IErpAdapter Inner => _inner;

    public CompanyInfoDto? Company { get; set; }

    public ConsequencePreview? Preview { get; set; }

    public IReadOnlyList<SiteDto> Sites { get; set; } = [];

    public Task<CompanyInfoDto?> GetCompanyInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(Company);

    public Task<ConsequencePreview?> GetConsequencePreviewAsync(
        string documentType, string documentNumber, CancellationToken ct = default) =>
        Task.FromResult(Preview);

    public Task<IReadOnlyList<SiteDto>> GetSitesAsync(CancellationToken ct = default) =>
        Task.FromResult(Sites);

    public Task<SalesOrder?> GetSalesOrderAsync(string soNumber, CancellationToken ct = default) =>
        _inner.GetSalesOrderAsync(soNumber, ct);

    public Task<IReadOnlyList<BomLine>> GetBomAsync(string itemId, CancellationToken ct = default) =>
        _inner.GetBomAsync(itemId, ct);

    public Task<decimal> GetStockAsync(string itemId, CancellationToken ct = default) =>
        _inner.GetStockAsync(itemId, ct);

    public Task<IReadOnlyList<PurchaseOrder>> GetOpenPurchaseOrdersAsync(string itemId, CancellationToken ct = default) =>
        _inner.GetOpenPurchaseOrdersAsync(itemId, ct);

    public Task<IReadOnlyList<PurchaseRequisition>> GetOpenPurchaseRequisitionsAsync(CancellationToken ct = default) =>
        _inner.GetOpenPurchaseRequisitionsAsync(ct);

    public Task<IReadOnlyList<PurchaseOrder>> GetDraftPurchaseOrdersAsync(CancellationToken ct = default) =>
        _inner.GetDraftPurchaseOrdersAsync(ct);

    public Task<IReadOnlyList<VendorInvoice>> GetOpenInvoicesAsync(CancellationToken ct = default) =>
        _inner.GetOpenInvoicesAsync(ct);

    public Task<IReadOnlyList<Payment>> GetOpenPaymentsAsync(CancellationToken ct = default) =>
        _inner.GetOpenPaymentsAsync(ct);

    public Task<IReadOnlyList<AsnDto>> GetUnreceivedAsnsAsync(CancellationToken ct = default) =>
        _inner.GetUnreceivedAsnsAsync(ct);

    public Task<IReadOnlyList<VendorDto>> GetVendorsAsync(CancellationToken ct = default) =>
        _inner.GetVendorsAsync(ct);

    public Task<IReadOnlyList<VendorDocumentDto>> GetVendorDocumentsAsync(string vendorId, CancellationToken ct = default) =>
        _inner.GetVendorDocumentsAsync(vendorId, ct);

    public Task<string?> GetVendorCountryAsync(string vendorId, CancellationToken ct = default) =>
        _inner.GetVendorCountryAsync(vendorId, ct);

    public Task<decimal> GetVendorVolumeShareAsync(string vendorId, string itemId, CancellationToken ct = default) =>
        _inner.GetVendorVolumeShareAsync(vendorId, itemId, ct);

    public Task<IReadOnlyList<VolumeHistory>> GetVolumeHistoryAsync(string itemId, CancellationToken ct = default) =>
        _inner.GetVolumeHistoryAsync(itemId, ct);

    public Task<ItemDto?> GetItemAsync(string itemId, CancellationToken ct = default) =>
        _inner.GetItemAsync(itemId, ct);

    public Task<PaymentTermsDto?> GetPaymentTermsAsync(string termsId, CancellationToken ct = default) =>
        _inner.GetPaymentTermsAsync(termsId, ct);

    public Task<VendorBankDto?> GetVendorBankAsync(string vendorId, CancellationToken ct = default) =>
        _inner.GetVendorBankAsync(vendorId, ct);

    public Task<UserDto?> GetUserAsync(string userId, CancellationToken ct = default) =>
        _inner.GetUserAsync(userId, ct);

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) =>
        _inner.GetUsersAsync(ct);

    public Task<decimal> GetAutoApproveThresholdAsync(CancellationToken ct = default) =>
        _inner.GetAutoApproveThresholdAsync(ct);

    public Task<IReadOnlyList<DelegationDto>> GetDelegationsAsync(CancellationToken ct = default) =>
        _inner.GetDelegationsAsync(ct);

    public Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken ct = default) =>
        _inner.GetPurchaseOrderAsync(poNumber, ct);

    public Task<ShipmentReceipt?> GetReceiptAsync(string receiptNumber, CancellationToken ct = default) =>
        _inner.GetReceiptAsync(receiptNumber, ct);

    public Task<VendorInvoice?> GetInvoiceAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default) =>
        _inner.GetInvoiceAsync(vendorId, supplierInvoiceNumber, ct);

    public Task<Payment?> GetPaymentAsync(string paymentNumber, CancellationToken ct = default) =>
        _inner.GetPaymentAsync(paymentNumber, ct);

    public Task<VendorConfirmation?> GetVendorConfirmationAsync(string poNumber, CancellationToken ct = default) =>
        _inner.GetVendorConfirmationAsync(poNumber, ct);

    public Task<AsnDto?> GetAsnAsync(string asnNumber, CancellationToken ct = default) =>
        _inner.GetAsnAsync(asnNumber, ct);

    public Task<IReadOnlyList<InventoryTransaction>> GetInventoryTransactionsAsync(string itemId, CancellationToken ct = default) =>
        _inner.GetInventoryTransactionsAsync(itemId, ct);

    public Task<IReadOnlyList<Settlement>> GetSettlementsAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default) =>
        _inner.GetSettlementsAsync(vendorId, supplierInvoiceNumber, ct);

    public Task<IReadOnlyList<HoldDto>> GetOpenHoldsAsync(string? documentNumber, CancellationToken ct = default) =>
        _inner.GetOpenHoldsAsync(documentNumber, ct);

    public Task<IReadOnlyList<HoldRelease>> GetHoldHistoryAsync(string holdId, CancellationToken ct = default) =>
        _inner.GetHoldHistoryAsync(holdId, ct);

    public Task<IReadOnlyList<ApprovalDto>> GetApprovalsAsync(string documentType, string documentNumber, CancellationToken ct = default) =>
        _inner.GetApprovalsAsync(documentType, documentNumber, ct);

    public Task<IReadOnlyList<SalesOrder>> GetSalesOrdersAsync(CancellationToken ct = default) =>
        _inner.GetSalesOrdersAsync(ct);

    public Task<IReadOnlyList<PurchaseRequisition>> GetPurchaseRequisitionsAsync(CancellationToken ct = default) =>
        _inner.GetPurchaseRequisitionsAsync(ct);

    public Task<IReadOnlyList<PurchaseOrder>> GetPurchaseOrdersAsync(CancellationToken ct = default) =>
        _inner.GetPurchaseOrdersAsync(ct);

    public Task<IReadOnlyList<VendorInvoice>> GetVendorInvoicesAsync(CancellationToken ct = default) =>
        _inner.GetVendorInvoicesAsync(ct);

    public Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default) =>
        _inner.GetPaymentsAsync(ct);

    public Task<IReadOnlyList<ShipmentReceipt>> GetShipmentReceiptsAsync(CancellationToken ct = default) =>
        _inner.GetShipmentReceiptsAsync(ct);

    public Task<IReadOnlyList<AsnDto>> GetAsnsAsync(CancellationToken ct = default) =>
        _inner.GetAsnsAsync(ct);

    public Task<IReadOnlyList<PurchaseOrder>> GetPurchaseOrdersByVendorAsync(string vendorId, CancellationToken ct = default) =>
        _inner.GetPurchaseOrdersByVendorAsync(vendorId, ct);

    public Task<IReadOnlyList<ItemDto>> GetItemsAsync(CancellationToken ct = default) =>
        _inner.GetItemsAsync(ct);

    public Task<IReadOnlyList<InventoryTransaction>> GetAllInventoryTransactionsAsync(CancellationToken ct = default) =>
        _inner.GetAllInventoryTransactionsAsync(ct);

    public Task<IReadOnlyList<PendingApprovalDto>> GetPendingApprovalsAsync(CancellationToken ct = default) =>
        _inner.GetPendingApprovalsAsync(ct);

    public Task<PurchaseRequisition> CreatePrAsync(string requester, string source, string createdBy, IReadOnlyList<PurchaseRequisitionLine> lines, string idempotencyKey, CancellationToken ct = default) =>
        _inner.CreatePrAsync(requester, source, createdBy, lines, idempotencyKey, ct);

    public Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default) =>
        _inner.GetPrAsync(prNumber, ct);

    public Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, string submittedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.SubmitForApprovalAsync(documentType, documentNumber, routedTo, submittedBy, idempotencyKey, ct);

    public Task<ApprovalDto> RecordApprovalAsync(string documentType, string documentNumber, string actedBy, bool approved, string? comment, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordApprovalAsync(documentType, documentNumber, actedBy, approved, comment, idempotencyKey, ct);

    public Task<PurchaseOrder> CreatePoAsync(string prNumber, string vendorId, string source, string createdBy, string idempotencyKey, IReadOnlyList<PoOrderLine>? lines = null, CancellationToken ct = default) =>
        _inner.CreatePoAsync(prNumber, vendorId, source, createdBy, idempotencyKey, lines, ct);

    public Task<VendorConfirmation> RecordVendorConfirmationAsync(string poNumber, DateOnly confirmDate, string source, string createdBy, IReadOnlyList<VendorConfirmLine> lines, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordVendorConfirmationAsync(poNumber, confirmDate, source, createdBy, lines, idempotencyKey, ct);

    public Task<AsnDto> CreateAsnAsync(string? asnNumber, string poNumber, string source, string createdBy, IReadOnlyList<AsnCarton> cartons, string idempotencyKey, CancellationToken ct = default) =>
        _inner.CreateAsnAsync(asnNumber, poNumber, source, createdBy, cartons, idempotencyKey, ct);

    public Task UpdateSalesOrderStatusAsync(string soNumber, SalesOrderStatus status, string actedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.UpdateSalesOrderStatusAsync(soNumber, status, actedBy, idempotencyKey, ct);

    public Task<SalesOrder> CreateSalesOrderAsync(string currency, IReadOnlyList<SalesOrderLine> lines, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.CreateSalesOrderAsync(currency, lines, source, createdBy, idempotencyKey, ct);

    public Task BlockVendorAsync(string vendorId, string reason, string blockedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.BlockVendorAsync(vendorId, reason, blockedBy, idempotencyKey, ct);

    public Task UnblockVendorAsync(string vendorId, string unblockedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.UnblockVendorAsync(vendorId, unblockedBy, idempotencyKey, ct);

    public Task AddPoNoteAsync(string poNumber, string note, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.AddPoNoteAsync(poNumber, note, createdBy, idempotencyKey, ct);

    public Task ClosePoAsync(string poNumber, string closedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.ClosePoAsync(poNumber, closedBy, idempotencyKey, ct);

    public Task<ShipmentReceipt> PostReceiptAsync(string poNumber, string supplierPackingSlipNumber, string source, string createdBy, IReadOnlyList<ReceiptLine> lines, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        _inner.PostReceiptAsync(poNumber, supplierPackingSlipNumber, source, createdBy, lines, idempotencyKey, postingDate, ct: ct);

    public Task QuarantineLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.QuarantineLotAsync(lotRef, holdId, actedBy, idempotencyKey, ct);

    public Task ReleaseLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.ReleaseLotAsync(lotRef, holdId, actedBy, idempotencyKey, ct);

    public Task ConsumeLotAsync(string lotRef, string soNumber, decimal quantity, string actedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.ConsumeLotAsync(lotRef, soNumber, quantity, actedBy, idempotencyKey, ct);

    public Task<HoldDto> SetHoldAsync(string reasonCode, string note, string? ruleId, string caseId, string setBy, string? documentType, string? documentNumber, string? poLineRef, string? lotRef, string? cartonId, string idempotencyKey, CancellationToken ct = default) =>
        _inner.SetHoldAsync(reasonCode, note, ruleId, caseId, setBy, documentType, documentNumber, poLineRef, lotRef, cartonId, idempotencyKey, ct);

    public Task ReleaseHoldAsync(string holdId, string releasedBy, string? approvalRef, string idempotencyKey, CancellationToken ct = default) =>
        _inner.ReleaseHoldAsync(holdId, releasedBy, approvalRef, idempotencyKey, ct);

    public Task<VendorInvoice> CreateInvoiceAsync(string supplierInvoiceNumber, string vendorId, string? poNumber, string source, string createdBy, IReadOnlyList<InvoiceLineInput> lines, string idempotencyKey, DateOnly? invoiceDate = null, CancellationToken ct = default) =>
        _inner.CreateInvoiceAsync(supplierInvoiceNumber, vendorId, poNumber, source, createdBy, lines, idempotencyKey, invoiceDate, ct: ct);

    public Task RecordMatchResultAsync(string vendorId, string supplierInvoiceNumber, IReadOnlyList<LineMatchResult> results, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordMatchResultAsync(vendorId, supplierInvoiceNumber, results, idempotencyKey, ct);

    public Task<VendorInvoice> PostInvoiceAsync(string vendorId, string supplierInvoiceNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        _inner.PostInvoiceAsync(vendorId, supplierInvoiceNumber, postedBy, idempotencyKey, postingDate, ct: ct);

    public Task ReleaseHeldInvoiceLineAsync(string vendorId, string supplierInvoiceNumber, string? poLineRef, decimal quantity, string releasedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.ReleaseHeldInvoiceLineAsync(vendorId, supplierInvoiceNumber, poLineRef, quantity, releasedBy, idempotencyKey, ct);

    public Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, string source, string createdBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        _inner.CreatePaymentDraftAsync(invoices, source, createdBy, idempotencyKey, postingDate, ct: ct);

    public Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        _inner.PostPaymentAsync(draftPaymentNumber, postedBy, idempotencyKey, postingDate, ct: ct);

    public Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordVendorBankChangeAsync(vendorId, newAccountRef, source, changedBy, idempotencyKey, ct);

    public Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.VerifyVendorBankAsync(vendorId, verifiedBy, idempotencyKey, ct);

    public Task<VendorDto> CreateVendorAsync(string? vendorId, string name, string countryOfOrigin, string paymentTermsId, string? email, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.CreateVendorAsync(vendorId, name, countryOfOrigin, paymentTermsId, email, source, createdBy, idempotencyKey, ct);

    public Task<ItemDto> CreateItemAsync(string itemId, string description, string unit, bool isCritical, int unitsPerCarton, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.CreateItemAsync(itemId, description, unit, isCritical, unitsPerCarton, source, createdBy, idempotencyKey, ct);
}

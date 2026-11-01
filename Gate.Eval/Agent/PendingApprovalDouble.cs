// Pending-approval double (agent-lane test kit). Wraps the in-memory fake
// ERP and serves GetPendingApprovalsAsync, which the fake does not
// implement: submissions are tracked at SubmitForApprovalAsync (routing and
// requester are recorded inside the fake and not re-readable), and each
// tracked row re-reads its document through the inner adapter, so amounts
// come from the fake and decided documents drop out. Every other member
// forwards untouched.
using Gate.Contracts;

namespace Gate.Eval;

internal sealed class PendingApprovalDouble : IErpAdapter
{
    private readonly IErpAdapter _inner;
    private readonly Dictionary<(string Type, string Number), (string RoutedTo, string SubmittedBy)> _submitted = new();

    public PendingApprovalDouble(IErpAdapter inner) => _inner = inner;

    public IErpAdapter Inner => _inner;

    public async Task<IReadOnlyList<PendingApprovalDto>> GetPendingApprovalsAsync(
        CancellationToken ct = default)
    {
        var rows = new List<PendingApprovalDto>();
        foreach (var ((type, number), sub) in _submitted
            .OrderBy(e => e.Key.Type, StringComparer.Ordinal)
            .ThenBy(e => e.Key.Number, StringComparer.Ordinal))
        {
            if ((await _inner.GetApprovalsAsync(type, number, ct).ConfigureAwait(false)).Count > 0)
            {
                continue;
            }

            var (amount, submitted) = await ResolveAsync(type, number, ct).ConfigureAwait(false);
            if (!submitted)
            {
                continue;
            }

            rows.Add(new PendingApprovalDto(type, number, sub.RoutedTo, sub.SubmittedBy, amount));
        }

        return rows;
    }

    /// <summary>Document amount plus whether it still sits in its submitted
    /// marker state (PR Submitted, PO InReview, invoice Matched, payment
    /// Submitted). Unknown types and missing documents are not pending.</summary>
    private async Task<(decimal Amount, bool Submitted)> ResolveAsync(
        string type, string number, CancellationToken ct)
    {
        return type switch
        {
            "PR" => await _inner.GetPrAsync(number, ct).ConfigureAwait(false) is { } pr
                ? (pr.EstimatedAmount, pr.Status == PrStatus.Submitted)
                : (0, false),
            "PO" => await _inner.GetPurchaseOrderAsync(number, ct).ConfigureAwait(false) is { } po
                ? (po.Lines.Sum(l => l.LineAmount), po.ApprovalStatus == PoApprovalStatus.InReview)
                : (0, false),
            "Invoice" => await ResolveInvoiceAsync(number, ct).ConfigureAwait(false),
            "Payment" => await _inner.GetPaymentAsync(number, ct).ConfigureAwait(false) is { } pay
                ? (pay.Lines.Sum(l => l.Amount), pay.Status == PaymentStatus.Submitted)
                : (0, false),
            _ => (0, false),
        };
    }

    private async Task<(decimal Amount, bool Submitted)> ResolveInvoiceAsync(
        string number, CancellationToken ct)
    {
        var parts = number.Split('/', 2);
        if (parts.Length != 2)
        {
            return (0, false);
        }

        var invoice = await _inner.GetInvoiceAsync(parts[0], parts[1], ct).ConfigureAwait(false);
        return invoice is null
            ? (0, false)
            : (invoice.Lines.Sum(l => l.Quantity * l.UnitPrice), invoice.Status == InvoiceStatus.Matched);
    }

    public async Task SubmitForApprovalAsync(
        string documentType, string documentNumber, string routedTo,
        string submittedBy, string idempotencyKey, CancellationToken ct = default)
    {
        await _inner.SubmitForApprovalAsync(
            documentType, documentNumber, routedTo, submittedBy, idempotencyKey, ct).ConfigureAwait(false);
        _submitted[(documentType, documentNumber)] = (routedTo, submittedBy);
    }

    public Task<SalesOrder?> GetSalesOrderAsync(string soNumber, CancellationToken ct = default) =>
        _inner.GetSalesOrderAsync(soNumber, ct);

    public Task<IReadOnlyList<BomLine>> GetBomAsync(string itemId, CancellationToken ct = default) =>
        _inner.GetBomAsync(itemId, ct);

    public Task<decimal> GetStockAsync(string itemId, CancellationToken ct = default) =>
        _inner.GetStockAsync(itemId, ct);

    public Task<IReadOnlyList<PurchaseOrder>> GetOpenPurchaseOrdersAsync(string itemId, CancellationToken ct = default) =>
        _inner.GetOpenPurchaseOrdersAsync(itemId, ct);

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

    public Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default) =>
        _inner.GetPrAsync(prNumber, ct);

    public Task<PurchaseRequisition> CreatePrAsync(string requester, string source, string createdBy, IReadOnlyList<PurchaseRequisitionLine> lines, string idempotencyKey, CancellationToken ct = default) =>
        _inner.CreatePrAsync(requester, source, createdBy, lines, idempotencyKey, ct);

    public Task<ApprovalDto> RecordApprovalAsync(string documentType, string documentNumber, string actedBy, bool approved, string? comment, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordApprovalAsync(documentType, documentNumber, actedBy, approved, comment, idempotencyKey, ct);

    public Task<PurchaseOrder> CreatePoAsync(string prNumber, string vendorId, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.CreatePoAsync(prNumber, vendorId, source, createdBy, idempotencyKey, ct);

    public Task<VendorConfirmation> RecordVendorConfirmationAsync(string poNumber, DateOnly confirmDate, string source, string createdBy, IReadOnlyList<VendorConfirmLine> lines, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordVendorConfirmationAsync(poNumber, confirmDate, source, createdBy, lines, idempotencyKey, ct);

    public Task<AsnDto> CreateAsnAsync(string asnNumber, string poNumber, string source, string createdBy, IReadOnlyList<AsnCarton> cartons, string idempotencyKey, CancellationToken ct = default) =>
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

    public Task<ShipmentReceipt> PostReceiptAsync(string poNumber, string supplierPackingSlipNumber, string source, string createdBy, IReadOnlyList<ReceiptLine> lines, string idempotencyKey, CancellationToken ct = default) =>
        _inner.PostReceiptAsync(poNumber, supplierPackingSlipNumber, source, createdBy, lines, idempotencyKey, ct);

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

    public Task<VendorInvoice> CreateInvoiceAsync(string supplierInvoiceNumber, string vendorId, string? poNumber, string source, string createdBy, IReadOnlyList<InvoiceLineInput> lines, string idempotencyKey, CancellationToken ct = default) =>
        _inner.CreateInvoiceAsync(supplierInvoiceNumber, vendorId, poNumber, source, createdBy, lines, idempotencyKey, ct);

    public Task RecordMatchResultAsync(string vendorId, string supplierInvoiceNumber, IReadOnlyList<LineMatchResult> results, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordMatchResultAsync(vendorId, supplierInvoiceNumber, results, idempotencyKey, ct);

    public Task<VendorInvoice> PostInvoiceAsync(string vendorId, string supplierInvoiceNumber, string postedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.PostInvoiceAsync(vendorId, supplierInvoiceNumber, postedBy, idempotencyKey, ct);

    public Task ReleaseHeldInvoiceLineAsync(string vendorId, string supplierInvoiceNumber, string? poLineRef, decimal quantity, string releasedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.ReleaseHeldInvoiceLineAsync(vendorId, supplierInvoiceNumber, poLineRef, quantity, releasedBy, idempotencyKey, ct);

    public Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.CreatePaymentDraftAsync(invoices, source, createdBy, idempotencyKey, ct);

    public Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.PostPaymentAsync(draftPaymentNumber, postedBy, idempotencyKey, ct);

    public Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordVendorBankChangeAsync(vendorId, newAccountRef, source, changedBy, idempotencyKey, ct);

    public Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.VerifyVendorBankAsync(vendorId, verifiedBy, idempotencyKey, ct);
}

// What-if list-read doubles (agent-lane test kit). WhatIfListReadDouble
// wraps any IErpAdapter, tracks documents across writes, and serves the
// five pipeline list-reads with the contract's lifecycle slices (open PRs
// Draft/Submitted, draft POs Draft/InReview, open invoices
// Received/Matched, open payments Draft/Submitted, ASNs whose PO has no
// posted receipt). Each list re-reads through the inner adapter, so status
// changes from later writes are honored. ListReadsMissingDouble instead
// throws NotSupportedException on the five reads, pinning the gather's
// rollout fallback no matter what the inner adapter implements.
using Gate.Contracts;

namespace Gate.Eval;

internal sealed class WhatIfListReadDouble : IErpAdapter
{
    private readonly IErpAdapter _inner;
    private readonly HashSet<string> _prs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pos = new(StringComparer.Ordinal);
    private readonly HashSet<(string VendorId, string SupplierInvoiceNumber)> _invoices = new();
    private readonly HashSet<string> _payments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _asns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _posWithReceipts = new(StringComparer.Ordinal);

    public WhatIfListReadDouble(IErpAdapter inner) => _inner = inner;

    public IErpAdapter Inner => _inner;

    public async Task<IReadOnlyList<PurchaseRequisition>> GetOpenPurchaseRequisitionsAsync(
        CancellationToken ct = default)
    {
        var rows = new List<PurchaseRequisition>();
        foreach (var number in _prs.OrderBy(n => n, StringComparer.Ordinal))
        {
            var pr = await _inner.GetPrAsync(number, ct).ConfigureAwait(false);
            if (pr is not null && pr.Status is PrStatus.Draft or PrStatus.Submitted)
            {
                rows.Add(pr);
            }
        }

        return rows;
    }

    public async Task<IReadOnlyList<PurchaseOrder>> GetDraftPurchaseOrdersAsync(
        CancellationToken ct = default)
    {
        var rows = new List<PurchaseOrder>();
        foreach (var number in _pos.OrderBy(n => n, StringComparer.Ordinal))
        {
            var po = await _inner.GetPurchaseOrderAsync(number, ct).ConfigureAwait(false);
            if (po is not null && po.ApprovalStatus is PoApprovalStatus.Draft or PoApprovalStatus.InReview)
            {
                rows.Add(po);
            }
        }

        return rows;
    }

    public async Task<IReadOnlyList<VendorInvoice>> GetOpenInvoicesAsync(
        CancellationToken ct = default)
    {
        var rows = new List<VendorInvoice>();
        foreach (var key in _invoices
            .OrderBy(k => k.VendorId, StringComparer.Ordinal)
            .ThenBy(k => k.SupplierInvoiceNumber, StringComparer.Ordinal))
        {
            var invoice = await _inner.GetInvoiceAsync(key.VendorId, key.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            if (invoice is not null && invoice.Status is InvoiceStatus.Received or InvoiceStatus.Matched)
            {
                rows.Add(invoice);
            }
        }

        return rows;
    }

    public async Task<IReadOnlyList<Payment>> GetOpenPaymentsAsync(
        CancellationToken ct = default)
    {
        var rows = new List<Payment>();
        foreach (var number in _payments.OrderBy(n => n, StringComparer.Ordinal))
        {
            var payment = await _inner.GetPaymentAsync(number, ct).ConfigureAwait(false);
            if (payment is not null && payment.Status is PaymentStatus.Draft or PaymentStatus.Submitted)
            {
                rows.Add(payment);
            }
        }

        return rows;
    }

    public async Task<IReadOnlyList<AsnDto>> GetUnreceivedAsnsAsync(
        CancellationToken ct = default)
    {
        var rows = new List<AsnDto>();
        foreach (var number in _asns.OrderBy(n => n, StringComparer.Ordinal))
        {
            var asn = await _inner.GetAsnAsync(number, ct).ConfigureAwait(false);
            if (asn is not null && !_posWithReceipts.Contains(asn.PoNumber))
            {
                rows.Add(asn);
            }
        }

        return rows;
    }

    public async Task<PurchaseRequisition> CreatePrAsync(
        string requester, string source, string createdBy,
        IReadOnlyList<PurchaseRequisitionLine> lines, string idempotencyKey,
        CancellationToken ct = default)
    {
        var pr = await _inner.CreatePrAsync(requester, source, createdBy, lines, idempotencyKey, ct).ConfigureAwait(false);
        _prs.Add(pr.PrNumber);
        return pr;
    }

    public async Task<PurchaseOrder> CreatePoAsync(
        string prNumber, string vendorId, string source, string createdBy,
        string idempotencyKey, IReadOnlyList<PoOrderLine>? lines = null, CancellationToken ct = default)
    {
        var po = await _inner.CreatePoAsync(prNumber, vendorId, source, createdBy, idempotencyKey, lines, ct).ConfigureAwait(false);
        _pos.Add(po.PoNumber);
        return po;
    }

    public async Task<VendorInvoice> CreateInvoiceAsync(
        string supplierInvoiceNumber, string vendorId, string? poNumber,
        string source, string createdBy, IReadOnlyList<InvoiceLineInput> lines,
        string idempotencyKey, DateOnly? invoiceDate = null, CancellationToken ct = default)
    {
        var invoice = await _inner.CreateInvoiceAsync(
            supplierInvoiceNumber, vendorId, poNumber, source, createdBy, lines, idempotencyKey, invoiceDate, ct: ct).ConfigureAwait(false);
        _invoices.Add((invoice.VendorId, invoice.SupplierInvoiceNumber));
        return invoice;
    }

    public async Task<Payment> CreatePaymentDraftAsync(
        IReadOnlyList<InvoiceKey> invoices, string source, string createdBy,
        string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default)
    {
        var payment = await _inner.CreatePaymentDraftAsync(invoices, source, createdBy, idempotencyKey, postingDate, ct: ct).ConfigureAwait(false);
        _payments.Add(payment.PaymentNumber);
        return payment;
    }

    public async Task<Payment> CreatePaymentDraftAsync(
        IReadOnlyList<InvoiceKey> invoices, DateOnly? payUpToDate, string source,
        string createdBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default)
    {
        var payment = await _inner.CreatePaymentDraftAsync(invoices, payUpToDate, source, createdBy, idempotencyKey, postingDate, ct: ct).ConfigureAwait(false);
        _payments.Add(payment.PaymentNumber);
        return payment;
    }

    public async Task<AsnDto> CreateAsnAsync(
        string? asnNumber, string poNumber, string source, string createdBy,
        IReadOnlyList<AsnCarton> cartons, string idempotencyKey,
        CancellationToken ct = default)
    {
        var asn = await _inner.CreateAsnAsync(asnNumber, poNumber, source, createdBy, cartons, idempotencyKey, ct).ConfigureAwait(false);
        _asns.Add(asn.AsnNumber);
        return asn;
    }

    public async Task<ShipmentReceipt> PostReceiptAsync(
        string poNumber, string supplierPackingSlipNumber, string source,
        string createdBy, IReadOnlyList<ReceiptLine> lines,
        string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default)
    {
        var receipt = await _inner.PostReceiptAsync(
            poNumber, supplierPackingSlipNumber, source, createdBy, lines, idempotencyKey, postingDate, ct: ct).ConfigureAwait(false);
        _posWithReceipts.Add(receipt.PoNumber);
        return receipt;
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

    public Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, string submittedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.SubmitForApprovalAsync(documentType, documentNumber, routedTo, submittedBy, idempotencyKey, ct);

    public Task<ApprovalDto> RecordApprovalAsync(string documentType, string documentNumber, string actedBy, bool approved, string? comment, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordApprovalAsync(documentType, documentNumber, actedBy, approved, comment, idempotencyKey, ct);

    public Task<VendorConfirmation> RecordVendorConfirmationAsync(string poNumber, DateOnly confirmDate, string source, string createdBy, IReadOnlyList<VendorConfirmLine> lines, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordVendorConfirmationAsync(poNumber, confirmDate, source, createdBy, lines, idempotencyKey, ct);

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

    public Task RecordMatchResultAsync(string vendorId, string supplierInvoiceNumber, IReadOnlyList<LineMatchResult> results, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordMatchResultAsync(vendorId, supplierInvoiceNumber, results, idempotencyKey, ct);

    public Task<VendorInvoice> PostInvoiceAsync(string vendorId, string supplierInvoiceNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        _inner.PostInvoiceAsync(vendorId, supplierInvoiceNumber, postedBy, idempotencyKey, postingDate, ct: ct);

    public Task ReleaseHeldInvoiceLineAsync(string vendorId, string supplierInvoiceNumber, string? poLineRef, decimal quantity, string releasedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.ReleaseHeldInvoiceLineAsync(vendorId, supplierInvoiceNumber, poLineRef, quantity, releasedBy, idempotencyKey, ct);

    public Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        _inner.PostPaymentAsync(draftPaymentNumber, postedBy, idempotencyKey, postingDate, ct: ct);

    public Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordVendorBankChangeAsync(vendorId, newAccountRef, source, changedBy, idempotencyKey, ct);

    public Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.VerifyVendorBankAsync(vendorId, verifiedBy, idempotencyKey, ct);
}

/// <summary>An ERP layer without the five pipeline list-reads: every other
/// call forwards to the inner adapter, the five reads throw exactly like
/// the contract's defaults. Pins the gather's rollout fallback.</summary>
internal sealed class ListReadsMissingDouble : IErpAdapter
{
    private readonly IErpAdapter _inner;

    public ListReadsMissingDouble(IErpAdapter inner) => _inner = inner;

    public Task<IReadOnlyList<PurchaseRequisition>> GetOpenPurchaseRequisitionsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GetOpenPurchaseRequisitionsAsync is not implemented by this ERP layer.");

    public Task<IReadOnlyList<PurchaseOrder>> GetDraftPurchaseOrdersAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GetDraftPurchaseOrdersAsync is not implemented by this ERP layer.");

    public Task<IReadOnlyList<VendorInvoice>> GetOpenInvoicesAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GetOpenInvoicesAsync is not implemented by this ERP layer.");

    public Task<IReadOnlyList<Payment>> GetOpenPaymentsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GetOpenPaymentsAsync is not implemented by this ERP layer.");

    public Task<IReadOnlyList<AsnDto>> GetUnreceivedAsnsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("GetUnreceivedAsnsAsync is not implemented by this ERP layer.");

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

    public Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, DateOnly? payUpToDate, string source, string createdBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        _inner.CreatePaymentDraftAsync(invoices, payUpToDate, source, createdBy, idempotencyKey, postingDate, ct: ct);

    public Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        _inner.PostPaymentAsync(draftPaymentNumber, postedBy, idempotencyKey, postingDate, ct: ct);

    public Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.RecordVendorBankChangeAsync(vendorId, newAccountRef, source, changedBy, idempotencyKey, ct);

    public Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default) =>
        _inner.VerifyVendorBankAsync(vendorId, verifiedBy, idempotencyKey, ct);
}

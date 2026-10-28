// In-memory fake ERP. Implements the full adapter contract with real D365
// behaviours (sequences, posted immutability, SoD and limit refusals) so
// other agents can build and test before the real module exists.
using Gate.Contracts;

namespace Gate.Erp;

public sealed class InMemoryErpAdapter : IErpAdapter
{
    private readonly Dictionary<string, SalesOrder> _salesOrders = new();
    private readonly Dictionary<string, List<BomLine>> _boms = new();
    private readonly Dictionary<string, decimal> _stock = new();
    private readonly Dictionary<string, VendorDto> _vendors = new();
    private readonly Dictionary<string, List<VendorDocumentDto>> _vendorDocs = new();
    private readonly Dictionary<string, decimal> _volumeShares = new();
    private readonly Dictionary<string, decimal> _signingLimits = new();
    private readonly Dictionary<string, PurchaseRequisition> _prs = new();
    private readonly Dictionary<string, PurchaseOrder> _pos = new();
    private readonly Dictionary<string, ShipmentReceipt> _receipts = new();
    private readonly Dictionary<string, VendorInvoice> _invoices = new();
    private readonly Dictionary<string, Payment> _payments = new();
    private readonly Dictionary<string, HoldDto> _holds = new();
    private readonly List<ApprovalDto> _approvals = new();
    private readonly Dictionary<(string Type, string Number), string> _requesters = new();
    private readonly Dictionary<string, List<string>> _paymentInvoices = new();
    private readonly Dictionary<string, int> _sequences = new();
    private readonly Dictionary<string, decimal> _paidAmounts = new();
    private readonly List<string> _poNotes = new();

    // Seed helpers (test and demo setup only).
    public void SeedSalesOrder(SalesOrder so) => _salesOrders[so.SoNumber] = so;
    public void SeedBom(string itemId, IEnumerable<BomLine> lines) => _boms[itemId] = lines.ToList();
    public void SeedStock(string itemId, decimal qty) => _stock[itemId] = qty;
    public void SeedVendor(VendorDto vendor) => _vendors[vendor.VendorId] = vendor;
    public void SeedVendorDocuments(string vendorId, IEnumerable<VendorDocumentDto> docs) => _vendorDocs[vendorId] = docs.ToList();
    public void SeedVolumeShare(string vendorId, decimal share) => _volumeShares[vendorId] = share;
    public void SeedSigningLimit(string user, decimal limit) => _signingLimits[user] = limit;

    private string Next(string prefix)
    {
        _sequences.TryGetValue(prefix, out var n);
        n++;
        _sequences[prefix] = n;
        return $"{prefix}-{n}";
    }

    private static void ThrowIfPosted(DocStatus status, string what)
    {
        if (status is DocStatus.Posted or DocStatus.Closed)
        {
            throw new InvalidOperationException($"{what} is already posted and immutable.");
        }
    }

    public Task<SalesOrder?> GetSalesOrderAsync(string soNumber, CancellationToken ct = default) =>
        Task.FromResult(_salesOrders.GetValueOrDefault(soNumber));

    public Task<IReadOnlyList<BomLine>> GetBomAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BomLine>>(_boms.GetValueOrDefault(itemId, []));

    public Task<decimal> GetStockAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult(_stock.GetValueOrDefault(itemId));

    public Task<IReadOnlyList<PurchaseOrder>> GetOpenPurchaseOrdersAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PurchaseOrder>>(
            _pos.Values.Where(p => p.Status is DocStatus.Approved or DocStatus.Submitted)
                .Where(p => p.Lines.Any(l => l.ItemId == itemId)).ToList());

    public Task<IReadOnlyList<VendorDto>> GetVendorsAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VendorDto>>(_vendors.Values.ToList());

    public Task<IReadOnlyList<VendorDocumentDto>> GetVendorDocumentsAsync(string vendorId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VendorDocumentDto>>(_vendorDocs.GetValueOrDefault(vendorId, []));

    public Task<string?> GetVendorCountryAsync(string vendorId, CancellationToken ct = default) =>
        Task.FromResult(_vendors.GetValueOrDefault(vendorId)?.CountryOfOrigin);

    public Task<decimal> GetVendorVolumeShareAsync(string vendorId, string itemId, CancellationToken ct = default) =>
        Task.FromResult(_volumeShares.GetValueOrDefault(vendorId));

    public Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken ct = default) =>
        Task.FromResult(_pos.GetValueOrDefault(poNumber));

    public Task<ShipmentReceipt?> GetReceiptAsync(string receiptNumber, CancellationToken ct = default) =>
        Task.FromResult(_receipts.GetValueOrDefault(receiptNumber));

    public Task<VendorInvoice?> GetInvoiceAsync(string invoiceNumber, CancellationToken ct = default) =>
        Task.FromResult(_invoices.GetValueOrDefault(invoiceNumber));

    public Task<IReadOnlyList<ApprovalDto>> GetApprovalsAsync(string documentType, string documentNumber, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApprovalDto>>(
            _approvals.Where(a => a.DocumentType == documentType && a.DocumentNumber == documentNumber).ToList());

    public Task<PurchaseRequisition> CreatePrAsync(string requester, string source, IReadOnlyList<PurchaseRequisitionLine> lines, CancellationToken ct = default)
    {
        var pr = new PurchaseRequisition(Next("PR"), DocStatus.Draft, requester, source, lines.ToList());
        _prs[pr.PrNumber] = pr;
        _requesters[("PR", pr.PrNumber)] = requester;
        return Task.FromResult(pr);
    }

    public Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default) =>
        Task.FromResult(_prs.GetValueOrDefault(prNumber));

    public Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, CancellationToken ct = default)
    {
        if (documentType == "PR" && _prs.TryGetValue(documentNumber, out var pr))
        {
            ThrowIfPosted(pr.Status, documentNumber);
            _prs[documentNumber] = pr with { Status = DocStatus.Submitted };
            return Task.CompletedTask;
        }

        throw new InvalidOperationException($"Unknown document {documentType} {documentNumber}.");
    }

    public Task RecordApprovalAsync(ApprovalDto approval, CancellationToken ct = default)
    {
        var actor = approval.ActingAsDelegate ? approval.RoutedTo : (approval.ActedBy ?? approval.RoutedTo);
        if (_requesters.TryGetValue((approval.DocumentType, approval.DocumentNumber), out var requester)
            && string.Equals(approval.ActedBy ?? actor, requester, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Requester cannot approve their own request.");
        }

        if (approval.Approved && _signingLimits.TryGetValue(actor, out var limit) && approval.LimitChecked > limit)
        {
            throw new InvalidOperationException($"{actor} cannot approve {approval.LimitChecked} above signing limit {limit}.");
        }

        if (approval is { DocumentType: "Payment", Approved: true } && _paymentInvoices.TryGetValue(approval.DocumentNumber, out var paidInvoices))
        {
            foreach (var inv in paidInvoices)
            {
                if (_approvals.Any(a => a is { DocumentType: "Invoice", Approved: true } && a.DocumentNumber == inv
                    && string.Equals(a.ActedBy ?? a.RoutedTo, approval.ActedBy ?? actor, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Invoice approver and payment approver must be different people.");
                }
            }
        }

        _approvals.Add(approval with { Timestamp = DateTimeOffset.UtcNow });
        if (approval.DocumentType == "PR" && approval.Approved && _prs.TryGetValue(approval.DocumentNumber, out var pr))
        {
            _prs[approval.DocumentNumber] = pr with { Status = DocStatus.Approved };
        }

        return Task.CompletedTask;
    }

    public Task<PurchaseOrder> CreatePoAsync(string prNumber, string vendorId, string currency, string source, CancellationToken ct = default)
    {
        if (!_prs.TryGetValue(prNumber, out var pr) || pr.Status != DocStatus.Approved)
        {
            throw new InvalidOperationException($"No valid approved PR {prNumber}: PO refused.");
        }

        if (_vendors.GetValueOrDefault(vendorId)?.IsBlocked is true)
        {
            throw new InvalidOperationException($"Vendor {vendorId} is blocked.");
        }

        var po = new PurchaseOrder(
            Next("PO"), prNumber, vendorId, DocStatus.Approved, currency, source,
            pr.Lines.Select((l, i) => new PurchaseOrderLine(i + 1, l.ItemId, l.Quantity, 0, 0, 0, 0, $"PO-L{i + 1}")).ToList());
        _pos[po.PoNumber] = po;
        _requesters[("PO", po.PoNumber)] = pr.Requester;
        return Task.FromResult(po);
    }

    public void SeedPaymentWithInvoices(Payment payment, IEnumerable<string> invoiceNumbers)
    {
        _payments[payment.PaymentNumber] = payment;
        _paymentInvoices[payment.PaymentNumber] = invoiceNumbers.ToList();
    }

    public void SetPoLinePrice(string poNumber, string poLineRef, decimal price)
    {
        if (_pos.TryGetValue(poNumber, out var po))
        {
            _pos[poNumber] = po with
            {
                Lines = po.Lines.Select(l => l.PoLineRef == poLineRef ? l with { UnitPrice = price } : l).ToList()
            };
        }
    }

    public Task UpdateSalesOrderStatusAsync(string soNumber, DocStatus status, CancellationToken ct = default)
    {
        if (_salesOrders.TryGetValue(soNumber, out var so))
        {
            _salesOrders[soNumber] = so with { Status = status };
        }

        return Task.CompletedTask;
    }

    public Task BlockVendorAsync(string vendorId, string reason, CancellationToken ct = default)
    {
        if (_vendors.TryGetValue(vendorId, out var v))
        {
            _vendors[vendorId] = v with { IsBlocked = true, BlockReason = reason };
        }

        return Task.CompletedTask;
    }

    public Task AddPoNoteAsync(string poNumber, string note, CancellationToken ct = default)
    {
        _poNotes.Add($"{poNumber}: {note}");
        return Task.CompletedTask;
    }

    public Task<ShipmentReceipt> PostReceiptAsync(string poNumber, string source, IReadOnlyList<ReceiptLine> lines, CancellationToken ct = default)
    {
        if (!_pos.TryGetValue(poNumber, out var po) || po.Status is DocStatus.Draft or DocStatus.Cancelled)
        {
            throw new InvalidOperationException($"PO {poNumber} is not approved for receipt.");
        }

        var receipt = new ShipmentReceipt(Next("RCT"), poNumber, DocStatus.Posted, source, lines.ToList());
        _receipts[receipt.ReceiptNumber] = receipt;
        var updated = po.Lines.Select(l =>
        {
            var got = lines.Where(r => r.PoLineRef == l.PoLineRef).Sum(r => r.QtyReceived);
            var quar = lines.Where(r => r.PoLineRef == l.PoLineRef).Sum(r => r.QtyQuarantined);
            return l with { QtyReceived = l.QtyReceived + got, QtyQuarantined = l.QtyQuarantined + quar };
        }).ToList();
        _pos[poNumber] = po with { Lines = updated };
        return Task.FromResult(receipt);
    }

    public Task<HoldDto> SetHoldAsync(string reasonCode, string note, string? ruleId, string caseId, string setBy, CancellationToken ct = default)
    {
        var hold = new HoldDto(Next("HLD"), reasonCode, note, ruleId, caseId, setBy, DateTimeOffset.UtcNow);
        _holds[hold.HoldId] = hold;
        return Task.FromResult(hold);
    }

    public Task ReleaseHoldAsync(string holdId, string releasedBy, CancellationToken ct = default)
    {
        _holds.Remove(holdId);
        return Task.CompletedTask;
    }

    public Task<VendorInvoice> CreateInvoiceAsync(VendorInvoice invoice, CancellationToken ct = default)
    {
        if (_invoices.ContainsKey(invoice.InvoiceNumber))
        {
            throw new InvalidOperationException($"Duplicate invoice {invoice.InvoiceNumber}.");
        }

        _invoices[invoice.InvoiceNumber] = invoice;
        return Task.FromResult(invoice);
    }

    public Task RecordMatchResultAsync(string invoiceNumber, IReadOnlyList<LineMatchResult> results, CancellationToken ct = default)
    {
        if (_invoices.TryGetValue(invoiceNumber, out var inv))
        {
            _invoices[invoiceNumber] = inv with { MatchResults = results.ToList() };
        }

        return Task.CompletedTask;
    }

    public Task<Payment> CreatePaymentAsync(string currency, string source, IReadOnlyList<PaymentLine> lines, CancellationToken ct = default)
    {
        foreach (var line in lines)
        {
            if (!_invoices.TryGetValue(line.InvoiceNumber, out var inv))
            {
                throw new InvalidOperationException($"Unknown invoice {line.InvoiceNumber}.");
            }

            ThrowIfPosted(inv.Status, line.InvoiceNumber);
            var accepted = inv.Lines.Where(l => l.Disposition == LineDisposition.Accepted).Sum(l => l.Quantity * l.UnitPrice);
            var paid = _paidAmounts.GetValueOrDefault(line.InvoiceNumber);
            if (paid + line.Amount > accepted)
            {
                throw new InvalidOperationException($"Invoice {line.InvoiceNumber} only has {accepted - paid} accepted value left: payment refused.");
            }

            _paidAmounts[line.InvoiceNumber] = paid + line.Amount;
        }

        var payment = new Payment(Next("PAY"), DocStatus.Posted, currency, source, lines.ToList());
        _payments[payment.PaymentNumber] = payment;
        _paymentInvoices[payment.PaymentNumber] = lines.Select(l => l.InvoiceNumber).ToList();
        return Task.FromResult(payment);
    }
}

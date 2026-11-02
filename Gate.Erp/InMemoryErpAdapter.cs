// In-memory fake ERP. Implements the full adapter contract with real D365
// behaviours (sequences, posted immutability, SoD and limit refusals) so
// other agents can build and test before the real module exists.
//
// Write-method rule: callers supply intent + audit + idempotency key only.
// ERP assigns numbers (SO/PR/PO at creation; receipt, voucher, payment at
// posting) and reads amounts, limits, due dates, bank snapshots, and
// delegate flags itself.
using Gate.Contracts;

namespace Gate.Erp;

public sealed class InMemoryErpAdapter : IErpAdapter
{
    private const decimal PriceTolerance = 0.02m;

    private readonly Dictionary<string, SalesOrder> _salesOrders = new();
    private readonly Dictionary<string, List<BomLine>> _boms = new();
    private readonly Dictionary<string, ItemDto> _items = new();
    private readonly Dictionary<string, VendorDto> _vendors = new();
    private readonly Dictionary<string, List<VendorDocumentDto>> _vendorDocs = new();
    private readonly Dictionary<string, PaymentTermsDto> _terms = new();
    private readonly Dictionary<string, VendorBankDto> _banks = new();
    private readonly Dictionary<string, UserDto> _users = new();
    private readonly List<DelegationDto> _delegations = new();
    private readonly Dictionary<string, decimal> _volumeShares = new();
    private readonly Dictionary<string, List<VolumeHistory>> _volumeSeed = new();
    private readonly Dictionary<(string Vendor, string Item), decimal> _prices = new();
    private readonly Dictionary<string, decimal> _lastPrice = new();
    private decimal _autoApproveThreshold;
    private readonly Dictionary<string, PurchaseRequisition> _prs = new();
    private readonly Dictionary<string, PurchaseOrder> _pos = new();
    private readonly Dictionary<string, VendorConfirmation> _confirmations = new();
    private readonly Dictionary<string, AsnDto> _asns = new();
    private readonly HashSet<string> _cartonIds = new();
    private readonly Dictionary<string, ShipmentReceipt> _receipts = new();
    private readonly Dictionary<(string Vendor, string Invoice), VendorInvoice> _invoices = new();
    private readonly Dictionary<string, DateTimeOffset> _invoicePostedAt = new();
    private readonly Dictionary<string, Payment> _payments = new();
    private readonly Dictionary<string, HoldDto> _holds = new();
    private readonly List<HoldRelease> _holdHistory = new();
    private readonly List<ApprovalDto> _approvals = new();
    private readonly Dictionary<(string Type, string Number), string> _requesters = new();
    private readonly Dictionary<(string Type, string Number), string> _routedTo = new();
    private readonly Dictionary<(string Type, string Number), string> _submittedBy = new();
    private readonly Dictionary<string, List<string>> _poNotes = new();
    private readonly Dictionary<string, List<InventoryTransaction>> _inventory = new();
    private readonly Dictionary<string, (string PoNumber, string PoLineRef, string ItemId)> _lotMap = new();
    private readonly List<Settlement> _settlements = new();
    private readonly List<HeldRelease> _heldReleases = new();
    private readonly Dictionary<string, long> _sequences = new();
    private readonly Dictionary<string, (string? Format, int Padding)> _seqFormats = new();
    private readonly Dictionary<string, object> _idem = new();
    private readonly Dictionary<string, decimal> _paidAmounts = new();
    private string _baseCurrency = string.Empty;

    // Seed helpers (test and demo setup only).
    public void SeedSalesOrder(SalesOrder so) => _salesOrders[so.SoNumber] = so;
    public void SeedBom(string itemId, IEnumerable<BomLine> lines) => _boms[itemId] = lines.ToList();
    public void SeedItem(ItemDto item) => _items[item.ItemId] = item;
    public void SeedStock(string itemId, decimal qty)
    {
        var list = _inventory.GetValueOrDefault(itemId);
        if (list is null) { list = new List<InventoryTransaction>(); _inventory[itemId] = list; }
        list.Add(new InventoryTransaction(itemId, "OPENING", "OPENING", InventoryStatus.Available, qty));
    }
    public void SeedVendor(VendorDto vendor) => _vendors[vendor.VendorId] = vendor;
    public void SeedVendorDocuments(string vendorId, IEnumerable<VendorDocumentDto> docs) => _vendorDocs[vendorId] = docs.ToList();
    public void SeedVolumeShare(string vendorId, decimal share) => _volumeShares[vendorId] = share;
    public void SeedVolumeHistory(string itemId, IEnumerable<VolumeHistory> rows) => _volumeSeed[itemId] = rows.ToList();
    public void SeedPaymentTerms(PaymentTermsDto terms) => _terms[terms.TermsId] = terms;
    public void SeedVendorBank(VendorBankDto bank) => _banks[bank.VendorId] = bank;
    public void SeedUser(UserDto user) => _users[user.UserId] = user;
    public void SeedDelegation(DelegationDto delegation) => _delegations.Add(delegation);
    public void SeedVendorPrice(string vendorId, string itemId, decimal unitPrice)
    {
        _prices[(vendorId, itemId)] = unitPrice;
        _lastPrice[itemId] = unitPrice;
    }
    public void SeedBaseCurrency(string currency) => _baseCurrency = currency;
    public void SeedAutoApproveThreshold(decimal threshold) => _autoApproveThreshold = threshold;

    public void Clear()
    {
        _salesOrders.Clear(); _boms.Clear(); _items.Clear(); _vendors.Clear();
        _vendorDocs.Clear(); _terms.Clear(); _banks.Clear(); _users.Clear();
        _delegations.Clear(); _volumeShares.Clear(); _volumeSeed.Clear(); _prices.Clear();
        _prs.Clear(); _pos.Clear(); _confirmations.Clear(); _asns.Clear(); _cartonIds.Clear();
        _receipts.Clear(); _invoices.Clear(); _invoicePostedAt.Clear(); _payments.Clear();
        _holds.Clear(); _holdHistory.Clear(); _approvals.Clear(); _requesters.Clear();
        _routedTo.Clear(); _poNotes.Clear(); _inventory.Clear(); _lotMap.Clear();
        _settlements.Clear(); _heldReleases.Clear(); _sequences.Clear(); _idem.Clear();
        _seqFormats.Clear(); _submittedBy.Clear();
        _paidAmounts.Clear(); _baseCurrency = string.Empty;
        _lastPrice.Clear(); _autoApproveThreshold = 0;
    }

    /// <summary>Applies one scenario number-sequence rule: stores the format
    /// and starts the counter at Start, or after the max seeded number when
    /// seeded rows already run higher.</summary>
    public void ConfigureSequence(string prefix, string? format, int padding, long start)
    {
        var next = Math.Max(start, MaxSeededNumber(prefix) + 1);
        _sequences[prefix] = next;
        _seqFormats[prefix] = (format, padding);
    }

    private long MaxSeededNumber(string prefix)
    {
        var max = 0L;
        void Scan(string number)
        {
            if (NumberSequences.TryParseNumber(prefix, number, out var n) && n > max) max = n;
        }
        foreach (var so in _salesOrders.Keys) Scan(so);
        foreach (var pr in _prs.Keys) Scan(pr);
        foreach (var po in _pos.Keys) Scan(po);
        foreach (var r in _receipts.Keys) Scan(r);
        foreach (var a in _asns.Keys) Scan(a);
        foreach (var h in _holds.Keys) Scan(h);
        foreach (var v in _vendors.Keys) Scan(v);
        foreach (var p in _payments.Keys) Scan(p);
        foreach (var i in _invoices.Values) if (i.Voucher is not null) Scan(i.Voucher);
        return max;
    }

    private string Next(string prefix)
    {
        if (!_sequences.TryGetValue(prefix, out var n)) n = NumberSequences.DefaultStart;
        _sequences[prefix] = n + 1;
        var (format, padding) = _seqFormats.TryGetValue(prefix, out var cfg)
            ? cfg
            : ((string?)null, NumberSequences.DefaultPadding);
        return NumberSequences.FormatNumber(prefix, format, padding, n);
    }

    private T Idem<T>(string key, Func<T> create)
    {
        if (_idem.TryGetValue(key, out var existing)) return (T)existing!;
        var result = create();
        _idem[key] = result!;
        return result;
    }

    private static string InvoiceDoc(string vendorId, string supplierNumber) => $"{vendorId}/{supplierNumber}";
    private static string PaidKey(string vendorId, string supplierNumber) => $"{vendorId}/{supplierNumber}";

    private static DateTimeOffset BusinessStamp(DateOnly? businessDate) =>
        businessDate.HasValue
            ? new DateTimeOffset(businessDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
            : DateTimeOffset.UtcNow;

    private decimal PoAcceptedInvoiced(string poNumber, string poLineRef)
    {
        return _invoices.Values
            .Where(i => i.PoNumber == poNumber)
            .SelectMany(i => i.Lines)
            .Where(l => l.PoLineRef == poLineRef && l.Disposition == LineDisposition.Accepted)
            .Sum(l => l.Quantity);
    }

    private static void ThrowIfTerminal(string what, bool terminal)
    {
        if (terminal) throw new InvalidOperationException($"{what} is terminal and immutable.");
    }

    public Task<SalesOrder?> GetSalesOrderAsync(string soNumber, CancellationToken ct = default) =>
        Task.FromResult(_salesOrders.GetValueOrDefault(soNumber));

    public Task<IReadOnlyList<BomLine>> GetBomAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BomLine>>(_boms.GetValueOrDefault(itemId, []));

    public Task<decimal> GetStockAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult(_inventory.GetValueOrDefault(itemId, []).Where(t => t.Status == InventoryStatus.Available).Sum(t => t.Quantity));

    public Task<IReadOnlyList<PurchaseOrder>> GetOpenPurchaseOrdersAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PurchaseOrder>>(
            _pos.Values.Where(p => p.DocStatus is not PoDocStatus.Closed and not PoDocStatus.Cancelled)
                .Where(p => p.Lines.Any(l => l.ItemId == itemId && l.QtyOrdered - l.QtyReceived > 0)).ToList());

    public Task<IReadOnlyList<PurchaseRequisition>> GetOpenPurchaseRequisitionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PurchaseRequisition>>(
            _prs.Values.Where(p => p.Status is PrStatus.Draft or PrStatus.Submitted)
                .OrderBy(p => p.PrNumber).ToList());

    public Task<IReadOnlyList<PurchaseOrder>> GetDraftPurchaseOrdersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PurchaseOrder>>(
            _pos.Values.Where(p => p.ApprovalStatus is PoApprovalStatus.Draft or PoApprovalStatus.InReview)
                .OrderBy(p => p.PoNumber).ToList());

    public Task<IReadOnlyList<VendorInvoice>> GetOpenInvoicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VendorInvoice>>(
            _invoices.Values.Where(i => i.Status is InvoiceStatus.Received or InvoiceStatus.Matched)
                .OrderBy(i => i.VendorId).ThenBy(i => i.SupplierInvoiceNumber).ToList());

    public Task<IReadOnlyList<Payment>> GetOpenPaymentsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Payment>>(
            _payments.Values.Where(p => p.Status is PaymentStatus.Draft or PaymentStatus.Submitted)
                .OrderBy(p => p.PaymentNumber).ToList());

    public Task<IReadOnlyList<AsnDto>> GetUnreceivedAsnsAsync(CancellationToken ct = default)
    {
        // An ASN is unreceived while no receipt is posted against its PO.
        var receivedPo = _receipts.Values.Select(r => r.PoNumber).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlyList<AsnDto>>(
            _asns.Values.Where(a => !receivedPo.Contains(a.PoNumber))
                .OrderBy(a => a.AsnNumber).ToList());
    }

    public Task<IReadOnlyList<SalesOrder>> GetSalesOrdersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SalesOrder>>(
            _salesOrders.Values.OrderBy(s => s.SoNumber, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<PurchaseRequisition>> GetPurchaseRequisitionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PurchaseRequisition>>(
            _prs.Values.OrderBy(p => p.PrNumber, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<PurchaseOrder>> GetPurchaseOrdersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PurchaseOrder>>(
            _pos.Values.OrderBy(p => p.PoNumber, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<VendorInvoice>> GetVendorInvoicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VendorInvoice>>(
            _invoices.Values
                .OrderBy(i => i.VendorId, StringComparer.Ordinal)
                .ThenBy(i => i.SupplierInvoiceNumber, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Payment>>(
            _payments.Values.OrderBy(p => p.PaymentNumber, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<ShipmentReceipt>> GetShipmentReceiptsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ShipmentReceipt>>(
            _receipts.Values.OrderBy(r => r.ReceiptNumber, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<AsnDto>> GetAsnsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AsnDto>>(
            _asns.Values.OrderBy(a => a.AsnNumber, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<PurchaseOrder>> GetPurchaseOrdersByVendorAsync(string vendorId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PurchaseOrder>>(
            _pos.Values.Where(p => string.Equals(p.VendorId, vendorId, StringComparison.Ordinal))
                .OrderBy(p => p.PoNumber, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<ItemDto>> GetItemsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ItemDto>>(
            _items.Values.OrderBy(i => i.ItemId, StringComparer.Ordinal).ToList());

    public Task<IReadOnlyList<InventoryTransaction>> GetAllInventoryTransactionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InventoryTransaction>>(
            _inventory.Values.SelectMany(l => l)
                .OrderBy(t => t.ItemId, StringComparer.Ordinal)
                .ThenBy(t => t.ReceiptNumber, StringComparer.Ordinal)
                .ThenBy(t => t.LotRef, StringComparer.Ordinal)
                .ThenBy(t => t.Status).ToList());

    public Task<IReadOnlyList<PendingApprovalDto>> GetPendingApprovalsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PendingApprovalDto>>(
            _routedTo
                .Where(kv => IsAwaitingDecision(kv.Key.Type, kv.Key.Number))
                .OrderBy(kv => kv.Key.Type, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.Number, StringComparer.Ordinal)
                .Select(kv => new PendingApprovalDto(
                    kv.Key.Type,
                    kv.Key.Number,
                    kv.Value,
                    _submittedBy.TryGetValue(kv.Key, out var by) && !string.IsNullOrWhiteSpace(by)
                        ? by
                        : _requesters.GetValueOrDefault(kv.Key, string.Empty),
                    ResolveDocumentAmount(kv.Key.Type, kv.Key.Number)))
                .ToList());

    private bool IsAwaitingDecision(string documentType, string documentNumber)
    {
        return documentType switch
        {
            "PR" => _prs.TryGetValue(documentNumber, out var pr) && pr.Status == PrStatus.Submitted,
            "PO" => _pos.TryGetValue(documentNumber, out var po) && po.ApprovalStatus == PoApprovalStatus.InReview,
            "Invoice" => _invoices.Values
                .FirstOrDefault(i => InvoiceDoc(i.VendorId, i.SupplierInvoiceNumber) == documentNumber) is { } inv
                && inv.Status == InvoiceStatus.Matched,
            "Payment" => _payments.TryGetValue(documentNumber, out var pay) && pay.Status == PaymentStatus.Submitted,
            _ => false
        };
    }

    public Task<IReadOnlyList<VendorDto>> GetVendorsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VendorDto>>(_vendors.Values.ToList());

    public Task<IReadOnlyList<VendorDocumentDto>> GetVendorDocumentsAsync(string vendorId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VendorDocumentDto>>(_vendorDocs.GetValueOrDefault(vendorId, []));

    public Task<string?> GetVendorCountryAsync(string vendorId, CancellationToken ct = default) =>
        Task.FromResult(_vendors.GetValueOrDefault(vendorId)?.CountryOfOrigin);

    public Task<IReadOnlyList<string>> GetPricedVendorIdsAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_prices.Keys
            .Where(k => string.Equals(k.Item, itemId, StringComparison.Ordinal))
            .Select(k => k.Vendor)
            .Order(StringComparer.Ordinal)
            .ToList());

    public Task<decimal> GetVendorVolumeShareAsync(string vendorId, string itemId, CancellationToken ct = default) =>
        Task.FromResult(_volumeShares.GetValueOrDefault(vendorId));

    public Task<IReadOnlyList<VolumeHistory>> GetVolumeHistoryAsync(string itemId, CancellationToken ct = default)
    {
        // Same trailing-12-month contract as the real adapter: posted
        // receipts older than the window stay out. Seeded openings carry
        // no date, so they always stay in.
        var windowStart = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime).AddMonths(-12);
        var seeded = _volumeSeed.GetValueOrDefault(itemId, []);
        var posted = _receipts.Values.Where(r => r.PostingDate is null || r.PostingDate.Value >= windowStart).SelectMany(r =>
        {
            var po = _pos.GetValueOrDefault(r.PoNumber);
            var vendor = po is null ? null : _vendors.GetValueOrDefault(po.VendorId);
            var country = vendor?.CountryOfOrigin ?? "OTHER";
            return r.Lines.Select(l =>
            {
                var lineItem = po?.Lines.FirstOrDefault(x => x.PoLineRef == l.PoLineRef)?.ItemId ?? itemId;
                return (Item: lineItem, Row: new VolumeHistory($"Receipt {r.ReceiptNumber}", country, l.QtyReceived));
            }).Where(x => x.Item == itemId).Select(x => x.Row);
        }).ToList();
        return Task.FromResult<IReadOnlyList<VolumeHistory>>(seeded.Concat(posted).ToList());
    }

    public Task<ItemDto?> GetItemAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(itemId));

    public Task<PaymentTermsDto?> GetPaymentTermsAsync(string termsId, CancellationToken ct = default) =>
        Task.FromResult(_terms.GetValueOrDefault(termsId));

    public Task<VendorBankDto?> GetVendorBankAsync(string vendorId, CancellationToken ct = default) =>
        Task.FromResult(_banks.GetValueOrDefault(vendorId));

    public Task<UserDto?> GetUserAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult(_users.GetValueOrDefault(userId));

    public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserDto>>(_users.Values.ToList());

    public Task<decimal> GetAutoApproveThresholdAsync(CancellationToken ct = default) =>
        Task.FromResult(_autoApproveThreshold);

    public Task<IReadOnlyList<DelegationDto>> GetDelegationsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DelegationDto>>(_delegations.ToList());

    public Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken ct = default) =>
        Task.FromResult(_pos.GetValueOrDefault(poNumber));

    public Task<ShipmentReceipt?> GetReceiptAsync(string receiptNumber, CancellationToken ct = default) =>
        Task.FromResult(_receipts.GetValueOrDefault(receiptNumber));

    public Task<VendorInvoice?> GetInvoiceAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default) =>
        Task.FromResult(_invoices.GetValueOrDefault((vendorId, supplierInvoiceNumber)));

    public Task<Payment?> GetPaymentAsync(string paymentNumber, CancellationToken ct = default) =>
        Task.FromResult(_payments.GetValueOrDefault(paymentNumber));

    public Task<VendorConfirmation?> GetVendorConfirmationAsync(string poNumber, CancellationToken ct = default) =>
        Task.FromResult(_confirmations.GetValueOrDefault(poNumber));

    public Task<AsnDto?> GetAsnAsync(string asnNumber, CancellationToken ct = default) =>
        Task.FromResult(_asns.GetValueOrDefault(asnNumber));

    public Task<IReadOnlyList<InventoryTransaction>> GetInventoryTransactionsAsync(string itemId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InventoryTransaction>>(_inventory.GetValueOrDefault(itemId, []));

    public Task<IReadOnlyList<Settlement>> GetSettlementsAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Settlement>>(_settlements.Where(s => s.VendorId == vendorId && s.SupplierInvoiceNumber == supplierInvoiceNumber).ToList());

    public Task<IReadOnlyList<HoldDto>> GetOpenHoldsAsync(string? documentNumber, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HoldDto>>(_holds.Values
            .Where(h => h.Status == HoldStatus.Set && (documentNumber == null || h.DocumentNumber == documentNumber)).ToList());

    public Task<IReadOnlyList<HoldRelease>> GetHoldHistoryAsync(string holdId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HoldRelease>>(_holdHistory.Where(h => h.HoldId == holdId).ToList());

    public Task<IReadOnlyList<ApprovalDto>> GetApprovalsAsync(string documentType, string documentNumber, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ApprovalDto>>(
            _approvals.Where(a => a.DocumentType == documentType && a.DocumentNumber == documentNumber).ToList());

    public Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default) =>
        Task.FromResult(_prs.GetValueOrDefault(prNumber));

    public Task<PurchaseRequisition> CreatePrAsync(string requester, string source, string createdBy, IReadOnlyList<PurchaseRequisitionLine> lines, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"pr:{idempotencyKey}", () =>
        {
            if (string.IsNullOrWhiteSpace(_baseCurrency)) throw new InvalidOperationException("Base currency is not seeded.");
            if (lines.Count == 0) throw new InvalidOperationException("PR needs at least one line.");
            foreach (var l in lines) if (l.Quantity <= 0) throw new InvalidOperationException($"PR line {l.ItemId} quantity must be positive.");
            var estimated = lines.Sum(l =>
            {
                if (!_lastPrice.TryGetValue(l.ItemId, out var price))
                    throw new InvalidOperationException($"No last purchase price for item {l.ItemId}: PR cannot be estimated.");
                return l.Quantity * price;
            });
            var status = estimated < _autoApproveThreshold ? PrStatus.Approved : PrStatus.Draft;
            var now = DateTimeOffset.UtcNow;
            var prNumber = Next("PR");
            string? approvedBy = null;
            DateTimeOffset? approvedAt = null;
            if (status == PrStatus.Approved)
            {
                approvedBy = ErpApprovalActors.AutoApprovePolicy;
                approvedAt = now;
                _approvals.Add(new ApprovalDto("PR", prNumber, string.Empty, approvedBy, false, now, true,
                    $"Auto-approved: {estimated} {_baseCurrency} under the {_autoApproveThreshold} threshold.", estimated, _autoApproveThreshold));
            }
            var pr = new PurchaseRequisition(prNumber, status, requester, _baseCurrency, estimated, source, createdBy, idempotencyKey, lines.ToList(), CreatedAt: now, ApprovedBy: approvedBy, ApprovedAt: approvedAt);
            _prs[pr.PrNumber] = pr;
            _requesters[("PR", pr.PrNumber)] = requester;
            return pr;
        }));

    public Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, string submittedBy, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"submit:{idempotencyKey}", () =>
        {
            switch (documentType)
            {
                case "PR" when _prs.TryGetValue(documentNumber, out var pr):
                    if (pr.Status is PrStatus.Submitted) return null;
                    if (pr.Status is not PrStatus.Draft and not PrStatus.Rejected) throw new InvalidOperationException($"PR {documentNumber} cannot be submitted from {pr.Status}.");
                    _prs[documentNumber] = pr with { Status = PrStatus.Submitted };
                    break;
                case "PO" when _pos.TryGetValue(documentNumber, out var po):
                    if (po.ApprovalStatus is PoApprovalStatus.InReview) return null;
                    if (po.ApprovalStatus is not PoApprovalStatus.Draft) throw new InvalidOperationException($"PO {documentNumber} cannot be submitted from {po.ApprovalStatus}.");
                    _pos[documentNumber] = po with { ApprovalStatus = PoApprovalStatus.InReview };
                    break;
                case "Invoice":
                    var inv = _invoices.Values.FirstOrDefault(i => InvoiceDoc(i.VendorId, i.SupplierInvoiceNumber) == documentNumber)
                        ?? throw new InvalidOperationException($"Unknown invoice {documentNumber}.");
                    if (inv.Status is not InvoiceStatus.Received and not InvoiceStatus.Matched) throw new InvalidOperationException($"Invoice {documentNumber} cannot be submitted from {inv.Status}.");
                    _invoices[(inv.VendorId, inv.SupplierInvoiceNumber)] = inv with { Status = InvoiceStatus.Matched };
                    break;
                case "Payment" when _payments.TryGetValue(documentNumber, out var pay):
                    if (pay.Status is PaymentStatus.Submitted) return null;
                    if (pay.Status is not PaymentStatus.Draft) throw new InvalidOperationException($"Payment {documentNumber} cannot be submitted from {pay.Status}.");
                    _payments[documentNumber] = pay with { Status = PaymentStatus.Submitted };
                    break;
                default: throw new InvalidOperationException($"Unknown document {documentType} {documentNumber}.");
            }
            _routedTo[(documentType, documentNumber)] = routedTo;
            _submittedBy[(documentType, documentNumber)] = submittedBy;
            return null;
        });
        return Task.CompletedTask;
    }

    public Task<ApprovalDto> RecordApprovalAsync(string documentType, string documentNumber, string actedBy, bool approved, string? comment, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"approval:{idempotencyKey}", () =>
        {
            // Same-actor idempotency beyond the idempotency key, mirroring
            // the real adapter: a repeated click mints a fresh key.
            var prior = _approvals.FirstOrDefault(a =>
                a.DocumentType == documentType && a.DocumentNumber == documentNumber &&
                string.Equals(a.ActedBy ?? a.RoutedTo, actedBy, StringComparison.OrdinalIgnoreCase));
            if (prior is not null)
            {
                if (prior.Approved != approved)
                    throw new InvalidOperationException(
                        $"{actedBy} already {(prior.Approved ? "approved" : "rejected")} {documentType} {documentNumber}; " +
                        "a changed decision needs a new submission.");
                return prior;
            }
            if (!_routedTo.TryGetValue((documentType, documentNumber), out var routedTo))
                throw new InvalidOperationException($"Document {documentType} {documentNumber} was never submitted for approval.");
            if (!_users.TryGetValue(actedBy, out var actor)) throw new InvalidOperationException($"Unknown user {actedBy}.");
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
            var isDelegate = _delegations.Any(d =>
                string.Equals(d.FromUser, routedTo, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(d.ToUser, actedBy, StringComparison.OrdinalIgnoreCase) &&
                today >= d.Start && today <= d.End);
            if (!string.Equals(actedBy, routedTo, StringComparison.OrdinalIgnoreCase) && !isDelegate)
                throw new InvalidOperationException($"{actedBy} is not routed to approve {documentType} {documentNumber} and holds no delegation.");
            var requester = _requesters.GetValueOrDefault((documentType, documentNumber))
                ?? _requesters.GetValueOrDefault((documentType, ResolveDocKey(documentType, documentNumber)));
            if (requester is not null && string.Equals(actedBy, requester, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Requester cannot approve their own request.");
            var amount = ResolveDocumentAmount(documentType, documentNumber);
            if (approved && amount > actor.SigningLimit)
                throw new InvalidOperationException($"{actedBy} cannot approve {amount} above signing limit {actor.SigningLimit}.");
            if (documentType == "Payment" && approved)
            {
                var pay = _payments.GetValueOrDefault(documentNumber) ?? throw new InvalidOperationException($"Unknown payment {documentNumber}.");
                foreach (var line in pay.Lines)
                {
                    var invKey = InvoiceDoc(line.VendorId, line.SupplierInvoiceNumber);
                    if (_approvals.Any(a => a.DocumentType == "Invoice" && a.DocumentNumber == invKey && a.Approved &&
                        string.Equals(a.ActedBy ?? a.RoutedTo, actedBy, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Invoice approver and payment approver must be different people.");
                }
            }
            var stored = new ApprovalDto(documentType, documentNumber, routedTo, actedBy, isDelegate, DateTimeOffset.UtcNow, approved, comment, amount, actor.SigningLimit);
            _approvals.Add(stored);
            ApplyApprovalOutcome(documentType, documentNumber, approved);
            if (approved) StampApproved(documentType, documentNumber, actedBy, stored.Timestamp);
            return stored;
        }));

    private string ResolveDocKey(string documentType, string documentNumber)
    {
        if (documentType == "Invoice")
        {
            var inv = _invoices.Values.FirstOrDefault(i => InvoiceDoc(i.VendorId, i.SupplierInvoiceNumber) == documentNumber);
            if (inv is not null && _requesters.TryGetValue(("Invoice", InvoiceDoc(inv.VendorId, inv.SupplierInvoiceNumber)), out _)) return InvoiceDoc(inv.VendorId, inv.SupplierInvoiceNumber);
        }
        return documentNumber;
    }

    private decimal ResolveDocumentAmount(string documentType, string documentNumber)
    {
        return documentType switch
        {
            "PR" when _prs.TryGetValue(documentNumber, out var pr) => pr.EstimatedAmount,
            "PO" when _pos.TryGetValue(documentNumber, out var po) => po.Lines.Sum(l => l.LineAmount),
            "Invoice" => _invoices.Values.FirstOrDefault(i => InvoiceDoc(i.VendorId, i.SupplierInvoiceNumber) == documentNumber) is { } inv
                ? inv.Lines.Sum(l => l.Quantity * l.UnitPrice) : 0,
            "Payment" when _payments.TryGetValue(documentNumber, out var pay) => pay.Lines.Sum(l => l.Amount),
            _ => 0
        };
    }

    private void StampApproved(string documentType, string documentNumber, string actedBy, DateTimeOffset at)
    {
        switch (documentType)
        {
            case "PR" when _prs.TryGetValue(documentNumber, out var pr):
                _prs[documentNumber] = pr with { ApprovedBy = actedBy, ApprovedAt = at };
                break;
            case "PO" when _pos.TryGetValue(documentNumber, out var po):
                _pos[documentNumber] = po with { ApprovedBy = actedBy, ApprovedAt = at };
                break;
            case "Invoice":
                var inv = _invoices.Values.FirstOrDefault(i => InvoiceDoc(i.VendorId, i.SupplierInvoiceNumber) == documentNumber);
                if (inv is not null) _invoices[(inv.VendorId, inv.SupplierInvoiceNumber)] = inv with { ApprovedBy = actedBy, ApprovedAt = at };
                break;
            case "Payment" when _payments.TryGetValue(documentNumber, out var pay):
                _payments[documentNumber] = pay with { ApprovedBy = actedBy, ApprovedAt = at };
                break;
        }
    }

    private void ApplyApprovalOutcome(string documentType, string documentNumber, bool approved)
    {
        switch (documentType)
        {
            case "PR" when _prs.TryGetValue(documentNumber, out var pr):
                _prs[documentNumber] = pr with { Status = approved ? PrStatus.Approved : PrStatus.Rejected };
                break;
            case "PO" when _pos.TryGetValue(documentNumber, out var po):
                _pos[documentNumber] = po with { ApprovalStatus = approved ? PoApprovalStatus.Approved : PoApprovalStatus.Draft };
                break;
            case "Invoice":
                var inv = _invoices.Values.FirstOrDefault(i => InvoiceDoc(i.VendorId, i.SupplierInvoiceNumber) == documentNumber);
                if (inv is not null) _invoices[(inv.VendorId, inv.SupplierInvoiceNumber)] = inv with { Status = approved ? InvoiceStatus.Approved : InvoiceStatus.Received };
                break;
            case "Payment" when _payments.TryGetValue(documentNumber, out var pay):
                _payments[documentNumber] = pay with { Status = approved ? PaymentStatus.Approved : PaymentStatus.Draft };
                break;
        }
    }

    public Task<PurchaseOrder> CreatePoAsync(string prNumber, string vendorId, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"po:{idempotencyKey}", () =>
        {
            if (!_prs.TryGetValue(prNumber, out var pr) || pr.Status != PrStatus.Approved)
                throw new InvalidOperationException($"No valid approved PR {prNumber}: PO refused.");
            if (!_vendors.TryGetValue(vendorId, out var vendor)) throw new InvalidOperationException($"Unknown vendor {vendorId}.");
            if (vendor.IsBlocked) throw new InvalidOperationException($"Vendor {vendorId} is blocked: {vendor.BlockReason}.");
            var lines = pr.Lines.Select((l, i) =>
            {
                if (!_prices.TryGetValue((vendorId, l.ItemId), out var price))
                    throw new InvalidOperationException($"No price for vendor {vendorId} item {l.ItemId}: PO refused.");
                return new PurchaseOrderLine($"PO-L{i + 1}", i + 1, l.ItemId, l.Quantity, 0, 0, 0, 0, price, l.Quantity * price);
            }).ToList();
            var total = lines.Sum(l => l.LineAmount);
            var approval = total < _autoApproveThreshold ? PoApprovalStatus.Approved : PoApprovalStatus.Draft;
            var poNow = DateTimeOffset.UtcNow;
            var poNumber = Next("PO");
            string? poApprovedBy = null;
            DateTimeOffset? poApprovedAt = null;
            if (approval == PoApprovalStatus.Approved)
            {
                poApprovedBy = ErpApprovalActors.AutoApprovePolicy;
                poApprovedAt = poNow;
                _approvals.Add(new ApprovalDto("PO", poNumber, string.Empty, poApprovedBy, false, poNow, true,
                    $"Auto-approved: {total} {pr.Currency} under the {_autoApproveThreshold} threshold.", total, _autoApproveThreshold));
            }
            var po = new PurchaseOrder(poNumber, prNumber, vendorId, vendor.PaymentTermsId, approval,
                PoDocStatus.Open, pr.Currency, source, createdBy, idempotencyKey, lines, CreatedAt: poNow, ApprovedBy: poApprovedBy, ApprovedAt: poApprovedAt);
            foreach (var l in lines) _lastPrice[l.ItemId] = l.UnitPrice;
            _pos[po.PoNumber] = po;
            _requesters[("PO", po.PoNumber)] = pr.Requester;
            _prs[prNumber] = pr with { Status = PrStatus.Closed };
            return po;
        }));

    public Task<VendorConfirmation> RecordVendorConfirmationAsync(string poNumber, DateOnly confirmDate, string source, string createdBy, IReadOnlyList<VendorConfirmLine> lines, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"confirm:{idempotencyKey}", () =>
        {
            if (!_pos.TryGetValue(poNumber, out var po)) throw new InvalidOperationException($"Unknown PO {poNumber}.");
            if (po.ApprovalStatus != PoApprovalStatus.Approved) throw new InvalidOperationException($"PO {poNumber} must be approved before confirmation.");
            foreach (var l in lines)
            {
                var poLine = po.Lines.FirstOrDefault(x => x.PoLineRef == l.PoLineRef)
                    ?? throw new InvalidOperationException($"Unknown PO line {l.PoLineRef}.");
                if (l.ConfirmedQty > poLine.QtyOrdered) throw new InvalidOperationException($"Confirmed {l.ConfirmedQty} exceeds ordered {poLine.QtyOrdered}.");
            }
            var conf = new VendorConfirmation(poNumber, confirmDate, source, createdBy, idempotencyKey, lines.ToList());
            _confirmations[poNumber] = conf;
            _pos[poNumber] = po with { ApprovalStatus = PoApprovalStatus.Confirmed };
            return conf;
        }));

    public Task<AsnDto> CreateAsnAsync(string? asnNumber, string poNumber, string source, string createdBy, IReadOnlyList<AsnCarton> cartons, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"asn:{idempotencyKey}", () =>
        {
            if (!_pos.ContainsKey(poNumber)) throw new InvalidOperationException($"Unknown PO {poNumber}.");
            if (cartons.Count == 0) throw new InvalidOperationException("ASN needs at least one carton.");
            string number;
            if (string.IsNullOrWhiteSpace(asnNumber))
            {
                number = Next("ASN");
            }
            else
            {
                number = asnNumber;
                if (_asns.ContainsKey(number)) throw new InvalidOperationException($"Duplicate ASN {number}.");
            }
            foreach (var c in cartons)
                if (!_cartonIds.Add(c.CartonId)) throw new InvalidOperationException($"Duplicate carton {c.CartonId}.");
            var asn = new AsnDto(number, poNumber, source, createdBy, idempotencyKey, cartons.ToList());
            _asns[number] = asn;
            return asn;
        }));

    public Task<SalesOrder> CreateSalesOrderAsync(string currency, IReadOnlyList<SalesOrderLine> lines, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"so:{idempotencyKey}", () =>
        {
            if (string.IsNullOrWhiteSpace(currency)) throw new InvalidOperationException("Sales order currency is required.");
            if (lines.Count == 0) throw new InvalidOperationException("Sales order needs at least one line.");
            foreach (var l in lines) if (l.Quantity <= 0) throw new InvalidOperationException($"Sales order line {l.ItemId} quantity must be positive.");
            string soNumber;
            do { soNumber = Next("SO"); } while (_salesOrders.ContainsKey(soNumber));
            var so = new SalesOrder(soNumber, SalesOrderStatus.Created, currency, lines.ToList());
            _salesOrders[so.SoNumber] = so;
            return so;
        }));

    public Task UpdateSalesOrderStatusAsync(string soNumber, SalesOrderStatus status, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"sostatus:{idempotencyKey}", () =>
        {
            if (!_salesOrders.TryGetValue(soNumber, out var so)) throw new InvalidOperationException($"Unknown sales order {soNumber}.");
            ThrowIfTerminal(soNumber, so.Status is SalesOrderStatus.Closed or SalesOrderStatus.Cancelled);
            _salesOrders[soNumber] = so with { Status = status };
            return null;
        });
        return Task.CompletedTask;
    }

    public Task BlockVendorAsync(string vendorId, string reason, string blockedBy, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"block:{idempotencyKey}", () =>
        {
            if (!_vendors.TryGetValue(vendorId, out var v)) throw new InvalidOperationException($"Unknown vendor {vendorId}.");
            _vendors[vendorId] = v with { IsBlocked = true, BlockReason = reason };
            return null;
        });
        return Task.CompletedTask;
    }

    public Task UnblockVendorAsync(string vendorId, string unblockedBy, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"unblock:{idempotencyKey}", () =>
        {
            if (!_vendors.TryGetValue(vendorId, out var v)) throw new InvalidOperationException($"Unknown vendor {vendorId}.");
            _vendors[vendorId] = v with { IsBlocked = false, BlockReason = null };
            return null;
        });
        return Task.CompletedTask;
    }

    public Task AddPoNoteAsync(string poNumber, string note, string createdBy, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"note:{idempotencyKey}", () =>
        {
            var list = _poNotes.GetValueOrDefault(poNumber);
            if (list is null) { list = new List<string>(); _poNotes[poNumber] = list; }
            list.Add($"{createdBy}: {note}");
            return null;
        });
        return Task.CompletedTask;
    }

    public Task ClosePoAsync(string poNumber, string closedBy, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"closepo:{idempotencyKey}", () =>
        {
            if (!_pos.TryGetValue(poNumber, out var po)) throw new InvalidOperationException($"Unknown PO {poNumber}.");
            ThrowIfTerminal(poNumber, po.DocStatus is PoDocStatus.Closed or PoDocStatus.Cancelled);
            _pos[poNumber] = po with { DocStatus = PoDocStatus.Closed };
            return null;
        });
        return Task.CompletedTask;
    }

    public Task<ShipmentReceipt> PostReceiptAsync(string poNumber, string supplierPackingSlipNumber, string source, string createdBy, IReadOnlyList<ReceiptLine> lines, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        Task.FromResult(Idem($"receipt:{idempotencyKey}", () =>
        {
            if (!_pos.TryGetValue(poNumber, out var po)) throw new InvalidOperationException($"Unknown PO {poNumber}.");
            if (po.ApprovalStatus != PoApprovalStatus.Confirmed) throw new InvalidOperationException($"PO {poNumber} is not confirmed for receipt.");
            ThrowIfTerminal(poNumber, po.DocStatus is PoDocStatus.Closed or PoDocStatus.Cancelled);
            if (string.IsNullOrWhiteSpace(supplierPackingSlipNumber)) throw new InvalidOperationException("Supplier packing-slip number is required.");
            foreach (var l in lines)
            {
                if (!po.Lines.Any(x => x.PoLineRef == l.PoLineRef)) throw new InvalidOperationException($"Unknown PO line {l.PoLineRef}.");
                if (l.QtyReceived < 0 || l.QtyQuarantined < 0 || l.QtyRejected < 0) throw new InvalidOperationException("Receipt quantities cannot be negative.");
                if (l.QtyQuarantined + l.QtyRejected > l.QtyReceived) throw new InvalidOperationException($"Quarantined plus rejected exceeds received on {l.PoLineRef}.");
                if (string.IsNullOrWhiteSpace(l.LotRef)) throw new InvalidOperationException("Lot reference is required.");
            }
            var businessDate = postingDate ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
            var postedAt = BusinessStamp(postingDate);
            var receipt = new ShipmentReceipt(Next("RCT"), poNumber, supplierPackingSlipNumber, ReceiptStatus.Posted, source, createdBy, idempotencyKey, lines.ToList(),
                CreatedAt: DateTimeOffset.UtcNow, PostedBy: createdBy, PostedAt: postedAt, PostingDate: businessDate);
            _receipts[receipt.ReceiptNumber] = receipt;
            foreach (var l in lines)
            {
                var poLine = po.Lines.First(x => x.PoLineRef == l.PoLineRef);
                _lotMap[l.LotRef] = (poNumber, l.PoLineRef, poLine.ItemId);
                AddInventory(poLine.ItemId, receipt.ReceiptNumber, l.LotRef, InventoryStatus.Available, l.QtyReceived - l.QtyQuarantined - l.QtyRejected);
                if (l.QtyQuarantined > 0) AddInventory(poLine.ItemId, receipt.ReceiptNumber, l.LotRef, InventoryStatus.Quarantined, l.QtyQuarantined);
                if (l.QtyRejected > 0) AddInventory(poLine.ItemId, receipt.ReceiptNumber, l.LotRef, InventoryStatus.Rejected, l.QtyRejected);
            }
            var updated = po.Lines.Select(l =>
            {
                var got = lines.Where(r => r.PoLineRef == l.PoLineRef).Sum(r => r.QtyReceived);
                var quar = lines.Where(r => r.PoLineRef == l.PoLineRef).Sum(r => r.QtyQuarantined);
                var rej = lines.Where(r => r.PoLineRef == l.PoLineRef).Sum(r => r.QtyRejected);
                return l with { QtyReceived = l.QtyReceived + got, QtyQuarantined = l.QtyQuarantined + quar, QtyRejected = l.QtyRejected + rej };
            }).ToList();
            _pos[poNumber] = po with { Lines = updated, DocStatus = DerivePoDoc(po with { Lines = updated }) };
            return receipt;
        }));

    private void AddInventory(string itemId, string receiptNumber, string lotRef, InventoryStatus status, decimal qty)
    {
        if (qty <= 0) return;
        var list = _inventory.GetValueOrDefault(itemId);
        if (list is null) { list = new List<InventoryTransaction>(); _inventory[itemId] = list; }
        list.Add(new InventoryTransaction(itemId, receiptNumber, lotRef, status, qty));
    }

    private static PoDocStatus DerivePoDoc(PurchaseOrder po)
    {
        if (po.DocStatus is PoDocStatus.Closed or PoDocStatus.Cancelled) return po.DocStatus;
        var ordered = po.Lines.Sum(l => l.QtyOrdered);
        var received = po.Lines.Sum(l => l.QtyReceived);
        var invoiced = po.Lines.Sum(l => l.QtyInvoiced);
        if (ordered <= 0) return PoDocStatus.Open;
        var fullyReceived = received >= ordered;
        var fullyInvoiced = invoiced >= ordered;
        if (fullyReceived && fullyInvoiced) return PoDocStatus.Closed;
        if (fullyReceived) return fullyInvoiced ? PoDocStatus.Invoiced : PoDocStatus.Received;
        if (fullyInvoiced) return PoDocStatus.Invoiced;
        if (received > 0 && invoiced > 0) return PoDocStatus.PartiallyInvoiced;
        if (received > 0) return PoDocStatus.PartiallyReceived;
        if (invoiced > 0) return PoDocStatus.PartiallyInvoiced;
        return PoDocStatus.Open;
    }

    public Task QuarantineLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"quar:{idempotencyKey}", () =>
        {
            if (!_holds.ContainsKey(holdId)) throw new InvalidOperationException($"Unknown hold {holdId}.");
            if (!_lotMap.TryGetValue(lotRef, out var map)) throw new InvalidOperationException($"Unknown lot {lotRef}.");
            var list = _inventory.GetValueOrDefault(map.ItemId, []);
            var available = list.Where(t => t.LotRef == lotRef && t.Status == InventoryStatus.Available).Sum(t => t.Quantity);
            if (available <= 0) throw new InvalidOperationException($"Lot {lotRef} has nothing available to quarantine.");
            list.RemoveAll(t => t.LotRef == lotRef && t.Status == InventoryStatus.Available);
            list.Add(new InventoryTransaction(map.ItemId, "QUAR", lotRef, InventoryStatus.Quarantined, available));
            if (_pos.TryGetValue(map.PoNumber, out var po))
            {
                _pos[map.PoNumber] = po with { Lines = po.Lines.Select(l => l.PoLineRef == map.PoLineRef ? l with { QtyQuarantined = l.QtyQuarantined + available } : l).ToList() };
            }
            return null;
        });
        return Task.CompletedTask;
    }

    public Task ReleaseLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"releaselot:{idempotencyKey}", () =>
        {
            if (!_lotMap.TryGetValue(lotRef, out var map)) throw new InvalidOperationException($"Unknown lot {lotRef}.");
            var list = _inventory.GetValueOrDefault(map.ItemId, []);
            var quar = list.Where(t => t.LotRef == lotRef && t.Status == InventoryStatus.Quarantined).Sum(t => t.Quantity);
            if (quar <= 0) throw new InvalidOperationException($"Lot {lotRef} has nothing quarantined to release.");
            list.RemoveAll(t => t.LotRef == lotRef && t.Status == InventoryStatus.Quarantined);
            list.Add(new InventoryTransaction(map.ItemId, "QUAR", lotRef, InventoryStatus.Available, quar));
            if (_pos.TryGetValue(map.PoNumber, out var po))
            {
                _pos[map.PoNumber] = po with { Lines = po.Lines.Select(l => l.PoLineRef == map.PoLineRef ? l with { QtyQuarantined = Math.Max(0, l.QtyQuarantined - quar) } : l).ToList() };
            }
            return null;
        });
        return Task.CompletedTask;
    }

    public Task ConsumeLotAsync(string lotRef, string soNumber, decimal quantity, string actedBy, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"consume:{idempotencyKey}", () =>
        {
            if (!_lotMap.TryGetValue(lotRef, out var map)) throw new InvalidOperationException($"Unknown lot {lotRef}.");
            if (!_salesOrders.ContainsKey(soNumber)) throw new InvalidOperationException($"Unknown sales order {soNumber}.");
            var list = _inventory.GetValueOrDefault(map.ItemId, []);
            var available = list.Where(t => t.LotRef == lotRef && t.Status == InventoryStatus.Available).Sum(t => t.Quantity);
            if (quantity <= 0 || quantity > available) throw new InvalidOperationException($"Cannot consume {quantity} from lot {lotRef} (available {available}).");
            list.RemoveAll(t => t.LotRef == lotRef && t.Status == InventoryStatus.Available);
            if (available - quantity > 0) list.Add(new InventoryTransaction(map.ItemId, "QUAR", lotRef, InventoryStatus.Available, available - quantity));
            list.Add(new InventoryTransaction(map.ItemId, soNumber, lotRef, InventoryStatus.Consumed, quantity));
            return null;
        });
        return Task.CompletedTask;
    }

    public Task<HoldDto> SetHoldAsync(string reasonCode, string note, string? ruleId, string caseId, string setBy, string? documentType, string? documentNumber, string? poLineRef, string? lotRef, string? cartonId, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"hold:{idempotencyKey}", () =>
        {
            var hold = new HoldDto(Next("HLD"), reasonCode, note, ruleId, caseId, setBy, DateTimeOffset.UtcNow,
                HoldStatus.Set, documentType, documentNumber, poLineRef, lotRef, cartonId);
            _holds[hold.HoldId] = hold;
            return hold;
        }));

    public Task ReleaseHoldAsync(string holdId, string releasedBy, string? approvalRef, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"releasehold:{idempotencyKey}", () =>
        {
            if (!_holds.TryGetValue(holdId, out var hold)) throw new InvalidOperationException($"Unknown hold {holdId}.");
            if (hold.Status == HoldStatus.Released) throw new InvalidOperationException($"Hold {holdId} is already released.");
            _holds[holdId] = hold with { Status = HoldStatus.Released };
            _holdHistory.Add(new HoldRelease(holdId, releasedBy, DateTimeOffset.UtcNow, approvalRef));
            return null;
        });
        return Task.CompletedTask;
    }

    public Task<VendorInvoice> CreateInvoiceAsync(string supplierInvoiceNumber, string vendorId, string? poNumber, string source, string createdBy, IReadOnlyList<InvoiceLineInput> lines, string idempotencyKey, DateOnly? invoiceDate = null, CancellationToken ct = default) =>
        Task.FromResult(Idem($"invoice:{idempotencyKey}", () =>
        {
            if (string.IsNullOrWhiteSpace(supplierInvoiceNumber))
                throw new InvalidOperationException("Supplier invoice number is required.");
            if (string.IsNullOrWhiteSpace(vendorId) || string.IsNullOrWhiteSpace(poNumber))
                throw new InvalidOperationException("Vendor or PO number missing; goods receipt required before pay.");
            if (!_vendors.TryGetValue(vendorId, out var vendor)) throw new InvalidOperationException($"Unknown vendor {vendorId}.");
            if (!_pos.TryGetValue(poNumber!, out var po)) throw new InvalidOperationException($"Unknown PO {poNumber}.");
            if (_invoices.ContainsKey((vendorId, supplierInvoiceNumber)))
                throw new InvalidOperationException($"Duplicate invoice {supplierInvoiceNumber} for vendor {vendorId}.");
            if (lines.Count == 0) throw new InvalidOperationException("Invoice needs at least one line.");
            if (!_terms.TryGetValue(vendor.PaymentTermsId, out var terms))
                throw new InvalidOperationException($"No payment terms for vendor {vendorId}.");
            var invoicedOn = invoiceDate ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
            var due = invoicedOn.AddDays(terms.NetDays);
            var stored = lines.Select(l => new VendorInvoiceLine(l.PoLineRef, l.Quantity, l.UnitPrice, LineDisposition.Held, "Awaiting match.")).ToList();
            var inv = new VendorInvoice(supplierInvoiceNumber, vendorId, poNumber, InvoiceStatus.Received, null,
                po.Currency, source, createdBy, idempotencyKey, stored, [],
                CreatedAt: DateTimeOffset.UtcNow, DueDate: due);
            _invoices[(vendorId, supplierInvoiceNumber)] = inv;
            _requesters[("Invoice", InvoiceDoc(vendorId, supplierInvoiceNumber))] = createdBy;
            return inv;
        }));

    public Task RecordMatchResultAsync(string vendorId, string supplierInvoiceNumber, IReadOnlyList<LineMatchResult> results, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"match:{idempotencyKey}", () =>
        {
            if (!_invoices.TryGetValue((vendorId, supplierInvoiceNumber), out var inv))
                throw new InvalidOperationException($"Unknown invoice {supplierInvoiceNumber}.");
            if (!_pos.TryGetValue(inv.PoNumber!, out var po)) throw new InvalidOperationException($"Unknown PO {inv.PoNumber}.");
            var split = new List<VendorInvoiceLine>();
            foreach (var line in inv.Lines)
            {
                var result = results.FirstOrDefault(r => r.PoLineRef == line.PoLineRef);
                var poLine = po.Lines.FirstOrDefault(l => l.PoLineRef == line.PoLineRef);
                if (poLine is null || result is null || !result.Matched)
                {
                    var why = result?.Message ?? "AP-C3: no PO line match.";
                    split.Add(line with { Disposition = LineDisposition.Held, HoldReason = why });
                    continue;
                }
                if (line.UnitPrice < 0) throw new InvalidOperationException("Invoice price cannot be negative.");
                var poPrice = poLine.UnitPrice;
                var priceOk = poPrice == 0 || Math.Abs(line.UnitPrice - poPrice) / poPrice <= PriceTolerance;
                if (!priceOk)
                {
                    split.Add(line with { Disposition = LineDisposition.Held, HoldReason = $"AP-C2: price {line.UnitPrice} vs PO {poPrice}." });
                    continue;
                }
                var acceptedSoFar = PoAcceptedInvoiced(po.PoNumber, poLine.PoLineRef);
                var available = Math.Max(0, poLine.QtyReceived - poLine.QtyQuarantined - poLine.QtyRejected - acceptedSoFar);
                var acceptedQty = Math.Min(line.Quantity, available);
                var heldQty = line.Quantity - acceptedQty;
                if (acceptedQty > 0) split.Add(new VendorInvoiceLine(line.PoLineRef, acceptedQty, line.UnitPrice, LineDisposition.Accepted, null));
                if (heldQty > 0)
                {
                    var why = acceptedQty <= 0 && available <= 0
                        ? $"AP-C3: qty {line.Quantity} exceeds available received {available}."
                        : $"Quarantine hold: {heldQty} of {line.Quantity} held.";
                    split.Add(new VendorInvoiceLine(line.PoLineRef, heldQty, line.UnitPrice, LineDisposition.Held, why));
                }
            }
            _invoices[(vendorId, supplierInvoiceNumber)] = inv with { Lines = split, MatchResults = results.ToList(), Status = InvoiceStatus.Matched };
            return null;
        });
        return Task.CompletedTask;
    }

    public Task<VendorInvoice> PostInvoiceAsync(string vendorId, string supplierInvoiceNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        Task.FromResult(Idem($"postinv:{idempotencyKey}", () =>
        {
            if (!_invoices.TryGetValue((vendorId, supplierInvoiceNumber), out var inv))
                throw new InvalidOperationException($"Unknown invoice {supplierInvoiceNumber}.");
            if (inv.Status is InvoiceStatus.Posted or InvoiceStatus.Paid or InvoiceStatus.Closed)
                throw new InvalidOperationException($"Invoice {supplierInvoiceNumber} is already posted and immutable.");
            var docKey = InvoiceDoc(vendorId, supplierInvoiceNumber);
            if (!_approvals.Any(a => a.DocumentType == "Invoice" && a.DocumentNumber == docKey && a.Approved))
                throw new InvalidOperationException($"Invoice {supplierInvoiceNumber} must be approved before posting.");
            var posted = inv with { Status = InvoiceStatus.Posted, Voucher = Next("V"), PostedBy = postedBy, PostedAt = BusinessStamp(postingDate) };
            _invoices[(vendorId, supplierInvoiceNumber)] = posted;
            _invoicePostedAt[docKey] = DateTimeOffset.UtcNow;
            if (posted.PoNumber is not null && _pos.TryGetValue(posted.PoNumber, out var po))
            {
                var updated = po.Lines.Select(l =>
                {
                    var billed = posted.Lines.Where(x => x.PoLineRef == l.PoLineRef).Sum(x => x.Quantity);
                    return l with { QtyInvoiced = l.QtyInvoiced + billed };
                }).ToList();
                _pos[posted.PoNumber] = po with { Lines = updated, DocStatus = DerivePoDoc(po with { Lines = updated }) };
            }
            return posted;
        }));

    public Task ReleaseHeldInvoiceLineAsync(string vendorId, string supplierInvoiceNumber, string? poLineRef, decimal quantity, string releasedBy, string idempotencyKey, CancellationToken ct = default)
    {
        Idem<object?>($"releaseline:{idempotencyKey}", () =>
        {
            if (!_invoices.TryGetValue((vendorId, supplierInvoiceNumber), out var inv))
                throw new InvalidOperationException($"Unknown invoice {supplierInvoiceNumber}.");
            if (inv.Status is InvoiceStatus.Paid or InvoiceStatus.Closed) throw new InvalidOperationException($"Invoice {supplierInvoiceNumber} is terminal.");
            var held = inv.Lines.Where(l => l.PoLineRef == poLineRef && l.Disposition == LineDisposition.Held).Sum(l => l.Quantity);
            if (quantity <= 0 || quantity > held) throw new InvalidOperationException($"Cannot release {quantity} (held {held}).");
            var lines = new List<VendorInvoiceLine>();
            var remaining = quantity;
            foreach (var l in inv.Lines)
            {
                if (l.PoLineRef != poLineRef || l.Disposition != LineDisposition.Held || remaining <= 0) { lines.Add(l); continue; }
                var take = Math.Min(l.Quantity, remaining);
                remaining -= take;
                if (l.Quantity - take > 0) lines.Add(l with { Quantity = l.Quantity - take });
                lines.Add(new VendorInvoiceLine(l.PoLineRef, take, l.UnitPrice, LineDisposition.Accepted, null));
            }
            _invoices[(vendorId, supplierInvoiceNumber)] = inv with { Lines = lines };
            _heldReleases.Add(new HeldRelease(vendorId, supplierInvoiceNumber, poLineRef, quantity, releasedBy, DateTimeOffset.UtcNow));
            return null;
        });
        return Task.CompletedTask;
    }

    public Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, string source, string createdBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        CreatePaymentDraftAsync(invoices, null, source, createdBy, idempotencyKey, postingDate, ct);

    public Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, DateOnly? payUpToDate, string source, string createdBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        Task.FromResult(Idem($"paydraft:{idempotencyKey}", () =>
        {
            if (invoices.Count == 0) throw new InvalidOperationException("Payment needs at least one invoice.");
            // One draft may pay many vendors (a payment run); every line vendor
            // needs verified bank details, and the header snapshots the bank of
            // the alphabetically-first paying line for the posting check.
            var currency = string.Empty;
            var lines = new List<PaymentLine>();
            var due = DateOnly.MinValue;
            var banksByVendor = new Dictionary<string, VendorBankDto>(StringComparer.Ordinal);
            foreach (var key in invoices)
            {
                if (!_invoices.TryGetValue((key.VendorId, key.SupplierInvoiceNumber), out var inv))
                    throw new InvalidOperationException($"Unknown invoice {key.SupplierInvoiceNumber}.");
                if (inv.Status is not InvoiceStatus.Posted and not InvoiceStatus.PartiallyPaid)
                    throw new InvalidOperationException($"Invoice {key.SupplierInvoiceNumber} must be posted before pay.");
                if (string.IsNullOrEmpty(currency)) currency = inv.Currency;
                else if (currency != inv.Currency) throw new InvalidOperationException("Payment lines must share one currency.");
                if (!_banks.TryGetValue(inv.VendorId, out var lineBank))
                    throw new InvalidOperationException($"No bank details for vendor {inv.VendorId}.");
                if (!lineBank.Verified) throw new InvalidOperationException("Bank details changed after verification: payment refused.");
                var dueDate = inv.DueDate ?? FallbackDueDate(inv);
                if (payUpToDate.HasValue && dueDate > payUpToDate.Value) continue;
                if (due > dueDate || due == DateOnly.MinValue) due = dueDate;
                // Terms set the due date shown on the draft; like D365, the ERP
                // does not refuse early payment (PROJECT.md Done needs same-day pay runs).
                var accepted = inv.Lines.Where(l => l.Disposition == LineDisposition.Accepted).Sum(l => l.Quantity * l.UnitPrice);
                var paid = _paidAmounts.GetValueOrDefault(PaidKey(inv.VendorId, inv.SupplierInvoiceNumber));
                var remaining = accepted - paid;
                if (remaining > 0)
                {
                    banksByVendor.TryAdd(inv.VendorId, lineBank);
                    lines.Add(new PaymentLine(inv.VendorId, inv.SupplierInvoiceNumber, remaining));
                }
            }
            if (lines.Count == 0) throw new InvalidOperationException("Nothing due: held value excluded, remainder stays open.");
            var draftId = Next("PAYDRAFT");
            var firstPaying = lines.OrderBy(l => l.VendorId, StringComparer.Ordinal)
                .ThenBy(l => l.SupplierInvoiceNumber, StringComparer.Ordinal).First();
            var bank = banksByVendor[firstPaying.VendorId];
            var draft = new Payment(draftId, PaymentStatus.Draft, currency,
                new VendorBankSnapshot(bank.AccountRef, bank.Verified), due, source, createdBy, idempotencyKey, lines,
                CreatedAt: BusinessStamp(postingDate));
            _payments[draftId] = draft;
            _requesters[("Payment", draftId)] = createdBy;
            return draft;
        }));

    private DateOnly FallbackDueDate(VendorInvoice inv)
    {
        if (!_terms.TryGetValue(_vendors[inv.VendorId].PaymentTermsId, out var terms))
            throw new InvalidOperationException($"No payment terms for vendor {inv.VendorId}.");
        var postedAt = _invoicePostedAt.GetValueOrDefault(InvoiceDoc(inv.VendorId, inv.SupplierInvoiceNumber), DateTimeOffset.UtcNow);
        return DateOnly.FromDateTime(postedAt.DateTime).AddDays(terms.NetDays);
    }

    public Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) =>
        Task.FromResult(Idem($"paypost:{idempotencyKey}", () =>
        {
            if (!_payments.TryGetValue(draftPaymentNumber, out var draft)) throw new InvalidOperationException($"Unknown payment draft {draftPaymentNumber}.");
            if (draft.Status != PaymentStatus.Approved) throw new InvalidOperationException($"Payment {draftPaymentNumber} must be approved before posting.");
            VendorBankDto? firstBank = null;
            var orderedVendors = draft.Lines
                .OrderBy(l => l.VendorId, StringComparer.Ordinal)
                .ThenBy(l => l.SupplierInvoiceNumber, StringComparer.Ordinal)
                .Select(l => l.VendorId).Distinct(StringComparer.Ordinal);
            foreach (var vendorId in orderedVendors)
            {
                if (!_banks.TryGetValue(vendorId, out var lineBank) || !lineBank.Verified)
                    throw new InvalidOperationException("Bank details changed after verification: payment refused.");
                firstBank ??= lineBank;
            }
            var bank = firstBank!;
            if (bank.AccountRef != draft.PayeeBank.AccountRef)
                throw new InvalidOperationException("Bank details changed after verification: payment refused.");
            var posted = draft with
            {
                PaymentNumber = Next("PAY"),
                Status = PaymentStatus.Posted,
                PayeeBank = new VendorBankSnapshot(bank.AccountRef, bank.Verified),
                IdempotencyKey = idempotencyKey,
                PostedBy = postedBy,
                PostedAt = BusinessStamp(postingDate)
            };
            foreach (var line in posted.Lines)
            {
                var key = PaidKey(line.VendorId, line.SupplierInvoiceNumber);
                _paidAmounts[key] = _paidAmounts.GetValueOrDefault(key) + line.Amount;
                var inv = _invoices[(line.VendorId, line.SupplierInvoiceNumber)];
                var totalInvoiced = inv.Lines.Sum(l => l.Quantity * l.UnitPrice);
                var settled = _paidAmounts[key];
                var remaining = totalInvoiced - settled;
                _settlements.Add(new Settlement(line.VendorId, line.SupplierInvoiceNumber, posted.PaymentNumber, line.Amount, Math.Max(0, remaining)));
                var accepted = inv.Lines.Where(l => l.Disposition == LineDisposition.Accepted).Sum(l => l.Quantity * l.UnitPrice);
                var held = inv.Lines.Where(l => l.Disposition == LineDisposition.Held).Sum(l => l.Quantity * l.UnitPrice);
                _invoices[(line.VendorId, line.SupplierInvoiceNumber)] = inv with
                {
                    Status = settled >= accepted && held <= 0 ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid
                };
            }
            _payments[posted.PaymentNumber] = posted;
            _payments.Remove(draftPaymentNumber);
            return posted;
        }));

    public Task<VendorDto> CreateVendorAsync(string? vendorId, string name, string countryOfOrigin, string paymentTermsId, string? email, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"vendor:{idempotencyKey}", () =>
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Vendor name is required.");
            if (string.IsNullOrWhiteSpace(countryOfOrigin)) throw new InvalidOperationException("Vendor country of origin is required.");
            if (string.IsNullOrWhiteSpace(paymentTermsId)) throw new InvalidOperationException("Payment terms are required.");
            if (!_terms.ContainsKey(paymentTermsId)) throw new InvalidOperationException($"Unknown payment terms {paymentTermsId}.");
            string id;
            if (string.IsNullOrWhiteSpace(vendorId))
            {
                do { id = Next("VND"); } while (_vendors.ContainsKey(id));
            }
            else
            {
                id = vendorId;
                if (_vendors.ContainsKey(id)) throw new InvalidOperationException($"Vendor {id} already exists.");
            }
            var vendor = new VendorDto(id, name, countryOfOrigin, false, null, paymentTermsId, email);
            _vendors[id] = vendor;
            return vendor;
        }));

    public Task<ItemDto> CreateItemAsync(string itemId, string description, string unit, bool isCritical, int unitsPerCarton, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"item:{idempotencyKey}", () =>
        {
            if (string.IsNullOrWhiteSpace(itemId)) throw new InvalidOperationException("Item id is required.");
            if (_items.ContainsKey(itemId)) throw new InvalidOperationException($"Item {itemId} already exists.");
            if (unitsPerCarton <= 0) throw new InvalidOperationException("Units per carton must be above zero.");
            var item = new ItemDto(itemId, description, isCritical, unitsPerCarton, unit);
            _items[itemId] = item;
            return item;
        }));

    public Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"bankchange:{idempotencyKey}", () =>
        {
            if (!_vendors.ContainsKey(vendorId)) throw new InvalidOperationException($"Unknown vendor {vendorId}.");
            var bank = new VendorBankDto(vendorId, newAccountRef, false, null);
            _banks[vendorId] = bank;
            return bank;
        }));

    public Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(Idem($"bankverify:{idempotencyKey}", () =>
        {
            if (!_banks.TryGetValue(vendorId, out var bank)) throw new InvalidOperationException($"No bank details for vendor {vendorId}.");
            var verified = bank with { Verified = true, VerifiedAt = DateTimeOffset.UtcNow };
            _banks[vendorId] = verified;
            return verified;
        }));
}

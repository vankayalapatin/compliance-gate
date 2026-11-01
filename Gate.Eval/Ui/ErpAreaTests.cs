// ERP area service tests: full-list reads with honest notes when the ERP
// layer has not implemented them, record pages with header/lines/related/
// actions per state, and attempt-for-real actions plus creates where every
// ERP refusal surfaces with its reason. The double below subclasses the
// in-memory fake ERP and adds only the new full-list reads (the ERP lane
// implements those on the real adapter concurrently); every write runs
// through the fake's real controls. Test data is local; nothing depends
// on the demo numbers. No live calls.
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class ErpAreaTests
{
    // Own double: the fake ERP behind a delegating wrapper, plus settable
    // full-list reads. Every other member forwards untouched, so every
    // control refusal below comes from real ERP logic.
    private sealed class ListErpAdapter : IErpAdapter
    {
        private readonly InMemoryErpAdapter _inner = new();

        public List<SalesOrder> SalesOrders { get; } = [];
        public List<PurchaseRequisition> PurchaseRequisitions { get; } = [];
        public List<PurchaseOrder> PurchaseOrders { get; } = [];
        public List<VendorInvoice> VendorInvoices { get; } = [];
        public List<Payment> Payments { get; } = [];
        public List<ShipmentReceipt> ShipmentReceipts { get; } = [];
        public List<AsnDto> Asns { get; } = [];
        public List<PurchaseOrder> PosByVendor { get; } = [];
        public List<ItemDto> Items { get; } = [];
        public List<InventoryTransaction> AllTxns { get; } = [];
        public List<PendingApprovalDto> PendingApprovals { get; } = [];

        // Simulates an ERP layer that has not implemented the full-list
        // reads yet: every list read throws like the interface defaults.
        public bool ThrowNotSupported { get; set; }

        // Seed helpers used below (the fake's own setup surface).
        public void SeedSalesOrder(SalesOrder so) => _inner.SeedSalesOrder(so);
        public void SeedBom(string itemId, IEnumerable<BomLine> lines) => _inner.SeedBom(itemId, lines);
        public void SeedItem(ItemDto item) => _inner.SeedItem(item);
        public void SeedStock(string itemId, decimal qty) => _inner.SeedStock(itemId, qty);
        public void SeedVendor(VendorDto vendor) => _inner.SeedVendor(vendor);
        public void SeedVendorDocuments(string vendorId, IEnumerable<VendorDocumentDto> docs) =>
            _inner.SeedVendorDocuments(vendorId, docs);
        public void SeedPaymentTerms(PaymentTermsDto terms) => _inner.SeedPaymentTerms(terms);
        public void SeedVendorBank(VendorBankDto bank) => _inner.SeedVendorBank(bank);
        public void SeedUser(UserDto user) => _inner.SeedUser(user);
        public void SeedVendorPrice(string vendorId, string itemId, decimal unitPrice) =>
            _inner.SeedVendorPrice(vendorId, itemId, unitPrice);
        public void SeedBaseCurrency(string currency) => _inner.SeedBaseCurrency(currency);
        public void SeedAutoApproveThreshold(decimal threshold) => _inner.SeedAutoApproveThreshold(threshold);

        public Task<IReadOnlyList<SalesOrder>> GetSalesOrdersAsync(CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<SalesOrder>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<SalesOrder>>(SalesOrders);

        public Task<IReadOnlyList<PurchaseRequisition>> GetPurchaseRequisitionsAsync(CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<PurchaseRequisition>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<PurchaseRequisition>>(PurchaseRequisitions);

        public Task<IReadOnlyList<PurchaseOrder>> GetPurchaseOrdersAsync(CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<PurchaseOrder>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<PurchaseOrder>>(PurchaseOrders);

        public Task<IReadOnlyList<VendorInvoice>> GetVendorInvoicesAsync(CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<VendorInvoice>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<VendorInvoice>>(VendorInvoices);

        public Task<IReadOnlyList<Payment>> GetPaymentsAsync(CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<Payment>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<Payment>>(Payments);

        public Task<IReadOnlyList<ShipmentReceipt>> GetShipmentReceiptsAsync(CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<ShipmentReceipt>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<ShipmentReceipt>>(ShipmentReceipts);

        public Task<IReadOnlyList<AsnDto>> GetAsnsAsync(CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<AsnDto>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<AsnDto>>(Asns);

        public Task<IReadOnlyList<PurchaseOrder>> GetPurchaseOrdersByVendorAsync(string vendorId, CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<PurchaseOrder>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<PurchaseOrder>>(PosByVendor);

        public Task<IReadOnlyList<ItemDto>> GetItemsAsync(CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<ItemDto>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<ItemDto>>(Items);

        public Task<IReadOnlyList<InventoryTransaction>> GetAllInventoryTransactionsAsync(CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<InventoryTransaction>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<InventoryTransaction>>(AllTxns);

        public Task<IReadOnlyList<PendingApprovalDto>> GetPendingApprovalsAsync(CancellationToken ct = default) =>
            ThrowNotSupported
                ? Task.FromException<IReadOnlyList<PendingApprovalDto>>(new NotSupportedException("Full-list read is implemented by the ERP layer."))
                : Task.FromResult<IReadOnlyList<PendingApprovalDto>>(PendingApprovals);

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
        public Task<PurchaseRequisition> CreatePrAsync(string requester, string source, string createdBy, IReadOnlyList<PurchaseRequisitionLine> lines, string idempotencyKey, CancellationToken ct = default) =>
            _inner.CreatePrAsync(requester, source, createdBy, lines, idempotencyKey, ct);
        public Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default) =>
            _inner.GetPrAsync(prNumber, ct);
        public Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, string submittedBy, string idempotencyKey, CancellationToken ct = default) =>
            _inner.SubmitForApprovalAsync(documentType, documentNumber, routedTo, submittedBy, idempotencyKey, ct);
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
        public Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, DateOnly? payUpToDate, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
            _inner.CreatePaymentDraftAsync(invoices, payUpToDate, source, createdBy, idempotencyKey, ct);
        public Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, CancellationToken ct = default) =>
            _inner.PostPaymentAsync(draftPaymentNumber, postedBy, idempotencyKey, ct);
        public Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default) =>
            _inner.RecordVendorBankChangeAsync(vendorId, newAccountRef, source, changedBy, idempotencyKey, ct);
        public Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default) =>
            _inner.VerifyVendorBankAsync(vendorId, verifiedBy, idempotencyKey, ct);
        public Task<VendorDto> CreateVendorAsync(string? vendorId, string name, string countryOfOrigin, string paymentTermsId, string? email, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
            _inner.CreateVendorAsync(vendorId, name, countryOfOrigin, paymentTermsId, email, source, createdBy, idempotencyKey, ct);
        public Task<ItemDto> CreateItemAsync(string itemId, string description, string unit, bool isCritical, int unitsPerCarton, string source, string createdBy, string idempotencyKey, CancellationToken ct = default) =>
            _inner.CreateItemAsync(itemId, description, unit, isCritical, unitsPerCarton, source, createdBy, idempotencyKey, ct);
    }

    private static string WriteConfig(string manifestJson = """{"scenario":"ui-erp","documents":[]}""")
    {
        var dir = UiTestSupport.NewDir("gate-ui-erp");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "scenario.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "run-manifest.json"), manifestJson);
        return dir;
    }

    private static ErpViewService Views(IErpAdapter erp, string dir) =>
        new(erp,
            new CaseViewService(erp, new CaseCacheStore(Path.Combine(dir, "cache")),
                new DockPhotoService(Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Scenario:File"] = Path.Combine(dir, "scenario.json"),
                        ["Scenario:Manifest"] = Path.Combine(dir, "run-manifest.json"),
                    })
                    .Build(),
                UiTestLogger<CaseViewService>.Instance),
            UiTestLogger<ErpViewService>.Instance);

    private static ErpActionService Actions(IErpAdapter erp) =>
        new(erp, UiTestLogger<ErpActionService>.Instance);

    private static void SeedUsers(ListErpAdapter erp)
    {
        erp.SeedBaseCurrency("TST");
        // Low threshold: the 50-value test documents stay Draft so the
        // submit/decide paths run for real instead of auto-approving.
        erp.SeedAutoApproveThreshold(10m);
        erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        erp.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        erp.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));
        erp.SeedUser(new UserDto("finance", "finance", 1_000_000m, "TST"));
        erp.SeedUser(new UserDto("ap-clerk", "ap", 1_000_000m, "TST"));
        erp.SeedVendor(new VendorDto("V-E", "Erp Vendor", "US", false, null, "NET0"));
        erp.SeedVendorBank(new VendorBankDto("V-E", "BANK-E", true, DateTimeOffset.UtcNow));
        erp.SeedVendorPrice("V-E", "ITEM-E", 10m);
        erp.SeedItem(new ItemDto("ITEM-E", "Erp item", true, 5, "EA"));
    }

    private static async Task<string> SeedApprovedPrAsync(ListErpAdapter erp)
    {
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.Human, "requester",
            [new PurchaseRequisitionLine("ITEM-E", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        return pr.PrNumber;
    }

    [Fact]
    public async Task FullLists_ReturnRowsSorted()
    {
        var erp = new ListErpAdapter();
        erp.PurchaseRequisitions.AddRange(
        [
            new PurchaseRequisition("PR-B", PrStatus.Draft, "requester", "TST", 5m,
                RecordSource.Human, "requester", "k1", []),
            new PurchaseRequisition("PR-A", PrStatus.Draft, "requester", "TST", 5m,
                RecordSource.Human, "requester", "k2", []),
        ]);
        erp.VendorInvoices.Add(new VendorInvoice("INV-9", "V-E", null, InvoiceStatus.Received, null,
            "TST", RecordSource.Human, "ap-clerk", "k3", [], []));
        var views = Views(erp, WriteConfig());

        var prs = await views.ListPurchaseRequisitionsAsync();
        var invoices = await views.ListInvoicesAsync();

        Assert.Equal(["PR-A", "PR-B"], prs.Rows.Select(p => p.PrNumber).ToList());
        Assert.Empty(prs.Notes);
        Assert.Single(invoices.Rows);
    }

    [Fact]
    public async Task FullLists_NoteWhenErpLayerHasNotImplementedThem()
    {
        // Gap simulation: the list reads throw like the interface defaults,
        // so every list renders empty with an honest note instead of failing.
        var erp = new ListErpAdapter { ThrowNotSupported = true };
        var views = Views(erp, WriteConfig());

        Assert.Empty((await views.ListSalesOrdersAsync()).Rows);
        Assert.Empty((await views.ListPurchaseRequisitionsAsync()).Rows);
        Assert.Empty((await views.ListPurchaseOrdersAsync()).Rows);
        Assert.Empty((await views.ListAsnsAsync()).Rows);
        Assert.Empty((await views.ListReceiptsAsync()).Rows);
        Assert.Empty((await views.ListInvoicesAsync()).Rows);
        Assert.Empty((await views.ListPaymentsAsync()).Rows);
        Assert.Empty((await views.ListItemsAsync()).Rows);
        Assert.Empty((await views.ListStockAsync()).Rows);
        var pos = await views.ListPurchaseOrdersAsync();
        Assert.Contains(pos.Notes, n => n.Contains("not implemented by the ERP layer yet"));
        var stock = await views.ListStockAsync();
        Assert.Contains(stock.Notes, n => n.Contains("not implemented by the ERP layer yet"));
    }

    [Fact]
    public async Task FullLists_ReflectRealWrites()
    {
        // Integration with the ERP lane's real reads: documents written
        // through the adapter show up on the plain fake's full lists.
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("TST");
        erp.SeedAutoApproveThreshold(10m);
        erp.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        erp.SeedVendorPrice("V-E", "ITEM-E", 10m);
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.Human, "requester",
            [new PurchaseRequisitionLine("ITEM-E", 5m, "EA")], Guid.NewGuid().ToString("N"));
        var views = Views(erp, WriteConfig());

        var prs = await views.ListPurchaseRequisitionsAsync();
        var approved = await views.ListApprovedPrsAsync();

        Assert.Contains(prs.Rows, p => p.PrNumber == pr.PrNumber);
        Assert.Empty(prs.Notes);
        Assert.Empty(approved.Rows);
    }

    [Fact]
    public async Task StockList_GroupsLotsAndReadsStock()
    {
        var erp = new ListErpAdapter();
        erp.SeedItem(new ItemDto("ITEM-E", "Erp item", true, 5, "EA"));
        erp.SeedStock("ITEM-E", 12m);
        erp.AllTxns.AddRange(
        [
            new InventoryTransaction("ITEM-E", "RCT-1", "LOT-B", InventoryStatus.Available, 7m),
            new InventoryTransaction("ITEM-E", "RCT-1", "LOT-A", InventoryStatus.Available, 5m),
        ]);
        erp.Items.Add(new ItemDto("ITEM-E", "Erp item", true, 5, "EA"));
        var views = Views(erp, WriteConfig());

        var stock = await views.ListStockAsync();

        var row = Assert.Single(stock.Rows);
        Assert.Equal("ITEM-E", row.ItemId);
        Assert.Equal("Erp item", row.Description);
        Assert.Equal(12m, row.Stock);
        Assert.Equal(["LOT-A", "LOT-B"], row.Lots.Select(l => l.LotRef).ToList());
    }

    [Fact]
    public async Task PrRecord_DraftOffersSubmitWithHeaderAndLines()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-E", 5m, "EA")], Guid.NewGuid().ToString("N"));
        var views = Views(erp, WriteConfig());

        var record = await views.GetRecordAsync("pr", pr.PrNumber);

        Assert.NotNull(record);
        Assert.Equal(RecordSource.P2pAgent, record.SourceName);
        Assert.Contains(record.Header, f => f.Label == "Requester" && f.Value == "requester");
        var lines = Assert.Single(record.LineTables.Where(t => t.Title == "Lines"));
        Assert.Single(lines.Rows);
        var submit = Assert.Single(record.Actions);
        Assert.Equal(ErpActions.Submit, submit.Id);
        Assert.Contains(submit.Inputs, i => i.Key == ErpActions.InRoutedTo && i.Required);
    }

    [Fact]
    public async Task PrRecord_ActionsFollowStatus()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.Human, "requester",
            [new PurchaseRequisitionLine("ITEM-E", 5m, "EA")], Guid.NewGuid().ToString("N"));
        var views = Views(erp, WriteConfig());

        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        var submitted = await views.GetRecordAsync("pr", pr.PrNumber);
        Assert.NotNull(submitted);
        Assert.Equal([ErpActions.Approve, ErpActions.Reject],
            submitted.Actions.Select(a => a.Id).ToList());

        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        var approved = await views.GetRecordAsync("pr", pr.PrNumber);
        Assert.NotNull(approved);
        Assert.Empty(approved.Actions);
        Assert.Contains(approved.Related, r => r.Href.Contains("erp/new/po/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Record_MissingDocRendersShellNote()
    {
        var views = Views(new ListErpAdapter(), WriteConfig());

        var po = await views.GetRecordAsync("po", "PO-GONE");
        var inv = await views.GetRecordAsync("invoice", "V-E", "INV-GONE");

        Assert.NotNull(po);
        Assert.Contains(po.Notes, n => n.Contains("PO-GONE") && n.Contains("not found in ERP"));
        Assert.NotNull(inv);
        Assert.Contains(inv.Notes, n => n.Contains("V-E/INV-GONE") && n.Contains("not found in ERP"));
    }

    [Fact]
    public async Task Record_UnknownKindReturnsNull()
    {
        var views = Views(new ListErpAdapter(), WriteConfig());

        Assert.Null(await views.GetRecordAsync("ledger", "X-1"));
    }

    [Fact]
    public async Task Record_OpeningNumbersComeFromManifest()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.Human, "requester",
            [new PurchaseRequisitionLine("ITEM-E", 5m, "EA")], Guid.NewGuid().ToString("N"));
        var dir = WriteConfig(
            $$"""{"scenario":"ui-erp","documents":[{"event":"openPoInbound.pr","docType":"PR","number":"{{pr.PrNumber}}"}]}""");
        var views = Views(erp, dir);

        var record = await views.GetRecordAsync("pr", pr.PrNumber);

        Assert.NotNull(record);
        Assert.Contains(pr.PrNumber, record.OpeningNumbers);
    }

    [Fact]
    public async Task Attempt_SubmitThenDecideHappyPath()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.Human, "requester",
            [new PurchaseRequisitionLine("ITEM-E", 5m, "EA")], Guid.NewGuid().ToString("N"));
        var actions = Actions(erp);

        var submit = await actions.AttemptAsync("pr", pr.PrNumber, null, ErpActions.Submit,
            new Dictionary<string, string> { [ErpActions.InRoutedTo] = "approver" }, "requester");
        var decide = await actions.AttemptAsync("pr", pr.PrNumber, null, ErpActions.Approve,
            new Dictionary<string, string> { [ErpActions.InComment] = "looks good" }, "approver");

        Assert.True(submit.Ok, submit.Message);
        Assert.Contains("submitted to approver", submit.Message);
        Assert.True(decide.Ok, decide.Message);
        Assert.Contains("approved by approver", decide.Message);
    }

    [Fact]
    public async Task Attempt_SelfApprovalRefusedWithReason()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var pr = await erp.CreatePrAsync(
            "approver", RecordSource.Human, "approver",
            [new PurchaseRequisitionLine("ITEM-E", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync(
            "PR", pr.PrNumber, "approver", "approver", Guid.NewGuid().ToString("N"));
        var actions = Actions(erp);

        var outcome = await actions.AttemptAsync("pr", pr.PrNumber, null, ErpActions.Approve,
            new Dictionary<string, string>(), "approver");

        Assert.False(outcome.Ok);
        Assert.Contains("Requester cannot approve", outcome.Message);
    }

    [Fact]
    public async Task Attempt_WrongUserRefusedWithReason()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.Human, "requester",
            [new PurchaseRequisitionLine("ITEM-E", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync(
            "PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        var actions = Actions(erp);

        var outcome = await actions.AttemptAsync("pr", pr.PrNumber, null, ErpActions.Approve,
            new Dictionary<string, string>(), "finance");

        Assert.False(outcome.Ok);
        Assert.Contains("not routed", outcome.Message);
    }

    [Fact]
    public async Task Attempt_RequiresActingUserAndKnownAction()
    {
        var actions = Actions(new ListErpAdapter());

        var noUser = await actions.AttemptAsync("pr", "PR-X", null, ErpActions.Submit,
            new Dictionary<string, string> { [ErpActions.InRoutedTo] = "approver" }, null);
        var unknown = await actions.AttemptAsync("pr", "PR-X", null, "launch",
            new Dictionary<string, string>(), "approver");

        Assert.False(noUser.Ok);
        Assert.Contains("acting user", noUser.Message);
        Assert.False(unknown.Ok);
        Assert.Contains("Unknown action", unknown.Message);
    }

    [Fact]
    public async Task Attempt_PostInvoiceRequiresApprovalFirst()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var prNumber = await SeedApprovedPrAsync(erp);
        var po = await erp.CreatePoAsync(
            prNumber, "V-E", RecordSource.Human, "requester", Guid.NewGuid().ToString("N"));
        var line = po.Lines.Single();
        await erp.CreateInvoiceAsync("INV-E1", "V-E", po.PoNumber, RecordSource.Human, "ap-clerk",
            [new InvoiceLineInput(line.PoLineRef, 5m, line.UnitPrice)], Guid.NewGuid().ToString("N"));
        var actions = Actions(erp);

        var outcome = await actions.AttemptAsync("invoice", "V-E", "INV-E1",
            ErpActions.PostInvoice, new Dictionary<string, string>(), "ap-clerk");

        Assert.False(outcome.Ok);
        Assert.Contains("must be approved before posting", outcome.Message);
    }

    [Fact]
    public async Task Attempt_BlockUnblockVerifyVendor()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var actions = Actions(erp);

        var noReason = await actions.AttemptAsync("vendor", "V-E", null, ErpActions.BlockVendor,
            new Dictionary<string, string>(), "approver");
        var block = await actions.AttemptAsync("vendor", "V-E", null, ErpActions.BlockVendor,
            new Dictionary<string, string> { [ErpActions.InReason] = "failing docs" }, "approver");
        var unblock = await actions.AttemptAsync("vendor", "V-E", null, ErpActions.UnblockVendor,
            new Dictionary<string, string>(), "approver");
        var verify = await actions.AttemptAsync("vendor", "V-E", null, ErpActions.VerifyBank,
            new Dictionary<string, string>(), "ap-clerk");

        Assert.False(noReason.Ok);
        Assert.Contains("reason is required", noReason.Message);
        Assert.True(block.Ok, block.Message);
        Assert.True(unblock.Ok, unblock.Message);
        Assert.True(verify.Ok, verify.Message);
        Assert.Contains("BANK-E", verify.Message);
    }

    [Fact]
    public async Task Attempt_ReleaseHoldTwiceRefusesSecond()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var hold = await erp.SetHoldAsync(
            "DOC", "missing declaration", "BAT-LI-01", "CASE-E", "approver",
            "PO", "PO-E", null, null, null, Guid.NewGuid().ToString("N"));
        var actions = Actions(erp);

        var first = await actions.AttemptAsync("hold", hold.HoldId, null, ErpActions.ReleaseHold,
            new Dictionary<string, string>(), "approver");
        var second = await actions.AttemptAsync("hold", hold.HoldId, null, ErpActions.ReleaseHold,
            new Dictionary<string, string>(), "approver");

        Assert.True(first.Ok, first.Message);
        Assert.False(second.Ok);
        Assert.Contains("already released", second.Message);
    }

    [Fact]
    public async Task Attempt_UpdateSoStatusSurfacesUnknownOrder()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        erp.SeedSalesOrder(new SalesOrder("SO-E", SalesOrderStatus.Created, "TST",
            [new SalesOrderLine("ITEM-E", 5m)]));
        var actions = Actions(erp);

        var ok = await actions.AttemptAsync("so", "SO-E", null, ErpActions.UpdateSoStatus,
            new Dictionary<string, string> { [ErpActions.InStatus] = nameof(SalesOrderStatus.Confirmed) },
            "requester");
        var gone = await actions.AttemptAsync("so", "SO-GONE", null, ErpActions.UpdateSoStatus,
            new Dictionary<string, string> { [ErpActions.InStatus] = nameof(SalesOrderStatus.Confirmed) },
            "requester");

        Assert.True(ok.Ok, ok.Message);
        Assert.False(gone.Ok);
        Assert.Contains("Unknown sales order", gone.Message);
    }

    [Fact]
    public async Task HoldRecord_LinksBlockedDocAndOffersRelease()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var hold = await erp.SetHoldAsync(
            "DOC", "missing declaration", "BAT-LI-01", "CASE-E", "approver",
            "PO", "PO-E1", null, null, null, Guid.NewGuid().ToString("N"));
        var views = Views(erp, WriteConfig());

        var record = await views.GetRecordAsync("hold", hold.HoldId);

        Assert.NotNull(record);
        Assert.Contains(record.Related, r => r.Label.Contains("PO-E1") && r.Href.Contains("erp/record/po/PO-E1"));
        Assert.Equal(ErpActions.ReleaseHold, Assert.Single(record.Actions).Id);
    }

    [Fact]
    public async Task VendorRecord_ShowsBankDocsAndBlockAction()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        erp.SeedVendorDocuments("V-E",
            [new VendorDocumentDto("V-E", "UN38.3", "UN38.3-V-E", true)]);
        var views = Views(erp, WriteConfig());

        var record = await views.GetRecordAsync("vendor", "V-E");

        Assert.NotNull(record);
        Assert.Contains(record.Header, f => f.Label == "Bank" && f.Value.Contains("BANK-E"));
        Assert.Contains(record.LineTables, t => t.Title == "Documents");
        Assert.Contains(record.Actions, a => a.Id == ErpActions.BlockVendor);
        Assert.Contains(record.Actions, a => a.Id == ErpActions.VerifyBank);
    }

    [Fact]
    public async Task ItemRecord_ShowsBomAndStockLink()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        erp.SeedBom("ITEM-E", [new BomLine("ITEM-PART", 4m)]);
        var views = Views(erp, WriteConfig());

        var record = await views.GetRecordAsync("item", "ITEM-E");

        Assert.NotNull(record);
        Assert.Contains(record.LineTables, t => t.Title == "Bill of materials");
        Assert.Contains(record.Related, r => r.Kind == "stock" && r.Key == "ITEM-E");
        Assert.Empty(record.Actions);
    }

    [Fact]
    public async Task Create_SalesOrderValidatesAndMints()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var actions = Actions(erp);

        var noCurrency = await actions.CreateSalesOrderAsync(
            null, [new SalesOrderLine("ITEM-E", 5m)], "requester");
        var noLines = await actions.CreateSalesOrderAsync("TST", [], "requester");
        var ok = await actions.CreateSalesOrderAsync(
            "TST", [new SalesOrderLine("ITEM-E", 5m)], "requester");

        Assert.False(noCurrency.Ok);
        Assert.Contains("Currency is required", noCurrency.Message);
        Assert.False(noLines.Ok);
        Assert.True(ok.Ok, ok.Message);
        Assert.NotNull(ok.CreatedKey);
        Assert.NotNull(await erp.GetSalesOrderAsync(ok.CreatedKey));
    }

    [Fact]
    public async Task Create_PoNeedsApprovedPrAndKnownVendor()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var draft = await erp.CreatePrAsync(
            "requester", RecordSource.Human, "requester",
            [new PurchaseRequisitionLine("ITEM-E", 5m, "EA")], Guid.NewGuid().ToString("N"));
        var actions = Actions(erp);

        var fromDraft = await actions.CreatePoAsync(draft.PrNumber, "V-E", "requester");
        var prNumber = await SeedApprovedPrAsync(erp);
        var unknownVendor = await actions.CreatePoAsync(prNumber, "V-GONE", "requester");
        var ok = await actions.CreatePoAsync(prNumber, "V-E", "requester");

        Assert.False(fromDraft.Ok);
        Assert.Contains("No valid approved PR", fromDraft.Message);
        Assert.False(unknownVendor.Ok);
        Assert.Contains("Unknown vendor", unknownVendor.Message);
        Assert.True(ok.Ok, ok.Message);
        Assert.NotNull(ok.CreatedKey);
    }

    [Fact]
    public async Task Create_InvoiceDuplicateRefused()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var prNumber = await SeedApprovedPrAsync(erp);
        var po = await erp.CreatePoAsync(
            prNumber, "V-E", RecordSource.Human, "requester", Guid.NewGuid().ToString("N"));
        var line = po.Lines.Single();
        var actions = Actions(erp);

        var first = await actions.CreateInvoiceAsync("INV-DUP", "V-E", po.PoNumber,
            [new InvoiceLineInput(line.PoLineRef, 5m, line.UnitPrice)], "ap-clerk");
        var second = await actions.CreateInvoiceAsync("INV-DUP", "V-E", po.PoNumber,
            [new InvoiceLineInput(line.PoLineRef, 5m, line.UnitPrice)], "ap-clerk");

        Assert.True(first.Ok, first.Message);
        Assert.Equal("V-E", first.CreatedKey);
        Assert.Equal("INV-DUP", first.CreatedKey2);
        Assert.False(second.Ok);
        Assert.Contains("Duplicate invoice", second.Message);
    }

    [Fact]
    public async Task Create_VendorMintsBlankIdAndValidatesTerms()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var actions = Actions(erp);

        var noName = await actions.CreateVendorAsync(
            null, null, "US", "NET0", null, "ap-clerk");
        var badTerms = await actions.CreateVendorAsync(
            null, "Bad Terms Co", "US", "NET-GONE", null, "ap-clerk");
        var minted = await actions.CreateVendorAsync(
            null, "Minted Co", "US", "NET0", null, "ap-clerk");
        var over = await actions.CreateVendorAsync(
            "V-OWN", "Override Co", "US", "NET0", null, "ap-clerk");

        Assert.False(noName.Ok);
        Assert.Contains("Name, country, and payment terms are required", noName.Message);
        Assert.False(badTerms.Ok);
        Assert.Contains("Unknown payment terms", badTerms.Message);
        Assert.True(minted.Ok, minted.Message);
        Assert.NotNull(minted.CreatedKey);
        Assert.NotEqual("V-OWN", minted.CreatedKey);
        Assert.True(over.Ok, over.Message);
        Assert.Equal("V-OWN", over.CreatedKey);
    }

    [Fact]
    public async Task Create_ItemManualIdAndPackSize()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var actions = Actions(erp);

        var blank = await actions.CreateItemAsync(null, "desc", "EA", false, 5, "ap-clerk");
        var zeroPack = await actions.CreateItemAsync("ITEM-N", "desc", "EA", false, 0, "ap-clerk");
        var ok = await actions.CreateItemAsync("ITEM-N", "New item", "EA", true, 4, "ap-clerk");
        var dup = await actions.CreateItemAsync("ITEM-N", "New item", "EA", true, 4, "ap-clerk");

        Assert.False(blank.Ok);
        Assert.Contains("Item id, description, and unit are required", blank.Message);
        Assert.False(zeroPack.Ok);
        Assert.Contains("above zero", zeroPack.Message);
        Assert.True(ok.Ok, ok.Message);
        Assert.Equal("ITEM-N", ok.CreatedKey);
        Assert.False(dup.Ok);
        Assert.Contains("already exists", dup.Message);
    }

    [Fact]
    public async Task FullFlow_PrToPaymentAttemptsForReal()
    {
        // One document chain through every attempt entry point: PR submit and
        // approve, PO create, vendor confirm, ASN, receipt post, invoice
        // create/match/approve/post, payment draft/submit/approve/post. The
        // invoice and payment approvers differ (ERP SoD).
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var actions = Actions(erp);
        static Dictionary<string, string> In(string k, string v) =>
            new() { [k] = v };

        var created = await actions.CreatePrAsync("requester",
            [new PurchaseRequisitionLine("ITEM-E", 5m, "EA")], "requester");
        Assert.True(created.Ok, created.Message);
        var pr = created.CreatedKey!;

        var submitPr = await actions.AttemptAsync("pr", pr, null, ErpActions.Submit,
            In(ErpActions.InRoutedTo, "approver"), "requester");
        var approvePr = await actions.AttemptAsync("pr", pr, null, ErpActions.Approve,
            new Dictionary<string, string>(), "approver");
        Assert.True(submitPr.Ok, submitPr.Message);
        Assert.True(approvePr.Ok, approvePr.Message);

        var poOutcome = await actions.CreatePoAsync(pr, "V-E", "requester");
        Assert.True(poOutcome.Ok, poOutcome.Message);
        var poNumber = poOutcome.CreatedKey!;
        var submitPo = await actions.AttemptAsync("po", poNumber, null, ErpActions.Submit,
            In(ErpActions.InRoutedTo, "approver"), "requester");
        var approvePo = await actions.AttemptAsync("po", poNumber, null, ErpActions.Approve,
            new Dictionary<string, string>(), "approver");
        Assert.True(submitPo.Ok, submitPo.Message);
        Assert.True(approvePo.Ok, approvePo.Message);
        var po = (await erp.GetPurchaseOrderAsync(poNumber))!;
        Assert.Equal(PoApprovalStatus.Approved, po.ApprovalStatus);
        var poLine = po.Lines.Single().PoLineRef;

        await erp.RecordVendorConfirmationAsync(po.PoNumber, DateOnly.FromDateTime(DateTime.UtcNow),
            RecordSource.Vendor, "V-E",
            [new VendorConfirmLine(poLine, 5m, null)], Guid.NewGuid().ToString("N"));
        await erp.CreateAsnAsync("ASN-E1", po.PoNumber, RecordSource.Vendor, "V-E",
            [new AsnCarton("CTN-E1", "ITEM-E", 5, "LOT-E1")], Guid.NewGuid().ToString("N"));
        var receipt = await actions.PostReceiptAsync(po.PoNumber, "SLIP-E1",
            [new ReceiptLine(poLine, 5m, 0m, 0m, "LOT-E1", null)], "ap-clerk");
        Assert.True(receipt.Ok, receipt.Message);

        var invoice = await actions.CreateInvoiceAsync("INV-E9", "V-E", po.PoNumber,
            [new InvoiceLineInput(poLine, 5m, 10m)], "ap-clerk");
        Assert.True(invoice.Ok, invoice.Message);
        await erp.RecordMatchResultAsync("V-E", "INV-E9",
            [new LineMatchResult(poLine, true, "matched")], Guid.NewGuid().ToString("N"));
        var submitInv = await actions.AttemptAsync("invoice", "V-E", "INV-E9", ErpActions.Submit,
            In(ErpActions.InRoutedTo, "approver"), "ap-clerk");
        var approveInv = await actions.AttemptAsync("invoice", "V-E", "INV-E9", ErpActions.Approve,
            new Dictionary<string, string>(), "approver");
        Assert.True(submitInv.Ok, submitInv.Message);
        Assert.True(approveInv.Ok, approveInv.Message);
        var postInv = await actions.AttemptAsync("invoice", "V-E", "INV-E9", ErpActions.PostInvoice,
            new Dictionary<string, string>(), "ap-clerk");
        Assert.True(postInv.Ok, postInv.Message);
        Assert.Contains("voucher", postInv.Message);

        var draft = await actions.CreatePaymentDraftAsync(
            [new InvoiceKey("V-E", "INV-E9")], null, "ap-clerk");
        Assert.True(draft.Ok, draft.Message);
        var submitPay = await actions.AttemptAsync("payment", draft.CreatedKey!, null,
            ErpActions.Submit, In(ErpActions.InRoutedTo, "finance"), "ap-clerk");
        var approvePay = await actions.AttemptAsync("payment", draft.CreatedKey!, null,
            ErpActions.Approve, new Dictionary<string, string>(), "finance");
        Assert.True(submitPay.Ok, submitPay.Message);
        Assert.True(approvePay.Ok, approvePay.Message);
        var postPay = await actions.AttemptAsync("payment", draft.CreatedKey!, null,
            ErpActions.PostPayment, new Dictionary<string, string>(), "finance");
        Assert.True(postPay.Ok, postPay.Message);

        var settled = await erp.GetInvoiceAsync("V-E", "INV-E9");
        Assert.NotNull(settled);
        Assert.Equal(InvoiceStatus.Paid, settled.Status);
    }

    [Fact]
    public async Task StockRecord_ShowsLotsAndLotActionsAfterReceipt()
    {
        var erp = new ListErpAdapter();
        SeedUsers(erp);
        var actions = Actions(erp);
        var prNumber = await SeedApprovedPrAsync(erp);
        var po = await erp.CreatePoAsync(
            prNumber, "V-E", RecordSource.Human, "requester", Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        await erp.RecordVendorConfirmationAsync(po.PoNumber, DateOnly.FromDateTime(DateTime.UtcNow),
            RecordSource.Vendor, "V-E",
            [new VendorConfirmLine(po.Lines.Single().PoLineRef, 5m, null)], Guid.NewGuid().ToString("N"));
        var posted = await actions.PostReceiptAsync(po.PoNumber, "SLIP-E2",
            [new ReceiptLine(po.Lines.Single().PoLineRef, 5m, 0m, 0m, "LOT-E2", null)], "ap-clerk");
        Assert.True(posted.Ok, posted.Message);
        var views = Views(erp, WriteConfig());

        var record = await views.GetRecordAsync("stock", "ITEM-E");

        Assert.NotNull(record);
        Assert.Contains(record.Header, f => f.Label == "On hand" && f.Value == "5");
        var lots = Assert.Single(record.LineTables.Where(t => t.Title == "Lots"));
        Assert.Single(lots.Rows);
        Assert.Equal(
            [ErpActions.QuarantineLot, ErpActions.ReleaseLot, ErpActions.ConsumeLot],
            record.Actions.Select(a => a.Id).ToList());
    }

    [Fact]
    public async Task ResetSeedsOpeningDecisionsIntoClearedCache()
    {
        // Reset wiring for the agent lane's projector: the stale live file
        // is cleared away, then the lifecycle's four submitted-stage rows
        // (PR, PO, invoice, payment) seed one opening shell each with a
        // single pending card and zero agent-authored events. Document
        // numbers come from the run manifest, never from literals. The UI
        // lane only asserts the cache state; the projector stays agent-side.
        var erp = new InMemoryErpAdapter();
        var dir = UiTestSupport.NewDir("gate-ui-erp-seed");
        Directory.CreateDirectory(dir);
        var scenarioPath = Path.Combine(dir, "scenario.json");
        File.WriteAllText(scenarioPath, DemoScenarioJson);
        var manifestPath = Path.Combine(dir, "run-manifest.json");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scenario:File"] = scenarioPath,
                ["Scenario:Sqlite"] = Path.Combine(dir, "gate.db"),
                ["Scenario:Manifest"] = manifestPath,
            })
            .Build();
        var cacheDir = Path.Combine(dir, "case-cache");
        var store = new CaseCacheStore(cacheDir);
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-STALE", "SO-STALE", "ITEM-STALE"));
        var models = new FakeModelClient();
        var runner = new Gate.Agent.CaseRunner(
            erp, new Gate.Agent.P2pAgent(erp, models, new RecordingComplianceEngine()),
            models, new RecordingComplianceEngine(), new DockEvaluatorAdapter(), cacheDir);
        var service = new ReplayService(
            erp, store,
            new DockPhotoService(Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
            new ReplayState(), config, UiTestLogger<ReplayService>.Instance, runner);

        var report = await service.ResetAndReplayAsync();

        Assert.Null(report.Error);
        Assert.Contains(report.Steps, s => s.Contains("4 opening decision(s) seeded"));
        var manifest = ManifestNumbers(manifestPath);
        var files = await store.ListAsync();
        Assert.Equal(4, files.Count);
        Assert.DoesNotContain(files, f => f.Case.CaseId == "CASE-STALE");
        foreach (var file in files)
        {
            Assert.True(file.Case.IsOpeningData);
            Assert.Empty(file.Events);
            var card = Assert.Single(file.Decisions);
            Assert.Equal(DecisionStatus.Pending, card.Status);
        }

        Assert.Contains(files, f => f.Case.PrNumber == manifest["lc.prSubmitted.pr"]);
        Assert.Contains(files, f => f.Case.PoNumber == manifest["lc.poSubmitted.po"]);
        Assert.Contains(files, f => f.Case.Invoices.Any(k =>
            k.VendorId == "V-L" && k.SupplierInvoiceNumber == manifest["lc.inv.submitted.inv"]));
        Assert.Contains(files, f => f.Case.PaymentNumbers.Contains(manifest["lc.paySubmitted.pay"]));
    }

    private static Dictionary<string, string> ManifestNumbers(string manifestPath)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.GetProperty("Documents").EnumerateArray()
            .ToDictionary(
                d => d.GetProperty("Event").GetString() ?? string.Empty,
                d => d.GetProperty("Number").GetString() ?? string.Empty,
                StringComparer.Ordinal);
    }

    [Fact]
    public async Task ResetWithoutRunnerWarnsNothingAndLeavesCacheEmpty()
    {
        // The runner is optional so older hosts still reset: no runner
        // means zero seeded cards and no seed warning.
        var dir = UiTestSupport.NewDir("gate-ui-erp-seed");
        Directory.CreateDirectory(dir);
        var scenarioPath = Path.Combine(dir, "scenario.json");
        File.WriteAllText(scenarioPath, DemoScenarioJson);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scenario:File"] = scenarioPath,
                ["Scenario:Sqlite"] = Path.Combine(dir, "gate.db"),
                ["Scenario:Manifest"] = Path.Combine(dir, "run-manifest.json"),
            })
            .Build();
        var store = new CaseCacheStore(Path.Combine(dir, "case-cache"));
        var service = new ReplayService(
            new InMemoryErpAdapter(), store,
            new DockPhotoService(Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
            new ReplayState(), config, UiTestLogger<ReplayService>.Instance);

        var report = await service.ResetAndReplayAsync();

        Assert.Null(report.Error);
        Assert.Contains(report.Steps, s => s.Contains("0 opening decision(s) seeded"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("seed failed"));
        Assert.Empty(await store.ListAsync());
    }

    private const string DemoScenarioJson = """
        {"name":"ui-erp-gap","currency":"TST",
         "salesOrder":{"item":"ITEM-G","quantity":10},
         "bom":[],"stock":[],
         "openPoInbound":{"item":"ITEM-G","quantity":5,"vendor":"V-G"},
         "items":[{"id":"ITEM-G","description":"Gap item","critical":false,"packSize":1,"unit":"EA"},
          {"id":"ITEM-L","description":"Lifecycle item","critical":false,"packSize":1,"unit":"EA"}],
         "paymentTerms":[{"id":"NET0","netDays":0}],
         "vendors":[{"id":"V-G","name":"Gap Vendor","country":"US","role":"test",
          "item":"ITEM-G","unitPrice":10,"terms":"NET0","bankAccount":"BANK-G",
          "documents":[],"email":"g@test.example"},
          {"id":"V-L","name":"Lifecycle Vendor","country":"US","role":"test",
          "item":"ITEM-L","unitPrice":10,"terms":"NET0","bankAccount":"BANK-L",
          "documents":[],"email":"l@test.example"}],
         "historyPart":"ITEM-G","historyCells":[],
         "users":[{"name":"requester","role":"requester","signingLimit":50},
          {"name":"approver","role":"approver","signingLimit":1000000},
          {"name":"finance","role":"payment-approver","signingLimit":1000000}],
         "delegations":[],"autoApproveUnder":10,
         "cartonPlan":{"unitsPerCarton":5,"cartons":2},
         "smallPos":[],
         "lifecycle":{"item":"ITEM-L","vendor":"V-L",
          "prDraftQty":5,"prSubmittedQty":5,"prSubmittedTo":"approver","prApprovedQty":5,
          "poDraftQty":5,"poSubmittedQty":5,"poSubmittedTo":"approver","poApprovedQty":5,
          "backboneQty":10,"packingSlip":"PACK-T","asn":"ASN-T","carton":"CTN-T","lot":"LOT-T",
          "invoiceSubmittedTo":"approver","paySubmittedTo":"finance",
          "invoices":[{"key":"submitted","number":"INV-T-S","quantity":1,"stage":"submitted"},
           {"key":"payDraft","number":"INV-T-D","quantity":1,"stage":"posted"},
           {"key":"paySubmitted","number":"INV-T-P","quantity":1,"stage":"posted"},
           {"key":"payApproved","number":"INV-T-G","quantity":1,"stage":"posted"},
           {"key":"paid","number":"INV-T-F","quantity":1,"stage":"posted"}]}}
        """;
}

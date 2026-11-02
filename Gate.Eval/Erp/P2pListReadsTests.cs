// Full-list P2P reads: every list returns header plus lines, stays
// empty-honest on a fresh ERP, filters POs by vendor, unions inventory
// across items, and projects pending approvals with submitter and amount.
// Runs against both adapters through the shared interface.
using Gate.Contracts;

namespace Gate.Eval;

public sealed class P2pListReadsTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    private static async Task<PurchaseRequisition> DraftPrAsync(IErpAdapter erp, decimal qty = 50)
    {
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", qty, "EA")], Idem());
        Assert.Equal(PrStatus.Draft, pr.Status);
        return pr;
    }

    private static async Task<PurchaseRequisition> ApprovedPrAsync(IErpAdapter erp, decimal qty = 50)
    {
        var pr = await DraftPrAsync(erp, qty);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        return (await erp.GetPrAsync(pr.PrNumber))!;
    }

    private static async Task<string> ConfirmedPoNumberAsync(IErpAdapter erp, decimal qty = 50, string vendor = "V-B")
    {
        var pr = await ApprovedPrAsync(erp, qty);
        var po = await erp.CreatePoAsync(pr.PrNumber, vendor, RecordSource.P2pAgent, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        }
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var current = (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, vendor,
            current.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        return po.PoNumber;
    }

    [Fact]
    public async Task FullListsEmptyHonest_Fake()
    {
        await EmptyHonestAsync(SeededErp.Create());
    }

    [Fact]
    public async Task FullListsEmptyHonest_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await EmptyHonestAsync(fx.Adapter);
    }

    private static async Task EmptyHonestAsync(IErpAdapter erp)
    {
        // Masters exist on a fresh ERP, but no transactional documents do.
        Assert.Empty(await erp.GetPurchaseRequisitionsAsync());
        Assert.Empty(await erp.GetPurchaseOrdersAsync());
        Assert.Empty(await erp.GetVendorInvoicesAsync());
        Assert.Empty(await erp.GetPaymentsAsync());
        Assert.Empty(await erp.GetShipmentReceiptsAsync());
        Assert.Empty(await erp.GetAsnsAsync());
        Assert.Empty(await erp.GetPurchaseOrdersByVendorAsync("V-B"));
        Assert.Empty(await erp.GetPendingApprovalsAsync());
        Assert.NotEmpty(await erp.GetSalesOrdersAsync());
        Assert.NotEmpty(await erp.GetItemsAsync());
        Assert.NotEmpty(await erp.GetAllInventoryTransactionsAsync());
    }

    [Fact]
    public async Task FullListsReturnHeaderPlusLines_Fake()
    {
        await HeaderPlusLinesAsync(SeededErp.Create());
    }

    [Fact]
    public async Task FullListsReturnHeaderPlusLines_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await HeaderPlusLinesAsync(fx.Adapter);
    }

    private static async Task HeaderPlusLinesAsync(IErpAdapter erp)
    {
        var poNumber = await ConfirmedPoNumberAsync(erp);
        var po = (await erp.GetPurchaseOrderAsync(poNumber))!;
        var lineRef = po.Lines[0].PoLineRef;
        await erp.CreateAsnAsync("ASN-L1", poNumber, RecordSource.Vendor, "V-B",
            [new AsnCarton("CARTON-L1", "BAT-1", 10, "LOT-L1")], Idem());
        var receipt = await erp.PostReceiptAsync(poNumber, "PACK-L1", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, 50, 0, 0, "LOT-L1", null)], Idem());
        await erp.CreateInvoiceAsync("INV-L1", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 10, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-L1", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        await erp.SubmitForApprovalAsync("Invoice", "V-B/INV-L1", "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", "V-B/INV-L1", "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "INV-L1", "planner", Idem());
        var draft = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "INV-L1")], RecordSource.P2pAgent, "planner", Idem());

        var prs = await erp.GetPurchaseRequisitionsAsync();
        Assert.NotEmpty(prs);
        Assert.All(prs, p => Assert.NotEmpty(p.Lines));
        var pos = await erp.GetPurchaseOrdersAsync();
        Assert.Contains(pos, p => p.PoNumber == poNumber && p.Lines.Count == 1
            && p.Lines[0].ItemId == "BAT-1" && p.Lines[0].QtyOrdered == 50m);
        var asns = await erp.GetAsnsAsync();
        var asn = Assert.Single(asns);
        Assert.Equal("ASN-L1", asn.AsnNumber);
        Assert.Equal(poNumber, asn.PoNumber);
        var carton = Assert.Single(asn.Cartons);
        Assert.Equal("CARTON-L1", carton.CartonId);
        Assert.Equal("LOT-L1", carton.LotRef);
        var receipts = await erp.GetShipmentReceiptsAsync();
        var got = Assert.Single(receipts);
        Assert.Equal(receipt.ReceiptNumber, got.ReceiptNumber);
        Assert.Equal("PACK-L1", got.SupplierPackingSlipNumber);
        Assert.Equal(50m, Assert.Single(got.Lines).QtyReceived);
        var invoices = await erp.GetVendorInvoicesAsync();
        var inv = Assert.Single(invoices);
        Assert.Equal("INV-L1", inv.SupplierInvoiceNumber);
        Assert.Equal(InvoiceStatus.Posted, inv.Status);
        Assert.NotEmpty(inv.Lines);
        Assert.NotEmpty(inv.MatchResults);
        var payments = await erp.GetPaymentsAsync();
        var pay = Assert.Single(payments);
        Assert.Equal(draft.PaymentNumber, pay.PaymentNumber);
        var payLine = Assert.Single(pay.Lines);
        Assert.Equal("V-B", payLine.VendorId);
        Assert.Equal("INV-L1", payLine.SupplierInvoiceNumber);
        var sos = await erp.GetSalesOrdersAsync();
        Assert.All(sos, s => Assert.NotEmpty(s.Lines));
        var items = await erp.GetItemsAsync();
        Assert.Contains(items, i => i.ItemId == "BAT-1");
        Assert.Contains(items, i => i.ItemId == "SCR-9");
    }

    [Fact]
    public async Task PurchaseOrdersByVendorFilters_Fake()
    {
        await ByVendorAsync(SeededErp.Create());
    }

    [Fact]
    public async Task PurchaseOrdersByVendorFilters_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await ByVendorAsync(fx.Adapter);
    }

    private static async Task ByVendorAsync(IErpAdapter erp)
    {
        var winner = await ConfirmedPoNumberAsync(erp, vendor: "V-B");
        var other = await ConfirmedPoNumberAsync(erp, vendor: "V-A");
        var forB = await erp.GetPurchaseOrdersByVendorAsync("V-B");
        Assert.Contains(forB, p => p.PoNumber == winner);
        Assert.DoesNotContain(forB, p => p.PoNumber == other);
        Assert.All(forB, p =>
        {
            Assert.Equal("V-B", p.VendorId);
            Assert.NotEmpty(p.Lines);
        });
        var forA = await erp.GetPurchaseOrdersByVendorAsync("V-A");
        Assert.Contains(forA, p => p.PoNumber == other);
        Assert.DoesNotContain(forA, p => p.PoNumber == winner);
        Assert.Empty(await erp.GetPurchaseOrdersByVendorAsync("V-NOPE"));
    }

    [Fact]
    public async Task AllInventoryTransactionsUnionItems_Fake()
    {
        await InventoryUnionAsync(SeededErp.Create());
    }

    [Fact]
    public async Task AllInventoryTransactionsUnionItems_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await InventoryUnionAsync(fx.Adapter);
    }

    private static async Task InventoryUnionAsync(IErpAdapter erp)
    {
        var before = await erp.GetAllInventoryTransactionsAsync();
        Assert.Contains(before, t => t.ItemId == "BAT-1");
        Assert.Contains(before, t => t.ItemId == "SCR-9");
        var poNumber = await ConfirmedPoNumberAsync(erp);
        var po = (await erp.GetPurchaseOrderAsync(poNumber))!;
        await erp.PostReceiptAsync(poNumber, "PACK-L2", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(po.Lines[0].PoLineRef, 50, 5, 0, "LOT-L2", null)], Idem());
        var after = await erp.GetAllInventoryTransactionsAsync();
        Assert.True(after.Count > before.Count);
        Assert.Contains(after, t => t.LotRef == "LOT-L2" && t.Status == InventoryStatus.Available && t.Quantity == 45m);
        Assert.Contains(after, t => t.LotRef == "LOT-L2" && t.Status == InventoryStatus.Quarantined && t.Quantity == 5m);
    }

    [Fact]
    public async Task PendingApprovalsProjectSubmitterAndAmount_Fake()
    {
        await PendingAsync(SeededErp.Create());
    }

    [Fact]
    public async Task PendingApprovalsProjectSubmitterAndAmount_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await PendingAsync(fx.Adapter);
    }

    private static async Task PendingAsync(IErpAdapter erp)
    {
        var pr = await DraftPrAsync(erp);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        var pending = await erp.GetPendingApprovalsAsync();
        var row = Assert.Single(pending);
        Assert.Equal("PR", row.DocumentType);
        Assert.Equal(pr.PrNumber, row.DocumentNumber);
        Assert.Equal("approver", row.RoutedTo);
        Assert.Equal("planner", row.SubmittedBy);
        Assert.Equal(600m, row.DocumentAmount);

        // Decided documents leave the queue; resubmission returns them.
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", false, "no", Idem());
        Assert.Empty(await erp.GetPendingApprovalsAsync());
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "delegate", "planner", Idem());
        var again = Assert.Single(await erp.GetPendingApprovalsAsync());
        Assert.Equal("delegate", again.RoutedTo);
        Assert.Equal("planner", again.SubmittedBy);
    }

    [Fact]
    public async Task PendingApprovalsCoverEveryRoutableType_Fake()
    {
        await PendingAllTypesAsync(SeededErp.Create());
    }

    [Fact]
    public async Task PendingApprovalsCoverEveryRoutableType_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await PendingAllTypesAsync(fx.Adapter);
    }

    private static async Task PendingAllTypesAsync(IErpAdapter erp)
    {
        var pr = await ApprovedPrAsync(erp);
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
        var pendingPo = await erp.GetPendingApprovalsAsync();
        Assert.Contains(pendingPo, r => r.DocumentType == "PO" && r.DocumentNumber == po.PoNumber
            && r.RoutedTo == "approver" && r.SubmittedBy == "planner" && r.DocumentAmount == 600m);
        await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());

        // A matched-but-never-submitted invoice is not pending.
        var poNumber = await ConfirmedPoNumberAsync(erp);
        var current = (await erp.GetPurchaseOrderAsync(poNumber))!;
        var lineRef = current.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(poNumber, "PACK-L3", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, 50, 0, 0, "LOT-L3", null)], Idem());
        await erp.CreateInvoiceAsync("INV-L3", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 10, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-L3", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        Assert.DoesNotContain(await erp.GetPendingApprovalsAsync(),
            r => r.DocumentType == "Invoice" && r.DocumentNumber == "V-B/INV-L3");
        await erp.SubmitForApprovalAsync("Invoice", "V-B/INV-L3", "approver", "planner", Idem());
        var pendingInv = await erp.GetPendingApprovalsAsync();
        Assert.Contains(pendingInv, r => r.DocumentType == "Invoice" && r.DocumentNumber == "V-B/INV-L3"
            && r.RoutedTo == "approver" && r.SubmittedBy == "planner" && r.DocumentAmount == 120m);
        await erp.RecordApprovalAsync("Invoice", "V-B/INV-L3", "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "INV-L3", "planner", Idem());

        var draft = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "INV-L3")], RecordSource.P2pAgent, "planner", Idem());
        Assert.DoesNotContain(await erp.GetPendingApprovalsAsync(),
            r => r.DocumentType == "Payment" && r.DocumentNumber == draft.PaymentNumber);
        await erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "finance", "planner", Idem());
        var pendingPay = await erp.GetPendingApprovalsAsync();
        Assert.Contains(pendingPay, r => r.DocumentType == "Payment" && r.DocumentNumber == draft.PaymentNumber
            && r.RoutedTo == "finance" && r.SubmittedBy == "planner" && r.DocumentAmount == 120m);
    }

    [Fact]
    public async Task PricedVendorIds_Fake()
    {
        await PricedVendorIdsAsync(SeededErp.Create());
    }

    [Fact]
    public async Task PricedVendorIds_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await PricedVendorIdsAsync(fx.Adapter);
    }

    private static async Task PricedVendorIdsAsync(IErpAdapter erp)
    {
        Assert.Equal(["V-A", "V-B"], await erp.GetPricedVendorIdsAsync(SeededErp.Battery));
        Assert.Equal(["V-B", "V-C"], await erp.GetPricedVendorIdsAsync(SeededErp.Screw));
        Assert.Empty(await erp.GetPricedVendorIdsAsync("NOPE"));
    }
}

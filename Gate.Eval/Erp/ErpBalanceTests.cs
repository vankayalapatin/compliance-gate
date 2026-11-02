// Partial receipt plus partial invoice plus split disposition balances by
// hand, with a remainder run after release. Runs against both adapters.
using Gate.Contracts;

namespace Gate.Eval;

public sealed class ErpBalanceTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task PartialReceiptSplitInvoiceAndRemainderBalance_Fake()
    {
        await RunBalanceAsync(SeededErp.Create());
    }

    [Fact]
    public async Task PartialReceiptSplitInvoiceAndRemainderBalance_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await RunBalanceAsync(fx.Adapter);
    }

    private static async Task RunBalanceAsync(IErpAdapter erp)
    {
        // PO line orders 12 units at 12.00 (line amount 144.00).
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 12, "EA")], Idem());
        // Under the 500 auto-approve threshold the PR may already be approved.
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        }
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        }
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-B",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        var lineRef = po.Lines[0].PoLineRef;
        Assert.Equal(12m, po.Lines[0].QtyOrdered);
        Assert.Equal(12m, po.Lines[0].UnitPrice);

        // Receipt 1 books 8 units: 6 available, 2 quarantined.
        var receipt = await erp.PostReceiptAsync(po.PoNumber, "PACK-W1", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, 8, 2, 0, "LOT-W1", "crushed corner")], Idem());
        var afterReceipt = await erp.GetPurchaseOrderAsync(po.PoNumber);
        Assert.Equal(8m, afterReceipt!.Lines[0].QtyReceived);
        Assert.Equal(2m, afterReceipt.Lines[0].QtyQuarantined);
        Assert.Equal(PoDocStatus.PartiallyReceived, afterReceipt.DocStatus);

        // Invoice 1 bills 8 units: accepted 6 (72.00), held 2 (24.00).
        await erp.CreateInvoiceAsync("INV-W1", "V-B", po.PoNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 8, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-W1", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        var split = await erp.GetInvoiceAsync("V-B", "INV-W1");
        Assert.Equal(2, split!.Lines.Count);
        Assert.Equal(6m, split.Lines.Single(l => l.Disposition == LineDisposition.Accepted).Quantity);
        Assert.Equal(2m, split.Lines.Single(l => l.Disposition == LineDisposition.Held).Quantity);
        var invKey = $"V-B/INV-W1";
        await erp.SubmitForApprovalAsync("Invoice", invKey, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", invKey, "approver", true, "ok", Idem());
        var posted = await erp.PostInvoiceAsync("V-B", "INV-W1", "planner", Idem());
        Assert.False(string.IsNullOrWhiteSpace(posted.Voucher));

        // Payment run 1 pays the accepted 72.00 only.
        var draft1 = await erp.CreatePaymentDraftAsync([new InvoiceKey("V-B", "INV-W1")], RecordSource.P2pAgent, "planner", Idem());
        Assert.Equal(72m, draft1.Lines[0].Amount);
        await erp.SubmitForApprovalAsync("Payment", draft1.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", draft1.PaymentNumber, "finance", true, "ok", Idem());
        await erp.PostPaymentAsync(draft1.PaymentNumber, "finance", Idem());
        var afterPay1 = await erp.GetInvoiceAsync("V-B", "INV-W1");
        Assert.Equal(InvoiceStatus.PartiallyPaid, afterPay1!.Status);
        var settle1 = await erp.GetSettlementsAsync("V-B", "INV-W1");
        Assert.Single(settle1);
        Assert.Equal(72m, settle1[0].SettledAmount);
        Assert.Equal(24m, settle1[0].RemainingBalance);

        // Resolution: quarantine clears, held units release, run 2 pays 24.00.
        var hold = await erp.SetHoldAsync("DOCK", "damaged carton", "BAT-LI-05", "CASE-W1",
            "planner", "Receipt", receipt.ReceiptNumber, lineRef, "LOT-W1", null, Idem());
        await erp.ReleaseLotAsync("LOT-W1", hold.HoldId, "planner", Idem());
        await erp.ReleaseHoldAsync(hold.HoldId, "planner", null, Idem());
        await erp.ReleaseHeldInvoiceLineAsync("V-B", "INV-W1", lineRef, 2, "planner", Idem());
        var draft2 = await erp.CreatePaymentDraftAsync([new InvoiceKey("V-B", "INV-W1")], RecordSource.P2pAgent, "planner", Idem());
        Assert.Equal(24m, draft2.Lines[0].Amount);
        await erp.SubmitForApprovalAsync("Payment", draft2.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", draft2.PaymentNumber, "finance", true, "ok", Idem());
        await erp.PostPaymentAsync(draft2.PaymentNumber, "finance", Idem());

        // Balances by hand: 72 + 24 = 96 paid against 8 received at 12.00.
        var settleAll = await erp.GetSettlementsAsync("V-B", "INV-W1");
        Assert.Equal(2, settleAll.Count);
        Assert.Equal(96m, settleAll.Sum(s => s.SettledAmount));
        Assert.Equal(0m, settleAll.Single(s => s.SettledAmount == 24m).RemainingBalance);
        var done = await erp.GetInvoiceAsync("V-B", "INV-W1");
        Assert.Equal(InvoiceStatus.Paid, done!.Status);
        Assert.Equal(posted.Voucher, done.Voucher);
        var reread = await erp.GetReceiptAsync(receipt.ReceiptNumber);
        Assert.Equal(8m, reread!.Lines[0].QtyReceived);
        Assert.Equal(2m, reread.Lines[0].QtyQuarantined);
    }

    [Fact]
    public async Task SecondInvoiceOnSamePoLineIsNotOverHeld_Fake()
    {
        await RunTwoInvoicesAsync(SeededErp.Create());
    }

    [Fact]
    public async Task SecondInvoiceOnSamePoLineIsNotOverHeld_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await RunTwoInvoicesAsync(fx.Adapter);
    }

    private static async Task RunTwoInvoicesAsync(IErpAdapter erp)
    {
        // Prior accepted quantity is units, not value: a second invoice must
        // see remaining units (10 - 4 = 6), not 10 - 4*price.
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 10, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        }
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        }
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-B",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        var lineRef = po.Lines[0].PoLineRef;
        var unitPrice = po.Lines[0].UnitPrice;
        Assert.True(unitPrice > 1, "Test needs a unit price above 1 to separate units from value.");

        await erp.PostReceiptAsync(po.PoNumber, "PACK-2A", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, 10, 0, 0, "LOT-2A", null)], Idem());

        await erp.CreateInvoiceAsync("INV-2A", "V-B", po.PoNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 4, unitPrice)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-2A", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        var first = await erp.GetInvoiceAsync("V-B", "INV-2A");
        Assert.Single(first!.Lines);
        Assert.Equal(4m, first.Lines[0].Quantity);
        Assert.Equal(LineDisposition.Accepted, first.Lines[0].Disposition);

        await erp.CreateInvoiceAsync("INV-2B", "V-B", po.PoNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 6, unitPrice)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-2B", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        var second = await erp.GetInvoiceAsync("V-B", "INV-2B");
        Assert.Single(second!.Lines);
        Assert.Equal(6m, second.Lines[0].Quantity);
        Assert.Equal(LineDisposition.Accepted, second.Lines[0].Disposition);

        foreach (var inv in new[] { "INV-2A", "INV-2B" })
        {
            var key = $"V-B/{inv}";
            await erp.SubmitForApprovalAsync("Invoice", key, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("Invoice", key, "approver", true, "ok", Idem());
            await erp.PostInvoiceAsync("V-B", inv, "planner", Idem());
        }
        var billed = await erp.GetPurchaseOrderAsync(po.PoNumber);
        Assert.Equal(10m, billed!.Lines[0].QtyInvoiced);
    }

    [Fact]
    public async Task RemainderDraftSkipsFullyPaidInvoice_Fake()
    {
        await RunRemainderSkipsPaidAsync(SeededErp.Create());
    }

    [Fact]
    public async Task RemainderDraftSkipsFullyPaidInvoice_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await RunRemainderSkipsPaidAsync(fx.Adapter);
    }

    private static async Task RunRemainderSkipsPaidAsync(IErpAdapter erp)
    {
        // The first invoice pays in full; a later draft over both keys must
        // cover only the open remainder instead of refusing the paid
        // invoice's terminal status.
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 10, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        }
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        }
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-B",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        var lineRef = po.Lines[0].PoLineRef;
        var unitPrice = po.Lines[0].UnitPrice;
        await erp.PostReceiptAsync(po.PoNumber, "PACK-RS", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, 10, 0, 0, "LOT-RS", "ok")], Idem());

        await erp.CreateInvoiceAsync("INV-RS1", "V-B", po.PoNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 4, unitPrice)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-RS1", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        await erp.CreateInvoiceAsync("INV-RS2", "V-B", po.PoNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 6, unitPrice)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-RS2", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        foreach (var inv in new[] { "INV-RS1", "INV-RS2" })
        {
            var key = $"V-B/{inv}";
            await erp.SubmitForApprovalAsync("Invoice", key, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("Invoice", key, "approver", true, "ok", Idem());
            await erp.PostInvoiceAsync("V-B", inv, "planner", Idem());
        }

        var first = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "INV-RS1")], RecordSource.P2pAgent, "planner", Idem());
        Assert.Equal(4 * unitPrice, first.Lines.Single().Amount);
        await erp.SubmitForApprovalAsync("Payment", first.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", first.PaymentNumber, "finance", true, "ok", Idem());
        await erp.PostPaymentAsync(first.PaymentNumber, "finance", Idem());
        Assert.Equal(InvoiceStatus.Paid, (await erp.GetInvoiceAsync("V-B", "INV-RS1"))!.Status);

        var remainder = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "INV-RS1"), new InvoiceKey("V-B", "INV-RS2")],
            RecordSource.P2pAgent, "planner", Idem());
        var line = Assert.Single(remainder.Lines);
        Assert.Equal("INV-RS2", line.SupplierInvoiceNumber);
        Assert.Equal(6 * unitPrice, line.Amount);
        await erp.SubmitForApprovalAsync("Payment", remainder.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", remainder.PaymentNumber, "finance", true, "ok", Idem());
        await erp.PostPaymentAsync(remainder.PaymentNumber, "finance", Idem());
        Assert.Equal(InvoiceStatus.Paid, (await erp.GetInvoiceAsync("V-B", "INV-RS2"))!.Status);
    }
}

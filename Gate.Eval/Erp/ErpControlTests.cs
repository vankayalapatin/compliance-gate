// ERP control refusals: SoD, limits, PR requirements, held value, duplicates,
// bank verification, confirmation gating, idempotency, number timing.
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class ErpControlTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    private static async Task<(string PrNumber, string PoNumber)> ConfirmedPoAsync(InMemoryErpAdapter erp,
        string item = SeededErp.Battery, decimal qty = 10, string vendor = "V-B",
        string requester = "planner", string approver = "approver")
    {
        var pr = await erp.CreatePrAsync(requester, RecordSource.P2pAgent, requester,
            [new PurchaseRequisitionLine(item, qty, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, approver, requester, Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, approver, true, "ok", Idem());
        }
        var po = await erp.CreatePoAsync(pr.PrNumber, vendor, RecordSource.P2pAgent, requester, Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, approver, requester, Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, approver, true, "ok", Idem());
        }
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, vendor,
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        return (pr.PrNumber, po.PoNumber);
    }

    [Fact]
    public async Task RequesterCannotApproveOwnPr()
    {
        var erp = SeededErp.Create();
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 50, "EA")], Idem());
        Assert.Equal(600m, pr.EstimatedAmount);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "planner", "planner", Idem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.RecordApprovalAsync("PR", pr.PrNumber, "planner", true, "self", Idem()));
    }

    [Fact]
    public async Task OverLimitApprovalRefused()
    {
        var erp = SeededErp.Create();
        erp.SeedUser(new UserDto("junior", "approver", 5m, "USD"));
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 50, "EA")], Idem());
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        await erp.SubmitForApprovalAsync("PO", po.PoNumber, "junior", "planner", Idem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.RecordApprovalAsync("PO", po.PoNumber, "junior", true, "over", Idem()));
    }

    [Fact]
    public async Task DelegateApprovalCoversApprover()
    {
        var erp = SeededErp.Create();
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 50, "EA")], Idem());
        Assert.Equal(PrStatus.Draft, pr.Status);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        var stored = await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "covering", Idem());
        Assert.True(stored.ActingAsDelegate);
        var approvals = await erp.GetApprovalsAsync("PR", pr.PrNumber);
        Assert.Single(approvals);
        Assert.True(approvals[0].ActingAsDelegate);
    }

    [Fact]
    public async Task PoNeedsApprovedPr()
    {
        var erp = SeededErp.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePoAsync("PR-999", "V-B", RecordSource.P2pAgent, "planner", Idem()));
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 50, "EA")], Idem());
        Assert.Equal(PrStatus.Draft, pr.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem()));
    }

    [Fact]
    public async Task BlockedVendorRefusedAndUnblocked()
    {
        var erp = SeededErp.Create();
        var (_, poNumber) = await ConfirmedPoAsync(erp);
        var po = await erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        await erp.BlockVendorAsync("V-B", "CORP-02: over cap.", "p2p-agent", Idem());
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 50, "EA")], Idem());
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem()));
        await erp.UnblockVendorAsync("V-B", "approver", Idem());
        var reopened = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        Assert.Equal(PoApprovalStatus.Draft, reopened.ApprovalStatus);
    }

    [Fact]
    public async Task ReceiptRequiresConfirmation()
    {
        var erp = SeededErp.Create();
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 50, "EA")], Idem());
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.PostReceiptAsync(po.PoNumber, "PACK-1", RecordSource.P2pAgent, "planner",
                [new ReceiptLine(po.Lines[0].PoLineRef, 5, 0, 0, "LOT-X", "ok")], Idem()));
    }

    [Fact]
    public async Task DuplicateInvoiceRefused()
    {
        var erp = SeededErp.Create();
        var (_, poNumber) = await ConfirmedPoAsync(erp);
        var po = await erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        await erp.CreateInvoiceAsync("INV-1", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(po.Lines[0].PoLineRef, 1, 12)], Idem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreateInvoiceAsync("INV-1", "V-B", poNumber, "vendor", "planner",
                [new InvoiceLineInput(po.Lines[0].PoLineRef, 1, 12)], Idem()));
    }

    [Fact]
    public async Task MissingVendorOrPoRefused()
    {
        var erp = SeededErp.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreateInvoiceAsync("INV-9", "", null, "vendor", "planner",
                [new InvoiceLineInput("PO-L1", 1, 10)], Idem()));
    }

    [Fact]
    public async Task HeldValueExcludedFromPayment()
    {
        var erp = SeededErp.Create();
        var (_, poNumber) = await ConfirmedPoAsync(erp, qty: 8);
        var po = await erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(poNumber, "PACK-1", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, 8, 2, 0, "LOT-H1", "damage")], Idem());
        await erp.CreateInvoiceAsync("INV-1", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 8, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-1",
            [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        var inv = await erp.GetInvoiceAsync("V-B", "INV-1");
        Assert.NotNull(inv);
        Assert.Equal(2, inv.Lines.Count);
        var invKey = InvoiceDoc("V-B", "INV-1");
        await erp.SubmitForApprovalAsync("Invoice", invKey, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", invKey, "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "INV-1", "planner", Idem());
        var draft = await erp.CreatePaymentDraftAsync([new InvoiceKey("V-B", "INV-1")], RecordSource.P2pAgent, "planner", Idem());
        Assert.Single(draft.Lines);
        Assert.Equal(72m, draft.Lines[0].Amount);
        await erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", draft.PaymentNumber, "finance", true, "ok", Idem());
        var posted = await erp.PostPaymentAsync(draft.PaymentNumber, "finance", Idem());
        Assert.Equal(PaymentStatus.Posted, posted.Status);
        var settlements = await erp.GetSettlementsAsync("V-B", "INV-1");
        Assert.Single(settlements);
        Assert.Equal(72m, settlements[0].SettledAmount);
        Assert.Equal(24m, settlements[0].RemainingBalance);
    }

    [Fact]
    public async Task InvoiceAndPaymentApproversMustDiffer()
    {
        var erp = SeededErp.Create();
        var (_, poNumber) = await ConfirmedPoAsync(erp, qty: 2);
        var po = await erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(poNumber, "PACK-1", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, 2, 0, 0, "LOT-S1", "ok")], Idem());
        await erp.CreateInvoiceAsync("INV-1", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 2, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-1", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        var invKey = InvoiceDoc("V-B", "INV-1");
        await erp.SubmitForApprovalAsync("Invoice", invKey, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", invKey, "finance", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "INV-1", "planner", Idem());
        var draft = await erp.CreatePaymentDraftAsync([new InvoiceKey("V-B", "INV-1")], RecordSource.P2pAgent, "planner", Idem());
        await erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "finance", "planner", Idem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.RecordApprovalAsync("Payment", draft.PaymentNumber, "finance", true, "self", Idem()));
    }

    [Fact]
    public async Task BankChangeBlocksPaymentUntilReverified()
    {
        var erp = SeededErp.Create();
        var (_, poNumber) = await ConfirmedPoAsync(erp, qty: 2);
        var po = await erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(poNumber, "PACK-1", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, 2, 0, 0, "LOT-B1", "ok")], Idem());
        await erp.CreateInvoiceAsync("INV-1", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 2, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-1", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        var invKey = InvoiceDoc("V-B", "INV-1");
        await erp.SubmitForApprovalAsync("Invoice", invKey, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", invKey, "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "INV-1", "planner", Idem());
        await erp.RecordVendorBankChangeAsync("V-B", "BANK-B-NEW", "vendor", "ap-clerk", Idem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePaymentDraftAsync([new InvoiceKey("V-B", "INV-1")], RecordSource.P2pAgent, "planner", Idem()));
        await erp.VerifyVendorBankAsync("V-B", "finance", Idem());
        var draft = await erp.CreatePaymentDraftAsync([new InvoiceKey("V-B", "INV-1")], RecordSource.P2pAgent, "planner", Idem());
        Assert.Equal("BANK-B-NEW", draft.PayeeBank.AccountRef);
        Assert.True(draft.PayeeBank.Verified);
    }

    [Fact]
    public async Task IdempotencyReplaysSameNumbers()
    {
        var erp = SeededErp.Create();
        var key = Idem();
        var first = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 1, "EA")], key);
        var second = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 1, "EA")], key);
        Assert.Equal(first.PrNumber, second.PrNumber);
        var receiptKey = Idem();
        var (_, poNumber) = await ConfirmedPoAsync(erp, qty: 1);
        var po = await erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        var r1 = await erp.PostReceiptAsync(poNumber, "PACK-1", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(po.Lines[0].PoLineRef, 1, 0, 0, "LOT-I1", "ok")], receiptKey);
        var r2 = await erp.PostReceiptAsync(poNumber, "PACK-1", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(po.Lines[0].PoLineRef, 1, 0, 0, "LOT-I1", "ok")], receiptKey);
        Assert.Equal(r1.ReceiptNumber, r2.ReceiptNumber);
        var refreshed = await erp.GetPurchaseOrderAsync(poNumber);
        Assert.Equal(1, refreshed!.Lines[0].QtyReceived);
    }

    [Fact]
    public async Task NumberTimingAssignsAtCreationAndPosting()
    {
        var erp = SeededErp.Create();
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("BAT-1", 50, "EA")], Idem());
        Assert.Equal(600m, pr.EstimatedAmount);
        Assert.StartsWith("PR-", pr.PrNumber);
        Assert.Equal(PrStatus.Draft, pr.Status);
        var (_, poNumber) = await ConfirmedPoAsync(erp, qty: 1);
        var po = await erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        Assert.StartsWith("PO-", po.PoNumber);
        var receipt = await erp.PostReceiptAsync(poNumber, "PACK-9", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(po.Lines[0].PoLineRef, 1, 0, 0, "LOT-N1", "ok")], Idem());
        Assert.StartsWith("RCT-", receipt.ReceiptNumber);
        Assert.False(string.IsNullOrWhiteSpace(receipt.SupplierPackingSlipNumber));
    }

    private static string InvoiceDoc(string vendorId, string supplierNumber) => $"{vendorId}/{supplierNumber}";
}

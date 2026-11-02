// Real-ERP control refusals: every refusal enforced in the EF Core layer
// carries a failure-path test here, mirroring the fake's suite.
using Gate.Contracts;
using Gate.Erp;
using Microsoft.EntityFrameworkCore;

namespace Gate.Eval;

public sealed class EfCoreControlTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    private static async Task<(string PrNumber, string PoNumber)> ConfirmedPoAsync(EfCoreErpAdapter erp,
        string item = "BAT-1", decimal qty = 10, string vendor = "V-B",
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
    public async Task MigrationAppliesCleanly()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        var applied = await fx.Db.Database.GetAppliedMigrationsAsync();
        Assert.Contains("20261030000000_InitialCreate", applied);
        Assert.Contains("20261101000000_SequenceFormatAndSubmittedBy", applied);
        var vendors = await fx.Adapter.GetVendorsAsync();
        Assert.Equal(3, vendors.Count);
    }

    [Fact]
    public async Task RequesterCannotApproveOwnPr()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
        fx.Db.Users.Add(new UserRow { UserId = "junior", Role = "approver", SigningLimit = 5m, LimitCurrency = "USD" });
        await fx.Db.SaveChangesAsync();
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
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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
    public async Task ReceivingAgainstClosedPoRefused()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
        var (_, poNumber) = await ConfirmedPoAsync(erp);
        var po = await erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        await erp.ClosePoAsync(poNumber, "planner", Idem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.PostReceiptAsync(poNumber, "PACK-1", RecordSource.P2pAgent, "planner",
                [new ReceiptLine(po.Lines[0].PoLineRef, 1, 0, 0, "LOT-C1", "ok")], Idem()));
    }

    [Fact]
    public async Task DuplicateInvoiceRefused()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreateInvoiceAsync("INV-9", "", null, "vendor", "planner",
                [new InvoiceLineInput("PO-L1", 1, 10)], Idem()));
    }

    [Fact]
    public async Task FullyHeldInvoicePaymentRefused()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
        var (_, poNumber) = await ConfirmedPoAsync(erp, qty: 2);
        var po = await erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.CreateInvoiceAsync("INV-1", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 2, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-1", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        var invKey = InvoiceDoc("V-B", "INV-1");
        await erp.SubmitForApprovalAsync("Invoice", invKey, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", invKey, "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "INV-1", "planner", Idem());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePaymentDraftAsync([new InvoiceKey("V-B", "INV-1")], RecordSource.P2pAgent, "planner", Idem()));
    }

    [Fact]
    public async Task HeldValueExcludedFromPayment()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
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

    [Fact]
    public async Task ScenarioResetRegeneratesIdenticalManifests()
    {
        var scenario = ScenarioLoader.ResolvePath("scenarios/demo-v1.json");
        Assert.True(File.Exists(scenario), $"Scenario file not found: {scenario}");
        var workDir = Path.Combine(Path.GetTempPath(), "gate-efcore-reset", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var erpFile = Path.Combine(workDir, "erp.db");
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlite($"Data Source={erpFile}")
            .Options;
        await using var db = new ErpDbContext(options);
        var erp = new EfCoreErpAdapter(options);
        var sqlite = Path.Combine(workDir, "gate.db");
        var manifestFile = Path.Combine(workDir, "run-manifest.json");
        try
        {
            var first = await ScenarioLoader.ResetAsync(erp, scenario, sqlite, manifestFile);
            var second = await ScenarioLoader.ResetAsync(erp, scenario, sqlite, manifestFile);

            Assert.NotEmpty(first.Documents);
            Assert.Equal(first.Documents, second.Documents);
            Assert.True(File.Exists(manifestFile));
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    private static string InvoiceDoc(string vendorId, string supplierNumber) => $"{vendorId}/{supplierNumber}";
}

// Company-data contract: ASN minting, business dates, multi-vendor runs,
// audit stamps, consequence previews, and posted-only volume history.
// Runs against both adapters; the fake keeps its seeded-history union for
// its own unit tests while EF Core reads posted receipt lines only.
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class ErpCompanyDataTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task AsnNullMintsFromSequence_Fake()
    {
        await AsnMintAsync(SeededErp.Create());
    }

    [Fact]
    public async Task AsnNullMintsFromSequence_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await AsnMintAsync(fx.Adapter);
    }

    private static async Task AsnMintAsync(IErpAdapter erp)
    {
        var po = await ConfirmedPoAsync(erp, "V-B", "BAT-1", 10m);
        var minted = await erp.CreateAsnAsync(null, po, RecordSource.Vendor, "V-B",
            [new AsnCarton("CTN-M1", "BAT-1", 10, "LOT-M1")], Idem());
        Assert.StartsWith("ASN-", minted.AsnNumber);
        Assert.Matches(@"^ASN-\d{4,}$", minted.AsnNumber);
        var blank = await erp.CreateAsnAsync("  ", po, RecordSource.Vendor, "V-B",
            [new AsnCarton("CTN-M2", "BAT-1", 10, "LOT-M2")], Idem());
        Assert.StartsWith("ASN-", blank.AsnNumber);
        Assert.NotEqual(minted.AsnNumber, blank.AsnNumber);
        var supplier = await erp.CreateAsnAsync("SUPP-REF-9", po, RecordSource.Vendor, "V-B",
            [new AsnCarton("CTN-M3", "BAT-1", 10, "LOT-M3")], Idem());
        Assert.Equal("SUPP-REF-9", supplier.AsnNumber);
        var dup = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreateAsnAsync("SUPP-REF-9", po, RecordSource.Vendor, "V-B",
                [new AsnCarton("CTN-M4", "BAT-1", 10, "LOT-M4")], Idem()));
        Assert.Contains("Duplicate ASN", dup.Message);
    }

    [Fact]
    public async Task BusinessDatesHonored_Fake()
    {
        await BusinessDatesAsync(SeededErp.Create());
    }

    [Fact]
    public async Task BusinessDatesHonored_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await BusinessDatesAsync(fx.Adapter);
    }

    private static async Task BusinessDatesAsync(IErpAdapter erp)
    {
        var po = await ConfirmedPoAsync(erp, "V-B", "BAT-1", 4m);
        var lineRef = (await erp.GetPurchaseOrderAsync(po))!.Lines[0].PoLineRef;
        var receiptDay = new DateOnly(2026, 3, 19);
        var receipt = await erp.PostReceiptAsync(po, "PACK-D1", RecordSource.Human, "planner",
            [new ReceiptLine(lineRef, 4m, 0, 0, "LOT-D1", null)], Idem(), receiptDay);
        Assert.Equal(receiptDay, receipt.PostingDate);
        Assert.NotNull(receipt.PostedAt);
        Assert.Equal(receiptDay, DateOnly.FromDateTime(receipt.PostedAt.Value.DateTime));
        Assert.Equal("planner", receipt.PostedBy);
        var invoiceDay = new DateOnly(2026, 3, 20);
        var invoice = await erp.CreateInvoiceAsync("INV-D1", "V-B", po, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 4m, 12m)], Idem(), invoiceDay);
        Assert.Equal(invoiceDay, invoice.DueDate);
        await erp.RecordMatchResultAsync("V-B", "INV-D1",
            [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        await erp.SubmitForApprovalAsync("Invoice", "V-B/INV-D1", "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", "V-B/INV-D1", "approver", true, "ok", Idem());
        var stored = await erp.GetInvoiceAsync("V-B", "INV-D1");
        Assert.NotNull(stored);
        Assert.Equal("approver", stored.ApprovedBy);
        Assert.NotNull(stored.ApprovedAt);
        var postDay = new DateOnly(2026, 3, 25);
        var posted = await erp.PostInvoiceAsync("V-B", "INV-D1", "planner", Idem(), postDay);
        Assert.Equal("planner", posted.PostedBy);
        Assert.NotNull(posted.PostedAt);
        Assert.Equal(postDay, DateOnly.FromDateTime(posted.PostedAt.Value.DateTime));
        Assert.Equal(invoiceDay, posted.DueDate);
        var reread = await erp.GetInvoiceAsync("V-B", "INV-D1");
        Assert.NotNull(reread);
        DateOnly? rereadDay = reread.PostedAt.HasValue
            ? DateOnly.FromDateTime(reread.PostedAt.Value.DateTime)
            : null;
        Assert.Equal(postDay, rereadDay);
    }

    [Fact]
    public async Task MultiVendorDraftAllowed_Fake()
    {
        await MultiVendorDraftAsync(SeededErp.Create());
    }

    [Fact]
    public async Task MultiVendorDraftAllowed_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await MultiVendorDraftAsync(fx.Adapter);
    }

    private static async Task MultiVendorDraftAsync(IErpAdapter erp)
    {
        await PostedInvoiceAsync(erp, "V-A", "INV-MV1", "BAT-1", 2m, 11m);
        await PostedInvoiceAsync(erp, "V-B", "INV-MV2", "BAT-1", 3m, 12m);
        var draft = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-A", "INV-MV1"), new InvoiceKey("V-B", "INV-MV2")],
            RecordSource.Human, "planner", Idem());
        Assert.Equal(2, draft.Lines.Count);
        var bankA = await erp.GetVendorBankAsync("V-A");
        Assert.NotNull(bankA);
        Assert.Equal(bankA.AccountRef, draft.PayeeBank.AccountRef);
        Assert.NotNull(draft.CreatedAt);
        await erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", draft.PaymentNumber, "finance", true, "ok", Idem());
        var approved = await erp.GetPaymentAsync(draft.PaymentNumber);
        Assert.NotNull(approved);
        Assert.Equal("finance", approved.ApprovedBy);
        var posted = await erp.PostPaymentAsync(draft.PaymentNumber, "finance", Idem());
        Assert.Equal(PaymentStatus.Posted, posted.Status);
        Assert.Equal("finance", posted.ApprovedBy);
        Assert.Equal("finance", posted.PostedBy);
        Assert.Equal(InvoiceStatus.Paid, (await erp.GetInvoiceAsync("V-A", "INV-MV1"))!.Status);
        Assert.Equal(InvoiceStatus.Paid, (await erp.GetInvoiceAsync("V-B", "INV-MV2"))!.Status);
    }

    [Fact]
    public async Task StampsPopulated_Fake()
    {
        await StampsAsync(SeededErp.Create());
    }

    [Fact]
    public async Task StampsPopulated_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await StampsAsync(fx.Adapter);
    }

    private static async Task StampsAsync(IErpAdapter erp)
    {
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine("BAT-1", 60m, "EA")], Idem());
        Assert.NotNull(pr.CreatedAt);
        Assert.Null(pr.ApprovedBy);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        var reread = await erp.GetPrAsync(pr.PrNumber);
        Assert.NotNull(reread);
        Assert.Equal("approver", reread.ApprovedBy);
        Assert.NotNull(reread.ApprovedAt);
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.Human, "planner", Idem());
        Assert.NotNull(po.CreatedAt);
        await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        var poRead = await erp.GetPurchaseOrderAsync(po.PoNumber);
        Assert.NotNull(poRead);
        Assert.Equal("approver", poRead.ApprovedBy);
    }

    [Fact]
    public async Task ConcurrentReadsShareOneAdapter_EfCore()
    {
        // Blazor circuit handlers interleave on the shared scoped adapter:
        // concurrent calls must each run on their own context instead of
        // throwing "a second operation was started on this context".
        await using var fx = await EfCoreTestDb.CreateAsync();
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => fx.Adapter.GetVendorsAsync())
            .ToList();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(3, r.Count));
    }

    [Fact]
    public async Task ApprovalSameActorIdempotent_Fake()
    {
        await ApprovalIdempotentAsync(SeededErp.Create());
    }

    [Fact]
    public async Task ApprovalSameActorIdempotent_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await ApprovalIdempotentAsync(fx.Adapter);
    }

    private static async Task ApprovalIdempotentAsync(IErpAdapter erp)
    {
        // A repeated click mints a fresh idempotency key: the same actor
        // repeating the same decision gets the stored row back, never a
        // second row. A flipped decision is refused.
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine("BAT-1", 60m, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        }
        var first = await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        var second = await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok again", Idem());
        Assert.Equal(first.Timestamp, second.Timestamp);
        Assert.Single(await erp.GetApprovalsAsync("PR", pr.PrNumber));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", false, "changed mind", Idem()));
    }

    [Fact]
    public async Task VolumeHistoryPostedOnly_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        Assert.Empty(await fx.Adapter.GetVolumeHistoryAsync("BAT-1"));
        var po = await ConfirmedPoAsync(fx.Adapter, "V-B", "BAT-1", 25m);
        var lineRef = (await fx.Adapter.GetPurchaseOrderAsync(po))!.Lines[0].PoLineRef;
        await fx.Adapter.PostReceiptAsync(po, "PACK-VH", RecordSource.Human, "planner",
            [new ReceiptLine(lineRef, 25m, 0, 0, "LOT-VH", null)], Idem());
        var history = await fx.Adapter.GetVolumeHistoryAsync("BAT-1");
        Assert.Equal(25m, history.Sum(h => h.Cells));
        Assert.All(history, h => Assert.Equal("MX", h.Country));
        Assert.Empty(await fx.Adapter.GetVolumeHistoryAsync("SCR-9"));
    }

    [Fact]
    public async Task VolumeHistoryUnionKept_Fake()
    {
        // The fake is unit-test-only: its seeded union stays so compliance
        // and agent suites keep their small history doubles.
        var erp = SeededErp.Create();
        var before = await erp.GetVolumeHistoryAsync("BAT-1");
        Assert.Equal(10000m, before.Sum(h => h.Cells));
        var po = await ConfirmedPoAsync(erp, "V-B", "BAT-1", 25m);
        var lineRef = (await erp.GetPurchaseOrderAsync(po))!.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(po, "PACK-VH", RecordSource.Human, "planner",
            [new ReceiptLine(lineRef, 25m, 0, 0, "LOT-VH", null)], Idem());
        var after = await erp.GetVolumeHistoryAsync("BAT-1");
        Assert.Equal(10025m, after.Sum(h => h.Cells));
    }

    [Fact]
    public async Task VolumeHistoryTrailingWindow_Fake()
    {
        await VolumeHistoryWindowAsync(SeededErp.Create());
    }

    [Fact]
    public async Task VolumeHistoryTrailingWindow_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await VolumeHistoryWindowAsync(fx.Adapter);
    }

    private static async Task VolumeHistoryWindowAsync(IErpAdapter erp)
    {
        // Trailing-12-month contract: a receipt posted 13 months ago stays
        // out of CORP-02 history; a current receipt stays in. SCR-9 carries
        // no seeded history on either adapter, so the posted rows stand alone.
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var oldPo = await ConfirmedPoAsync(erp, "V-B", "SCR-9", 40m);
        var oldRef = (await erp.GetPurchaseOrderAsync(oldPo))!.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(oldPo, "PACK-OLD", RecordSource.Human, "planner",
            [new ReceiptLine(oldRef, 40m, 0, 0, "LOT-OLD", null)], Idem(), today.AddMonths(-13));
        var newPo = await ConfirmedPoAsync(erp, "V-B", "SCR-9", 25m);
        var newRef = (await erp.GetPurchaseOrderAsync(newPo))!.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(newPo, "PACK-NEW", RecordSource.Human, "planner",
            [new ReceiptLine(newRef, 25m, 0, 0, "LOT-NEW", null)], Idem());
        var history = await erp.GetVolumeHistoryAsync("SCR-9");
        Assert.Equal(25m, history.Sum(h => h.Cells));
    }

    [Fact]
    public async Task ConsequencePreview_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        var erp = fx.Adapter;
        Assert.Null(await erp.GetConsequencePreviewAsync("PR", "PR-NOPE"));
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine("BAT-1", 60m, "EA")], Idem());
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        var prPreview = await erp.GetConsequencePreviewAsync("PR", pr.PrNumber);
        Assert.NotNull(prPreview);
        Assert.Contains(pr.PrNumber, prPreview.Postings.Single());
        Assert.Equal("approver", prPreview.NextApprover);
        Assert.Equal("USD", prPreview.Currency);
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        Assert.Null(await erp.GetConsequencePreviewAsync("PR", pr.PrNumber));

        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.Human, "planner", Idem());
        await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
        var poPreview = await erp.GetConsequencePreviewAsync("PO", po.PoNumber);
        Assert.NotNull(poPreview);
        Assert.Equal("approver", poPreview.NextApprover);
        await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-B",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(po.PoNumber, "PACK-CP", RecordSource.Human, "planner",
            [new ReceiptLine(lineRef, 60m, 0, 0, "LOT-CP", null)], Idem());
        await erp.CreateInvoiceAsync("INV-CP", "V-B", po.PoNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 60m, 12m)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-CP",
            [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        await erp.SubmitForApprovalAsync("Invoice", "V-B/INV-CP", "approver", "planner", Idem());
        var invPreview = await erp.GetConsequencePreviewAsync("Invoice", "V-B/INV-CP");
        Assert.NotNull(invPreview);
        Assert.Equal(today, invPreview.DueDate);
        Assert.Equal("PAYDRAFT-0001", invPreview.PaymentRun);
        Assert.Equal("approver", invPreview.NextApprover);
        Assert.Equal(0m, invPreview.HeldValue);
        await erp.RecordApprovalAsync("Invoice", "V-B/INV-CP", "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "INV-CP", "planner", Idem());
        Assert.Null(await erp.GetConsequencePreviewAsync("Invoice", "V-B/INV-CP"));

        var draft = await erp.CreatePaymentDraftAsync([new InvoiceKey("V-B", "INV-CP")],
            RecordSource.Human, "planner", Idem());
        var payPreview = await erp.GetConsequencePreviewAsync("Payment", draft.PaymentNumber);
        Assert.NotNull(payPreview);
        Assert.Equal(draft.PaymentNumber, payPreview.PaymentRun);
        Assert.Equal(today, payPreview.DueDate);
        Assert.Null(await erp.GetConsequencePreviewAsync("Nope", "X-1"));
    }

    [Fact]
    public async Task ConsequencePreview_FakeThrows()
    {
        IErpAdapter erp = SeededErp.Create();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            erp.GetConsequencePreviewAsync("PR", "PR-0001"));
        await Assert.ThrowsAsync<NotSupportedException>(() => erp.GetCompanyInfoAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => erp.GetSitesAsync());
    }

    [Fact]
    public async Task CompanyInfoAndSites_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        Assert.Null(await fx.Adapter.GetCompanyInfoAsync());
        Assert.Empty(await fx.Adapter.GetSitesAsync());
        fx.Db.CompanyInfo.Add(new CompanyInfoRow
        {
            EntityCode = "LE-T", LegalName = "Test Co", Currency = "USD",
            BillToAddress = "Bill T", ShipToAddress = "Ship T", SenderEmail = "buy@test.example"
        });
        fx.Db.Sites.Add(new SiteRow
        {
            SiteId = "SITE-T", Name = "Dock T", Address = "Dock T", Capabilities = "dock-camera;cold"
        });
        await fx.Db.SaveChangesAsync();
        var info = await fx.Adapter.GetCompanyInfoAsync();
        Assert.NotNull(info);
        Assert.Equal("LE-T", info.EntityCode);
        Assert.Equal("buy@test.example", info.SenderEmail);
        var sites = await fx.Adapter.GetSitesAsync();
        var site = Assert.Single(sites);
        Assert.Equal(new[] { "dock-camera", "cold" }, site.Capabilities);
    }

    [Fact]
    public async Task VendorHoldRecordsActor_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await fx.Adapter.BlockVendorAsync("V-A", "Review", "approver", Idem());
        // The adapter commits on its own context: clear the fixture's
        // tracked copy so each re-read sees the committed flags.
        fx.Db.ChangeTracker.Clear();
        var blocked = await fx.Db.Vendors.FindAsync("V-A");
        Assert.NotNull(blocked);
        Assert.True(blocked.IsBlocked);
        Assert.Equal("approver", blocked.BlockedBy);
        Assert.NotNull(blocked.BlockedAt);
        await fx.Adapter.UnblockVendorAsync("V-A", "approver", Idem());
        fx.Db.ChangeTracker.Clear();
        var cleared = await fx.Db.Vendors.FindAsync("V-A");
        Assert.NotNull(cleared);
        Assert.False(cleared.IsBlocked);
        Assert.Null(cleared.BlockedBy);
        Assert.Null(cleared.BlockedAt);
    }

    [Fact]
    public async Task VendorHoldRoundTrip_Fake()
    {
        var erp = SeededErp.Create();
        await erp.BlockVendorAsync("V-A", "Review", "approver", Idem());
        Assert.True((await erp.GetVendorsAsync()).Single(v => v.VendorId == "V-A").IsBlocked);
        await erp.UnblockVendorAsync("V-A", "approver", Idem());
        Assert.False((await erp.GetVendorsAsync()).Single(v => v.VendorId == "V-A").IsBlocked);
    }

    private static async Task<string> ConfirmedPoAsync(IErpAdapter erp, string vendor, string item, decimal qty)
    {
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(item, qty, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        }
        var po = await erp.CreatePoAsync(pr.PrNumber, vendor, RecordSource.Human, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        }
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, vendor,
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        return po.PoNumber;
    }

    private static async Task PostedInvoiceAsync(
        IErpAdapter erp, string vendor, string supplierInvoice, string item, decimal qty, decimal price)
    {
        var po = await ConfirmedPoAsync(erp, vendor, item, qty);
        var lineRef = (await erp.GetPurchaseOrderAsync(po))!.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(po, $"PACK-{supplierInvoice}", RecordSource.Human, "planner",
            [new ReceiptLine(lineRef, qty, 0, 0, $"LOT-{supplierInvoice}", null)], Idem());
        await erp.CreateInvoiceAsync(supplierInvoice, vendor, po, "vendor", "planner",
            [new InvoiceLineInput(lineRef, qty, price)], Idem());
        await erp.RecordMatchResultAsync(vendor, supplierInvoice,
            [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        var key = $"{vendor}/{supplierInvoice}";
        await erp.SubmitForApprovalAsync("Invoice", key, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", key, "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync(vendor, supplierInvoice, "planner", Idem());
    }

    [Fact]
    public async Task AutoApproveRecordsPolicyApproval_Fake()
    {
        await AutoApproveAsync(SeededErp.Create());
    }

    [Fact]
    public async Task AutoApproveRecordsPolicyApproval_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await AutoApproveAsync(fx.Adapter);
    }

    private static async Task AutoApproveAsync(IErpAdapter erp)
    {
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine("SCR-9", 1m, "EA")], Idem());
        Assert.Equal(PrStatus.Approved, pr.Status);
        Assert.Equal(ErpApprovalActors.AutoApprovePolicy, pr.ApprovedBy);
        Assert.NotNull(pr.ApprovedAt);
        var approvals = await erp.GetApprovalsAsync("PR", pr.PrNumber);
        var row = Assert.Single(approvals);
        Assert.True(row.Approved);
        Assert.Equal(ErpApprovalActors.AutoApprovePolicy, row.ActedBy);
        Assert.Contains("Auto-approved", row.Comment);
        var pending = await erp.GetPendingApprovalsAsync();
        Assert.DoesNotContain(pending, p => p.DocumentNumber == pr.PrNumber);
    }
}

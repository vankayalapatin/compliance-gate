// Pipeline list-reads: each of the 5 what-if gather reads returns exactly
// its lifecycle slice with lines populated, stays empty-honest on a fresh
// ERP, and unreceived ASN cartons join back to their PO. Runs against both
// adapters through the shared interface.
using Gate.Contracts;

namespace Gate.Eval;

public sealed class PipelineListReadsTests
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

    private static async Task<string> ConfirmedPoNumberAsync(IErpAdapter erp, decimal qty = 50)
    {
        var pr = await ApprovedPrAsync(erp, qty);
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        }
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var current = (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-B",
            current.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        return po.PoNumber;
    }

    [Fact]
    public async Task PipelineReadsEmptyOnFreshErp_Fake()
    {
        await EmptyHonestAsync(SeededErp.Create());
    }

    [Fact]
    public async Task PipelineReadsEmptyOnFreshErp_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await EmptyHonestAsync(fx.Adapter);
    }

    private static async Task EmptyHonestAsync(IErpAdapter erp)
    {
        Assert.Empty(await erp.GetOpenPurchaseRequisitionsAsync());
        Assert.Empty(await erp.GetDraftPurchaseOrdersAsync());
        Assert.Empty(await erp.GetOpenInvoicesAsync());
        Assert.Empty(await erp.GetOpenPaymentsAsync());
        Assert.Empty(await erp.GetUnreceivedAsnsAsync());
    }

    [Fact]
    public async Task OpenPrsAreDraftAndSubmittedOnly_Fake()
    {
        await OpenPrsSliceAsync(SeededErp.Create());
    }

    [Fact]
    public async Task OpenPrsAreDraftAndSubmittedOnly_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await OpenPrsSliceAsync(fx.Adapter);
    }

    private static async Task OpenPrsSliceAsync(IErpAdapter erp)
    {
        var draft = await DraftPrAsync(erp);
        var submitted = await DraftPrAsync(erp);
        await erp.SubmitForApprovalAsync("PR", submitted.PrNumber, "approver", "planner", Idem());
        var approved = await ApprovedPrAsync(erp);
        var open = await erp.GetOpenPurchaseRequisitionsAsync();
        Assert.Equal(2, open.Count);
        Assert.Contains(open, p => p.PrNumber == draft.PrNumber && p.Status == PrStatus.Draft);
        Assert.Contains(open, p => p.PrNumber == submitted.PrNumber && p.Status == PrStatus.Submitted);
        Assert.DoesNotContain(open, p => p.PrNumber == approved.PrNumber);
        Assert.All(open, p =>
        {
            Assert.NotEmpty(p.Lines);
            Assert.Equal("BAT-1", p.Lines[0].ItemId);
            Assert.Equal(50m, p.Lines[0].Quantity);
        });
    }

    [Fact]
    public async Task DraftPosAreDraftAndInReviewOnly_Fake()
    {
        await DraftPosSliceAsync(SeededErp.Create());
    }

    [Fact]
    public async Task DraftPosAreDraftAndInReviewOnly_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await DraftPosSliceAsync(fx.Adapter);
    }

    private static async Task DraftPosSliceAsync(IErpAdapter erp)
    {
        var pr1 = await ApprovedPrAsync(erp);
        var draft = await erp.CreatePoAsync(pr1.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        Assert.Equal(PoApprovalStatus.Draft, draft.ApprovalStatus);
        var pr2 = await ApprovedPrAsync(erp);
        var review = await erp.CreatePoAsync(pr2.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        await erp.SubmitForApprovalAsync("PO", review.PoNumber, "approver", "planner", Idem());
        var pr3 = await ApprovedPrAsync(erp);
        var approved = await erp.CreatePoAsync(pr3.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Idem());
        await erp.SubmitForApprovalAsync("PO", approved.PoNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PO", approved.PoNumber, "approver", true, "ok", Idem());
        var open = await erp.GetDraftPurchaseOrdersAsync();
        Assert.Equal(2, open.Count);
        Assert.Contains(open, p => p.PoNumber == draft.PoNumber && p.ApprovalStatus == PoApprovalStatus.Draft);
        Assert.Contains(open, p => p.PoNumber == review.PoNumber && p.ApprovalStatus == PoApprovalStatus.InReview);
        Assert.DoesNotContain(open, p => p.PoNumber == approved.PoNumber);
        Assert.All(open, p =>
        {
            Assert.NotEmpty(p.Lines);
            Assert.Equal("BAT-1", p.Lines[0].ItemId);
            Assert.Equal(50m, p.Lines[0].QtyOrdered);
            Assert.Equal(12m, p.Lines[0].UnitPrice);
        });
    }

    [Fact]
    public async Task OpenInvoicesAreReceivedAndMatchedOnly_Fake()
    {
        await OpenInvoicesSliceAsync(SeededErp.Create());
    }

    [Fact]
    public async Task OpenInvoicesAreReceivedAndMatchedOnly_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await OpenInvoicesSliceAsync(fx.Adapter);
    }

    private static async Task OpenInvoicesSliceAsync(IErpAdapter erp)
    {
        var poNumber = await ConfirmedPoNumberAsync(erp);
        var po = (await erp.GetPurchaseOrderAsync(poNumber))!;
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(poNumber, "PACK-R1", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, 50, 0, 0, "LOT-R1", null)], Idem());
        await erp.CreateInvoiceAsync("INV-R1", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 8, 12)], Idem());
        await erp.CreateInvoiceAsync("INV-R2", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 8, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-R2", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        await erp.CreateInvoiceAsync("INV-R3", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 8, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-R3", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        await erp.SubmitForApprovalAsync("Invoice", "V-B/INV-R3", "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", "V-B/INV-R3", "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "INV-R3", "planner", Idem());
        var open = await erp.GetOpenInvoicesAsync();
        Assert.Equal(2, open.Count);
        Assert.Contains(open, i => i.SupplierInvoiceNumber == "INV-R1" && i.Status == InvoiceStatus.Received);
        Assert.Contains(open, i => i.SupplierInvoiceNumber == "INV-R2" && i.Status == InvoiceStatus.Matched);
        Assert.DoesNotContain(open, i => i.SupplierInvoiceNumber == "INV-R3");
        Assert.All(open, i =>
        {
            Assert.NotEmpty(i.Lines);
            Assert.Null(i.Voucher);
        });
        var matched = open.Single(i => i.SupplierInvoiceNumber == "INV-R2");
        Assert.NotEmpty(matched.MatchResults);
        Assert.Equal(LineDisposition.Accepted, matched.Lines[0].Disposition);
    }

    [Fact]
    public async Task OpenPaymentsAreDraftAndSubmittedOnly_Fake()
    {
        await OpenPaymentsSliceAsync(SeededErp.Create());
    }

    [Fact]
    public async Task OpenPaymentsAreDraftAndSubmittedOnly_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await OpenPaymentsSliceAsync(fx.Adapter);
    }

    private static async Task OpenPaymentsSliceAsync(IErpAdapter erp)
    {
        var poNumber = await ConfirmedPoNumberAsync(erp);
        var po = (await erp.GetPurchaseOrderAsync(poNumber))!;
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(poNumber, "PACK-P1", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(lineRef, 50, 0, 0, "LOT-P1", null)], Idem());
        await erp.CreateInvoiceAsync("INV-P1", "V-B", poNumber, "vendor", "planner",
            [new InvoiceLineInput(lineRef, 10, 12)], Idem());
        await erp.RecordMatchResultAsync("V-B", "INV-P1", [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        await erp.SubmitForApprovalAsync("Invoice", "V-B/INV-P1", "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", "V-B/INV-P1", "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "INV-P1", "planner", Idem());
        var key = new InvoiceKey("V-B", "INV-P1");
        var draft = await erp.CreatePaymentDraftAsync([key], RecordSource.P2pAgent, "planner", Idem());
        var submitted = await erp.CreatePaymentDraftAsync([key], RecordSource.P2pAgent, "planner", Idem());
        await erp.SubmitForApprovalAsync("Payment", submitted.PaymentNumber, "finance", "planner", Idem());
        var posted = await erp.CreatePaymentDraftAsync([key], RecordSource.P2pAgent, "planner", Idem());
        await erp.SubmitForApprovalAsync("Payment", posted.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", posted.PaymentNumber, "finance", true, "ok", Idem());
        var postedPay = await erp.PostPaymentAsync(posted.PaymentNumber, "finance", Idem());
        var open = await erp.GetOpenPaymentsAsync();
        Assert.Equal(2, open.Count);
        Assert.Contains(open, p => p.PaymentNumber == draft.PaymentNumber && p.Status == PaymentStatus.Draft);
        Assert.Contains(open, p => p.PaymentNumber == submitted.PaymentNumber && p.Status == PaymentStatus.Submitted);
        Assert.DoesNotContain(open, p => p.PaymentNumber == postedPay.PaymentNumber);
        Assert.All(open, p =>
        {
            Assert.NotEmpty(p.Lines);
            Assert.Equal(120m, p.Lines[0].Amount);
        });
    }

    [Fact]
    public async Task UnreceivedAsnCartonsJoinToTheirPo_Fake()
    {
        await UnreceivedAsnsSliceAsync(SeededErp.Create());
    }

    [Fact]
    public async Task UnreceivedAsnCartonsJoinToTheirPo_EfCore()
    {
        await using var fx = await EfCoreTestDb.CreateAsync();
        await UnreceivedAsnsSliceAsync(fx.Adapter);
    }

    private static async Task UnreceivedAsnsSliceAsync(IErpAdapter erp)
    {
        var openPo = await ConfirmedPoNumberAsync(erp);
        await erp.CreateAsnAsync("ASN-U1", openPo, RecordSource.Vendor, "V-B",
            [new AsnCarton("CARTON-U1", "BAT-1", 10, "LOT-U1")], Idem());
        var gotPo = await ConfirmedPoNumberAsync(erp);
        await erp.CreateAsnAsync("ASN-U2", gotPo, RecordSource.Vendor, "V-B",
            [new AsnCarton("CARTON-U2", "BAT-1", 10, "LOT-U2")], Idem());
        var got = (await erp.GetPurchaseOrderAsync(gotPo))!;
        await erp.PostReceiptAsync(gotPo, "PACK-U2", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(got.Lines[0].PoLineRef, 50, 0, 0, "LOT-U2", null)], Idem());
        var open = await erp.GetUnreceivedAsnsAsync();
        Assert.Single(open);
        var asn = open[0];
        Assert.Equal("ASN-U1", asn.AsnNumber);
        Assert.Equal(openPo, asn.PoNumber);
        Assert.Single(asn.Cartons);
        // What-if-shaped join: carton lot refs link back to their PO lines.
        var linked = await erp.GetPurchaseOrderAsync(asn.PoNumber);
        Assert.NotNull(linked);
        Assert.Contains(linked.Lines, l => l.ItemId == asn.Cartons[0].ItemId);
        Assert.False(string.IsNullOrWhiteSpace(asn.Cartons[0].LotRef));
    }
}

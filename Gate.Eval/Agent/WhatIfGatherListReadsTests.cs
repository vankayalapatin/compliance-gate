// What-if gather over the five pipeline list-reads: complete rows over
// seeded open docs, empty-honest on a fresh ERP, lot-to-PO joins,
// quarantine flags, join caveats, and the rollout fallback when an ERP
// layer has not implemented the reads yet. Test-double data only.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class WhatIfGatherListReadsTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    private static (InMemoryErpAdapter Inner, WhatIfListReadDouble Erp) Create()
    {
        var inner = SeededErp.Create();
        return (inner, new WhatIfListReadDouble(inner));
    }

    private static RuleProposal CapProposal() =>
        MemoFixtures.Proposal(rule: MemoFixtures.CapRule());

    private static Task<PurchaseRequisition> DraftPrAsync(IErpAdapter erp, decimal qty = 600) =>
        erp.CreatePrAsync("planner", "test", "planner",
            [new PurchaseRequisitionLine("SCR-9", qty, "EA")], Idem());

    private static async Task<PurchaseRequisition> ApprovedPrAsync(IErpAdapter erp, decimal qty = 600)
    {
        var pr = await DraftPrAsync(erp, qty);
        Assert.Equal(PrStatus.Draft, pr.Status);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        return (await erp.GetPrAsync(pr.PrNumber))!;
    }

    private static async Task<PurchaseOrder> DraftPoAsync(IErpAdapter erp, decimal qty = 600)
    {
        var pr = await ApprovedPrAsync(erp, qty);
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-C", "test", "planner", Idem());
        Assert.Equal(PoApprovalStatus.Draft, po.ApprovalStatus);
        return po;
    }

    private static async Task<PurchaseOrder> ConfirmedPoAsync(
        IErpAdapter erp, decimal qty = 5, string vendor = "V-B")
    {
        var pr = await erp.CreatePrAsync("planner", "test", "planner",
            [new PurchaseRequisitionLine("SCR-9", qty, "EA")], Idem());
        Assert.Equal(PrStatus.Approved, pr.Status);
        var po = await erp.CreatePoAsync(pr.PrNumber, vendor, "test", "planner", Idem());
        Assert.Equal(PoApprovalStatus.Approved, po.ApprovalStatus);
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var current = (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, "test", "planner",
            current.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        return (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
    }

    [Fact]
    public async Task OpenPrRowsCoverDraftAndSubmitted()
    {
        var (_, erp) = Create();
        var draft = await DraftPrAsync(erp);
        var submitted = await DraftPrAsync(erp, 700);
        await erp.SubmitForApprovalAsync("PR", submitted.PrNumber, "approver", "planner", Idem());

        var (rows, caveats) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        Assert.Equal(2, rows.Count);
        var prRows = rows.Where(r => r.DocumentType == MemoWhatIfGather.PrDocumentType).ToList();
        Assert.Equal(2, prRows.Count);
        Assert.Equal(draft.PrNumber, prRows[0].DocumentNumber);
        Assert.Equal(600m, prRows[0].Quantity);
        Assert.Equal(submitted.PrNumber, prRows[1].DocumentNumber);
        Assert.Equal(700m, prRows[1].Quantity);
        Assert.All(prRows, r =>
        {
            Assert.Equal("SCR-9", r.ItemId);
            Assert.False(r.IsCritical);
            Assert.Null(r.VendorId);
            Assert.Null(r.Country);
            Assert.Null(r.UnitPrice);
            Assert.Equal("USD", r.Currency);
            Assert.False(r.DocPresent);
            Assert.False(r.HasOpenHold);
            Assert.False(r.HasQuarantine);
            Assert.False(r.HasOpenReview);
        });
        Assert.DoesNotContain(caveats, c => c.Contains("Open PRs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DraftPoRowsCoverDraftAndInReview()
    {
        var (_, erp) = Create();
        var draft = await DraftPoAsync(erp);
        var review = await DraftPoAsync(erp);
        await erp.SubmitForApprovalAsync("PO", review.PoNumber, "approver", "planner", Idem());

        var (rows, caveats) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        Assert.Equal(2, rows.Count);
        var poRows = rows.Where(r => r.DocumentType == MemoWhatIfGather.PoDocumentType).ToList();
        Assert.Equal(2, poRows.Count);
        Assert.Equal(draft.PoNumber, poRows[0].DocumentNumber);
        Assert.Equal(review.PoNumber, poRows[1].DocumentNumber);
        Assert.All(poRows, r =>
        {
            Assert.Equal("SCR-9", r.ItemId);
            Assert.False(r.IsCritical);
            Assert.Equal("V-C", r.VendorId);
            Assert.Equal("US", r.Country);
            Assert.Equal(600m, r.Quantity);
            Assert.Equal(1m, r.UnitPrice);
            Assert.Equal("USD", r.Currency);
            Assert.False(r.HasOpenHold);
            Assert.False(r.HasQuarantine);
        });
        Assert.DoesNotContain(caveats, c => c.Contains("Draft POs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenInvoiceRowsJoinPoLines()
    {
        var (_, erp) = Create();
        var po = await ConfirmedPoAsync(erp);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.CreateAsnAsync("ASN-G2-INV", po.PoNumber, "test", "planner",
            [new AsnCarton("CTN-G2-INV", "SCR-9", 5, "LOT-G2-INV")], Idem());
        await erp.PostReceiptAsync(po.PoNumber, "PACK-G2-INV", "test", "planner",
            [new ReceiptLine(lineRef, 5, 0, 0, "LOT-G2-INV", null)], Idem());
        await erp.CreateInvoiceAsync("SUP-G2-INV", "V-B", po.PoNumber, "test", "planner",
            [new InvoiceLineInput(lineRef, 5, 1)], Idem());

        var gather = new MemoWhatIfGather(erp);
        var (rows, caveats) = await gather.GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        var row = Assert.Single(rows.Where(r => r.DocumentType == MemoWhatIfGather.InvoiceDocumentType));
        Assert.Equal("V-B/SUP-G2-INV", row.DocumentNumber);
        Assert.Equal("SCR-9", row.ItemId);
        Assert.False(row.IsCritical);
        Assert.Equal("V-B", row.VendorId);
        Assert.Equal("MX", row.Country);
        Assert.Equal(5m, row.Quantity);
        Assert.Equal(1m, row.UnitPrice);
        Assert.Equal("USD", row.Currency);
        Assert.False(row.HasOpenHold);
        Assert.False(row.HasQuarantine);
        Assert.DoesNotContain(caveats, c => c.Contains("invoice", StringComparison.OrdinalIgnoreCase));
        // The PO is fully received and the ASN is received: only the invoice row remains.
        Assert.DoesNotContain(rows, r => r.DocumentType == MemoWhatIfGather.PoDocumentType);
        Assert.DoesNotContain(rows, r => r.DocumentType == MemoWhatIfGather.AsnDocumentType);

        await erp.RecordMatchResultAsync("V-B", "SUP-G2-INV",
            [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        var (matchedRows, _) = await gather.GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());
        Assert.Single(matchedRows.Where(r => r.DocumentType == MemoWhatIfGather.InvoiceDocumentType));
    }

    [Fact]
    public async Task DraftPaymentRowsExpandOverInvoiceLines()
    {
        var (_, erp) = Create();
        var po = await ConfirmedPoAsync(erp);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(po.PoNumber, "PACK-G2-PAY", "test", "planner",
            [new ReceiptLine(lineRef, 5, 0, 0, "LOT-G2-PAY", null)], Idem());
        await erp.CreateInvoiceAsync("SUP-G2-PAY", "V-B", po.PoNumber, "test", "planner",
            [new InvoiceLineInput(lineRef, 5, 1)], Idem());
        await erp.RecordMatchResultAsync("V-B", "SUP-G2-PAY",
            [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        await erp.SubmitForApprovalAsync("Invoice", "V-B/SUP-G2-PAY", "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", "V-B/SUP-G2-PAY", "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "SUP-G2-PAY", "planner", Idem());
        var payment = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "SUP-G2-PAY")], "test", "planner", Idem());

        var (rows, caveats) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        var row = Assert.Single(rows.Where(r => r.DocumentType == MemoWhatIfGather.PaymentDocumentType));
        Assert.Equal(payment.PaymentNumber, row.DocumentNumber);
        Assert.Equal("SCR-9", row.ItemId);
        Assert.False(row.IsCritical);
        Assert.Equal("V-B", row.VendorId);
        Assert.Equal("MX", row.Country);
        Assert.Equal(5m, row.Quantity);
        Assert.Equal(1m, row.UnitPrice);
        Assert.Equal("USD", row.Currency);
        Assert.False(row.HasOpenHold);
        Assert.False(row.HasQuarantine);
        Assert.DoesNotContain(caveats, c => c.Contains("payment", StringComparison.OrdinalIgnoreCase));
        // The invoice is posted: only the payment row remains.
        Assert.DoesNotContain(rows, r => r.DocumentType == MemoWhatIfGather.InvoiceDocumentType);
    }

    [Fact]
    public async Task UnreceivedAsnRowsCoverCartonsUntilReceipt()
    {
        var (_, erp) = Create();
        var po = await ConfirmedPoAsync(erp);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.CreateAsnAsync("ASN-G2-U", po.PoNumber, "test", "planner",
            [new AsnCarton("CTN-G2-U1", "SCR-9", 3, "LOT-G2-U1"),
             new AsnCarton("CTN-G2-U2", "SCR-9", 2, "LOT-G2-U2")], Idem());

        var gather = new MemoWhatIfGather(erp);
        var (rows, caveats) = await gather.GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        var asnRows = rows.Where(r => r.DocumentType == MemoWhatIfGather.AsnDocumentType)
            .OrderBy(r => r.Quantity).ToList();
        Assert.Equal(2, asnRows.Count);
        Assert.All(asnRows, r =>
        {
            Assert.Equal("ASN-G2-U", r.DocumentNumber);
            Assert.Equal("SCR-9", r.ItemId);
            Assert.False(r.IsCritical);
            Assert.Equal("V-B", r.VendorId);
            Assert.Equal("MX", r.Country);
            Assert.Equal(1m, r.UnitPrice);
            Assert.Equal("USD", r.Currency);
            Assert.False(r.HasOpenHold);
            Assert.False(r.HasQuarantine);
        });
        Assert.Equal(2m, asnRows[0].Quantity);
        Assert.Equal(3m, asnRows[1].Quantity);
        Assert.DoesNotContain(caveats, c => c.Contains("ASN", StringComparison.Ordinal));

        await erp.PostReceiptAsync(po.PoNumber, "PACK-G2-U", "test", "planner",
            [new ReceiptLine(lineRef, 5, 0, 0, "LOT-G2-U1", null)], Idem());
        var (receivedRows, _) = await gather.GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());
        Assert.DoesNotContain(receivedRows, r => r.DocumentType == MemoWhatIfGather.AsnDocumentType);
    }

    [Fact]
    public async Task EmptyHonestWhenNothingOpen()
    {
        var (_, erp) = Create();

        var (rows, caveats) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        Assert.Empty(rows);
        Assert.Equal(2, caveats.Count);
        Assert.Contains(caveats, c => c.Contains("dock observables", StringComparison.Ordinal));
        Assert.Contains(caveats, c => c.Contains("eceipt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LotMapJoinsAsnCartonsAndReceiptLines()
    {
        var (_, erp) = Create();
        var openPo = await ConfirmedPoAsync(erp);
        await erp.CreateAsnAsync("ASN-G2-M1", openPo.PoNumber, "test", "planner",
            [new AsnCarton("CTN-G2-M1", "SCR-9", 5, "LOT-G2-M1")], Idem());
        var gotPo = await ConfirmedPoAsync(erp);
        var lineRef = gotPo.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(gotPo.PoNumber, "PACK-G2-M2", "test", "planner",
            [new ReceiptLine(lineRef, 5, 0, 0, "LOT-G2-M2", null)], Idem());

        var gather = new MemoWhatIfGather(erp);
        var asns = await erp.GetUnreceivedAsnsAsync();
        var asn = Assert.Single(asns);
        Assert.Equal("ASN-G2-M1", asn.AsnNumber);
        var map = await gather.BuildLotPoMapAsync(MemoFixtures.TestKb(), asns);

        Assert.Equal(openPo.PoNumber, map["LOT-G2-M1"].PoNumber);
        Assert.Null(map["LOT-G2-M1"].PoLineRef);
        Assert.Equal(gotPo.PoNumber, map["LOT-G2-M2"].PoNumber);
        Assert.Equal(lineRef, map["LOT-G2-M2"].PoLineRef);
    }

    [Fact]
    public async Task QuarantineFlagsFollowLotsAndLines()
    {
        var (_, erp) = Create();
        var po = await ConfirmedPoAsync(erp, 10);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(po.PoNumber, "PACK-G2-Q", "test", "planner",
            [new ReceiptLine(lineRef, 5, 2, 0, "LOT-G2-Q", null)], Idem());
        await erp.CreateInvoiceAsync("SUP-G2-Q", "V-B", po.PoNumber, "test", "planner",
            [new InvoiceLineInput(lineRef, 5, 1)], Idem());
        await erp.RecordMatchResultAsync("V-B", "SUP-G2-Q",
            [new LineMatchResult(lineRef, true, "Matched.")], Idem());

        var (rows, _) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        var poRow = Assert.Single(rows.Where(r => r.DocumentType == MemoWhatIfGather.PoDocumentType));
        Assert.Equal(5m, poRow.Quantity);
        Assert.True(poRow.HasQuarantine);
        var invRows = rows.Where(r => r.DocumentType == MemoWhatIfGather.InvoiceDocumentType).ToList();
        Assert.Equal(2, invRows.Count);
        Assert.All(invRows, r => Assert.True(r.HasQuarantine));
        Assert.Equal(5m, invRows.Sum(r => r.Quantity));
    }

    [Fact]
    public async Task FallbackCaveatsWhenReadsUnimplemented()
    {
        var erp = new ListReadsMissingDouble(SeededErp.Create());

        var (rows, caveats) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        Assert.Empty(rows);
        Assert.Equal(7, caveats.Count);
        Assert.Contains(caveats, c => c.Contains("Open PRs", StringComparison.Ordinal));
        Assert.Contains(caveats, c => c.Contains("Draft POs", StringComparison.Ordinal));
        Assert.Contains(caveats, c => c.Contains("invoices", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(caveats, c => c.Contains("payments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(caveats, c => c.Contains("ASN", StringComparison.Ordinal));
        Assert.Contains(caveats, c => c.Contains("eceipt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnjoinableInvoiceLinesCaveatHonestly()
    {
        var (_, erp) = Create();
        var po = await ConfirmedPoAsync(erp);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.CreateInvoiceAsync("SUP-G2-J", "V-B", po.PoNumber, "test", "planner",
            [new InvoiceLineInput(lineRef, 5, 1), new InvoiceLineInput("PO-L9", 2, 1)], Idem());

        var (rows, caveats) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        var row = Assert.Single(rows.Where(r => r.DocumentType == MemoWhatIfGather.InvoiceDocumentType));
        Assert.Equal(5m, row.Quantity);
        var caveat = Assert.Single(caveats.Where(c => c.Contains("invoice row", StringComparison.Ordinal)));
        Assert.Contains("1 invoice row", caveat, StringComparison.Ordinal);
        Assert.Contains("V-B/SUP-G2-J", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErpItemMasterResolvesKbMisses()
    {
        var (_, erp) = Create();
        Assert.DoesNotContain(MemoFixtures.TestKb().Items, i => i.Id == SeededErp.Battery);
        var pr = await erp.CreatePrAsync("planner", "test", "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, 600, "EA")], Idem());
        Assert.Equal(PrStatus.Draft, pr.Status);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        var approved = (await erp.GetPrAsync(pr.PrNumber))!;
        var po = await erp.CreatePoAsync(approved.PrNumber, "V-B", "test", "planner", Idem());
        Assert.Equal(PoApprovalStatus.Draft, po.ApprovalStatus);

        var (rows, caveats) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        var poRow = Assert.Single(rows);
        Assert.Equal(MemoWhatIfGather.PoDocumentType, poRow.DocumentType);
        Assert.Equal(SeededErp.Battery, poRow.ItemId);
        Assert.True(poRow.IsCritical);
        Assert.DoesNotContain(caveats, c => c.Contains("neither the pinned KB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownItemsCaveatHonestly()
    {
        var (inner, erp) = Create();
        inner.SeedVendorPrice("V-C", "ZZ-9", 1m);
        var pr = await erp.CreatePrAsync("planner", "test", "planner",
            [new PurchaseRequisitionLine("ZZ-9", 600, "EA")], Idem());
        Assert.Equal(PrStatus.Draft, pr.Status);
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());
        var approved = (await erp.GetPrAsync(pr.PrNumber))!;
        var po = await erp.CreatePoAsync(approved.PrNumber, "V-C", "test", "planner", Idem());
        Assert.Equal(PoApprovalStatus.Draft, po.ApprovalStatus);

        var (rows, caveats) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        Assert.Empty(rows);
        Assert.Equal(3, caveats.Count);
        var caveat = Assert.Single(caveats.Where(c => c.Contains("neither the pinned KB", StringComparison.Ordinal)));
        Assert.Contains("ZZ-9", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnjoinablePaymentLegsCaveatHonestly()
    {
        var (_, erp) = Create();
        var po = await ConfirmedPoAsync(erp);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(po.PoNumber, "PACK-G2-PJ", "test", "planner",
            [new ReceiptLine(lineRef, 5, 0, 0, "LOT-G2-PJ", null)], Idem());
        await erp.CreateInvoiceAsync("SUP-G2-PJ", "V-B", po.PoNumber, "test", "planner",
            [new InvoiceLineInput(lineRef, 5, 1), new InvoiceLineInput("PO-L9", 2, 1)], Idem());
        await erp.RecordMatchResultAsync("V-B", "SUP-G2-PJ",
            [new LineMatchResult(lineRef, true, "Matched.")], Idem());
        await erp.SubmitForApprovalAsync("Invoice", "V-B/SUP-G2-PJ", "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", "V-B/SUP-G2-PJ", "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "SUP-G2-PJ", "planner", Idem());
        var payment = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "SUP-G2-PJ")], "test", "planner", Idem());

        var (rows, caveats) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        var row = Assert.Single(rows.Where(r => r.DocumentType == MemoWhatIfGather.PaymentDocumentType));
        Assert.Equal(payment.PaymentNumber, row.DocumentNumber);
        Assert.Equal(5m, row.Quantity);
        var caveat = Assert.Single(caveats.Where(c => c.Contains("payment row", StringComparison.Ordinal)));
        Assert.Contains("1 payment row", caveat, StringComparison.Ordinal);
        Assert.Contains(payment.PaymentNumber, caveat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsnRowsSurviveAnUnresolvablePo()
    {
        var asn = new AsnDto("ASN-G2-X", "PO-GONE", "test", "planner", "asn-g2-x",
            [new AsnCarton("CTN-G2-X", "SCR-9", 4, "LOT-G2-X")]);

        var (rows, caveats) = await new MemoWhatIfGather(new MissingPoErp(asn))
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        var row = Assert.Single(rows);
        Assert.Equal(MemoWhatIfGather.AsnDocumentType, row.DocumentType);
        Assert.Equal("ASN-G2-X", row.DocumentNumber);
        Assert.Equal("SCR-9", row.ItemId);
        Assert.False(row.IsCritical);
        Assert.Null(row.VendorId);
        Assert.Null(row.Country);
        Assert.Equal(4m, row.Quantity);
        Assert.Null(row.UnitPrice);
        Assert.Null(row.Currency);
        Assert.False(row.HasOpenHold);
        Assert.False(row.HasQuarantine);
        Assert.Equal(2, caveats.Count);
    }

    /// <summary>End-to-end against the real adapter reads (no gather double):
    /// every open doc type produces rows and no fallback reason survives.</summary>
    [Fact]
    public async Task GatherConsumesRealAdapterReadsEndToEnd()
    {
        var erp = SeededErp.Create();
        var pr = await DraftPrAsync(erp);
        var draftPo = await DraftPoAsync(erp);
        var po = await ConfirmedPoAsync(erp);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.CreateAsnAsync("ASN-G2-E2E", po.PoNumber, "test", "planner",
            [new AsnCarton("CTN-G2-E2E", "SCR-9", 5, "LOT-G2-E2E")], Idem());
        await erp.CreateInvoiceAsync("SUP-G2-E2E", "V-B", po.PoNumber, "test", "planner",
            [new InvoiceLineInput(lineRef, 5, 1)], Idem());
        var payPo = await ConfirmedPoAsync(erp);
        var payLineRef = payPo.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(payPo.PoNumber, "PACK-G2-E2E", "test", "planner",
            [new ReceiptLine(payLineRef, 5, 0, 0, "LOT-G2-E2P", null)], Idem());
        await erp.CreateInvoiceAsync("SUP-G2-E2P", "V-B", payPo.PoNumber, "test", "planner",
            [new InvoiceLineInput(payLineRef, 5, 1)], Idem());
        await erp.RecordMatchResultAsync("V-B", "SUP-G2-E2P",
            [new LineMatchResult(payLineRef, true, "Matched.")], Idem());
        await erp.SubmitForApprovalAsync("Invoice", "V-B/SUP-G2-E2P", "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", "V-B/SUP-G2-E2P", "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "SUP-G2-E2P", "planner", Idem());
        var payment = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "SUP-G2-E2P")], "test", "planner", Idem());

        var (rows, caveats) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), CapProposal());

        Assert.Contains(rows, r =>
            r.DocumentType == MemoWhatIfGather.PrDocumentType && r.DocumentNumber == pr.PrNumber);
        Assert.Contains(rows, r =>
            r.DocumentType == MemoWhatIfGather.PoDocumentType && r.DocumentNumber == draftPo.PoNumber);
        Assert.Contains(rows, r =>
            r.DocumentType == MemoWhatIfGather.PoDocumentType && r.DocumentNumber == po.PoNumber);
        Assert.Contains(rows, r => r.DocumentType == MemoWhatIfGather.InvoiceDocumentType);
        Assert.Contains(rows, r => r.DocumentType == MemoWhatIfGather.AsnDocumentType);
        Assert.Contains(rows, r =>
            r.DocumentType == MemoWhatIfGather.PaymentDocumentType && r.DocumentNumber == payment.PaymentNumber);
        Assert.DoesNotContain(caveats, c => c.Contains("does not implement", StringComparison.Ordinal));
        Assert.Equal(2, caveats.Count);
    }

    [Fact]
    public async Task PipelineRowsReadRealDocumentsAndHolds()
    {
        var (_, erp) = Create();
        var po = await ConfirmedPoAsync(erp);
        var lineRef = po.Lines[0].PoLineRef;
        await erp.PostReceiptAsync(po.PoNumber, "PACK-G2-D", "test", "planner",
            [new ReceiptLine(lineRef, 5, 0, 0, "LOT-G2-D", null)], Idem());
        await erp.CreateInvoiceAsync("SUP-G2-D", "V-B", po.PoNumber, "test", "planner",
            [new InvoiceLineInput(lineRef, 5, 1)], Idem());
        await erp.SetHoldAsync("REVIEW", "look", null, "case-g2", "planner",
            "Invoice", "V-B/SUP-G2-D", null, null, null, Idem());
        var proposal = MemoFixtures.Proposal(rule: new ProposedRule(
            MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["docKind"] = "UN38.3", ["partScope"] = "all" }));

        var (rows, _) = await new MemoWhatIfGather(erp)
            .GatherPipelineAsync(MemoFixtures.TestKb(), proposal);

        var row = Assert.Single(rows.Where(r => r.DocumentType == MemoWhatIfGather.InvoiceDocumentType));
        Assert.True(row.DocPresent);
        Assert.True(row.HasOpenHold);
    }

    /// <summary>An ERP whose unreceived ASN names a PO the master does not
    /// resolve: every other read is empty.</summary>
    private sealed class MissingPoErp : IErpAdapter
    {
        private readonly AsnDto _asn;

        public MissingPoErp(AsnDto asn) => _asn = asn;

        public Task<IReadOnlyList<AsnDto>> GetUnreceivedAsnsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AsnDto>>([_asn]);

        public Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken ct = default) =>
            Task.FromResult<PurchaseOrder?>(null);

        public Task<IReadOnlyList<PurchaseOrder>> GetOpenPurchaseOrdersAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PurchaseOrder>>([]);

        public Task<IReadOnlyList<PurchaseRequisition>> GetOpenPurchaseRequisitionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PurchaseRequisition>>([]);

        public Task<IReadOnlyList<PurchaseOrder>> GetDraftPurchaseOrdersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PurchaseOrder>>([]);

        public Task<IReadOnlyList<VendorInvoice>> GetOpenInvoicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VendorInvoice>>([]);

        public Task<IReadOnlyList<Payment>> GetOpenPaymentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Payment>>([]);

        public Task<IReadOnlyList<HoldDto>> GetOpenHoldsAsync(string? documentNumber, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HoldDto>>([]);

        public Task<IReadOnlyList<InventoryTransaction>> GetInventoryTransactionsAsync(string itemId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InventoryTransaction>>([]);

        public Task<SalesOrder?> GetSalesOrderAsync(string soNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<BomLine>> GetBomAsync(string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<decimal> GetStockAsync(string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<VendorDto>> GetVendorsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<VendorDocumentDto>> GetVendorDocumentsAsync(string vendorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GetVendorCountryAsync(string vendorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<decimal> GetVendorVolumeShareAsync(string vendorId, string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<VolumeHistory>> GetVolumeHistoryAsync(string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ItemDto?> GetItemAsync(string itemId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PaymentTermsDto?> GetPaymentTermsAsync(string termsId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorBankDto?> GetVendorBankAsync(string vendorId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<UserDto?> GetUserAsync(string userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<UserDto>> GetUsersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<decimal> GetAutoApproveThresholdAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DelegationDto>> GetDelegationsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ShipmentReceipt?> GetReceiptAsync(string receiptNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorInvoice?> GetInvoiceAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Payment?> GetPaymentAsync(string paymentNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorConfirmation?> GetVendorConfirmationAsync(string poNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AsnDto?> GetAsnAsync(string asnNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Settlement>> GetSettlementsAsync(string vendorId, string supplierInvoiceNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<HoldRelease>> GetHoldHistoryAsync(string holdId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ApprovalDto>> GetApprovalsAsync(string documentType, string documentNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PurchaseRequisition> CreatePrAsync(string requester, string source, string createdBy, IReadOnlyList<PurchaseRequisitionLine> lines, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SubmitForApprovalAsync(string documentType, string documentNumber, string routedTo, string submittedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ApprovalDto> RecordApprovalAsync(string documentType, string documentNumber, string actedBy, bool approved, string? comment, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PurchaseOrder> CreatePoAsync(string prNumber, string vendorId, string source, string createdBy, string idempotencyKey, IReadOnlyList<PoOrderLine>? lines = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorConfirmation> RecordVendorConfirmationAsync(string poNumber, DateOnly confirmDate, string source, string createdBy, IReadOnlyList<VendorConfirmLine> lines, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AsnDto> CreateAsnAsync(string? asnNumber, string poNumber, string source, string createdBy, IReadOnlyList<AsnCarton> cartons, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateSalesOrderStatusAsync(string soNumber, SalesOrderStatus status, string actedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task BlockVendorAsync(string vendorId, string reason, string blockedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnblockVendorAsync(string vendorId, string unblockedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddPoNoteAsync(string poNumber, string note, string createdBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClosePoAsync(string poNumber, string closedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ShipmentReceipt> PostReceiptAsync(string poNumber, string supplierPackingSlipNumber, string source, string createdBy, IReadOnlyList<ReceiptLine> lines, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task QuarantineLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReleaseLotAsync(string lotRef, string holdId, string actedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ConsumeLotAsync(string lotRef, string soNumber, decimal quantity, string actedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<HoldDto> SetHoldAsync(string reasonCode, string note, string? ruleId, string caseId, string setBy, string? documentType, string? documentNumber, string? poLineRef, string? lotRef, string? cartonId, string idempotencyKey, CancellationToken ct = default, string? asnNumber = null) => throw new NotImplementedException();
        public Task ReleaseHoldAsync(string holdId, string releasedBy, string? approvalRef, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorInvoice> CreateInvoiceAsync(string supplierInvoiceNumber, string vendorId, string? poNumber, string source, string createdBy, IReadOnlyList<InvoiceLineInput> lines, string idempotencyKey, DateOnly? invoiceDate = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordMatchResultAsync(string vendorId, string supplierInvoiceNumber, IReadOnlyList<LineMatchResult> results, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorInvoice> PostInvoiceAsync(string vendorId, string supplierInvoiceNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReleaseHeldInvoiceLineAsync(string vendorId, string supplierInvoiceNumber, string? poLineRef, decimal quantity, string releasedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Payment> CreatePaymentDraftAsync(IReadOnlyList<InvoiceKey> invoices, string source, string createdBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Payment> PostPaymentAsync(string draftPaymentNumber, string postedBy, string idempotencyKey, DateOnly? postingDate = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorBankDto> RecordVendorBankChangeAsync(string vendorId, string newAccountRef, string source, string changedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<VendorBankDto> VerifyVendorBankAsync(string vendorId, string verifiedBy, string idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
    }
}

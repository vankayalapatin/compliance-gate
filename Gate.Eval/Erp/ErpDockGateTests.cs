// Dock-gated prepay: received quantity is payable only after the dock
// check completes with every carton verdict a pass. Until then the match
// holds the quantity with "Awaiting dock check" and the release gate
// refuses; after the pass the value releases and pays. Covers critical
// and non-critical items alike, with no IsCritical bypass anywhere.
// Failure paths assert the cited rule id. Test-double data only.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class ErpDockGateTests
{
    private sealed record DockWorld(
        InMemoryErpAdapter Erp,
        string CaseDir,
        string CaseId,
        string PoNumber,
        string PoLineRef,
        string AsnNumber,
        string InvoiceNumber);

    private static string Idem() => Guid.NewGuid().ToString("N");

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gate-erp-dock", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Full buying flow with an ASN: approved PR, approved PO,
    /// two cartons on two lots, receipt, invoice, match, and a case file
    /// linking the invoice so the platform context resolves it.</summary>
    private static async Task<DockWorld> ReceivedWorldAsync(
        string item, string vendor, string invoiceNumber, IErpPolicyContext? context = null,
        string? caseDir = null)
    {
        var dir = caseDir ?? NewDir();
        var erp = SeededErp.CreateWithPolicy(context ?? new CasePolicyContext(dir));
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(item, 80, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Idem());
        }

        var po = await erp.CreatePoAsync(pr.PrNumber, vendor, RecordSource.Human, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "delegate", true, "ok", Idem());
            po = await erp.GetPurchaseOrderAsync(po.PoNumber)
                ?? throw new InvalidOperationException($"PO {po.PoNumber} vanished.");
        }

        var poLineRef = Assert.Single(po.Lines).PoLineRef;
        var price = Assert.Single(po.Lines).UnitPrice;
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, vendor,
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        var asn = await erp.CreateAsnAsync(null, po.PoNumber, RecordSource.Human, "planner",
            [new AsnCarton("CTN-A1", item, 40, "LOT-A1"), new AsnCarton("CTN-A2", item, 40, "LOT-A2")],
            Idem());
        await erp.PostReceiptAsync(po.PoNumber, "PACK-D1", RecordSource.Human, "planner",
            [new ReceiptLine(poLineRef, 40, 0, 0, "LOT-A1", null),
             new ReceiptLine(poLineRef, 40, 0, 0, "LOT-A2", null)], Idem());
        await erp.CreateInvoiceAsync(invoiceNumber, vendor, po.PoNumber,
            RecordSource.Human, "planner", [new InvoiceLineInput(poLineRef, 80, price)], Idem());
        await erp.RecordMatchResultAsync(
            vendor, invoiceNumber, [new LineMatchResult(poLineRef, true, "ok")], Idem());

        var caseId = $"DOCK-{invoiceNumber}";
        var now = DateTimeOffset.UtcNow;
        CaseStore.Save(dir, new CaseFile(
            new Case(caseId, "SO-DOCK", item, "erp-dock-test", CaseStage.ReceiptPosted, now, now,
                null, po.PoNumber, asn.AsnNumber, null, [new InvoiceKey(vendor, invoiceNumber)], [],
                null, null),
            [], []));
        return new DockWorld(erp, dir, caseId, po.PoNumber, poLineRef, asn.AsnNumber, invoiceNumber);
    }

    /// <summary>Real evaluator pass verdicts for the named cartons, saved as
    /// the case's dock evidence.</summary>
    private static void SavePass(DockWorld w, string item, params string[] cartons)
    {
        var class9 = string.Equals(item, SeededErp.Battery, StringComparison.Ordinal);
        var reads = cartons
            .Select(c => new CartonRead(
                c, item, w.PoNumber, c.Replace("CTN-", "LOT-", StringComparison.Ordinal), 40,
                true, true, "UN3480", class9, class9, false, null, $"img-{c}", 0.95m))
            .ToList();
        var verdicts = new DockEvaluator().EvaluateDock(
            cartons.ToList(), reads, w.PoNumber,
            cartons.ToDictionary(
                c => c, c => c.Replace("CTN-", "LOT-", StringComparison.Ordinal),
                StringComparer.Ordinal));
        foreach (var (_, list) in verdicts)
        {
            Assert.NotEmpty(list);
            Assert.All(list, v => Assert.Equal(VerdictKind.Pass, v.Kind));
        }

        CaseStore.SaveDock(w.CaseDir, new DockEvidence(w.CaseId, [], verdicts, DateTimeOffset.UtcNow));
    }

    private static async Task<VendorInvoice> InvoiceOf(DockWorld w, string vendor) =>
        await w.Erp.GetInvoiceAsync(vendor, w.InvoiceNumber)
            ?? throw new InvalidOperationException($"Invoice {w.InvoiceNumber} vanished.");

    [Fact]
    public async Task MatchHoldsEverythingAwaitingDockCheckWithNoEvidence()
    {
        var w = await ReceivedWorldAsync(SeededErp.Battery, "V-B", "INV-D1");

        var invoice = await InvoiceOf(w, "V-B");
        var held = Assert.Single(invoice.Lines);
        Assert.Equal(LineDisposition.Held, held.Disposition);
        Assert.Equal(80m, held.Quantity);
        Assert.Equal("Awaiting dock check", held.HoldReason);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Erp.CreatePaymentDraftAsync(
                [new InvoiceKey("V-B", "INV-D1")], null, RecordSource.Human, "planner", Idem()));
        Assert.StartsWith("Nothing due: held value excluded", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseRefusedPreDockThenPaysAfterPass()
    {
        var w = await ReceivedWorldAsync(SeededErp.Battery, "V-B", "INV-D2");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Erp.ReleaseHeldInvoiceLineAsync("V-B", "INV-D2", w.PoLineRef, 80, "planner", Idem()));
        Assert.StartsWith(ComplianceEngine.PrePayHoldId, refused.Message, StringComparison.Ordinal);
        Assert.Contains("LOT-A1", refused.Message, StringComparison.Ordinal);
        Assert.Contains("LOT-A2", refused.Message, StringComparison.Ordinal);
        Assert.Contains("awaiting dock check", refused.Message, StringComparison.Ordinal);

        SavePass(w, SeededErp.Battery, "CTN-A1", "CTN-A2");
        await w.Erp.ReleaseHeldInvoiceLineAsync("V-B", "INV-D2", w.PoLineRef, 80, "planner", Idem());
        await w.Erp.SubmitForApprovalAsync("Invoice", "V-B/INV-D2", "delegate", "planner", Idem());
        await w.Erp.RecordApprovalAsync("Invoice", "V-B/INV-D2", "delegate", true, "ok", Idem());
        await w.Erp.PostInvoiceAsync("V-B", "INV-D2", "planner", Idem());

        var draft = await w.Erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "INV-D2")], null, RecordSource.Human, "planner", Idem());
        await w.Erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "finance", "planner", Idem());
        await w.Erp.RecordApprovalAsync("Payment", draft.PaymentNumber, "finance", true, "ok", Idem());
        var posted = await w.Erp.PostPaymentAsync(draft.PaymentNumber, "finance", Idem());
        Assert.Equal(PaymentStatus.Posted, posted.Status);
    }

    [Fact]
    public async Task PartialPassReleasesOnlyPassedLot()
    {
        var w = await ReceivedWorldAsync(SeededErp.Battery, "V-B", "INV-D3");
        SavePass(w, SeededErp.Battery, "CTN-A1");

        // Re-match after the partial pass: the passed lot is accepted,
        // the pending lot stays held.
        await w.Erp.RecordMatchResultAsync("V-B", "INV-D3",
            [new LineMatchResult(w.PoLineRef, true, "ok")], Idem());
        var invoice = await InvoiceOf(w, "V-B");
        Assert.Equal(2, invoice.Lines.Count);
        var accepted = Assert.Single(invoice.Lines, l => l.Disposition == LineDisposition.Accepted);
        Assert.Equal(40m, accepted.Quantity);
        var held = Assert.Single(invoice.Lines, l => l.Disposition == LineDisposition.Held);
        Assert.Equal(40m, held.Quantity);
        Assert.Equal("Awaiting dock check", held.HoldReason);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.Erp.ReleaseHeldInvoiceLineAsync("V-B", "INV-D3", w.PoLineRef, 40, "planner", Idem()));
        Assert.StartsWith(ComplianceEngine.PrePayHoldId, refused.Message, StringComparison.Ordinal);
        Assert.Contains("LOT-A2", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("LOT-A1", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuarantinedLotHoldsExactlyItsOwnValue()
    {
        // One carton passed, one held and quarantined: the held carton is
        // both unpassed and quarantined, but the match must hold its value
        // once, not once per taint.
        var w = await ReceivedWorldAsync(SeededErp.Battery, "V-B", "INV-D6");
        SavePass(w, SeededErp.Battery, "CTN-A1");
        var hold = await w.Erp.SetHoldAsync(
            "RELABEL", "missing mark", DockEvaluator.MarkRuleId, w.CaseId,
            "planner", "Receipt", null, w.PoLineRef, "LOT-A2", "CTN-A2", Idem());
        await w.Erp.QuarantineLotAsync("LOT-A2", hold.HoldId, "planner", Idem());

        await w.Erp.RecordMatchResultAsync("V-B", "INV-D6",
            [new LineMatchResult(w.PoLineRef, true, "ok")], Idem());
        var invoice = await InvoiceOf(w, "V-B");
        Assert.Equal(2, invoice.Lines.Count);
        var accepted = Assert.Single(invoice.Lines, l => l.Disposition == LineDisposition.Accepted);
        Assert.Equal(40m, accepted.Quantity);
        var held = Assert.Single(invoice.Lines, l => l.Disposition == LineDisposition.Held);
        Assert.Equal(40m, held.Quantity);
        Assert.StartsWith("Quarantine hold", held.HoldReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonCriticalScrewHeldIdentically()
    {
        var w = await ReceivedWorldAsync(SeededErp.Screw, "V-C", "INV-D4");
        SavePass(w, SeededErp.Screw, "CTN-A1", "CTN-A2");

        // Non-critical items pass through the same gate with no bypass:
        // held before the pass here, payable only after it.
        var dir = NewDir();
        var preWorld = await ReceivedWorldAsync(SeededErp.Screw, "V-C", "INV-D5", caseDir: dir);
        var pre = await InvoiceOf(preWorld, "V-C");
        Assert.Equal("Awaiting dock check", Assert.Single(pre.Lines).HoldReason);

        await w.Erp.ReleaseHeldInvoiceLineAsync("V-C", "INV-D4", w.PoLineRef, 80, "planner", Idem());
        var invoice = await InvoiceOf(w, "V-C");
        Assert.All(invoice.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
    }

    [Fact]
    public async Task NullContextKeepsTodayBehavior()
    {
        // Unit-test ERP without a platform context has no dock input and
        // behaves as before: the app always wires the real context.
        var erp = SeededErp.CreateWithPolicy();
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, 80, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Idem());
        }

        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.Human, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "delegate", true, "ok", Idem());
            po = await erp.GetPurchaseOrderAsync(po.PoNumber)
                ?? throw new InvalidOperationException($"PO {po.PoNumber} vanished.");
        }

        var poLineRef = Assert.Single(po.Lines).PoLineRef;
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-B",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        await erp.CreateAsnAsync(null, po.PoNumber, RecordSource.Human, "planner",
            [new AsnCarton("CTN-N1", SeededErp.Battery, 80, "LOT-N1")], Idem());
        await erp.PostReceiptAsync(po.PoNumber, "PACK-N1", RecordSource.Human, "planner",
            [new ReceiptLine(poLineRef, 80, 0, 0, "LOT-N1", null)], Idem());
        await erp.CreateInvoiceAsync("INV-N1", "V-B", po.PoNumber,
            RecordSource.Human, "planner",
            [new InvoiceLineInput(poLineRef, 80, Assert.Single(po.Lines).UnitPrice)], Idem());
        await erp.RecordMatchResultAsync(
            "V-B", "INV-N1", [new LineMatchResult(poLineRef, true, "ok")], Idem());

        var invoice = await erp.GetInvoiceAsync("V-B", "INV-N1");
        Assert.NotNull(invoice);
        Assert.All(invoice.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
    }

    [Fact]
    public async Task NoAsnMeansNoDockGate()
    {
        // Receipt lots with no ASN cartons declare nothing photographable,
        // so the dock gate does not apply to them; holds, quarantine, and
        // reviews still do.
        var dir = NewDir();
        var erp = SeededErp.CreateWithPolicy(new CasePolicyContext(dir));
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, 80, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Idem());
        }

        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.Human, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "delegate", true, "ok", Idem());
            po = await erp.GetPurchaseOrderAsync(po.PoNumber)
                ?? throw new InvalidOperationException($"PO {po.PoNumber} vanished.");
        }

        var poLineRef = Assert.Single(po.Lines).PoLineRef;
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-B",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        await erp.PostReceiptAsync(po.PoNumber, "PACK-N2", RecordSource.Human, "planner",
            [new ReceiptLine(poLineRef, 80, 0, 0, "LOT-N2", null)], Idem());
        await erp.CreateInvoiceAsync("INV-N2", "V-B", po.PoNumber,
            RecordSource.Human, "planner",
            [new InvoiceLineInput(poLineRef, 80, Assert.Single(po.Lines).UnitPrice)], Idem());
        await erp.RecordMatchResultAsync(
            "V-B", "INV-N2", [new LineMatchResult(poLineRef, true, "ok")], Idem());

        var invoice = await erp.GetInvoiceAsync("V-B", "INV-N2");
        Assert.NotNull(invoice);
        Assert.All(invoice.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
    }
}

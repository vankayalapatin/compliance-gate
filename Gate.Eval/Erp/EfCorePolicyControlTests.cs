// EfCore control-point policy tests: the same hooks as the fake, against
// sqlite, proving the queries translate and the refusals carry rule id,
// pack version, and reason. Test doubles only.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.EntityFrameworkCore;

namespace Gate.Eval;

public sealed class EfCorePolicyControlTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gate-ef-dock", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static EfCoreErpAdapter PolicyAdapter(EfCoreTestDb db)
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlite($"Data Source={db.FilePath}")
            .Options;
        return new EfCoreErpAdapter(options, new ErpPolicy());
    }

    private static async Task<string> ApprovedPrAsync(EfCoreErpAdapter erp)
    {
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine("BAT-1", 90, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Idem());
        }

        return pr.PrNumber;
    }

    [Fact]
    public async Task SourcingRunsOnPoCreateWithCitedRefusal()
    {
        await using var db = await EfCoreTestDb.CreateAsync();
        var erp = PolicyAdapter(db);
        var pr = await ApprovedPrAsync(erp);

        // The fixture packs UN38.3 and SDS only; RoHS lapses the award.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.CreatePoAsync(pr, "V-B", RecordSource.Human, "planner", Idem()));

        Assert.StartsWith("ELEC-01 (ELEC v", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PayableRunsOnDraftAndClearsOnRelease()
    {
        // Screws (non-critical, no pack applies) keep sourcing green so the
        // payable gate is the only thing under test.
        await using var db = await EfCoreTestDb.CreateAsync();
        var erp = PolicyAdapter(db);
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine("SCR-9", 100, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Idem());
        }

        var po = await erp.CreatePoAsync(pr.PrNumber, "V-C", RecordSource.Human, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "delegate", true, "ok", Idem());
            po = (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
        }

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-C",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        var line = po.Lines.Single(l => l.ItemId == "SCR-9");
        await erp.PostReceiptAsync(po.PoNumber, "PACK-T1", RecordSource.Human, "planner",
            [new ReceiptLine(line.PoLineRef, 80, 0, 0, "LOT-OPEN", null),
             new ReceiptLine(line.PoLineRef, 20, 0, 0, "LOT-QUAR", null)],
            Idem());
        var hold = await erp.SetHoldAsync("RELABEL", "missing mark", DockEvaluator.MarkRuleId,
            "CASE-T", "planner", "Receipt", null, null, "LOT-QUAR", null, Idem());
        await erp.QuarantineLotAsync("LOT-QUAR", hold.HoldId, "planner", Idem());

        await erp.CreateInvoiceAsync("INV-T1", "V-C", po.PoNumber, RecordSource.Human, "planner",
            [new InvoiceLineInput(line.PoLineRef, 100, 1m)], Idem());
        await erp.RecordMatchResultAsync("V-C", "INV-T1",
            [new LineMatchResult(line.PoLineRef, true, "ok")], Idem());
        await erp.SubmitForApprovalAsync("Invoice", "V-C/INV-T1", "delegate", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", "V-C/INV-T1", "delegate", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-C", "INV-T1", "planner", Idem());

        // Quarantine holds only its own value: accepted value pays.
        var draft = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-C", "INV-T1")], null, RecordSource.Human, "planner", Idem());
        Assert.Equal(80 * 1m, draft.Lines.Sum(l => l.Amount));
        await erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", draft.PaymentNumber, "finance", true, "ok", Idem());
        await erp.PostPaymentAsync(draft.PaymentNumber, "finance", Idem());

        // The held value itself is neither payable nor releasable.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.ReleaseHeldInvoiceLineAsync("V-C", "INV-T1", line.PoLineRef, 20, "planner", Idem()));
        Assert.StartsWith("ELEC-04 (ELEC v", ex.Message, StringComparison.Ordinal);
        Assert.Contains("LOT-QUAR", ex.Message, StringComparison.Ordinal);

        await erp.ReleaseHoldAsync(hold.HoldId, "planner", null, Idem());
        await erp.ReleaseLotAsync("LOT-QUAR", hold.HoldId, "planner", Idem());
        await erp.ReleaseHeldInvoiceLineAsync("V-C", "INV-T1", line.PoLineRef, 20, "planner", Idem());
        var remainder = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-C", "INV-T1")], null, RecordSource.Human, "planner", Idem());
        Assert.Equal(20 * 1m, remainder.Lines.Sum(l => l.Amount));
    }

    [Fact]
    public async Task DockPendingHoldsThenReleasesAfterPass()
    {
        // Screws keep sourcing green so the dock gate is the only thing
        // under test; same hooks as the fake, against sqlite.
        await using var db = await EfCoreTestDb.CreateAsync();
        var dir = NewDir();
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlite($"Data Source={db.FilePath}")
            .Options;
        var erp = new EfCoreErpAdapter(options, new ErpPolicy(), new CasePolicyContext(dir));
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine("SCR-9", 100, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Idem());
        }

        var po = await erp.CreatePoAsync(pr.PrNumber, "V-C", RecordSource.Human, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "delegate", true, "ok", Idem());
            po = (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
        }

        var line = po.Lines.Single(l => l.ItemId == "SCR-9");
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, "V-C",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        var asn = await erp.CreateAsnAsync(null, po.PoNumber, RecordSource.Human, "planner",
            [new AsnCarton("CTN-E1", "SCR-9", 50, "LOT-E1"),
             new AsnCarton("CTN-E2", "SCR-9", 50, "LOT-E2")], Idem());
        await erp.PostReceiptAsync(po.PoNumber, "PACK-E1", RecordSource.Human, "planner",
            [new ReceiptLine(line.PoLineRef, 50, 0, 0, "LOT-E1", null),
             new ReceiptLine(line.PoLineRef, 50, 0, 0, "LOT-E2", null)], Idem());
        await erp.CreateInvoiceAsync("INV-E1", "V-C", po.PoNumber, RecordSource.Human, "planner",
            [new InvoiceLineInput(line.PoLineRef, 100, 1m)], Idem());
        await erp.RecordMatchResultAsync("V-C", "INV-E1",
            [new LineMatchResult(line.PoLineRef, true, "ok")], Idem());

        var now = DateTimeOffset.UtcNow;
        CaseStore.Save(dir, new CaseFile(
            new Case("DOCK-INV-E1", "SO-DOCK", "SCR-9", "ef-dock-test", CaseStage.ReceiptPosted,
                now, now, null, po.PoNumber, asn.AsnNumber, null,
                [new InvoiceKey("V-C", "INV-E1")], [], null, null),
            [], []));

        var invoice = await erp.GetInvoiceAsync("V-C", "INV-E1");
        Assert.NotNull(invoice);
        var held = Assert.Single(invoice.Lines);
        Assert.Equal(LineDisposition.Held, held.Disposition);
        Assert.Equal("Awaiting dock check", held.HoldReason);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            erp.ReleaseHeldInvoiceLineAsync("V-C", "INV-E1", line.PoLineRef, 100, "planner", Idem()));
        Assert.StartsWith("ELEC-04 (ELEC v", refused.Message, StringComparison.Ordinal);
        Assert.Contains("LOT-E1", refused.Message, StringComparison.Ordinal);
        Assert.Contains("awaiting dock check", refused.Message, StringComparison.Ordinal);

        var reads = new List<CartonRead>
        {
            new("CTN-E1", "SCR-9", po.PoNumber, "LOT-E1", 50, true, true, "UN3480",
                false, false, false, null, "img-CTN-E1", 0.95m),
            new("CTN-E2", "SCR-9", po.PoNumber, "LOT-E2", 50, true, true, "UN3480",
                false, false, false, null, "img-CTN-E2", 0.95m),
        };
        var verdicts = new DockEvaluator().EvaluateDock(
            ["CTN-E1", "CTN-E2"], reads, po.PoNumber,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CTN-E1"] = "LOT-E1",
                ["CTN-E2"] = "LOT-E2",
            });
        foreach (var (_, list) in verdicts)
        {
            Assert.NotEmpty(list);
            Assert.All(list, v => Assert.Equal(VerdictKind.Pass, v.Kind));
        }

        CaseStore.SaveDock(dir, new DockEvidence("DOCK-INV-E1", [], verdicts, DateTimeOffset.UtcNow));
        await erp.ReleaseHeldInvoiceLineAsync("V-C", "INV-E1", line.PoLineRef, 100, "planner", Idem());
        await erp.SubmitForApprovalAsync("Invoice", "V-C/INV-E1", "delegate", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", "V-C/INV-E1", "delegate", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-C", "INV-E1", "planner", Idem());
        var draft = await erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-C", "INV-E1")], null, RecordSource.Human, "planner", Idem());
        Assert.Equal(100 * 1m, draft.Lines.Sum(l => l.Amount));
    }
}

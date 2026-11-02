// EfCore control-point policy tests: the same hooks as the fake, against
// sqlite, proving the queries translate and the refusals carry rule id,
// pack version, and reason. Test doubles only.
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.EntityFrameworkCore;

namespace Gate.Eval;

public sealed class EfCorePolicyControlTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

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
}

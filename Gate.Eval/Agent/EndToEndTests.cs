// Full shortfall-to-payment run on fakes: the behavioral half of the
// independence proof. Covers the demo path with the cheaper vendor failing
// CORP-02 and partial pay holding the failed value back.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class EndToEndTests
{
    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ShortfallToPaymentOnFakes()
    {
        var erp = SeededErp.Create();
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());

        var shortfall = await agent.ComputeShortfallAsync("SO-1001");
        Assert.Equal(2, shortfall.Count);

        var pr = await agent.RaisePrAsync("planner", "approver", RecordSource.P2pAgent, shortfall);
        Assert.Equal(PrStatus.Submitted, pr.Status);
        Assert.Equal(RecordSource.P2pAgent, pr.Source);
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());

        var (po, screenings) = await agent.ScreenAndOrderAsync(pr.PrNumber, ["V-A", "V-B"], RecordSource.P2pAgent, "case-1");
        Assert.Equal("V-B", po.VendorId);
        var rejected = screenings.Single(s => s.VendorId == "V-A");
        Assert.False(rejected.Passed);
        Assert.Contains(rejected.Verdicts, v => v.RuleId == ComplianceEngine.CorpOriginCapId && v.Kind == VerdictKind.Block);

        await erp.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("PO", po.PoNumber, "approver", true, "ok", Idem());
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, po.VendorId,
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());

        var batteryLine = po.Lines.Single(l => l.ItemId == SeededErp.Battery);
        await erp.PostReceiptAsync(po.PoNumber, "PACK-E2E", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(batteryLine.PoLineRef, batteryLine.QtyOrdered, 0, 0, "LOT-E2E", "ok")], Idem());

        var matched = await agent.MatchInvoiceAsync("V-B", "INV-100", po.PoNumber, "vendor", "planner",
            [new InvoiceLineInput(batteryLine.PoLineRef, batteryLine.QtyOrdered, 12m)]);
        Assert.All(matched.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));

        var invKey = $"V-B/INV-100";
        await erp.SubmitForApprovalAsync("Invoice", invKey, "approver", "planner", Idem());
        await erp.RecordApprovalAsync("Invoice", invKey, "approver", true, "ok", Idem());
        await erp.PostInvoiceAsync("V-B", "INV-100", "planner", Idem());

        var (draft, skipped) = await agent.RequestPaymentAsync([new InvoiceKey("V-B", "INV-100")], RecordSource.P2pAgent);
        Assert.NotNull(draft);
        Assert.Empty(skipped);
        Assert.Equal(90 * 12m, draft.Lines.Single().Amount);
        await erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "finance", "planner", Idem());
        await erp.RecordApprovalAsync("Payment", draft.PaymentNumber, "finance", true, "ok", Idem());
        var posted = await erp.PostPaymentAsync(draft.PaymentNumber, "finance", Idem());
        Assert.Equal(PaymentStatus.Posted, posted.Status);
    }

    [Fact]
    public async Task NonCriticalPartRunsStraightThroughVendorC()
    {
        var erp = SeededErp.Create();
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());

        var shortfall = await agent.ComputeShortfallAsync("SO-1001");
        var screws = shortfall.Where(s => s.ItemId == SeededErp.Screw).ToList();
        Assert.Single(screws);

        var pr = await agent.RaisePrAsync("planner", "approver", RecordSource.P2pAgent, screws);
        Assert.Equal(PrStatus.Approved, pr.Status);

        var (po, screenings) = await agent.ScreenAndOrderAsync(pr.PrNumber, ["V-C"], RecordSource.P2pAgent, "case-screw");
        Assert.Equal("V-C", po.VendorId);
        var screening = Assert.Single(screenings);
        Assert.True(screening.Passed);
        Assert.All(screening.Verdicts, v => Assert.Equal(VerdictKind.Pass, v.Kind));
    }

    [Fact]
    public async Task DocumentOnlyScreeningFailureSkipsMasterBlock()
    {
        var erp = SeededErp.Create();
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());

        var shortfall = await agent.ComputeShortfallAsync("SO-1001");
        var pr = await agent.RaisePrAsync("planner", "approver", RecordSource.P2pAgent, shortfall);
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Idem());

        var (po, screenings) = await agent.ScreenAndOrderAsync(
            pr.PrNumber, ["V-C", "V-B"], RecordSource.P2pAgent, "case-doc");
        Assert.Equal("V-B", po.VendorId);
        var loser = screenings.Single(s => s.VendorId == "V-C");
        Assert.False(loser.Passed);
        Assert.Contains(loser.Verdicts, v => v.RuleId == "BAT-LI-01" && v.Kind == VerdictKind.Block);
        var vendors = await erp.GetVendorsAsync();
        Assert.False(vendors.Single(v => v.VendorId == "V-C").IsBlocked);
    }

    [Fact]
    public async Task HeldLinesStayOutOfPayment()
    {
        var erp = SeededErp.Create();
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());

        var matched = await agent.MatchInvoiceAsync("", "INV-200", null, "vendor", "planner",
            [new InvoiceLineInput("PO-L9", 5, 10)]);
        Assert.All(matched.Lines, l => Assert.Equal(LineDisposition.Held, l.Disposition));

        var (payment, skipped) = await agent.RequestPaymentAsync([new InvoiceKey("", "INV-200")], RecordSource.P2pAgent);
        Assert.Null(payment);
        Assert.Equal(["INV-200"], skipped);
    }
}

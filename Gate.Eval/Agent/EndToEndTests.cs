// Full shortfall-to-payment run on fakes: the behavioral half of the
// independence proof. Covers the demo path with the cheaper vendor failing
// CORP-02 and partial pay holding the failed value back.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class EndToEndTests
{
    [Fact]
    public async Task ShortfallToPaymentOnFakes()
    {
        var erp = SeededErp.Create();
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());

        var shortfall = await agent.ComputeShortfallAsync("SO-1001");
        Assert.Equal(2, shortfall.Count);

        var pr = await agent.RaisePrAsync("planner", "approver", RecordSource.P2pAgent, shortfall);
        Assert.Equal(DocStatus.Submitted, pr.Status);
        Assert.Equal(RecordSource.P2pAgent, pr.Source);
        await erp.RecordApprovalAsync(new ApprovalDto("PR", pr.PrNumber, "approver", "approver", false, DateTimeOffset.UtcNow, true, null, 1000));

        var (po, screenings) = await agent.ScreenAndOrderAsync(pr.PrNumber, ["V-A", "V-B"], "USD", RecordSource.P2pAgent, "case-1");
        Assert.Equal("V-B", po.VendorId);
        var rejected = screenings.Single(s => s.VendorId == "V-A");
        Assert.False(rejected.Passed);
        Assert.Contains(rejected.Verdicts, v => v.RuleId == ComplianceEngine.CorpOriginCapId && v.Kind == VerdictKind.Block);

        var batteryLine = po.Lines.Single(l => l.ItemId == SeededErp.Battery);
        erp.SetPoLinePrice(po.PoNumber, batteryLine.PoLineRef, 12m);
        await erp.PostReceiptAsync(po.PoNumber, RecordSource.P2pAgent,
            [new ReceiptLine(batteryLine.PoLineRef, batteryLine.QtyOrdered, 0, "ok")]);

        var invoice = new VendorInvoice("INV-100", "V-B", po.PoNumber, DocStatus.Submitted, "USD", "vendor",
            [new VendorInvoiceLine(batteryLine.PoLineRef, batteryLine.QtyOrdered, 12m, LineDisposition.Accepted, null)], []);
        var matched = await agent.MatchInvoiceAsync(invoice);
        Assert.All(matched.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));

        var (payment, skipped) = await agent.RequestPaymentAsync(["INV-100"], "USD", RecordSource.P2pAgent);
        Assert.NotNull(payment);
        Assert.Empty(skipped);
        Assert.Equal(90 * 12m, payment.Lines.Single().Amount);
    }

    [Fact]
    public async Task HeldLinesStayOutOfPayment()
    {
        var erp = SeededErp.Create();
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());

        var invoice = new VendorInvoice("INV-200", "V-B", null, DocStatus.Submitted, "USD", "vendor",
            [new VendorInvoiceLine("PO-L9", 5, 10, LineDisposition.Accepted, null)], []);
        var matched = await agent.MatchInvoiceAsync(invoice);
        Assert.All(matched.Lines, l => Assert.Equal(LineDisposition.Held, l.Disposition));

        var (payment, skipped) = await agent.RequestPaymentAsync(["INV-200"], "USD", RecordSource.P2pAgent);
        Assert.Null(payment);
        Assert.Equal(["INV-200"], skipped);
    }
}

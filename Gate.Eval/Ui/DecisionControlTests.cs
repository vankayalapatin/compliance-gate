// Control refusals surface through the Decisions path: the UI never
// pre-checks — every attempt runs through the runner/ERP and the refusal
// reason comes back as the outcome message on the card. Tests run the real
// runner against the in-memory fake ERP (unit tests only) with local-only
// data. No live calls.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class DecisionControlTests
{
    private sealed record Harness(InMemoryErpAdapter Erp, CaseCacheStore Store, CaseRunner Runner);

    private static Harness Create()
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("TST");
        erp.SeedAutoApproveThreshold(10m);
        erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        erp.SeedVendor(new VendorDto("V-TEST", "Test Vendor", "US", false, null, "NET0"));
        erp.SeedVendorPrice("V-TEST", "ITEM-1", 10m);
        erp.SeedUser(new UserDto("req", "requester", 50m, "TST"));
        erp.SeedUser(new UserDto("fin", "payment-approver", 1_000_000m, "TST"));
        erp.SeedUser(new UserDto("other", "approver", 1_000_000m, "TST"));
        erp.SeedUser(new UserDto("tiny", "approver", 1m, "TST"));
        var dir = UiTestSupport.NewDir("gate-ui-controls");
        var store = new CaseCacheStore(dir);
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models, compliance,
            new DockEvaluatorAdapter(), dir);
        return new Harness(erp, store, runner);
    }

    private static DecisionOutcomeService Outcomes(Harness h) =>
        new(h.Store, UiTestLogger<DecisionOutcomeService>.Instance, h.Runner);

    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task PaymentByInvoiceApproverRefusesWithSodReason()
    {
        var h = Create();
        var po = await ApprovedPoAsync(h, "other");
        var line = po.Lines.Single();
        await h.Erp.PostReceiptAsync(po.PoNumber, "PACK-1", RecordSource.P2pAgent, "req",
            [new ReceiptLine(line.PoLineRef, 5m, 0m, 0m, "LOT-1", null)], Idem());
        await h.Erp.CreateInvoiceAsync("INV-1", "V-TEST", po.PoNumber, RecordSource.P2pAgent, "req",
            [new InvoiceLineInput(line.PoLineRef, 5m, line.UnitPrice)], Idem());
        await h.Erp.RecordMatchResultAsync("V-TEST", "INV-1",
            [new LineMatchResult(line.PoLineRef, true, "Matched.")], Idem());
        await h.Erp.SubmitForApprovalAsync("Invoice", "V-TEST/INV-1", "fin", "req", Idem());
        await h.Erp.RecordApprovalAsync("Invoice", "V-TEST/INV-1", "fin", true, "ok", Idem());
        await h.Erp.PostInvoiceAsync("V-TEST", "INV-1", "req", Idem());
        h.Erp.SeedVendorBank(new VendorBankDto("V-TEST", "BANK-X", false, null));
        await h.Erp.VerifyVendorBankAsync("V-TEST", "other", Idem());
        var draft = await h.Erp.CreatePaymentDraftAsync(
            [new InvoiceKey("V-TEST", "INV-1")], RecordSource.P2pAgent, "req", Idem());
        await h.Erp.SubmitForApprovalAsync("Payment", draft.PaymentNumber, "fin", "req", Idem());
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PaymentApproval, "Payment", draft.PaymentNumber)
                with { RequestedTo = "fin" },
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, "pay it", "fin");

        Assert.False(outcome.Ok);
        Assert.Contains("different people", outcome.Message);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Pending, reread.Decisions.Single().Status);
    }

    [Fact]
    public async Task ApprovalAboveSigningLimitRefusesWithLimitReason()
    {
        var h = Create();
        var pr = await h.Erp.CreatePrAsync(
            "req", RecordSource.P2pAgent, "req",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Idem());
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "tiny", "req", Idem());
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber)
                with { RequestedTo = "tiny" },
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, null, "tiny");

        Assert.False(outcome.Ok);
        Assert.Contains("signing limit", outcome.Message);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Pending, reread.Decisions.Single().Status);
    }

    [Fact]
    public async Task SelfApprovalRefusesWithRequesterReason()
    {
        var h = Create();
        var pr = await h.Erp.CreatePrAsync(
            "other", RecordSource.P2pAgent, "other",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Idem());
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, "other", "other", Idem());
        await h.Store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber)
                with { RequestedTo = "other" },
        ]));

        var outcome = await Outcomes(h).ApplyAsync("CASE-1", "D-1", true, null, "other");

        Assert.False(outcome.Ok);
        Assert.Contains("own request", outcome.Message);
        var reread = await h.Store.ReadAsync("CASE-1");
        Assert.NotNull(reread);
        Assert.Equal(DecisionStatus.Pending, reread.Decisions.Single().Status);
    }

    private static async Task<PurchaseOrder> ApprovedPoAsync(Harness h, string approver)
    {
        var pr = await h.Erp.CreatePrAsync(
            "req", RecordSource.P2pAgent, "req",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Idem());
        await h.Erp.SubmitForApprovalAsync("PR", pr.PrNumber, approver, "req", Idem());
        await h.Erp.RecordApprovalAsync("PR", pr.PrNumber, approver, true, "ok", Idem());
        var po = await h.Erp.CreatePoAsync(pr.PrNumber, "V-TEST", RecordSource.P2pAgent, "req", Idem());
        await h.Erp.SubmitForApprovalAsync("PO", po.PoNumber, approver, "req", Idem());
        await h.Erp.RecordApprovalAsync("PO", po.PoNumber, approver, true, "ok", Idem());
        await h.Erp.RecordVendorConfirmationAsync(po.PoNumber, DateOnly.FromDateTime(DateTime.UtcNow),
            RecordSource.P2pAgent, "req",
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        return po;
    }
}

// Payment run-date tests: AdvanceAsync threads payUpToDate to the pre-pay
// draft overload (null means no limit); invoices due after the run date are
// skipped, and a run with nothing due parks in AwaitDueDate.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class PayUpToDateTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp, CaseRunner Runner, P2pAgent Core, string CaseDir);

    private static Harness Create()
    {
        var erp = SeededErp.Create();
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = Path.Combine(Path.GetTempPath(), "gate-paydate-test", Guid.NewGuid().ToString("N"));
        var core = new P2pAgent(erp, models, compliance);
        var runner = new CaseRunner(erp, core, models, compliance, new DockEvaluatorAdapter(), dir);
        return new Harness(erp, runner, core, dir);
    }

    private static async Task<string> SetupPostedInvoiceAsync(Harness h)
    {
        var cases = await h.Runner.OnOrderWebhookAsync(new OrderWebhookInput(
            "test", "SO-1001", "planner",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [SeededErp.Battery] = ["V-A", "V-B"],
                [SeededErp.Screw] = ["V-C"]
            }));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        await h.Runner.AdvanceAsync(caseId);
        var raised = CaseStore.Load(h.CaseDir, caseId)!;
        var pr = raised.Decisions.Single(d => d.Kind == DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(caseId, pr.DecisionId, true, pr.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(caseId);
        var ordered = CaseStore.Load(h.CaseDir, caseId)!;
        var poApproval = ordered.Decisions.Single(d => d.Kind == DecisionKind.PoApproval);
        await h.Runner.ResolveDecisionAsync(caseId, poApproval.DecisionId, true, poApproval.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(caseId);
        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(caseId));
        await h.Runner.AdvanceAsync(caseId);
        var poNumber = CaseStore.Load(h.CaseDir, caseId)!.Case.PoNumber!;
        var line = (await h.Erp.GetPurchaseOrderAsync(poNumber))!.Lines
            .Single(l => l.ItemId == SeededErp.Battery);
        await h.Runner.OnAsnAsync(new SupplierAsnInput(caseId, "ASN-P1", "PACK-P1",
            [new AsnCartonInput("CTN-P1", SeededErp.Battery, (int)line.QtyOrdered, "LOT-P1")]));
        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-P1", "V-B", poNumber,
            [new SupplierInvoiceLineInput(line.PoLineRef, line.QtyOrdered, line.UnitPrice)]));
        var invoiced = CaseStore.Load(h.CaseDir, caseId)!;
        var approval = invoiced.Decisions.Single(d => d.Kind == DecisionKind.InvoiceApproval);
        await h.Runner.ResolveDecisionAsync(caseId, approval.DecisionId, true, approval.RequestedTo, "ok");
        return caseId;
    }

    [Fact]
    public async Task RunDateThreadsToTheDraftAndNullMeansNoLimit()
    {
        var h = Create();
        var caseId = await SetupPostedInvoiceAsync(h);
        var keys = CaseStore.Load(h.CaseDir, caseId)!.Case.Invoices;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var excluded = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Core.RequestPaymentAsync(keys, RecordSource.P2pAgent, payUpToDate: today.AddDays(-1)));
        Assert.Contains("Nothing due", excluded.Message, StringComparison.Ordinal);

        var (dated, _) = await h.Core.RequestPaymentAsync(
            keys, RecordSource.P2pAgent, payUpToDate: today.AddDays(1));
        Assert.NotNull(dated);

        var (undated, _) = await h.Core.RequestPaymentAsync(keys, RecordSource.P2pAgent);
        Assert.NotNull(undated);
        Assert.Equal(
            dated.Lines.Sum(l => l.Amount),
            undated.Lines.Sum(l => l.Amount));
    }

    [Fact]
    public async Task AdvanceWithAnEarlyRunDateParksInAwaitDueDate()
    {
        var h = Create();
        var caseId = await SetupPostedInvoiceAsync(h);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var outcome = await h.Runner.AdvanceAsync(caseId, payUpToDate: today.AddDays(-1));

        Assert.Equal(CaseNextStep.AwaitDueDate, outcome.Step);
        var file = CaseStore.Load(h.CaseDir, caseId)!;
        Assert.True(CasePicker.HasPaymentNotDueMarker(file, today.AddDays(-1)));
        Assert.Contains(file.Events, e =>
            (e.Summary ?? string.Empty).StartsWith(CasePicker.PaymentNotDueMarker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaterRunDateReattemptsAndPaysAfterEarlyRefusal()
    {
        var h = Create();
        var caseId = await SetupPostedInvoiceAsync(h);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var parked = await h.Runner.AdvanceAsync(caseId, payUpToDate: today.AddDays(-1));
        Assert.Equal(CaseNextStep.AwaitDueDate, parked.Step);

        var drafted = await h.Runner.AdvanceAsync(caseId, payUpToDate: today.AddDays(1));
        Assert.Equal(CaseNextStep.AwaitDecision, drafted.Step);
        var pay = CaseStore.Load(h.CaseDir, caseId)!.Decisions
            .Single(d => d.Kind == DecisionKind.PaymentApproval && d.Status == DecisionStatus.Pending);
        await h.Runner.ResolveDecisionAsync(caseId, pay.DecisionId, true, pay.RequestedTo, "ok");

        var done = await h.Runner.AdvanceAsync(caseId);
        Assert.True(done.Complete);
        Assert.Equal(CaseStage.Resolved, CaseStore.Load(h.CaseDir, caseId)!.Case.Stage);
        var invoice = (await h.Erp.GetInvoiceAsync("V-B", "INV-P1"))!;
        var accepted = invoice.Lines
            .Where(l => l.Disposition == LineDisposition.Accepted)
            .Sum(l => l.Quantity * l.UnitPrice);
        var settlements = await h.Erp.GetSettlementsAsync("V-B", "INV-P1");
        Assert.Equal(accepted, settlements.Sum(s => s.SettledAmount));
    }

    [Fact]
    public async Task UndatedRunPaysAfterDatedRefusal()
    {
        var h = Create();
        var caseId = await SetupPostedInvoiceAsync(h);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var parked = await h.Runner.AdvanceAsync(caseId, payUpToDate: today.AddDays(-1));
        Assert.Equal(CaseNextStep.AwaitDueDate, parked.Step);

        var drafted = await h.Runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitDecision, drafted.Step);
        Assert.Contains(CaseStore.Load(h.CaseDir, caseId)!.Decisions, d =>
            d.Kind == DecisionKind.PaymentApproval && d.Status == DecisionStatus.Pending);
    }

    [Fact]
    public async Task SameOrEarlierRunDateStaysParked()
    {
        var h = Create();
        var caseId = await SetupPostedInvoiceAsync(h);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var early = today.AddDays(-1);

        var parked = await h.Runner.AdvanceAsync(caseId, payUpToDate: early);
        Assert.Equal(CaseNextStep.AwaitDueDate, parked.Step);

        Assert.Equal(CaseNextStep.AwaitDueDate, (await h.Runner.AdvanceAsync(caseId, payUpToDate: early)).Step);
        Assert.Equal(CaseNextStep.AwaitDueDate, (await h.Runner.AdvanceAsync(caseId, payUpToDate: early.AddDays(-1))).Step);
    }
}

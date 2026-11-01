// Runner tests on the seeded fake ERP (NET0 terms, own small data): webhook
// intake, the full battery run to Resolved with partial pay plus reshot
// remainder, the straight-through screw run, and every failure path.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class CaseRunnerTests
{
    private static string TempDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    private sealed record Harness(
        InMemoryErpAdapter Erp, CaseRunner Runner, RecordingComplianceEngine Compliance,
        FakeModelClient Models, string CaseDir);

    private static Harness Create(IModelClient? models = null)
    {
        var erp = SeededErp.Create();
        var inner = models ?? new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var core = new P2pAgent(erp, inner, compliance);
        var dir = TempDir("gate-runner-test");
        var runner = new CaseRunner(erp, core, inner, compliance, new DockEvaluatorAdapter(), dir);
        return new Harness(erp, runner, compliance, inner as FakeModelClient ?? new FakeModelClient(), dir);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-A", "V-B"],
            [SeededErp.Screw] = ["V-C"]
        };

    private static Decision PendingDecision(CaseFile file, DecisionKind kind) =>
        file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

    private static CaseFile Load(Harness h, string caseId) =>
        CaseStore.Load(h.CaseDir, caseId) ?? throw new InvalidOperationException($"Case {caseId} vanished.");

    [Fact]
    public async Task WebhookStartsOneCasePerShortfallItemAndReplaysThem()
    {
        var h = Create();
        var input = new OrderWebhookInput("test", "SO-1001", "planner", Candidates());
        var first = await h.Runner.OnOrderWebhookAsync(input);
        Assert.Equal(2, first.Count);
        Assert.Contains(CaseStore.MintCaseId("test", "SO-1001", SeededErp.Battery), first);
        Assert.Contains(CaseStore.MintCaseId("test", "SO-1001", SeededErp.Screw), first);

        var second = await h.Runner.OnOrderWebhookAsync(input);
        Assert.Equal(first.Order().ToList(), second.Order().ToList());
        Assert.Equal(2, CaseStore.ListCaseIds(h.CaseDir).Count);
    }

    [Fact]
    public async Task WebhookOnUnknownSoThrows()
    {
        var h = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.OnOrderWebhookAsync(new OrderWebhookInput("test", "SO-404", "planner", Candidates())));
    }

    [Fact]
    public async Task WebhookWithNoShortfallStartsNoCases()
    {
        var erp = SeededErp.Create();
        erp.SeedStock(SeededErp.Battery, 1000);
        erp.SeedStock(SeededErp.Screw, 1000);
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), TempDir("gate-runner-test"));

        var cases = await runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        Assert.Empty(cases);
    }

    [Fact]
    public async Task MissingCandidatesSurfaceAsAGapError()
    {
        var h = Create();
        var cases = await h.Runner.OnOrderWebhookAsync(new OrderWebhookInput("test", "SO-1001", "planner"));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        await h.Runner.AdvanceAsync(battery);
        var file = Load(h, battery);
        await h.Runner.ResolveDecisionAsync(
            battery, PendingDecision(file, DecisionKind.PrApproval).DecisionId, true, "delegate", "ok");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.AdvanceAsync(battery));
        Assert.Contains("No candidate vendors", ex.Message);
    }

    [Fact]
    public async Task NoCompliantVendorRefusesWithANote()
    {
        var h = Create();
        var onlyBlocked = new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-A"],
            [SeededErp.Screw] = ["V-C"]
        };
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", onlyBlocked));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        await h.Runner.AdvanceAsync(battery);
        var file = Load(h, battery);
        await h.Runner.ResolveDecisionAsync(
            battery, PendingDecision(file, DecisionKind.PrApproval).DecisionId, true, "delegate", "ok");

        // CORP-02 block parks for the exception card; denying it keeps the
        // rejection, and the retried award then refuses with a note.
        var parked = await h.Runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        var screened = Load(h, battery);
        var card = PendingDecision(screened, DecisionKind.ExceptionApproval);
        Assert.Equal(ComplianceEngine.CorpOriginCapId, card.RuleId);
        await h.Runner.ResolveDecisionAsync(battery, card.DecisionId, false, card.RequestedTo, "no");
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.AdvanceAsync(battery));

        var failed = Load(h, battery);
        Assert.Contains(failed.Events,
            e => e.Kind == CaseEventKind.Note && e.Summary.Contains("No compliant vendor"));
        var vendor = (await h.Erp.GetVendorsAsync()).Single(v => v.VendorId == "V-A");
        Assert.True(vendor.IsBlocked);
        Assert.Contains(ComplianceEngine.CorpOriginCapId, vendor.BlockReason);
    }

    [Fact]
    public async Task PrRejectionCancelsTheCase()
    {
        var h = Create();
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        await h.Runner.AdvanceAsync(battery);
        var file = Load(h, battery);
        await h.Runner.ResolveDecisionAsync(
            battery, PendingDecision(file, DecisionKind.PrApproval).DecisionId, false, "delegate", "no budget");

        var cancelled = Load(h, battery);
        Assert.Equal(CaseStage.Cancelled, cancelled.Case.Stage);
        Assert.Contains(cancelled.Events, e => e.Kind == CaseEventKind.PrRejected);
        var outcome = await h.Runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.Done, outcome.Step);
        Assert.True(outcome.Complete);
    }

    [Fact]
    public async Task WrongApproverIsRefusedAndStaysPending()
    {
        var h = Create();
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        await h.Runner.AdvanceAsync(battery);
        var file = Load(h, battery);
        var decision = PendingDecision(file, DecisionKind.PrApproval);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Runner.ResolveDecisionAsync(battery, decision.DecisionId, true, "planner", "self"));

        var still = Load(h, battery);
        Assert.Equal(DecisionStatus.Pending,
            still.Decisions.Single(d => d.DecisionId == decision.DecisionId).Status);
    }

    [Fact]
    public async Task ResolvingADecidedDecisionReturnsItUnchanged()
    {
        var h = Create();
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        await h.Runner.AdvanceAsync(battery);
        var file = Load(h, battery);
        var decision = PendingDecision(file, DecisionKind.PrApproval);
        var first = await h.Runner.ResolveDecisionAsync(battery, decision.DecisionId, true, "delegate", "ok");
        var second = await h.Runner.ResolveDecisionAsync(battery, decision.DecisionId, true, "delegate", "ok");
        Assert.Equal(first.DecisionId, second.DecisionId);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.DecidedBy, second.DecidedBy);
        Assert.Equal(first.DecidedAt, second.DecidedAt);
        Assert.Equal(first.Comment, second.Comment);
        Assert.Equal(first.EvidenceRefs, second.EvidenceRefs);
        Assert.Equal(DecisionStatus.Approved, second.Status);
    }

    private sealed record BatteryContext(
        Harness Harness, string CaseId, PurchaseOrder Po, string PoLineRef, decimal UnitPrice);

    private static DockCartonRead Read(
        string carton, string image, bool mark, bool damage,
        string poNumber, string lot, int units, string part, bool class9Required = true) =>
        new(carton, part, poNumber, lot, units, mark, mark, "UN3480",
            class9Required, class9Required,
            damage, damage ? "crush outer" : null, image, 0.95m);

    private static async Task<BatteryContext> SetupBatteryCaseAsync(
        Harness h, IReadOnlyList<AsnCartonInput> cartons, string asnNumber = "ASN-T1")
    {
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        await h.Runner.AdvanceAsync(battery);
        var raised = Load(h, battery);
        var prDecision = PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(battery, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(battery);
        var ordered = Load(h, battery);
        var poDecision = PendingDecision(ordered, DecisionKind.PoApproval);
        await h.Runner.ResolveDecisionAsync(battery, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(battery);
        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(battery));
        await h.Runner.AdvanceAsync(battery);
        await h.Runner.OnAsnAsync(new SupplierAsnInput(battery, asnNumber, "PACK-T1", cartons));
        var cased = Load(h, battery);
        var po = await h.Erp.GetPurchaseOrderAsync(cased.Case.PoNumber!);
        var line = po!.Lines.Single(l => l.ItemId == SeededErp.Battery);
        return new BatteryContext(h, battery, po, line.PoLineRef, line.UnitPrice);
    }

    [Fact]
    public async Task DockReadsWithoutPhotoProvenanceAreRejected()
    {
        var h = Create();
        var ctx = await SetupBatteryCaseAsync(h, [
            new AsnCartonInput("CTN-T1", SeededErp.Battery, 80, "LOT-T1")]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.OnDockReadsAsync(ctx.CaseId, [
            Read("CTN-T1", "", mark: true, damage: false, ctx.Po.PoNumber, "LOT-T1", 80, SeededErp.Battery)]));
        Assert.Contains("names no photo", ex.Message);
        Assert.DoesNotContain(Load(h, ctx.CaseId).Events, e => e.Kind == CaseEventKind.DockEvaluated);
    }

    [Fact]
    public async Task ReshotWithoutPhotoProvenanceIsRejected()
    {
        var h = Create();
        var ctx = await SetupBatteryCaseAsync(h, [
            new AsnCartonInput("CTN-T1", SeededErp.Battery, 80, "LOT-T1")]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.OnReshotAsync(
            new ReshotInput(ctx.CaseId, ["CTN-T1"], [
                Read("CTN-T1", "  ", mark: true, damage: false, ctx.Po.PoNumber, "LOT-T1", 80, SeededErp.Battery)])));
        Assert.Contains("names no photo", ex.Message);
    }

    [Fact]
    public async Task FullBatteryRunPartialPaysThenResolvesOnReshot()
    {
        var h = Create();
        var ctx = await SetupBatteryCaseAsync(h, [
            new AsnCartonInput("CTN-T1", SeededErp.Battery, 80, "LOT-T1"),
            new AsnCartonInput("CTN-T2", SeededErp.Battery, 10, "LOT-T2")]);

        var blocked = Load(h, ctx.CaseId).Events.Single(e => e.Kind == CaseEventKind.VendorBlocked);
        Assert.Equal(ComplianceEngine.CorpOriginCapId, blocked.RuleId);
        Assert.Equal("V-B", ctx.Po.VendorId);

        await h.Runner.AdvanceAsync(ctx.CaseId);
        var verdicts = await h.Runner.OnDockReadsAsync(ctx.CaseId, [
            Read("CTN-T1", "IMG-T1", mark: true, damage: false, ctx.Po.PoNumber, "LOT-T1", 80, SeededErp.Battery),
            Read("CTN-T2", "IMG-T1", mark: false, damage: false, ctx.Po.PoNumber, "LOT-T2", 10, SeededErp.Battery),
            Read("CTN-T2", "IMG-T2", mark: false, damage: false, ctx.Po.PoNumber, "LOT-T2", 10, SeededErp.Battery)]);
        Assert.All(verdicts["CTN-T1"], v => Assert.Equal(VerdictKind.Pass, v.Kind));
        Assert.Contains(verdicts["CTN-T2"],
            v => v.Kind == VerdictKind.Hold && v.RuleId == DockEvaluator.MarkRuleId);

        await h.Runner.AdvanceAsync(ctx.CaseId);
        var intake = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            ctx.CaseId, "INV-T1", "V-B", ctx.Po.PoNumber,
            [new SupplierInvoiceLineInput(ctx.PoLineRef, 90, ctx.UnitPrice)]));
        Assert.True(intake.Persisted);
        Assert.Equal(80 * 12m, intake.AcceptedValue);
        Assert.Equal(10 * 12m, intake.HeldValue);

        var invoiced = Load(h, ctx.CaseId);
        var invDecision = PendingDecision(invoiced, DecisionKind.InvoiceApproval);
        Assert.Equal("Invoice", invDecision.DocumentType);
        Assert.Equal("V-B/INV-T1", invDecision.DocumentNumber);
        await h.Runner.ResolveDecisionAsync(ctx.CaseId, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");

        await h.Runner.AdvanceAsync(ctx.CaseId);
        var drafted = Load(h, ctx.CaseId);
        Assert.Equal(CaseStage.PaymentPartial, drafted.Case.Stage);
        var pay1 = PendingDecision(drafted, DecisionKind.PaymentApproval);
        Assert.Equal("finance", pay1.RequestedTo);
        Assert.Equal(80 * 12m, pay1.Amount);
        Assert.Equal("Payment", pay1.DocumentType);
        Assert.NotNull(pay1.DocumentNumber);
        await h.Runner.ResolveDecisionAsync(ctx.CaseId, pay1.DecisionId, true, "finance", "ok");

        var parked = await h.Runner.AdvanceAsync(ctx.CaseId);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);

        var reshot = await h.Runner.OnReshotAsync(new ReshotInput(ctx.CaseId, ["CTN-T2"],
            [Read("CTN-T2", "IMG-T4", mark: true, damage: false, ctx.Po.PoNumber, "LOT-T2", 10, SeededErp.Battery)]));
        Assert.All(reshot["CTN-T2"], v => Assert.Equal(VerdictKind.Pass, v.Kind));

        await h.Runner.AdvanceAsync(ctx.CaseId);
        var redrafted = Load(h, ctx.CaseId);
        var pay2 = PendingDecision(redrafted, DecisionKind.PaymentApproval);
        Assert.NotEqual(pay1.DecisionId, pay2.DecisionId);
        Assert.Equal(10 * 12m, pay2.Amount);
        await h.Runner.ResolveDecisionAsync(ctx.CaseId, pay2.DecisionId, true, pay2.RequestedTo, "ok");

        var done = await h.Runner.AdvanceAsync(ctx.CaseId);
        Assert.Equal(CaseNextStep.Done, done.Step);
        Assert.True(done.Complete);
        var final = Load(h, ctx.CaseId);
        Assert.Equal(CaseStage.Resolved, final.Case.Stage);

        Assert.Equal(2, final.Case.PaymentNumbers.Count);
        var posted1 = await h.Erp.GetPaymentAsync(final.Case.PaymentNumbers[0]);
        var posted2 = await h.Erp.GetPaymentAsync(final.Case.PaymentNumbers[1]);
        Assert.Equal(80 * 12m, posted1!.Lines.Single().Amount);
        Assert.Equal(10 * 12m, posted2!.Lines.Single().Amount);
        var settlements = await h.Erp.GetSettlementsAsync("V-B", "INV-T1");
        Assert.Equal(90 * 12m, settlements.Sum(s => s.SettledAmount));
        Assert.Equal(InvoiceStatus.Paid, (await h.Erp.GetInvoiceAsync("V-B", "INV-T1"))!.Status);

        Assert.Single(final.Events, e => e.Kind == CaseEventKind.CustomerNotified);

        Assert.Equal(2, h.Compliance.PrePayStates.Count);
        var firstGate = h.Compliance.PrePayStates[0];
        Assert.Single(firstGate.OpenHolds);
        Assert.Equal("BAT-LI-03", firstGate.OpenHolds[0].RuleId);
        Assert.Equal("CTN-T2", firstGate.OpenHolds[0].CartonId);
        Assert.Equal(["LOT-T2"], firstGate.QuarantinedLotRefs);
        Assert.Empty(firstGate.OpenReviews);
        Assert.Empty(h.Compliance.PrePayStates[1].OpenHolds);
        Assert.Empty(h.Compliance.PrePayStates[1].QuarantinedLotRefs);

        Assert.Equal(RecordSource.P2pAgent, (await h.Erp.GetPrAsync(final.Case.PrNumber!))!.Source);
        Assert.Equal(RecordSource.P2pAgent, (await h.Erp.GetPurchaseOrderAsync(final.Case.PoNumber!))!.Source);
        Assert.Equal(RecordSource.P2pAgent, (await h.Erp.GetReceiptAsync(final.Case.ReceiptNumber!))!.Source);
        Assert.Equal(RecordSource.P2pAgent, (await h.Erp.GetInvoiceAsync("V-B", "INV-T1"))!.Source);
        Assert.Equal(RecordSource.P2pAgent, posted1.Source);
    }

    [Fact]
    public async Task ScrewCaseRunsStraightThroughWithOnlyMoneyApprovals()
    {
        var h = Create();
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var screw = cases.Single(c => c.EndsWith(SeededErp.Screw, StringComparison.Ordinal));

        var outcome = await h.Runner.AdvanceAsync(screw);
        var ordered = Load(h, screw);
        Assert.DoesNotContain(ordered.Decisions,
            d => d.Kind is DecisionKind.PrApproval or DecisionKind.PoApproval);
        Assert.NotNull(ordered.Case.PoNumber);
        Assert.Equal("V-C", (await h.Erp.GetPurchaseOrderAsync(ordered.Case.PoNumber))!.VendorId);
        Assert.Equal(CaseNextStep.AwaitSupplierAck, outcome.Step);

        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(screw));
        await h.Runner.AdvanceAsync(screw);
        await h.Runner.OnAsnAsync(new SupplierAsnInput(screw, "ASN-S1", "PACK-S1",
            [new AsnCartonInput("CTN-S1", SeededErp.Screw, 150, "LOT-S1")]));
        await h.Runner.AdvanceAsync(screw);
        var screwPo = Load(h, screw).Case.PoNumber!;
        var verdicts = await h.Runner.OnDockReadsAsync(screw,
            [Read("CTN-S1", "IMG-S1", mark: true, damage: false, screwPo, "LOT-S1", 150, SeededErp.Screw,
                class9Required: false)]);
        Assert.All(verdicts["CTN-S1"], v => Assert.Equal(VerdictKind.Pass, v.Kind));

        await h.Runner.AdvanceAsync(screw);
        var poLine = (await h.Erp.GetPurchaseOrderAsync(screwPo))!.Lines.Single();
        var intake = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            screw, "INV-S1", "V-C", screwPo,
            [new SupplierInvoiceLineInput(poLine.PoLineRef, 150, poLine.UnitPrice)]));
        Assert.Equal(150 * 1m, intake.AcceptedValue);
        Assert.Equal(0, intake.HeldValue);

        var invoiced = Load(h, screw);
        var invDecision = PendingDecision(invoiced, DecisionKind.InvoiceApproval);
        await h.Runner.ResolveDecisionAsync(screw, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(screw);
        var drafted = Load(h, screw);
        Assert.Equal(CaseStage.PaymentRequested, drafted.Case.Stage);
        var pay = PendingDecision(drafted, DecisionKind.PaymentApproval);
        await h.Runner.ResolveDecisionAsync(screw, pay.DecisionId, true, pay.RequestedTo, "ok");

        var done = await h.Runner.AdvanceAsync(screw);
        Assert.Equal(CaseStage.Resolved, Load(h, screw).Case.Stage);
        Assert.True(done.Complete);
    }

    private static string Idem() => Guid.NewGuid().ToString("N");

    private static async Task<PurchaseOrder> CreateReceivedSmallPoAsync(
        InMemoryErpAdapter erp, string vendor, string item, decimal qty, string lot)
    {
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine(item, qty, "EA")], Idem());
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Idem());
        }

        var po = await erp.CreatePoAsync(pr.PrNumber, vendor, RecordSource.P2pAgent, "planner", Idem());
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PO", po.PoNumber, "delegate", "planner", Idem());
            await erp.RecordApprovalAsync("PO", po.PoNumber, "delegate", true, "ok", Idem());
        }

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        await erp.RecordVendorConfirmationAsync(po.PoNumber, today, RecordSource.Vendor, vendor,
            po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList(), Idem());
        await erp.PostReceiptAsync(po.PoNumber, "PACK-SMALL", RecordSource.P2pAgent, "planner",
            [new ReceiptLine(po.Lines.Single().PoLineRef, qty, 0, 0, lot, null)], Idem());
        return (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
    }

    private static async Task<string> OpenCaseAsync(Harness h)
    {
        h.Erp.SeedUser(new UserDto("clerk", "ap-clerk", 0, "USD"));
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        return cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateInvoiceIsHeldWithAnApReview()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h);
        var small = await CreateReceivedSmallPoAsync(h.Erp, "V-B", SeededErp.Battery, 50, "LOT-DUP");
        var line = small.Lines.Single();

        var first = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-DUP", "V-B", small.PoNumber,
            [new SupplierInvoiceLineInput(line.PoLineRef, 50, line.UnitPrice)]));
        Assert.True(first.Persisted);
        Assert.Null(first.DecisionId);

        var second = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-DUP", "V-B", small.PoNumber,
            [new SupplierInvoiceLineInput(line.PoLineRef, 50, line.UnitPrice)]));
        Assert.False(second.Persisted);
        Assert.NotNull(second.DecisionId);
        var file = Load(h, caseId);
        var decision = file.Decisions.Single(d => d.DecisionId == second.DecisionId);
        Assert.Equal(DecisionKind.ApReview, decision.Kind);
        Assert.Equal("clerk", decision.RequestedTo);
        Assert.Contains(file.Events, e =>
            e.Kind == CaseEventKind.Note && e.Summary.Contains("AP-C1"));
    }

    [Fact]
    public async Task MissingFieldsInvoiceIsHeldWithAnApReview()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h);

        var outcome = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BLANK", null, null,
            [new SupplierInvoiceLineInput(null, 5, 10)]));
        Assert.False(outcome.Persisted);
        Assert.Equal(50m, outcome.HeldValue);
        var file = Load(h, caseId);
        var decision = file.Decisions.Single(d => d.DecisionId == outcome.DecisionId);
        Assert.Equal(DecisionKind.ApReview, decision.Kind);
        Assert.Equal("clerk", decision.RequestedTo);
        Assert.Equal("Invoice", decision.DocumentType);
    }

    [Fact]
    public async Task BlankInvoiceNumberWithValidVendorAndPoIsHeldWithAnApReview()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h);
        var small = await CreateReceivedSmallPoAsync(h.Erp, "V-B", SeededErp.Battery, 50, "LOT-BLANKNUM");
        var line = small.Lines.Single();

        var outcome = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "   ", "V-B", small.PoNumber,
            [new SupplierInvoiceLineInput(line.PoLineRef, 50, line.UnitPrice)]));
        Assert.False(outcome.Persisted);
        Assert.Equal(50 * line.UnitPrice, outcome.HeldValue);
        Assert.Equal(0, outcome.AcceptedValue);
        var file = Load(h, caseId);
        var decision = file.Decisions.Single(d => d.DecisionId == outcome.DecisionId);
        Assert.Equal(DecisionKind.ApReview, decision.Kind);
        Assert.Equal("clerk", decision.RequestedTo);
        Assert.Contains(file.Events, e =>
            e.Kind == CaseEventKind.Note && e.Summary.Contains("AP-C3", StringComparison.Ordinal));
        Assert.Null(await h.Erp.GetInvoiceAsync("V-B", "   "));
        Assert.Null(await h.Erp.GetInvoiceAsync("V-B", string.Empty));
    }

    [Fact]
    public async Task BankChangeIsHeldThenReleasedOnVerification()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h);
        var small = await CreateReceivedSmallPoAsync(h.Erp, "V-B", SeededErp.Battery, 40, "LOT-BANK");
        var line = small.Lines.Single();

        var outcome = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-BANK", "V-B", small.PoNumber,
            [new SupplierInvoiceLineInput(line.PoLineRef, 40, line.UnitPrice)],
            BankAccountRef: "BANK-B-NEW"));
        Assert.True(outcome.Persisted);
        Assert.Equal(0, outcome.AcceptedValue);
        Assert.Equal(40 * 12m, outcome.HeldValue);

        var file = Load(h, caseId);
        var decision = file.Decisions.Single(d => d.DecisionId == outcome.DecisionId);
        Assert.Equal(DecisionKind.BankVerification, decision.Kind);
        Assert.Equal("VendorBank", decision.DocumentType);
        Assert.Equal("V-B", decision.DocumentNumber);
        Assert.False((await h.Erp.GetVendorBankAsync("V-B"))!.Verified);

        await h.Runner.ResolveDecisionAsync(caseId, decision.DecisionId, true, "clerk", "called the bank");
        Assert.True((await h.Erp.GetVendorBankAsync("V-B"))!.Verified);
        var released = await h.Erp.GetInvoiceAsync("V-B", "INV-BANK");
        Assert.All(released!.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
    }

    [Fact]
    public async Task PriceBreachIsHeldWithAnApReview()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h);
        var small = await CreateReceivedSmallPoAsync(h.Erp, "V-C", SeededErp.Screw, 200, "LOT-PRICE");
        var line = small.Lines.Single();

        var outcome = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-PRICE", "V-C", small.PoNumber,
            [new SupplierInvoiceLineInput(line.PoLineRef, 200, line.UnitPrice * 1.10m)]));
        Assert.True(outcome.Persisted);
        Assert.Equal(0, outcome.AcceptedValue);
        var stored = await h.Erp.GetInvoiceAsync("V-C", "INV-PRICE");
        Assert.All(stored!.Lines, l => Assert.StartsWith("AP-C2", l.HoldReason));
        var file = Load(h, caseId);
        Assert.Single(file.Decisions, d => d.Kind == DecisionKind.ApReview);
    }

    [Fact]
    public async Task OverReceivedQuantityIsHeld()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h);
        var small = await CreateReceivedSmallPoAsync(h.Erp, "V-C", SeededErp.Screw, 200, "LOT-QTY");

        var outcome = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-QTY", "V-C", small.PoNumber,
            [new SupplierInvoiceLineInput(small.Lines.Single().PoLineRef, 250, 1m)]));
        Assert.True(outcome.Persisted);
        Assert.Equal(0, outcome.AcceptedValue);
        Assert.Equal(250 * 1m, outcome.HeldValue);
        var stored = await h.Erp.GetInvoiceAsync("V-C", "INV-QTY");
        Assert.Contains(stored!.Lines, l => (l.HoldReason ?? string.Empty).Contains("exceeds received"));
    }

    [Fact]
    public async Task PendingDamageReviewReachesThePrePayGateAsAnOpenReview()
    {
        var h = Create();
        var ctx = await SetupBatteryCaseAsync(h, [
            new AsnCartonInput("CTN-D1", SeededErp.Battery, 90, "LOT-D1")]);
        await h.Runner.AdvanceAsync(ctx.CaseId);
        await h.Runner.OnDockReadsAsync(ctx.CaseId,
            [Read("CTN-D1", "IMG-D1", mark: true, damage: true, ctx.Po.PoNumber, "LOT-D1", 90, SeededErp.Battery)]);
        var reviewed = Load(h, ctx.CaseId);
        var damage = Assert.Single(reviewed.Decisions, d => d.Kind == DecisionKind.DamageReview);
        Assert.Equal(DockEvaluator.DamageRuleId, damage.RuleId);
        Assert.Contains(damage.EvidenceRefs, r => r == "carton:CTN-D1");

        await h.Runner.AdvanceAsync(ctx.CaseId);
        await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            ctx.CaseId, "INV-D1", "V-B", ctx.Po.PoNumber,
            [new SupplierInvoiceLineInput(ctx.PoLineRef, 90, ctx.UnitPrice)]));
        var invoiced = Load(h, ctx.CaseId);
        var invDecision = PendingDecision(invoiced, DecisionKind.InvoiceApproval);
        await h.Runner.ResolveDecisionAsync(ctx.CaseId, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");

        await h.Runner.AdvanceAsync(ctx.CaseId);
        var state = Assert.Single(h.Compliance.PrePayStates);
        var open = Assert.Single(state.OpenReviews);
        Assert.Equal(damage.DecisionId, open.ReviewId);
        Assert.Equal(DockEvaluator.DamageRuleId, open.RuleId);
        Assert.Equal("CTN-D1", open.CartonId);
        Assert.Equal("LOT-D1", open.LotRef);
    }

    [Fact]
    public async Task DamageRejectionQuarantinesTheLot()
    {
        var h = Create();
        var ctx = await SetupBatteryCaseAsync(h, [
            new AsnCartonInput("CTN-D1", SeededErp.Battery, 90, "LOT-D1")]);
        await h.Runner.AdvanceAsync(ctx.CaseId);
        await h.Runner.OnDockReadsAsync(ctx.CaseId,
            [Read("CTN-D1", "IMG-D1", mark: true, damage: true, ctx.Po.PoNumber, "LOT-D1", 90, SeededErp.Battery)]);
        var reviewed = Load(h, ctx.CaseId);
        var damage = Assert.Single(reviewed.Decisions, d => d.Kind == DecisionKind.DamageReview);

        await h.Runner.ResolveDecisionAsync(ctx.CaseId, damage.DecisionId, false, damage.RequestedTo, "cells punctured");
        var rejected = Load(h, ctx.CaseId);
        Assert.Contains(rejected.Events, e => e.Kind == CaseEventKind.HoldSet);
        Assert.Contains(rejected.Events, e => e.Kind == CaseEventKind.ReviewClosed);
        var txns = await h.Erp.GetInventoryTransactionsAsync(SeededErp.Battery);
        Assert.Contains(txns, t => t.LotRef == "LOT-D1" && t.Status == InventoryStatus.Quarantined);
    }

    [Fact]
    public async Task ReviewApprovalReleasesTheCartonHold()
    {
        var h = Create();
        var ctx = await SetupBatteryCaseAsync(h, [
            new AsnCartonInput("CTN-D1", SeededErp.Battery, 90, "LOT-D1")]);
        await h.Runner.AdvanceAsync(ctx.CaseId);
        await h.Runner.OnDockReadsAsync(ctx.CaseId,
            [Read("CTN-D1", "IMG-D1", mark: false, damage: true, ctx.Po.PoNumber, "LOT-D1", 90, SeededErp.Battery)]);
        var reviewed = Load(h, ctx.CaseId);
        var damage = Assert.Single(reviewed.Decisions, d => d.Kind == DecisionKind.DamageReview);
        Assert.NotEmpty(await h.Erp.GetOpenHoldsAsync(null));

        await h.Runner.ResolveDecisionAsync(ctx.CaseId, damage.DecisionId, true, damage.RequestedTo, "mark confirmed");
        var cleared = Load(h, ctx.CaseId);
        Assert.Empty(await h.Erp.GetOpenHoldsAsync(null));
        Assert.Contains(cleared.Events, e =>
            e.Kind == CaseEventKind.HoldReleased && e.Summary.Contains("after review approval"));
        Assert.Contains(cleared.Events, e => e.Kind == CaseEventKind.ReviewClosed);
    }

    [Fact]
    public async Task FailingReshotKeepsTheHold()
    {
        var h = Create();
        var ctx = await SetupBatteryCaseAsync(h, [
            new AsnCartonInput("CTN-T2", SeededErp.Battery, 90, "LOT-T2")]);
        await h.Runner.AdvanceAsync(ctx.CaseId);
        await h.Runner.OnDockReadsAsync(ctx.CaseId,
            [Read("CTN-T2", "IMG-T1", mark: false, damage: false, ctx.Po.PoNumber, "LOT-T2", 90, SeededErp.Battery)]);

        await h.Runner.OnReshotAsync(new ReshotInput(ctx.CaseId, ["CTN-T2"],
            [Read("CTN-T2", "IMG-T4", mark: false, damage: false, ctx.Po.PoNumber, "LOT-T2", 90, SeededErp.Battery)]));
        var file = Load(h, ctx.CaseId);
        Assert.DoesNotContain(file.Events, e => e.Kind == CaseEventKind.HoldReleased);
        Assert.Contains(file.Events, e => e.Kind == CaseEventKind.Note && e.Summary.Contains("still failing"));
        Assert.NotEmpty(await h.Erp.GetOpenHoldsAsync(null));
    }

    [Fact]
    public async Task ReadCartonMissingFromTheAsnIsNeverSilentlyDropped()
    {
        var h = Create();
        var ctx = await SetupBatteryCaseAsync(h, [
            new AsnCartonInput("CTN-T1", SeededErp.Battery, 90, "LOT-T1")]);
        await h.Runner.AdvanceAsync(ctx.CaseId);
        var verdicts = await h.Runner.OnDockReadsAsync(ctx.CaseId, [
            Read("CTN-T1", "IMG-T1", mark: true, damage: false, ctx.Po.PoNumber, "LOT-T1", 90, SeededErp.Battery),
            Read("CTN-STRANGER", "IMG-T1", mark: false, damage: false, ctx.Po.PoNumber, "LOT-X", 10, SeededErp.Battery)]);
        Assert.True(verdicts.ContainsKey("CTN-STRANGER"));
        var file = Load(h, ctx.CaseId);
        Assert.Contains(file.Events, e => e.Summary.Contains("CTN-STRANGER"));
    }

    [Fact]
    public async Task ReplayFromCacheMakesZeroLiveCalls()
    {
        var erp = SeededErp.Create();
        var fake = new FakeModelClient();
        var modelDir = TempDir("gate-model-test");
        var cached = new CachedModelClient(fake, modelDir);
        var compliance = new RecordingComplianceEngine();
        var caseDir = TempDir("gate-runner-test");
        var runner = new CaseRunner(erp, new P2pAgent(erp, cached, compliance), cached,
            compliance, new DockEvaluatorAdapter(), caseDir);

        var cases = await runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        await runner.AdvanceAsync(battery);
        var raised = CaseStore.Load(caseDir, battery)!;
        var pr = PendingDecision(raised, DecisionKind.PrApproval);
        await runner.ResolveDecisionAsync(battery, pr.DecisionId, true, pr.RequestedTo, "ok");
        await runner.AdvanceAsync(battery);
        var why = await runner.ExplainDecisionAsync(battery,
            PendingDecision(CaseStore.Load(caseDir, battery)!, DecisionKind.PoApproval).DecisionId,
            "Why this approver?");
        Assert.NotEmpty(why);
        Assert.NotEmpty(Directory.GetFiles(modelDir));

        var throwing = new ThrowingModelClient();
        var replayCached = new CachedModelClient(throwing, modelDir);
        var replay = new CaseRunner(erp, new P2pAgent(erp, replayCached, compliance), replayCached,
            compliance, new DockEvaluatorAdapter(), caseDir);
        var replayed = await replay.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        Assert.Equal(2, replayed.Count);
        var parked = await replay.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        var whyAgain = await replay.ExplainDecisionAsync(battery,
            PendingDecision(CaseStore.Load(caseDir, battery)!, DecisionKind.PoApproval).DecisionId,
            "Why this approver?");
        Assert.Equal(why, whyAgain);
        Assert.Equal(0, throwing.CallCount);
    }

    [Fact]
    public async Task ResilientCallsRetryOnceThenThrow()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var flaky = new FlakyModelClient("recovered");
        var ok = await ResilientCalls.CompleteWithRetryAsync(
            flaky, new ModelRequest("hi"), logger, "flaky");
        Assert.Equal("recovered", ok.Text);
        Assert.Equal(2, flaky.CallCount);

        var throwing = new ThrowingModelClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ResilientCalls.CompleteWithRetryAsync(throwing, new ModelRequest("hi"), logger, "live"));
        Assert.Equal(2, throwing.CallCount);

        var empty = new EmptyModelClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ResilientCalls.CompleteWithRetryAsync(empty, new ModelRequest("hi"), logger, "empty"));
        Assert.Equal(2, empty.CallCount);
    }
}

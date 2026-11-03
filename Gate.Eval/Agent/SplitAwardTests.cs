// Split-award tests: when the cheapest vendor is blocked for the full
// quantity, Spark may propose a whole-carton split, code verifies the
// whole plan, and a person approves before anything is ordered. Each
// approved line runs as its own child case; the parent resolves when
// all children do, and the customer message waits for the parent.
// Rejecting, or switching splits off, runs today's single-vendor award.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Eval.VideoProbes;

namespace Gate.Eval.Agent;

public sealed class SplitAwardTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp, P2pAgent Core, ProbeModelClient Models, CaseRunner Runner, string CaseDir);

    private static IReadOnlyDictionary<string, string> CountryMap() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CN"] = "China",
            ["MX"] = "Mexico",
            ["US"] = "United States",
        };

    private static Harness Create(bool policy = false, SplitProposals? splits = null)
    {
        var erp = policy ? SeededErp.CreateWithPolicy() : SeededErp.Create();
        // (5,600 + 1,200) / (10,000 + 1,200) = 60.7%: CinderVolt at $11
        // blocks for the full 1,200 while Meridian at $12 passes, so the
        // 800/400 split saves $800 with China ending at 57.1%.
        erp.SeedVolumeHistory(SeededErp.Battery,
        [
            new VolumeHistory("V-A CN active", "CN", 5600),
            new VolumeHistory("V-B MX active", "MX", 4000),
            new VolumeHistory("Other non-CN", "OTHER", 400),
        ]);
        erp.SeedVendor(new VendorDto("V-D", "CinderVolt Second Plant Ltd", "CN", false, null, "NET0"));
        erp.SeedVendorPrice("V-D", SeededErp.Battery, 13m);
        erp.SeedVendorDocuments("V-D",
        [
            new VendorDocumentDto("V-D", "UN38.3", "un383-d", true),
            new VendorDocumentDto("V-D", "SDS", "sds-d", true),
            new VendorDocumentDto("V-D", "RoHS", "rohs-d", true),
            new VendorDocumentDto("V-D", "REACH", "reach-d", true),
        ]);
        erp.SeedSalesOrder(new SalesOrder("SO-SPLIT-1", SalesOrderStatus.Confirmed, "USD",
            [new SalesOrderLine(SeededErp.Glasses, 1210)]));
        var models = new ProbeModelClient();
        var compliance = new ComplianceEngine();
        var core = new P2pAgent(erp, models, compliance);
        var dir = Path.Combine(Path.GetTempPath(), "gate-split-test", Guid.NewGuid().ToString("N"));
        var runner = new CaseRunner(
            erp, core, models, compliance, new DockEvaluatorAdapter(), dir,
            countryNames: CountryMap(), splitProposals: splits);
        return new Harness(erp, core, models, runner, dir);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-A", "V-B"],
            [SeededErp.Screw] = ["V-C"]
        };

    private static CaseFile Load(Harness h, string caseId) =>
        CaseStore.Load(h.CaseDir, caseId) ?? throw new InvalidOperationException($"Case {caseId} vanished.");

    private static Decision PendingDecision(CaseFile file, DecisionKind kind) =>
        file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

    private static IReadOnlyList<CaseFile> ChildrenOf(Harness h, string parentId)
    {
        var children = new List<CaseFile>();
        foreach (var id in CaseStore.ListCaseIds(h.CaseDir))
        {
            var file = CaseStore.Load(h.CaseDir, id);
            if (file is not null
                && string.Equals(file.Case.ParentCaseId, parentId, StringComparison.Ordinal))
            {
                children.Add(file);
            }
        }

        return children;
    }

    private static string Args(string caseId) => $"{{\"case_id\":\"{caseId}\"}}";

    private static string SplitArgs(string caseId, string[] vendors, decimal[] quantities, string? note = null) =>
        $"{{\"case_id\":\"{caseId}\",\"vendor_ids\":[{string.Join(",", vendors.Select(v => $"\"{v}\""))}]," +
        $"\"quantities\":[{string.Join(",", quantities)}]" +
        (note is null ? "}" : $",\"note\":\"{note}\"}}");

    private static DockCartonRead Read(
        string carton, string image, string poNumber, string lot, int units, string part) =>
        new(carton, part, poNumber, lot, units, true, true, "UN3480", true, true,
            false, null, image, 0.95m);

    private static void QueueTexts(ProbeModelClient models, int count, string what)
    {
        for (var i = 0; i < count; i++)
        {
            models.QueueText(what);
        }
    }

    [Fact]
    public async Task ApproveSplitRunsChildrenAndResolvesParent()
    {
        var h = Create(policy: true);
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-SPLIT-1", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        var screw = cases.Single(c => c.EndsWith(SeededErp.Screw, StringComparison.Ordinal));

        QueueTexts(h.Models, 2, "shortfall then raise");
        await h.Runner.AdvanceAsync(battery);
        var raised = Load(h, battery);
        Assert.Equal(1200m, (await h.Erp.GetPrAsync(raised.Case.PrNumber!))!.Lines.Sum(l => l.Quantity));
        var prDecision = PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(battery, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");

        h.Models.QueueText("screen");
        h.Models.QueueToolCalls("split 800/400",
            ("p1", AgentTools.ProposeSplit, SplitArgs(battery, ["V-A", "V-B"], [800, 400], "keep dual supply for resilience")));
        var parked = await h.Runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        var proposed = Load(h, battery);
        var split = PendingDecision(proposed, DecisionKind.SplitApproval);
        Assert.Equal("delegate", split.RequestedTo);
        var plan = split.Split;
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Lines.Count);
        Assert.Equal("V-A", plan.Lines[0].VendorId);
        Assert.Equal(800m, plan.Lines[0].Quantity);
        Assert.Equal("V-B", plan.Lines[1].VendorId);
        Assert.Equal(400m, plan.Lines[1].Quantity);
        Assert.Equal(13600m, plan.SplitTotal);
        Assert.Equal(14400m, plan.SingleVendorTotal);
        Assert.Equal(800m, plan.Saving);
        Assert.Equal("USD", plan.Currency);
        var china = plan.FinalShares.Single(s => s.CountryCode == "CN");
        var mexico = plan.FinalShares.Single(s => s.CountryCode == "MX");
        Assert.Equal("China", china.Country);
        Assert.Equal(0.5714m, Math.Round(china.Share, 4));
        Assert.Equal(0.3929m, Math.Round(mexico.Share, 4));
        Assert.Contains("800", split.Reason, StringComparison.Ordinal);
        Assert.Contains("400", split.Reason, StringComparison.Ordinal);
        Assert.Contains("57.1%", split.Reason, StringComparison.Ordinal);
        Assert.Contains("39.3%", split.Reason, StringComparison.Ordinal);
        Assert.Contains("$800.00", split.Reason, StringComparison.Ordinal);
        Assert.Contains("Agent note: keep dual supply for resilience", split.Reason, StringComparison.Ordinal);

        // Nothing is ordered until the person approves.
        Assert.Null(proposed.Case.PoNumber);
        Assert.Null(proposed.Case.AwardedVendorId);
        var snapshot = proposed.ScreeningSnapshot;
        Assert.NotNull(snapshot);
        Assert.Null(snapshot.AwardedVendorId);
        Assert.All(snapshot.Rows, r => Assert.False(r.Awarded));
        Assert.Contains("60.7%",
            snapshot.Rows.Single(r => r.VendorId == "V-A").Reason, StringComparison.Ordinal);

        await h.Runner.ResolveDecisionAsync(battery, split.DecisionId, true, split.RequestedTo, "ok");
        h.Models.QueueToolCalls("create children",
            ("c1", AgentTools.CreateSplitChildren, Args(battery)));
        QueueTexts(h.Models, 2, "fan-out child submits");
        var ordered = await h.Runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitSplitChildren, ordered.Step);
        var parent = Load(h, battery);
        Assert.Equal(CaseStage.SplitOrdered, parent.Case.Stage);

        var children = ChildrenOf(h, battery);
        Assert.Equal(2, children.Count);
        var childA = children.Single(c => c.Case.AwardedVendorId == "V-A");
        var childB = children.Single(c => c.Case.AwardedVendorId == "V-B");
        Assert.NotNull(childA.Case.PoNumber);
        Assert.NotNull(childB.Case.PoNumber);
        Assert.True(
            string.Compare(childA.Case.PoNumber, childB.Case.PoNumber, StringComparison.Ordinal) < 0,
            "Split POs are created in plan order.");
        var poA = await h.Erp.GetPurchaseOrderAsync(childA.Case.PoNumber!);
        var poB = await h.Erp.GetPurchaseOrderAsync(childB.Case.PoNumber!);
        Assert.NotNull(poA);
        Assert.NotNull(poB);
        Assert.Equal(800m, Assert.Single(poA.Lines).QtyOrdered);
        Assert.Equal(400m, Assert.Single(poB.Lines).QtyOrdered);
        Assert.Equal(parent.Case.PrNumber, poA.PrNumber);
        Assert.Equal(parent.Case.PrNumber, poB.PrNumber);
        Assert.Equal(PrStatus.Closed, (await h.Erp.GetPrAsync(parent.Case.PrNumber!))?.Status);
        foreach (var child in children)
        {
            Assert.Equal(CaseStage.PoPlaced, child.Case.Stage);
            var childSnapshot = child.ScreeningSnapshot;
            Assert.NotNull(childSnapshot);
            Assert.Equal(snapshot.TakenAt, childSnapshot.TakenAt);
            Assert.True(childSnapshot.Rows.Single(r => r.VendorId == child.Case.AwardedVendorId).Awarded);
            Assert.Contains(child.Events, e =>
                e.Kind == CaseEventKind.SupplierAwarded && e.Summary.Contains("Split line awarded"));
            Assert.Contains(child.Events, e => e.Kind == CaseEventKind.VendorScreened);
        }

        // The screw sibling resolves first, but the customer message waits
        // for the split parent.
        await RunScrewAsync(h, screw);
        Assert.DoesNotContain(Load(h, screw).Events, e => e.Kind == CaseEventKind.CustomerNotified);

        await RunChildAsync(h, childA.Case.CaseId, "CTN-A1", "LOT-A1", "INV-A1", 800, 11m);
        var mid = Load(h, battery);
        Assert.Equal(CaseStage.SplitOrdered, mid.Case.Stage);
        Assert.DoesNotContain(mid.Events, e => e.Kind == CaseEventKind.CustomerNotified);

        await RunChildAsync(h, childB.Case.CaseId, "CTN-B1", "LOT-B1", "INV-B1", 400, 12m);
        var done = Load(h, battery);
        Assert.Equal(CaseStage.Resolved, done.Case.Stage);
        Assert.Contains(done.Events, e => e.Kind == CaseEventKind.CustomerNotified);
        Assert.DoesNotContain(
            Load(h, childA.Case.CaseId).Events, e => e.Kind == CaseEventKind.CustomerNotified);
        Assert.DoesNotContain(
            Load(h, childB.Case.CaseId).Events, e => e.Kind == CaseEventKind.CustomerNotified);
    }

    [Fact]
    public async Task ParentAdvanceSubmitsSplitChildren()
    {
        // No manual child advance anywhere in this test: the parent's
        // advance fans out to the children, each child submits its own
        // PO and raises its own approval card.
        var h = Create(policy: true);
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-SPLIT-1", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        QueueTexts(h.Models, 2, "shortfall then raise");
        await h.Runner.AdvanceAsync(battery);
        var raised = Load(h, battery);
        var prDecision = PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(battery, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");

        h.Models.QueueText("screen");
        h.Models.QueueToolCalls("split 800/400",
            ("p1", AgentTools.ProposeSplit, SplitArgs(battery, ["V-A", "V-B"], [800, 400], "keep dual supply for resilience")));
        var parked = await h.Runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        var proposed = Load(h, battery);
        var split = PendingDecision(proposed, DecisionKind.SplitApproval);

        await h.Runner.ResolveDecisionAsync(battery, split.DecisionId, true, split.RequestedTo, "ok");
        h.Models.QueueToolCalls("create children",
            ("c1", AgentTools.CreateSplitChildren, Args(battery)));
        QueueTexts(h.Models, 2, "fan-out child submits");
        var ordered = await h.Runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitSplitChildren, ordered.Step);

        var children = ChildrenOf(h, battery);
        Assert.Equal(2, children.Count);
        foreach (var child in children)
        {
            var card = PendingDecision(child, DecisionKind.PoApproval);
            Assert.NotNull(child.Case.PoNumber);
            Assert.Contains(child.Case.PoNumber, card.Title, StringComparison.Ordinal);
            var po = await h.Erp.GetPurchaseOrderAsync(child.Case.PoNumber!);
            Assert.NotNull(po);
            Assert.Equal(PoApprovalStatus.InReview, po.ApprovalStatus);
        }
    }

    [Fact]
    public async Task ReorderedSplitCreatesPosInPassingOrder()
    {
        // CinderVolt first would breach at the ERP's own check
        // (6,700/11,100 = 60.4%), but Meridian first passes, so the
        // verified plan reorders and the POs create Meridian first.
        var h = Create(policy: true);
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-SPLIT-1", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        QueueTexts(h.Models, 2, "shortfall then raise");
        await h.Runner.AdvanceAsync(battery);
        var raised = Load(h, battery);
        var prDecision = PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(battery, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");

        h.Models.QueueText("screen");
        h.Models.QueueToolCalls("split 1100/100",
            ("p1", AgentTools.ProposeSplit, SplitArgs(battery, ["V-A", "V-B"], [1100, 100], "keep dual supply for resilience")));
        var parked = await h.Runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        var proposed = Load(h, battery);
        var split = PendingDecision(proposed, DecisionKind.SplitApproval);
        var plan = split.Split;
        Assert.NotNull(plan);
        Assert.Equal("V-B", plan.Lines[0].VendorId);
        Assert.Equal(100m, plan.Lines[0].Quantity);
        Assert.Equal("V-A", plan.Lines[1].VendorId);
        Assert.Equal(1100m, plan.Lines[1].Quantity);
        Assert.Equal(0.5982m, Math.Round(plan.FinalShares.Single(s => s.CountryCode == "CN").Share, 4));

        await h.Runner.ResolveDecisionAsync(battery, split.DecisionId, true, split.RequestedTo, "ok");
        h.Models.QueueToolCalls("create children",
            ("c1", AgentTools.CreateSplitChildren, Args(battery)));
        QueueTexts(h.Models, 2, "fan-out child submits");
        var ordered = await h.Runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitSplitChildren, ordered.Step);

        var children = ChildrenOf(h, battery);
        Assert.Equal(2, children.Count);
        var childA = children.Single(c => c.Case.AwardedVendorId == "V-A");
        var childB = children.Single(c => c.Case.AwardedVendorId == "V-B");
        Assert.NotNull(childA.Case.PoNumber);
        Assert.NotNull(childB.Case.PoNumber);
        Assert.True(
            string.Compare(childB.Case.PoNumber, childA.Case.PoNumber, StringComparison.Ordinal) < 0,
            "Split POs are created in verified passing order (Meridian first).");
        var poA = await h.Erp.GetPurchaseOrderAsync(childA.Case.PoNumber!);
        var poB = await h.Erp.GetPurchaseOrderAsync(childB.Case.PoNumber!);
        Assert.NotNull(poA);
        Assert.NotNull(poB);
        Assert.Equal(1100m, Assert.Single(poA.Lines).QtyOrdered);
        Assert.Equal(100m, Assert.Single(poB.Lines).QtyOrdered);
    }

    [Fact]
    public async Task RejectSplitRunsSingleVendorAward()
    {
        var h = Create();
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-SPLIT-1", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        QueueTexts(h.Models, 2, "shortfall then raise");
        await h.Runner.AdvanceAsync(battery);
        var raised = Load(h, battery);
        var prDecision = PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(battery, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");

        h.Models.QueueText("screen");
        h.Models.QueueToolCalls("split 800/400",
            ("p1", AgentTools.ProposeSplit, SplitArgs(battery, ["V-A", "V-B"], [800, 400], "keep dual supply for resilience")));
        await h.Runner.AdvanceAsync(battery);
        var proposed = Load(h, battery);
        var split = PendingDecision(proposed, DecisionKind.SplitApproval);
        await h.Runner.ResolveDecisionAsync(battery, split.DecisionId, false, split.RequestedTo, "keep it simple");

        QueueTexts(h.Models, 6, "deferred award and order");
        await h.Runner.AdvanceAsync(battery);
        var awarded = Load(h, battery);
        Assert.Equal("V-B", awarded.Case.AwardedVendorId);
        Assert.NotNull(awarded.Case.PoNumber);
        Assert.Empty(ChildrenOf(h, battery));
        var snapshot = awarded.ScreeningSnapshot;
        Assert.NotNull(snapshot);
        Assert.Equal("V-B", snapshot.AwardedVendorId);
        Assert.Contains(awarded.Events, e =>
            e.Kind == CaseEventKind.Note && e.Summary.Contains("single-vendor award proceeds"));
    }

    [Fact]
    public async Task SwitchOffRunsTodayFlowWithNoProposeTurn()
    {
        var h = Create(splits: new SplitProposals { IsOn = false });
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-SPLIT-1", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        QueueTexts(h.Models, 8, "deferred single-vendor flow");
        await h.Runner.AdvanceAsync(battery);
        var raised = Load(h, battery);
        var prDecision = PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(battery, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(battery);

        var awarded = Load(h, battery);
        Assert.Equal("V-B", awarded.Case.AwardedVendorId);
        Assert.NotNull(awarded.Case.PoNumber);
        Assert.DoesNotContain(awarded.Decisions, d => d.Kind == DecisionKind.SplitApproval);
        Assert.DoesNotContain(awarded.Events, e =>
            e.Kind == CaseEventKind.Note && e.Summary.Contains(CasePicker.SplitSkippedMarker));
        var turns = CaseTranscript.ReadTurns(h.CaseDir, battery);
        Assert.DoesNotContain(
            turns.SelectMany(t => t.Calls), c => c.Name == AgentTools.ProposeSplit);
    }

    [Fact]
    public async Task RefusedProposalBlocksAndRetryCards()
    {
        var h = Create();
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-SPLIT-1", "planner", Candidates()));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        QueueTexts(h.Models, 2, "shortfall then raise");
        await h.Runner.AdvanceAsync(battery);
        var raised = Load(h, battery);
        var prDecision = PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(battery, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");

        h.Models.QueueText("screen");
        h.Models.QueueToolCalls("bad split",
            ("p1", AgentTools.ProposeSplit, SplitArgs(battery, ["V-A", "V-B"], [800, 300])));
        h.Models.QueueToolCalls("good split",
            ("p2", AgentTools.ProposeSplit, SplitArgs(battery, ["V-A", "V-B"], [800, 400], "keep dual supply for resilience")));
        var parked = await h.Runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);

        var turns = CaseTranscript.ReadTurns(h.CaseDir, battery);
        var calls = turns.SelectMany(t => t.Calls)
            .Where(c => c.Name == AgentTools.ProposeSplit)
            .ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(ToolCallVerdict.Blocked, calls[0].Verdict);
        Assert.Contains("Split refused", calls[0].Detail, StringComparison.Ordinal);
        Assert.Contains("adds up to", calls[0].Detail, StringComparison.Ordinal);
        Assert.Equal(ToolCallVerdict.Executed, calls[1].Verdict);
        _ = PendingDecision(Load(h, battery), DecisionKind.SplitApproval);
    }

    private static async Task RunChildAsync(
        Harness h, string caseId, string carton, string lot, string invoice, int qty, decimal price)
    {
        QueueTexts(h.Models, 6, "deferred child flow");
        // The parent's fan-out already submitted this child, so this
        // advance is a no-op that re-affirms the retry is idempotent:
        // submitted POs skip the draft check and the pending card is
        // found, never duplicated.
        await h.Runner.AdvanceAsync(caseId);
        var submitted = Load(h, caseId);
        var poDecision = PendingDecision(submitted, DecisionKind.PoApproval);
        await h.Runner.ResolveDecisionAsync(caseId, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");

        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(caseId));
        await h.Runner.AdvanceAsync(caseId);
        await h.Runner.OnAsnAsync(new SupplierAsnInput(caseId, $"ASN-{carton}", $"SLIP-{carton}",
            [new AsnCartonInput(carton, SeededErp.Battery, qty, lot)]));
        await h.Runner.AdvanceAsync(caseId);
        var poNumber = Load(h, caseId).Case.PoNumber!;
        var verdicts = await h.Runner.OnDockReadsAsync(caseId,
            [Read(carton, $"IMG-{carton}", poNumber, lot, qty, SeededErp.Battery)]);
        Assert.All(verdicts[carton], v => Assert.Equal(VerdictKind.Pass, v.Kind));
        await h.Runner.AdvanceAsync(caseId);

        var po = await h.Erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        var intake = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(caseId, invoice, po.VendorId, poNumber,
            [new SupplierInvoiceLineInput(po.Lines.Single().PoLineRef, qty, price)]));
        Assert.True(intake.Persisted);
        var invoiced = Load(h, caseId);
        var invDecision = PendingDecision(invoiced, DecisionKind.InvoiceApproval);
        await h.Runner.ResolveDecisionAsync(caseId, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(caseId);

        var drafted = Load(h, caseId);
        var pay = PendingDecision(drafted, DecisionKind.PaymentApproval);
        await h.Runner.ResolveDecisionAsync(caseId, pay.DecisionId, true, pay.RequestedTo, "ok");
        var done = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.Done, done.Step);
        Assert.Equal(CaseStage.Resolved, Load(h, caseId).Case.Stage);
    }

    private static async Task RunScrewAsync(Harness h, string caseId)
    {
        QueueTexts(h.Models, 10, "deferred screw flow");
        await h.Runner.AdvanceAsync(caseId);
        var raised = Load(h, caseId);
        var prDecision = PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(caseId, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(caseId);

        var awarded = Load(h, caseId);
        Assert.Equal("V-C", awarded.Case.AwardedVendorId);
        var poDecision = PendingDecision(awarded, DecisionKind.PoApproval);
        await h.Runner.ResolveDecisionAsync(caseId, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");
        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(caseId));
        await h.Runner.AdvanceAsync(caseId);
        await h.Runner.OnAsnAsync(new SupplierAsnInput(caseId, "ASN-S1", "SLIP-S1",
            [new AsnCartonInput("CTN-S1", SeededErp.Screw, 2370, "LOT-S1")]));
        await h.Runner.AdvanceAsync(caseId);

        var poNumber = Load(h, caseId).Case.PoNumber!;
        var verdicts = await h.Runner.OnDockReadsAsync(caseId,
            [new DockCartonRead("CTN-S1", SeededErp.Screw, poNumber, "LOT-S1", 2370,
                true, true, "UN3480", false, false, false, null, "IMG-S1", 0.95m)]);
        Assert.All(verdicts["CTN-S1"], v => Assert.Equal(VerdictKind.Pass, v.Kind));
        await h.Runner.AdvanceAsync(caseId);

        var po = await h.Erp.GetPurchaseOrderAsync(poNumber);
        Assert.NotNull(po);
        var intake = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(caseId, "INV-S1", po.VendorId, poNumber,
            [new SupplierInvoiceLineInput(po.Lines.Single().PoLineRef, 2370, 1m)]));
        Assert.True(intake.Persisted);
        var invoiced = Load(h, caseId);
        var invDecision = PendingDecision(invoiced, DecisionKind.InvoiceApproval);
        await h.Runner.ResolveDecisionAsync(caseId, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");
        await h.Runner.AdvanceAsync(caseId);

        var drafted = Load(h, caseId);
        var pay = PendingDecision(drafted, DecisionKind.PaymentApproval);
        await h.Runner.ResolveDecisionAsync(caseId, pay.DecisionId, true, pay.RequestedTo, "ok");
        var done = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.Done, done.Step);
        Assert.Equal(CaseStage.Resolved, Load(h, caseId).Case.Stage);
    }
}

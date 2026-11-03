// Video moment probes: four tests over one single flow, each driving its
// moment through the real P2pAgent, CaseRunner, ComplianceEngine, ERP, and
// dock controls on probe-only test data (no company baseline, no scenario
// files). Moment 1: shop shortfall screens PVCV out by CORP-02 and awards
// PVMW with a cited reason; the vendor master stays untouched. Moment 2: a
// dock photo without the lithium mark holds its carton and splits the
// invoice. Moment 3: Spark's illegal award is refused and Spark corrects
// itself; the ERP independently refuses the CORP-breaking award. Moment 4:
// finance part-pays the good cartons, pays the remainder after the reshot,
// and the customer is told the order is secured once every case for the
// order is resolved (the screw case sends it).
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval.VideoProbes;

public sealed class VideoMomentsTests
{
    [Fact]
    public async Task Moment1_ShopShortfallScreensAndAwardsMeridian()
    {
        var h = VideoFlowFixture.CreateHarness();
        var caseId = await VideoFlowFixture.OpenBatteryCaseAsync(h);

        var battery = VideoFlowFixture.Load(h, caseId);
        var so = await h.Erp.GetSalesOrderAsync(battery.Case.SoNumber);
        Assert.NotNull(so);
        Assert.Equal(SalesOrderStatus.Confirmed, so.Status);
        Assert.NotEqual(VideoProbeData.ShopId, so.SoNumber);

        var shortfalls = await h.Core.ComputeShortfallAsync(so.SoNumber);
        var row = shortfalls.Single(s => s.ItemId == VideoProbeData.Battery);
        Assert.Equal(35, row.Required);
        Assert.Equal(5, row.Covered);
        Assert.Equal(30, row.Shortfall);

        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, VideoFlowFixture.Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);

        var raised = VideoFlowFixture.Load(h, caseId);
        var prDecision = VideoFlowFixture.PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(caseId, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");

        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls(
            $"Award {VideoProbeData.VendorMw}; {VideoProbeData.VendorCv} blocked by CORP-02.",
            ("c4", AgentTools.AwardSupplier, VideoFlowFixture.AwardArgs(caseId, VideoProbeData.VendorMw)));
        h.Models.QueueToolCalls("create", ("c5", AgentTools.CreatePo, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c6", AgentTools.SubmitPoApproval, VideoFlowFixture.Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);

        var awarded = VideoFlowFixture.Load(h, caseId);
        Assert.Contains(awarded.Events, e =>
            e.Kind == CaseEventKind.VendorScreened &&
            (e.Summary ?? string.Empty).Contains(VideoProbeData.VendorCv, StringComparison.Ordinal) &&
            e.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Contains(awarded.Events, e =>
            e.Kind == CaseEventKind.Note
            && e.RuleId == ComplianceEngine.CorpOriginCapId
            && (e.Summary ?? string.Empty).Contains("rejected for the award", StringComparison.Ordinal));
        var vendors = await h.Erp.GetVendorsAsync();
        Assert.False(vendors.Single(v => v.VendorId == VideoProbeData.VendorCv).IsBlocked);
        var award = awarded.Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains(VideoProbeData.VendorMw, award.Summary, StringComparison.Ordinal);
        Assert.Contains(VideoProbeData.VendorCv, award.Summary, StringComparison.Ordinal);
        Assert.Contains(ComplianceEngine.CorpOriginCapId, award.Summary, StringComparison.Ordinal);
        Assert.Equal(VideoProbeData.VendorMw, awarded.Case.AwardedVendorId);
        Assert.NotNull(awarded.Case.PoNumber);
        var po = await h.Erp.GetPurchaseOrderAsync(awarded.Case.PoNumber);
        Assert.NotNull(po);
        Assert.Equal(VideoProbeData.VendorMw, po.VendorId);
        Assert.Equal(30 * 22.00m, po.Lines.Sum(l => l.LineAmount));
        Assert.Contains(awarded.Decisions, d =>
            d.Kind == DecisionKind.PoApproval && d.Status == DecisionStatus.Pending);
    }

    [Fact]
    public async Task Moment2_DockHoldSplitsInvoiceSoOnlyGoodCartonsPay()
    {
        var h = VideoFlowFixture.CreateHarness();
        var caseId = await VideoFlowFixture.OpenBatteryCaseAsync(h);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, VideoFlowFixture.Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);
        var raised = VideoFlowFixture.Load(h, caseId);
        var prDecision = VideoFlowFixture.PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(caseId, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");

        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls(
            $"Award {VideoProbeData.VendorMw}; {VideoProbeData.VendorCv} blocked by CORP-02.",
            ("c4", AgentTools.AwardSupplier, VideoFlowFixture.AwardArgs(caseId, VideoProbeData.VendorMw)));
        h.Models.QueueToolCalls("create", ("c5", AgentTools.CreatePo, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c6", AgentTools.SubmitPoApproval, VideoFlowFixture.Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);
        var submitted = VideoFlowFixture.Load(h, caseId);
        var poDecision = VideoFlowFixture.PendingDecision(submitted, DecisionKind.PoApproval);
        await h.Runner.ResolveDecisionAsync(caseId, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");

        var poNumber = VideoFlowFixture.Load(h, caseId).Case.PoNumber!;
        await h.Runner.AdvanceAsync(caseId);
        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(caseId));
        await h.Runner.AdvanceAsync(caseId);
        await h.Runner.OnAsnAsync(new SupplierAsnInput(
            caseId, VideoProbeData.SupplierAsn, VideoProbeData.SupplierPackingSlip,
            VideoFlowFixture.AsnCartons()));
        await h.Runner.AdvanceAsync(caseId);

        var verdicts = await h.Runner.OnDockReadsAsync(caseId, VideoFlowFixture.DockReads(poNumber));
        Assert.All(verdicts[VideoProbeData.CartonGood], v => Assert.Equal(VerdictKind.Pass, v.Kind));
        Assert.Contains(verdicts[VideoProbeData.CartonBad],
            v => v.Kind == VerdictKind.Hold && v.RuleId == DockEvaluator.MarkRuleId);

        var holds = await h.Erp.GetOpenHoldsAsync(null);
        var cartonHold = Assert.Single(holds, hh =>
            string.Equals(hh.CaseId, caseId, StringComparison.Ordinal) &&
            string.Equals(hh.CartonId, VideoProbeData.CartonBad, StringComparison.Ordinal));
        Assert.Equal(DockEvaluator.MarkRuleId, cartonHold.RuleId);
        var docked = VideoFlowFixture.Load(h, caseId);
        Assert.Contains(docked.Events, e => e.Kind == CaseEventKind.HoldSet);

        await h.Runner.AdvanceAsync(caseId);
        var poLine = (await h.Erp.GetPurchaseOrderAsync(poNumber))!.Lines.Single();
        var intake = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, VideoProbeData.SupplierInvoice, VideoProbeData.VendorMw, poNumber,
            [new SupplierInvoiceLineInput(poLine.PoLineRef, 30, 22.00m)]));
        Assert.True(intake.Persisted);
        Assert.Equal(15 * 22.00m, intake.AcceptedValue);
        Assert.Equal(15 * 22.00m, intake.HeldValue);
        var stored = await h.Erp.GetInvoiceAsync(VideoProbeData.VendorMw, VideoProbeData.SupplierInvoice);
        Assert.NotNull(stored);
        Assert.Contains(stored.Lines, l => l.Disposition == LineDisposition.Accepted && l.Quantity == 15);
        Assert.Contains(stored.Lines, l => l.Disposition == LineDisposition.Held && l.Quantity == 15);
    }

    [Fact]
    public async Task Moment3_SparkMisstepRefusedThenCorrectedWithErpRefusal()
    {
        var h = VideoFlowFixture.CreateHarness();
        var caseId = await VideoFlowFixture.OpenBatteryCaseAsync(h);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, VideoFlowFixture.Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);
        var raised = VideoFlowFixture.Load(h, caseId);
        var prDecision = VideoFlowFixture.PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(caseId, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");

        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls(
            $"award {VideoProbeData.VendorCv}",
            ("c4", AgentTools.AwardSupplier, VideoFlowFixture.AwardArgs(caseId, VideoProbeData.VendorCv)));
        h.Models.QueueToolCalls(
            $"Award {VideoProbeData.VendorMw}; {VideoProbeData.VendorCv} blocked by CORP-02.",
            ("c5", AgentTools.AwardSupplier, VideoFlowFixture.AwardArgs(caseId, VideoProbeData.VendorMw)));
        h.Models.QueueToolCalls("create", ("c6", AgentTools.CreatePo, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c7", AgentTools.SubmitPoApproval, VideoFlowFixture.Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);

        var turns = CaseTranscript.ReadTurns(h.CaseDir, caseId);
        var calls = turns.SelectMany(t => t.Calls).ToList();
        var refused = calls.First(c =>
            string.Equals(c.Name, AgentTools.AwardSupplier, StringComparison.Ordinal) &&
            c.Verdict == ToolCallVerdict.Blocked);
        Assert.Contains("did not pass screening", refused.Detail, StringComparison.Ordinal);
        Assert.Contains(VideoProbeData.VendorMw, refused.Detail, StringComparison.Ordinal);
        Assert.Contains(calls, c =>
            string.Equals(c.Name, AgentTools.AwardSupplier, StringComparison.Ordinal) &&
            c.Verdict == ToolCallVerdict.Executed);
        Assert.Equal(VideoProbeData.VendorMw, VideoFlowFixture.Load(h, caseId).Case.AwardedVendorId);

        var vendors = await h.Erp.GetVendorsAsync();
        Assert.False(vendors.Single(v => v.VendorId == VideoProbeData.VendorCv).IsBlocked);
        // 60 cells: with the case's 30-cell open Meridian order diluting the
        // total, (236 + 60) / (400 + 30 + 60) = 60.4% still breaches.
        var probePr = await h.Erp.CreatePrAsync(
            VideoProbeData.PlannerId, RecordSource.Human, VideoProbeData.PlannerId,
            [new PurchaseRequisitionLine(VideoProbeData.Battery, 60, "EA")],
            Guid.NewGuid().ToString("N"));
        Assert.Equal(PrStatus.Draft, probePr.Status);
        await h.Erp.SubmitForApprovalAsync(
            "PR", probePr.PrNumber, VideoProbeData.ApproverId, VideoProbeData.PlannerId,
            Guid.NewGuid().ToString("N"));
        await h.Erp.RecordApprovalAsync(
            "PR", probePr.PrNumber, VideoProbeData.DelegateId, true, "ok",
            Guid.NewGuid().ToString("N"));
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Erp.CreatePoAsync(
            probePr.PrNumber, VideoProbeData.VendorCv, RecordSource.Human,
            VideoProbeData.PlannerId, Guid.NewGuid().ToString("N")));
        Assert.StartsWith(ComplianceEngine.CorpOriginCapId, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Moment4_FinancePaysGoodCartonsAndCustomerToldSecured()
    {
        var h = VideoFlowFixture.CreateHarness();
        var caseId = await VideoFlowFixture.OpenBatteryCaseAsync(h);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, VideoFlowFixture.Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);
        var raised = VideoFlowFixture.Load(h, caseId);
        var prDecision = VideoFlowFixture.PendingDecision(raised, DecisionKind.PrApproval);
        await h.Runner.ResolveDecisionAsync(caseId, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");

        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls(
            $"Award {VideoProbeData.VendorMw}; {VideoProbeData.VendorCv} blocked by CORP-02.",
            ("c4", AgentTools.AwardSupplier, VideoFlowFixture.AwardArgs(caseId, VideoProbeData.VendorMw)));
        h.Models.QueueToolCalls("create", ("c5", AgentTools.CreatePo, VideoFlowFixture.Args(caseId)));
        h.Models.QueueToolCalls("submit", ("c6", AgentTools.SubmitPoApproval, VideoFlowFixture.Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);
        var submitted = VideoFlowFixture.Load(h, caseId);
        var poDecision = VideoFlowFixture.PendingDecision(submitted, DecisionKind.PoApproval);
        await h.Runner.ResolveDecisionAsync(caseId, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");

        var poNumber = VideoFlowFixture.Load(h, caseId).Case.PoNumber!;
        await h.Runner.AdvanceAsync(caseId);
        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(caseId));
        await h.Runner.AdvanceAsync(caseId);
        await h.Runner.OnAsnAsync(new SupplierAsnInput(
            caseId, VideoProbeData.SupplierAsn, VideoProbeData.SupplierPackingSlip,
            VideoFlowFixture.AsnCartons()));
        await h.Runner.AdvanceAsync(caseId);
        await h.Runner.OnDockReadsAsync(caseId, VideoFlowFixture.DockReads(poNumber));
        await h.Runner.AdvanceAsync(caseId);

        var poLine = (await h.Erp.GetPurchaseOrderAsync(poNumber))!.Lines.Single();
        var intake = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, VideoProbeData.SupplierInvoice, VideoProbeData.VendorMw, poNumber,
            [new SupplierInvoiceLineInput(poLine.PoLineRef, 30, 22.00m)]));
        Assert.Equal(15 * 22.00m, intake.AcceptedValue);
        var invoiced = VideoFlowFixture.Load(h, caseId);
        var invDecision = VideoFlowFixture.PendingDecision(invoiced, DecisionKind.InvoiceApproval);
        await h.Runner.ResolveDecisionAsync(caseId, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");

        // Quarantine holds only its own value: accepted value pays now.
        h.Models.QueueToolCalls("draft pay", ("c7", AgentTools.DraftPayment, VideoFlowFixture.Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);
        var drafted = VideoFlowFixture.Load(h, caseId);
        Assert.Equal(CaseStage.PaymentPartial, drafted.Case.Stage);
        var pay1Decision = VideoFlowFixture.PendingDecision(drafted, DecisionKind.PaymentApproval);
        Assert.Equal(VideoProbeData.FinanceId, pay1Decision.RequestedTo);
        Assert.Equal(15 * 22.00m, pay1Decision.Amount);
        await h.Runner.ResolveDecisionAsync(caseId, pay1Decision.DecisionId, true, VideoProbeData.FinanceId, "ok");
        var pay1Number = Assert.Single(VideoFlowFixture.Load(h, caseId).Case.PaymentNumbers);
        Assert.Equal(15 * 22.00m, (await h.Erp.GetPaymentAsync(pay1Number))!.Lines.Sum(l => l.Amount));
        Assert.Equal(InvoiceStatus.PartiallyPaid, (await h.Erp.GetInvoiceAsync(VideoProbeData.VendorMw, VideoProbeData.SupplierInvoice))!.Status);

        await h.Runner.AdvanceAsync(caseId);
        var reshot = await h.Runner.OnReshotAsync(new ReshotInput(caseId, [VideoProbeData.CartonBad],
        [
            new DockCartonRead(
                VideoProbeData.CartonBad, VideoProbeData.Battery, poNumber, VideoProbeData.LotBad, 15,
                true, true, "UN3480", true, true,
                false, null, VideoProbeData.ImageReshot, 0.98m)
        ]));
        Assert.All(reshot[VideoProbeData.CartonBad], v => Assert.Equal(VerdictKind.Pass, v.Kind));

        h.Models.QueueToolCalls("draft remainder", ("c8", AgentTools.DraftPayment, VideoFlowFixture.Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);
        var redrafted = VideoFlowFixture.Load(h, caseId);
        var pay2Decision = VideoFlowFixture.PendingDecision(redrafted, DecisionKind.PaymentApproval);
        Assert.Equal(15 * 22.00m, pay2Decision.Amount);
        await h.Runner.ResolveDecisionAsync(caseId, pay2Decision.DecisionId, true, VideoProbeData.FinanceId, "ok");

        h.Models.QueueToolCalls("finish", ("c9", AgentTools.FinishCase, VideoFlowFixture.Args(caseId)));
        var done = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.Done, done.Step);
        Assert.True(done.Complete);
        var final = VideoFlowFixture.Load(h, caseId);
        Assert.Equal(CaseStage.Resolved, final.Case.Stage);
        Assert.Equal(2, final.Case.PaymentNumbers.Count);
        Assert.Equal(InvoiceStatus.Paid, (await h.Erp.GetInvoiceAsync(VideoProbeData.VendorMw, VideoProbeData.SupplierInvoice))!.Status);
        // The screw sibling is still open, so the order-level customer
        // message stays unsent on the battery case.
        Assert.DoesNotContain(final.Events, e => e.Kind == CaseEventKind.CustomerNotified);

        await RunScrewCaseToNotifiedAsync(h);
    }

    private static async Task RunScrewCaseToNotifiedAsync(ProbeHarness h)
    {
        // The last case for the order sends the secured message, then finishes.
        var screw = CaseStore.ListCaseIds(h.CaseDir)
            .Single(id => !id.EndsWith(VideoProbeData.Battery, StringComparison.Ordinal));
        h.Models.QueueToolCalls("shortfall", ("s1", AgentTools.ComputeShortfall, VideoFlowFixture.Args(screw)));
        h.Models.QueueToolCalls("raise", ("s2", AgentTools.RaisePr, VideoFlowFixture.Args(screw)));
        h.Models.QueueToolCalls("screen", ("s3", AgentTools.ScreenVendors, VideoFlowFixture.Args(screw)));
        h.Models.QueueToolCalls("sole candidate",
            ("s4", AgentTools.AwardSupplier, VideoFlowFixture.AwardArgs(screw, VideoProbeData.VendorFs)));
        h.Models.QueueToolCalls("create", ("s5", AgentTools.CreatePo, VideoFlowFixture.Args(screw)));
        await h.Runner.AdvanceAsync(screw);

        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(screw));
        await h.Runner.AdvanceAsync(screw);
        var screwPo = VideoFlowFixture.Load(h, screw).Case.PoNumber!;
        var qty = (await h.Erp.GetPurchaseOrderAsync(screwPo))!.Lines.Single().QtyOrdered;
        var carton = $"PROBE-SCREW-{screwPo}";
        var lot = $"PROBE-SCREW-LOT-{screwPo}";
        await h.Runner.OnAsnAsync(new SupplierAsnInput(screw, $"SUP-ASN-{screwPo}", $"SUP-PS-{screwPo}",
            [new AsnCartonInput(carton, VideoProbeData.Screw, (int)qty, lot)]));
        await h.Runner.AdvanceAsync(screw);
        var verdicts = await h.Runner.OnDockReadsAsync(screw,
            [new DockCartonRead(carton, VideoProbeData.Screw, screwPo, lot, (int)qty,
                true, true, "UN3480", false, false, false, null, "PROBE-IMG-SCREW", 0.97m)]);
        Assert.All(verdicts.Values.SelectMany(v => v), v => Assert.Equal(VerdictKind.Pass, v.Kind));

        h.Models.QueueToolCalls("notify", ("s6", AgentTools.NotifyCustomer, VideoFlowFixture.Args(screw)));
        await h.Runner.AdvanceAsync(screw);
        var told = VideoFlowFixture.Load(h, screw);
        var tell = Assert.Single(told.Events, e => e.Kind == CaseEventKind.CustomerNotified);
        Assert.Contains("is secured", tell.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(told.Case.SoNumber, tell.Summary, StringComparison.Ordinal);

        var poLine = (await h.Erp.GetPurchaseOrderAsync(screwPo))!.Lines.Single();
        var intake = await h.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            screw, $"FS-{screwPo}", VideoProbeData.VendorFs, screwPo,
            [new SupplierInvoiceLineInput(poLine.PoLineRef, qty, poLine.UnitPrice)]));
        Assert.Equal(0, intake.HeldValue);
        var invoiced = VideoFlowFixture.Load(h, screw);
        var invDecision = VideoFlowFixture.PendingDecision(invoiced, DecisionKind.InvoiceApproval);
        await h.Runner.ResolveDecisionAsync(screw, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");

        h.Models.QueueToolCalls("draft pay", ("s7", AgentTools.DraftPayment, VideoFlowFixture.Args(screw)));
        await h.Runner.AdvanceAsync(screw);
        var drafted = VideoFlowFixture.Load(h, screw);
        var pay = VideoFlowFixture.PendingDecision(drafted, DecisionKind.PaymentApproval);
        Assert.Equal(qty * poLine.UnitPrice, pay.Amount);
        await h.Runner.ResolveDecisionAsync(screw, pay.DecisionId, true, pay.RequestedTo, "ok");

        h.Models.QueueToolCalls("finish", ("s8", AgentTools.FinishCase, VideoFlowFixture.Args(screw)));
        var done = await h.Runner.AdvanceAsync(screw);
        Assert.Equal(CaseNextStep.Done, done.Step);
        Assert.True(done.Complete);
        Assert.Equal(CaseStage.Resolved, VideoFlowFixture.Load(h, screw).Case.Stage);
    }
}

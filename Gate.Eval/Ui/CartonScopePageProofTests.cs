// Carton-scope page proof: two suppliers ship CTN-01 at once, with dock
// problems on only the battery one. Battery CTN-01 holds for its missing
// mark and CTN-02 routes to damage review, while screw CTN-01 passes and
// its invoice pays; the battery invoice holds with "Awaiting dock check"
// because the screw pass never releases battery lots. Both invoice
// records plus Decisions render. Test-double data only. No live calls.
using System.Net;
using Bunit;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Components.Pages;
using Gate.Web.Components.Pages.Erp;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

public sealed class CartonScopePageProofTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed record Flow(
        InMemoryErpAdapter Erp,
        CaseRunner Runner,
        RecordingModelClient Models,
        string CaseDir,
        string BatteryCaseId,
        string ScrewCaseId,
        string BatteryPoNumber,
        string ScrewPoNumber,
        string BatteryAsnNumber,
        string ScrewAsnNumber);

    private static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";

    private static string AwardArgs(string caseId, string vendorId) =>
        $$"""{"case_id":"{{caseId}}","vendor_id":"{{vendorId}}"}""";

    private static DockCartonRead Read(
        string carton, string part, string poNumber, string lot, int units,
        bool markPresent, bool damageVisible, bool class9Required, string imageId,
        string? damageNote = null) =>
        new(carton, part, poNumber, lot, units,
            markPresent, markPresent, markPresent ? "UN3480" : null,
            class9Required, true, damageVisible, damageNote, imageId, 0.97m);

    /// <summary>Drives both suppliers to receipted ASNs sharing CTN-01:
// battery CTN-01/02 plus screw CTN-01.</summary>
    private static async Task<Flow> DriveTwoSuppliersAsync()
    {
        var dir = UiTestSupport.NewDir("gate-ui-cartonscope");
        Directory.CreateDirectory(dir);
        var caseDir = Path.Combine(dir, "case-cache");
        var erp = SeededErp.CreateWithPolicy(new CasePolicyContext(caseDir));
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), caseDir);
        var started = await runner.OnOrderWebhookAsync(new OrderWebhookInput(
            "test",
            $"SHOP-{Guid.NewGuid():N}",
            "planner",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [SeededErp.Battery] = ["V-B"],
                [SeededErp.Screw] = ["V-C"],
            },
            $"idem-{Guid.NewGuid():N}",
            [new SalesOrderLine(SeededErp.Battery, 15),
             new SalesOrderLine(SeededErp.Screw, 150)],
            "USD"));
        var battery = started.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        var screw = started.Single(c => c.EndsWith(SeededErp.Screw, StringComparison.Ordinal));

        models.QueueToolCalls("shortfall", ("b1", AgentTools.ComputeShortfall, Args(battery)));
        models.QueueToolCalls("raise", ("b2", AgentTools.RaisePr, Args(battery)));
        models.QueueToolCalls("screen", ("b3", AgentTools.ScreenVendors, Args(battery)));
        models.QueueToolCalls("Award to V-B; sole candidate.", ("b4", AgentTools.AwardSupplier, AwardArgs(battery, "V-B")));
        models.QueueToolCalls("order", ("b5", AgentTools.CreatePo, Args(battery)));
        Assert.Equal(CaseNextStep.AwaitSupplierAck, (await runner.AdvanceAsync(battery)).Step);
        await runner.OnSupplierAckAsync(new SupplierAckInput(battery));
        var (asnB, receiptB) = await runner.OnAsnAsync(new SupplierAsnInput(
            battery, "ASN-TB", "PACK-TB",
            [new AsnCartonInput("CTN-01", SeededErp.Battery, 3, "LOT-B1"),
             new AsnCartonInput("CTN-02", SeededErp.Battery, 2, "LOT-B2")]));
        Assert.NotNull(receiptB);

        models.QueueToolCalls("shortfall", ("s1", AgentTools.ComputeShortfall, Args(screw)));
        models.QueueToolCalls("raise", ("s2", AgentTools.RaisePr, Args(screw)));
        models.QueueToolCalls("screen", ("s3", AgentTools.ScreenVendors, Args(screw)));
        models.QueueToolCalls("Award to V-C; sole candidate.", ("s4", AgentTools.AwardSupplier, AwardArgs(screw, "V-C")));
        models.QueueToolCalls("order", ("s5", AgentTools.CreatePo, Args(screw)));
        Assert.Equal(CaseNextStep.AwaitSupplierAck, (await runner.AdvanceAsync(screw)).Step);
        await runner.OnSupplierAckAsync(new SupplierAckInput(screw));
        var (asnS, receiptS) = await runner.OnAsnAsync(new SupplierAsnInput(
            screw, "ASN-TS", "PACK-TS",
            [new AsnCartonInput("CTN-01", SeededErp.Screw, 100, "LOT-S1")]));
        Assert.NotNull(receiptS);

        Assert.NotEqual(asnB.AsnNumber, asnS.AsnNumber);
        var batteryFile = CaseStore.Load(caseDir, battery);
        var screwFile = CaseStore.Load(caseDir, screw);
        Assert.NotNull(batteryFile?.Case.PoNumber);
        Assert.NotNull(screwFile?.Case.PoNumber);
        return new Flow(
            erp, runner, models, caseDir, battery, screw,
            batteryFile.Case.PoNumber, screwFile.Case.PoNumber,
            asnB.AsnNumber, asnS.AsnNumber);
    }

    private static TestContext Host(Flow flow, string actingAs)
    {
        var dir = UiTestSupport.NewDir("gate-ui-cartonscope-pages");
        Directory.CreateDirectory(dir);
        var cases = new CaseCacheStore(flow.CaseDir);
        var photos = new DockPhotoService(
            Path.Combine(dir, "dock"), Path.Combine(dir, "vision"));
        var views = new CaseViewService(flow.Erp, cases, photos, UiTestLogger<CaseViewService>.Instance);
        var acting = new ActingUserState();
        acting.SetCurrent(actingAs);
        var ctx = new TestContext();
        ctx.Services.AddSingleton(views);
        ctx.Services.AddSingleton(new ErpViewService(
            flow.Erp, views, UiTestLogger<ErpViewService>.Instance));
        ctx.Services.AddSingleton(new ErpActionService(
            flow.Erp, flow.Runner, UiTestLogger<ErpActionService>.Instance));
        ctx.Services.AddSingleton(new DecisionOutcomeService(
            cases, UiTestLogger<DecisionOutcomeService>.Instance, flow.Runner));
        ctx.Services.AddSingleton(new SimulationUiService(
            new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://localhost/") },
            flow.Erp, cases, photos, UiTestLogger<SimulationUiService>.Instance));
        ctx.Services.AddSingleton(acting);
        ctx.Services.AddSingleton<ILogger<Decisions>>(_ => UiTestLogger<Decisions>.Instance);
        ctx.Services.AddSingleton<ILogger<ErpRecord>>(_ => UiTestLogger<ErpRecord>.Instance);
        var models = new FakeModelClient();
        ctx.Services.AddSingleton(new AskWhyService(
            new P2pAgent(flow.Erp, models, new RecordingComplianceEngine()),
            models, flow.Erp, cases, photos, new ReplayState(),
            UiTestLogger<AskWhyService>.Instance, Path.Combine(dir, "model-cache")));
        return ctx;
    }

    private static void AssertClean(string markup)
    {
        Assert.DoesNotContain("gate-notice-error", markup);
        Assert.DoesNotContain("failed to load", markup);
        Assert.DoesNotContain("Object reference", markup);
    }

    private static string RenderRecord(TestContext ctx, string vendor, string number)
    {
        using var page = ctx.RenderComponent<ErpRecord>(p => p
            .Add(r => r.Vendor, vendor)
            .Add(r => r.Number, number));
        page.WaitForAssertion(
            () => Assert.Contains(number, page.Markup),
            TimeSpan.FromSeconds(10));
        AssertClean(page.Markup);
        return page.Markup;
    }

    [Fact]
    public async Task TwoSuppliersSharingCtn01StayIsolatedOnPages()
    {
        var flow = await DriveTwoSuppliersAsync();

        // Both notices carry CTN-01; the ERP holds both.
        var asnB = await flow.Erp.GetAsnAsync(flow.BatteryAsnNumber);
        var asnS = await flow.Erp.GetAsnAsync(flow.ScrewAsnNumber);
        Assert.NotNull(asnB);
        Assert.NotNull(asnS);
        Assert.Contains(asnB.Cartons, c => c.CartonId == "CTN-01");
        Assert.Contains(asnS.Cartons, c => c.CartonId == "CTN-01");

        // Problems land only on the battery shipment.
        var outcomeB = await flow.Runner.OnDockReadsAsync(flow.BatteryCaseId,
            [Read("CTN-01", SeededErp.Battery, flow.BatteryPoNumber, "LOT-B1", 3, false, false, true, "img-B"),
             Read("CTN-02", SeededErp.Battery, flow.BatteryPoNumber, "LOT-B2", 2, true, true, true, "img-B", "caved-in corner")]);
        var markHold = Assert.Single(outcomeB["CTN-01"], v => v.Kind != VerdictKind.Pass);
        Assert.Equal(VerdictKind.Hold, markHold.Kind);
        var damageReview = Assert.Single(outcomeB["CTN-02"], v => v.Kind != VerdictKind.Pass);
        Assert.Equal(VerdictKind.HumanReview, damageReview.Kind);
        var outcomeS = await flow.Runner.OnDockReadsAsync(flow.ScrewCaseId,
            [Read("CTN-01", SeededErp.Screw, flow.ScrewPoNumber, "LOT-S1", 100, true, false, false, "img-S")]);
        Assert.All(outcomeS.Values, list => Assert.All(list, v => Assert.Equal(VerdictKind.Pass, v.Kind)));

        // The hold sits on the battery case only.
        var holds = await flow.Erp.GetOpenHoldsAsync(null);
        var hold = Assert.Single(holds);
        Assert.Equal(flow.BatteryCaseId, hold.CaseId);
        Assert.Equal("CTN-01", hold.CartonId);
        Assert.Equal("LOT-B1", hold.LotRef);
        Assert.Equal(flow.BatteryAsnNumber, hold.AsnNumber);
        var screwFile = CaseStore.Load(flow.CaseDir, flow.ScrewCaseId);
        Assert.NotNull(screwFile);
        Assert.DoesNotContain(screwFile.Decisions, d =>
            d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview);

        // Invoices: battery holds for the dock check, screw is accepted.
        var poB = await flow.Erp.GetPurchaseOrderAsync(flow.BatteryPoNumber);
        var poS = await flow.Erp.GetPurchaseOrderAsync(flow.ScrewPoNumber);
        Assert.NotNull(poB);
        Assert.NotNull(poS);
        var lineB = Assert.Single(poB.Lines);
        var lineS = Assert.Single(poS.Lines);
        await flow.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            flow.BatteryCaseId, "INV-TB", "V-B", flow.BatteryPoNumber,
            [new SupplierInvoiceLineInput(lineB.PoLineRef, lineB.QtyOrdered, lineB.UnitPrice)]));
        await flow.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            flow.ScrewCaseId, "INV-TS", "V-C", flow.ScrewPoNumber,
            [new SupplierInvoiceLineInput(lineS.PoLineRef, lineS.QtyOrdered, lineS.UnitPrice)]));

        var batteryFile = CaseStore.Load(flow.CaseDir, flow.BatteryCaseId);
        Assert.NotNull(batteryFile);
        var review = batteryFile.Decisions.Single(d =>
            d.Kind == DecisionKind.DamageReview && d.Status == DecisionStatus.Pending);
        using var ctx = Host(flow, review.RequestedTo);
        var actions = ctx.Services.GetRequiredService<ErpActionService>();

        var held = RenderRecord(ctx, "V-B", "INV-TB");
        Assert.Contains("Awaiting dock check", held);

        var refused = await actions.CreatePaymentDraftAsync(
            [new InvoiceKey("V-B", "INV-TB")], null, "planner");
        Assert.False(refused.Ok);

        var released = await actions.AttemptAsync(
            "invoice", "V-C", "INV-TS", ErpActions.ReleaseInvoiceLine,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ErpActions.InPoLineRef] = lineS.PoLineRef,
                [ErpActions.InQuantity] = lineS.QtyOrdered.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            "planner");
        Assert.True(released.Ok, released.Message);

        var submitted = await actions.AttemptAsync(
            "invoice", "V-C", "INV-TS", ErpActions.Submit,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ErpActions.InRoutedTo] = "delegate",
            },
            "planner");
        Assert.True(submitted.Ok, submitted.Message);
        var approved = await actions.AttemptAsync(
            "invoice", "V-C", "INV-TS", ErpActions.Approve,
            new Dictionary<string, string>(StringComparer.Ordinal), "delegate");
        Assert.True(approved.Ok, approved.Message);
        var posted = await actions.AttemptAsync(
            "invoice", "V-C", "INV-TS", ErpActions.PostInvoice,
            new Dictionary<string, string>(StringComparer.Ordinal), "planner");
        Assert.True(posted.Ok, posted.Message);
        var draft = await actions.CreatePaymentDraftAsync(
            [new InvoiceKey("V-C", "INV-TS")], null, "planner");
        Assert.True(draft.Ok, draft.Message);
        Assert.NotNull(draft.CreatedKey);
        await flow.Erp.SubmitForApprovalAsync("Payment", draft.CreatedKey, "finance", "planner", Guid.NewGuid().ToString("N"));
        await flow.Erp.RecordApprovalAsync("Payment", draft.CreatedKey, "finance", true, "ok", Guid.NewGuid().ToString("N"));
        await flow.Erp.PostPaymentAsync(draft.CreatedKey, "finance", Guid.NewGuid().ToString("N"));
        var paid = RenderRecord(ctx, "V-C", "INV-TS");
        Assert.Contains("Paid", paid);

        using var board = ctx.RenderComponent<Decisions>();
        board.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", board.Markup),
            TimeSpan.FromSeconds(10));
        AssertClean(board.Markup);
        Assert.Contains("Damage review for CTN-02", board.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Damage review for CTN-01", board.Markup);
        Assert.DoesNotContain("Dock review for CTN-01", board.Markup);
    }
}

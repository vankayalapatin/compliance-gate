// Dock-gate page proof: one shop webhook starts a battery case (parked
// on its PR approval) plus a screw case driven through screening, award,
// PO, supplier ACK, and a two-carton receipted ASN on a policy-wired ERP.
// The agent-intaken invoice matches Held with "Awaiting dock check", the
// payment draft refuses, the dock pass releases the value, and the draft
// then pays. Every assertion that matters renders a real page: the
// invoice and receipt records, Decisions, the case detail, and the
// outside world. Test-double data only. No live calls.
using System.Net;
using Bunit;
using Gate.Agent;
using Gate.Compliance;
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

public sealed class DockGatePageProofTests
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
        string CaseDir,
        string BatteryCaseId,
        string ScrewCaseId,
        string ScrewPoNumber);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-A", "V-B"],
            [SeededErp.Screw] = ["V-C"],
        };

    private static DockCartonRead Read(
        string carton, string lot, int units, string poNumber) =>
        new(carton, SeededErp.Screw, poNumber, lot, units, true, true, "UN3480",
            false, false, false, null, $"img-{carton}", 0.95m);

    /// <summary>Drives the two-case walk on a policy-wired ERP: battery
    /// parks on its PR approval while screw reaches a receipted two-carton
    /// ASN.</summary>
    private static async Task<Flow> DriveDockStageAsync()
    {
        var dir = UiTestSupport.NewDir("gate-ui-dockgate");
        Directory.CreateDirectory(dir);
        var erp = SeededErp.CreateWithPolicy(new CasePolicyContext(dir));
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        var started = await runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var battery = started.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        var screw = started.Single(c => c.EndsWith(SeededErp.Screw, StringComparison.Ordinal));

        await runner.AdvanceAsync(battery);
        var parked = CaseStore.Load(dir, battery);
        Assert.NotNull(parked);
        Assert.Contains(parked.Decisions, d =>
            d.Kind == DecisionKind.PrApproval && d.Status == DecisionStatus.Pending);

        var outcome = await runner.AdvanceAsync(screw);
        Assert.Equal(CaseNextStep.AwaitSupplierAck, outcome.Step);
        await runner.OnSupplierAckAsync(new SupplierAckInput(screw));
        await runner.AdvanceAsync(screw);
        var (asn, receipt) = await runner.OnAsnAsync(new SupplierAsnInput(
            screw,
            $"ASN-{Guid.NewGuid():N}",
            "PACK-G1",
            [new AsnCartonInput("CTN-G1", SeededErp.Screw, 75, "LOT-G1"),
             new AsnCartonInput("CTN-G2", SeededErp.Screw, 75, "LOT-G2")]));
        Assert.NotNull(receipt);
        _ = asn;

        var screwFile = CaseStore.Load(dir, screw);
        Assert.NotNull(screwFile);
        Assert.NotNull(screwFile.Case.PoNumber);
        return new Flow(erp, runner, dir, battery, screw, screwFile.Case.PoNumber);
    }

    private static TestContext Host(Flow flow, string actingAs)
    {
        var dir = UiTestSupport.NewDir("gate-ui-dockgate-pages");
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
        ctx.Services.AddSingleton<ILogger<CaseDetail>>(_ => UiTestLogger<CaseDetail>.Instance);
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
        Assert.DoesNotContain("Unknown ERP record kind", markup);
        Assert.DoesNotContain("was not found", markup);
    }

    private static string RenderClean<T>(TestContext ctx) where T : IComponent
    {
        using var page = ctx.RenderComponent<T>();
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        AssertClean(page.Markup);
        return page.Markup;
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
    public async Task DockPassBeforeInvoiceAcceptsAtIntakeOnPages()
    {
        // The dock check passes first; the invoice arriving later must be
        // accepted at intake instead of held for a check that already
        // passed. Regression: the match ran before the invoice was linked
        // to the case, so the dock-pass lookup always came back empty and
        // every invoice matched accepted 0, held full.
        var flow = await DriveDockStageAsync();
        var po = await flow.Erp.GetPurchaseOrderAsync(flow.ScrewPoNumber);
        Assert.NotNull(po);
        var poLine = Assert.Single(po.Lines);

        var verdicts = await flow.Runner.OnDockReadsAsync(
            flow.ScrewCaseId,
            [Read("CTN-G1", "LOT-G1", 75, flow.ScrewPoNumber),
             Read("CTN-G2", "LOT-G2", 75, flow.ScrewPoNumber)]);
        Assert.All(verdicts.Values, list => Assert.All(list, v => Assert.Equal(VerdictKind.Pass, v.Kind)));

        var intake = await flow.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            flow.ScrewCaseId, "INV-G2", "V-C", flow.ScrewPoNumber,
            [new SupplierInvoiceLineInput(poLine.PoLineRef, 150, poLine.UnitPrice)]));
        Assert.True(intake.Persisted);
        Assert.Equal(150 * poLine.UnitPrice, intake.AcceptedValue);
        Assert.Equal(0, intake.HeldValue);

        var screwFile = CaseStore.Load(flow.CaseDir, flow.ScrewCaseId);
        Assert.NotNull(screwFile);
        var approval = screwFile.Decisions.Single(d =>
            d.Kind == DecisionKind.InvoiceApproval && d.Status == DecisionStatus.Pending);
        using var ctx = Host(flow, approval.RequestedTo);
        var actions = ctx.Services.GetRequiredService<ErpActionService>();

        // The invoice record shows accepted value with no dock hold.
        var accepted = RenderRecord(ctx, "V-C", "INV-G2");
        Assert.DoesNotContain("Awaiting dock check", accepted);

        // The approval card renders on Decisions and the flow pays.
        using var board = ctx.RenderComponent<Decisions>();
        board.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", board.Markup),
            TimeSpan.FromSeconds(10));
        AssertClean(board.Markup);
        Assert.Contains("Approve invoice V-C/INV-G2", board.Markup);

        var approved = await actions.AttemptAsync(
            "invoice", "V-C", "INV-G2", ErpActions.Approve,
            new Dictionary<string, string>(StringComparer.Ordinal), approval.RequestedTo);
        Assert.True(approved.Ok, approved.Message);
        // Approval settles the linked card, which posts the invoice; the
        // draft then pays.
        var draft = await actions.CreatePaymentDraftAsync(
            [new InvoiceKey("V-C", "INV-G2")], null, "planner");
        Assert.True(draft.Ok, draft.Message);
    }

    [Fact]
    public async Task DockGateHoldsReleasesAndPaysOnPages()
    {
        var flow = await DriveDockStageAsync();
        var batteryFile = CaseStore.Load(flow.CaseDir, flow.BatteryCaseId);
        Assert.NotNull(batteryFile);
        var prCard = batteryFile.Decisions.Single(d =>
            d.Kind == DecisionKind.PrApproval && d.Status == DecisionStatus.Pending);
        using var ctx = Host(flow, prCard.RequestedTo);
        var actions = ctx.Services.GetRequiredService<ErpActionService>();

        var po = await flow.Erp.GetPurchaseOrderAsync(flow.ScrewPoNumber);
        Assert.NotNull(po);
        var poLineRef = Assert.Single(po.Lines).PoLineRef;
        var price = Assert.Single(po.Lines).UnitPrice;
        var intake = await flow.Runner.OnInvoiceAsync(new SupplierInvoiceInput(
            flow.ScrewCaseId, "INV-G1", "V-C", flow.ScrewPoNumber,
            [new SupplierInvoiceLineInput(poLineRef, 150, price)]));
        _ = intake;

        // Pre-dock: the invoice record shows the held value and its reason.
        var held = RenderRecord(ctx, "V-C", "INV-G1");
        Assert.Contains("Awaiting dock check", held);
        Assert.Contains("Held lines release only when the hold, quarantine, dock check, or review behind them closes.", held);

        // The draft refuses: held value never enters a payment.
        var refused = await actions.CreatePaymentDraftAsync(
            [new InvoiceKey("V-C", "INV-G1")], null, "planner");
        Assert.False(refused.Ok);
        Assert.Contains("Nothing due", refused.Message, StringComparison.Ordinal);

        // The dock pass releases the value through the page action path.
        var verdicts = await flow.Runner.OnDockReadsAsync(
            flow.ScrewCaseId,
            [Read("CTN-G1", "LOT-G1", 75, flow.ScrewPoNumber),
             Read("CTN-G2", "LOT-G2", 75, flow.ScrewPoNumber)]);
        Assert.All(verdicts.Values, list => Assert.All(list, v => Assert.Equal(VerdictKind.Pass, v.Kind)));
        var released = await actions.AttemptAsync(
            "invoice", "V-C", "INV-G1", ErpActions.ReleaseInvoiceLine,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ErpActions.InPoLineRef] = poLineRef,
                [ErpActions.InQuantity] = "150",
            },
            "planner");
        Assert.True(released.Ok, released.Message);

        // Post-release: the invoice record shows accepted value, and the
        // draft pays after the invoice posts.
        var accepted = RenderRecord(ctx, "V-C", "INV-G1");
        Assert.DoesNotContain("Awaiting dock check", accepted);
        var submitted = await actions.AttemptAsync(
            "invoice", "V-C", "INV-G1", ErpActions.Submit,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ErpActions.InRoutedTo] = "delegate",
            },
            "planner");
        Assert.True(submitted.Ok, submitted.Message);
        var approved = await actions.AttemptAsync(
            "invoice", "V-C", "INV-G1", ErpActions.Approve,
            new Dictionary<string, string>(StringComparer.Ordinal), "delegate");
        Assert.True(approved.Ok, approved.Message);
        var posted = await actions.AttemptAsync(
            "invoice", "V-C", "INV-G1", ErpActions.PostInvoice,
            new Dictionary<string, string>(StringComparer.Ordinal), "planner");
        Assert.True(posted.Ok, posted.Message);
        var draft = await actions.CreatePaymentDraftAsync(
            [new InvoiceKey("V-C", "INV-G1")], null, "planner");
        Assert.True(draft.Ok, draft.Message);

        // The surrounding pages stay clean over the policy-wired world.
        var decisions = RenderClean<Decisions>(ctx);
        Assert.Contains("Purchase request approval", decisions);
        RenderClean<Outside>(ctx);
        using var detail = ctx.RenderComponent<CaseDetail>(p => p.Add(c => c.CaseId, flow.ScrewCaseId));
        detail.WaitForAssertion(
            () => Assert.Contains(flow.ScrewCaseId, detail.Markup),
            TimeSpan.FromSeconds(10));
        AssertClean(detail.Markup);
    }
}

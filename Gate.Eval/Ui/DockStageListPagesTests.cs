// Dock-stage list-page proof over the real flow: one shop webhook starts a
// battery case plus a screw case on the seeded ERP; the battery parks on
// its human PR approval (the walk's approve step) while the screw runs
// the real runner path through screening, award, PO, supplier ACK, and an
// ASN carrying staged dock problems (the same OnAsnAsync path the
// supplier ASN endpoint uses, which persists the problems sidecar next to
// the case file and posts the receipt). Then the real list pages render
// over the real files: Decisions, Cases, Governance Rules, and the
// outside world with the supplier portal. Fails on any load error: the
// sidecar shares the cache dir but is never a case. Test-double data
// only. No live calls.
using System.Net;
using Bunit;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Components.Pages;
using Gate.Web.Components.Pages.Governance;
using Gate.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class DockStageListPagesTests
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
        string BatteryPrNumber,
        string PrRequestedTo,
        string ScrewCaseId,
        string ScrewPoNumber);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-A", "V-B"],
            [SeededErp.Screw] = ["V-C"],
        };

    /// <summary>Drives the two-case walk: battery parks on its pending PR
    /// approval while screw reaches a receipted ASN with staged dock
    /// problems persisted beside its case file.</summary>
    private static async Task<Flow> DriveDockStageAsync()
    {
        var erp = SeededErp.Create();
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = UiTestSupport.NewDir("gate-ui-dockstage");
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
        var prCard = parked.Decisions.Single(
            d => d.Kind == DecisionKind.PrApproval && d.Status == DecisionStatus.Pending);
        Assert.NotNull(prCard.DocumentNumber);

        var outcome = await runner.AdvanceAsync(screw);
        Assert.Equal(CaseNextStep.AwaitSupplierAck, outcome.Step);
        await runner.OnSupplierAckAsync(new SupplierAckInput(screw));
        await runner.AdvanceAsync(screw);
        var (asn, receipt) = await runner.OnAsnAsync(new SupplierAsnInput(
            screw,
            "ASN-S1",
            "PACK-S1",
            [new AsnCartonInput("CTN-S1", SeededErp.Screw, 150, "LOT-S1")],
            SimulatedProblems: [new SimulatedProblem("CTN-S1", SimulatedProblemKind.Crushed)]));
        Assert.NotNull(receipt);
        _ = asn;

        // The crash precondition: the sidecar exists beside the case file.
        var sidecar = CaseStore.LoadProblems(dir, screw);
        Assert.NotNull(sidecar);
        Assert.Equal("CTN-S1", Assert.Single(sidecar.Problems).CartonId);

        var screwFile = CaseStore.Load(dir, screw);
        Assert.NotNull(screwFile);
        Assert.NotNull(screwFile.Case.PoNumber);
        return new Flow(
            erp, runner, dir, battery, prCard.DocumentNumber, prCard.RequestedTo,
            screw, screwFile.Case.PoNumber);
    }

    private static CrawlerTests.World BuildWorld(Flow flow)
    {
        var dir = UiTestSupport.NewDir("gate-ui-dockstage-pages");
        Directory.CreateDirectory(dir);
        var erp = new CompanyInfoErpDouble(flow.Erp, new CompanyInfoDto(
            "LE-T", "Dockstage Company", "USD", "1 Bill St", "2 Ship St", "buy@dockstage.test"));
        var cases = new CaseCacheStore(flow.CaseDir);
        var photos = new DockPhotoService(
            Path.Combine(dir, "dock"), Path.Combine(dir, "vision"));
        var views = new CaseViewService(erp, cases, photos, UiTestLogger<CaseViewService>.Instance);
        var erpViews = new ErpViewService(erp, views, UiTestLogger<ErpViewService>.Instance);
        var outcomes = new DecisionOutcomeService(
            cases, UiTestLogger<DecisionOutcomeService>.Instance, flow.Runner);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Baseline:Dir"] = Path.Combine(dir, "baseline"),
                ["Erp:Sqlite"] = Path.Combine(dir, "live", "erp.db"),
            })
            .Build();
        var replayState = new ReplayState();
        var replay = new ReplayService(
            cases, photos, replayState, config,
            UiTestLogger<ReplayService>.Instance, dir, new ModelSpendTracker());
        var simulate = new SimulationUiService(
            new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://localhost/") },
            erp, cases, photos, UiTestLogger<SimulationUiService>.Instance);

        var kit = MemoTestKit.Create();
        var memos = new MemoFileStore(kit.CacheDir);
        var engine = new GovernanceFakeEngine();
        var govViews = new GovernanceViewService(
            engine, cases, memos, UiTestLogger<GovernanceViewService>.Instance);
        var kbViews = new KbViewService(engine, UiTestLogger<KbViewService>.Instance);
        var kbEdit = new KbEditService(engine, erp, UiTestLogger<KbEditService>.Instance);
        var policyViews = new PolicyViewService(memos, UiTestLogger<PolicyViewService>.Instance);
        var policyOutcomes = new PolicyOutcomeService(
            memos, kit.Runner, UiTestLogger<PolicyOutcomeService>.Instance);
        var memoSubmit = new MemoSubmitService(
            kit.Runner, memos, kit.InboxDir, UiTestLogger<MemoSubmitService>.Instance);

        return new CrawlerTests.World(
            dir, flow.CaseDir, erp, cases, photos, views, erpViews,
            new ErpActionService(erp, flow.Runner, UiTestLogger<ErpActionService>.Instance),
            outcomes, replay, simulate, govViews, kbViews, kbEdit, memos,
            policyViews, policyOutcomes, memoSubmit, replayState, new ModelSpendTracker(),
            engine, [flow.BatteryCaseId, flow.ScrewCaseId]);
    }

    private static void AssertClean(string markup)
    {
        Assert.DoesNotContain("gate-notice-error", markup);
        Assert.DoesNotContain("failed to load", markup);
        Assert.DoesNotContain("Object reference", markup);
        Assert.DoesNotContain("Unknown list", markup);
        Assert.DoesNotContain("Unknown ERP record kind", markup);
        Assert.DoesNotContain("was not found", markup);
        Assert.DoesNotContain("not associated with the Dispatcher", markup);
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

    [Fact]
    public async Task DockStageCaseListsEveryPageClean()
    {
        var flow = await DriveDockStageAsync();
        var w = BuildWorld(flow);
        var acting = new ActingUserState();
        acting.SetCurrent(flow.PrRequestedTo);
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);

        // The three scans behind the reported pages, straight at the
        // services first so a failure names its scan.
        Assert.Equal(flow.ScrewCaseId, await w.Simulate.FindCaseIdForPoAsync(flow.ScrewPoNumber));
        var cards = await w.Views.ListDecisionCardsAsync();
        Assert.Contains(cards, c => c.Decision.Kind == DecisionKind.PrApproval
            && c.Decision.Status == DecisionStatus.Pending);
        _ = await w.GovViews.ListRuleRowsAsync();

        // Then the real pages over the real files.
        var decisions = RenderClean<Decisions>(ctx);
        Assert.Contains(flow.BatteryPrNumber, decisions);
        var cases = RenderClean<Cases>(ctx);
        Assert.Contains(flow.BatteryCaseId, cases);
        Assert.Contains(flow.ScrewCaseId, cases);
        RenderClean<GovernanceRules>(ctx);
        RenderClean<Outside>(ctx);
    }
}

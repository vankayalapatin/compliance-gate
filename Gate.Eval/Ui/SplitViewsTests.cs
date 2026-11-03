// Split-view tests: the pending split card shows each line, the final
// country shares, and the saving; the parent page lists both children
// with their orders and stages; each child links back to the parent.
// Real flows through the runner, rendered pages, local data only.
using System.Net;
using Bunit;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Eval.VideoProbes;
using Gate.Web.Components.Layout;
using Gate.Web.Components.Outside;
using Gate.Web.Components.Pages;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

public sealed class SplitViewsTests
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
        InMemoryErpAdapter Erp, CaseRunner Runner, ProbeModelClient Models, string CaseDir, string Battery);

    private static async Task<Flow> SplitFlowAsync()
    {
        var erp = SeededErp.Create();
        erp.SeedVolumeHistory(SeededErp.Battery,
        [
            new VolumeHistory("V-A CN active", "CN", 5600),
            new VolumeHistory("V-B MX active", "MX", 4000),
            new VolumeHistory("Other non-CN", "OTHER", 400),
        ]);
        erp.SeedSalesOrder(new SalesOrder("SO-SPLIT-V", SalesOrderStatus.Confirmed, "USD",
            [new SalesOrderLine(SeededErp.Glasses, 1210)]));
        var models = new ProbeModelClient();
        var compliance = new ComplianceEngine();
        var core = new P2pAgent(erp, models, compliance);
        var dir = UiTestSupport.NewDir("gate-split-views");
        var runner = new CaseRunner(erp, core, models, compliance, new DockEvaluatorAdapter(), dir,
            countryNames: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CN"] = "China",
                ["MX"] = "Mexico",
            });
        var cases = await runner.OnOrderWebhookAsync(new OrderWebhookInput("test", "SO-SPLIT-V", "planner",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [SeededErp.Battery] = ["V-A", "V-B"],
                [SeededErp.Screw] = ["V-C"],
            }));
        return new Flow(erp, runner, models, dir,
            cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal)));
    }

    private static string Args(string caseId) => $"{{\"case_id\":\"{caseId}\"}}";

    private static async Task<Decision> CardUpAsync(Flow f)
    {
        f.Models.QueueText("shortfall");
        f.Models.QueueText("raise");
        await f.Runner.AdvanceAsync(f.Battery);
        var raised = CaseStore.Load(f.CaseDir, f.Battery)!;
        var prDecision = raised.Decisions.Single(d =>
            d.Kind == DecisionKind.PrApproval && d.Status == DecisionStatus.Pending);
        await f.Runner.ResolveDecisionAsync(f.Battery, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");
        f.Models.QueueText("screen");
        f.Models.QueueToolCalls("split",
            ("p1", AgentTools.ProposeSplit,
            $"{{\"case_id\":\"{f.Battery}\",\"vendor_ids\":[\"V-A\",\"V-B\"],\"quantities\":[800,400]," +
            $"\"note\":\"keep dual supply for resilience\"}}"));
        await f.Runner.AdvanceAsync(f.Battery);
        return CaseStore.Load(f.CaseDir, f.Battery)!.Decisions.Single(d =>
            d.Kind == DecisionKind.SplitApproval && d.Status == DecisionStatus.Pending);
    }

    private static TestContext Host(Flow f, string actingAs = "delegate")
    {
        var dir = UiTestSupport.NewDir("gate-split-host");
        var imgDir = Path.Combine(dir, "img");
        Directory.CreateDirectory(imgDir);
        var cases = new CaseCacheStore(f.CaseDir);
        var photos = new DockPhotoService(imgDir, Path.Combine(dir, "reads"));
        var countries = new CountryNames(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CN"] = "China",
            ["MX"] = "Mexico",
        });
        var views = new CaseViewService(
            f.Erp, cases, photos,
            UiTestLogger<CaseViewService>.Instance,
            countries: countries);
        var acting = new ActingUserState();
        acting.SetCurrent(actingAs);
        var ctx = new TestContext();
        ctx.Services.AddSingleton(views);
        ctx.Services.AddSingleton(new DecisionOutcomeService(
            cases, UiTestLogger<DecisionOutcomeService>.Instance, f.Runner));
        ctx.Services.AddSingleton(new SimulationUiService(
            new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://localhost/") },
            f.Erp, cases, photos, UiTestLogger<SimulationUiService>.Instance));
        ctx.Services.AddSingleton(new AgentActivityService(
            cases, f.Erp, f.Runner, UiTestLogger<AgentActivityService>.Instance,
            countries: countries));
        ctx.Services.AddSingleton(new PresentationPace());
        ctx.Services.AddSingleton(new AskWhyService(
            new P2pAgent(f.Erp, new FakeModelClient(), new RecordingComplianceEngine()),
            new FakeModelClient(), f.Erp, cases,
            new DockPhotoService(imgDir, Path.Combine(dir, "reads")), new ReplayState(),
            UiTestLogger<AskWhyService>.Instance, Path.Combine(dir, "model-cache")));
        ctx.Services.AddSingleton(acting);
        ctx.Services.AddSingleton<ILogger<Decisions>>(UiTestLogger<Decisions>.Instance);
        ctx.Services.AddSingleton<ILogger<CaseDetail>>(UiTestLogger<CaseDetail>.Instance);
        return ctx;
    }

    private static string Render<T>(TestContext ctx) where T : IComponent
    {
        using var page = ctx.RenderComponent<T>();
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        return page.Markup;
    }

    private static string RenderDetail(TestContext ctx, string caseId)
    {
        using var page = ctx.RenderComponent<CaseDetail>(p => p.Add(c => c.CaseId, caseId));
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading case", page.Markup),
            TimeSpan.FromSeconds(10));
        return page.Markup;
    }

    [Fact]
    public async Task SplitCardShowsLinesSharesAndSaving()
    {
        var f = await SplitFlowAsync();
        _ = await CardUpAsync(f);
        using var ctx = Host(f);

        var markup = Render<Decisions>(ctx);

        Assert.DoesNotContain("gate-notice-error", markup);
        Assert.Contains("Approve split award", markup);
        Assert.Contains("CinderVolt Energy Ltd", markup);
        Assert.Contains("Meridian Cellworks Inc", markup);
        Assert.Contains("Final shares", markup);
        Assert.Contains("China 57.1%", markup);
        Assert.Contains("Mexico 39.3%", markup);
        Assert.Contains("Split total", markup);
        Assert.Contains("$13,600.00", markup);
        Assert.Contains("Single award", markup);
        Assert.Contains("$14,400.00", markup);
        Assert.Contains("Saving", markup);
        Assert.Contains("$800.00", markup);
    }

    [Fact]
    public async Task ParentShowsChildrenAndChildrenLinkBack()
    {
        var f = await SplitFlowAsync();
        var split = await CardUpAsync(f);
        await f.Runner.ResolveDecisionAsync(f.Battery, split.DecisionId, true, split.RequestedTo, "ok");
        f.Models.QueueToolCalls("create children",
            ("c1", AgentTools.CreateSplitChildren, Args(f.Battery)));
        f.Models.QueueText("fan-out child submit A");
        f.Models.QueueText("fan-out child submit B");
        await f.Runner.AdvanceAsync(f.Battery);
        var parent = CaseStore.Load(f.CaseDir, f.Battery)!;
        Assert.Equal(CaseStage.SplitOrdered, parent.Case.Stage);
        using var ctx = Host(f);

        var markup = RenderDetail(ctx, f.Battery);

        Assert.DoesNotContain("gate-notice-error", markup);
        Assert.Contains("Split orders", markup);
        Assert.Contains("CinderVolt Energy Ltd", markup);
        Assert.Contains("Meridian Cellworks Inc", markup);
        Assert.Contains("800", markup);
        Assert.Contains("400", markup);
        var children = new List<string>();
        foreach (var id in CaseStore.ListCaseIds(f.CaseDir))
        {
            var file = CaseStore.Load(f.CaseDir, id);
            if (file is not null
                && string.Equals(file.Case.ParentCaseId, f.Battery, StringComparison.Ordinal))
            {
                children.Add(id);
            }
        }

        Assert.Equal(2, children.Count);
        foreach (var childId in children)
        {
            Assert.Contains(childId, markup);
            var child = CaseStore.Load(f.CaseDir, childId)!;
            Assert.Contains(child.Case.PoNumber!, markup);
            // The parent's advance fans out: each child submits its own
            // PO and raises its own approval card with no manual advance.
            var card = Assert.Single(child.Decisions, d =>
                d.Kind == DecisionKind.PoApproval && d.Status == DecisionStatus.Pending);
            Assert.Contains(child.Case.PoNumber!, card.Title, StringComparison.Ordinal);
            var childMarkup = RenderDetail(ctx, childId);
            Assert.DoesNotContain("gate-notice-error", childMarkup);
            Assert.Contains("Split child of", childMarkup);
            Assert.Contains(f.Battery, childMarkup);
            Assert.Contains(child.Case.PoNumber!, childMarkup);
        }
    }

    [Fact]
    public async Task SplitChildrenSubmitAndUnlockSupplierPortal()
    {
        var f = await SplitFlowAsync();
        var split = await CardUpAsync(f);
        var outcomes = new DecisionOutcomeService(
            new CaseCacheStore(f.CaseDir),
            UiTestLogger<DecisionOutcomeService>.Instance, f.Runner);
        f.Models.QueueToolCalls("create children",
            ("c1", AgentTools.CreateSplitChildren, Args(f.Battery)));
        f.Models.QueueText("fan-out child submit A");
        f.Models.QueueText("fan-out child submit B");
        var approved = await outcomes.ApplyAsync(
            f.Battery, split.DecisionId, true, null, split.RequestedTo);
        Assert.True(approved.Ok);
        Assert.False(approved.AdvanceFailed);

        var children = new List<CaseFile>();
        foreach (var id in CaseStore.ListCaseIds(f.CaseDir))
        {
            var file = CaseStore.Load(f.CaseDir, id);
            if (file is not null
                && string.Equals(file.Case.ParentCaseId, f.Battery, StringComparison.Ordinal))
            {
                children.Add(file);
            }
        }

        Assert.Equal(2, children.Count);
        foreach (var child in children)
        {
            Assert.NotNull(child.Case.PoNumber);
            var card = Assert.Single(child.Decisions, d =>
                d.Kind == DecisionKind.PoApproval && d.Status == DecisionStatus.Pending);
            using var voter = Host(f, card.RequestedTo);
            var ballot = Render<Decisions>(voter);
            Assert.DoesNotContain("gate-notice-error", ballot);
            Assert.Contains($"Approve PO {child.Case.PoNumber}", ballot);
        }

        // Approving on the Decisions path unlocks the supplier side.
        foreach (var child in children)
        {
            var card = child.Decisions.Single(d =>
                d.Kind == DecisionKind.PoApproval && d.Status == DecisionStatus.Pending);
            var done = await outcomes.ApplyAsync(
                child.Case.CaseId, card.DecisionId, true, null, card.RequestedTo);
            Assert.True(done.Ok);
            Assert.False(done.AdvanceFailed);
            var po = await f.Erp.GetPurchaseOrderAsync(child.Case.PoNumber!);
            Assert.NotNull(po);
            Assert.Equal(PoApprovalStatus.Approved, po.ApprovalStatus);
        }

        using var ctx = Host(f);
        var simulate = ctx.Services.GetRequiredService<SimulationUiService>();
        var options = await simulate.GetOptionsAsync();
        foreach (var child in children)
        {
            var inbox = await simulate.GetSupplierInboxForPoAsync(child.Case.PoNumber!);
            Assert.NotNull(inbox);
            Assert.Equal(SupplierNextAction.Acknowledge, inbox.NextAction);
            var vendor = (await f.Erp.GetPurchaseOrderAsync(child.Case.PoNumber!))!.VendorId;
            using var portal = ctx.RenderComponent<SupplierPortal>(p => p
                .Add(s => s.VendorId, vendor)
                .Add(s => s.Options, options));
            portal.WaitForAssertion(
                () => Assert.DoesNotContain("Loading", portal.Markup),
                TimeSpan.FromSeconds(10));
            Assert.DoesNotContain("gate-notice-error", portal.Markup);
            Assert.DoesNotContain("Waiting on buyer approval", portal.Markup);
            Assert.Contains("Acknowledge", portal.Markup);
        }

        var outside = Render<Outside>(ctx);
        Assert.DoesNotContain("gate-notice-error", outside);

        var activity = ctx.Services.GetRequiredService<AgentActivityService>();
        var feed = await activity.BuildAsync();
        Assert.DoesNotContain(feed.Waiting, w => w.Text.Contains(
            "Submitting the approval", StringComparison.Ordinal));
        using var pane = ctx.RenderComponent<AgentActivityPane>();
        pane.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", pane.Markup),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("Submitting the approval", pane.Markup);
    }
}

// Split-view tests: the pending split card shows each line, the final
// country shares, and the saving; the parent page lists both children
// with their orders and stages; each child links back to the parent.
// Real flows through the runner, rendered pages, local data only.
using Bunit;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Eval.VideoProbes;
using Gate.Web.Components.Pages;
using Gate.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

public sealed class SplitViewsTests
{
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

    private static TestContext Host(Flow f)
    {
        var dir = UiTestSupport.NewDir("gate-split-host");
        var imgDir = Path.Combine(dir, "img");
        Directory.CreateDirectory(imgDir);
        var cases = new CaseCacheStore(f.CaseDir);
        var views = new CaseViewService(
            f.Erp, cases, new DockPhotoService(imgDir, Path.Combine(dir, "reads")),
            UiTestLogger<CaseViewService>.Instance,
            countries: new CountryNames(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["CN"] = "China",
                ["MX"] = "Mexico",
            }));
        var acting = new ActingUserState();
        acting.SetCurrent("delegate");
        var ctx = new TestContext();
        ctx.Services.AddSingleton(views);
        ctx.Services.AddSingleton(new DecisionOutcomeService(
            cases, UiTestLogger<DecisionOutcomeService>.Instance, f.Runner));
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
            var childMarkup = RenderDetail(ctx, childId);
            Assert.DoesNotContain("gate-notice-error", childMarkup);
            Assert.Contains("Split child of", childMarkup);
            Assert.Contains(f.Battery, childMarkup);
        }
    }
}

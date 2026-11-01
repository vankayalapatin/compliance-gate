// Reset plus explicit replay behind the top-bar controls: progress
// reports each step as it lands, the report carries the full result,
// Reset never touches replay mode (Live stays Live), replay switches
// only through its explicit toggle, and the whole run is local
// (scenario reload, cache clear, cache counts, spend readout — zero live
// calls). Service tests prove the run; component tests prove the top bar
// asks to confirm before any reset and toggles replay explicitly. Paths
// arrive via in-memory configuration only — this class never touches
// GATE_MANIFEST / GATE_SCENARIO (see RecordsViewTests). No live calls.
using Bunit;
using Gate.Erp;
using Gate.Web.Components;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

public sealed class DemoReplayTests
{
    private const string ScenarioJson = """
        {"name":"ui-demo","currency":"TST",
         "salesOrder":{"item":"ITEM-D","quantity":10},
         "bom":[],"stock":[],
         "openPoInbound":{"item":"ITEM-D","quantity":5,"vendor":"V-D"},
         "items":[{"id":"ITEM-D","description":"Demo item","critical":false,"packSize":1,"unit":"EA"}],
         "paymentTerms":[{"id":"NET0","netDays":0}],
         "vendors":[{"id":"V-D","name":"Demo Vendor","country":"US","role":"test",
          "item":"ITEM-D","unitPrice":10,"terms":"NET0","bankAccount":"BANK-D",
          "documents":[],"email":"d@test.example"}],
         "historyPart":"ITEM-D","historyCells":[],
         "users":[{"name":"requester","role":"requester","signingLimit":50},
          {"name":"approver","role":"approver","signingLimit":1000000}],
         "delegations":[],"autoApproveUnder":10,
         "cartonPlan":{"unitsPerCarton":5,"cartons":2},
         "smallPos":[]}
        """;

    private static (ReplayService Service, ReplayState State, ModelSpendTracker Spend, string ManifestPath) Create()
    {
        var dir = UiTestSupport.NewDir("gate-ui-demo");
        Directory.CreateDirectory(dir);
        var scenarioPath = Path.Combine(dir, "scenario.json");
        var manifestPath = Path.Combine(dir, "run-manifest.json");
        File.WriteAllText(scenarioPath, ScenarioJson);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scenario:File"] = scenarioPath,
                ["Scenario:Sqlite"] = Path.Combine(dir, "gate.db"),
                ["Scenario:Manifest"] = manifestPath,
            })
            .Build();
        var replay = new ReplayState();
        var spend = new ModelSpendTracker();
        var service = new ReplayService(
            new InMemoryErpAdapter(),
            new CaseCacheStore(Path.Combine(dir, "case-cache")),
            new DockPhotoService(Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
            replay, config, UiTestLogger<ReplayService>.Instance, null, spend);
        return (service, replay, spend, manifestPath);
    }

    private sealed class SyncProgress : IProgress<string>
    {
        public readonly List<string> Seen = [];

        public void Report(string value) => Seen.Add(value);
    }

    [Fact]
    public async Task ResetReportsProgressThenResult()
    {
        var (service, replay, _, manifestPath) = Create();
        var progress = new SyncProgress();

        var report = await service.ResetAsync(progress);

        Assert.Null(report.Error);
        Assert.False(report.IsReplay);
        Assert.False(replay.IsReplay);
        Assert.Equal(4, report.Steps.Count);
        Assert.Equal(report.Steps, progress.Seen);
        Assert.Contains(report.Steps, s => s.Contains("Spend so far"));
        Assert.True(File.Exists(manifestPath));
    }

    [Fact]
    public async Task ResetWorksWithoutProgress()
    {
        var (service, _, _, _) = Create();

        var report = await service.ResetAsync();

        Assert.Null(report.Error);
        Assert.False(report.IsReplay);
        Assert.NotEmpty(report.Steps);
    }

    [Fact]
    public async Task ResetNeverTouchesReplayMode()
    {
        var (service, replay, _, _) = Create();

        // Live stays Live.
        var live = await service.ResetAsync();
        Assert.False(live.IsReplay);
        Assert.False(replay.IsReplay);

        // Replay stays replay: only the explicit toggle switches modes.
        service.EnterReplay();
        var kept = await service.ResetAsync();
        Assert.True(kept.IsReplay);
        Assert.True(replay.IsReplay);
    }

    [Fact]
    public void ReplayToggleIsExplicit()
    {
        var (service, replay, _, _) = Create();

        Assert.False(replay.IsReplay);
        service.EnterReplay();
        Assert.True(replay.IsReplay);
        service.EnterReplay();
        Assert.True(replay.IsReplay);
        service.ExitReplay();
        Assert.False(replay.IsReplay);
        service.ExitReplay();
        Assert.False(replay.IsReplay);
    }

    [Fact]
    public void TopBar_ResetAsksToConfirmFirst()
    {
        var (service, replay, spend, _) = Create();
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);
        ctx.Services.AddSingleton(replay);
        ctx.Services.AddSingleton(spend);
        ctx.Services.AddSingleton<ILogger<ReplayControls>>(UiTestLogger<ReplayControls>.Instance);

        using var page = ctx.RenderComponent<ReplayControls>();
        Assert.Contains("Live", page.Markup);
        Assert.Contains("spend unmetered", page.Markup);

        page.Find("button").Click();
        Assert.Contains("Confirm reset", page.Markup);
        Assert.False(replay.IsReplay);

        page.FindAll("button")
            .First(b => b.TextContent.Contains("Cancel", StringComparison.Ordinal))
            .Click();
        Assert.DoesNotContain("Confirm reset", page.Markup);
        Assert.False(replay.IsReplay);
    }

    [Fact]
    public void TopBar_ConfirmRunsResetAndStaysLive()
    {
        var (service, replay, spend, _) = Create();
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);
        ctx.Services.AddSingleton(replay);
        ctx.Services.AddSingleton(spend);
        ctx.Services.AddSingleton<ILogger<ReplayControls>>(UiTestLogger<ReplayControls>.Instance);

        using var page = ctx.RenderComponent<ReplayControls>();
        page.Find("button").Click();
        page.FindAll("button")
            .First(b => b.TextContent.Contains("Confirm reset", StringComparison.Ordinal))
            .Click();

        page.WaitForAssertion(
            () => Assert.Contains("Reset done", page.Markup),
            TimeSpan.FromSeconds(30));
        Assert.False(replay.IsReplay);
        Assert.Contains("Live", page.Markup);
        Assert.DoesNotContain(">Replay<", page.Markup);
    }

    [Fact]
    public void TopBar_ReplayToggleSwitchesModeExplicitly()
    {
        var (service, replay, spend, _) = Create();
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);
        ctx.Services.AddSingleton(replay);
        ctx.Services.AddSingleton(spend);
        ctx.Services.AddSingleton<ILogger<ReplayControls>>(UiTestLogger<ReplayControls>.Instance);

        using var page = ctx.RenderComponent<ReplayControls>();
        page.FindAll("button")
            .First(b => b.TextContent.Contains("Enter replay", StringComparison.Ordinal))
            .Click();

        Assert.True(replay.IsReplay);
        Assert.Contains("Replay on", page.Markup);

        page.FindAll("button")
            .First(b => b.TextContent.Contains("Exit replay", StringComparison.Ordinal))
            .Click();

        Assert.False(replay.IsReplay);
        Assert.Contains("Live", page.Markup);
    }

    [Fact]
    public void TopBar_ShowsMeteredSpendReadout()
    {
        var (service, replay, _, _) = Create();
        var spend = new ModelSpendTracker(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ModelSpend:CostPerCallUsd"] = "0.50",
                ["ModelSpend:CapUsd"] = "10",
            })
            .Build());
        spend.RecordLiveCall("spark");
        spend.RecordLiveCall("spark");
        spend.RecordLiveCall("spark");
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);
        ctx.Services.AddSingleton(replay);
        ctx.Services.AddSingleton(spend);
        ctx.Services.AddSingleton<ILogger<ReplayControls>>(UiTestLogger<ReplayControls>.Instance);

        using var page = ctx.RenderComponent<ReplayControls>();

        Assert.Contains("$1.50 of $10.00", page.Markup);
    }
}

// Restore plus explicit offline answers behind the Admin-page controls:
// progress reports each step as it lands, the report carries the full
// result, restore copies the snapshot pair over the live stores (wiping
// the workspace first), restore never touches the answers mode (live
// answers stay live), answers switch only through the explicit toggle,
// and the whole run is local (file copy, cache counts, spend readout —
// zero live calls). Service tests prove the run; component tests prove
// the Admin page asks to confirm before any restore and toggles answers
// explicitly. No live calls.
using Bunit;
using Gate.Web.Components;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

[Collection(RestoreEnvCollection.Name)]
public sealed class DemoReplayTests
{
    /// <summary>Points dock-dir resolution at a temp dir for the restore
    /// under test: without it the restore would wipe the real image
    /// cache. Shares a collection with the other restore tests so two
    /// restores never interleave on the process env.</summary>
    private sealed class DockImagesDir : IDisposable
    {
        private readonly string? _previous;

        public DockImagesDir(string dir)
        {
            _previous = Environment.GetEnvironmentVariable("GATE_DOCK_IMAGES");
            Environment.SetEnvironmentVariable("GATE_DOCK_IMAGES", dir);
        }

        public void Dispose() =>
            Environment.SetEnvironmentVariable("GATE_DOCK_IMAGES", _previous);
    }

    private static (ReplayService Service, ReplayState State, ModelSpendTracker Spend, string Dir) Create()
    {
        var dir = UiTestSupport.NewDir("gate-ui-demo");
        Directory.CreateDirectory(dir);
        var baseline = Path.Combine(dir, "baseline");
        Directory.CreateDirectory(Path.Combine(baseline, "platform"));
        File.WriteAllBytes(Path.Combine(baseline, "erp.db"), [7, 7, 7]);
        File.WriteAllText(Path.Combine(baseline, "platform", "CASE-T.json"), """{"saved":true}""");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Baseline:Dir"] = baseline,
                ["Erp:Sqlite"] = Path.Combine(dir, "live", "erp.db"),
            })
            .Build();
        var replay = new ReplayState();
        var spend = new ModelSpendTracker();
        var service = new ReplayService(
            new CaseCacheStore(Path.Combine(dir, "case-cache")),
            new DockPhotoService(Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
            replay, config, UiTestLogger<ReplayService>.Instance, dir, spend);
        return (service, replay, spend, dir);
    }

    private sealed class SyncProgress : IProgress<string>
    {
        public readonly List<string> Seen = [];

        public void Report(string value) => Seen.Add(value);
    }

    [Fact]
    public async Task RestoreReportsProgressThenResult()
    {
        var (service, replay, _, dir) = Create();
        var progress = new SyncProgress();
        Directory.CreateDirectory(Path.Combine(dir, "case-cache"));
        await File.WriteAllTextAsync(Path.Combine(dir, "case-cache", "STALE.json"), "{}");

        using var dock = new DockImagesDir(Path.Combine(dir, "dock"));
        var report = await service.RestoreAsync(progress);

        Assert.Null(report.Error);
        Assert.False(report.IsReplay);
        Assert.False(replay.IsReplay);
        Assert.Equal(7, report.Steps.Count);
        Assert.Contains(report.Steps, s => s.Contains("Dock counter cleared", StringComparison.Ordinal));
        Assert.Contains(report.Steps, s => s.Contains("Published rule packs cleared", StringComparison.Ordinal));
        Assert.Contains(report.Steps, s => s.Contains("Memo inbox cleared", StringComparison.Ordinal));
        Assert.Equal(report.Steps, progress.Seen);
        Assert.Contains(report.Steps, s => s.Contains("Spend so far"));
        Assert.Equal(new byte[] {7, 7, 7}, await File.ReadAllBytesAsync(Path.Combine(dir, "live", "erp.db")));
        Assert.True(File.Exists(Path.Combine(dir, "case-cache", "CASE-T.json")));
        Assert.False(File.Exists(Path.Combine(dir, "case-cache", "STALE.json")));
    }

    [Fact]
    public async Task RestoreWorksWithoutProgress()
    {
        var (service, _, _, dir) = Create();
        using var dock = new DockImagesDir(Path.Combine(dir, "dock"));

        var report = await service.RestoreAsync();

        Assert.Null(report.Error);
        Assert.False(report.IsReplay);
        Assert.NotEmpty(report.Steps);
    }

    [Fact]
    public async Task RestoreFailsLoudlyWithoutSnapshot()
    {
        var (service, _, _, dir) = Create();
        Directory.Delete(Path.Combine(dir, "baseline"), recursive: true);

        var report = await service.RestoreAsync();

        Assert.NotNull(report.Error);
        Assert.Contains("Saved opening data not found", report.Error);
    }

    [Fact]
    public async Task RestoreNeverTouchesAnswersMode()
    {
        var (service, replay, _, dir) = Create();
        using var dock = new DockImagesDir(Path.Combine(dir, "dock"));

        // Live answers stay live.
        var live = await service.RestoreAsync();
        Assert.False(live.IsReplay);
        Assert.False(replay.IsReplay);

        // Offline answers stay offline: only the explicit toggle switches.
        service.EnterReplay();
        var kept = await service.RestoreAsync();
        Assert.True(kept.IsReplay);
        Assert.True(replay.IsReplay);
    }

    [Fact]
    public void AnswersToggleIsExplicit()
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
    public void Admin_RestoreAsksToConfirmFirst()
    {
        var (service, replay, spend, _) = Create();
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);
        ctx.Services.AddSingleton(replay);
        ctx.Services.AddSingleton(spend);
        ctx.Services.AddSingleton<ILogger<ReplayControls>>(UiTestLogger<ReplayControls>.Instance);

        using var page = ctx.RenderComponent<ReplayControls>();
        Assert.Contains("Live answers", page.Markup);
        Assert.Contains("spend unmetered", page.Markup);

        page.FindAll("button")
            .First(b => b.TextContent.Contains("Restore saved data", StringComparison.Ordinal))
            .Click();
        Assert.Contains("Confirm restore", page.Markup);
        Assert.False(replay.IsReplay);

        page.FindAll("button")
            .First(b => b.TextContent.Contains("Cancel", StringComparison.Ordinal))
            .Click();
        Assert.DoesNotContain("Confirm restore", page.Markup);
        Assert.False(replay.IsReplay);
    }

    [Fact]
    public void Admin_ConfirmRunsRestoreAndStaysLive()
    {
        var (service, replay, spend, _) = Create();
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);
        ctx.Services.AddSingleton(replay);
        ctx.Services.AddSingleton(spend);
        ctx.Services.AddSingleton<ILogger<ReplayControls>>(UiTestLogger<ReplayControls>.Instance);

        using var page = ctx.RenderComponent<ReplayControls>();
        page.FindAll("button")
            .First(b => b.TextContent.Contains("Restore saved data", StringComparison.Ordinal))
            .Click();
        page.FindAll("button")
            .First(b => b.TextContent.Contains("Confirm restore", StringComparison.Ordinal))
            .Click();

        page.WaitForAssertion(
            () => Assert.Contains("Restore done", page.Markup),
            TimeSpan.FromSeconds(30));
        Assert.False(replay.IsReplay);
        Assert.Contains("Live answers", page.Markup);
        Assert.DoesNotContain(">Offline answers<", page.Markup);
    }

    [Fact]
    public void Admin_AnswersToggleSwitchesModeExplicitly()
    {
        var (service, replay, spend, _) = Create();
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);
        ctx.Services.AddSingleton(replay);
        ctx.Services.AddSingleton(spend);
        ctx.Services.AddSingleton<ILogger<ReplayControls>>(UiTestLogger<ReplayControls>.Instance);

        using var page = ctx.RenderComponent<ReplayControls>();
        page.FindAll("button")
            .First(b => b.TextContent.Contains("Use offline answers", StringComparison.Ordinal))
            .Click();

        Assert.True(replay.IsReplay);
        Assert.Contains("Offline answers on", page.Markup);

        page.FindAll("button")
            .First(b => b.TextContent.Contains("Use live answers", StringComparison.Ordinal))
            .Click();

        Assert.False(replay.IsReplay);
        Assert.Contains("Live answers", page.Markup);
    }

    [Fact]
    public void Admin_ShowsMeteredSpendReadout()
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

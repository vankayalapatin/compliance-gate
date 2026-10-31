// Demo reset-plus-replay: progress reports each step as it lands, the
// report carries the full result, replay mode pins on, and the whole run
// is local (scenario reload, cache clear, cache counts — zero live calls).
// Paths arrive via in-memory configuration only — this class never touches
// GATE_MANIFEST / GATE_SCENARIO (see RecordsViewTests). No live calls.
using Gate.Erp;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;

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

    private static (ReplayService Service, ReplayState State, string ManifestPath) Create()
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
        var service = new ReplayService(
            new InMemoryErpAdapter(),
            new CaseCacheStore(Path.Combine(dir, "case-cache")),
            new DockPhotoService(Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
            replay, config, UiTestLogger<ReplayService>.Instance);
        return (service, replay, manifestPath);
    }

    private sealed class SyncProgress : IProgress<string>
    {
        public readonly List<string> Seen = [];

        public void Report(string value) => Seen.Add(value);
    }

    [Fact]
    public async Task ResetReportsProgressThenResult()
    {
        var (service, replay, manifestPath) = Create();
        var progress = new SyncProgress();

        var report = await service.ResetAndReplayAsync(progress);

        Assert.Null(report.Error);
        Assert.True(report.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(4, report.Steps.Count);
        Assert.Equal(report.Steps, progress.Seen);
        Assert.True(File.Exists(manifestPath));
    }

    [Fact]
    public async Task ResetWorksWithoutProgress()
    {
        var (service, _, _) = Create();

        var report = await service.ResetAndReplayAsync();

        Assert.Null(report.Error);
        Assert.True(report.IsReplay);
        Assert.NotEmpty(report.Steps);
    }
}

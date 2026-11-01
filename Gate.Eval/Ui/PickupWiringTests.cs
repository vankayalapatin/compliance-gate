// ERP-work pickup wiring: reset adopts actionable ERP documents into live
// cases with zero live calls, the pickup service posts the explicit
// /agent/pickup shape, and a blank scenario is refused before any POST.
// Temp scenario, manifest, ERP, and cache only — never the demo numbers.
using System.Net;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class PickupWiringTests
{
    private const string ScenarioJson = """
        {"name":"ui-pickup","currency":"TST",
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

    private sealed record ResetHarness(
        ReplayService Replay, CaseCacheStore Store, InMemoryErpAdapter Erp, FakeModelClient Models);

    private static ResetHarness CreateReset()
    {
        var dir = UiTestSupport.NewDir("gate-ui-pickup");
        Directory.CreateDirectory(dir);
        var scenarioPath = Path.Combine(dir, "scenario.json");
        File.WriteAllText(scenarioPath, ScenarioJson);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scenario:File"] = scenarioPath,
                ["Scenario:Sqlite"] = Path.Combine(dir, "gate.db"),
                ["Scenario:Manifest"] = Path.Combine(dir, "run-manifest.json"),
            })
            .Build();
        var erp = new InMemoryErpAdapter();
        var cacheDir = Path.Combine(dir, "case-cache");
        var store = new CaseCacheStore(cacheDir);
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models, compliance,
            new DockEvaluatorAdapter(), cacheDir);
        var replay = new ReplayService(
            erp, store,
            new DockPhotoService(Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
            new ReplayState(), config, UiTestLogger<ReplayService>.Instance,
            runner, new ModelSpendTracker());
        return new ResetHarness(replay, store, erp, models);
    }

    [Fact]
    public async Task ResetAdoptsInboundPoIntoLiveCase()
    {
        var h = CreateReset();

        var report = await h.Replay.ResetAsync();

        Assert.Null(report.Error);
        Assert.False(report.IsReplay);
        Assert.Contains(report.Steps, s => s.Contains("ERP-work case(s) picked up"));
        var adoptable = (await h.Erp.GetPurchaseOrdersAsync())
            .Where(p => p.ApprovalStatus is PoApprovalStatus.Approved or PoApprovalStatus.Confirmed
                && p.DocStatus is not PoDocStatus.Closed and not PoDocStatus.Cancelled)
            .ToList();
        Assert.NotEmpty(adoptable);
        var files = await h.Store.ListAsync();
        foreach (var po in adoptable)
        {
            Assert.Contains(files, f => !f.Case.IsOpeningData && f.Case.PoNumber == po.PoNumber);
        }

        Assert.Equal(0, h.Models.CallCount);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int CallCount;
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public string Json = """{"touched":[],"count":0}""";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (SimulationUiService Service, StubHandler Http) CreateService(string json)
    {
        var dir = UiTestSupport.NewDir("gate-ui-pickup-svc");
        Directory.CreateDirectory(Path.Combine(dir, "dock"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var stub = new StubHandler { Json = json };
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://localhost/") };
        var service = new SimulationUiService(
            http, new InMemoryErpAdapter(), new CaseCacheStore(Path.Combine(dir, "case-cache")),
            new DockPhotoService(Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
            config, UiTestLogger<SimulationUiService>.Instance);
        return (service, stub);
    }

    [Fact]
    public async Task PickupServicePostsExplicitEndpointShape()
    {
        var (service, stub) = CreateService("""{"touched":["CASE-A","CASE-B"],"count":2}""");

        var outcome = await service.PickUpErpWorkAsync("ui-pickup");

        Assert.True(outcome.Ok, outcome.Summary);
        Assert.Equal(new[] { "CASE-A", "CASE-B" }, outcome.CaseIds);
        Assert.Equal(1, stub.CallCount);
        Assert.Equal(HttpMethod.Post, stub.LastRequest!.Method);
        Assert.Equal(SimulationUiService.PickupPath, stub.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("ui-pickup", stub.LastBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickupServiceRefusesBlankScenario()
    {
        var (service, stub) = CreateService("""{"touched":[],"count":0}""");

        var outcome = await service.PickUpErpWorkAsync("  ");

        Assert.False(outcome.Ok);
        Assert.Equal(0, stub.CallCount);
    }
}

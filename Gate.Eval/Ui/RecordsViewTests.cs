// Records plus case-detail view models against the in-memory fake ERP:
// ledgers stay plain, agent Source flows through for the banner, and
// missing documents surface as notes. This is the only test class that
// touches GATE_MANIFEST / GATE_SCENARIO, so no parallel test fights it.
// No live calls.
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class RecordsViewTests : IDisposable
{
    private readonly string? _savedManifest = Environment.GetEnvironmentVariable("GATE_MANIFEST");
    private readonly string? _savedScenario = Environment.GetEnvironmentVariable("GATE_SCENARIO");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GATE_MANIFEST", _savedManifest);
        Environment.SetEnvironmentVariable("GATE_SCENARIO", _savedScenario);
    }

    private static CaseViewService Service(InMemoryErpAdapter erp, CaseCacheStore store) =>
        new(erp, store,
            new DockPhotoService(UiTestSupport.NewDir("gate-ui-dock"), UiTestSupport.NewDir("gate-ui-vision")),
            new ConfigurationBuilder().Build(),
            UiTestLogger<CaseViewService>.Instance);

    [Fact]
    public async Task EmptyCacheYieldsEmptyLedgersWithNotes()
    {
        Environment.SetEnvironmentVariable("GATE_MANIFEST", Path.Combine(UiTestSupport.NewDir("gate-ui-x"), "missing.json"));
        Environment.SetEnvironmentVariable("GATE_SCENARIO", Path.Combine(UiTestSupport.NewDir("gate-ui-x"), "missing.json"));
        var service = Service(new InMemoryErpAdapter(), new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache")));

        var records = await service.GetRecordsAsync();

        Assert.Empty(records.SalesOrders);
        Assert.Empty(records.PurchaseRequisitions);
        Assert.Empty(records.PurchaseOrders);
        Assert.Empty(records.Invoices);
        Assert.Empty(records.Payments);
        Assert.NotEmpty(records.Notes);
        Assert.Empty(await service.ListDecisionCardsAsync());
        Assert.Null(await service.GetCaseDetailAsync("CASE-MISSING"));
    }

    [Fact]
    public async Task CaseRefsDriveRecordFetches()
    {
        Environment.SetEnvironmentVariable("GATE_MANIFEST", Path.Combine(UiTestSupport.NewDir("gate-ui-x"), "missing.json"));
        Environment.SetEnvironmentVariable("GATE_SCENARIO", Path.Combine(UiTestSupport.NewDir("gate-ui-x"), "missing.json"));
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("TST");
        erp.SeedAutoApproveThreshold(1_000_000m);
        erp.SeedVendorPrice("V-1", "ITEM-1", 10m);
        erp.SeedSalesOrder(new SalesOrder("SO-1", SalesOrderStatus.Created, "TST", [new SalesOrderLine("ITEM-1", 5m)]));
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.UtcNow;
        var kase = new Case(
            "CASE-1", "SO-1", "ITEM-1", "ui-test", CaseStage.PrSubmitted, now, now,
            pr.PrNumber, null, null, null, [], [], "RULE-1", "waiting on approver");
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(new CaseFile(kase, [], [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber),
        ]));
        var service = Service(erp, store);

        var records = await service.GetRecordsAsync();

        Assert.Single(records.SalesOrders);
        var fetched = Assert.Single(records.PurchaseRequisitions);
        Assert.Equal(RecordSource.P2pAgent, fetched.Source);
        Assert.Contains(records.Inventory, i => i.ItemId == "ITEM-1");

        var cards = await service.ListDecisionCardsAsync();
        var card = Assert.Single(cards);
        Assert.Equal("SO-1", card.SoNumber);
        Assert.Contains(pr.PrNumber, card.ErpSummary ?? string.Empty);

        var detail = await service.GetCaseDetailAsync("CASE-1");
        Assert.NotNull(detail);
        Assert.Equal(CaseStage.PrSubmitted, detail.Case.Stage);
        Assert.Contains(detail.Chain, s => s.Label == "Sales order" && s.DocumentNumber == "SO-1");
    }

    [Fact]
    public async Task ManifestDocumentsAreRead()
    {
        var dir = UiTestSupport.NewDir("gate-ui-manifest");
        var manifestPath = Path.Combine(dir, "run-manifest.json");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(manifestPath,
            """{"scenario":"ui-test","documents":[{"event":"salesOrder","docType":"SO","number":"SO-9"}]}""");
        Environment.SetEnvironmentVariable("GATE_MANIFEST", manifestPath);
        Environment.SetEnvironmentVariable("GATE_SCENARIO", Path.Combine(dir, "missing.json"));
        var erp = new InMemoryErpAdapter();
        erp.SeedSalesOrder(new SalesOrder("SO-9", SalesOrderStatus.Created, "TST", [new SalesOrderLine("ITEM-9", 1m)]));
        var service = Service(erp, new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache")));

        var records = await service.GetRecordsAsync();

        Assert.Contains(records.SalesOrders, so => so.SoNumber == "SO-9");
    }

    [Fact]
    public async Task MissingDocSurfacesNote()
    {
        Environment.SetEnvironmentVariable("GATE_MANIFEST", Path.Combine(UiTestSupport.NewDir("gate-ui-x"), "missing.json"));
        Environment.SetEnvironmentVariable("GATE_SCENARIO", Path.Combine(UiTestSupport.NewDir("gate-ui-x"), "missing.json"));
        var now = DateTimeOffset.UtcNow;
        var kase = new Case(
            "CASE-1", "SO-GONE", "ITEM-1", "ui-test", CaseStage.Created, now, now,
            "PR-GONE", null, null, null, [], [], null, null);
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(new CaseFile(kase, [], []));
        var service = Service(new InMemoryErpAdapter(), store);

        var records = await service.GetRecordsAsync();

        Assert.Empty(records.SalesOrders);
        Assert.Contains(records.Notes, n => n.Contains("SO-GONE"));
        Assert.Contains(records.Notes, n => n.Contains("PR-GONE"));
    }
}

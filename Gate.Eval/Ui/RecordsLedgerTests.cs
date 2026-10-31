// Records ledger plus case-detail service shape: manifest numbers tracked
// as opening data (seeded invoices resolve through the scenario small-PO
// map), no "no reader" notes, the detail chain marks its current step, and
// every detail carries the agent summary. Paths arrive via in-memory
// configuration only — this class never touches GATE_MANIFEST /
// GATE_SCENARIO (see RecordsViewTests). No live calls.
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval.Ui;

public sealed class RecordsLedgerTests
{
    private static string WriteFiles(string manifestJson, string scenarioJson)
    {
        var dir = UiTestSupport.NewDir("gate-ui-ledger");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "scenario.json"), scenarioJson);
        File.WriteAllText(Path.Combine(dir, "run-manifest.json"), manifestJson);
        return dir;
    }

    private static CaseViewService Service(InMemoryErpAdapter erp, CaseCacheStore store, string dir) =>
        new(erp, store,
            new DockPhotoService(
                Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Scenario:File"] = Path.Combine(dir, "scenario.json"),
                    ["Scenario:Manifest"] = Path.Combine(dir, "run-manifest.json"),
                })
                .Build(),
            UiTestLogger<CaseViewService>.Instance);

    private static async Task<(string PrNumber, string PoNumber, string PoLineRef, decimal UnitPrice)> SeedPoAsync(
        InMemoryErpAdapter erp)
    {
        erp.SeedBaseCurrency("TST");
        erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        erp.SeedVendor(new VendorDto("V-T", "Test Vendor", "US", false, null, "NET0"));
        erp.SeedVendorPrice("V-T", "ITEM-T", 10m);
        erp.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        erp.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));
        var pr = await erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-T", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        var po = await erp.CreatePoAsync(
            pr.PrNumber, "V-T", RecordSource.P2pAgent, "requester", Guid.NewGuid().ToString("N"));
        var line = po.Lines.Single();
        return (pr.PrNumber, po.PoNumber, line.PoLineRef, line.UnitPrice);
    }

    [Fact]
    public async Task OpeningNumbersTrackEveryManifestDoc()
    {
        var erp = new InMemoryErpAdapter();
        var (prNumber, poNumber, poLineRef, unitPrice) = await SeedPoAsync(erp);
        erp.SeedSalesOrder(new SalesOrder("SO-T", SalesOrderStatus.Created, "TST",
            [new SalesOrderLine("ITEM-T", 5m)]));
        await erp.CreateInvoiceAsync("SEED-INV-T1", "V-T", poNumber, RecordSource.Erp, "requester",
            [new InvoiceLineInput(poLineRef, 5m, unitPrice)], Guid.NewGuid().ToString("N"));
        var dir = WriteFiles(
            """{"scenario":"ui-ledger","documents":[{"event":"salesOrder","docType":"SO","number":"SO-T"},"""
            + $$"""{"event":"openPoInbound.pr","docType":"PR","number":"{{prNumber}}"},"""
            + $$"""{"event":"openPoInbound.po","docType":"PO","number":"{{poNumber}}"},"""
            + """{"event":"smallPo.k1.invoice","docType":"INV","number":"SEED-INV-T1"}]}""",
            """{"smallPos":[{"key":"k1","item":"ITEM-T","quantity":5,"vendor":"V-T","unitPrice":10}]}""");
        var service = Service(erp, new CaseCacheStore(Path.Combine(dir, "cache")), dir);

        var records = await service.GetRecordsAsync();

        Assert.Contains("SO-T", records.OpeningNumbers);
        Assert.Contains(prNumber, records.OpeningNumbers);
        Assert.Contains(poNumber, records.OpeningNumbers);
        Assert.Contains("SEED-INV-T1", records.OpeningNumbers);
        Assert.DoesNotContain(records.Notes, n => n.Contains("no reader"));
    }

    [Fact]
    public async Task SeedInvoicesResolveThroughSmallPos()
    {
        var erp = new InMemoryErpAdapter();
        var (_, poNumber, poLineRef, unitPrice) = await SeedPoAsync(erp);
        await erp.CreateInvoiceAsync("SEED-INV-T1", "V-T", poNumber, RecordSource.Erp, "requester",
            [new InvoiceLineInput(poLineRef, 5m, unitPrice)], Guid.NewGuid().ToString("N"));
        var dir = WriteFiles(
            """{"scenario":"ui-ledger","documents":"""
            + """[{"event":"smallPo.k1.invoice","docType":"INV","number":"SEED-INV-T1"}]}""",
            """{"smallPos":[{"key":"k1","item":"ITEM-T","quantity":5,"vendor":"V-T","unitPrice":10}]}""");
        var service = Service(erp, new CaseCacheStore(Path.Combine(dir, "cache")), dir);

        var records = await service.GetRecordsAsync();

        var invoice = Assert.Single(records.Invoices);
        Assert.Equal("SEED-INV-T1", invoice.SupplierInvoiceNumber);
        Assert.Equal("V-T", invoice.VendorId);
    }

    [Fact]
    public async Task UnresolvableSeedInvoiceNotesVendorNotReader()
    {
        var erp = new InMemoryErpAdapter();
        var dir = WriteFiles(
            """{"scenario":"ui-ledger","documents":"""
            + """[{"event":"smallPo.gone.invoice","docType":"INV","number":"SEED-INV-X"}]}""",
            """{"smallPos":[]}""");
        var service = Service(erp, new CaseCacheStore(Path.Combine(dir, "cache")), dir);

        var records = await service.GetRecordsAsync();

        Assert.Empty(records.Invoices);
        Assert.Contains("SEED-INV-X", records.OpeningNumbers);
        Assert.Contains(records.Notes, n => n.Contains("SEED-INV-X") && n.Contains("vendor"));
        Assert.DoesNotContain(records.Notes, n => n.Contains("no reader"));
    }

    [Fact]
    public async Task DetailChainMarksCurrentAndSummarizes()
    {
        var erp = new InMemoryErpAdapter();
        var dir = UiTestSupport.NewDir("gate-ui-ledger");
        var store = new CaseCacheStore(Path.Combine(dir, "cache"));
        var now = DateTimeOffset.UtcNow;
        var kase = new Case(
            "CASE-T", "SO-T", "ITEM-T", "ui-test", CaseStage.PoPlaced, now, now,
            "PR-T", "PO-T", null, null, [], [], "CORP-02", "origin cap review");
        await store.WriteAsync(new CaseFile(
            kase,
            [new CaseEvent("E-1", "CASE-T", now, RecordSource.P2pAgent,
                CaseEventKind.PoCreated, "PO placed with V-T.", null,
                "PO", "PO-T", null, 50m, "TST")],
            [UiTestSupport.PendingDecision("D-1", "CASE-T", DecisionKind.PoApproval, "PO", "PO-T")
                with { RequestedTo = "approver" }]));
        var service = Service(erp, store, dir);

        var detail = await service.GetCaseDetailAsync("CASE-T");

        Assert.NotNull(detail);
        var current = detail.Chain.Where(s => s.IsCurrent).ToList();
        Assert.Equal("Purchase order", Assert.Single(current).Label);
        Assert.NotEmpty(detail.Summary.Did);
        Assert.NotEmpty(detail.Summary.Stopped);
        Assert.Contains(detail.Summary.Stopped, s => s.RuleId == "CORP-02");
    }
}

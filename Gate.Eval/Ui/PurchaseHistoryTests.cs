// Purchase-history and screening-math integration tests: the ERP history
// view reads the same rows the math line computes from, so page totals
// equal the numbers in the line — on the case page and in the activity
// pane, for the demo item and for an unfamiliar one. Test data is local.
using Bunit;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Components.Pages.Erp;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Gate.Eval.Ui;

public sealed class PurchaseHistoryTests
{
    [Fact]
    public async Task HistoryPageTotalsEqualTheMathLineNumbers()
    {
        var erp = SeededErp.Create();
        erp.SeedVolumeHistory(SeededErp.Battery, [
            new VolumeHistory("V-A CN active", "CN", 5900),
            new VolumeHistory("V-B MX active", "MX", 3800),
            new VolumeHistory("Receipt RCT-9", "MX", 300),
        ]);
        var views = ErpViews(erp);

        var page = await views.GetPurchaseHistoryAsync(SeededErp.Battery);

        Assert.Equal(10000m, page.GrandTotal);
        Assert.Equal(5900m, page.CountryTotals.Single(c => c.CountryCode == "CN").Cells);
        Assert.Equal(4100m, page.CountryTotals.Single(c => c.CountryCode == "MX").Cells);
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(
            "erp/record/receipt/RCT-9",
            page.Rows.Single(r => r.Source == "Receipt RCT-9").ReceiptHref);
        Assert.Null(page.Rows.Single(r => r.Source == "V-A CN active").ReceiptHref);
        var history = await erp.GetVolumeHistoryAsync(SeededErp.Battery);
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "CinderVolt Energy Ltd", "CN",
            history.Where(h => h.Country == "CN").Sum(h => h.Cells),
            history.Sum(h => h.Cells), 1200m, 0.60m,
            page.Rows.Count == 0 ? "none" : OriginCapMath.HistoryHref(SeededErp.Battery),
            "erp/record/pr/PR-1");
        Assert.NotNull(line);
        Assert.Contains(OriginCapMath.Cells(page.GrandTotal), line.PlainText);
        Assert.Contains(
            OriginCapMath.Cells(page.CountryTotals.Single(c => c.CountryCode == "CN").Cells),
            line.PlainText);
    }

    [Fact]
    public async Task HistoryPageWorksForAnUnfamiliarItem()
    {
        var erp = SeededErp.Create();
        erp.SeedItem(new ItemDto("COIL-3", "Copper coil", false, 5, "EA"));
        erp.SeedVolumeHistory("COIL-3", [
            new VolumeHistory("Zed opening", "SE", 400),
            new VolumeHistory("Other opening", "OTHER", 600),
        ]);
        var views = ErpViews(erp);

        var page = await views.GetPurchaseHistoryAsync("COIL-3");

        Assert.Equal("Copper coil", page.ItemDescription);
        Assert.Equal(1000m, page.GrandTotal);
        Assert.Equal("Sweden", page.CountryTotals.Single(c => c.CountryCode == "SE").CountryName);
        Assert.Equal("OTHER", page.CountryTotals.Single(c => c.CountryCode == "OTHER").CountryName);
    }

    [Fact]
    public async Task HistoryPageWithNoRowsSaysSoPlainly()
    {
        var views = ErpViews(SeededErp.Create());

        var page = await views.GetPurchaseHistoryAsync("NOPE-1");

        Assert.Empty(page.Rows);
        Assert.Empty(page.CountryTotals);
        Assert.Equal(0m, page.GrandTotal);
        Assert.Contains(page.Notes, n => n.Contains("No purchase history"));
    }

    [Fact]
    public async Task CaseDetailCarriesTheBlockingVendorMathLine()
    {
        var erp = SeededErp.Create();
        erp.SeedVolumeHistory(SeededErp.Battery, [
            new VolumeHistory("V-A CN active", "CN", 5900),
            new VolumeHistory("V-B MX active", "MX", 4100),
        ]);
        var pr = await erp.CreatePrAsync(
            "planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, 1200m, "EA")],
            Guid.NewGuid().ToString("N"));
        var dir = UiTestSupport.NewDir("gate-ui-math-case");
        var cacheDir = Path.Combine(dir, "case-cache");
        var store = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(new CaseFile(
            new Case("CASE-M1", "SO-1", SeededErp.Battery, "ui-test", CaseStage.VendorScreened, now, now,
                pr.PrNumber, null, null, null, [], [], OriginCapMath.OriginCapRuleId, "Award blocked."),
            [
                new CaseEvent("e1", "CASE-M1", now.AddMinutes(-5), RecordSource.P2pAgent,
                    CaseEventKind.VendorBlocked,
                    $"Vendor V-A blocked: {OriginCapMath.OriginCapRuleId}: share exceeded.",
                    OriginCapMath.OriginCapRuleId, null, null, null, null, null),
            ],
            []));
        var views = CaseViews(erp, cacheDir);

        var detail = await views.GetCaseDetailAsync("CASE-M1");

        Assert.NotNull(detail);
        var line = Assert.Single(detail.MathLines);
        Assert.Contains("China share", line.PlainText);
        Assert.Contains("(5,900+1,200)/(10,000+1,200)=63.4%", line.PlainText);
        Assert.Contains("CinderVolt Energy Ltd is blocked", line.PlainText);
        Assert.Contains(line.Segments, s => s.Text == "5,900" && s.Href == line.HistoryHref);
    }

    [Fact]
    public async Task ActivityPaneShowsTheMathLineForAnUnfamiliarItemAndVendor()
    {
        var erp = SeededErp.Create();
        erp.SeedItem(new ItemDto("COIL-3", "Copper coil", false, 5, "EA"));
        erp.SeedVendor(new VendorDto("V-Z", "Zed Parts AB", "SE", false, null, "NET0"));
        erp.SeedVendorPrice("V-Z", "COIL-3", 2m);
        erp.SeedVolumeHistory("COIL-3", [
            new VolumeHistory("Zed opening", "SE", 7000),
            new VolumeHistory("Other opening", "OTHER", 3000),
        ]);
        var pr = await erp.CreatePrAsync(
            "planner", RecordSource.P2pAgent, "planner",
            [new PurchaseRequisitionLine("COIL-3", 1000m, "EA")],
            Guid.NewGuid().ToString("N"));
        var dir = UiTestSupport.NewDir("gate-ui-math-pane");
        var cacheDir = Path.Combine(dir, "case-cache");
        var store = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(new CaseFile(
            new Case("CASE-Z1", "SO-77", "COIL-3", "ui-test", CaseStage.InvoiceMatched, now, now,
                pr.PrNumber, "PO-Z1", "ASN-Z1", "RCT-Z1", [], [], OriginCapMath.OriginCapRuleId, "Award blocked."),
            [
                new CaseEvent("e1", "CASE-Z1", now.AddMinutes(-5), RecordSource.P2pAgent,
                    CaseEventKind.VendorBlocked,
                    $"Vendor V-Z blocked: {OriginCapMath.OriginCapRuleId}: share exceeded.",
                    OriginCapMath.OriginCapRuleId, null, null, null, null, null),
            ],
            []));
        var service = new AgentActivityService(
            new CaseCacheStore(cacheDir), erp,
            new CaseRunner(erp, new P2pAgent(erp, new FakeModelClient(), new RecordingComplianceEngine()),
                new FakeModelClient(), new RecordingComplianceEngine(), new DockEvaluatorAdapter(), cacheDir),
            UiTestLogger<AgentActivityService>.Instance,
            Countries());

        var vm = await service.BuildAsync();

        var group = Assert.Single(vm.Recent);
        Assert.Equal("Copper coil for SO-77", group.HeaderText);
        var entry = Assert.Single(group.Lines);
        Assert.Contains("Sweden share", entry.Text);
        Assert.Contains("(7,000+1,000)/(10,000+1,000)=72.7%", entry.Text);
        Assert.Contains("Zed Parts AB is blocked", entry.Text);
        Assert.Equal(OriginCapMath.HistoryHref("COIL-3"), entry.Href);
    }

    [Fact]
    public void HistoryPageRendersRowsTotalsAndReceiptLinks()
    {
        using var ctx = new TestContext();
        var erp = SeededErp.Create();
        erp.SeedItem(new ItemDto("COIL-3", "Copper coil", false, 5, "EA"));
        erp.SeedVolumeHistory("COIL-3", [
            new VolumeHistory("Receipt RCT-3", "SE", 400),
            new VolumeHistory("Other opening", "OTHER", 600),
        ]);
        var dir = UiTestSupport.NewDir("gate-ui-math-render");
        ctx.Services.AddSingleton<IErpAdapter>(erp);
        ctx.Services.AddSingleton(new CaseCacheStore(Path.Combine(dir, "case-cache")));
        ctx.Services.AddSingleton(new DockPhotoService(Path.Combine(dir, "img"), Path.Combine(dir, "reads")));
        ctx.Services.AddSingleton(UiTestLogger<CaseViewService>.Instance);
        ctx.Services.AddSingleton(UiTestLogger<ErpViewService>.Instance);
        ctx.Services.AddSingleton(UiTestLogger<ErpPurchaseHistory>.Instance);
        ctx.Services.AddSingleton(Countries());
        ctx.Services.AddScoped<CaseViewService>();
        ctx.Services.AddScoped<ErpViewService>();

        var cut = ctx.RenderComponent<ErpPurchaseHistory>(p => p.Add(r => r.ItemId, "COIL-3"));

        cut.WaitForAssertion(() => Assert.Contains("Totals by vendor country", cut.Markup));
        Assert.Contains("Copper coil", cut.Markup);
        Assert.Contains("Sweden (SE)", cut.Markup);
        Assert.Contains("1,000", cut.Markup);
        Assert.Contains("erp/record/receipt/RCT-3", cut.Markup);
        Assert.DoesNotContain("gate-notice-error", cut.Markup);
    }

    private static CountryNames Countries() =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CN"] = "China",
            ["MX"] = "Mexico",
            ["SE"] = "Sweden",
            ["US"] = "United States",
        });

    private static ErpViewService ErpViews(InMemoryErpAdapter erp)
    {
        var dir = UiTestSupport.NewDir("gate-ui-math-erp");
        var cases = new CaseCacheStore(Path.Combine(dir, "case-cache"));
        var photos = new DockPhotoService(Path.Combine(dir, "img"), Path.Combine(dir, "reads"));
        return new ErpViewService(
            erp,
            new CaseViewService(erp, cases, photos, UiTestLogger<CaseViewService>.Instance),
            UiTestLogger<ErpViewService>.Instance,
            Countries());
    }

    private static CaseViewService CaseViews(InMemoryErpAdapter erp, string cacheDir)
    {
        var dir = UiTestSupport.NewDir("gate-ui-math-caseviews");
        return new CaseViewService(
            erp,
            new CaseCacheStore(cacheDir),
            new DockPhotoService(Path.Combine(dir, "img"), Path.Combine(dir, "reads")),
            UiTestLogger<CaseViewService>.Instance,
            countries: Countries());
    }
}

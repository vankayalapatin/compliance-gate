// ERP area display tests: case display names plus opening-data labels,
// sidebar module defs, route shapes, clean-number formatting, and the
// doc-type map. Render tests pin the Cases real-runs-only list, the
// opening-record note, and the ERP index redirect through bUnit
// (sidebar click-through lives in SidebarNavTests). All data local;
// no live calls.
using Bunit;
using Gate.Contracts;
using Microsoft.AspNetCore.Components;
using Gate.Web.Components.Pages;
using Gate.Web.Components.Pages.Erp;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Gate.Eval.Ui;

public sealed class ErpDisplayTests
{
    private static Case MakeCase(CaseStage stage, bool opening = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new Case(
            "CASE-E", "SO-E", "ITEM-E", "ui-erp", stage, now, now,
            "PR-E", "PO-E", null, null, [], [], null, null, null, opening);
    }

    [Fact]
    public void DisplayName_ShowsSoItemAndStage()
    {
        Assert.Equal(
            "SO SO-E · ITEM-E · PO placed",
            CaseDisplay.DisplayName(MakeCase(CaseStage.PoPlaced)));
    }

    [Fact]
    public void DisplayName_OpeningShellFallsBackToDocRef()
    {
        var now = DateTimeOffset.UtcNow;
        var shell = new Case(
            "CASE-SHELL", string.Empty, string.Empty, "ui-erp", CaseStage.Created, now, now,
            "PR-OPEN", null, null, null, [], [], null, null, null, true);
        var invoiced = shell with
        {
            PrNumber = null,
            Invoices = [new InvoiceKey("V-E", "INV-E")],
        };
        var bare = shell with { PrNumber = null };

        Assert.Equal("PR PR-OPEN · order created", CaseDisplay.DisplayName(shell));
        Assert.Equal("Invoice V-E/INV-E · order created", CaseDisplay.DisplayName(invoiced));
        Assert.Equal("CASE-SHELL · order created", CaseDisplay.DisplayName(bare));
    }

    [Fact]
    public void StageSummary_CoversEveryStage()
    {
        foreach (var stage in Enum.GetValues<CaseStage>())
        {
            var summary = CaseDisplay.StageSummary(stage);
            Assert.False(string.IsNullOrWhiteSpace(summary));
            Assert.DoesNotContain("_", summary);
        }

        Assert.Equal("partially paid", CaseDisplay.StageSummary(CaseStage.PaymentPartial));
        Assert.Equal("cancelled", CaseDisplay.StageSummary(CaseStage.Cancelled));
    }

    [Fact]
    public void Modules_HaveFiveAreasWithExpectedLists()
    {
        var names = ErpModules.All.Select(m => m.Name).ToList();
        Assert.Equal(
            ["Sales", "Procurement", "Inventory", "Accounts Payable", "Masters"], names);

        var lists = ErpModules.All
            .SelectMany(m => m.Lists.Select(l => l.Slug))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            ["asn", "hold", "invoice", "item", "payment", "po", "pr", "receipt", "so", "stock", "vendor"],
            lists);

        Assert.Equal("Procurement", ErpModules.ModuleFor("po"));
        Assert.Equal("Purchase orders", ErpModules.TitleFor("po"));
        Assert.True(ErpModules.IsKnownList("stock"));
        Assert.False(ErpModules.IsKnownList("ledger"));
        Assert.Equal("erp/list/po", ErpModules.ListHref("po"));
    }

    [Fact]
    public void Routes_ShapeRecordAndNewHrefs()
    {
        Assert.Equal("erp/record/po/PO-1", ErpRoutes.RecordHref("po", "PO-1"));
        Assert.Equal("erp/record/invoice/V-1/INV-1", ErpRoutes.RecordHref("invoice", "V-1", "INV-1"));
        Assert.Equal("erp/new/po", ErpRoutes.NewHref("po"));
        Assert.Equal("erp/new/po/PR-1", ErpRoutes.NewHref("po", "PR-1"));

        Assert.True(ErpRoutes.CanCreate("so"));
        Assert.True(ErpRoutes.CanCreate("vendor"));
        Assert.False(ErpRoutes.CanCreate("asn"));
        Assert.False(ErpRoutes.CanCreate("stock"));
        Assert.False(ErpRoutes.CanCreate("hold"));
    }

    [Fact]
    public void Format_RendersCleanNumbers()
    {
        Assert.Equal("10.50 TST", ErpFormat.Money(10.5m, "TST"));
        Assert.Equal("5", ErpFormat.Qty(5.00m));
        Assert.Equal("4.25", ErpFormat.Qty(4.25m));
        Assert.Equal("1,234", ErpFormat.Count(1234));
        Assert.Contains("missing", ErpFormat.Money(1m, null));
    }

    [Fact]
    public void DocTypes_MapKindsAndBlockedDocs()
    {
        Assert.Equal("PR", ErpDocTypes.ForKind("pr"));
        Assert.Equal("Payment", ErpDocTypes.ForKind("payment"));
        Assert.Null(ErpDocTypes.ForKind("vendor"));
        Assert.Equal("V-1/INV-1", ErpDocTypes.InvoiceDocNumber("V-1", "INV-1"));

        var invoice = ErpDocTypes.RecordFor("Invoice", "V-1/INV-1");
        Assert.NotNull(invoice);
        Assert.Equal(("invoice", "V-1", "INV-1"), invoice.Value);
        var po = ErpDocTypes.RecordFor("PO", "PO-1");
        Assert.NotNull(po);
        Assert.Equal(("po", "PO-1", null), po.Value);
        Assert.Null(ErpDocTypes.RecordFor("Invoice", "no-slash"));
        Assert.Null(ErpDocTypes.RecordFor("What", "X-1"));
        Assert.Null(ErpDocTypes.RecordFor("PO", null));
    }

    [Fact]
    public async Task CasesPage_ShowsRealRunsOnly()
    {
        var dir = UiTestSupport.NewDir("gate-ui-erp-display");
        var store = new CaseCacheStore(Path.Combine(dir, "cache"));
        await store.WriteAsync(new CaseFile(
            MakeCase(CaseStage.PaymentRequested, opening: true), [], []));
        await store.WriteAsync(new CaseFile(
            MakeCase(CaseStage.PoPlaced) with { CaseId = "CASE-LIVE" }, [], []));
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(store);

        using var page = ctx.RenderComponent<Cases>();
        page.WaitForAssertion(
            () => Assert.Contains("SO SO-E · ITEM-E · PO placed", page.Markup),
            TimeSpan.FromSeconds(10));

        // The opening shell stays out of the list entirely.
        Assert.DoesNotContain("cases/CASE-E", page.Markup);
        Assert.DoesNotContain("payment requested", page.Markup);
        Assert.DoesNotContain("opening data", page.Markup);
        // Keys and routes stay the case id.
        Assert.Contains("cases/CASE-LIVE", page.Markup);
    }

    [Fact]
    public async Task CaseDetailPage_OpeningCaseShowsNote()
    {
        var dir = UiTestSupport.NewDir("gate-ui-erp-display");
        var store = new CaseCacheStore(Path.Combine(dir, "cache"));
        await store.WriteAsync(new CaseFile(
            MakeCase(CaseStage.DockChecked, opening: true), [], []));
        // CaseDetail renders through CaseViewService; build the real one over
        // an empty fake ERP so the detail resolves from the cached case file.
        using var ctx = new TestContext();
        var erp = new Gate.Erp.InMemoryErpAdapter();
        var views = new CaseViewService(
            erp, store,
            new DockPhotoService(
                Path.Combine(dir, "dock"), Path.Combine(dir, "vision")),
            UiTestLogger<CaseViewService>.Instance);
        ctx.Services.AddSingleton(views);

        using var page = ctx.RenderComponent<CaseDetail>(
            parameters => parameters.Add(p => p.CaseId, "CASE-E"));
        page.WaitForAssertion(
            () => Assert.Contains("Opening record — see Decisions or ERP.", page.Markup),
            TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("What the agent did", page.Markup);
        Assert.DoesNotContain("What it tried and was stopped from doing", page.Markup);
    }

    [Fact]
    public void ErpIndex_RedirectsToFirstList()
    {
        using var ctx = new TestContext();
        using var page = ctx.RenderComponent<Gate.Web.Components.Pages.Erp.Erp>();

        // No landing cards: /erp lands straight on the first list.
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var first = ErpModules.All[0].Lists[0].Slug;
        Assert.Equal("so", first);
        Assert.EndsWith(ErpModules.ListHref(first), nav.Uri);
    }
}

// Banned-word tests: rendered pages plus the baseline platform snapshot
// carry product language only. The scan strips markup (attributes such as
// image sources are not UI text) and fails on every product-bar token:
// seed/scenario/manifest/demo/fictional/lifecycle/kbv/case file/case
// cache/JSON/True/False/endpoint mentions/system-as-person. Pages render
// over the crawler's fixture world, so failures name the page. The
// snapshot part scans the shipped baseline when present (exempting only
// the load-bearing kbVersion file tag); a missing snapshot passes
// vacuously (lane 1 owns that artifact). A placement pin
// keeps operator controls on the Admin page only. No live calls.
using System.Text.RegularExpressions;
using Bunit;
using Gate.Contracts;
using Gate.Web.Components;
using Gate.Web.Components.Outside;
using Gate.Web.Components.Pages;
using Gate.Web.Components.Pages.Erp;
using Gate.Web.Components.Pages.Governance;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Gate.Eval.Ui;

public sealed class BannedWordTests
{
    private static readonly (Regex Pattern, string Name)[] Banned =
    [
        (new Regex(@"\bseed\w*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "seed*"),
        (new Regex(@"\bscenarios?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "scenario"),
        (new Regex(@"\bmanifests?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "manifest"),
        (new Regex(@"\bdemos?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "demo"),
        (new Regex(@"\bfictional\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "fictional"),
        (new Regex(@"\blifecycle\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "lifecycle"),
        (new Regex(@"kbv\w*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "kbv*"),
        (new Regex(@"case[\s-]file", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "case file"),
        (new Regex(@"case[\s-]cache", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "case cache"),
        (new Regex(@"\bjson\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "json"),
        (new Regex(@"\bTrue\b", RegexOptions.CultureInvariant), "True"),
        (new Regex(@"\bFalse\b", RegexOptions.CultureInvariant), "False"),
        (new Regex(@"/simulation/|/scenario/|/agent/|/dock-images/", RegexOptions.CultureInvariant), "endpoint"),
        (new Regex(@"\bsystem\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "system"),
        (new Regex(@"gate\.db", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "gate.db"),
    ];

    private static readonly Regex TagStripper = new("<[^>]*>", RegexOptions.Compiled);

    private static string VisibleText(string markup) =>
        TagStripper.Replace(markup, " ");

    private static void AssertClean(string text, string what)
    {
        foreach (var (pattern, name) in Banned)
        {
            var hit = pattern.Match(text);
            Assert.False(hit.Success, $"{what} shows banned word '{name}': '...{Clip(text, hit.Index)}...'.");
        }
    }

    private static string Clip(string text, int index)
    {
        var start = Math.Max(0, index - 40);
        var length = Math.Min(80, text.Length - start);
        return length <= 0 ? string.Empty : text.Substring(start, length);
    }

    private static string Render<T>(TestContext ctx) where T : IComponent
    {
        using var page = ctx.RenderComponent<T>();
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        return page.Markup;
    }

    private static string Render<T>(
        TestContext ctx, Action<ComponentParameterCollectionBuilder<T>> parameters)
        where T : IComponent
    {
        using var page = ctx.RenderComponent<T>(parameters);
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        return page.Markup;
    }

    [Fact]
    public async Task RenderedPagesCarryNoBannedWords()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        var pages = new Dictionary<string, string>(StringComparer.Ordinal);

        pages["decisions"] = Render<Decisions>(ctx);
        pages["cases"] = Render<Cases>(ctx);
        foreach (var caseId in w.CaseIds)
        {
            pages[$"cases/{caseId}"] = Render<CaseDetail>(ctx, p => p.Add(c => c.CaseId, caseId));
        }

        var slugs = ErpModules.All.SelectMany(m => m.Lists).Select(l => l.Slug).ToList();
        foreach (var slug in slugs)
        {
            pages[$"erp/list/{slug}"] = Render<ErpList>(ctx, p => p.Add(l => l.Slug, slug));
        }

        var pos = await w.ErpViews.ListPurchaseOrdersAsync();
        var poNumber = pos.Rows.Single().PoNumber;
        var prs = await w.ErpViews.ListPurchaseRequisitionsAsync();
        var pendingPr = prs.Rows.First(r => r.Status == PrStatus.Submitted);
        pages["erp/record/pr"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "pr").Add(r => r.Key, pendingPr.PrNumber));
        pages["erp/record/po"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "po").Add(r => r.Key, poNumber));
        pages["erp/record/asn"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "asn").Add(r => r.Key, "ASN-C"));
        var receipts = await w.ErpViews.ListReceiptsAsync();
        pages["erp/record/receipt"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "receipt").Add(r => r.Key, receipts.Rows.Single().ReceiptNumber));
        // Invoice route in real binding: Kind/Key arrive null (see
        // CrawlerTests.VisitHref); only Vendor/Number are bound.
        pages["erp/record/invoice"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, (string)null!).Add(r => r.Key, (string)null!)
            .Add(r => r.Vendor, "V-C").Add(r => r.Number, "INV-C"));
        var payments = await w.ErpViews.ListPaymentsAsync();
        pages["erp/record/payment"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "payment").Add(r => r.Key, payments.Rows.Single().PaymentNumber));
        pages["erp/record/vendor"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "vendor").Add(r => r.Key, "V-C"));
        pages["erp/record/item"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "item").Add(r => r.Key, "ITEM-C"));
        pages["erp/record/stock"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "stock").Add(r => r.Key, "ITEM-C"));
        var sos = await w.ErpViews.ListSalesOrdersAsync();
        pages["erp/record/so"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "so").Add(r => r.Key, sos.Rows.Single().SoNumber));
        var holds = await w.ErpViews.ListHoldsAsync();
        pages["erp/record/hold"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "hold").Add(r => r.Key, holds.Rows.Single().HoldId));

        pages["governance/rules"] = Render<GovernanceRules>(ctx);
        pages["governance/memos"] = Render<GovernanceMemos>(ctx);
        pages["governance/exceptions"] = Render<GovernanceExceptions>(ctx);
        pages["governance/versions"] = Render<GovernanceVersions>(ctx);
        pages["governance/kb/treasury"] = Render<GovernanceKbSpace>(ctx, p => p.Add(k => k.Space, "treasury"));
        pages["governance/kb/kbv9/finance/w-c1"] = Render<GovernanceKbEntry>(ctx, p => p
            .Add(k => k.Version, "kbv9").Add(k => k.Section, "finance").Add(k => k.EntryId, "w-c1"));

        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("search?q=V-C");
        pages["search"] = Render<Search>(ctx);
        pages["admin"] = Render<Admin>(ctx);
        pages["outside"] = Render<Outside>(ctx);
        pages["entity-header"] = Render<EntityHeader>(ctx);

        var options = await w.Simulate.GetOptionsAsync();
        pages["shop"] = Render<ShopPanel>(ctx, p => p.Add(s => s.Options, options));
        pages["dock"] = Render<DockPanel>(ctx, p => p.Add(d => d.Options, options));
        pages["supplier"] = Render<SupplierPortal>(ctx, p => p
            .Add(s => s.VendorId, "V-C").Add(s => s.Options, options));

        Assert.True(pages.Count > 30, $"Expected broad page coverage, got {pages.Count}.");
        foreach (var (name, markup) in pages)
        {
            AssertClean(VisibleText(markup), name);
        }

        // Spend shows on the Admin page only.
        Assert.Contains("spend unmetered", pages["admin"]);
        foreach (var (name, markup) in pages.Where(p => p.Key != "admin"))
        {
            Assert.DoesNotContain("spend unmetered", markup);
            Assert.DoesNotContain("live call(s)", markup);
        }
    }

    [Fact]
    public void BaselineSnapshotCarriesNoBannedWords()
    {
        var root = FindRepoRoot();
        var baseline = Path.Combine(root, "Gate.Web", "data", "baseline");
        if (!Directory.Exists(baseline))
        {
            // Lane 1 owns the snapshot artifact; nothing to scan yet.
            return;
        }

        var files = Directory.GetFiles(baseline, "*.json", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            // The kbVersion file tag is the scheme's load-bearing key in
            // storage (routes, pins, comparisons); surfaces render it as
            // "version N". The scan exempts the whole tag pair (key and
            // value — the key alone still matches the kbv pattern); every
            // other banned word still fails.
            var text = Regex.Replace(
                File.ReadAllText(file),
                @"""kbVersion""\s*:\s*""kbv\d+""",
                @"""version"":""version""",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            AssertClean(text, Path.GetRelativePath(root, file));
        }
    }

    [Fact]
    public void TopBarKeepsEntityUserSearchOnly()
    {
        var root = FindRepoRoot();
        var layout = File.ReadAllText(Path.Combine(root, "Gate.Web", "Components", "Layout", "MainLayout.razor"));
        Assert.DoesNotContain("ReplayControls", layout);
        Assert.DoesNotContain("Spend", layout);
        Assert.Contains("EntityHeader", layout);
        Assert.Contains("UserSwitcher", layout);
        Assert.Contains("admin", layout);

        var admin = File.ReadAllText(Path.Combine(root, "Gate.Web", "Components", "Pages", "Admin.razor"));
        Assert.Contains("ReplayControls", admin);
        Assert.Contains("Pick up ERP work", admin);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Gate.Web")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (no directory containing Gate.Web found above "
            + AppContext.BaseDirectory + ").");
    }
}

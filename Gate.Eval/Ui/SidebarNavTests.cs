// Round-3 sidebar regression tests: every sidebar link in both shells is
// clicked through bUnit and its destination asserted. ERP links navigate to
// their list hrefs (modules expand on click, the current module opens on
// its own); Outside buttons select in-page (shop, each vendor, dock) and
// never navigate anywhere — the vendor href="#" trap that landed on home
// is pinned gone. All data local; no live calls.
using Bunit;
using Gate.Contracts;
using Gate.Web.Components.Erp;
using Gate.Web.Components.Outside;
using Gate.Web.ViewModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Gate.Eval.Ui;

public sealed class SidebarNavTests
{
    private sealed class SelectRecorder
    {
        public readonly List<string> Seen = [];

        public void Record(string selected) => Seen.Add(selected);
    }

    [Fact]
    public void ErpSidebar_EveryLinkClicksThroughToItsList()
    {
        using var ctx = new TestContext();
        using var sidebar = ctx.RenderComponent<ErpSidebar>();

        // Modules start collapsed; one click per module button expands each
        // (re-query after every click: each render replaces the nodes).
        Assert.Equal(ErpModules.All.Count, sidebar.FindAll(".gate-erp-module-btn").Count);
        Assert.Empty(sidebar.FindAll(".gate-erp-links a"));
        for (var i = 0; i < ErpModules.All.Count; i++)
        {
            sidebar.FindAll(".gate-erp-module-btn")[i].Click();
        }

        // Follow every link and assert its destination href. NavLinks carry
        // real hrefs with no click handler (the browser navigates), so the
        // test walks each href through the navigation manager instead of
        // dispatching a handler-less click, which bUnit refuses.
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var expected = ErpModules.All
            .SelectMany(m => m.Lists.Select(l => ErpModules.ListHref(l.Slug)))
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
        var links = sidebar.FindAll(".gate-erp-links a");
        Assert.Equal(expected.Count, links.Count);
        var seen = new List<string>();
        foreach (var link in links)
        {
            var href = link.GetAttribute("href") ?? string.Empty;
            seen.Add(href);
            nav.NavigateTo(href);
            Assert.EndsWith(href, nav.Uri);
        }

        Assert.Equal(expected, seen.OrderBy(h => h, StringComparer.Ordinal).ToList());

        // Toggle back: clicking the first module button collapses it again.
        sidebar.FindAll(".gate-erp-module-btn")[0].Click();
        Assert.DoesNotContain(
            sidebar.FindAll(".gate-erp-links a"),
            a => string.Equals(
                a.GetAttribute("href"),
                ErpModules.ListHref(ErpModules.All[0].Lists[0].Slug),
                StringComparison.Ordinal));
    }

    [Fact]
    public void ErpSidebar_CurrentSlugOpensItsModule()
    {
        using var ctx = new TestContext();
        using var sidebar = ctx.RenderComponent<ErpSidebar>(
            parameters => parameters.Add(p => p.CurrentSlug, "po"));

        Assert.Contains(
            sidebar.FindAll(".gate-erp-links a"),
            a => string.Equals(
                a.GetAttribute("href"), ErpModules.ListHref("po"), StringComparison.Ordinal));
        Assert.DoesNotContain(
            sidebar.FindAll(".gate-erp-links a"),
            a => string.Equals(
                a.GetAttribute("href"), ErpModules.ListHref("so"), StringComparison.Ordinal));
    }

    [Fact]
    public void OutsideSidebar_EveryButtonSelectsItsTargetWithoutNavigating()
    {
        using var ctx = new TestContext();
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var before = nav.Uri;
        var recorder = new SelectRecorder();
        var vendors = new List<VendorDto>
        {
            new("V-1", "First Vendor", "US", false, null, "NET0"),
            new("V-2", "Second Vendor", "US", false, null, "NET0"),
        };
        using var sidebar = ctx.RenderComponent<OutsideSidebar>(parameters => parameters
            .Add(p => p.Vendors, vendors)
            .Add(p => p.Selected, "shop")
            .Add(p => p.Select, EventCallback.Factory.Create<string>(recorder, recorder.Record)));

        // No anchor traps: selection is buttons only, so nothing can navigate.
        Assert.Empty(sidebar.FindAll("a[href]"));

        // Click every control: shop, each vendor, dock (re-query after every
        // click: each render replaces the nodes).
        Assert.Equal(3, sidebar.FindAll(".gate-erp-module-btn").Count);
        sidebar.FindAll(".gate-erp-module-btn")[0].Click();
        Assert.Equal(vendors.Count, sidebar.FindAll(".gate-erp-link-btn").Count);
        for (var i = 0; i < vendors.Count; i++)
        {
            sidebar.FindAll(".gate-erp-link-btn")[i].Click();
        }

        sidebar.FindAll(".gate-erp-module-btn")[2].Click();

        Assert.Equal(
            ["shop", "vendor:V-1", "vendor:V-2", "dock"],
            recorder.Seen);
        Assert.Equal(before, nav.Uri);

        // The suppliers section collapses and reopens on click.
        sidebar.FindAll(".gate-erp-module-btn")[1].Click();
        Assert.Empty(sidebar.FindAll(".gate-erp-link-btn"));
        sidebar.FindAll(".gate-erp-module-btn")[1].Click();
        Assert.Equal(vendors.Count, sidebar.FindAll(".gate-erp-link-btn").Count);
        Assert.Equal(before, nav.Uri);
    }
}

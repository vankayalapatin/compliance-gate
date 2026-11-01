// Governance sidebar regression tests: every sidebar link clicks through
// to its destination href, and the current key opens its own module.
// Knowledge spaces arrive as a parameter (runtime data); fixture spaces
// here are invented, never the demo spaces.
using Bunit;
using Gate.Contracts;
using Gate.Web.Components.Governance;
using Gate.Web.ViewModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Gate.Eval.Ui;

public sealed class GovernanceSidebarTests
{
    private static List<KbSpace> FixtureSpaces() =>
    [
        new("treasury", "Terry Owner", "approver", ["finance"], ["Terry Owner"]),
        new("docks", "Dana Owner", "approver", ["operations"], ["Dana Owner", "Terry Owner"]),
    ];

    [Fact]
    public void EveryLinkClicksThroughToItsDestination()
    {
        using var ctx = new TestContext();
        var spaces = FixtureSpaces();
        using var sidebar = ctx.RenderComponent<GovernanceSidebar>(
            parameters => parameters.Add(p => p.Spaces, spaces));

        Assert.Equal(2, sidebar.FindAll(".gate-erp-module-btn").Count);
        Assert.Empty(sidebar.FindAll(".gate-erp-links a"));
        sidebar.FindAll(".gate-erp-module-btn")[0].Click();
        sidebar.FindAll(".gate-erp-module-btn")[1].Click();

        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var expected = new List<string>
        {
            GovernanceRoutes.RulesHref,
            GovernanceRoutes.MemosHref,
            GovernanceRoutes.ExceptionsHref,
            GovernanceRoutes.KbSpaceHref("treasury"),
            GovernanceRoutes.KbSpaceHref("docks"),
            GovernanceRoutes.VersionsHref,
        };
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

        Assert.Equal(
            expected.OrderBy(h => h, StringComparer.Ordinal).ToList(),
            seen.OrderBy(h => h, StringComparer.Ordinal).ToList());

        sidebar.FindAll(".gate-erp-module-btn")[0].Click();
        Assert.DoesNotContain(
            sidebar.FindAll(".gate-erp-links a"),
            a => string.Equals(
                a.GetAttribute("href"), GovernanceRoutes.RulesHref, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("rules", "governance/rules", "governance/kb/treasury")]
    [InlineData("memos", "governance/memos", "governance/versions")]
    [InlineData("exceptions", "governance/exceptions", "governance/kb/treasury")]
    public void CurrentComplianceKeyOpensComplianceOnly(string key, string visible, string hidden)
    {
        using var ctx = new TestContext();
        using var sidebar = ctx.RenderComponent<GovernanceSidebar>(parameters => parameters
            .Add(p => p.CurrentKey, key)
            .Add(p => p.Spaces, FixtureSpaces()));

        Assert.Contains(
            sidebar.FindAll(".gate-erp-links a"),
            a => string.Equals(a.GetAttribute("href"), visible, StringComparison.Ordinal));
        Assert.DoesNotContain(
            sidebar.FindAll(".gate-erp-links a"),
            a => string.Equals(a.GetAttribute("href"), hidden, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("kb:treasury")]
    [InlineData("versions")]
    public void CurrentKnowledgeKeyOpensKnowledgeOnly(string key)
    {
        using var ctx = new TestContext();
        using var sidebar = ctx.RenderComponent<GovernanceSidebar>(parameters => parameters
            .Add(p => p.CurrentKey, key)
            .Add(p => p.Spaces, FixtureSpaces()));

        Assert.Contains(
            sidebar.FindAll(".gate-erp-links a"),
            a => string.Equals(
                a.GetAttribute("href"), GovernanceRoutes.VersionsHref, StringComparison.Ordinal));
        Assert.Contains(
            sidebar.FindAll(".gate-erp-links a"),
            a => string.Equals(
                a.GetAttribute("href"), GovernanceRoutes.KbSpaceHref("treasury"), StringComparison.Ordinal));
        Assert.DoesNotContain(
            sidebar.FindAll(".gate-erp-links a"),
            a => string.Equals(
                a.GetAttribute("href"), GovernanceRoutes.RulesHref, StringComparison.Ordinal));
    }
}

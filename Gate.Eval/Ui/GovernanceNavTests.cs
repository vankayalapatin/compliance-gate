// Governance routes, modules, citation hrefs, version staleness, and entry
// JSON: pure derivations with local fixture data only.
using Gate.Compliance;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Eval.Ui;

public sealed class GovernanceNavTests
{
    [Fact]
    public void ComplianceLinksAreExact()
    {
        // Exceptions stays routable but is not listed in the sidebar.
        Assert.Equal(
            ["rules", "memos"],
            GovernanceModules.ComplianceLinks.Select(l => l.Slug).ToList());
        Assert.Equal(
            ["Rules", "Memos"],
            GovernanceModules.ComplianceLinks.Select(l => l.Title).ToList());
        Assert.True(GovernanceModules.IsUnlistedCompliance("exceptions"));
        Assert.False(GovernanceModules.IsUnlistedCompliance("rules"));
    }

    [Fact]
    public void ComplianceHrefsAreExact()
    {
        Assert.Equal("governance/rules", GovernanceRoutes.RulesHref);
        Assert.Equal("governance/memos", GovernanceRoutes.MemosHref);
        Assert.Equal("governance/exceptions", GovernanceRoutes.ExceptionsHref);
        Assert.Equal("governance/versions", GovernanceRoutes.VersionsHref);
    }

    [Fact]
    public void KbSpaceHrefIsExact()
    {
        Assert.Equal("governance/kb/treasury", GovernanceRoutes.KbSpaceHref("treasury"));
    }

    [Fact]
    public void CitationHrefPinsVersionSectionAndEntry()
    {
        var href = GovernanceRoutes.CitationHref(new KbCitation("items", "WIDGET-1", "kbv9"));

        Assert.Equal("governance/kb/kbv9/items/WIDGET-1", href);
    }

    [Fact]
    public void CitationHrefEscapesSegments()
    {
        var href = GovernanceRoutes.KbEntryHref("kbv9", "wiki section", "entry id");

        Assert.Equal("governance/kb/kbv9/wiki%20section/entry%20id", href);
    }

    [Fact]
    public void ModuleForRoutesComplianceKnowledgeAndUnknown()
    {
        Assert.Equal(GovernanceModules.Compliance, GovernanceModules.ModuleFor("rules"));
        Assert.Equal(GovernanceModules.Compliance, GovernanceModules.ModuleFor("memos"));
        Assert.Equal(GovernanceModules.Compliance, GovernanceModules.ModuleFor("exceptions"));
        Assert.Equal(GovernanceModules.Knowledge, GovernanceModules.ModuleFor("kb:treasury"));
        Assert.Equal(GovernanceModules.Knowledge, GovernanceModules.ModuleFor("versions"));
        Assert.Null(GovernanceModules.ModuleFor("nope"));
    }

    [Fact]
    public void TitleForNamesComplianceAndSpaces()
    {
        Assert.Equal("Rules", GovernanceModules.TitleFor("rules"));
        Assert.Equal("KB space treasury", GovernanceModules.TitleFor("kb:treasury"));
    }

    [Theory]
    [InlineData("kbv4", "kbv9", true)]
    [InlineData("kbv9", "kbv9", false)]
    [InlineData("kbv10", "kbv9", false)]
    [InlineData(null, "kbv9", false)]
    [InlineData("kbv9", "", false)]
    public void NeedsReviewFlagsOnlyOlderPins(string? proposalKb, string currentKb, bool expected)
    {
        Assert.Equal(expected, KbVersionCompare.NeedsReview(proposalKb, currentKb));
    }

    [Fact]
    public void NeedsReviewFlagsUnparseableMismatch()
    {
        Assert.True(KbVersionCompare.NeedsReview("v-next", "kbv9"));
    }

    [Fact]
    public void EntryJsonRoundTripsStructuredAndWikiEntries()
    {
        var snapshot = new KbSnapshot(
            "kbv9", "hash9",
            [new KbLegalEntity("LE-9", "Fixture Co", "US", "USD")],
            [new KbSite("SITE-9", "LE-9", "north", ["dock-camera"])],
            new KbFiscalCalendar("Dec", "FY26", ["P1"]),
            [new KbApprovalEntry("sourcing-policy", "approver", "Terry Owner")],
            [new KbItem("BAT-9", true)],
            [new KbSourcingPolicy("origin-cap", new Dictionary<string, string> { ["cap"] = "0.60" })],
            [new KbRegionRule("north", ["US", "MX"], "kbv9")],
            [new KbDocument("un383-template", "UN38.3", true)],
            [new KbWikiEntry("w-lead", "Lead", "Body")],
            [], [], [], [], []);

        Assert.Contains("LE-9", KbEntries.EntryJson(snapshot, KbCitations.LegalEntities, "LE-9"));
        Assert.Contains("SITE-9", KbEntries.EntryJson(snapshot, KbCitations.SitesWarehouses, "SITE-9"));
        Assert.Contains("FY26", KbEntries.EntryJson(snapshot, KbCitations.FiscalCalendar, KbCitations.FiscalCalendarCurrent));
        Assert.Contains("sourcing-policy", KbEntries.EntryJson(snapshot, KbCitations.ApprovalMatrix, "sourcing-policy"));
        Assert.Contains("BAT-9", KbEntries.EntryJson(snapshot, KbCitations.Items, "BAT-9"));
        Assert.Contains("origin-cap", KbEntries.EntryJson(snapshot, KbCitations.SourcingPolicies, "origin-cap"));
        Assert.Contains("north", KbEntries.EntryJson(snapshot, KbCitations.RegionRules, "north"));
        Assert.Contains("un383-template", KbEntries.EntryJson(snapshot, KbCitations.Documents, "un383-template"));
        Assert.Contains("w-lead", KbEntries.EntryJson(snapshot, KbCitations.WikiLeadership, "w-lead"));
    }

    [Fact]
    public void EntryJsonIsNullForUnknownSectionOrEntry()
    {
        var snapshot = MemoFixtures.TestKb();

        Assert.Null(KbEntries.EntryJson(snapshot, KbCitations.Items, "missing"));
        Assert.Null(KbEntries.EntryJson(snapshot, "nope", "missing"));
        Assert.Null(KbEntries.EntryJson(snapshot, KbCitations.FiscalCalendar, "wrong-id"));
    }
}

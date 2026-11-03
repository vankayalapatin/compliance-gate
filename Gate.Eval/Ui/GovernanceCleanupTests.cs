// Governance cleanup proofs: per-rule pack versions and dates,
// proposal-level company owners, resolution citations in grounded
// entries, blank empty used-by cells, ERP item links, and the
// unlisted-but-routable Exceptions page. Test-local data only.
using Bunit;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Web.Components.Governance;
using Gate.Web.Components.Pages.Governance;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.AspNetCore.Components;

namespace Gate.Eval.Ui;

public sealed class GovernanceCleanupTests
{
    private static readonly DateTimeOffset FixedAt = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static LivePackRule LiveRule(
        string ruleId, string packVersion, DateOnly adopted, string owner = "Pack Owner") =>
        new("PACK-T", packVersion, ruleId, null, RuleTier.Tier2, MemoRuleGate.Vendor,
            $"Fixture {ruleId}.", true, owner, adopted, $"Fixture source for {ruleId}.");

    private static MemoFile MemoWithActivation(
        RuleProposal proposal, string policyOwner, RuleActivation activation, string memoId = "MEMO-9") =>
        new(new PolicyMemo(memoId, FixedAt, "Board", "Excerpt."), "Full.",
            [new MemoProposalRecord(proposal, new ProposalValidation(true, [], []), null, policyOwner)],
            [], [],
            [activation]);

    private static RuleActivation ActivationFor(
        RuleProposal proposal, string ruleId = "PACK-T-02", string approver = "Amy Approver") =>
        new("ACT-9", ruleId, "PACK-T", "v2", "v1", "MEMO-9", approver,
            new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            RuleApplicability.ProspectiveOnly, proposal.ProposalHash, FixedAt, "kbv9");

    [Fact]
    public void EachLiveRowShowsItsOwnPackVersionAndAdoptionDate()
    {
        var live = new List<LivePackRule>
        {
            LiveRule("PACK-T-01", "v1", new DateOnly(2026, 3, 16)),
            LiveRule("PACK-T-02", "v2", new DateOnly(2026, 6, 2)),
        };

        var rows = GovernanceRuleBoard.BuildRows(
            live, [], new Dictionary<string, int>(StringComparer.Ordinal), "kbv9");

        var first = rows.Single(r => r.RuleId == "PACK-T-01");
        Assert.Equal("v1", first.PackVersion);
        Assert.Equal("2026-03-16", first.EffectiveOrAdopted);
        var second = rows.Single(r => r.RuleId == "PACK-T-02");
        Assert.Equal("v2", second.PackVersion);
        Assert.Equal("2026-06-02", second.EffectiveOrAdopted);
    }

    [Fact]
    public void CompanyRowShowsItsOwnVersionOwnerAndEffectiveDate()
    {
        var proposal = MemoFixtures.Proposal(
            citations: [MemoFixtures.Cite(KbCitations.Items, "BAT-9", "kbv9")],
            proposalId: "PROP-9", memoId: "MEMO-9");
        var memo = MemoWithActivation(proposal, "Polly Owner", ActivationFor(proposal));

        var rows = GovernanceRuleBoard.BuildRows(
            [LiveRule("PACK-T-01", "v1", new DateOnly(2026, 3, 16))],
            [memo], new Dictionary<string, int>(StringComparer.Ordinal), "kbv9");

        var company = rows.Single(r => r.IsCompanyRule);
        Assert.Equal("v2", company.PackVersion);
        Assert.Equal("Polly Owner", company.Owner);
        Assert.NotEqual("Pack Owner", company.Owner);
        Assert.Equal("2026-12-01", company.EffectiveOrAdopted);
    }

    [Fact]
    public void UnmatchedActivationFallsBackToItsApprover()
    {
        var proposal = MemoFixtures.Proposal(proposalId: "PROP-9", memoId: "MEMO-9");
        var orphan = ActivationFor(proposal) with { ProposalHash = "unmatched", KbVersion = null };
        var memo = MemoWithActivation(proposal, "Polly Owner", orphan);

        var rows = GovernanceRuleBoard.BuildRows(
            [LiveRule("PACK-T-01", "v1", new DateOnly(2026, 3, 16))],
            [memo], new Dictionary<string, int>(StringComparer.Ordinal), "kbv9");

        var company = rows.Single(r => r.IsCompanyRule);
        Assert.Equal("Amy Approver", company.Owner);
        Assert.Empty(company.GroundedEntries);
    }

    [Fact]
    public void GroundedEntriesIncludeResolutionOnlyCitations()
    {
        var proposal = MemoFixtures.Proposal(
            resolutions:
            [
                new EntityResolution(
                    "cells", "BAT-9", MemoFixtures.Cite(KbCitations.Items, "BAT-9", "kbv9")),
            ],
            citations: [],
            proposalId: "PROP-9", memoId: "MEMO-9");
        var memo = MemoWithActivation(proposal, "Polly Owner", ActivationFor(proposal));

        var rows = GovernanceRuleBoard.BuildRows(
            [LiveRule("PACK-T-01", "v1", new DateOnly(2026, 3, 16))],
            [memo], new Dictionary<string, int>(StringComparer.Ordinal), "kbv9");

        var grounded = Assert.Single(rows.Single(r => r.IsCompanyRule).GroundedEntries);
        Assert.Equal(KbCitations.Items, grounded.Section);
        Assert.Equal("BAT-9", grounded.EntryId);
        Assert.Equal("kbv9", grounded.KbVersion);
    }

    [Fact]
    public void GroundedEntriesDedupeCitationAndResolution()
    {
        var citation = MemoFixtures.Cite(KbCitations.Items, "BAT-9", "kbv9");
        var proposal = MemoFixtures.Proposal(
            resolutions: [new EntityResolution("cells", "BAT-9", citation)],
            citations: [citation],
            proposalId: "PROP-9", memoId: "MEMO-9");
        var memo = MemoWithActivation(proposal, "Polly Owner", ActivationFor(proposal));

        var rows = GovernanceRuleBoard.BuildRows(
            [LiveRule("PACK-T-01", "v1", new DateOnly(2026, 3, 16))],
            [memo], new Dictionary<string, int>(StringComparer.Ordinal), "kbv9");

        Assert.Single(rows.Single(r => r.IsCompanyRule).GroundedEntries);
    }

    private static async Task<string> RenderSpaceAsync(string space)
    {
        var w = await CrawlerTests.CreateWorldAsync();
        w.Engine.Spaces.Add(new KbSpace(
            "ops", "Opal Owner", "approver",
            [KbCitations.Items, KbCitations.ApprovalMatrix], ["Opal Owner"]));
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        using var page = ctx.RenderComponent<GovernanceKbSpace>(
            p => p.Add(k => k.Space, space));
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        return page.Markup;
    }

    [Fact]
    public async Task EmptyUsedByCellsRenderNothingButKeepTheColumn()
    {
        var markup = await RenderSpaceAsync("ops");

        Assert.Contains("Used by", markup);
        Assert.DoesNotContain("unused", markup);
    }

    [Fact]
    public async Task EmptyUsedByCellHasNoText()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        w.Engine.Spaces.Add(new KbSpace(
            "ops", "Opal Owner", "approver",
            [KbCitations.Items, KbCitations.ApprovalMatrix], ["Opal Owner"]));
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        using var page = ctx.RenderComponent<GovernanceKbSpace>(
            p => p.Add(k => k.Space, "ops"));
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));

        var row = page.FindAll("tbody tr")
            .Single(r => r.TextContent.Contains("BAT-9", StringComparison.Ordinal));
        var cells = row.QuerySelectorAll("td");

        Assert.Equal(string.Empty, cells.Last().TextContent.Trim());
    }

    [Fact]
    public async Task CitedEntriesStillShowTheirHits()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        w.Engine.Spaces.Add(new KbSpace(
            "ops", "Opal Owner", "approver",
            [KbCitations.Items, KbCitations.ApprovalMatrix], ["Opal Owner"]));
        var proposal = MemoFixtures.Proposal(
            citations: [MemoFixtures.Cite(KbCitations.Items, "BAT-9", "kbv9")],
            proposalId: "PROP-9", memoId: "MEMO-9");
        await w.Memos.WriteAsync(MemoWithActivation(
            proposal, "Polly Owner", ActivationFor(proposal)));
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        using var page = ctx.RenderComponent<GovernanceKbSpace>(
            p => p.Add(k => k.Space, "ops"));
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));

        var row = page.FindAll("tbody tr")
            .Single(r => r.TextContent.Contains("BAT-9", StringComparison.Ordinal));

        Assert.Contains("1 proposal", row.TextContent);
    }

    [Fact]
    public async Task ItemsSpaceRowsLinkToTheirErpItemRecords()
    {
        var markup = await RenderSpaceAsync("ops");

        Assert.Contains("erp/record/item/BAT-9", markup);
        Assert.Contains("erp/record/item/SCR-9", markup);
    }

    [Fact]
    public async Task ItemsEntryLinksToItsErpItemRecord()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        using var page = ctx.RenderComponent<GovernanceKbEntry>(p => p
            .Add(k => k.Version, "kbv9")
            .Add(k => k.Section, KbCitations.Items)
            .Add(k => k.EntryId, "BAT-9"));
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));

        Assert.Contains("erp/record/item/BAT-9", page.Markup);
    }

    [Fact]
    public async Task NonItemsEntriesCarryNoErpItemLink()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        using var page = ctx.RenderComponent<GovernanceKbEntry>(p => p
            .Add(k => k.Version, "kbv9")
            .Add(k => k.Section, "finance")
            .Add(k => k.EntryId, "w-c1"));
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("erp/record/", page.Markup);
    }

    [Fact]
    public async Task ApprovalMatrixEntryShowsItsApproverWithNoDeadUserLink()
    {
        // No ERP user record route exists, so the approver name must stay
        // plain text: a link to a route that refuses would be a dead link.
        var w = await CrawlerTests.CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        using var page = ctx.RenderComponent<GovernanceKbEntry>(p => p
            .Add(k => k.Version, "kbv9")
            .Add(k => k.Section, KbCitations.ApprovalMatrix)
            .Add(k => k.EntryId, "sourcing-policy"));
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));

        Assert.Contains("Terry Owner", page.Markup);
        Assert.DoesNotContain("erp/record/user", page.Markup);
    }

    [Fact]
    public async Task RulesPageShowsOwnVersionsDatesAndPlainEmptyGrounding()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        using var page = ctx.RenderComponent<GovernanceRules>();
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));

        Assert.Contains("Pack version", page.Markup);
        Assert.Contains("Adopted / Effective", page.Markup);
        Assert.Contains("CORP-T", page.Markup);
        Assert.Contains(GovernanceLabels.ShippedRuleGrounding, page.Markup);
    }

    [Fact]
    public void SidebarHidesExceptionsButKeepsItsModuleAndRoute()
    {
        using var ctx = new TestContext();
        var spaces = new List<KbSpace>
        {
            new("treasury", "Terry Owner", "approver", ["finance"], ["Terry Owner"]),
        };
        using var sidebar = ctx.RenderComponent<GovernanceSidebar>(parameters => parameters
            .Add(p => p.CurrentKey, GovernanceModules.ExceptionsSlug)
            .Add(p => p.Spaces, spaces));

        var hrefs = sidebar.FindAll(".gate-erp-links a")
            .Select(a => a.GetAttribute("href"))
            .ToList();

        Assert.DoesNotContain(hrefs, h =>
            string.Equals(h, GovernanceRoutes.ExceptionsHref, StringComparison.Ordinal));
        Assert.Contains(hrefs, h =>
            string.Equals(h, GovernanceRoutes.RulesHref, StringComparison.Ordinal));
        Assert.DoesNotContain(
            GovernanceModules.ComplianceLinks,
            l => string.Equals(l.Slug, GovernanceModules.ExceptionsSlug, StringComparison.Ordinal));
        Assert.Equal("governance/exceptions", GovernanceRoutes.ExceptionsHref);
        Assert.Equal(
            GovernanceModules.Compliance,
            GovernanceModules.ModuleFor(GovernanceModules.ExceptionsSlug));
        Assert.True(GovernanceModules.IsKnownCompliance(GovernanceModules.ExceptionsSlug));
    }

    [Fact]
    public async Task ExceptionsPageLoadsOnDirectVisit()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        using var page = ctx.RenderComponent<GovernanceExceptions>();
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));

        Assert.Contains("Exceptions", page.Markup);
        Assert.DoesNotContain("failed to load", page.Markup);
    }
}

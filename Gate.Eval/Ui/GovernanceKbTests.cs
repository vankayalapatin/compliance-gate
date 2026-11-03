// Governance KB views and edit path: entry projection, space/version/
// pinned-entry reads through the fake seam, delegation resolution, and
// every publish refusal. Fixture spaces, owners, and versions only.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Gate.Web.ViewModels;

namespace Gate.Eval.Ui;

public sealed class GovernanceKbTests
{
    private static readonly DateTimeOffset FixedAt = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static KbSnapshot FixtureKb() => new(
        "kbv9", "hash9",
        [new KbLegalEntity("LE-9", "Fixture Co", "US", "USD",
            "kbv9", "sys", FixedAt, "sys", FixedAt, "Terry Owner", "Terry Owner",
            KbEntryStatus.Active, new DateOnly(2026, 11, 1))],
        [new KbSite("SITE-9", "LE-9", "north", ["dock-camera"])],
        new KbFiscalCalendar("Dec", "FY26", ["P1"]),
        [new KbApprovalEntry("sourcing-policy", "approver", "Terry Owner")],
        [new KbItem("BAT-9", true)],
        [new KbSourcingPolicy("origin-cap", new Dictionary<string, string> { ["cap"] = "0.60" })],
        [new KbRegionRule("north", ["US", "MX"], "kbv9")],
        [new KbDocument("un383-template", "UN38.3", true)],
        [new KbWikiEntry("w-lead", "Lead", "First line.\nSecond line.")],
        [], [], [], [], []);

    private static KbSpace FixtureSpace() => new(
        "treasury", "Terry Owner", "approver",
        [KbCitations.LegalEntities, KbCitations.WikiLeadership, KbCitations.Items],
        ["Terry Owner"]);

    [Fact]
    public void EntriesProjectEverySectionWithStamps()
    {
        var snapshot = FixtureKb();

        var entity = Assert.Single(KbEntries.FromSnapshot(snapshot, KbCitations.LegalEntities));
        Assert.Equal("LE-9", entity.EntryId);
        Assert.Equal("Fixture Co", entity.Title);
        Assert.Contains("USD", entity.Summary);
        Assert.Equal("Terry Owner", entity.Owner);
        Assert.Equal("Active", entity.Status);
        Assert.Equal("kbv9", entity.Version);
        Assert.Equal("2026-11-01", entity.Effective);
        Assert.Equal("Terry Owner", entity.ApprovedBy);

        Assert.Single(KbEntries.FromSnapshot(snapshot, KbCitations.SitesWarehouses));
        var fiscal = Assert.Single(KbEntries.FromSnapshot(snapshot, KbCitations.FiscalCalendar));
        Assert.Equal(KbCitations.FiscalCalendarCurrent, fiscal.EntryId);
        Assert.Single(KbEntries.FromSnapshot(snapshot, KbCitations.ApprovalMatrix));
        Assert.Single(KbEntries.FromSnapshot(snapshot, KbCitations.Items));
        Assert.Single(KbEntries.FromSnapshot(snapshot, KbCitations.SourcingPolicies));
        Assert.Single(KbEntries.FromSnapshot(snapshot, KbCitations.RegionRules));
        Assert.Single(KbEntries.FromSnapshot(snapshot, KbCitations.Documents));

        var wiki = Assert.Single(KbEntries.FromSnapshot(snapshot, KbCitations.WikiLeadership));
        Assert.Equal("First line.", wiki.Summary);
        Assert.Contains("Second line.", wiki.Detail);
    }

    [Fact]
    public void EntriesRefuseUnknownSections()
    {
        Assert.Throws<InvalidOperationException>(() =>
            KbEntries.FromSnapshot(FixtureKb(), "nope"));
    }

    [Fact]
    public async Task ViewServiceBuildsKnownSpacesAndRefusesUnknown()
    {
        var engine = new GovernanceFakeEngine(FixtureKb());
        engine.Spaces.Add(FixtureSpace());
        var views = new KbViewService(engine, UiTestLogger<KbViewService>.Instance);

        var space = await views.BuildSpaceAsync("treasury");

        Assert.Equal("Terry Owner", space.Space.Owner);
        Assert.Equal("kbv9", space.CurrentVersion);
        Assert.Equal(3, space.Sections.Count);
        Assert.Single(space.EntriesBySection[KbCitations.LegalEntities]);
        Assert.Empty(space.Notes);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            views.BuildSpaceAsync("missing"));
        Assert.Contains("treasury", refused.Message);
    }

    [Fact]
    public async Task ViewServiceProbesVersionsUntilTheFirstUnknown()
    {
        var engine = new GovernanceFakeEngine(FixtureKb());
        engine.SnapshotsByVersion["kbv1"] = MemoFixtures.TestKb(version: "kbv1", hash: "hash1");
        engine.SnapshotsByVersion["kbv2"] = MemoFixtures.TestKb(version: "kbv2", hash: "hash2");
        var views = new KbViewService(engine, UiTestLogger<KbViewService>.Instance);

        var versions = await views.ListVersionsAsync();

        Assert.Equal(["kbv1", "kbv2"], versions);
    }

    [Fact]
    public async Task ViewServiceFindsPinnedEntriesWithUsedBy()
    {
        var engine = new GovernanceFakeEngine(FixtureKb());
        var views = new KbViewService(engine, UiTestLogger<KbViewService>.Instance);
        var proposal = MemoFixtures.Proposal(
            citations: [MemoFixtures.Cite("items", "BAT-9")],
            proposalId: "PROP-1", memoId: "MEMO-1");
        var memos = new List<MemoFile>
        {
            new(new PolicyMemo("MEMO-1", FixedAt, "Board", "Excerpt."), "Full.",
                [new MemoProposalRecord(proposal, new ProposalValidation(true, [], []), null, "Terry Owner")],
                [], [],
                [new RuleActivation(
                    "ACT-1", "PACK-T-02", "PACK-T", "v2", "v1", "MEMO-1", "Terry Owner",
                    FixedAt, RuleApplicability.ProspectiveOnly, proposal.ProposalHash, FixedAt, "kbv9")]),
        };

        var detail = await views.FindEntryAsync("kbv9", "items", "BAT-9", memos);

        Assert.Equal("BAT-9", detail.Entry.EntryId);
        Assert.Equal("kbv9", detail.SnapshotVersion);
        Assert.Equal("PROP-1", Assert.Single(detail.UsedBy.Proposals).ProposalId);
        Assert.Equal(["PACK-T-02"], detail.UsedBy.Rules);
    }

    [Fact]
    public async Task ViewServiceFlagsStalePinsAndRefusesUnknowns()
    {
        var engine = new GovernanceFakeEngine(FixtureKb());
        engine.SnapshotsByVersion["kbv4"] = MemoFixtures.TestKb(version: "kbv4", hash: "hash4");
        var views = new KbViewService(engine, UiTestLogger<KbViewService>.Instance);

        var stale = await views.FindEntryAsync("kbv4", "items", "BAT-9", []);
        Assert.Contains(stale.Notes, n => n.Contains("version 9"));
        Assert.DoesNotContain(stale.Notes, n => n.Contains("kbv"));

        var missingEntry = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            views.FindEntryAsync("kbv9", "items", "missing", []));
        Assert.Contains("missing", missingEntry.Message);

        var missingVersion = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            views.FindEntryAsync("kbv7", "items", "BAT-9", []));
        Assert.Contains("kbv7", missingVersion.Message);
    }

    [Theory]
    [InlineData("Terry Owner", "Terry Owner", true, null)]
    [InlineData("Terry Owner", "terry owner", true, null)]
    [InlineData("Terry Owner", "Dave Delegate", false, "Terry Owner")]
    public void DelegationResolvesDirectAndActiveCover(
        string owner, string acting, bool direct, string? approvedFor)
    {
        var today = new DateOnly(2026, 11, 1);
        var delegations = new List<DelegationDto>
        {
            new("Terry Owner", "Dave Delegate", new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 10)),
        };

        var resolved = GovernanceDelegation.TryResolve(owner, acting, delegations, today);

        Assert.NotNull(resolved);
        Assert.Equal(direct, resolved.IsDirect);
        Assert.Equal(approvedFor, resolved.ApprovedFor);
    }

    [Theory]
    [InlineData("2026-10-19")]
    [InlineData("2026-11-11")]
    public void DelegationRefusesOutsideTheWindow(string day)
    {
        var delegations = new List<DelegationDto>
        {
            new("Terry Owner", "Dave Delegate", new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 10)),
        };

        Assert.Null(GovernanceDelegation.TryResolve(
            "Terry Owner", "Dave Delegate", delegations, DateOnly.Parse(day)));
    }

    [Fact]
    public void DelegationRefusesStrangers()
    {
        Assert.Null(GovernanceDelegation.TryResolve(
            "Terry Owner", "Stranger", [], new DateOnly(2026, 11, 1)));
    }

    private static KbEditService EditService(
        GovernanceFakeEngine engine, IErpAdapter erp) =>
        new(engine, erp, UiTestLogger<KbEditService>.Instance);

    private static GovernanceFakeEngine SpaceEngine()
    {
        var engine = new GovernanceFakeEngine(FixtureKb());
        engine.Spaces.Add(FixtureSpace());
        return engine;
    }

    [Fact]
    public async Task PublishRefusesBlankAuthorReasonAndContent()
    {
        var service = EditService(SpaceEngine(), SeededErp.Create());

        var author = await service.PublishAsync(
            "treasury", "leadership", "w-1", KbChangeKind.Add, "{}", "reason", "  ");
        Assert.False(author.Ok);
        Assert.Contains("Acting user", author.Message);

        var reason = await service.PublishAsync(
            "treasury", "leadership", "w-1", KbChangeKind.Add, "{}", "  ", "Terry Owner");
        Assert.False(reason.Ok);

        var content = await service.PublishAsync(
            "treasury", "leadership", "w-1", KbChangeKind.Add, "  ", "reason", "Terry Owner");
        Assert.False(content.Ok);
    }

    [Fact]
    public async Task PublishRefusesUnknownSpacesAndNonOwners()
    {
        var erp = SeededErp.Create();
        var service = EditService(SpaceEngine(), erp);

        var space = await service.PublishAsync(
            "missing", "leadership", "w-1", KbChangeKind.Add, "{}", "reason", "approver");
        Assert.False(space.Ok);
        Assert.Contains("treasury", space.Message);

        var stranger = await service.PublishAsync(
            "treasury", "leadership", "w-1", KbChangeKind.Add, "{}", "reason", "finance",
            new DateOnly(2026, 11, 1));
        Assert.False(stranger.Ok);
        Assert.Contains("Terry Owner", stranger.Message);
        Assert.Equal("Terry Owner", stranger.Owner);
    }

    [Fact]
    public async Task PublishSendsDelegatedApprovalsThroughTheSeam()
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedUser(new UserDto("Terry Owner", "approver", 100_000m, "USD"));
        erp.SeedUser(new UserDto("Dave Delegate", "delegate", 50_000m, "USD"));
        erp.SeedDelegation(new DelegationDto(
            "Terry Owner", "Dave Delegate", new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 10)));
        var engine = SpaceEngine();
        engine.PublishHook = (baseVersion, changes, approvedBy, reason, activationId) =>
            new KbPublication(
                $"company-kb.kbv10.json", "{}",
                new KbVersionActivation(
                    activationId, "kbv10", baseVersion, "treasury", approvedBy,
                    FixedAt, reason, ["leadership#w-1"], "Terry Owner"));
        var service = EditService(engine, erp);

        var outcome = await service.PublishAsync(
            "treasury", "leadership", "w-1", KbChangeKind.Add,
            """{"id":"w-1","title":"T","body":"B"}""", "delegate seeds the wiki", "Dave Delegate",
            new DateOnly(2026, 11, 1));

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal("kbv10", outcome.NewVersion);
        Assert.Equal("Terry Owner", outcome.Owner);
        Assert.Equal("Terry Owner", outcome.ApprovedFor);
        Assert.Contains("Dave Delegate", outcome.Message);
        Assert.Contains("Terry Owner", outcome.Message);
        var publish = Assert.Single(engine.Publishes);
        Assert.Equal("kbv9", publish.BaseVersion);
        Assert.Equal("Dave Delegate", publish.ApprovedBy);
        Assert.Equal("Terry Owner", publish.ApprovedFor);
        Assert.Equal("Dave Delegate", Assert.Single(publish.Changes).ChangedBy);
    }

    [Fact]
    public async Task PublishRefusesExpiredDelegations()
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedUser(new UserDto("Terry Owner", "approver", 100_000m, "USD"));
        erp.SeedUser(new UserDto("Dave Delegate", "delegate", 50_000m, "USD"));
        erp.SeedDelegation(new DelegationDto(
            "Terry Owner", "Dave Delegate", new DateOnly(2026, 10, 20), new DateOnly(2026, 11, 10)));
        var engine = SpaceEngine();
        var service = EditService(engine, erp);

        var outcome = await service.PublishAsync(
            "treasury", "leadership", "w-1", KbChangeKind.Add,
            """{"id":"w-1","title":"T","body":"B"}""", "reason", "Dave Delegate",
            new DateOnly(2026, 11, 11));

        Assert.False(outcome.Ok);
        Assert.Contains("Dave Delegate", outcome.Message);
        Assert.Contains("Terry Owner", outcome.Message);
        Assert.Equal("Terry Owner", outcome.Owner);
        Assert.Empty(engine.Publishes);
    }

    [Fact]
    public async Task PublishSendsDirectOwnerApprovalsThroughTheSeam()
    {
        var erp = SeededErp.Create();
        erp.SeedUser(new UserDto("Terry Owner", "approver", 100_000m, "USD"));
        var engine = SpaceEngine();
        engine.PublishHook = (baseVersion, changes, approvedBy, reason, activationId) =>
            new KbPublication(
                $"company-kb.kbv10.json", "{}",
                new KbVersionActivation(
                    activationId, "kbv10", baseVersion, "treasury", approvedBy,
                    FixedAt, reason, ["leadership#w-1"]));
        var service = EditService(engine, erp);

        var outcome = await service.PublishAsync(
            "treasury", "leadership", "w-1", KbChangeKind.Add,
            """{"id":"w-1","title":"T","body":"B"}""", "seed the wiki", "Terry Owner",
            new DateOnly(2026, 11, 1));

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Equal("kbv10", outcome.NewVersion);
        Assert.Equal("Terry Owner", outcome.Owner);
        Assert.Null(outcome.ApprovedFor);
        var publish = Assert.Single(engine.Publishes);
        Assert.Equal("kbv9", publish.BaseVersion);
        Assert.Equal("Terry Owner", publish.ApprovedBy);
        Assert.Null(publish.ApprovedFor);
        Assert.Equal("seed the wiki", publish.Reason);
        var change = Assert.Single(publish.Changes);
        Assert.Equal("leadership", change.Section);
        Assert.Equal("w-1", change.EntryId);
        Assert.Equal(KbChangeKind.Add, change.Kind);
        Assert.Equal("Terry Owner", change.ChangedBy);
    }

    [Fact]
    public async Task PublishSurfacesSeamRefusals()
    {
        var erp = SeededErp.Create();
        erp.SeedUser(new UserDto("Terry Owner", "approver", 100_000m, "USD"));
        var engine = SpaceEngine();
        engine.PublishHook = (_, _, _, _, _) =>
            throw new InvalidOperationException("Stale KB base: the caller pins 'kbv9'.");
        var service = EditService(engine, erp);

        var outcome = await service.PublishAsync(
            "treasury", "leadership", "w-1", KbChangeKind.Add, "{}", "reason", "Terry Owner",
            new DateOnly(2026, 11, 1));

        Assert.False(outcome.Ok);
        Assert.Contains("Stale KB base", outcome.Message);
        Assert.Equal("Terry Owner", outcome.Owner);
    }
}

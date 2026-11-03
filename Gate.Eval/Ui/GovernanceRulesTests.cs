// Governance Rules and Exceptions derivations: block-count scans across
// vendor/dock/pre-pay events with verified dock coverage, stale company
// rows, used-by joins, exception rows, and the service end to end over
// temp case-cache plus memo files. Fixture data only.
using Gate.Agent;
using Gate.Contracts;
using Gate.Web.Services;
using Gate.Web.ViewModels;

namespace Gate.Eval.Ui;

public sealed class GovernanceRulesTests
{
    private static readonly DateTimeOffset FixedAt = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static CaseEvent Event(
        string eventId, string caseId, CaseEventKind kind, string? ruleId, string summary = "note") =>
        new(eventId, caseId, FixedAt, "P2pAgent", kind, summary, ruleId, null, null, null, null, null);

    private static CaseFile CaseWith(string caseId, params CaseEvent[] events)
    {
        var kase = new Case(
            caseId, "SO-T1", "WIDGET-1", "gov-test", CaseStage.VendorScreened,
            FixedAt, FixedAt, null, null, null, null, [], [], null, null);
        return new CaseFile(kase, events, []);
    }

    private static CaseFile CaseWithDecisions(string caseId, params Decision[] decisions)
    {
        var kase = new Case(
            caseId, "SO-T1", "WIDGET-1", "gov-test", CaseStage.VendorScreened,
            FixedAt, FixedAt, null, null, null, null, [], [], null, null);
        return new CaseFile(kase, [], decisions);
    }

    [Fact]
    public void BlockScanCountsVendorDockAndPrepayRuleIds()
    {
        var files = new List<CaseFile>
        {
            CaseWith("CASE-1",
            [
                Event("CASE-1-e001", "CASE-1", CaseEventKind.VendorScreened, "CORP-T1", "failed screening"),
                Event("CASE-1-e002", "CASE-1", CaseEventKind.VendorBlocked, "CORP-T1", "blocked"),
                Event("CASE-1-e003", "CASE-1", CaseEventKind.Note, "ELEC-T9", "pre-pay blocked"),
                Event("CASE-1-e004", "CASE-1", CaseEventKind.DockEvaluated, null, "dock evaluated"),
            ]),
            CaseWith("CASE-2",
            [
                Event("CASE-2-e001", "CASE-2", CaseEventKind.HoldSet, "DOCK-T3", "carton held"),
                Event("CASE-2-e002", "CASE-2", CaseEventKind.ReviewOpened, "DOCK-T5", "damage review"),
                Event("CASE-2-e003", "CASE-2", CaseEventKind.Note, "DOCK-T3", "off-ASN hold kept agent-side"),
                Event("CASE-2-e004", "CASE-2", CaseEventKind.HoldReleased, "DOCK-T3", "released, not a block"),
                Event("CASE-2-e005", "CASE-2", CaseEventKind.ReviewClosed, "DOCK-T5", "closed, not a block"),
                Event("CASE-2-e006", "CASE-2", CaseEventKind.Note, null, "plain note"),
            ]),
        };

        var scan = RuleBlockScan.CountBlocks(files);

        Assert.Equal(2, scan.FilesScanned);
        Assert.Equal(2, scan.Counts["CORP-T1"]);
        Assert.Equal(1, scan.Counts["ELEC-T9"]);
        Assert.Equal(2, scan.Counts["DOCK-T3"]);
        Assert.Equal(1, scan.Counts["DOCK-T5"]);
        Assert.Equal(1, scan.DockEvaluatedFiles);
        Assert.Contains("1 case", scan.DockNote);
    }

    [Fact]
    public void BlockScanNotesMissingDockCoverage()
    {
        var scan = RuleBlockScan.CountBlocks([CaseWith("CASE-1", [])]);

        Assert.Equal(0, scan.DockEvaluatedFiles);
        Assert.Equal(GovernanceLabels.DockNoCoverage, scan.DockNote);
    }

    [Fact]
    public void BuildRowsJoinsLiveCompanyBlocksAndStale()
    {
        var live = new List<LivePackRule>
        {
            new("PACK-T", "v1", "PACK-T-01", null, RuleTier.Tier2, MemoRuleGate.Vendor,
                "Fixture rule.", true, "Terry Owner", new DateOnly(2026, 3, 16),
                "Fixture pack source."),
        };
        var proposal = MemoFixtures.Proposal(
            citations: [MemoFixtures.Cite("items", "BAT-9", "kbv4")],
            kbVersion: "kbv4", kbHash: "hash4", proposalId: "PROP-9", memoId: "MEMO-9");
        var stale = new RuleActivation(
            "ACT-9", "PACK-T-02", "PACK-T", "v2", "v1", "MEMO-9", "Terry Owner",
            new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            RuleApplicability.ProspectiveOnly, proposal.ProposalHash, FixedAt, "kbv4");
        var rollback = stale with
        {
            ActivationId = "ACT-8", RuleId = "PACK-T-01", KbVersion = null,
            ProposalHash = "unmatched",
        };
        var memo = new MemoFile(
            new PolicyMemo("MEMO-9", FixedAt, "Board", "Excerpt."),
            "Full text.",
            [new MemoProposalRecord(proposal, new ProposalValidation(true, [], []), null, "Terry Owner")],
            [], [],
            [stale, rollback]);

        var rows = GovernanceRuleBoard.BuildRows(
            live, [memo],
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["PACK-T-01"] = 3,
                ["PACK-T-02"] = 1,
            },
            "kbv9");

        Assert.Equal(3, rows.Count);
        var shipped = rows.Single(r => !r.IsCompanyRule);
        Assert.Equal("PACK-T-01", shipped.RuleId);
        Assert.Equal(3, shipped.BlockCount);
        Assert.Equal("2026-03-16", shipped.EffectiveOrAdopted);
        Assert.Equal("Fixture pack source.", shipped.Source);
        Assert.Empty(shipped.GroundedEntries);
        Assert.False(shipped.NeedsReview);

        var company = rows.Single(r => r.IsCompanyRule && r.RuleId == "PACK-T-02");
        Assert.Equal("v2", company.PackVersion);
        Assert.Equal("2026-12-01", company.EffectiveOrAdopted);
        Assert.Equal(
            "MEMO-9 by Board, approved by Terry Owner, effective Dec 1, 2026",
            company.Source);
        Assert.Equal(1, company.BlockCount);
        Assert.Equal("Items: BAT 9, v4", PolicyLabels.CitationLabel(Assert.Single(company.GroundedEntries)));
        Assert.True(company.NeedsReview);
        Assert.Contains("version 4", company.NeedsReviewReason);
        Assert.Contains("version 9", company.NeedsReviewReason);
        Assert.DoesNotContain("kbv", company.NeedsReviewReason);
        Assert.Equal("History (v1)", company.HistoryLabel);
        Assert.Contains("ACT-9", company.HistoryHref);

        var rolled = rows.Single(r => r.IsCompanyRule && r.KbVersion is null);
        Assert.False(rolled.NeedsReview);
    }

    [Fact]
    public void BuildRowsCollapsesToCurrentVersionPerRule()
    {
        var proposal = MemoFixtures.Proposal(
            citations: [MemoFixtures.Cite("sourcing-policy", "origin-cap", "kbv9")],
            kbVersion: "kbv9", kbHash: "hash9", proposalId: "PROP-9", memoId: "MEMO-9");
        var old = new RuleActivation(
            "ACT-OLD", "PACK-T-02", "PACK-T", "v1", "", "MEMO-9", "Terry Owner",
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            RuleApplicability.ProspectiveOnly, "hash-old",
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), "kbv1");
        var current = new RuleActivation(
            "ACT-NEW", "PACK-T-02", "PACK-T", "v3", "v2", "MEMO-9", "Terry Owner",
            new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero),
            RuleApplicability.ProspectiveOnly, proposal.ProposalHash,
            new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero), "kbv9");
        var nine = new RuleActivation(
            "ACT-N9", "PACK-T-03", "PACK-T", "v9", "v8", "MEMO-9", "Terry Owner",
            new DateTimeOffset(2026, 12, 4, 0, 0, 0, TimeSpan.Zero),
            RuleApplicability.ProspectiveOnly, "hash-nine",
            new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero), "kbv9");
        var ten = new RuleActivation(
            "ACT-N10", "PACK-T-03", "PACK-T", "v10", "v9", "MEMO-9", "Terry Owner",
            new DateTimeOffset(2026, 12, 3, 0, 0, 0, TimeSpan.Zero),
            RuleApplicability.ProspectiveOnly, "hash-ten",
            new DateTimeOffset(2026, 12, 3, 12, 0, 0, TimeSpan.Zero), "kbv9");
        var memo = new MemoFile(
            new PolicyMemo("MEMO-9", FixedAt, "Board", "Excerpt."),
            "Full text.",
            [new MemoProposalRecord(proposal, new ProposalValidation(true, [], []), null, "Terry Owner")],
            [], [],
            [old, current, nine, ten]);

        var rows = GovernanceRuleBoard.BuildRows(
            [], [memo],
            new Dictionary<string, int>(StringComparer.Ordinal),
            "kbv9");

        Assert.Equal(2, rows.Count);
        var rule2 = rows.Single(r => r.RuleId == "PACK-T-02");
        Assert.Equal("v3", rule2.PackVersion);
        Assert.False(rule2.NeedsReview);
        Assert.Equal("History (v2)", rule2.HistoryLabel);
        Assert.Contains("ACT-NEW", rule2.HistoryHref);
        Assert.Equal(
            "Sourcing policy: origin cap, v9",
            PolicyLabels.CitationLabel(Assert.Single(rule2.GroundedEntries)));

        // Version rank is numeric: v10 beats v9 even though it published
        // earlier ("v10" sorts before "v9" as text).
        var rule3 = rows.Single(r => r.RuleId == "PACK-T-03");
        Assert.Equal("v10", rule3.PackVersion);
        Assert.Equal("History (v9)", rule3.HistoryLabel);
    }

    [Fact]
    public void BuildRowsRefusesLiveRulesWithoutASource()
    {
        var live = new List<LivePackRule>
        {
            new("PACK-T", "v1", "PACK-T-01", null, RuleTier.Tier2, MemoRuleGate.Vendor,
                "Fixture rule.", true, "Terry Owner", new DateOnly(2026, 3, 16)),
        };

        var refused = Assert.Throws<InvalidOperationException>(() =>
            GovernanceRuleBoard.BuildRows(
                live, [], new Dictionary<string, int>(StringComparer.Ordinal), "kbv9"));

        Assert.Contains("PACK-T-01", refused.Message);
        Assert.Contains("PACK-T", refused.Message);
    }

    [Fact]
    public void UsedByFindsCitationsResolutionsAndActivations()
    {
        var citing = MemoFixtures.Proposal(
            resolutions: [new EntityResolution("cells", "BAT-9", MemoFixtures.Cite("items", "BAT-9", "kbv4"))],
            citations: [MemoFixtures.Cite("sourcingPolicies", "origin-cap", "kbv4")],
            kbVersion: "kbv4", kbHash: "hash4",
            proposalId: "PROP-A", memoId: "MEMO-A");
        var other = MemoFixtures.Proposal(proposalId: "PROP-B", memoId: "MEMO-A");
        var memo = new MemoFile(
            new PolicyMemo("MEMO-A", FixedAt, "Board", "Excerpt."), "Full.",
            [
                new MemoProposalRecord(citing, new ProposalValidation(true, [], []), null, "Terry Owner"),
                new MemoProposalRecord(other, new ProposalValidation(true, [], []), null, "Terry Owner"),
            ],
            [], [],
            [
                new RuleActivation(
                    "ACT-A", "PACK-T-02", "PACK-T", "v2", "v1", "MEMO-A", "Terry Owner",
                    FixedAt, RuleApplicability.ProspectiveOnly, citing.ProposalHash, FixedAt, "kbv9"),
            ]);

        var byItem = UsedByIndex.FindUsedBy("items", "BAT-9", [memo]);

        Assert.Equal("PROP-A", Assert.Single(byItem.Proposals).ProposalId);
        Assert.Equal("ACT-A", Assert.Single(byItem.Activations).ActivationId);
        Assert.Equal(["PACK-T-02"], byItem.Rules);

        var byPolicy = UsedByIndex.FindUsedBy("sourcingPolicies", "origin-cap", [memo]);

        Assert.Equal("kbv4", Assert.Single(byPolicy.Proposals).KbVersion);

        var missing = UsedByIndex.FindUsedBy("items", "missing", [memo]);

        Assert.Empty(missing.Proposals);
        Assert.Empty(missing.Activations);
        Assert.Empty(missing.Rules);
    }

    [Fact]
    public void ExceptionRowsListEveryCardNewestFirst()
    {
        var granted = new Decision(
            "CASE-1-d001", "CASE-1", DecisionKind.ExceptionApproval, "CORP exception",
            DecisionStatus.Approved, "Terry Owner", FixedAt, "Terry Owner",
            FixedAt.AddHours(2), "Vendor", "V-T1", null, null, "CORP-T1",
            "blocked by cap", [], "grant with reason");
        var pending = granted with
        {
            DecisionId = "CASE-2-d001",
            CaseId = "CASE-2",
            Status = DecisionStatus.Pending,
            DecidedBy = null,
            DecidedAt = null,
            Comment = null,
        };
        var other = granted with
        {
            DecisionId = "CASE-1-d002",
            Kind = DecisionKind.PrApproval,
        };

        var rows = ExceptionBoard.Rows([
            CaseWithDecisions("CASE-1", [granted, other]),
            CaseWithDecisions("CASE-2", [pending]),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Equal("CASE-1", rows[0].CaseId);
        Assert.Equal("V-T1", rows[0].Vendor);
        Assert.Equal("CORP-T1", rows[0].RuleId);
        Assert.Equal("Terry Owner", rows[0].DecidedBy);
        Assert.Equal("Approved", rows[0].Outcome);
        Assert.Equal("grant with reason", rows[0].Comment);
        Assert.Equal("Pending", rows[1].Outcome);
    }

    [Fact]
    public async Task RulesServiceScansCachesEndToEnd()
    {
        var dir = UiTestSupport.NewDir("gate-gov-rules");
        var cases = new CaseCacheStore(dir);
        var memos = new MemoFileStore(dir);
        await cases.WriteAsync(CaseWith("CASE-1",
        [
            Event("CASE-1-e001", "CASE-1", CaseEventKind.VendorBlocked, "PACK-T-01", "blocked"),
            Event("CASE-1-e002", "CASE-1", CaseEventKind.DockEvaluated, null, "dock evaluated"),
        ]));
        var proposal = MemoFixtures.Proposal(
            citations: [MemoFixtures.Cite("items", "BAT-9")],
            proposalId: "PROP-1", memoId: "MEMO-1");
        await memos.WriteAsync(new MemoFile(
            new PolicyMemo("MEMO-1", FixedAt, "Board", "Excerpt."), "Full.",
            [new MemoProposalRecord(proposal, new ProposalValidation(true, [], []), null, "Terry Owner")],
            [], [],
            [new RuleActivation(
                "ACT-1", "PACK-T-02", "PACK-T", "v2", "v1", "MEMO-1", "Terry Owner",
                FixedAt, RuleApplicability.ProspectiveOnly, proposal.ProposalHash, FixedAt, "kbv9")]));
        var engine = new GovernanceFakeEngine(MemoFixtures.TestKb());
        engine.LiveRules.Add(new LivePackRule(
            "PACK-T", "v1", "PACK-T-01", null, RuleTier.Tier2, MemoRuleGate.Vendor,
            "Fixture rule.", true, "Terry Owner", new DateOnly(2026, 3, 16),
            "Fixture pack source."));
        var service = new GovernanceViewService(
            engine, cases, memos, UiTestLogger<GovernanceViewService>.Instance);

        var vm = await service.ListRuleRowsAsync();

        Assert.Equal("kbv9", vm.CurrentKbVersion);
        Assert.Equal(2, vm.Rows.Count);
        var shipped = vm.Rows.Single(r => r.RuleId == "PACK-T-01");
        Assert.Equal(1, shipped.BlockCount);
        Assert.Equal("Fixture pack source.", shipped.Source);
        Assert.Equal("2026-03-16", shipped.EffectiveOrAdopted);
        Assert.Single(vm.Rows.Single(r => r.IsCompanyRule).GroundedEntries);
        Assert.Contains("all cases (1 scanned)", vm.ScanLabel);
        Assert.Contains("1 case", vm.DockNote);
        Assert.Empty(vm.Notes);
    }

    [Fact]
    public async Task ExceptionsServiceScansCasesEndToEnd()
    {
        var dir = UiTestSupport.NewDir("gate-gov-exceptions");
        var cases = new CaseCacheStore(dir);
        var memos = new MemoFileStore(dir);
        await cases.WriteAsync(CaseWithDecisions("CASE-1",
        [
            new Decision(
                "CASE-1-d001", "CASE-1", DecisionKind.ExceptionApproval, "exception",
                DecisionStatus.Pending, "Terry Owner", FixedAt, null, null,
                "Vendor", "V-T1", null, null, "CORP-T1", "blocked", [], null),
        ]));
        var service = new GovernanceViewService(
            new GovernanceFakeEngine(), cases, memos, UiTestLogger<GovernanceViewService>.Instance);

        var rows = await service.ListExceptionsAsync();

        var row = Assert.Single(rows);
        Assert.Equal("CASE-1", row.CaseId);
        Assert.Equal("V-T1", row.Vendor);
        Assert.Equal("CORP-T1", row.RuleId);
        Assert.Equal("Pending", row.Outcome);
    }

    [Fact]
    public async Task RulesServiceSurfacesSourcelessLiveRulesWithContext()
    {
        var dir = UiTestSupport.NewDir("gate-gov-rules-nosrc");
        var cases = new CaseCacheStore(dir);
        var memos = new MemoFileStore(dir);
        await cases.WriteAsync(CaseWith("CASE-1", []));
        var engine = new GovernanceFakeEngine(MemoFixtures.TestKb());
        engine.LiveRules.Add(new LivePackRule(
            "PACK-T", "v1", "PACK-T-01", null, RuleTier.Tier2, MemoRuleGate.Vendor,
            "Fixture rule.", true, "Terry Owner", new DateOnly(2026, 3, 16)));
        var service = new GovernanceViewService(
            engine, cases, memos, UiTestLogger<GovernanceViewService>.Instance);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ListRuleRowsAsync());

        Assert.Contains("PACK-T-01", refused.Message);
    }
}

// Proposal validation: structural rules, citation resolution against the
// pinned KB snapshot, and the proposal-hash tamper check. Test-double
// memos and KB entries only.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class ProposalValidationTests
{
    private static RuleProposal CapProposal(
        IReadOnlyList<ProposalStatement>? statements = null,
        IReadOnlyList<EntityResolution>? resolutions = null,
        IReadOnlyList<OpenQuestion>? questions = null,
        IReadOnlyList<KbCitation>? citations = null,
        ProposedRule? rule = null) =>
        MemoFixtures.Proposal(
            rule: rule ?? MemoFixtures.CapRule(),
            statements: statements,
            resolutions: resolutions,
            questions: questions,
            citations: citations);

    [Fact]
    public void WellFormedCapProposalValidates()
    {
        var citation = MemoFixtures.Cite("sourcingPolicies", "origin-cap");
        var proposal = CapProposal(
            statements:
            [
                new ProposalStatement(
                    "no more than 40% of cells from any single country",
                    "OriginShareCap 40% over trailing 12 months",
                    0.95m),
            ],
            resolutions:
            [
                new EntityResolution("single country", "origin-cap", citation),
            ],
            citations: [citation]);
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.NotEmpty(result.Conflicts);
    }

    [Fact]
    public void BothRuleAndNotMappableRejects()
    {
        var proposal = MemoFixtures.Proposal(
            rule: MemoFixtures.CapRule(),
            notMappable: new NotMappableDetail("VendorShareCap", "no share named", MissingCapability.Data));
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("both ProposedRule and NotMappable"));
    }

    [Fact]
    public void NeitherRuleNorNotMappableRejects()
    {
        var proposal = MemoFixtures.Proposal();
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("neither ProposedRule nor NotMappable"));
    }

    [Fact]
    public void UnknownRuleTypeRejectsNamingTheRegistry()
    {
        var proposal = CapProposal(rule: new ProposedRule(
            "CarbonScore", MemoRuleGate.Vendor, new Dictionary<string, string>()));
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("CarbonScore") && e.Contains("closed registry"));
    }

    [Fact]
    public void GateMismatchRejects()
    {
        var proposal = CapProposal(rule: new ProposedRule(
            MemoRuleRegistry.OriginShareCap, MemoRuleGate.Dock,
            new Dictionary<string, string> { ["cap"] = "0.40", ["windowMonths"] = "12", ["partScope"] = "critical" }));
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("Vendor") && e.Contains("Dock"));
    }

    [Fact]
    public void OutOfRangeParamsReject()
    {
        var proposal = CapProposal(rule: MemoFixtures.CapRule(cap: "2.5"));
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("cap"));
    }

    [Fact]
    public void UnresolvedStatementWithoutBlockingQuestionRejects()
    {
        var proposal = CapProposal(
            statements:
            [
                new ProposalStatement("prefer sustainable suppliers", "no measurable threshold found", 0.2m),
            ],
            resolutions: [],
            questions: []);
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("blocking question"));
    }

    [Fact]
    public void UnresolvedStatementWithBlockingQuestionPassesStructure()
    {
        var proposal = CapProposal(
            statements:
            [
                new ProposalStatement("prefer sustainable suppliers", "no measurable threshold found", 0.2m),
            ],
            resolutions: [],
            questions:
            [
                new OpenQuestion("q1", "What threshold decides?", ["block", "score"], Blocking: true),
            ]);
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void NonBlockingQuestionsDoNotSatisfyTheBlock()
    {
        var proposal = CapProposal(
            statements:
            [
                new ProposalStatement("prefer sustainable suppliers", "no measurable threshold found", 0.2m),
            ],
            resolutions: [],
            questions:
            [
                new OpenQuestion("q1", "Any context?", [], Blocking: false),
            ]);
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("blocking question"));
    }

    [Fact]
    public void DuplicateQuestionIdsReject()
    {
        var proposal = CapProposal(
            statements:
            [
                new ProposalStatement("cap the share", "cap the share at 40%", 0.9m),
            ],
            resolutions: [new EntityResolution("cap the share", "origin-cap", MemoFixtures.Cite("sourcingPolicies", "origin-cap"))],
            questions:
            [
                new OpenQuestion("q1", "First?", [], Blocking: false),
                new OpenQuestion("q1", "Second?", [], Blocking: false),
            ],
            citations: [MemoFixtures.Cite("sourcingPolicies", "origin-cap")]);
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("Duplicate question id"));
    }

    [Fact]
    public void ResolutionCitationMissingFromListRejects()
    {
        var proposal = CapProposal(
            statements:
            [
                new ProposalStatement("cap the share", "cap the share at 40%", 0.9m),
            ],
            resolutions: [new EntityResolution("cap the share", "origin-cap", MemoFixtures.Cite("sourcingPolicies", "origin-cap"))],
            citations: []);
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("kb:sourcingPolicies#origin-cap@kbv9"));
    }

    [Fact]
    public void MixedCitationPinsRejectQuotingTheCitation()
    {
        var bad = MemoFixtures.Cite("sourcingPolicies", "origin-cap", "kbv8");
        var proposal = CapProposal(
            statements:
            [
                new ProposalStatement("cap the share", "cap the share at 40%", 0.9m),
            ],
            resolutions: [new EntityResolution("cap the share", "origin-cap", bad)],
            citations: [bad]);
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("kb:sourcingPolicies#origin-cap@kbv8") && e.Contains("mixed pins"));
    }

    [Fact]
    public void TamperedProposalFailsTheHashCheck()
    {
        var citation = MemoFixtures.Cite("sourcingPolicies", "origin-cap");
        var proposal = CapProposal(
            statements:
            [
                new ProposalStatement("cap the share", "cap the share at 40%", 0.9m),
            ],
            resolutions: [new EntityResolution("cap the share", "origin-cap", citation)],
            citations: [citation]);
        var tampered = proposal with
        {
            Statements = [new ProposalStatement("cap the share", "cap the share at 10%", 0.9m)],
        };
        var result = ProposalValidator.ValidateStructure(tampered);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("hash mismatch"));
    }

    [Fact]
    public void HasherIsDeterministicAndOrderStableForParams()
    {
        var a = MemoFixtures.Proposal(rule: new ProposedRule(
            MemoRuleRegistry.OriginShareCap, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["cap"] = "0.40", ["windowMonths"] = "12", ["partScope"] = "critical" }));
        var b = MemoFixtures.Proposal(rule: new ProposedRule(
            MemoRuleRegistry.OriginShareCap, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["partScope"] = "critical", ["cap"] = "0.40", ["windowMonths"] = "12" }));
        Assert.Equal(ProposalHasher.Hash(a), ProposalHasher.Hash(b));
    }

    [Fact]
    public void NotMappableRequiresKnownNearestTypeAndWhyNot()
    {
        var bad = MemoFixtures.Proposal(
            notMappable: new NotMappableDetail("CarbonScore", "", MissingCapability.Data),
            statements: [new ProposalStatement("be careful out there", "no rule fits", 0.1m)],
            questions: [new OpenQuestion("q1", "What do you mean?", [], Blocking: true)]);
        var result = ProposalValidator.ValidateStructure(bad);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("nearestType"));
        Assert.Contains(result.Errors, e => e.Contains("whyNot"));
    }

    [Fact]
    public void ValidNotMappablePassesWithNoConflicts()
    {
        var proposal = MemoFixtures.Proposal(
            notMappable: new NotMappableDetail("VendorShareCap", "no share named", MissingCapability.Data),
            statements: [new ProposalStatement("prefer sustainable suppliers", "no measurable threshold", 0.2m)],
            questions: [new OpenQuestion("q1", "Block, score, or tiebreak?", ["block", "score", "tiebreak"], Blocking: true)]);
        var result = ProposalValidator.ValidateStructure(proposal);
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void EnsureValidThrowsQuotingEveryReason()
    {
        var result = ProposalValidator.ValidateStructure(MemoFixtures.Proposal());
        Assert.False(result.Ok);
        var ex = Assert.Throws<InvalidOperationException>(() => result.EnsureValid());
        Assert.Contains("neither ProposedRule nor NotMappable", ex.Message);
    }

    [Fact]
    public void KbPinMismatchRequiresRegrounding()
    {
        var citation = MemoFixtures.Cite("sourcingPolicies", "origin-cap");
        var proposal = CapProposal(
            statements: [new ProposalStatement("cap the share", "cap the share at 40%", 0.9m)],
            resolutions: [new EntityResolution("cap the share", "origin-cap", citation)],
            citations: [citation]);
        var moved = MemoFixtures.TestKb(version: "kbv10", hash: "hash10");
        var result = ProposalValidator.ValidateAgainstKb(proposal, moved);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("Re-ground") && e.Contains("kbv9") && e.Contains("kbv10"));
    }

    [Fact]
    public void UnresolvableCitationRejectsQuotingIt()
    {
        var bad = MemoFixtures.Cite("regionRules", "atlantis");
        var proposal = CapProposal(
            statements: [new ProposalStatement("cap the share", "cap the share at 40%", 0.9m)],
            resolutions: [new EntityResolution("cap the share", "atlantis", bad)],
            citations: [bad]);
        var result = ProposalValidator.ValidateAgainstKb(proposal, MemoFixtures.TestKb());
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("kb:regionRules#atlantis@kbv9"));
    }

    [Fact]
    public void UnknownSectionRejectsQuotingIt()
    {
        var bad = MemoFixtures.Cite("weather", "today");
        var proposal = CapProposal(
            statements: [new ProposalStatement("cap the share", "cap the share at 40%", 0.9m)],
            resolutions: [new EntityResolution("cap the share", "today", bad)],
            citations: [bad]);
        var result = ProposalValidator.ValidateAgainstKb(proposal, MemoFixtures.TestKb());
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("kb:weather#today@kbv9") && e.Contains("section"));
    }

    [Fact]
    public void HrDocumentCitationIsExcludedFromRuleMapping()
    {
        var hr = MemoFixtures.Cite("documents", "hr-001");
        var kb = MemoFixtures.TestKb(documents: [new KbDocument("hr-001", "hr-policy", false)]);
        var proposal = CapProposal(
            statements: [new ProposalStatement("cap the share", "cap the share at 40%", 0.9m)],
            resolutions: [new EntityResolution("cap the share", "hr-001", hr)],
            citations: [hr]);
        var result = ProposalValidator.ValidateAgainstKb(proposal, kb);
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("kb:documents#hr-001@kbv9") && e.Contains("p2pRelevant=false"));
    }

    [Fact]
    public void CriticalFlagPseudoEntryResolves()
    {
        var pseudo = MemoFixtures.Cite("items", "critical-flag");
        var proposal = MemoFixtures.Proposal(
            rule: new ProposedRule(
                MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
                new Dictionary<string, string> { ["docKind"] = "COO-Declaration", ["partScope"] = "critical" }),
            statements: [new ProposalStatement("critical components need a declaration", "DocumentPresence for critical parts", 0.9m)],
            resolutions: [new EntityResolution("critical components", "critical-flag", pseudo)],
            citations: [pseudo]);
        var result = ProposalValidator.ValidateAgainstKb(proposal, MemoFixtures.TestKb());
        Assert.True(result.Ok, string.Join("; ", result.Errors));
    }

    [Fact]
    public void MissingFiscalCalendarRejectsItsCitation()
    {
        var fy = MemoFixtures.Cite("fiscalCalendar", "current");
        var proposal = CapProposal(
            statements: [new ProposalStatement("cap the share", "cap the share at 40%", 0.9m)],
            resolutions: [new EntityResolution("cap the share", "current", fy)],
            citations: [fy]);
        var result = ProposalValidator.ValidateAgainstKb(proposal, MemoFixtures.TestKb());
        Assert.False(result.Ok);
        Assert.Contains(result.Errors, e => e.Contains("kb:fiscalCalendar#current@kbv9") && e.Contains("no fiscal calendar"));
    }

    [Fact]
    public void FullKbValidationPassesAndComputesConflicts()
    {
        var citation = MemoFixtures.Cite("sourcingPolicies", "origin-cap");
        var proposal = CapProposal(
            statements: [new ProposalStatement("cap the share", "cap the share at 40%", 0.9m)],
            resolutions: [new EntityResolution("cap the share", "origin-cap", citation)],
            citations: [citation]);
        var result = ProposalValidator.ValidateAgainstKb(proposal, MemoFixtures.TestKb());
        Assert.True(result.Ok, string.Join("; ", result.Errors));
        Assert.Contains(result.Conflicts, c => c.RuleId == "CORP-02" && c.Kind == RuleConflictKind.Tightens);
    }
}

// Conflict computation against live packs: tighten, loosen,
// contradiction, subsumption. Test-double params only; live sides come from
// the real catalog (CORP pack config) or explicit live sets.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class MemoConflictTests
{
    private static ProposedRule OriginCap(string cap, string scope = "critical", string window = "12") =>
        new(MemoRuleRegistry.OriginShareCap, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["cap"] = cap, ["windowMonths"] = window, ["partScope"] = scope });

    [Fact]
    public void LowerCapTightensQuotingBothCaps()
    {
        var conflicts = RuleConflictDetector.Compute(OriginCap("0.40"));
        var tighten = Assert.Single(conflicts, c => c.RuleId == "CORP-02");
        Assert.Equal(RuleConflictKind.Tightens, tighten.Kind);
        Assert.Contains("60", tighten.Detail);
        Assert.Contains("40", tighten.Detail);
    }

    [Fact]
    public void HigherCapLoosens()
    {
        var conflicts = RuleConflictDetector.Compute(OriginCap("0.90"));
        var loosen = Assert.Single(conflicts, c => c.RuleId == "CORP-02");
        Assert.Equal(RuleConflictKind.Loosens, loosen.Kind);
        Assert.Contains("90", loosen.Detail);
        Assert.Contains("60", loosen.Detail);
    }

    [Fact]
    public void IdenticalCapSubsumes()
    {
        var conflicts = RuleConflictDetector.Compute(OriginCap("0.60"));
        var subsumed = Assert.Single(conflicts);
        Assert.Equal(RuleConflictKind.Subsumes, subsumed.Kind);
        Assert.Equal("CORP-02", subsumed.RuleId);
    }

    [Fact]
    public void WindowOnlyChangeYieldsNoConflict()
    {
        // Equal cap, shorter window: neither strictly tighter nor looser,
        // so no kind fits. The what-if report shows the impact instead.
        var conflicts = RuleConflictDetector.Compute(OriginCap("0.60", window: "6"));
        Assert.Empty(conflicts);
    }

    [Fact]
    public void ScopeWideningTightens()
    {
        var conflicts = RuleConflictDetector.Compute(OriginCap("0.60", scope: "all"));
        var tighten = Assert.Single(conflicts);
        Assert.Equal(RuleConflictKind.Tightens, tighten.Kind);
        Assert.Contains("all", tighten.Detail);
    }

    [Fact]
    public void MixedCapAndScopeYieldOneRecordPerDimension()
    {
        var conflicts = RuleConflictDetector.Compute(OriginCap("0.90", scope: "all"));
        Assert.Contains(conflicts, c => c.Kind == RuleConflictKind.Loosens && c.Detail.Contains("90"));
        Assert.Contains(conflicts, c => c.Kind == RuleConflictKind.Tightens && c.Detail.Contains("all"));
    }

    [Fact]
    public void VendorCapNewlyEnforcesOverDocumentedCorp01()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.VendorShareCap, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["cap"] = "0.40", ["windowMonths"] = "12", ["partScope"] = "critical" });
        var conflicts = RuleConflictDetector.Compute(rule);
        var tighten = Assert.Single(conflicts);
        Assert.Equal(RuleConflictKind.Tightens, tighten.Kind);
        Assert.Equal("CORP-01", tighten.RuleId);
        Assert.Contains("documented-only", tighten.Detail);
    }

    [Fact]
    public void NewDocumentKindHasNoConflict()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["docKind"] = "COO-Declaration", ["partScope"] = "critical" });
        Assert.Empty(RuleConflictDetector.Compute(rule));
    }

    [Fact]
    public void SameDocumentSubsumes()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["docKind"] = "UN38.3", ["partScope"] = "critical" });
        var subsumed = Assert.Single(RuleConflictDetector.Compute(rule));
        Assert.Equal(RuleConflictKind.Subsumes, subsumed.Kind);
        Assert.Equal("BAT-LI-01", subsumed.RuleId);
    }

    [Fact]
    public void SameDocumentWiderScopeTightens()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["docKind"] = "RoHS", ["partScope"] = "all" });
        var tighten = Assert.Single(RuleConflictDetector.Compute(rule));
        Assert.Equal(RuleConflictKind.Tightens, tighten.Kind);
        Assert.Equal("ELEC-01", tighten.RuleId);
    }

    [Fact]
    public void DocKindMatchIsExactLikeTheEngine()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.DocumentPresence, MemoRuleGate.Vendor,
            new Dictionary<string, string> { ["docKind"] = "un38.3", ["partScope"] = "critical" });
        Assert.Empty(RuleConflictDetector.Compute(rule));
    }

    [Fact]
    public void ScreeningProposalIsSubsumedByLiveScr01()
    {
        var rule = new ProposedRule(MemoRuleRegistry.ScreeningMatch, MemoRuleGate.Vendor,
            new Dictionary<string, string>());
        var subsumed = Assert.Single(RuleConflictDetector.Compute(rule));
        Assert.Equal(RuleConflictKind.Subsumes, subsumed.Kind);
        Assert.Equal("SCR-01", subsumed.RuleId);
    }

    [Fact]
    public void LithiumMarkProposalIsSubsumed()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.DockMarkPresence, MemoRuleGate.Dock,
            new Dictionary<string, string> { ["markKind"] = "Lithium-Battery Mark" });
        var subsumed = Assert.Single(RuleConflictDetector.Compute(rule));
        Assert.Equal(RuleConflictKind.Subsumes, subsumed.Kind);
        Assert.Equal("BAT-LI-03", subsumed.RuleId);
    }

    [Fact]
    public void NewMarkKindHasNoConflict()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.DockMarkPresence, MemoRuleGate.Dock,
            new Dictionary<string, string> { ["markKind"] = "UN number placard" });
        Assert.Empty(RuleConflictDetector.Compute(rule));
    }

    [Fact]
    public void Class9ProposalIsSubsumed()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.DockHandlingPresence, MemoRuleGate.Dock,
            new Dictionary<string, string> { ["markKind"] = "Class 9 diamond" });
        var subsumed = Assert.Single(RuleConflictDetector.Compute(rule));
        Assert.Equal(RuleConflictKind.Subsumes, subsumed.Kind);
        Assert.Equal("BAT-LI-04", subsumed.RuleId);
    }

    [Fact]
    public void DamageProposalIsSubsumedByHumanRoutedBatLi05()
    {
        var rule = new ProposedRule(MemoRuleRegistry.DamageReview, MemoRuleGate.Dock,
            new Dictionary<string, string>());
        var subsumed = Assert.Single(RuleConflictDetector.Compute(rule));
        Assert.Equal(RuleConflictKind.Subsumes, subsumed.Kind);
        Assert.Equal("BAT-LI-05", subsumed.RuleId);
    }

    [Fact]
    public void IdenticalPrePaySetSubsumes()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.PrePayBlockOnOpen, MemoRuleGate.PrePay,
            new Dictionary<string, string> { ["holds"] = "true", ["quarantine"] = "true", ["reviews"] = "true" });
        var subsumed = Assert.Single(RuleConflictDetector.Compute(rule));
        Assert.Equal(RuleConflictKind.Subsumes, subsumed.Kind);
        Assert.Equal("ELEC-04", subsumed.RuleId);
    }

    [Fact]
    public void DroppedHoldStateLoosensAndContradictsTier1Producers()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.PrePayBlockOnOpen, MemoRuleGate.PrePay,
            new Dictionary<string, string> { ["holds"] = "false", ["quarantine"] = "true", ["reviews"] = "true" });
        var conflicts = RuleConflictDetector.Compute(rule);
        Assert.Contains(conflicts, c => c.RuleId == "ELEC-04" && c.Kind == RuleConflictKind.Loosens);
        Assert.Contains(conflicts, c => c.RuleId == "BAT-LI-03" && c.Kind == RuleConflictKind.Contradicts);
        // BAT-LI-04 also produces holds, but it is Tier 2 (passes silently):
        // only enforced Tier-1 producers contradict.
        Assert.DoesNotContain(conflicts, c => c.RuleId == "BAT-LI-04");
    }

    [Fact]
    public void DroppedReviewStateContradictsBatLi05()
    {
        var rule = new ProposedRule(
            MemoRuleRegistry.PrePayBlockOnOpen, MemoRuleGate.PrePay,
            new Dictionary<string, string> { ["holds"] = "true", ["quarantine"] = "true", ["reviews"] = "false" });
        var conflicts = RuleConflictDetector.Compute(rule);
        Assert.Contains(conflicts, c => c.RuleId == "ELEC-04" && c.Kind == RuleConflictKind.Loosens);
        Assert.Contains(conflicts, c => c.RuleId == "BAT-LI-05" && c.Kind == RuleConflictKind.Contradicts);
    }

    [Fact]
    public void MissingLiveRuleYieldsNoConflict()
    {
        var live = new List<RuleDefinition>
        {
            new("ELEC-01", "ELEC", "v0.2", RuleTier.Tier2, ComplianceGate.Vendor,
                "RoHS", "Fixture", true, "RoHS", "RoHS declaration"),
        };
        var corp = new CorpPackConfig(0.60m, 0.50m, "Fixture");
        Assert.Empty(RuleConflictDetector.Compute(OriginCap("0.40"), live, corp));
    }

    [Fact]
    public void UnparseableCapThrowsInsteadOfGuessing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RuleConflictDetector.Compute(OriginCap("not-a-cap")));
        Assert.Contains("not-a-cap", ex.Message);
    }

    [Fact]
    public async Task DetectorPrePaySetAgreesWithTheEngine()
    {
        // The detector claims live ELEC-04 blocks on holds, quarantine, and
        // reviews. Each single-state PrePayState must block in the engine.
        var engine = new ComplianceEngine();
        var hold = new HoldDto("HLD-1", "DOCK", "note", "BAT-LI-03", "case-1", "agent",
            MemoFixtures.FixedAt, HoldStatus.Set, "Shipment", "RCT-1", null, null, null);
        var review = new OpenReview("REV-1", "BAT-LI-05", "case-1", null, null, MemoFixtures.FixedAt, "look");

        foreach (var state in new[]
        {
            new PrePayState([hold], [], []),
            new PrePayState([], ["LOT-1"], []),
            new PrePayState([], [], [review]),
        })
        {
            var verdicts = await engine.EvaluatePaymentAsync(state);
            Assert.Equal(VerdictKind.Block, Assert.Single(verdicts).Kind);
        }

        Assert.True(RuleConflictDetector.LivePrePayStates["holds"]);
        Assert.True(RuleConflictDetector.LivePrePayStates["quarantine"]);
        Assert.True(RuleConflictDetector.LivePrePayStates["reviews"]);
    }
}

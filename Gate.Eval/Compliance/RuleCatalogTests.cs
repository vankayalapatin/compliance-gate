// Catalog integrity: tier assignments, enforcement flags, and citations
// match PROJECT.md section 6 (catalog v1). Deterministic by construction.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class RuleCatalogTests
{
    [Fact]
    public void Tier1SetIsExactlyTheFiveBlockers()
    {
        var tier1 = RuleCatalog.Rules
            .Where(r => r.Tier == RuleTier.Tier1)
            .Select(r => r.RuleId)
            .OrderBy(id => id)
            .ToList();
        Assert.Equal(["BAT-LI-03", "BAT-LI-05", "BAT-LI-06", "CORP-02", "ELEC-04"], tier1);
        Assert.All(RuleCatalog.Rules.Where(r => r.Tier == RuleTier.Tier1), r => Assert.True(r.Enforced));
    }

    [Fact]
    public void Tier3RulesAreDocumentedOnly()
    {
        var tier3 = RuleCatalog.Rules.Where(r => r.Tier == RuleTier.Tier3).Select(r => r.RuleId).OrderBy(id => id).ToList();
        Assert.Equal(["CORP-01", "ELEC-03", "SCR-02"], tier3);
        Assert.All(RuleCatalog.Rules.Where(r => r.Tier == RuleTier.Tier3), r => Assert.False(r.Enforced));
    }

    [Fact]
    public void BatLiV2CarriesAllSixRulesEnforced()
    {
        var batLi = RuleCatalog.Rules.Where(r => r.Pack == "BAT-LI").OrderBy(r => r.RuleId).ToList();
        Assert.Equal(
            ["BAT-LI-01", "BAT-LI-02", "BAT-LI-03", "BAT-LI-04", "BAT-LI-05", "BAT-LI-06"],
            batLi.Select(r => r.RuleId).ToList());
        Assert.All(batLi, r => Assert.True(r.Enforced));
        Assert.All(batLi, r => Assert.Equal("v2", r.PackVersion));
    }

    [Fact]
    public void EveryEnforcedRuleHasSourceTitleAndPackVersion()
    {
        Assert.All(RuleCatalog.Rules.Where(r => r.Enforced), r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Source));
            Assert.False(string.IsNullOrWhiteSpace(r.Title));
            Assert.Equal(r.Pack is "BAT-LI" or "CORP" ? "v2" : "v1", r.PackVersion);
        });
    }

    [Fact]
    public void CatalogOrderIsDeterministicPackOrder()
    {
        // Pack order follows PROJECT.md section 6: BAT-LI, ELEC, SCR, CORP.
        var ids = RuleCatalog.Rules.Select(r => r.RuleId).ToList();
        Assert.Equal(
        [
            "BAT-LI-01", "BAT-LI-02", "BAT-LI-03", "BAT-LI-04", "BAT-LI-05", "BAT-LI-06",
            "ELEC-01", "ELEC-02", "ELEC-03", "ELEC-04",
            "SCR-01", "SCR-02",
            "CORP-01", "CORP-02"
        ], ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void FindUnknownRuleThrowsWithContext()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RuleCatalog.Find("NOPE-00"));
        Assert.Contains("NOPE-00", ex.Message);
    }

    [Fact]
    public void EachGateHasAtLeastOneEnforcedRule()
    {
        foreach (var gate in new[] { ComplianceGate.Vendor, ComplianceGate.Dock, ComplianceGate.PrePay })
        {
            Assert.Contains(RuleCatalog.Rules, r => r.Gate == gate && r.Enforced);
        }
    }
}

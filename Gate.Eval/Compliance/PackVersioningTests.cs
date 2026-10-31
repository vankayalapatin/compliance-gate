// Pack version loading: prior versions stay retained while the highest
// version enforces. Test-double pack fragments only.
using Gate.Compliance;

namespace Gate.Eval;

public sealed class PackVersioningTests
{
    [Fact]
    public void HighestVersionWinsWhilePriorStaysRetained()
    {
        var inputs = MemoFixtures.FourPacks();
        inputs.Add(("Packs.corp.v0.3.json", MemoFixtures.CorpPackJson("v0.3", "0.40")));
        var set = RulePackLoader.LoadPacks(inputs);
        Assert.Equal(0.40m, set.Corp.OriginCap);
        Assert.Equal("v0.3", set.Rules.Single(r => r.RuleId == "CORP-02").PackVersion);
        Assert.Equal("v0.2", set.Rules.Single(r => r.RuleId == "ELEC-01").PackVersion);
    }

    [Fact]
    public void NumericCompareBeatsLexicographic()
    {
        Assert.True(PackVersions.Compare("v0.10", "v0.9") > 0);
        Assert.Equal("v0.3", PackVersions.Next("v0.2"));
        var (major, minor) = PackVersions.Parse("v1.12");
        Assert.Equal((1, 12), (major, minor));
    }

    [Fact]
    public void MalformedVersionRefusesLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => PackVersions.Parse("0.2"));
        Assert.Throws<InvalidOperationException>(() => PackVersions.Parse("v2"));
        var inputs = MemoFixtures.FourPacks(corpVersion: "draft");
        var ex = Assert.Throws<InvalidOperationException>(() => RulePackLoader.LoadPacks(inputs));
        Assert.Contains("draft", ex.Message);
    }

    [Fact]
    public void DuplicateVersionRefuses()
    {
        var inputs = MemoFixtures.FourPacks();
        inputs.Add(("Packs.corp.v0.2.json", MemoFixtures.CorpPackJson("v0.2")));
        var ex = Assert.Throws<InvalidOperationException>(() => RulePackLoader.LoadPacks(inputs));
        Assert.Contains("twice", ex.Message);
    }

    [Fact]
    public void FilenameContentMismatchRefuses()
    {
        var inputs = MemoFixtures.FourPacks();
        inputs.Add(("Packs.corp.v0.3.json", MemoFixtures.CorpPackJson("v0.4")));
        var ex = Assert.Throws<InvalidOperationException>(() => RulePackLoader.LoadPacks(inputs));
        Assert.Contains("v0.3", ex.Message);
        Assert.Contains("v0.4", ex.Message);
    }

    [Fact]
    public void MissingPackRefuses()
    {
        var inputs = MemoFixtures.FourPacks().Where(p => !p.Name.Contains("scr")).ToList();
        var ex = Assert.Throws<InvalidOperationException>(() => RulePackLoader.LoadPacks(inputs));
        Assert.Contains("scr", ex.Message);
    }

    [Fact]
    public void PublishedRuleFieldsRoundTripThroughTheLoader()
    {
        var json = """
        {"pack":"CORP","version":"v0.3","rules":[
        {"id":"CORP-02","tier":"Tier1","gate":"Vendor","title":"Country-of-origin cap.","source":"Board memo MEMO-9","enforced":true,"ruleType":"OriginShareCap","params":{"cap":"0.40","windowMonths":"12","partScope":"critical"}}
        ],"config":{"originCap":0.40,"vendorCap":0.50,"policySource":"Board memo MEMO-9"},
        "provenance":{"memoId":"MEMO-9","approver":"Terry Owner","effectiveAt":"2026-12-01T00:00:00+00:00"}}
        """;
        var inputs = MemoFixtures.FourPacks();
        inputs.Add(("Packs.corp.v0.3.json", json));
        var set = RulePackLoader.LoadPacks(inputs);
        var rule = set.Rules.Single(r => r.RuleId == "CORP-02");
        Assert.Equal("OriginShareCap", rule.RuleType);
        Assert.Equal("0.40", rule.Params!["cap"]);
        Assert.Equal(0.40m, set.Corp.OriginCap);
    }

    [Fact]
    public void EmbeddedCatalogStillShipsV02()
    {
        Assert.Equal("v0.2", RuleCatalog.PackVersion);
        Assert.All(RuleCatalog.Rules, r => Assert.Equal("v0.2", r.PackVersion));
        Assert.Equal(0.60m, RuleCatalog.Corp.OriginCap);
        Assert.Equal(0.50m, RuleCatalog.Corp.VendorCap);
    }
}

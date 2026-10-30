// Rule pack configuration: thresholds and caps load from per-pack JSON,
// and the engine honors different values. Test-double data only.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class RuleConfigTests
{
    private static VendorDto Vendor(string id, string country) =>
        new(id, $"{id} Ltd", country, false, null, "NET0");

    private static ItemDto Cell() =>
        new("BAT-9", "Test cell", true, 10, "EA");

    private static IReadOnlyList<VendorDocumentDto> Docs(string vendorId) =>
    [
        new VendorDocumentDto(vendorId, "UN38.3", "un383", true),
        new VendorDocumentDto(vendorId, "SDS", "sds", true),
        new VendorDocumentDto(vendorId, "RoHS", "rohs", true),
        new VendorDocumentDto(vendorId, "REACH", "reach", true)
    ];

    private static IReadOnlyList<VolumeHistory> DemoHistory() =>
    [
        new VolumeHistory("V-A CN active", "CN", 3400),
        new VolumeHistory("Legacy CN inactive", "CN", 2500),
        new VolumeHistory("V-B MX active", "MX", 3800),
        new VolumeHistory("Other non-CN", "OTHER", 300)
    ];

    private static async Task<Verdict> CorpVerdictAsync(
        ComplianceEngine engine, string vendorId, string country, decimal quantity)
    {
        var verdicts = await engine.EvaluateVendorAsync(
            Vendor(vendorId, country), Cell(), Docs(vendorId),
            new ProposedAward("BAT-9", quantity, country), DemoHistory());
        return verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
    }

    [Fact]
    public void DefaultJsonCarriesDemoThresholds()
    {
        Assert.Equal(0.60m, RuleCatalog.Corp.OriginCap);
        Assert.Equal(0.50m, RuleCatalog.Corp.VendorCap);
        Assert.False(string.IsNullOrWhiteSpace(RuleCatalog.Corp.PolicySource));
    }

    [Fact]
    public void DocumentRulesCarryKindsFromJson()
    {
        Assert.Equal("UN38.3", RuleCatalog.Find("BAT-LI-01").DocumentKind);
        Assert.Equal("SDS", RuleCatalog.Find("BAT-LI-02").DocumentKind);
        Assert.Equal("RoHS", RuleCatalog.Find("ELEC-01").DocumentKind);
        Assert.Equal("REACH", RuleCatalog.Find("ELEC-02").DocumentKind);
        Assert.Null(RuleCatalog.Find("BAT-LI-03").DocumentKind);
    }

    [Fact]
    public async Task LowerCapBlocksAwardPassingAtDefault()
    {
        // (3800 + 1200) / (10000 + 1200) = 44.6%: passes at 0.60, blocks at 0.40.
        var defaultVerdict = await CorpVerdictAsync(new ComplianceEngine(), "V-B", "MX", 1200);
        Assert.Equal(VerdictKind.Pass, defaultVerdict.Kind);

        var strict = new ComplianceEngine(new CorpPackConfig(0.40m, 0.50m, "trial policy"));
        var strictVerdict = await CorpVerdictAsync(strict, "V-B", "MX", 1200);
        Assert.Equal(VerdictKind.Block, strictVerdict.Kind);
        Assert.Contains("40", strictVerdict.Reason);
    }

    [Fact]
    public async Task HigherCapPassesAwardBlockedAtDefault()
    {
        // (5900 + 1200) / (10000 + 1200) = 63.4%: blocks at 0.60, passes at 0.90.
        var defaultVerdict = await CorpVerdictAsync(new ComplianceEngine(), "V-A", "CN", 1200);
        Assert.Equal(VerdictKind.Block, defaultVerdict.Kind);

        var lenient = new ComplianceEngine(new CorpPackConfig(0.90m, 0.50m, "trial policy"));
        var lenientVerdict = await CorpVerdictAsync(lenient, "V-A", "CN", 1200);
        Assert.Equal(VerdictKind.Pass, lenientVerdict.Kind);
    }

    [Fact]
    public async Task CustomPolicySourceIsCited()
    {
        var custom = new ComplianceEngine(new CorpPackConfig(0.60m, 0.50m, "trial memo 7"));
        var verdict = await CorpVerdictAsync(custom, "V-A", "CN", 1200);
        Assert.Equal("trial memo 7", verdict.Source);
    }
}

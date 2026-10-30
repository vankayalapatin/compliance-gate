// CORP-02 origin-cap calculation: the engine computes
// (country history + award) / (total history + award) itself from raw
// trailing-12-month rows. Policy (60% cap) and history numbers are inputs,
// never tuned here.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class CorpOriginCapTests
{
    private readonly ComplianceEngine _engine = new();

    private static VendorDto Vendor(string id, string country) =>
        new(id, $"{id} Ltd", country, false, null, "NET0");

    private static IReadOnlyList<VendorDocumentDto> Docs(string vendorId) =>
    [
        new VendorDocumentDto(vendorId, "UN38.3", "un383", true),
        new VendorDocumentDto(vendorId, "SDS", "sds", true)
    ];

    // Demo history in cells: CN 5900, MX 3800, OTHER 300, total 10000.
    private static IReadOnlyList<VolumeHistory> DemoHistory() =>
    [
        new VolumeHistory("V-A CN active", "CN", 3400),
        new VolumeHistory("Legacy CN inactive", "CN", 2500),
        new VolumeHistory("V-B MX active", "MX", 3800),
        new VolumeHistory("Other non-CN", "OTHER", 300)
    ];

    [Fact]
    public async Task AwardAboveCapBlocks()
    {
        // (5900 + 1200) / (10000 + 1200) = 63.4%: the cheaper CN award breaches.
        var verdicts = await _engine.EvaluateVendorAsync(
            Vendor("V-A", "CN"), Docs("V-A"), new ProposedAward("BAT-1", 1200, "CN"), DemoHistory());
        var corp = Assert.Single(verdicts, v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Block, corp.Kind);
        Assert.Contains("exceeding", corp.Reason);
    }

    [Fact]
    public async Task AwardBelowCapPasses()
    {
        // (3800 + 1200) / (10000 + 1200) = 44.6%: the compliant MX award passes.
        var verdicts = await _engine.EvaluateVendorAsync(
            Vendor("V-B", "MX"), Docs("V-B"), new ProposedAward("BAT-1", 1200, "MX"), DemoHistory());
        var corp = Assert.Single(verdicts, v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Pass, corp.Kind);
        Assert.Contains("within", corp.Reason);
    }

    [Fact]
    public async Task ExactlySixtyPercentPasses()
    {
        // (560 + 100) / (1000 + 100) = 60.0% exactly: the cap is exclusive.
        var history = new[]
        {
            new VolumeHistory("Home active", "CN", 560),
            new VolumeHistory("Other", "OTHER", 440)
        };
        var verdicts = await _engine.EvaluateVendorAsync(
            Vendor("V-A", "CN"), Docs("V-A"), new ProposedAward("BAT-1", 100, "CN"), history);
        var corp = Assert.Single(verdicts, v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Pass, corp.Kind);
    }

    [Fact]
    public async Task OneCellOverCapBlocks()
    {
        // (560 + 101) / (1000 + 101) = 60.04%: one cell over the cap blocks.
        var history = new[]
        {
            new VolumeHistory("Home active", "CN", 560),
            new VolumeHistory("Other", "OTHER", 440)
        };
        var verdicts = await _engine.EvaluateVendorAsync(
            Vendor("V-A", "CN"), Docs("V-A"), new ProposedAward("BAT-1", 101, "CN"), history);
        var corp = Assert.Single(verdicts, v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Block, corp.Kind);
    }

    [Fact]
    public async Task EmptyHistoryPassesAsNotApplicable()
    {
        // No trailing history for the part: CORP-02 has no denominator, so it
        // passes as not applicable instead of blocking history-free parts.
        var verdicts = await _engine.EvaluateVendorAsync(
            Vendor("V-C", "TW"), Docs("V-C"), new ProposedAward("SCR-9", 100, "TW"), []);
        var corp = Assert.Single(verdicts, v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Pass, corp.Kind);
        Assert.Contains("not applicable", corp.Reason);
    }
}

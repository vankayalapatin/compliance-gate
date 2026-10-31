// Vendor-gate document rules plus blocked-vendor refusal: every rule and
// every refusal carries its failure path. Test-double data only.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class VendorDocumentTests
{
    private readonly ComplianceEngine _engine = new();

    private static VendorDto Vendor() =>
        new("V-T", "Test Cells Ltd", "MX", false, null, "NET0");

    private static IReadOnlyList<VolumeHistory> History() =>
        [new VolumeHistory("Home", "MX", 900), new VolumeHistory("Other", "OTHER", 100)];

    private static IReadOnlyList<VendorDocumentDto> DocsWithout(params string[] missing) =>
        new[] { "UN38.3", "SDS", "RoHS", "REACH" }
            .Where(k => !missing.Contains(k))
            .Select(k => new VendorDocumentDto("V-T", k, "ref", true))
            .ToList();

    private static ItemDto CriticalCell() =>
        new("BAT-9", "Test cell", true, 10, "EA");

    private async Task<Verdict> RuleVerdictAsync(string ruleId, IReadOnlyList<VendorDocumentDto> docs)
    {
        var verdicts = await _engine.EvaluateVendorAsync(Vendor(), CriticalCell(), docs, new ProposedAward("BAT-9", 10, "MX"), History());
        return verdicts.Single(v => v.RuleId == ruleId);
    }

    [Fact]
    public async Task FullDocumentSetPassesAllDocRules()
    {
        foreach (var ruleId in new[] { "BAT-LI-01", "BAT-LI-02", "ELEC-01", "ELEC-02" })
        {
            var verdict = await RuleVerdictAsync(ruleId, DocsWithout());
            Assert.Equal(VerdictKind.Pass, verdict.Kind);
            Assert.False(string.IsNullOrWhiteSpace(verdict.Source));
        }
    }

    [Fact]
    public async Task MissingSdsBlocksBatLi02()
    {
        var verdict = await RuleVerdictAsync("BAT-LI-02", DocsWithout("SDS"));
        Assert.Equal(VerdictKind.Block, verdict.Kind);
        Assert.Contains("BAT-LI-02", verdict.RuleId);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Source));
    }

    [Fact]
    public async Task MissingRohsBlocksElec01()
    {
        var verdict = await RuleVerdictAsync("ELEC-01", DocsWithout("RoHS"));
        Assert.Equal(VerdictKind.Block, verdict.Kind);
        Assert.Contains("RoHS", verdict.Reason);
    }

    [Fact]
    public async Task MissingReachBlocksElec02()
    {
        var verdict = await RuleVerdictAsync("ELEC-02", DocsWithout("REACH"));
        Assert.Equal(VerdictKind.Block, verdict.Kind);
        Assert.Contains("REACH", verdict.Reason);
    }

    [Fact]
    public async Task PresentButFlaggedAbsentBlocks()
    {
        var docs = new[]
        {
            new VendorDocumentDto("V-T", "UN38.3", "un383", false),
            new VendorDocumentDto("V-T", "SDS", "sds", true),
            new VendorDocumentDto("V-T", "RoHS", "rohs", true),
            new VendorDocumentDto("V-T", "REACH", "reach", true)
        };
        var verdict = await RuleVerdictAsync("BAT-LI-01", docs);
        Assert.Equal(VerdictKind.Block, verdict.Kind);
    }

    [Fact]
    public async Task BlockedVendorIsRefused()
    {
        var vendor = new VendorDto("V-X", "Blocked Ltd", "MX", true, "unpaid invoices", "NET0");
        var verdicts = await _engine.EvaluateVendorAsync(
            vendor, CriticalCell(), DocsWithout(), new ProposedAward("BAT-9", 10, "MX"), History());
        var refusal = Assert.Single(verdicts, v => v.RuleId == ComplianceVerdictIds.VendorBlock);
        Assert.Equal(VerdictKind.Block, refusal.Kind);
        Assert.Contains("unpaid invoices", refusal.Reason);
    }

    [Fact]
    public async Task NonCriticalPartSkipsDocPacksWithoutDocuments()
    {
        var item = new ItemDto("SCR-9", "Test screw", false, 1, "EA");
        var verdicts = await _engine.EvaluateVendorAsync(
            Vendor(), item, [], new ProposedAward("SCR-9", 100, "MX"), History());
        foreach (var ruleId in new[] { "BAT-LI-01", "BAT-LI-02", "ELEC-01", "ELEC-02" })
        {
            var verdict = verdicts.Single(v => v.RuleId == ruleId);
            Assert.Equal(VerdictKind.Pass, verdict.Kind);
            Assert.Contains("not applicable", verdict.Reason);
            Assert.False(string.IsNullOrWhiteSpace(verdict.Source));
        }
    }

    [Fact]
    public async Task NonCriticalPartStillScreensVendorIdentity()
    {
        var engine = new ComplianceEngine([
            new ScreeningEntry("OFAC SDN (fictional test entry)", "DEMO-TEST", "Blocked Test Entity Ltd", [], null)
        ]);
        var vendor = new VendorDto("V-T", "Blocked Test Entity Ltd", "MX", false, null, "NET0");
        var item = new ItemDto("SCR-9", "Test screw", false, 1, "EA");
        var verdicts = await engine.EvaluateVendorAsync(
            vendor, item, [], new ProposedAward("SCR-9", 100, "MX"), History());
        var screening = verdicts.Single(v => v.RuleId == "SCR-01");
        Assert.Equal(VerdictKind.Block, screening.Kind);
    }

    [Fact]
    public async Task ItemAwardMismatchThrows()
    {
        var boom = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.EvaluateVendorAsync(
                Vendor(), CriticalCell(), DocsWithout(), new ProposedAward("SCR-9", 100, "MX"), History()));
        Assert.Contains("does not match", boom.Message);
    }
}

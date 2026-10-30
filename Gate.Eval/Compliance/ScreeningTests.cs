// SCR-01 restricted-party screening: silent pass on the seed, cited block
// on a match, human review when the snapshot is unavailable. All names here
// are fictional test doubles; the bundled snapshot is fictional too.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class ScreeningTests
{
    private static IReadOnlyList<VendorDocumentDto> FullDocs(string vendorId) =>
    [
        new VendorDocumentDto(vendorId, "UN38.3", "un383", true),
        new VendorDocumentDto(vendorId, "SDS", "sds", true),
        new VendorDocumentDto(vendorId, "RoHS", "rohs", true),
        new VendorDocumentDto(vendorId, "REACH", "reach", true)
    ];

    private static IReadOnlyList<VolumeHistory> History() =>
        [new VolumeHistory("Home", "MX", 900), new VolumeHistory("Other", "OTHER", 100)];

    private static async Task<Verdict> ScreeningVerdictAsync(ComplianceEngine engine, string vendorName)
    {
        var vendor = new VendorDto("V-T", vendorName, "MX", false, null, "NET0");
        var verdicts = await engine.EvaluateVendorAsync(
            vendor, new ItemDto("BAT-9", "Test cell", true, 10, "EA"), FullDocs("V-T"), new ProposedAward("BAT-9", 10, "MX"), History());
        return verdicts.Single(v => v.RuleId == "SCR-01");
    }

    [Fact]
    public async Task BundledSnapshotPassesCleanVendorSilently()
    {
        var verdict = await ScreeningVerdictAsync(new ComplianceEngine(), "Compliant Cells Inc");
        Assert.Equal(VerdictKind.Pass, verdict.Kind);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Source));
    }

    [Fact]
    public async Task NameMatchBlocksCitingListAndProgram()
    {
        var engine = new ComplianceEngine([
            new ScreeningEntry("OFAC SDN (fictional test entry)", "DEMO-TEST", "Blocked Test Entity Ltd", [], null)
        ]);
        var verdict = await ScreeningVerdictAsync(engine, "Blocked Test Entity Ltd");
        Assert.Equal(VerdictKind.Block, verdict.Kind);
        Assert.Contains("OFAC SDN", verdict.Reason);
        Assert.Contains("DEMO-TEST", verdict.Reason);
    }

    [Fact]
    public async Task AliasMatchBlocks()
    {
        var engine = new ComplianceEngine([
            new ScreeningEntry("BIS Entity List (fictional test entry)", "DEMO-TEST", "Denied Test Components Inc", ["DTC Front"], null)
        ]);
        var verdict = await ScreeningVerdictAsync(engine, "DTC Front");
        Assert.Equal(VerdictKind.Block, verdict.Kind);
    }

    [Fact]
    public async Task MatchIsCaseInsensitive()
    {
        var engine = new ComplianceEngine([
            new ScreeningEntry("OFAC SDN (fictional test entry)", "DEMO-TEST", "Blocked Test Entity Ltd", [], null)
        ]);
        var verdict = await ScreeningVerdictAsync(engine, "  blocked test entity ltd ");
        Assert.Equal(VerdictKind.Block, verdict.Kind);
    }

    [Fact]
    public async Task EmptySnapshotPasses()
    {
        var verdict = await ScreeningVerdictAsync(new ComplianceEngine([]), "Anyone Ltd");
        Assert.Equal(VerdictKind.Pass, verdict.Kind);
    }

    [Fact]
    public async Task SnapshotFailureRoutesToHumanReview()
    {
        var engine = new ComplianceEngine(() => throw new InvalidOperationException("disk gone"));
        var verdict = await ScreeningVerdictAsync(engine, "Anyone Ltd");
        Assert.Equal(VerdictKind.HumanReview, verdict.Kind);
        Assert.Contains("unavailable", verdict.Reason);
        Assert.Contains("disk gone", verdict.Reason);
    }

    [Fact]
    public void BundledSnapshotLoadsWithFictionalEntries()
    {
        var snapshot = ScreeningSnapshotLoader.LoadEmbedded();
        Assert.NotEmpty(snapshot.Entries);
        Assert.All(snapshot.Entries, e => Assert.Contains("fictional", e.List.ToLowerInvariant()));
    }
}

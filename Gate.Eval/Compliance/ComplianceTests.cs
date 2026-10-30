// Starter compliance rules: CORP-02 origin cap, document checks, ELEC-04.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class ComplianceTests
{
    private readonly ComplianceEngine _engine = new();

    private static IReadOnlyList<VendorDocumentDto> FullDocs(string vendorId) =>
    [
        new VendorDocumentDto(vendorId, "UN38.3", "un383", true),
        new VendorDocumentDto(vendorId, "SDS", "sds", true),
        new VendorDocumentDto(vendorId, "RoHS", "rohs", true),
        new VendorDocumentDto(vendorId, "REACH", "reach", true)
    ];

    [Fact]
    public async Task Corp02BlocksCheaperVendorOverCap()
    {
        var vendor = new VendorDto("V-A", "Cheap Cells Ltd", "CN", false, null, "NET0");
        var docs = FullDocs("V-A");
        // (59 + 10) / (100 + 10) = 62.7%: award breaches the 60% cap.
        var history = new[]
        {
            new VolumeHistory("V-A CN active", "CN", 34),
            new VolumeHistory("Legacy CN inactive", "CN", 25),
            new VolumeHistory("V-B MX active", "MX", 38),
            new VolumeHistory("Other non-CN", "OTHER", 3)
        };
        var verdicts = await _engine.EvaluateVendorAsync(vendor, new ItemDto("BAT-1", "Test cell", true, 10, "EA"), docs, new ProposedAward("BAT-1", 10, "CN"), history);
        var corp = verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Block, corp.Kind);
        Assert.Contains("CORP-02", corp.Reason + corp.RuleId);
    }

    [Fact]
    public async Task Corp02PassesCompliantVendor()
    {
        var vendor = new VendorDto("V-B", "Compliant Cells Inc", "MX", false, null, "NET0");
        var docs = FullDocs("V-B");
        // (38 + 10) / (100 + 10) = 43.6%: award stays within the 60% cap.
        var history = new[]
        {
            new VolumeHistory("V-A CN active", "CN", 34),
            new VolumeHistory("Legacy CN inactive", "CN", 25),
            new VolumeHistory("V-B MX active", "MX", 38),
            new VolumeHistory("Other non-CN", "OTHER", 3)
        };
        var verdicts = await _engine.EvaluateVendorAsync(vendor, new ItemDto("BAT-1", "Test cell", true, 10, "EA"), docs, new ProposedAward("BAT-1", 10, "MX"), history);
        Assert.All(verdicts, v => Assert.Equal(VerdictKind.Pass, v.Kind));
    }

    [Fact]
    public async Task MissingUn383Blocks()
    {
        var vendor = new VendorDto("V-B", "Compliant Cells Inc", "MX", false, null, "NET0");
        var docs = new[]
        {
            new VendorDocumentDto("V-B", "SDS", "sds-b", true),
            new VendorDocumentDto("V-B", "RoHS", "rohs-b", true),
            new VendorDocumentDto("V-B", "REACH", "reach-b", true)
        };
        var history = new[]
        {
            new VolumeHistory("V-A CN active", "CN", 59),
            new VolumeHistory("V-B MX active", "MX", 38),
            new VolumeHistory("Other non-CN", "OTHER", 3)
        };
        var verdicts = await _engine.EvaluateVendorAsync(vendor, new ItemDto("BAT-1", "Test cell", true, 10, "EA"), docs, new ProposedAward("BAT-1", 10, "MX"), history);
        Assert.Contains(verdicts, v => v.RuleId == "BAT-LI-01" && v.Kind == VerdictKind.Block);
    }

    [Fact]
    public async Task OpenHoldBlocksPayment()
    {
        var holds = new[] { new HoldDto("HLD-1", "DOCK", "Mark missing.", "BAT-LI-03", "case-1", "agent", DateTimeOffset.UtcNow, HoldStatus.Set, "Shipment", "RCT-1", "PO-L1", "LOT-1", "CTN-07") };
        var verdicts = await _engine.EvaluatePaymentAsync(holds);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Block, verdicts[0].Kind);
        Assert.Equal(ComplianceEngine.PrePayHoldId, verdicts[0].RuleId);
    }

    [Fact]
    public async Task NoHoldsPassesPayment()
    {
        var verdicts = await _engine.EvaluatePaymentAsync([]);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Pass, verdicts[0].Kind);
    }

    [Fact]
    public async Task MultipleHoldsListEveryHold()
    {
        var holds = new[]
        {
            new HoldDto("HLD-1", "DOCK", "Mark missing.", "BAT-LI-03", "case-1", "agent", DateTimeOffset.UtcNow, HoldStatus.Set, "Shipment", "RCT-1", "PO-L1", "LOT-1", "CTN-07"),
            new HoldDto("HLD-2", "DOCK", "Damage review open.", "BAT-LI-05", "case-1", "agent", DateTimeOffset.UtcNow, HoldStatus.Set, "Shipment", "RCT-1", "PO-L1", "LOT-1", "CTN-04")
        };
        var verdicts = await _engine.EvaluatePaymentAsync(holds);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Block, verdicts[0].Kind);
        Assert.Contains("HLD-1", verdicts[0].Reason);
        Assert.Contains("HLD-2", verdicts[0].Reason);
    }
}

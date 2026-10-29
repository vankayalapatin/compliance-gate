// Starter compliance rules: CORP-02 origin cap, document checks, ELEC-04.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class ComplianceTests
{
    private readonly ComplianceEngine _engine = new();

    [Fact]
    public async Task Corp02BlocksCheaperVendorOverCap()
    {
        var vendor = new VendorDto("V-A", "Cheap Cells Ltd", "CN", false, null);
        var docs = new[]
        {
            new VendorDocumentDto("V-A", "UN38.3", "un383-a", true),
            new VendorDocumentDto("V-A", "SDS", "sds-a", true)
        };
        // (59 + 10) / (100 + 10) = 62.7%: award breaches the 60% cap.
        var history = new[]
        {
            new VolumeHistory("V-A CN active", "CN", 34),
            new VolumeHistory("Legacy CN inactive", "CN", 25),
            new VolumeHistory("V-B MX active", "MX", 38),
            new VolumeHistory("Other non-CN", "OTHER", 3)
        };
        var verdicts = await _engine.EvaluateVendorAsync(vendor, docs, new ProposedAward("BAT-1", 10, "CN"), history);
        var corp = verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Block, corp.Kind);
        Assert.Contains("CORP-02", corp.Reason + corp.RuleId);
    }

    [Fact]
    public async Task Corp02PassesCompliantVendor()
    {
        var vendor = new VendorDto("V-B", "Compliant Cells Inc", "MX", false, null);
        var docs = new[]
        {
            new VendorDocumentDto("V-B", "UN38.3", "un383-b", true),
            new VendorDocumentDto("V-B", "SDS", "sds-b", true)
        };
        // (38 + 10) / (100 + 10) = 43.6%: award stays within the 60% cap.
        var history = new[]
        {
            new VolumeHistory("V-A CN active", "CN", 34),
            new VolumeHistory("Legacy CN inactive", "CN", 25),
            new VolumeHistory("V-B MX active", "MX", 38),
            new VolumeHistory("Other non-CN", "OTHER", 3)
        };
        var verdicts = await _engine.EvaluateVendorAsync(vendor, docs, new ProposedAward("BAT-1", 10, "MX"), history);
        Assert.All(verdicts, v => Assert.Equal(VerdictKind.Pass, v.Kind));
    }

    [Fact]
    public async Task MissingUn383Blocks()
    {
        var vendor = new VendorDto("V-B", "Compliant Cells Inc", "MX", false, null);
        var docs = new[] { new VendorDocumentDto("V-B", "SDS", "sds-b", true) };
        var history = new[]
        {
            new VolumeHistory("V-A CN active", "CN", 59),
            new VolumeHistory("V-B MX active", "MX", 38),
            new VolumeHistory("Other non-CN", "OTHER", 3)
        };
        var verdicts = await _engine.EvaluateVendorAsync(vendor, docs, new ProposedAward("BAT-1", 10, "MX"), history);
        Assert.Contains(verdicts, v => v.RuleId == "BAT-LI-01" && v.Kind == VerdictKind.Block);
    }

    [Fact]
    public async Task OpenHoldBlocksPayment()
    {
        var holds = new[] { new HoldDto("HLD-1", "DOCK", "Mark missing.", "BAT-LI-03", "case-1", "agent", DateTimeOffset.UtcNow) };
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
}

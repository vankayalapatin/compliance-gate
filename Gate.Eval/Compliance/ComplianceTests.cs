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
        var verdicts = await _engine.EvaluateVendorAsync(vendor, docs, 0.65m);
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
        var verdicts = await _engine.EvaluateVendorAsync(vendor, docs, 0.30m);
        Assert.All(verdicts, v => Assert.Equal(VerdictKind.Pass, v.Kind));
    }

    [Fact]
    public async Task MissingUn383Blocks()
    {
        var vendor = new VendorDto("V-B", "Compliant Cells Inc", "MX", false, null);
        var docs = new[] { new VendorDocumentDto("V-B", "SDS", "sds-b", true) };
        var verdicts = await _engine.EvaluateVendorAsync(vendor, docs, 0.30m);
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

// CORP-02 logged-exception path: a valid named-approver exception excuses
// the origin cap; anything less still blocks. Test-double data only.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class CorpExceptionTests
{
    private readonly ComplianceEngine _engine = new();

    private static VendorDto Vendor() =>
        new("V-A", "CinderVolt Energy Ltd", "CN", false, null, "NET0");

    private static IReadOnlyList<VendorDocumentDto> FullDocs() =>
    [
        new VendorDocumentDto("V-A", "UN38.3", "un383", true),
        new VendorDocumentDto("V-A", "SDS", "sds", true),
        new VendorDocumentDto("V-A", "RoHS", "rohs", true),
        new VendorDocumentDto("V-A", "REACH", "reach", true)
    ];

    // (59 + 10) / (100 + 10) = 62.7%: over the 60% cap without an exception.
    private static IReadOnlyList<VolumeHistory> History() =>
        [new VolumeHistory("Home", "CN", 59), new VolumeHistory("Other", "OTHER", 41)];

    private async Task<Verdict> CorpVerdictAsync(CorpExceptionApproval? exception)
    {
        var verdicts = await _engine.EvaluateVendorWithExceptionAsync(
            Vendor(), new ItemDto("BAT-9", "Test cell", true, 10, "EA"), FullDocs(), new ProposedAward("BAT-9", 10, "CN"), History(), exception);
        return verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
    }

    [Fact]
    public async Task ValidExceptionPassesOverCapAward()
    {
        var exception = new CorpExceptionApproval(
            "CORP-02", "V-A", "Dana Director", DateTimeOffset.UtcNow, "sole qualified source this quarter");
        var verdict = await CorpVerdictAsync(exception);
        Assert.Equal(VerdictKind.Pass, verdict.Kind);
        Assert.Contains("Dana Director", verdict.Reason);
        Assert.Contains("sole qualified source", verdict.Reason);
    }

    [Fact]
    public async Task NullExceptionStillBlocks()
    {
        var verdict = await CorpVerdictAsync(null);
        Assert.Equal(VerdictKind.Block, verdict.Kind);
    }

    [Fact]
    public async Task BlankApproverStillBlocks()
    {
        var exception = new CorpExceptionApproval("CORP-02", "V-A", "  ", DateTimeOffset.UtcNow, "reason recorded");
        var verdict = await CorpVerdictAsync(exception);
        Assert.Equal(VerdictKind.Block, verdict.Kind);
    }

    [Fact]
    public async Task BlankReasonStillBlocks()
    {
        var exception = new CorpExceptionApproval("CORP-02", "V-A", "Dana Director", DateTimeOffset.UtcNow, "");
        var verdict = await CorpVerdictAsync(exception);
        Assert.Equal(VerdictKind.Block, verdict.Kind);
    }

    [Fact]
    public async Task WrongVendorExceptionStillBlocks()
    {
        var exception = new CorpExceptionApproval("CORP-02", "V-B", "Dana Director", DateTimeOffset.UtcNow, "reason recorded");
        var verdict = await CorpVerdictAsync(exception);
        Assert.Equal(VerdictKind.Block, verdict.Kind);
    }

    [Fact]
    public async Task WrongRuleExceptionStillBlocks()
    {
        var exception = new CorpExceptionApproval("CORP-01", "V-A", "Dana Director", DateTimeOffset.UtcNow, "reason recorded");
        var verdict = await CorpVerdictAsync(exception);
        Assert.Equal(VerdictKind.Block, verdict.Kind);
    }

    [Fact]
    public async Task ExceptionOnPassingAwardStaysPass()
    {
        var history = new[] { new VolumeHistory("Home", "CN", 10), new VolumeHistory("Other", "OTHER", 90) };
        var exception = new CorpExceptionApproval("CORP-02", "V-A", "Dana Director", DateTimeOffset.UtcNow, "not needed");
        var verdicts = await _engine.EvaluateVendorWithExceptionAsync(
            Vendor(), new ItemDto("BAT-9", "Test cell", true, 10, "EA"), FullDocs(), new ProposedAward("BAT-9", 10, "CN"), history, exception);
        var verdict = verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Pass, verdict.Kind);
        Assert.Contains("within", verdict.Reason);
    }
}

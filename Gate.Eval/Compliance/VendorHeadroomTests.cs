// Vendor headroom: most a supplier can provide under every cap with the
// rest of the request already open (last position), open orders counted,
// in whole cartons. Test-owned numbers only; no app db.
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class VendorHeadroomTests
{
    private static VendorDto Vendor(string country) =>
        new("V-T", "Test Cells Ltd", country, false, null, "NET0");

    private static ItemDto CriticalCell() =>
        new("BAT-9", "Test cell", true, 10, "EA");

    private static ItemDto NonCriticalPart() =>
        new("SCR-9", "Test screw", false, 1, "EA");

    private static IReadOnlyList<VendorDocumentDto> FullDocs() =>
    [
        new VendorDocumentDto("V-T", "UN38.3", "un383", true),
        new VendorDocumentDto("V-T", "SDS", "sds", true),
        new VendorDocumentDto("V-T", "RoHS", "rohs", true),
        new VendorDocumentDto("V-T", "REACH", "reach", true)
    ];

    private static IReadOnlyList<VendorDocumentDto> DocsWithoutUn383() =>
    [
        new VendorDocumentDto("V-T", "SDS", "sds", true),
        new VendorDocumentDto("V-T", "RoHS", "rohs", true),
        new VendorDocumentDto("V-T", "REACH", "reach", true)
    ];

    private static ComplianceEngine CleanEngine() =>
        new ComplianceEngine([]);

    [Fact]
    public async Task FullRequestPossible_WhenWithinCap()
    {
        // (30 + 10) / (100 + 10) = 36.4%: within the 60% cap.
        var history = new[]
        {
            new VolumeHistory("Home", "MX", 30),
            new VolumeHistory("Other", "OTHER", 70)
        };
        var headroom = await CleanEngine().HeadroomAsync(
            Vendor("MX"), CriticalCell(), FullDocs(), 10, history, [], 10);
        Assert.Equal(10m, headroom.HeadroomQty);
        Assert.Equal(1, headroom.HeadroomCartons);
        Assert.True(headroom.FullRequestPossible);
        Assert.Null(headroom.BindingRuleId);
        Assert.Null(headroom.BindingReason);
        Assert.Equal("V-T", headroom.VendorId);
    }

    [Fact]
    public async Task PartialHeadroom_WithCapBinding()
    {
        // (50 + 20) / (100 + 30) = 53.8% passes; (50 + 30) / (100 + 30) = 61.5% blocks.
        var history = new[]
        {
            new VolumeHistory("Home", "CN", 50),
            new VolumeHistory("Other", "OTHER", 50)
        };
        var headroom = await CleanEngine().HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 30, history, [], 10);
        Assert.Equal(20m, headroom.HeadroomQty);
        Assert.Equal(2, headroom.HeadroomCartons);
        Assert.False(headroom.FullRequestPossible);
        Assert.Equal(ComplianceEngine.CorpOriginCapId, headroom.BindingRuleId);
        Assert.Contains("exceeding", headroom.BindingReason!);
    }

    [Fact]
    public async Task ZeroHeadroom_WithDocumentFailureBinding()
    {
        // Cap would pass (10 + 20) / (100 + 20) = 25%, but UN38.3 is missing at every quantity.
        var history = new[]
        {
            new VolumeHistory("Home", "MX", 10),
            new VolumeHistory("Other", "OTHER", 90)
        };
        var headroom = await CleanEngine().HeadroomAsync(
            Vendor("MX"), CriticalCell(), DocsWithoutUn383(), 20, history, [], 10);
        Assert.Equal(0m, headroom.HeadroomQty);
        Assert.Equal(0, headroom.HeadroomCartons);
        Assert.False(headroom.FullRequestPossible);
        Assert.Equal("BAT-LI-01", headroom.BindingRuleId);
        Assert.Contains("missing", headroom.BindingReason!);
    }

    [Fact]
    public async Task OpenOrders_SameCountryCountsInShare()
    {
        // No open orders: headroom 20 (see PartialHeadroom_WithCapBinding).
        // Open CN 30: (80 + 10) / (130 + 30) = 56.2% passes but
        // (80 + 20) / (130 + 30) = 62.5% blocks the second carton.
        var history = new[]
        {
            new VolumeHistory("Home", "CN", 50),
            new VolumeHistory("Other", "OTHER", 50)
        };
        var engine = CleanEngine();
        var withoutOpen = await engine.HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 30, history, [], 10);
        Assert.Equal(20m, withoutOpen.HeadroomQty);

        var open = new[] { new VolumeHistory("Open commitment", "CN", 30) };
        var withOpen = await engine.HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 30, history, open, 10);
        Assert.Equal(10m, withOpen.HeadroomQty);
        Assert.Equal(1, withOpen.HeadroomCartons);
        Assert.False(withOpen.FullRequestPossible);
        Assert.Equal(ComplianceEngine.CorpOriginCapId, withOpen.BindingRuleId);
    }

    [Fact]
    public async Task OpenOrders_OtherCountryCountsInTotal()
    {
        // No open orders: (50 + 30) / (100 + 30) = 61.5% blocks.
        // Open OTHER 40: (50 + 30) / (140 + 30) = 47.1% passes.
        var history = new[]
        {
            new VolumeHistory("Home", "CN", 50),
            new VolumeHistory("Other", "OTHER", 50)
        };
        var engine = CleanEngine();
        var withoutOpen = await engine.HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 30, history, [], 10);
        Assert.False(withoutOpen.FullRequestPossible);

        var open = new[] { new VolumeHistory("Open commitment", "OTHER", 40) };
        var withOpen = await engine.HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 30, history, open, 10);
        Assert.Equal(30m, withOpen.HeadroomQty);
        Assert.Equal(3, withOpen.HeadroomCartons);
        Assert.True(withOpen.FullRequestPossible);
        Assert.Null(withOpen.BindingRuleId);
    }

    [Fact]
    public async Task WholeCartonRoundingDown_WhenFullPasses()
    {
        // (50 + 25) / (100 + 25) = 60.0% exactly: the full 25 passes, but headroom rounds to 20.
        var history = new[]
        {
            new VolumeHistory("Home", "CN", 50),
            new VolumeHistory("Other", "OTHER", 50)
        };
        var headroom = await CleanEngine().HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 25, history, [], 10);
        Assert.Equal(20m, headroom.HeadroomQty);
        Assert.Equal(2, headroom.HeadroomCartons);
        Assert.True(headroom.FullRequestPossible);
        Assert.Null(headroom.BindingRuleId);
        Assert.True(headroom.HeadroomQty < 25);
        Assert.Equal(0m, headroom.HeadroomQty % 10);
    }

    [Fact]
    public async Task WholeCartonRoundingDown_WhenRemainderFails()
    {
        // (50 + 20) / (100 + 6 + 20) = 55.6% passes; the full 26 blocks at 60.3%.
        var history = new[]
        {
            new VolumeHistory("Home", "CN", 50),
            new VolumeHistory("Other", "OTHER", 50)
        };
        var headroom = await CleanEngine().HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 26, history, [], 10);
        Assert.Equal(20m, headroom.HeadroomQty);
        Assert.Equal(2, headroom.HeadroomCartons);
        Assert.False(headroom.FullRequestPossible);
        Assert.Equal(ComplianceEngine.CorpOriginCapId, headroom.BindingRuleId);
    }

    [Fact]
    public async Task Generality_CustomCapChangesHeadroom()
    {
        // (30 + 30) / (100 + 30) = 46.2%: passes at 0.60, but at 0.40 only
        // (30 + 20) / (100 + 30) = 38.5% passes while (30 + 30) / (100 + 30) = 46.2% blocks.
        var history = new[]
        {
            new VolumeHistory("Home", "CN", 30),
            new VolumeHistory("Other", "OTHER", 70)
        };
        var standard = await new ComplianceEngine().HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 30, history, [], 10);
        Assert.Equal(30m, standard.HeadroomQty);
        Assert.True(standard.FullRequestPossible);

        var strict = new ComplianceEngine(new CorpPackConfig(0.40m, 0.50m, "test policy"));
        var tight = await strict.HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 30, history, [], 10);
        Assert.Equal(20m, tight.HeadroomQty);
        Assert.Equal(2, tight.HeadroomCartons);
        Assert.False(tight.FullRequestPossible);
        Assert.Equal(ComplianceEngine.CorpOriginCapId, tight.BindingRuleId);
    }

    [Fact]
    public async Task Generality_NonCriticalIgnoresCap()
    {
        // (90 + 50) / (100 + 50) = 93.3% would breach, but non-critical parts are out of scope.
        var history = new[]
        {
            new VolumeHistory("Home", "CN", 90),
            new VolumeHistory("Other", "OTHER", 10)
        };
        var headroom = await CleanEngine().HeadroomAsync(
            Vendor("CN"), NonCriticalPart(), [], 50, history, [], 10);
        Assert.Equal(50m, headroom.HeadroomQty);
        Assert.Equal(5, headroom.HeadroomCartons);
        Assert.True(headroom.FullRequestPossible);
        Assert.Null(headroom.BindingRuleId);
    }

    [Fact]
    public async Task Generality_HeadroomConsistentWithEngineVerdicts()
    {
        var history = new[]
        {
            new VolumeHistory("Home", "CN", 50),
            new VolumeHistory("Other", "OTHER", 50)
        };
        var engine = CleanEngine();
        var vendor = Vendor("CN");
        var item = CriticalCell();
        var headroom = await engine.HeadroomAsync(vendor, item, FullDocs(), 30, history, [], 10);
        Assert.Equal(20m, headroom.HeadroomQty);

        var atHeadroom = await engine.EvaluateVendorWithExceptionAsync(
            vendor, item, FullDocs(), new ProposedAward(item.ItemId, headroom.HeadroomQty, vendor.CountryOfOrigin),
            history, null);
        Assert.All(atHeadroom, v => Assert.Equal(VerdictKind.Pass, v.Kind));

        var aboveQty = headroom.HeadroomQty + 10;
        var above = await engine.EvaluateVendorWithExceptionAsync(
            vendor, item, FullDocs(), new ProposedAward(item.ItemId, aboveQty, vendor.CountryOfOrigin),
            history, null);
        var binding = above.Single(v => v.RuleId == headroom.BindingRuleId);
        Assert.Equal(VerdictKind.Block, binding.Kind);
        Assert.Equal(headroom.BindingReason, binding.Reason);
    }

    [Fact]
    public async Task ZeroHeadroom_WhenHistoryAlreadyOverCap()
    {
        // Zero reads 70 / 120 = 58.3% with the rest open, but the first
        // carton blocks at (70 + 10) / 120 = 66.7%: headroom stays zero.
        var history = new[]
        {
            new VolumeHistory("Home", "CN", 70),
            new VolumeHistory("Other", "OTHER", 30)
        };
        var headroom = await CleanEngine().HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 20, history, [], 10);
        Assert.Equal(0m, headroom.HeadroomQty);
        Assert.Equal(0, headroom.HeadroomCartons);
        Assert.False(headroom.FullRequestPossible);
        Assert.Equal(ComplianceEngine.CorpOriginCapId, headroom.BindingRuleId);
    }

    [Fact]
    public async Task EmptyHistory_PassesAsNotApplicable()
    {
        var headroom = await CleanEngine().HeadroomAsync(
            Vendor("CN"), CriticalCell(), FullDocs(), 20, [], [], 10);
        Assert.Equal(20m, headroom.HeadroomQty);
        Assert.Equal(2, headroom.HeadroomCartons);
        Assert.True(headroom.FullRequestPossible);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task InvalidCartonSizeThrows(int cartonSize)
    {
        var history = new[] { new VolumeHistory("Home", "MX", 10) };
        var boom = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CleanEngine().HeadroomAsync(Vendor("MX"), CriticalCell(), FullDocs(), 10, history, [], cartonSize));
        Assert.Equal("cartonSize", boom.ParamName);
    }

    [Fact]
    public async Task NegativeRequestedQtyThrows()
    {
        var history = new[] { new VolumeHistory("Home", "MX", 10) };
        var boom = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CleanEngine().HeadroomAsync(Vendor("MX"), CriticalCell(), FullDocs(), -5, history, [], 10));
        Assert.Equal("requestedQty", boom.ParamName);
    }
}

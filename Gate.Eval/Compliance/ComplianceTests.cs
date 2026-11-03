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
        var vendor = new VendorDto("V-A", "CinderVolt Energy Ltd", "CN", false, null, "NET0");
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
        var vendor = new VendorDto("V-B", "Meridian Cellworks Inc", "MX", false, null, "NET0");
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
        var vendor = new VendorDto("V-B", "Meridian Cellworks Inc", "MX", false, null, "NET0");
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

    private static readonly DateTimeOffset FixedAt = new(2026, 5, 4, 12, 0, 0, TimeSpan.Zero);

    private static HoldDto MakeHold(string holdId, string? cartonId = null) =>
        new(holdId, "DOCK", "Mark missing.", "BAT-LI-03", "case-1", "agent", FixedAt, HoldStatus.Set, "Shipment", "RCT-1", "PO-L1", "LOT-1", cartonId ?? "CTN-07");

    private static OpenReview MakeReview(string reviewId, string ruleId) =>
        new(reviewId, ruleId, "case-1", "CTN-07", "LOT-1", FixedAt, "Damage needs a human look.");

    [Fact]
    public async Task OpenHoldBlocksPayment()
    {
        var state = new PrePayState([MakeHold("HLD-1")], [], []);
        var verdicts = await _engine.EvaluatePaymentAsync(state);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Block, verdicts[0].Kind);
        Assert.Equal(ComplianceEngine.PrePayHoldId, verdicts[0].RuleId);
    }

    [Fact]
    public async Task NoHoldsPassesPayment()
    {
        var state = new PrePayState([], [], []);
        var verdicts = await _engine.EvaluatePaymentAsync(state);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Pass, verdicts[0].Kind);
    }

    [Fact]
    public async Task MultipleHoldsListEveryHold()
    {
        var state = new PrePayState([MakeHold("HLD-1"), MakeHold("HLD-2", "CTN-04")], [], []);
        var verdicts = await _engine.EvaluatePaymentAsync(state);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Block, verdicts[0].Kind);
        Assert.Contains("HLD-1", verdicts[0].Reason);
        Assert.Contains("HLD-2", verdicts[0].Reason);
    }

    [Fact]
    public async Task HoldsOnlyBlocksPaymentViaNewOverload()
    {
        var state = new PrePayState([MakeHold("HLD-9")], [], []);
        var verdicts = await _engine.EvaluatePaymentAsync(state);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Block, verdicts[0].Kind);
        Assert.Equal(ComplianceEngine.PrePayHoldId, verdicts[0].RuleId);
        Assert.Equal(RuleCatalog.Find("ELEC-04").Source, verdicts[0].Source);
        Assert.Contains("HLD-9", verdicts[0].Reason);
        var repeat = await _engine.EvaluatePaymentAsync(state);
        Assert.Equal(verdicts[0].Reason, repeat[0].Reason);
    }

    [Fact]
    public async Task QuarantineOnlyBlocksPayment()
    {
        var state = new PrePayState([], ["LOT-Q1", "LOT-Q2"], []);
        var verdicts = await _engine.EvaluatePaymentAsync(state);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Block, verdicts[0].Kind);
        Assert.Equal(ComplianceEngine.PrePayHoldId, verdicts[0].RuleId);
        Assert.Equal(RuleCatalog.Find("ELEC-04").Source, verdicts[0].Source);
        Assert.Contains("LOT-Q1", verdicts[0].Reason);
        Assert.Contains("LOT-Q2", verdicts[0].Reason);
    }

    [Fact]
    public async Task ReviewOnlyBlocksPayment()
    {
        var state = new PrePayState([], [], [MakeReview("REV-1", "BAT-LI-05")]);
        var verdicts = await _engine.EvaluatePaymentAsync(state);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Block, verdicts[0].Kind);
        Assert.Equal(ComplianceEngine.PrePayHoldId, verdicts[0].RuleId);
        Assert.Equal(RuleCatalog.Find("ELEC-04").Source, verdicts[0].Source);
        Assert.Contains("REV-1", verdicts[0].Reason);
        Assert.Contains("BAT-LI-05", verdicts[0].Reason);
    }

    [Fact]
    public async Task CombinedStateBlocksListingAllThree()
    {
        var state = new PrePayState(
            [MakeHold("HLD-1"), MakeHold("HLD-2", "CTN-04")],
            ["LOT-Q1"],
            [MakeReview("REV-1", "BAT-LI-05"), MakeReview("REV-2", "BAT-LI-03")]);
        var verdicts = await _engine.EvaluatePaymentAsync(state);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Block, verdicts[0].Kind);
        Assert.Equal(ComplianceEngine.PrePayHoldId, verdicts[0].RuleId);
        Assert.Contains("HLD-1", verdicts[0].Reason);
        Assert.Contains("HLD-2", verdicts[0].Reason);
        Assert.Contains("LOT-Q1", verdicts[0].Reason);
        Assert.Contains("REV-1", verdicts[0].Reason);
        Assert.Contains("BAT-LI-05", verdicts[0].Reason);
        Assert.Contains("REV-2", verdicts[0].Reason);
        Assert.Contains("BAT-LI-03", verdicts[0].Reason);
    }

    [Fact]
    public async Task EmptyPrePayStatePassesWithAllClear()
    {
        var state = new PrePayState([], [], []);
        var verdicts = await _engine.EvaluatePaymentAsync(state);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Pass, verdicts[0].Kind);
        Assert.Equal(ComplianceEngine.PrePayHoldId, verdicts[0].RuleId);
        Assert.Contains("No open holds", verdicts[0].Reason);
        Assert.Contains("no quarantined lots", verdicts[0].Reason);
        Assert.Contains("no open reviews", verdicts[0].Reason);
    }

    [Fact]
    public async Task OpenReviewsBlockTheWholePayment()
    {
        var state = new PrePayState([], [], [MakeReview("REV-1", "BAT-LI-05")]);
        var verdicts = await _engine.EvaluatePayableAsync(state);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Block, verdicts[0].Kind);
        Assert.Equal(ComplianceEngine.PrePayHoldId, verdicts[0].RuleId);
        Assert.Equal(RuleCatalog.Find("ELEC-04").Source, verdicts[0].Source);
        Assert.Contains("REV-1", verdicts[0].Reason);
        Assert.Contains("BAT-LI-05", verdicts[0].Reason);
    }

    [Fact]
    public async Task HoldsAndQuarantineLeaveAcceptedValuePayable()
    {
        var state = new PrePayState([MakeHold("HLD-1")], ["LOT-Q1"], []);
        var verdicts = await _engine.EvaluatePayableAsync(state);
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Pass, verdicts[0].Kind);
        Assert.Equal(ComplianceEngine.PrePayHoldId, verdicts[0].RuleId);
        Assert.Contains("accepted value is payable", verdicts[0].Reason);
    }

    [Fact]
    public async Task EmptyStatePassesPayable()
    {
        var verdicts = await _engine.EvaluatePayableAsync(new PrePayState([], [], []));
        Assert.Single(verdicts);
        Assert.Equal(VerdictKind.Pass, verdicts[0].Kind);
    }

    [Fact]
    public async Task Corp02VerdictCarriesShareFigures()
    {
        var vendor = new VendorDto("V-A", "CinderVolt Energy Ltd", "CN", false, null, "NET0");
        // (59 + 10) / (100 + 10) = 62.7%: the stored figures pin the share.
        var history = new[]
        {
            new VolumeHistory("V-A CN active", "CN", 34),
            new VolumeHistory("Legacy CN inactive", "CN", 25),
            new VolumeHistory("V-B MX active", "MX", 38),
            new VolumeHistory("Other non-CN", "OTHER", 3)
        };
        var verdicts = await _engine.EvaluateVendorAsync(vendor, new ItemDto("BAT-1", "Test cell", true, 10, "EA"), FullDocs("V-A"), new ProposedAward("BAT-1", 10, "CN"), history);
        var corp = verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        var figures = corp.OriginCap;
        Assert.NotNull(figures);
        Assert.Equal("BAT-1", figures.ItemId);
        Assert.Equal(59m, figures.CountryCells);
        Assert.Equal(100m, figures.TotalCells);
        Assert.Equal(10m, figures.AwardQty);
        Assert.Equal(0.6m, figures.Cap);
        Assert.Equal(0.627m, Math.Round(figures.Share, 3));
    }

    [Fact]
    public async Task Corp02NonCriticalPassCarriesFigures()
    {
        var vendor = new VendorDto("V-C", "Formosa Fasteners Co", "US", false, null, "NET0");
        var history = new[]
        {
            new VolumeHistory("V-C US active", "US", 40),
            new VolumeHistory("Other non-US", "OTHER", 60)
        };
        var verdicts = await _engine.EvaluateVendorAsync(vendor, new ItemDto("SCR-9", "Test screw", false, 10, "EA"), [], new ProposedAward("SCR-9", 10, "US"), history);
        var corp = verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Pass, corp.Kind);
        var figures = corp.OriginCap;
        Assert.NotNull(figures);
        Assert.Equal("SCR-9", figures.ItemId);
        Assert.Equal(40m, figures.CountryCells);
        Assert.Equal(100m, figures.TotalCells);
        Assert.Equal(10m, figures.AwardQty);
    }

    [Fact]
    public async Task Corp02EmptyHistoryPassCarriesNoFigures()
    {
        var vendor = new VendorDto("V-A", "CinderVolt Energy Ltd", "CN", false, null, "NET0");
        var verdicts = await _engine.EvaluateVendorAsync(vendor, new ItemDto("BAT-1", "Test cell", true, 10, "EA"), FullDocs("V-A"), new ProposedAward("BAT-1", 10, "CN"), []);
        var corp = verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Pass, corp.Kind);
        Assert.Null(corp.OriginCap);
    }

    [Fact]
    public async Task Corp02CountsOpenOrdersInBothShareAndTotal()
    {
        var vendor = new VendorDto("V-A", "CinderVolt Energy Ltd", "CN", false, null, "NET0");
        var history = new[]
        {
            new VolumeHistory("V-A CN active", "CN", 34),
            new VolumeHistory("Legacy CN inactive", "CN", 25),
            new VolumeHistory("V-B MX active", "MX", 38),
            new VolumeHistory("Other non-CN", "OTHER", 3)
        };
        var open = new[]
        {
            new OpenOrderQty("PO PO-1", "CN", 100),
            new OpenOrderQty("PO PO-2", "MX", 50)
        };
        // (59 + 100 + 10) / (100 + 150 + 10) = 65.0%: open orders push past the cap.
        var verdicts = await _engine.EvaluateVendorAsync(vendor, new ItemDto("BAT-1", "Test cell", true, 10, "EA"), FullDocs("V-A"), new ProposedAward("BAT-1", 10, "CN"), history, open);
        var corp = verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Block, corp.Kind);
        Assert.Contains("65.0%", corp.Reason);
        Assert.Contains("150 test cells on open orders", corp.Reason);
        var figures = corp.OriginCap;
        Assert.NotNull(figures);
        Assert.Equal(100m, figures.CountryOpenCells);
        Assert.Equal(150m, figures.TotalOpenCells);
        Assert.Equal(0.65m, figures.Share);
    }

    [Fact]
    public async Task Corp02OpenTotalDilutesShare()
    {
        var vendor = new VendorDto("V-A", "CinderVolt Energy Ltd", "CN", false, null, "NET0");
        var history = new[]
        {
            new VolumeHistory("V-A CN active", "CN", 60),
            new VolumeHistory("V-B MX active", "MX", 30),
            new VolumeHistory("Other non-CN", "OTHER", 10)
        };
        var open = new[] { new OpenOrderQty("PO PO-1", "OTHER", 50) };
        // (60 + 0 + 10) / (100 + 50 + 10) = 43.8%: open orders from
        // elsewhere dilute through the total — without them this award
        // blocks at 63.6%.
        var verdicts = await _engine.EvaluateVendorAsync(vendor, new ItemDto("BAT-1", "Test cell", true, 10, "EA"), FullDocs("V-A"), new ProposedAward("BAT-1", 10, "CN"), history, open);
        var corp = verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Pass, corp.Kind);
        Assert.Contains("43.8%", corp.Reason);
        Assert.Contains("50 test cells on open orders", corp.Reason);
    }

    [Fact]
    public async Task Corp02OpenOrdersAloneEngageTheCap()
    {
        var vendor = new VendorDto("V-A", "CinderVolt Energy Ltd", "CN", false, null, "NET0");
        var open = new[] { new OpenOrderQty("PO PO-1", "CN", 200) };
        // No received history, but 200 open: (0 + 200 + 100) / (0 + 200 + 100)
        // = 100%, a real verdict instead of the not-applicable pass.
        var verdicts = await _engine.EvaluateVendorAsync(vendor, new ItemDto("BAT-1", "Test cell", true, 10, "EA"), FullDocs("V-A"), new ProposedAward("BAT-1", 100, "CN"), [], open);
        var corp = verdicts.Single(v => v.RuleId == ComplianceEngine.CorpOriginCapId);
        Assert.Equal(VerdictKind.Block, corp.Kind);
        Assert.Contains("100.0%", corp.Reason);
    }
}

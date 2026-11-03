// Split-plan verifier tests: code judges every proposal before anyone
// sees it. Bad sums, broken cartons, unknown or repeat vendors, failing
// lines, missing savings, and pointless splits all come back refused;
// the good plan carries lines, shares, totals, and the saving.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval.Agent;

public sealed class SplitPlanVerifierTests
{
    private static IReadOnlyDictionary<string, string> CountryMap() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CN"] = "China",
            ["MX"] = "Mexico",
            ["US"] = "United States",
        };

    private static async Task<(InMemoryErpAdapter Erp, CaseFile File)> SetupAsync(
        decimal prQty = 1200, decimal cnCells = 5600)
    {
        var erp = SeededErp.Create();
        erp.SeedVolumeHistory(SeededErp.Battery,
        [
            new VolumeHistory("V-A CN active", "CN", cnCells),
            new VolumeHistory("V-B MX active", "MX", 4000),
            new VolumeHistory("Other non-CN", "OTHER", 10000 - cnCells - 4000),
        ]);
        erp.SeedVendor(new VendorDto("V-D", "CinderVolt Second Plant Ltd", "CN", false, null, "NET0"));
        erp.SeedVendorPrice("V-D", SeededErp.Battery, 13m);
        erp.SeedVendorDocuments("V-D",
        [
            new VendorDocumentDto("V-D", "UN38.3", "un383-d", true),
            new VendorDocumentDto("V-D", "SDS", "sds-d", true),
            new VendorDocumentDto("V-D", "RoHS", "rohs-d", true),
            new VendorDocumentDto("V-D", "REACH", "reach-d", true),
        ]);
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, prQty, "EA")],
            Guid.NewGuid().ToString("N"));
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "delegate", "planner", Guid.NewGuid().ToString("N"));
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Guid.NewGuid().ToString("N"));
        }

        var now = DateTimeOffset.UtcNow;
        var file = new CaseFile(
            new Case("case-split", "SO-1", SeededErp.Battery, "test", CaseStage.PrApproved, now, now,
                pr.PrNumber, null, null, null, [], [], null, null),
            [], []);
        return (erp, file);
    }

    private static Task<SplitVerification> VerifyAsync(
        InMemoryErpAdapter erp, CaseFile file, IReadOnlyList<string> candidates,
        string[] vendors, decimal[] quantities, string? note = null) =>
        SplitPlanVerifier.VerifyAsync(
            erp, new ComplianceEngine(), file, candidates, vendors, quantities, CountryMap(), note: note);

    [Fact]
    public async Task ValidPlanCarriesLinesSharesTotalsAndSaving()
    {
        // Best plan puts Meridian first: (5,600 + 1,120) / (10,000 + 1,200)
        // = 60.0% for the CinderVolt line with the Meridian line open.
        var (erp, file) = await SetupAsync();
        var result = await VerifyAsync(erp, file, ["V-A", "V-B"], ["V-B", "V-A"], [80, 1120]);

        Assert.Empty(result.Errors);
        var plan = result.Plan;
        Assert.NotNull(plan);
        Assert.Equal(2, plan.Lines.Count);
        Assert.Equal("V-B", plan.Lines[0].VendorId);
        Assert.Equal("Meridian Cellworks Inc", plan.Lines[0].VendorName);
        Assert.Equal(80m, plan.Lines[0].Quantity);
        Assert.All(plan.Lines[0].Verdicts, v => Assert.Equal(VerdictKind.Pass, v.Kind));
        Assert.Equal("V-A", plan.Lines[1].VendorId);
        Assert.Equal(1120m, plan.Lines[1].Quantity);
        Assert.Equal(13280m, plan.SplitTotal);
        Assert.Equal(14400m, plan.SingleVendorTotal);
        Assert.Equal(1120m, plan.Saving);
        Assert.Equal("USD", plan.Currency);
        Assert.Equal(0.6m, Math.Round(plan.FinalShares.Single(s => s.CountryCode == "CN").Share, 4));
        Assert.Equal(0.3643m, Math.Round(plan.FinalShares.Single(s => s.CountryCode == "MX").Share, 4));
        Assert.Equal(2, result.FullScreenings.Count);
        Assert.False(result.FullScreenings.Single(s => s.VendorId == "V-A").Passed);
    }

    [Fact]
    public async Task SuboptimalPlanBlockedNamesOnlyDollarsLeft()
    {
        // 800/400 costs $13,600 against the $13,280 best: blocked with the
        // $320 difference and no hint of the better composition.
        var (erp, file) = await SetupAsync();
        var result = await VerifyAsync(erp, file, ["V-A", "V-B"], ["V-A", "V-B"], [800, 400]);

        Assert.Null(result.Plan);
        var error = Assert.Single(result.Errors);
        Assert.Contains("left on the table", error, StringComparison.Ordinal);
        Assert.Contains("320", error, StringComparison.Ordinal);
        Assert.DoesNotContain("V-A", error, StringComparison.Ordinal);
        Assert.DoesNotContain("V-B", error, StringComparison.Ordinal);
        Assert.DoesNotContain("1120", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplainedPlanStandsWithNote()
    {
        var (erp, file) = await SetupAsync();
        var result = await VerifyAsync(
            erp, file, ["V-A", "V-B"], ["V-A", "V-B"], [800, 400], "keep dual supply for resilience");

        Assert.Empty(result.Errors);
        var plan = result.Plan;
        Assert.NotNull(plan);
        Assert.Equal(13600m, plan.SplitTotal);
        Assert.Equal(14400m, plan.SingleVendorTotal);
        Assert.Equal(800m, plan.Saving);
    }

    [Fact]
    public async Task BadSumIsRefused()
    {
        var (erp, file) = await SetupAsync();
        var result = await VerifyAsync(erp, file, ["V-A", "V-B"], ["V-A", "V-B"], [800, 300]);

        Assert.Null(result.Plan);
        Assert.Contains(result.Errors, e => e.Contains("adds up to"));
    }

    [Fact]
    public async Task BrokenCartonsAreRefused()
    {
        var (erp, file) = await SetupAsync();
        var result = await VerifyAsync(erp, file, ["V-A", "V-B"], ["V-A", "V-B"], [795, 405]);

        Assert.Null(result.Plan);
        Assert.Contains(result.Errors, e => e.Contains("whole cartons"));
    }

    [Fact]
    public async Task UnknownRepeatAndUnscreenedVendorsAreRefused()
    {
        var (erp, file) = await SetupAsync();
        var unknown = await VerifyAsync(erp, file, ["V-A", "V-B"], ["V-A", "V-Z"], [800, 400]);
        Assert.Null(unknown.Plan);
        Assert.Contains(unknown.Errors, e => e.Contains("unknown vendor"));

        var repeat = await VerifyAsync(erp, file, ["V-A", "V-B"], ["V-A", "V-A"], [800, 400]);
        Assert.Null(repeat.Plan);
        Assert.Contains(repeat.Errors, e => e.Contains("twice"));

        var unscreened = await VerifyAsync(erp, file, ["V-A", "V-B"], ["V-A", "V-D"], [800, 400]);
        Assert.Null(unscreened.Plan);
        Assert.Contains(unscreened.Errors, e => e.Contains("not screened"));
    }

    [Fact]
    public async Task SingleLineIsRefused()
    {
        var (erp, file) = await SetupAsync();
        var result = await VerifyAsync(erp, file, ["V-A", "V-B"], ["V-B"], [1200]);

        Assert.Null(result.Plan);
        Assert.Contains(result.Errors, e => e.Contains("at least two lines"));
    }

    [Fact]
    public async Task UnpricedLineIsRefused()
    {
        var (erp, file) = await SetupAsync();
        var result = await VerifyAsync(erp, file, ["V-A", "V-C"], ["V-A", "V-C"], [800, 400]);

        Assert.Null(result.Plan);
        Assert.Contains(result.Errors, e => e.Contains("names no price"));
    }

    [Fact]
    public async Task CheapestPassingFullQuantityRefusesTheSplit()
    {
        // (100 + 90) / (10,000 + 90) = 1.9%: CinderVolt passes for the
        // full 90, so proposing a split is pointless.
        var (erp, file) = await SetupAsync(prQty: 90, cnCells: 100);
        var result = await VerifyAsync(erp, file, ["V-A", "V-B"], ["V-A", "V-B"], [40, 50]);

        Assert.Null(result.Plan);
        Assert.Contains(result.Errors, e => e.Contains("passes for the full"));
    }

    [Fact]
    public async Task FailingLineNamesItsRule()
    {
        var (erp, file) = await SetupAsync();
        // 1,150 breaches in every order: proposed opens
        // (5,600 + 1,150) / (10,000 + 1,150) = 60.5%, and reversed closes
        // (5,600 + 1,150) / (10,000 + 50 + 1,150) = 60.3%.
        var result = await VerifyAsync(erp, file, ["V-A", "V-B"], ["V-A", "V-B"], [1150, 50]);

        Assert.Null(result.Plan);
        var error = Assert.Single(result.Errors);
        Assert.Contains("Line 1", error, StringComparison.Ordinal);
        Assert.Contains("CORP-02", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PassingOrderWinsWhenProposedFails()
    {
        // CinderVolt first breaches: (5,600 + 1,100) / (10,000 + 1,100)
        // = 60.4%. Meridian first passes: the CinderVolt line then closes
        // (5,600 + 1,100) / (10,000 + 100 + 1,100) = 59.8%. Creation
        // order is mechanics, so the plan stands reordered and the POs
        // create Meridian first.
        var (erp, file) = await SetupAsync();
        var result = await VerifyAsync(
            erp, file, ["V-A", "V-B"], ["V-A", "V-B"], [1100, 100],
            "keep CinderVolt share high within the cap");

        Assert.Empty(result.Errors);
        var plan = result.Plan;
        Assert.NotNull(plan);
        Assert.Equal("V-B", plan.Lines[0].VendorId);
        Assert.Equal(100m, plan.Lines[0].Quantity);
        Assert.Equal("V-A", plan.Lines[1].VendorId);
        Assert.Equal(1100m, plan.Lines[1].Quantity);
        Assert.Equal(13300m, plan.SplitTotal);
        Assert.Equal(14400m, plan.SingleVendorTotal);
        Assert.Equal(1100m, plan.Saving);
    }

    [Fact]
    public async Task SameCountryCombinationIsCoveredByAccumulation()
    {
        var (erp, file) = await SetupAsync();
        // Each line alone would pass, but the second sees the first as
        // open: (5,600 + 800 + 400) / (10,000 + 800 + 400) = 60.7%.
        var result = await VerifyAsync(erp, file, ["V-A", "V-B", "V-D"], ["V-D", "V-A"], [800, 400]);

        Assert.Null(result.Plan);
        var error = Assert.Single(result.Errors);
        Assert.Contains("Line 2", error, StringComparison.Ordinal);
        Assert.Contains("CORP-02", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSavingIsRefused()
    {
        var (erp, file) = await SetupAsync();
        // Both lines pass, but $15,200 beats no $14,400 single award.
        var result = await VerifyAsync(erp, file, ["V-A", "V-B", "V-D"], ["V-D", "V-B"], [800, 400]);

        Assert.Null(result.Plan);
        Assert.Contains(result.Errors, e => e.Contains("no saving"));
    }
}

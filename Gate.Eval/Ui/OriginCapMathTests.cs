// Origin-cap math-line tests: the one-line share calculation renders from
// rule parameters plus ERP history with no business values in code. Expected
// numbers are computed from the test's own inputs; the rule id comes from
// the compliance layer, never a literal.
using Gate.Compliance;
using Gate.Web.ViewModels;

namespace Gate.Eval.Ui;

public sealed class OriginCapMathTests
{
    private static Dictionary<string, string> Names() =>
        new(StringComparer.Ordinal)
        {
            ["CN"] = "China",
            ["MX"] = "Mexico",
            ["SE"] = "Sweden",
            ["US"] = "United States",
        };

    [Fact]
    public void BlockLineShowsTheShareFormulaAndTheBlock()
    {
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "CinderVolt Energy Ltd", "CN",
            5900m, 10000m, 1200m, 0.60m,
            OriginCapMath.HistoryHref("BAT-1"), "erp/record/pr/PR-1", Names());

        Assert.NotNull(line);
        Assert.Contains("China share", line.PlainText);
        Assert.Contains("(5,900+1,200)/(10,000+1,200)=63.4%", line.PlainText);
        Assert.Contains("over the 60%", line.PlainText);
        Assert.Contains(ComplianceEngine.CorpOriginCapId, line.PlainText);
        Assert.Contains("CinderVolt Energy Ltd is blocked", line.PlainText);
        Assert.True(line.PlainText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 25);
    }

    [Fact]
    public void PassLineShowsTheShareFormulaAndThePass()
    {
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "Meridian Cellworks Inc", "MX",
            3700m, 10000m, 1200m, 0.60m,
            OriginCapMath.HistoryHref("BAT-1"), "erp/record/pr/PR-1", Names());

        Assert.NotNull(line);
        Assert.Contains("Mexico share", line.PlainText);
        Assert.Contains("(3,700+1,200)/(10,000+1,200)=43.8%", line.PlainText);
        Assert.Contains("within the 60%", line.PlainText);
        Assert.Contains("Meridian Cellworks Inc passes", line.PlainText);
    }

    [Fact]
    public void BuildIsNullWithoutADenominator()
    {
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "Any Vendor", "CN",
            0m, 0m, 0m, 0.60m, "erp/purchase-history/BAT-1", "erp/record/pr/PR-1");

        Assert.Null(line);
    }

    [Fact]
    public void SegmentsConcatenateToThePlainTextWithHistoryAndQtyLinks()
    {
        var historyHref = OriginCapMath.HistoryHref("COIL-3");
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "Zed Parts AB", "SE",
            400m, 1000m, 200m, 0.60m, historyHref, "erp/record/pr/PR-3", Names());

        Assert.NotNull(line);
        Assert.Equal(line.PlainText, string.Concat(line.Segments.Select(s => s.Text)));
        Assert.Equal(historyHref, line.HistoryHref);
        Assert.Contains(line.Segments, s => s.Text == "400" && s.Href == historyHref);
        Assert.Contains(line.Segments, s => s.Text == "1,000" && s.Href == historyHref);
        Assert.Contains(line.Segments, s => s.Text == "200" && s.Href == "erp/record/pr/PR-3");
        Assert.Contains("(400+200)/(1,000+200)=50.0%", line.PlainText);
    }

    [Fact]
    public void BlockedVendorParsesFromTheScreeningSummary()
    {
        Assert.Equal("V-A", OriginCapMath.TryParseBlockedVendor(
            $"Vendor V-A blocked: {OriginCapMath.OriginCapRuleId}: share exceeded."));
        Assert.Equal("V-A", OriginCapMath.TryParseBlockedVendor(
            $"Vendor V-A rejected for the award: {OriginCapMath.OriginCapRuleId}: share exceeded."));
        Assert.Null(OriginCapMath.TryParseBlockedVendor("Something else happened."));
        Assert.Null(OriginCapMath.TryParseBlockedVendor("Vendor blocked: no id."));
    }

    [Fact]
    public void CountryNameComesFromTheMapAndFallsBackToTheCode()
    {
        Assert.Equal("China", OriginCapMath.CountryName(Names(), "CN"));
        Assert.Equal("OTHER", OriginCapMath.CountryName(Names(), "OTHER"));
        Assert.Equal("CN", OriginCapMath.CountryName(null, "CN"));
    }
}

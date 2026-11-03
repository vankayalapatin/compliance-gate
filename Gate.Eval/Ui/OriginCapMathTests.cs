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
    public void BlockLineReadsAsASentenceWithTheBlock()
    {
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "CinderVolt Energy Ltd", "CN",
            5900m, 10000m, 1200m, 0.60m,
            OriginCapMath.HistoryHref("BAT-1"), "erp/record/pr/PR-1", "battery cells", Names());

        Assert.NotNull(line);
        Assert.Contains("CinderVolt Energy Ltd (China) is blocked.", line.PlainText);
        Assert.Contains("we bought 10,000 battery cells, and 5,900 came from China", line.PlainText);
        Assert.Contains("This order adds 1,200 more", line.PlainText);
        Assert.Contains("China's share would be 7,100 of 11,200 battery cells: 63.4%", line.PlainText);
        Assert.Contains($"{ComplianceEngine.CorpOriginCapId} caps any one country at 60%", line.PlainText);
    }

    [Fact]
    public void PassLineReadsAsTheSameSentenceWithThePass()
    {
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "Meridian Cellworks Inc", "MX",
            3700m, 10000m, 1200m, 0.60m,
            OriginCapMath.HistoryHref("BAT-1"), "erp/record/pr/PR-1", "battery cells", Names());

        Assert.NotNull(line);
        Assert.Contains("Meridian Cellworks Inc (Mexico) passes.", line.PlainText);
        Assert.Contains("Mexico would be 4,900 of 11,200 battery cells: 43.8%, under the 60% cap", line.PlainText);
    }

    [Fact]
    public void OpenOrdersGetTheirOwnSentenceWhenPartOfTheMath()
    {
        // (5,900 + 200 + 1,200) / (10,000 + 400 + 1,200) = 62.9%: blocked,
        // with the open orders named in their own sentence.
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "CinderVolt Energy Ltd", "CN",
            5900m, 10000m, 1200m, 0.60m,
            OriginCapMath.HistoryHref("BAT-1"), "erp/record/pr/PR-1", "battery cells", Names(),
            false, 200m, 400m);

        Assert.NotNull(line);
        Assert.Contains("CinderVolt Energy Ltd (China) is blocked.", line.PlainText);
        Assert.Contains(
            "Another 400 battery cells are on open orders, 200 of them from China.",
            line.PlainText);
        Assert.Contains("China's share would be 7,300 of 11,600 battery cells: 62.9%", line.PlainText);
    }

    [Fact]
    public void NoOpenSentenceWhenNothingIsOpen()
    {
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "Meridian Cellworks Inc", "MX",
            3700m, 10000m, 1200m, 0.60m,
            OriginCapMath.HistoryHref("BAT-1"), "erp/record/pr/PR-1", "battery cells", Names());

        Assert.NotNull(line);
        Assert.DoesNotContain("open orders", line.PlainText);
    }

    [Fact]
    public void ExcusedLineNamesTheExceptionCover()
    {
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "CinderVolt Energy Ltd", "CN",
            5900m, 10000m, 1200m, 0.60m,
            OriginCapMath.HistoryHref("BAT-1"), "erp/record/pr/PR-1", "battery cells", Names(),
            excused: true);

        Assert.NotNull(line);
        Assert.Contains("is excused under exception", line.PlainText);
        Assert.Contains("China's share would be 7,100 of 11,200 battery cells: 63.4%", line.PlainText);
        Assert.Contains("a logged exception covers this award", line.PlainText);
    }

    [Fact]
    public void BuildIsNullWithoutADenominator()
    {
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "Any Vendor", "CN",
            0m, 0m, 0m, 0.60m, "erp/purchase-history/BAT-1", "erp/record/pr/PR-1", "units");

        Assert.Null(line);
    }

    [Fact]
    public void SegmentsConcatenateToThePlainTextWithHistoryAndQtyLinks()
    {
        var historyHref = OriginCapMath.HistoryHref("COIL-3");
        var line = OriginCapMath.Build(
            OriginCapMath.OriginCapRuleId, "Zed Parts AB", "SE",
            400m, 1000m, 200m, 0.60m, historyHref, "erp/record/pr/PR-3", "copper coils", Names());

        Assert.NotNull(line);
        Assert.Equal(line.PlainText, string.Concat(line.Segments.Select(s => s.Text)));
        Assert.Equal(historyHref, line.HistoryHref);
        Assert.Contains(line.Segments, s => s.Text == "400" && s.Href == historyHref);
        Assert.Contains(line.Segments, s => s.Text == "1,000" && s.Href == historyHref);
        Assert.Contains(line.Segments, s => s.Text == "200" && s.Href == "erp/record/pr/PR-3");
        Assert.Contains("Sweden would be 600 of 1,200 copper coils: 50.0%, under the 60% cap", line.PlainText);
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

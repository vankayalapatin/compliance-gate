// Origin-cap math line: the one-line country-share calculation shown on
// the case page and in the activity pane. Every number comes from ERP
// history plus the PR quantity (live for undecided orders, the stored
// award-time figures for decided ones), and the cap from the rule's own
// pack config — no amounts, caps, or names in code. History figures link
// to the purchase history view, which reads the same rows, so the page
// totals always equal the numbers in the line.
using System.Globalization;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Web.ViewModels;

public sealed record MathSegment(string Text, string? Href);

public sealed record MathLineVm(
    string PlainText,
    IReadOnlyList<MathSegment> Segments,
    string HistoryHref);

public static class OriginCapMath
{
    public static string HistoryHref(string itemId) =>
        $"erp/purchase-history/{Uri.EscapeDataString(itemId)}";

    /// <summary>Vendor id from a screening summary ("Vendor {id} passed
    /// screening.", "Vendor {id} failed screening: ..."), or null when
    /// the summary has no such shape.</summary>
    public static string? TryParseScreenedVendor(string summary)
    {
        const string prefix = "Vendor ";
        if (!summary.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var infix in new[] { " passed screening.", " failed screening:" })
        {
            var end = summary.IndexOf(infix, prefix.Length, StringComparison.Ordinal);
            if (end > prefix.Length)
            {
                return summary.Substring(prefix.Length, end - prefix.Length);
            }
        }

        return null;
    }

    /// <summary>Vendor id from an award-rejection summary ("Vendor {id}
    /// rejected for the award: ...", legacy "Vendor {id} blocked: ..."),
    /// or null when the summary has no such shape.</summary>
    public static string? TryParseBlockedVendor(string summary)
    {
        const string prefix = "Vendor ";
        if (!summary.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var infix in new[] { " rejected for the award:", " blocked:" })
        {
            var end = summary.IndexOf(infix, prefix.Length, StringComparison.Ordinal);
            if (end > prefix.Length)
            {
                return summary.Substring(prefix.Length, end - prefix.Length);
            }
        }

        return null;
    }

    /// <summary>Display name for a country code from the company-owned map,
    /// falling back to the code itself for unlisted values.</summary>
    public static string CountryName(IReadOnlyDictionary<string, string>? names, string code) =>
        names is not null && names.TryGetValue(code, out var name) ? name : code;

    public static string Cells(decimal cells) =>
        cells.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Share-afterward sentence, mirroring the engine's own
    /// formula: (country cells + award) / (total cells + award) against
    /// the cap. Country names come from the company-owned map, the count
    /// noun from the item description. The page renders a separate "See
    /// purchase history" link from HistoryHref; the sentence itself wraps
    /// normally. Null when there is no denominator to divide by.</summary>
    public static MathLineVm? Build(
        string ruleId,
        string vendorName,
        string countryCode,
        decimal countryCells,
        decimal totalCells,
        decimal awardQty,
        decimal cap,
        string historyHref,
        string qtyHref,
        string unitNoun,
        IReadOnlyDictionary<string, string>? countryNames = null,
        bool excused = false,
        decimal countryOpenCells = 0,
        decimal totalOpenCells = 0)
    {
        var line = ScreeningSentence.Build(
            ruleId, vendorName, CountryName(countryNames, countryCode),
            countryCells, totalCells, countryOpenCells, totalOpenCells,
            awardQty, cap, unitNoun, excused);
        if (line is null)
        {
            return null;
        }

        var segments = line.Segments.Select(s => new MathSegment(s.Text, s.Link switch
        {
            AwardMathLink.History => historyHref,
            AwardMathLink.Quantity => qtyHref,
            _ => null,
        })).ToList();
        return new MathLineVm(line.PlainText, segments, historyHref);
    }

    /// <summary>Stored math line back into view segments: the sentence and
    /// figures render exactly as decided; only the hrefs resolve live.</summary>
    public static MathLineVm FromStored(AwardMathLine stored, string historyHref, string qtyHref)
    {
        var segments = stored.Segments.Select(s => new MathSegment(s.Text, s.Link switch
        {
            AwardMathLink.History => historyHref,
            AwardMathLink.Quantity => qtyHref,
            _ => null,
        })).ToList();
        return new MathLineVm(stored.PlainText, segments, historyHref);
    }

    /// <summary>The origin-cap rule id as the compliance layer defines it.
    /// The UI keys math lines off this identity, never a literal.</summary>
    public static string OriginCapRuleId => ComplianceEngine.CorpOriginCapId;
}

// Origin-cap math line: the one-line country-share calculation shown on
// the case page and in the activity pane when a vendor is blocked under
// the origin-cap rule. Every number comes from ERP history plus the PR
// quantity at render time, and the cap from the rule's own pack config —
// no amounts, caps, or names in code. History figures link to the purchase
// history view, which reads the same rows, so the page totals always equal
// the numbers in the line.
using System.Globalization;
using Gate.Compliance;

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

    /// <summary>One-line share-afterward calculation, mirroring the engine's
    /// own formula: (country cells + award) / (total cells + award) against
    /// the cap. Country names come from the company-owned map. Null when
    /// there is no denominator to divide by.</summary>
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
        IReadOnlyDictionary<string, string>? countryNames = null)
    {
        if (totalCells + awardQty <= 0)
        {
            return null;
        }

        var share = (countryCells + awardQty) / (totalCells + awardQty);
        var over = share > cap;
        var country = CountryName(countryNames, countryCode);
        // Manual percent shapes (no space, invariant): identical on every
        // machine and matching the engine's own verdict figures.
        var shareText = (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        var capText = (cap * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        var cc = Cells(countryCells);
        var qty = Cells(awardQty);
        var total = Cells(totalCells);
        var relation = over ? "over" : "within";
        var outcome = over ? "is blocked" : "passes";

        var segments = new List<MathSegment>
        {
            new($"{country} share (", null),
            new(cc, historyHref),
            new("+", null),
            new(qty, qtyHref),
            new(")/(", null),
            new(total, historyHref),
            new("+", null),
            new(qty, qtyHref),
            new($")={shareText}, {relation} the {capText} {ruleId} cap, so {vendorName} {outcome}.", null),
        };
        return new MathLineVm(string.Concat(segments.Select(s => s.Text)), segments, historyHref);
    }

    /// <summary>The origin-cap rule id as the compliance layer defines it.
    /// The UI keys math lines off this identity, never a literal.</summary>
    public static string OriginCapRuleId => ComplianceEngine.CorpOriginCapId;
}

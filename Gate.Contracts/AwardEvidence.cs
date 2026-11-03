// Award evidence: the screening verdicts, sentences, and figures as they
// stood when the award was made. The agent stores a snapshot with the
// award; decided orders display the stored copy, and only orders not yet
// decided re-run screening live. No business values here: every vendor,
// figure, and sentence arrives at award time; only the shapes are fixed.
using System.Globalization;

namespace Gate.Contracts;

/// <summary>Link kind on one math-sentence segment. The UI resolves History
/// to the purchase-history page and Quantity to the PR record.</summary>
public enum AwardMathLink
{
    None,
    History,
    Quantity,
}

public sealed record AwardMathSegment(string Text, AwardMathLink Link);

public sealed record AwardMathLine(
    string PlainText,
    IReadOnlyList<AwardMathSegment> Segments);

/// <summary>One screened candidate as decided: vendor facts, unit price,
// result, the rule plus one reason sentence, the full verdicts, and the
// origin-cap sentence when one applies.</summary>
public sealed record AwardScreeningRow(
    string VendorId,
    string VendorName,
    string CountryCode,
    string Country,
    decimal? UnitPrice,
    string? Currency,
    bool Passed,
    string Result,
    string Reason,
    bool Awarded,
    IReadOnlyList<Verdict> Verdicts,
    AwardMathLine? Math);

/// <summary>Screening evidence stored with the award. Rows never change
/// after the award; only the winner marker moves on re-award.</summary>
public sealed record AwardScreeningSnapshot(
    DateTimeOffset TakenAt,
    string PrNumber,
    string ItemId,
    decimal AwardQty,
    string? AwardedVendorId,
    IReadOnlyList<AwardScreeningRow> Rows)
{
    /// <summary>Same evidence, new winner marker after a re-award.</summary>
    public AwardScreeningSnapshot WithWinner(string winner) =>
        this with
        {
            AwardedVendorId = winner,
            Rows = Rows.Select(r => r with { Awarded = string.Equals(r.VendorId, winner, StringComparison.Ordinal) }).ToList(),
        };
}

/// <summary>Shared screening row words: the award and the live re-run map
/// verdicts to result and reason through this one function, so stored and
/// live rows can never diverge in shape. The CORP rule id arrives as a
/// parameter (each layer owns its own constant).</summary>
public static class ScreeningRowWords
{
    public static string ResultFor(bool passed, IReadOnlyList<Verdict> verdicts)
    {
        if (passed)
        {
            return "Passed";
        }

        var block = verdicts.FirstOrDefault(v => v.Kind == VerdictKind.Block);
        if (block is not null)
        {
            return $"Blocked by {block.RuleId}";
        }

        var hold = verdicts.FirstOrDefault(v => v.Kind is VerdictKind.Hold or VerdictKind.HumanReview);
        return hold is not null ? $"Held for review by {hold.RuleId}" : "Not passed";
    }

    public static string ReasonFor(IReadOnlyList<Verdict> verdicts, string corpRuleId)
    {
        var first = verdicts.FirstOrDefault(v => v.Kind == VerdictKind.Block)
            ?? verdicts.FirstOrDefault(v => v.Kind is VerdictKind.Hold or VerdictKind.HumanReview);
        if (first is not null)
        {
            return $"{first.RuleId}: {first.Reason}";
        }

        var corp = verdicts.FirstOrDefault(v =>
            string.Equals(v.RuleId, corpRuleId, StringComparison.Ordinal));
        if (corp is not null)
        {
            return $"{corp.RuleId}: {corp.Reason}";
        }

        return $"Passed all {verdicts.Count} checks.";
    }
}

/// <summary>Origin-cap share sentence as pure logic: (country history +
/// country open + award) / (total history + total open + award) against
/// the cap, in words. The award stores the rendered sentence; the UI
/// renders live sentences through the same function. Open orders get
/// their own sentence only when they are part of the math; their figures
/// carry no link, since no page lists them yet. Other segments carry
/// link kinds; the UI resolves them to hrefs.</summary>
public static class ScreeningSentence
{
    public static AwardMathLine? Build(
        string ruleId,
        string vendorName,
        string country,
        decimal countryCells,
        decimal totalCells,
        decimal countryOpenCells,
        decimal totalOpenCells,
        decimal awardQty,
        decimal cap,
        string unitNoun,
        bool excused = false)
    {
        if (totalCells + totalOpenCells + awardQty <= 0)
        {
            return null;
        }

        var share = (countryCells + countryOpenCells + awardQty)
            / (totalCells + totalOpenCells + awardQty);
        var over = share > cap;
        // Manual percent shapes (no space, invariant): identical on every
        // machine and matching the engine's own verdict figures.
        var shareText = (share * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        var capText = (cap * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        var cc = countryCells.ToString("N0", CultureInfo.InvariantCulture);
        var qty = awardQty.ToString("N0", CultureInfo.InvariantCulture);
        var total = totalCells.ToString("N0", CultureInfo.InvariantCulture);
        var afterCountry = (countryCells + countryOpenCells + awardQty)
            .ToString("N0", CultureInfo.InvariantCulture);
        var afterTotal = (totalCells + totalOpenCells + awardQty)
            .ToString("N0", CultureInfo.InvariantCulture);
        var outcome = excused ? "is excused under exception" : over ? "is blocked" : "passes";
        var ending = excused
            ? $"so {country}'s share would be {afterCountry} of {afterTotal} {unitNoun}: {shareText}. " +
              $"{ruleId} caps any one country at {capText}; a logged exception covers this award."
            : over
                ? $"so {country}'s share would be {afterCountry} of {afterTotal} {unitNoun}: {shareText}. " +
                  $"{ruleId} caps any one country at {capText}."
                : $"so {country} would be {afterCountry} of {afterTotal} {unitNoun}: {shareText}, " +
                  $"under the {capText} cap.";

        var segments = new List<AwardMathSegment>
        {
            new($"{vendorName} ({country}) {outcome}. In the last 12 months we bought ", AwardMathLink.None),
            new(total, AwardMathLink.History),
            new($" {unitNoun}, and ", AwardMathLink.None),
            new(cc, AwardMathLink.History),
            new($" came from {country}. ", AwardMathLink.None),
        };
        if (totalOpenCells > 0)
        {
            var open = totalOpenCells.ToString("N0", CultureInfo.InvariantCulture);
            segments.Add(new("Another ", AwardMathLink.None));
            segments.Add(new(open, AwardMathLink.None));
            segments.Add(new($" {unitNoun} are on open orders", AwardMathLink.None));
            if (countryOpenCells > 0)
            {
                var countryOpen = countryOpenCells.ToString("N0", CultureInfo.InvariantCulture);
                segments.Add(new(", ", AwardMathLink.None));
                segments.Add(new(countryOpen, AwardMathLink.None));
                segments.Add(new($" of them from {country}", AwardMathLink.None));
            }

            segments.Add(new(". ", AwardMathLink.None));
        }

        segments.Add(new("This order adds ", AwardMathLink.None));
        segments.Add(new(qty, AwardMathLink.Quantity));
        segments.Add(new($" more, {ending}", AwardMathLink.None));
        return new AwardMathLine(string.Concat(segments.Select(s => s.Text)), segments);
    }
}

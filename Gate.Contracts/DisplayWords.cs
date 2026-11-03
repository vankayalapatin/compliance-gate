// Shared display words for prose the lower layers write (agent timeline
// events, compliance verdict reasons): money with two decimals plus
// currency ($ for USD), quantities with thousands separators, honest
// plurals. The web layer formats screens from these same shapes, so a
// sentence reads identically in the timeline, the feed, and the ledger.
// No business values here: every amount, name, and limit arrives at
// runtime; only the shapes are fixed.
using System.Globalization;

namespace Gate.Contracts;

public static class DisplayWords
{
    public static string Money(decimal amount, string? currency) =>
        currency is null
            ? $"{Amount(amount)} currency missing"
            : string.Equals(currency, "USD", StringComparison.Ordinal)
                ? $"${Amount(amount)}"
                : $"{Amount(amount)} {currency}";

    public static string Amount(decimal amount) =>
        amount.ToString("N2", CultureInfo.InvariantCulture);

    public static string Qty(decimal qty) =>
        qty.ToString("#,0.##", CultureInfo.InvariantCulture);

    public static string Count(int count) =>
        count.ToString("N0", CultureInfo.InvariantCulture);

    public static string Plural(int count, string one, string? many = null) =>
        count == 1 ? $"1 {one}" : $"{Count(count)} {many ?? one + "s"}";

    /// <summary>Count noun from a company item description ("Battery cell"
    /// becomes "battery cells"). Falls back to "units" when undescribed.</summary>
    public static string CountNoun(string? description)
    {
        var lower = (description ?? string.Empty).Trim().ToLowerInvariant();
        if (lower.Length == 0)
        {
            return "units";
        }

        return lower.EndsWith('s') ? lower : lower + "s";
    }

    /// <summary>Local-time stamp for prose. Never a trailing Z.</summary>
    public static string When(DateTimeOffset at) =>
        at.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);

    public static string Verdict(VerdictKind kind) =>
        kind switch
        {
            VerdictKind.Pass => "Pass",
            VerdictKind.Block => "Block",
            VerdictKind.Hold => "Hold",
            VerdictKind.HumanReview => "Human review",
            _ => "Unknown",
        };

    public static string Conflict(RuleConflictKind kind) =>
        kind switch
        {
            RuleConflictKind.Tightens => "Tightens",
            RuleConflictKind.Loosens => "Loosens",
            RuleConflictKind.Contradicts => "Contradicts",
            RuleConflictKind.Subsumes => "Subsumes",
            _ => "Unknown",
        };
}

// Number-sequence formatting shared by both ERP adapters, the seeder, and
// the loader. A sequence mints "{prefix}-{number}" with the number
// zero-padded to the configured width (the padded demo format is width 4:
// PR-1001, PO-1001, and so on). Widths and starts are scenario data; the
// code defaults below are fallback logic only, used when no scenario rule
// and no stored format exist (unit tests without a loader run).
using System.Globalization;

namespace Gate.Erp;

internal static class NumberSequences
{
    internal const string DefaultFormat = "{prefix}-{number}";
    internal const int DefaultPadding = 4;
    internal const long DefaultStart = 1;

    internal static string FormatNumber(string prefix, string? format, int padding, long number)
    {
        var digits = padding > 0
            ? number.ToString(CultureInfo.InvariantCulture).PadLeft(padding, '0')
            : number.ToString(CultureInfo.InvariantCulture);
        var template = string.IsNullOrWhiteSpace(format) ? DefaultFormat : format;
        return template
            .Replace("{prefix}", prefix, StringComparison.Ordinal)
            .Replace("{number}", digits, StringComparison.Ordinal);
    }

    /// <summary>Parses a default-shaped number ("PREFIX-0042" or "PREFIX-42")
    /// back to its counter. Custom templates mint fine but are invisible to
    /// the max-seeded scan, which only understands the default shape.</summary>
    internal static bool TryParseNumber(string prefix, string number, out long value)
    {
        value = 0;
        var head = prefix + "-";
        if (!number.StartsWith(head, StringComparison.Ordinal)) return false;
        var tail = number.Substring(head.Length);
        if (tail.Length == 0) return false;
        foreach (var ch in tail)
        {
            if (!char.IsDigit(ch)) return false;
        }

        return long.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

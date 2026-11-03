// Simulated carton problems for dock-view rebuilds. The supplier ASN panel
// and the dock tab offer the same picker (none plus the three wire kinds);
// the client validates before posting and the endpoints validate again, so
// an unknown kind is refused with its reason on both sides, never silently
// dropped. Wire names are the canonical kind names.
namespace Gate.Web.ViewModels;

public sealed record DockProblemForm(string CartonId, string Kind);

public static class DockProblemKinds
{
    public const string MissingMark = "MissingMark";
    public const string Crushed = "Crushed";
    public const string Hidden = "Hidden";

    public static readonly IReadOnlyList<(string Value, string Label)> PickerOptions =
    [
        (string.Empty, "none"),
        (MissingMark, "missing mark"),
        (Crushed, "crushed"),
        (Hidden, "hidden"),
    ];

    public static bool IsNone(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? value, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, MissingMark, StringComparison.OrdinalIgnoreCase))
        {
            canonical = MissingMark;
            return true;
        }

        if (string.Equals(trimmed, Crushed, StringComparison.OrdinalIgnoreCase))
        {
            canonical = Crushed;
            return true;
        }

        if (string.Equals(trimmed, Hidden, StringComparison.OrdinalIgnoreCase))
        {
            canonical = Hidden;
            return true;
        }

        return false;
    }

    public static string DescribeOptions() => "missing mark, crushed, or hidden";
}

// Pack version arithmetic shared by the loader and the publisher: versions
// are vN.M, comparisons are numeric, bumps increment the minor version.
// One rule in one place so publish and load can never disagree.
namespace Gate.Compliance;

public static class PackVersions
{
    /// <summary>Parses vN.M into (N, M). Anything else throws loudly.</summary>
    public static (int Major, int Minor) Parse(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var body = version.StartsWith('v') ? version[1..] : null;
        var parts = body?.Split('.');
        if (parts?.Length == 2
            && int.TryParse(parts[0], out var major) && major >= 0
            && int.TryParse(parts[1], out var minor) && minor >= 0)
        {
            return (major, minor);
        }

        throw new InvalidOperationException(
            $"Pack version '{version}' is not of the form vN.M (e.g. v0.2); refusing to guess.");
    }

    /// <summary>Compares two versions numerically (v0.10 beats v0.9).</summary>
    public static int Compare(string left, string right)
    {
        var (lmajor, lminor) = Parse(left);
        var (rmajor, rminor) = Parse(right);
        return lmajor != rmajor ? lmajor.CompareTo(rmajor) : lminor.CompareTo(rminor);
    }

    /// <summary>Bumps vN.M to vN.(M+1). Rollback also bumps: it publishes a
    /// new version restoring prior parameters, never rewrites history.</summary>
    public static string Next(string version)
    {
        var (major, minor) = Parse(version);
        return $"v{major}.{minor + 1}";
    }
}

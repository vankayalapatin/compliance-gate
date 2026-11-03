// Pack version arithmetic shared by the loader and the publisher: live
// versions are v1, v2, ... vN; comparisons are numeric, bumps increment
// the number. Parsing still accepts legacy vN.M files so old retained
// packs keep loading, but nothing mints them anymore. One rule in one
// place so publish and load can never disagree.
namespace Gate.Compliance;

public static class PackVersions
{
    /// <summary>Parses vN (or legacy vN.M) into (N, M). Anything else
    /// throws loudly.</summary>
    public static (int Major, int Minor) Parse(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var body = version.StartsWith('v') ? version[1..] : null;
        var parts = body?.Split('.');
        if (parts?.Length == 1
            && int.TryParse(parts[0], out var only) && only >= 0)
        {
            return (only, 0);
        }

        if (parts?.Length == 2
            && int.TryParse(parts[0], out var major) && major >= 0
            && int.TryParse(parts[1], out var minor) && minor >= 0)
        {
            return (major, minor);
        }

        throw new InvalidOperationException(
            $"Pack version '{version}' is not of the form vN (e.g. v2); refusing to guess.");
    }

    /// <summary>Compares two versions numerically (v10 beats v9, v2 beats
    /// v1).</summary>
    public static int Compare(string left, string right)
    {
        var (lmajor, lminor) = Parse(left);
        var (rmajor, rminor) = Parse(right);
        return lmajor != rmajor ? lmajor.CompareTo(rmajor) : lminor.CompareTo(rminor);
    }

    /// <summary>Bumps vN to v(N+1) (a legacy vN.M bumps to v(N+1) too,
    /// leaving the dotted scheme behind). Rollback also bumps: it publishes
    /// a new version restoring prior parameters, never rewrites history.</summary>
    public static string Next(string version)
    {
        var (major, _) = Parse(version);
        return $"v{major + 1}";
    }
}

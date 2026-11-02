// Dock photo access for Decisions evidence and Records receipts. Images
// are served from disk and reads come from the vision read cache only;
// this service never makes a live call. Evidence refs are opaque strings
// from the agent lane: a ref that names a file in the dock directory
// (with or without the .jpg suffix) resolves to that photo.
using Gate.Contracts;

namespace Gate.Web.Services;

public sealed class DockPhotoService
{
    private readonly string _imageDir;
    private readonly string _visionCacheDir;
    private readonly VisionReadCache _reads;

    public DockPhotoService(string imageDir, string visionCacheDir)
    {
        _imageDir = imageDir;
        _visionCacheDir = visionCacheDir;
        _reads = new VisionReadCache(visionCacheDir);
    }

    public string ImageDir => _imageDir;

    public static string ResolveImageDir() =>
        Environment.GetEnvironmentVariable("GATE_DOCK_IMAGES")
        ?? FindRepoDir("assets", "dock");

    public static string ResolveVisionCacheDir() =>
        Environment.GetEnvironmentVariable("GATE_VISION_CACHE")
        ?? FindRepoDir("assets", "read-cache");

    private static string FindRepoDir(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine([dir.FullName, .. parts]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Dock asset directory not found: {Path.Combine(parts)} (set GATE_DOCK_IMAGES / GATE_VISION_CACHE).");
    }

    // Dock-camera counter rules for the shipped image set: *-base.jpg files
    // are the unlabeled working originals and are never offered; IMG-04 is
    // the carton close-up and is reshot-only; every other labeled photo is
    // a pallet view for the first dock check. These are presentation facts
    // about the shipped photo set, kept in this one place.
    public static bool IsBaseImage(string fileName) =>
        fileName.EndsWith("-base.jpg", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith("-base.jpeg", StringComparison.OrdinalIgnoreCase);

    public static bool IsCloseUp(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return string.Equals(name, "IMG-04", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-IMG-04", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Counter images for one dock-camera mode: pallet views for a
    /// first check, close-ups for a reshot. Base originals never appear in
    /// either mode. Sorted for a stable counter.</summary>
    public static IReadOnlyList<string> OfferedCounterImages(IEnumerable<string> files, bool reshot) =>
        files
            .Where(f => !IsBaseImage(f) && IsCloseUp(f) == reshot)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Resolves an evidence ref to a dock image file name, or null
    /// when the ref names no photo. Never throws for unknown refs.</summary>
    public string? ResolveImageFileName(string? evidenceRef)
    {
        if (string.IsNullOrWhiteSpace(evidenceRef) || !Directory.Exists(_imageDir))
        {
            return null;
        }

        var trimmed = evidenceRef.Trim();
        foreach (var candidate in new[] { trimmed, trimmed + ".jpg" })
        {
            if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || candidate.Contains(Path.DirectorySeparatorChar)
                || candidate.Contains(Path.AltDirectorySeparatorChar))
            {
                continue;
            }

            var full = Path.Combine(_imageDir, candidate);
            if (File.Exists(full)
                && full.StartsWith(_imageDir, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<byte[]?> ReadImageBytesAsync(string fileName, CancellationToken ct = default)
    {
        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName.Contains(Path.DirectorySeparatorChar)
            || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        var full = Path.Combine(_imageDir, fileName);
        if (!full.StartsWith(_imageDir, StringComparison.Ordinal) || !File.Exists(full))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
    }

    /// <summary>Cached carton read for an image, or null when no cached read
    /// exists. A tampered or stale entry throws loudly (never reused).</summary>
    public string? TryCachedRead(string fileName)
    {
        var full = Path.Combine(_imageDir, fileName);
        if (!File.Exists(full))
        {
            return null;
        }

        var hash = VisionReadCache.Sha256Hex(File.ReadAllBytes(full));
        return _reads.TryRead(hash, VisionReadCache.CartonReadV1)?.Text;
    }

    public int CachedReadCount()
    {
        if (!Directory.Exists(_visionCacheDir))
        {
            return 0;
        }

        return Directory.GetFiles(_visionCacheDir, "*.json").Length;
    }
}

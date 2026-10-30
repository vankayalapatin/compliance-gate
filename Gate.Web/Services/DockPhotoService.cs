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

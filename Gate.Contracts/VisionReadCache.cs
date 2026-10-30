// Offline vision read cache: one entry per image read, keyed by image sha256
// plus prompt version, carrying model id, timestamp, and result hash. Demo
// and replay read cache only. A hash mismatch invalidates loudly instead of
// reusing a stale read.
using System.Text.Json;

namespace Gate.Contracts;

public sealed record VisionReadEntry(
    string ImageSha256,
    string PromptVersion,
    string Model,
    string Text,
    string ResultHash,
    DateTimeOffset ReadAtUtc);

public sealed class VisionReadCache
{
    public const string CartonReadV1 = "carton-read-v1";
    public const string InvoiceReadV1 = "invoice-read-v1";

    private readonly string _dir;

    public VisionReadCache(string cacheDir)
    {
        _dir = cacheDir;
    }

    public static string EntryFileName(string imageSha256, string promptVersion) =>
        imageSha256 + "." + promptVersion + ".json";

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    public void Write(string imageSha256, string promptVersion, string model, string text)
    {
        Directory.CreateDirectory(_dir);
        var entry = new VisionReadEntry(
            imageSha256, promptVersion, model, text,
            Sha256Hex(System.Text.Encoding.UTF8.GetBytes(text)),
            DateTimeOffset.UtcNow);
        File.WriteAllText(
            Path.Combine(_dir, EntryFileName(imageSha256, promptVersion)),
            JsonSerializer.Serialize(entry));
    }

    public VisionReadEntry? TryRead(string imageSha256, string promptVersion)
    {
        var file = Path.Combine(_dir, EntryFileName(imageSha256, promptVersion));
        if (!File.Exists(file))
        {
            return null;
        }

        var entry = JsonSerializer.Deserialize<VisionReadEntry>(File.ReadAllText(file));
        if (entry is null
            || !string.Equals(entry.ImageSha256, imageSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(entry.PromptVersion, promptVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Vision cache hash mismatch (stale read refused): {file}");
        }

        var expected = Sha256Hex(System.Text.Encoding.UTF8.GetBytes(entry.Text));
        if (!string.Equals(entry.ResultHash, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Vision cache result mismatch (tampered read refused): {file}");
        }

        return entry;
    }
}

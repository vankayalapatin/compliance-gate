// Restricted-party screening against a compliance-side local snapshot.
// The model never fetches lists at runtime; C# checks the bundled snapshot.
// Snapshot data lives in Screening/*.json (embedded), never in ERP tables.
using System.Reflection;
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Compliance;

public sealed record ScreeningEntry(
    string List,
    string Program,
    string Name,
    IReadOnlyList<string> Aliases,
    string? Notes);

public sealed record ScreeningSnapshot(
    string SnapshotId,
    string AsOf,
    IReadOnlyList<ScreeningEntry> Entries);

public static class ScreeningSnapshotLoader
{
    private const string SnapshotSuffix = "Screening.snapshot.json";

    public static ScreeningSnapshot LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(SnapshotSuffix, StringComparison.Ordinal));
        if (resource is null)
        {
            throw new InvalidOperationException(
                $"Embedded screening snapshot ending in '{SnapshotSuffix}' was not found in {assembly.GetName().Name}.");
        }

        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            throw new InvalidOperationException($"Embedded resource '{resource}' could not be opened.");
        }

        ScreeningSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ScreeningSnapshot>(stream, SnapshotJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Embedded screening snapshot '{resource}' is not valid JSON.", ex);
        }

        if (snapshot is null || snapshot.Entries is null)
        {
            throw new InvalidOperationException($"Embedded screening snapshot '{resource}' deserialized to null.");
        }

        return snapshot;
    }
}

internal static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}

public static class ScreeningMatcher
{
    public static ScreeningEntry? Match(VendorDto vendor, IReadOnlyList<ScreeningEntry> entries)
    {
        var name = Normalize(vendor.Name);
        foreach (var entry in entries)
        {
            if (Normalize(entry.Name) == name)
            {
                return entry;
            }

            foreach (var alias in entry.Aliases ?? [])
            {
                if (Normalize(alias) == name)
                {
                    return entry;
                }
            }
        }

        return null;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}

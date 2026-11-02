// Per-case vision usage: one JSON object per line in
// <CaseId>.vision.jsonl in the case dir. Every transcribed photo appends
// its image id, prompt version, model, reported tokens, and cache origin,
// so the scorecard can show the case's vision reads. Append-only; replays
// add lines, never rewrite them. Recording only; no read or verdict logic
// touches this file.
using System.Text.Json;

namespace Gate.Agent;

public sealed record VisionPhotoUsage(
    string ImageId,
    string PromptVersion,
    string? Model,
    int? InputTokens,
    int? OutputTokens,
    bool FromCache,
    DateTimeOffset At);

public static class CaseVisionUsage
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static string VisionPath(string cacheDir, string caseId) =>
        Path.Combine(cacheDir, caseId + ".vision.jsonl");

    public static void AppendPhoto(string cacheDir, string caseId, VisionPhotoUsage usage)
    {
        Directory.CreateDirectory(cacheDir);
        File.AppendAllText(
            VisionPath(cacheDir, caseId),
            JsonSerializer.Serialize(usage, JsonOpts) + "\n");
    }

    public static IReadOnlyList<VisionPhotoUsage> ReadPhotos(string cacheDir, string caseId)
    {
        var path = VisionPath(cacheDir, caseId);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<VisionPhotoUsage>(l, JsonOpts)
                ?? throw new InvalidOperationException($"Vision line did not parse: {path}"))
            .ToList();
    }
}

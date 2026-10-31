// Per-case tool-loop transcript: one JSON object per line in
// <CaseId>.transcript.jsonl in the case dir. Every turn appends its reasoning
// text, tool calls, validation outcome per call, and results. This is the
// verification log for Spark-driven runs: it proves which step Spark chose
// and what C# allowed. Append-only; replays add lines, never rewrite them.
using System.Text.Json;

namespace Gate.Agent;

public enum ToolCallVerdict
{
    Executed,
    Blocked,
    /// <summary>The call passed validation but execution threw. The error is
    /// also a timeline note; the advance aborts after this turn is appended.</summary>
    Failed,
    /// <summary>The model returned no tool calls, so C# executed the picked
    /// step (text-only model fallback). Recorded, never silent.</summary>
    ModelDeferred,
}

public sealed record TranscriptToolCall(
    string CallId,
    string Name,
    string ArgumentsJson,
    ToolCallVerdict Verdict,
    string Detail);

public sealed record TranscriptTurn(
    int Turn,
    DateTimeOffset At,
    string Stage,
    string PickedStep,
    string Reasoning,
    IReadOnlyList<TranscriptToolCall> Calls,
    IReadOnlyList<string> Results,
    string? Note = null);

public static class CaseTranscript
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static string TranscriptPath(string cacheDir, string caseId) =>
        Path.Combine(cacheDir, caseId + ".transcript.jsonl");

    public static void AppendTurn(string cacheDir, string caseId, TranscriptTurn turn)
    {
        Directory.CreateDirectory(cacheDir);
        File.AppendAllText(
            TranscriptPath(cacheDir, caseId),
            JsonSerializer.Serialize(turn, JsonOpts) + "\n");
    }

    public static IReadOnlyList<TranscriptTurn> ReadTurns(string cacheDir, string caseId)
    {
        var path = TranscriptPath(cacheDir, caseId);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<TranscriptTurn>(l, JsonOpts)
                ?? throw new InvalidOperationException($"Transcript line did not parse: {path}"))
            .ToList();
    }
}

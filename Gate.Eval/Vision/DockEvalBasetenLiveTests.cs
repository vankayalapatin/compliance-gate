// Live Baseten reader for both dock eval tiers. Loads ONLY each manifest
// (filenames plus hashes) and prompt text: never any oracle file. One read
// per photo into read-cache-baseten/ plus per-photo timings; complete
// caches make zero live calls. Reads eval-manifest.json plus
// read-prompt.txt only.
using System.Text.Json;
using Gate.Contracts;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class DockEvalBasetenLiveTests
{
    private const int MaxTokens = 8000;

    [Fact]
    [Trait("Category", "Live")]
    public Task ReadEasyFiftyWithBaseten() => RunTierAsync(
        DockEval50Files.EvalDir(), DockEval50Files.ManifestPath(), DockEval50Files.PromptPath(),
        Path.Combine(DockEval50Files.EvalDir(), "read-cache-baseten"),
        Path.Combine(DockEval50Files.EvalDir(), "timings-baseten.json"));

    [Fact]
    [Trait("Category", "Live")]
    public Task ReadHardThirtyWithBaseten() => RunTierAsync(
        DockEvalHardFiles.EvalDir(), DockEvalHardFiles.ManifestPath(), DockEvalHardFiles.PromptPath(),
        Path.Combine(DockEvalHardFiles.EvalDir(), "read-cache-baseten"),
        Path.Combine(DockEvalHardFiles.EvalDir(), "timings-baseten.json"));

    private static async Task RunTierAsync(
        string evalDir, string manifestFile, string promptFile, string cacheDir, string timingsFile)
    {
        Assert.True(File.Exists(manifestFile), $"Eval manifest is missing: {manifestFile}. Run the offline build first.");
        var prompt = File.ReadAllText(promptFile);
        Assert.False(string.IsNullOrWhiteSpace(prompt), $"Eval prompt is missing: {promptFile}.");

        using var manifest = VisionFiles.LoadJson(manifestFile);
        var promptVersion = manifest.RootElement.GetProperty("prompt_version").GetString()!;
        var images = manifest.RootElement.GetProperty("images").EnumerateArray().ToList();

        var cache = new VisionReadCache(cacheDir);
        var timings = LoadTimings(timingsFile);
        var missing = images
            .Where(entry => cache.TryRead(entry.GetProperty("sha256").GetString()!, promptVersion) is null)
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["Baseten:ApiKey"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey),
            "Baseten:ApiKey is missing. Set it with: dotnet user-secrets set \"Baseten:ApiKey\" \"<key>\" --project Gate.Web.");
        var model = config["Baseten:Model"];
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "eta-models/muse-spark-open-1026";
        }

        var baseUrl = config["Baseten:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://inference.baseten.co/v1";
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var reader = new DockEvalTimedClient(http, baseUrl!, model!, apiKey!, MaxTokens);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        foreach (var entry in missing)
        {
            var file = entry.GetProperty("image").GetString()!;
            var sha = entry.GetProperty("sha256").GetString()!;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(evalDir, file), cts.Token);
            var actual = VisionFiles.Sha256Hex(bytes);
            Assert.True(string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase),
                $"Final {file} changed since the manifest; rebuild before reading.");
            var result = await ReadWithOneRetryAsync(reader, prompt, bytes, cts.Token);
            DockEval50Scoring.ParseReads(result.Text);
            cache.Write(sha, promptVersion, model!, result.Text);
            timings[file] = new Dictionary<string, object?>
            {
                ["ms"] = result.ElapsedMs,
                ["prompt_tokens"] = result.Usage.PromptTokens,
                ["completion_tokens"] = result.Usage.CompletionTokens
            };
            File.WriteAllText(timingsFile, JsonSerializer.Serialize(
                new { model, reads = timings }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static Dictionary<string, Dictionary<string, object?>> LoadTimings(string timingsFile)
    {
        if (!File.Exists(timingsFile))
        {
            return new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(timingsFile));
        var timings = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.GetProperty("reads").EnumerateObject())
        {
            timings[prop.Name] = new Dictionary<string, object?>
            {
                ["ms"] = prop.Value.GetProperty("ms").GetInt64(),
                ["prompt_tokens"] = prop.Value.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : null,
                ["completion_tokens"] = prop.Value.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : null
            };
        }

        return timings;
    }

    private static async Task<TimedReadResult> ReadWithOneRetryAsync(
        DockEvalTimedClient reader, string prompt, byte[] bytes, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromMinutes(5));
                var result = await reader.ReadAsync(prompt, bytes, attemptCts.Token);
                DockEval50Scoring.ParseReads(result.Text);
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("Baseten read failed after one retry; refusing to cache it.", last);
    }
}

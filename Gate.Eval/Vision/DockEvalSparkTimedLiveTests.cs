// Live timed Spark re-run for both tiers. Fresh reads go to a NEW cache
// folder (read-cache-spark-rerun/) plus per-photo timings; the committed
// read-cache-spark/ originals are never overwritten. Compares like with
// like: per-tier token budgets match the original runs. Loads manifests
// plus prompts only: eval-manifest.json plus read-prompt.txt, never any
// oracle file.
using System.Text.Json;
using Gate.Contracts;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class DockEvalSparkTimedLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public Task RereadEasyFiftyWithSpark() => RunTierAsync(
        DockEval50Files.EvalDir(), DockEval50Files.ManifestPath(), DockEval50Files.PromptPath(),
        Path.Combine(DockEval50Files.EvalDir(), "read-cache-spark-rerun"),
        Path.Combine(DockEval50Files.EvalDir(), "timings-spark-rerun.json"),
        maxTokens: 2000);

    [Fact]
    [Trait("Category", "Live")]
    public Task RereadHardThirtyWithSpark() => RunTierAsync(
        DockEvalHardFiles.EvalDir(), DockEvalHardFiles.ManifestPath(), DockEvalHardFiles.PromptPath(),
        Path.Combine(DockEvalHardFiles.EvalDir(), "read-cache-spark-rerun"),
        Path.Combine(DockEvalHardFiles.EvalDir(), "timings-spark-rerun.json"),
        maxTokens: 4000);

    private static async Task RunTierAsync(
        string evalDir, string manifestFile, string promptFile,
        string cacheDir, string timingsFile, int maxTokens)
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
        var apiKey = config["MetaApi:ApiKey"];
        var model = config["MetaApi:Model"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "MetaApi:ApiKey is missing.");
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing.");
        var baseUrl = config["MetaApi:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://api.meta.ai/v1";
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var reader = new DockEvalTimedClient(http, baseUrl!, model!, apiKey!, maxTokens);
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

        throw new InvalidOperationException("Spark timed read failed after one retry; refusing to cache it.", last);
    }
}

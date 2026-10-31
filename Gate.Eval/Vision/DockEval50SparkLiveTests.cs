// Live Spark reader for the 50-photo eval. Loads ONLY the manifest
// (filenames plus hashes) and the prompt text: never the oracle file, so
// Spark cannot see expected values. One read per image, cached by sha; a
// complete cache makes zero live calls. Glimmer later reuses the same
// finals plus prompt with its own cache dir. Reads eval-manifest.json plus
// read-prompt.txt only.
using System.Text.Json;
using Gate.Contracts;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class DockEval50SparkLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task ReadAllFiftyWithSpark()
    {
        var evalDir = DockEval50Files.EvalDir();
        var manifestFile = DockEval50Files.ManifestPath();
        Assert.True(File.Exists(manifestFile), "Eval manifest is missing. Run the offline build first.");
        var prompt = File.ReadAllText(DockEval50Files.PromptPath());
        Assert.False(string.IsNullOrWhiteSpace(prompt), "Eval prompt is missing.");

        using var manifest = VisionFiles.LoadJson(manifestFile);
        var promptVersion = manifest.RootElement.GetProperty("prompt_version").GetString()!;
        var images = manifest.RootElement.GetProperty("images").EnumerateArray().ToList();
        Assert.True(images.Count >= 50, $"Eval needs >=50 images, found {images.Count}.");

        var cache = new VisionReadCache(DockEval50Files.SparkCacheDir());
        var missing = images
            .Where(entry => cache.TryRead(
                entry.GetProperty("sha256").GetString()!, promptVersion) is null)
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
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = baseUrl!,
            Model = model!,
            ApiKey = apiKey!
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        foreach (var entry in missing)
        {
            var file = entry.GetProperty("image").GetString()!;
            var sha = entry.GetProperty("sha256").GetString()!;
            var bytes = await File.ReadAllBytesAsync(Path.Combine(evalDir, file), cts.Token);
            var actual = VisionFiles.Sha256Hex(bytes);
            Assert.True(string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase),
                $"Final {file} changed since the manifest; rebuild before reading.");
            var text = await ReadWithOneRetryAsync(spark, prompt, bytes, cts.Token);
            DockEval50Scoring.ParseReads(text);
            cache.Write(sha, promptVersion, model!, text);
        }
    }

    private static async Task<string> ReadWithOneRetryAsync(
        IModelClient spark, string prompt, byte[] bytes, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromMinutes(5));
                var result = await spark.CompleteAsync(
                    new ModelRequest(prompt, MaxTokens: 2000, ImageJpeg: bytes), attemptCts.Token);
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    DockEval50Scoring.ParseReads(result.Text);
                    return result.Text;
                }

                last = new InvalidOperationException("empty read");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
            {
                last = ex;
            }
        }

        throw new InvalidOperationException("Spark read failed after one retry; refusing to cache it.", last);
    }
}

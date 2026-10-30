// Live guard for the AI-first labeling path: POST /images/edits answers
// 200 with an image payload. The pipeline falls back to compositing
// whenever an edit fails verification, so this test guards the attempt
// leg, not the verdict. Exactly one call, no retries.
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class VisionEditsProbeTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task EditsEndpointResponds()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "MetaApi:ApiKey is missing.");
        var baseUrl = config["MetaApi:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://api.meta.ai/v1";
        }

        var endpoint = baseUrl!.TrimEnd('/') + "/images/edits";
        var baseBytes = await File.ReadAllBytesAsync(
            Path.Combine(VisionFiles.RepoRoot(), "assets", "dock", "IMG-01-base.jpg"));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(baseBytes), "image", "base.jpg");
        form.Add(new StringContent("muse-image-1.0"), "model");
        form.Add(new StringContent("Add a small white rectangular sticker on the largest box."), "prompt");
        form.Add(new StringContent("1"), "n");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var response = await http.PostAsync(endpoint, form, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        Assert.True(response.IsSuccessStatusCode,
            $"Image edits failed at {endpoint} (HTTP {(int)response.StatusCode}): {(body.Length <= 300 ? body : body[..300] + "...")}");
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.GetProperty("data").EnumerateArray().First();
        var hasImage = (first.TryGetProperty("b64_json", out var b64) && !string.IsNullOrWhiteSpace(b64.GetString()))
            || (first.TryGetProperty("url", out var url) && !string.IsNullOrWhiteSpace(url.GetString()));
        Assert.True(hasImage, $"Image edits at {endpoint} returned neither b64_json nor url.");
    }
}

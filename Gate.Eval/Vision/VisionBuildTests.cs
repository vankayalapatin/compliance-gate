// Vision base generation: the two extra angle bases (Live, once). Skips
// when the bases already exist, so the `live` suite never respands calls.
// Labeling, reading, and scoring moved to the ASN-time DockImagePipeline
// (Gate.Agent); run it by explicit filter, then the offline accuracy test.
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class VisionBuildTests
{
    public const string ImageModelDefault = "muse-image-1.0";

    [Fact]
    [Trait("Category", "Live")]
    public async Task GenerateExtraAngles()
    {
        var dock = Path.Combine(VisionFiles.RepoRoot(), "assets", "dock");
        var jobs = new[]
        {
            (File: Path.Combine(dock, "IMG-02-base.jpg"), Prompt: "Photorealistic warehouse dock photograph, front-right three-quarter view of a wooden pallet stacked with exactly 5 plain brown cardboard shipping cartons (3 wide on the bottom row, 2 on top), every visible carton face completely blank with no text, no labels, no logos, no markings, soft daylight, concrete floor, blurred dock door background, no people."),
            (File: Path.Combine(dock, "IMG-03-base.jpg"), Prompt: "Photorealistic warehouse dock photograph, rear three-quarter view of a wooden pallet stacked with exactly 7 plain brown cardboard shipping cartons, every visible carton face completely blank with no text, no labels, no logos, no markings, one carton on the left side has a visibly crushed dented corner with torn outer paper but the boxes otherwise intact, concrete floor, blurred warehouse background, no people.")
        };
        if (jobs.All(job => File.Exists(job.File)))
        {
            return;
        }

        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey),
            "MetaApi:ApiKey is missing. Set it with: dotnet user-secrets set \"MetaApi:ApiKey\" \"<key>\" --project Gate.Web.");
        var baseUrl = config["MetaApi:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://api.meta.ai/v1";
        }

        var imageModel = config["MetaApi:ImageModel"];
        if (string.IsNullOrWhiteSpace(imageModel))
        {
            imageModel = ImageModelDefault;
        }

        var endpoint = baseUrl!.TrimEnd('/') + "/images/generations";
        using var http = new HttpClient();
        foreach (var job in jobs)
        {
            if (File.Exists(job.File))
            {
                continue;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            message.Content = new StringContent(
                JsonSerializer.Serialize(new { model = imageModel!, prompt = job.Prompt, n = 1 }),
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await http.SendAsync(message, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            Assert.True(response.IsSuccessStatusCode,
                $"Image generation failed at {endpoint} (HTTP {(int)response.StatusCode}): {(body.Length <= 400 ? body : body[..400] + "...")}");

            var bytes = await ResolveImageBytesAsync(http, body, cts.Token);
            Assert.NotNull(bytes);
            await File.WriteAllBytesAsync(job.File, bytes!, cts.Token);
        }
    }

    private static async Task<byte[]?> ResolveImageBytesAsync(HttpClient http, string body, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement.GetProperty("data").EnumerateArray().First();
        if (first.TryGetProperty("b64_json", out var b64) && !string.IsNullOrWhiteSpace(b64.GetString()))
        {
            return Convert.FromBase64String(b64.GetString()!);
        }

        if (first.TryGetProperty("url", out var url) && !string.IsNullOrWhiteSpace(url.GetString()))
        {
            return await http.GetByteArrayAsync(url.GetString(), ct);
        }

        Assert.Fail($"Image payload has neither b64_json nor url: {(body.Length <= 400 ? body : body[..400] + "...")}");
        return null;
    }
}

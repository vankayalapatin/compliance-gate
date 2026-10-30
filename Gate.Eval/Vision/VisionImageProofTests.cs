// Live proof: exactly ONE image call against the Meta model API, then done.
// Endpoint double-check (repo docs + OpenAI-compatible layout):
//   - SparkModelClient posts {BaseUrl}/chat/completions with BaseUrl default
//     https://api.meta.ai/v1 (Gate.Web/appsettings.json, SparkOptions).
//   - Fresh generation therefore posts {BaseUrl}/images/generations; edits
//     would post {BaseUrl}/images/edits.
//   - BaseUrl already ends in /v1, so appending "/images/generations" yields
//     the identical absolute URL as host-root + "/v1/images/..." without
//     doubling /v1.
// The key comes from user secrets (MetaApi:ApiKey) or the environment — it is
// never read from a repo file and never logged. No retries: a retry could
// burn a second call, so the single attempt stands as the proof.
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class VisionImageProofTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task SingleImageGenerationsCallProvesKeyAndEndpoint()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey),
            "MetaApi:ApiKey is missing. Set it with: dotnet user-secrets set \"MetaApi:ApiKey\" \"<key>\" --project Gate.Web (plus MetaApi:ImageModel).");

        var baseUrl = config["MetaApi:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://api.meta.ai/v1";
        }

        // Dedicated image model from secrets/env, defaulting to the pinned
        // Muse Image id proven via GET /models. Never the chat model: it can
        // only 404 and would burn the single allowed call.
        var imageModel = config["MetaApi:ImageModel"];
        var modelSource = "MetaApi:ImageModel";
        if (string.IsNullOrWhiteSpace(imageModel))
        {
            imageModel = "muse-image-1.0";
            modelSource = "pinned default muse-image-1.0 (override with MetaApi:ImageModel)";
        }
        var endpoint = baseUrl!.TrimEnd('/') + "/images/generations";

        var counter = new CountingHandler(new HttpClientHandler());
        using var http = new HttpClient(counter);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = imageModel!,
                prompt = "Connectivity probe: one plain brown cardboard shipping carton on a wooden warehouse pallet, no text, no logos, no labels, no people.",
                n = 1
            }),
            System.Text.Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Assert.Fail($"Vision image proof: request to {endpoint} ({modelSource} '{imageModel}') failed without a response: {ex.GetType().Name} {ex.Message}");
            return;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                Assert.Fail($"Vision image proof: key rejected at {endpoint} (HTTP {(int)response.StatusCode}). Check MetaApi:ApiKey. Body: {Truncate(body)}");
            }

            if (!response.IsSuccessStatusCode)
            {
                Assert.Fail($"Vision image proof: HTTP {(int)response.StatusCode} {response.ReasonPhrase} at {endpoint} ({modelSource} '{imageModel}'). Body: {Truncate(body)}");
            }

            var payload = Truncate(body, 2000);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var first = doc.RootElement.GetProperty("data").EnumerateArray().First();
                var b64 = first.TryGetProperty("b64_json", out var b) ? b.GetString() : null;
                var url = first.TryGetProperty("url", out var u) ? u.GetString() : null;
                Assert.False(string.IsNullOrWhiteSpace(b64) && string.IsNullOrWhiteSpace(url),
                    $"Vision image proof: 200 OK at {endpoint} but no b64_json/url in data[0]. Payload: {payload}");
            }
            catch (JsonException ex)
            {
                Assert.Fail($"Vision image proof: 200 OK at {endpoint} but the body is not the expected image payload: {ex.Message}. Payload: {payload}");
            }
        }

        Assert.Equal(1, counter.Count);
    }

    private static string Truncate(string value, int max = 400) =>
        value.Length <= max ? value : value[..max] + "...";

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return base.SendAsync(request, ct);
        }
    }
}

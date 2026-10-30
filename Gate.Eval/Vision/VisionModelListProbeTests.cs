// Live diagnostic: lists Meta model ids once so MetaApi:ImageModel can be
// pinned to the real Muse Image model. Prints model ids to test output;
// the key is never printed. Runs only when explicitly filtered.
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class VisionModelListProbeTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task ListModelsOnce()
    {
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

        var endpoint = baseUrl!.TrimEnd('/') + "/models";
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await http.SendAsync(message, cts.Token);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Assert.Fail($"GET /models at {endpoint} returned HTTP {(int)response.StatusCode} with an unparseable body: {(body.Length <= 500 ? body : body[..500] + "...")}");
            return;
        }

        Assert.True(response.IsSuccessStatusCode,
            $"GET /models failed at {endpoint} (HTTP {(int)response.StatusCode}). The key or base URL needs attention.");
        Assert.NotEmpty(ids);
        Assert.Contains("muse-image-1.0", ids);
    }
}

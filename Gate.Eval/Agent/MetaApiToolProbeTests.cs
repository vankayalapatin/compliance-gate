// Capability probe: Meta chat-completions tool calling, which the
// Spark-driven agent loop needs. Sends one function definition and asserts
// the model returns a tool call with parseable arguments. Category=Live:
// real API. The key comes from user secrets (MetaApi:ApiKey) or the
// environment, never from a file in the repo.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class MetaApiToolProbeTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task MetaApiSupportsToolCalling()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        var model = config["MetaApi:Model"];
        var baseUrl = config["MetaApi:BaseUrl"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "MetaApi:ApiKey is missing.");
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing.");

        using var http = new HttpClient();
        using var message = new HttpRequestMessage(HttpMethod.Post,
            (string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl).TrimEnd('/')
            + "/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(new
        {
            Model = model,
            Messages = new[] { new { Role = "user", Content = "What is 6 times 7? Use the tool." } },
            Tools = new[]
            {
                new
                {
                    Type = "function",
                    Function = new
                    {
                        Name = "multiply",
                        Description = "Multiply two numbers.",
                        Parameters = new
                        {
                            Type = "object",
                            Properties = new Dictionary<string, object>
                            {
                                ["a"] = new { Type = "number" },
                                ["b"] = new { Type = "number" },
                            },
                            Required = new[] { "a", "b" },
                        },
                    },
                },
            },
        }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web));

        using var response = await http.SendAsync(message);
        var body = await response.Content.ReadAsStringAsync();
        var excerpt = body[..Math.Min(body.Length, 500)];
        Assert.True(response.IsSuccessStatusCode, $"Meta API rejected tools: {(int)response.StatusCode} {excerpt}");
        using var json = JsonDocument.Parse(body);
        var messageElement = json.RootElement.GetProperty("choices")[0].GetProperty("message");
        var hasCalls = messageElement.TryGetProperty("tool_calls", out var calls)
            && calls.ValueKind == JsonValueKind.Array
            && calls.GetArrayLength() > 0;
        Assert.True(hasCalls, $"No tool_calls in response: {excerpt}");
        var fn = calls[0].GetProperty("function");
        Assert.Equal("multiply", fn.GetProperty("name").GetString());
        using var args = JsonDocument.Parse(fn.GetProperty("arguments").GetString() ?? "{}");
        Assert.True(
            args.RootElement.TryGetProperty("a", out _)
            && args.RootElement.TryGetProperty("b", out _),
            $"Tool arguments lack a/b: {fn.GetProperty("arguments").GetString()}");
    }
}

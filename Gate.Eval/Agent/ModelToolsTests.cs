// Tool-calling wire tests (offline): cache keys stay stable for plain
// requests, old cache entries without tool calls still serve, tool
// responses round-trip through the cache, and the Spark client serializes
// tools plus history and parses tool calls. No live calls.
using System.Net;
using System.Text;
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class ModelToolsTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public string? LastRequestJson;
        private readonly string _responseJson;

        public StubHandler(string responseJson) => _responseJson = responseJson;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestJson = await request.Content!.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string NewDir(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));

    [Fact]
    public void CacheKeyIgnoresAbsentToolsAndHistory()
    {
        var bare = new ModelRequest("Do it.", "Be brief.");
        var explicitNulls = new ModelRequest("Do it.", "Be brief.", 0, null, null, null, null);
        Assert.Equal(
            CachedModelClient.CacheKey("m", bare),
            CachedModelClient.CacheKey("m", explicitNulls));
    }

    [Fact]
    public void ToolsAndHistoryChangeTheCacheKey()
    {
        var bare = new ModelRequest("Do it.", "Be brief.");
        var keyed = CachedModelClient.CacheKey("m", bare);
        var withTools = bare with
        {
            Tools = [new ModelTool("t", "d", [new ModelToolParameter("p", "d", ModelToolParameterType.String)])],
        };
        var withHistory = bare with { History = [new ModelMessage("user", "hi")] };
        Assert.NotEqual(keyed, CachedModelClient.CacheKey("m", withTools));
        Assert.NotEqual(keyed, CachedModelClient.CacheKey("m", withHistory));
    }

    [Fact]
    public async Task OldCacheEntryWithoutToolCallsStillServes()
    {
        var dir = NewDir("gate-tools-cache");
        var request = new ModelRequest("Do it.", "Be brief.");
        var path = Path.Combine(dir, CachedModelClient.CacheKey("fake", request) + ".json");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path,
            """{"Model":"fake","Prompt":"Do it.","Text":"done","CachedAt":"2026-01-01T00:00:00+00:00"}""");

        var result = await new CachedModelClient(new FakeModelClient(), dir).CompleteAsync(request);

        Assert.Equal("done", result.Text);
        Assert.Null(result.ToolCalls);
    }

    [Fact]
    public async Task ToolResponseRoundTripsThroughCacheWithOneInnerCall()
    {
        var dir = NewDir("gate-tools-cache");
        var inner = new FakeModelClient();
        inner.QueueResult(new ModelResult(
            string.Empty, [new ModelToolCall("call-1", "raise_pr", """{"caseId":"C-1"}""")]));
        var cached = new CachedModelClient(inner, dir);
        var request = new ModelRequest(
            "Pick.", "Agent.",
            Tools: [new ModelTool("raise_pr", "Raise.", [new ModelToolParameter("caseId", "c", ModelToolParameterType.String)])]);

        var first = await cached.CompleteAsync(request);
        var second = await cached.CompleteAsync(request);

        Assert.Equal("raise_pr", Assert.Single(first.ToolCalls!).Name);
        Assert.Equal("raise_pr", Assert.Single(second.ToolCalls!).Name);
        Assert.Equal("""{"caseId":"C-1"}""", second.ToolCalls![0].ArgumentsJson);
        Assert.Equal(1, inner.CallCount);
    }

    [Fact]
    public async Task SparkClientSendsToolsAndHistoryAndParsesToolCalls()
    {
        var stub = new StubHandler("""
            {"choices": [{"message": {"content": "", "tool_calls": [
              {"id": "call-9", "type": "function",
               "function": {"name": "raise_pr", "arguments": "{\"caseId\":\"C-1\"}"}}]},
              "finish_reason": "tool_calls"}]}
            """);
        var client = new SparkModelClient(
            new HttpClient(stub),
            new SparkOptions { BaseUrl = "https://example.invalid/v1", Model = "m", ApiKey = "k" });
        var request = new ModelRequest(
            "Pick.", "Agent.",
            Tools:
            [
                new ModelTool("raise_pr", "Raise.", [
                    new ModelToolParameter("caseId", "c", ModelToolParameterType.String),
                    new ModelToolParameter("qty", "q", ModelToolParameterType.Number, Required: false),
                    new ModelToolParameter("tags", "t", ModelToolParameterType.String, IsArray: true),
                ]),
            ],
            History: [new ModelMessage("user", "earlier")]);

        var result = await client.CompleteAsync(request);

        var call = Assert.Single(result.ToolCalls!);
        Assert.Equal("call-9", call.Id);
        Assert.Equal("raise_pr", call.Name);
        Assert.Equal("""{"caseId":"C-1"}""", call.ArgumentsJson);
        using var sent = JsonDocument.Parse(stub.LastRequestJson!);
        var root = sent.RootElement;
        Assert.Equal("m", root.GetProperty("model").GetString());
        var tools = root.GetProperty("tools");
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        var fn = tools[0].GetProperty("function");
        Assert.Equal("raise_pr", fn.GetProperty("name").GetString());
        var schema = fn.GetProperty("parameters");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        var props = schema.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("caseId").GetProperty("type").GetString());
        Assert.Equal("number", props.GetProperty("qty").GetProperty("type").GetString());
        Assert.Equal("array", props.GetProperty("tags").GetProperty("type").GetString());
        Assert.Equal("string", props.GetProperty("tags").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(
            ["caseId", "tags"],
            schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList());
        var messages = root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("earlier", messages[1].GetProperty("content").GetString());
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("Pick.", messages[2].GetProperty("content").GetString());
    }
}

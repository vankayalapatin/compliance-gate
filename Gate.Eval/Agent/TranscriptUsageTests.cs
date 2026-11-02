// Transcript usage tests: tool-loop turns record the model, token usage,
// and cache origin; cached replays are marked so they never bill; the live
// client parses provider usage; old transcript lines still load.
using System.Net;
using System.Text.Json;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class TranscriptUsageTests
{
    [Fact]
    public async Task ToolLoopRecordsModelAndUsagePerTurn()
    {
        var erp = SeededErp.Create();
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = Path.Combine(Path.GetTempPath(), "gate-usage-test", Guid.NewGuid().ToString("N"));
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        var cases = await runner.OnOrderWebhookAsync(new OrderWebhookInput(
            "test", "SO-1001", "planner",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [SeededErp.Battery] = ["V-A", "V-B"],
                [SeededErp.Screw] = ["V-C"],
            }));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        models.QueueResult(new ModelResult("shortfall",
            [new ModelToolCall("c1", AgentTools.ComputeShortfall, Args(caseId))],
            new ModelUsage(8000, 2000)));
        models.QueueResult(new ModelResult("raise",
            [new ModelToolCall("c2", AgentTools.RaisePr, Args(caseId))],
            new ModelUsage(4000, 1000)));

        await runner.AdvanceAsync(caseId);

        var turns = CaseTranscript.ReadTurns(dir, caseId);
        Assert.Equal(2, turns.Count);
        Assert.All(turns, t => Assert.Equal("fake", t.Model));
        Assert.All(turns, t => Assert.False(t.FromCache));
        Assert.Equal(8000, turns[0].InputTokens);
        Assert.Equal(2000, turns[0].OutputTokens);
        Assert.Equal(4000, turns[1].InputTokens);
        Assert.Equal(1000, turns[1].OutputTokens);
    }

    [Fact]
    public async Task CachedClientMarksReplaysSoTheyNeverBill()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gate-cacheflag-test", Guid.NewGuid().ToString("N"));
        var models = new FakeModelClient();
        models.QueueResponse("hello");
        var cached = new CachedModelClient(models, dir);
        var request = new ModelRequest("Say hello.");

        var first = await cached.CompleteAsync(request);
        var second = await cached.CompleteAsync(request);

        Assert.Equal("hello", first.Text);
        Assert.Equal("hello", second.Text);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Null(second.Usage);
    }

    [Fact]
    public async Task SparkParsesProviderUsage()
    {
        var handler = new StubHandler("""
            {"choices":[{"message":{"content":"hi"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":11,"completion_tokens":7}}
            """);
        var client = new SparkModelClient(
            new HttpClient(handler), new SparkOptions { Model = "m", ApiKey = "test-key" });

        var result = await client.CompleteAsync(new ModelRequest("Say hi."));

        Assert.Equal("hi", result.Text);
        Assert.NotNull(result.Usage);
        Assert.Equal(11, result.Usage.InputTokens);
        Assert.Equal(7, result.Usage.OutputTokens);
    }

    [Fact]
    public async Task TranscriptionRecordsVisionUsagePerPhoto()
    {
        var erp = SeededErp.Create();
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = Path.Combine(Path.GetTempPath(), "gate-vision-usage-test", Guid.NewGuid().ToString("N"));
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        var cases = await runner.OnOrderWebhookAsync(new OrderWebhookInput(
            "test", "SO-1001", "planner",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [SeededErp.Battery] = ["V-A", "V-B"],
                [SeededErp.Screw] = ["V-C"],
            }));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        models.QueueResult(new ModelResult(
            """[{"carton_id":"CTN-1","part":"BAT-1","po_number":"PO-X","lot_code":"LOT-1","units_per_carton":10,"mark_present":true,"mark_legible":true,"un_number":"UN3480","class9_visible":true,"damage_visible":false,"damage_note":null,"read_confidence":0.95}]""",
            null, new ModelUsage(1200, 300)));
        var photo = new DockPhoto("IMG-9", [4, 5, 6], null);

        var first = await runner.TranscribeDockPhotosAsync(caseId, [photo]);
        var second = await runner.TranscribeDockPhotosAsync(caseId, [photo]);

        Assert.Equal(first, second);
        Assert.Equal(1, models.CallCount);
        var usage = CaseVisionUsage.ReadPhotos(dir, caseId);
        Assert.Equal(2, usage.Count);
        Assert.Equal("IMG-9", usage[0].ImageId);
        Assert.Equal(VisionReadCache.CartonReadV1, usage[0].PromptVersion);
        Assert.Equal("fake", usage[0].Model);
        Assert.Equal(1200, usage[0].InputTokens);
        Assert.Equal(300, usage[0].OutputTokens);
        Assert.False(usage[0].FromCache);
        Assert.True(usage[1].FromCache);
        Assert.Null(usage[1].InputTokens);
    }

    [Fact]
    public void OldTranscriptLinesLoadWithoutUsage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gate-oldturn-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "case-9.transcript.jsonl"),
            JsonSerializer.Serialize(new
            {
                turn = 1,
                at = DateTimeOffset.UtcNow,
                stage = "Created",
                pickedStep = "ComputeShortfall",
                reasoning = "r",
                calls = Array.Empty<object>(),
                results = Array.Empty<string>(),
            }) + "\n");

        var turns = CaseTranscript.ReadTurns(dir, "case-9");

        var turn = Assert.Single(turns);
        Assert.Null(turn.Model);
        Assert.Null(turn.InputTokens);
        Assert.Null(turn.OutputTokens);
        Assert.False(turn.FromCache);
    }

    private static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StubHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}

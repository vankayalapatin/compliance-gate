// Live agent proof: real Spark calls behind the file cache while the case
// runs from webhook to PO through the tool loop (Spark picks compute, raise,
// and screen; the award rationale rides along), then the identical advance
// served with zero new HTTP. The key comes from user secrets (MetaApi:ApiKey)
// or the environment, never from a file in the repo. Tool turns use the
// client default cap: small caps starve the model's reasoning.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class AgentSparkLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveSparkRunReachesThePoFullyCached()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        var model = config["MetaApi:Model"];
        var baseUrl = config["MetaApi:BaseUrl"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey),
            "MetaApi:ApiKey is missing. Set it with: dotnet user-secrets set \"MetaApi:ApiKey\" \"<key>\" --project Gate.Web (plus MetaApi:Model).");
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing.");

        var counter = new CountingHandler(new HttpClientHandler());
        using var http = new HttpClient(counter);
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl,
            Model = model!,
            ApiKey = apiKey!
        });
        var caseDir = Path.Combine(Path.GetTempPath(), "gate-agent-live", Guid.NewGuid().ToString("N"));
        var cacheDir = Path.Combine(Path.GetTempPath(), "gate-agent-live-cache", Guid.NewGuid().ToString("N"));

        var erp = SeededErp.Create();
        var cached = new CachedModelClient(spark, cacheDir);
        var compliance = new ComplianceEngine();
        var runner = new CaseRunner(erp, new P2pAgent(erp, cached, compliance), cached,
            compliance, new DockEvaluatorAdapter(), caseDir);

        var cases = await runner.OnOrderWebhookAsync(new OrderWebhookInput(
            "live", "SO-1001", "planner",
            new Dictionary<string, IReadOnlyList<string>> { [SeededErp.Battery] = ["V-A", "V-B"] }));
        var battery = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        await runner.AdvanceAsync(battery);
        var raised = CaseStore.Load(caseDir, battery)!;
        var pr = raised.Decisions.Single(d =>
            d.Kind == DecisionKind.PrApproval && d.Status == DecisionStatus.Pending);
        await runner.ResolveDecisionAsync(battery, pr.DecisionId, true, pr.RequestedTo, "ok");

        await runner.AdvanceAsync(battery);
        var ordered = CaseStore.Load(caseDir, battery)!;
        Assert.NotNull(ordered.Case.PoNumber);
        Assert.Equal("V-B", (await erp.GetPurchaseOrderAsync(ordered.Case.PoNumber))!.VendorId);
        Assert.Contains(ordered.Events, e =>
            e.Kind == CaseEventKind.Note
            && e.RuleId == ComplianceEngine.CorpOriginCapId
            && e.Summary.Contains("rejected for the award", StringComparison.Ordinal));

        Assert.True(counter.Count >= 4, $"Expected at least the compute, raise, screen, and rationale calls, saw {counter.Count}.");
        Assert.Equal(counter.Count, Directory.GetFiles(cacheDir).Length);
        var turns = Gate.Agent.CaseTranscript.ReadTurns(caseDir, battery);
        Assert.Contains(turns.SelectMany(t => t.Calls),
            c => c.Verdict == Gate.Agent.ToolCallVerdict.Executed && c.Name == Gate.Agent.AgentTools.ScreenVendors);
        var callsAfterPo = counter.Count;

        var parked = await runner.AdvanceAsync(battery);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        Assert.Equal(callsAfterPo, counter.Count);
    }

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

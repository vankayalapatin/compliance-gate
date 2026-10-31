// Live award-reason proof: the case is driven to award-ready offline
// (scripted compute, raise, and screen), then live Spark awards through the
// tool loop. The SupplierAwarded event must name each losing vendor plus its
// blocking rule id, proving Spark wrote the loser detail (C# validates the
// tokens but never appends them). Keys come from user secrets or the
// environment, never from a file in the repo.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class AwardReasonLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveSparkAwardNamesEachLoserAndItsBlockingRule()
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

        var erp = SeededErp.Create();
        var compliance = new ComplianceEngine();
        var caseDir = Path.Combine(Path.GetTempPath(), "gate-award-live", Guid.NewGuid().ToString("N"));
        var scripted = new RecordingModelClient();
        var setup = new CaseRunner(erp, new P2pAgent(erp, scripted, compliance), scripted,
            compliance, new DockEvaluatorAdapter(), caseDir);
        var cases = await setup.OnOrderWebhookAsync(new OrderWebhookInput(
            "live", "SO-1001", "planner",
            new Dictionary<string, IReadOnlyList<string>> { [SeededErp.Battery] = ["V-A", "V-B"] }));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
        string Args() => $$"""{"case_id":"{{caseId}}"}""";
        scripted.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args()));
        scripted.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args()));
        await setup.AdvanceAsync(caseId);
        var raised = CaseStore.Load(caseDir, caseId)!;
        var pr = raised.Decisions.Single(d => d.Kind == DecisionKind.PrApproval);
        await setup.ResolveDecisionAsync(caseId, pr.DecisionId, true, pr.RequestedTo, "ok");
        scripted.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args()));
        try
        {
            await setup.AdvanceAsync(caseId);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No scripted response", StringComparison.Ordinal)
            || ex.Message.Contains("failed after one retry", StringComparison.Ordinal))
        {
            // Expected: the scripted queue ends after screening so live Spark owns the award.
        }

        var screened = CaseStore.Load(caseDir, caseId)!;
        Assert.Contains(screened.Events, e => e.Kind == CaseEventKind.VendorScreened);
        Assert.Null(screened.Case.AwardedVendorId);

        using var http = new HttpClient();
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl,
            Model = model!,
            ApiKey = apiKey!,
        });
        var modelCache = Path.Combine(Path.GetTempPath(), "gate-award-live-cache", Guid.NewGuid().ToString("N"));
        var live = new CachedModelClient(spark, modelCache);
        var runner = new CaseRunner(erp, new P2pAgent(erp, live, compliance), live,
            compliance, new DockEvaluatorAdapter(), caseDir);

        var outcome = await runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);
        var awarded = CaseStore.Load(caseDir, caseId)!;
        Assert.Equal("V-B", awarded.Case.AwardedVendorId);
        var award = awarded.Events.Single(e => e.Kind == CaseEventKind.SupplierAwarded);
        Assert.Contains("V-A", award.Summary, StringComparison.Ordinal);
        Assert.Contains(ComplianceEngine.CorpOriginCapId, award.Summary, StringComparison.Ordinal);
        var turns = CaseTranscript.ReadTurns(caseDir, caseId);
        Assert.Contains(turns.SelectMany(t => t.Calls), c =>
            string.Equals(c.Name, AgentTools.AwardSupplier, StringComparison.Ordinal)
            && c.Verdict == ToolCallVerdict.Executed);
    }
}

// Per-turn output budget tests (offline, scripted model): every tool-turn
// request carries the constructor budget, defaulting to 16000 when no
// override arrives. Test-double data only.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class TurnBudgetTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp, CaseRunner Runner, RecordingModelClient Models, string CaseDir);

    private static string TempDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    private static Harness Create(int? turnMaxTokens = null)
    {
        var erp = SeededErp.Create();
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir("gate-turn-budget-test");
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir, turnMaxTokens: turnMaxTokens);
        return new Harness(erp, runner, models, dir);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-A", "V-B"],
            [SeededErp.Screw] = ["V-C"]
        };

    private static async Task<string> OpenBatteryCaseAsync(Harness h)
    {
        var cases = await h.Runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        return cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
    }

    private static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";

    [Fact]
    public async Task DefaultTurnBudgetIs16000()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));

        var outcome = await h.Runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);
        Assert.Equal(2, h.Models.Requests.Count);
        Assert.All(h.Models.Requests, r => Assert.Equal(16000, r.MaxTokens));
    }

    [Fact]
    public async Task OverrideTurnBudgetIsRespected()
    {
        var h = Create(turnMaxTokens: 8000);
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));

        var outcome = await h.Runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);
        Assert.Equal(2, h.Models.Requests.Count);
        Assert.All(h.Models.Requests, r => Assert.Equal(8000, r.MaxTokens));
    }
}

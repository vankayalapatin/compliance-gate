// Presentation-pace tests: off by default with a two-second step pause,
// and pacing only adds delay — the same scripted advance lands on the same
// step with the same transcript verdicts, only slower.
using System.Diagnostics;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class PresentationPaceTests
{
    [Fact]
    public void PaceIsOffByDefaultWithTwoSecondSteps()
    {
        var pace = new PresentationPace();

        Assert.False(pace.IsOn);
        Assert.Equal(TimeSpan.FromSeconds(2), pace.StepDelay);
    }

    [Fact]
    public async Task PaceOnlyAddsDelay()
    {
        var plain = CreateHarness(new PresentationPace { IsOn = false });
        var paced = CreateHarness(new PresentationPace { IsOn = true, StepDelay = TimeSpan.FromMilliseconds(100) });
        var plainCase = await OpenBatteryCaseAsync(plain);
        var pacedCase = await OpenBatteryCaseAsync(paced);
        plain.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(plainCase)));
        plain.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(plainCase)));
        paced.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(pacedCase)));
        paced.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(pacedCase)));

        var plainOutcome = await plain.Runner.AdvanceAsync(plainCase);
        var sw = Stopwatch.StartNew();
        var pacedOutcome = await paced.Runner.AdvanceAsync(pacedCase);
        sw.Stop();

        Assert.Equal(plainOutcome.Step, pacedOutcome.Step);
        Assert.Equal(
            CaseTranscript.ReadTurns(plain.CaseDir, plainCase).Select(Fingerprint),
            CaseTranscript.ReadTurns(paced.CaseDir, pacedCase).Select(Fingerprint));
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(100),
            $"Paced advance took {sw.Elapsed.TotalMilliseconds}ms; expected at least one 100ms step pause.");
    }

    private static string Fingerprint(TranscriptTurn turn) =>
        turn.PickedStep + ":" + string.Join(",", turn.Calls.Select(c => c.Name + "=" + c.Verdict));

    private sealed record Harness(
        InMemoryErpAdapter Erp, CaseRunner Runner, RecordingModelClient Models, string CaseDir);

    private static Harness CreateHarness(PresentationPace pace)
    {
        var erp = SeededErp.Create();
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = Path.Combine(Path.GetTempPath(), "gate-pace-test", Guid.NewGuid().ToString("N"));
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir, pace: pace);
        return new Harness(erp, runner, models, dir);
    }

    private static async Task<string> OpenBatteryCaseAsync(Harness h)
    {
        var cases = await h.Runner.OnOrderWebhookAsync(new OrderWebhookInput(
            "test", "SO-1001", "planner",
            new Dictionary<string, IReadOnlyList<string>>
            {
                [SeededErp.Battery] = ["V-A", "V-B"],
                [SeededErp.Screw] = ["V-C"],
            }));
        return cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));
    }

    private static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";
}

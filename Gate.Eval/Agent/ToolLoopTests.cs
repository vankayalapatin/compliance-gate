// Tool-loop tests (offline, scripted model): the prompt presents tools
// without prescribing the choice; the same state plus different model
// responses yields different executed actions; illegal proposals are blocked
// and fed back; waiting and Done stay C# truth with zero model calls; every
// turn lands on the transcript.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gate.Eval;

public sealed class ToolLoopTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp, CaseRunner Runner, RecordingModelClient Models, string CaseDir);

    private static string TempDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    private static Harness Create()
    {
        var erp = SeededErp.Create();
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir("gate-tooloop-test");
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
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

    private static CaseFile CaseFileAt(CaseStage stage, DateTimeOffset stamp)
    {
        return new CaseFile(
            new Case("case-1", "SO-1", "BAT-1", "test", stage, stamp, stamp,
                null, null, null, null, [], [], null, null),
            [new CaseEvent("e1", "case-1", stamp, RecordSource.P2pAgent,
                CaseEventKind.OrderReceived, "Order SO-1 received for BAT-1.",
                null, null, null, null, null, null)],
            []);
    }

    private static CaseErpView View() => new(true, null, null, false, false, false, 0, 0, 0, false, 0, false);

    [Fact]
    public void PromptPresentsToolsWithoutPrescribingTheChoice()
    {
        var file = CaseFileAt(CaseStage.Created, DateTimeOffset.UtcNow);
        var prompt = AgentTools.BuildTurnPrompt(file, View());

        Assert.Contains("case-1", prompt, StringComparison.Ordinal);
        Assert.Contains(CaseStage.Created.ToString(), prompt, StringComparison.Ordinal);
        Assert.Contains("Call the tool you judge best.", prompt, StringComparison.Ordinal);
        foreach (var tool in AgentTools.Catalog)
        {
            Assert.DoesNotContain(tool.Name, prompt, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("ComputeShortfall", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("must call", AgentTools.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolCatalogMirrorsExecutorsReadsAndVendorEmail()
    {
        var names = AgentTools.Catalog.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(11, names.Count);
        foreach (var action in new[]
            {
                AgentTools.ComputeShortfall, AgentTools.RaisePr, AgentTools.ScreenVendors,
                AgentTools.SubmitPoApproval, AgentTools.EvaluatePrePay, AgentTools.NotifyCustomer,
                AgentTools.FinishCase, AgentTools.SendVendorEmail,
            })
        {
            Assert.Contains(action, names);
        }

        foreach (var read in new[] { AgentTools.GetCase, AgentTools.GetErpView, AgentTools.GetMail })
        {
            Assert.Contains(read, names);
        }

        foreach (var tool in AgentTools.Catalog)
        {
            Assert.NotEmpty(tool.Parameters);
            Assert.Contains(tool.Parameters, p => p.Name == "case_id");
        }
    }

    [Fact]
    public void PromptIsStableAcrossTimestamps()
    {
        var view = View();
        var first = AgentTools.BuildTurnPrompt(
            CaseFileAt(CaseStage.Created, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), view);
        var second = AgentTools.BuildTurnPrompt(
            CaseFileAt(CaseStage.Created, new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero)), view);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task SameStateDifferentModelResponsesYieldDifferentExecutedActions()
    {
        var first = Create();
        var caseA = await OpenBatteryCaseAsync(first);
        first.Models.QueueToolCalls("shortfall first", ("c1", AgentTools.ComputeShortfall, Args(caseA)));
        first.Models.QueueToolCalls("raise now", ("c2", AgentTools.RaisePr, Args(caseA)));
        await first.Runner.AdvanceAsync(caseA);
        var transcriptA = CaseTranscript.ReadTurns(first.CaseDir, caseA);

        var second = Create();
        var caseB = await OpenBatteryCaseAsync(second);
        second.Models.QueueToolCalls("raise first", ("c1", AgentTools.RaisePr, Args(caseB)));
        second.Models.QueueToolCalls("fine, shortfall", ("c2", AgentTools.ComputeShortfall, Args(caseB)));
        second.Models.QueueToolCalls("raise now", ("c3", AgentTools.RaisePr, Args(caseB)));
        await second.Runner.AdvanceAsync(caseB);
        var transcriptB = CaseTranscript.ReadTurns(second.CaseDir, caseB);

        Assert.Equal(ToolCallVerdict.Executed, transcriptA[0].Calls[0].Verdict);
        Assert.Equal(AgentTools.ComputeShortfall, transcriptA[0].Calls[0].Name);
        Assert.Equal(ToolCallVerdict.Blocked, transcriptB[0].Calls[0].Verdict);
        Assert.Equal(AgentTools.RaisePr, transcriptB[0].Calls[0].Name);
        Assert.Contains("Illegal move", transcriptB[0].Calls[0].Detail, StringComparison.Ordinal);
        Assert.Equal(2, transcriptA.Count);
        Assert.Equal(3, transcriptB.Count);
    }

    [Fact]
    public async Task IllegalProposalIsBlockedAndFedBackToTheModel()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueToolCalls("raise first", ("c1", AgentTools.RaisePr, Args(caseId)));
        h.Models.QueueToolCalls("shortfall first", ("c2", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise now", ("c3", AgentTools.RaisePr, Args(caseId)));

        var outcome = await h.Runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);
        var turns = CaseTranscript.ReadTurns(h.CaseDir, caseId);
        Assert.Equal(3, turns.Count);
        Assert.Equal(ToolCallVerdict.Blocked, turns[0].Calls[0].Verdict);
        Assert.Contains(AgentTools.ComputeShortfall, turns[0].Calls[0].Detail, StringComparison.Ordinal);
        var feedback = h.Models.Requests[1].History ?? [];
        Assert.Contains(feedback, m => m.Role == "tool"
            && (m.Text ?? string.Empty).Contains("Blocked", StringComparison.Ordinal)
            && (m.Text ?? string.Empty).Contains(AgentTools.RaisePr, StringComparison.Ordinal));
        Assert.Equal(ToolCallVerdict.Executed, turns[1].Calls[0].Verdict);
    }

    [Fact]
    public async Task UnknownToolAndBadArgsAreBlockedWhileTheLegalCallExecutes()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueToolCalls("mixed bag",
            ("c1", "teleport", Args(caseId)),
            ("c2", AgentTools.ComputeShortfall, "{}"),
            ("c3", AgentTools.ComputeShortfall, "{oops"),
            ("c4", AgentTools.ComputeShortfall, Args("other-case")),
            ("c5", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise now", ("c6", AgentTools.RaisePr, Args(caseId)));

        var outcome = await h.Runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);
        var first = CaseTranscript.ReadTurns(h.CaseDir, caseId)[0];
        Assert.Equal(5, first.Calls.Count);
        Assert.Equal(ToolCallVerdict.Blocked, first.Calls[0].Verdict);
        Assert.Contains("Unknown tool", first.Calls[0].Detail, StringComparison.Ordinal);
        Assert.All(first.Calls.Skip(1).Take(3), c => Assert.Equal(ToolCallVerdict.Blocked, c.Verdict));
        Assert.Equal(ToolCallVerdict.Executed, first.Calls[4].Verdict);
    }

    [Fact]
    public async Task ReadToolsExecuteWithoutChangingState()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueToolCalls("look around",
            ("c1", AgentTools.GetCase, Args(caseId)),
            ("c2", AgentTools.GetErpView, Args(caseId)),
            ("c3", AgentTools.GetMail, Args(caseId)));
        h.Models.QueueToolCalls("shortfall", ("c4", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c5", AgentTools.RaisePr, Args(caseId)));

        var outcome = await h.Runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);
        var turns = CaseTranscript.ReadTurns(h.CaseDir, caseId);
        Assert.All(turns[0].Calls, c => Assert.Equal(ToolCallVerdict.Executed, c.Verdict));
        Assert.Contains(CaseStage.Created.ToString(), turns[0].Results[0], StringComparison.Ordinal);
        Assert.Contains(CaseStage.Created.ToString(), h.Models.Requests[1].Prompt, StringComparison.Ordinal);
        Assert.Equal(3, h.Models.CallCount);
    }

    [Fact]
    public async Task TextOnlyResponseDefersToThePickerAndRecordsIt()
    {
        var erp = SeededErp.Create();
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir("gate-tooloop-test");
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        var cases = await runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        var outcome = await runner.AdvanceAsync(caseId);

        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);
        var turns = CaseTranscript.ReadTurns(dir, caseId);
        Assert.Equal(2, turns.Count);
        Assert.All(turns, t => Assert.All(t.Calls,
            c => Assert.Equal(ToolCallVerdict.ModelDeferred, c.Verdict)));
        Assert.Contains("shortfall", turns[0].Results[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WaitingAndDoneMakeZeroModelCalls()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        var parked = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitDecision, parked.Step);
        var callsAfterPark = h.Models.CallCount;

        var again = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitDecision, again.Step);
        Assert.Equal(callsAfterPark, h.Models.CallCount);

        var file = CaseStore.Load(h.CaseDir, caseId)!;
        var decision = file.Decisions.Single(d => d.Status == DecisionStatus.Pending);
        await h.Runner.ResolveDecisionAsync(caseId, decision.DecisionId, false, decision.RequestedTo, "no");
        var done = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.Done, done.Step);
        Assert.True(done.Complete);
        Assert.Equal(callsAfterPark, h.Models.CallCount);
    }

    [Fact]
    public async Task ReadsForeverHitsTheTurnCap()
    {
        var h = Create();
        var caseId = await OpenBatteryCaseAsync(h);
        for (var i = 0; i < 50; i++)
        {
            h.Models.QueueToolCalls($"look {i}", ($"c{i}", AgentTools.GetCase, Args(caseId)));
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.AdvanceAsync(caseId));
        Assert.Contains("50 tool turns", ex.Message, StringComparison.Ordinal);
        Assert.Equal(50, CaseTranscript.ReadTurns(h.CaseDir, caseId).Count);
    }

    [Fact]
    public async Task ModelFailureIsNotedAndRethrown()
    {
        var erp = SeededErp.Create();
        var throwing = new ThrowingModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir("gate-tooloop-test");
        var runner = new CaseRunner(erp, new P2pAgent(erp, throwing, compliance), throwing,
            compliance, new DockEvaluatorAdapter(), dir);
        var cases = await runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.AdvanceAsync(caseId));

        var file = CaseStore.Load(dir, caseId)!;
        Assert.Contains(file.Events, e =>
            e.Kind == CaseEventKind.Note && (e.Summary ?? string.Empty).Contains("failed", StringComparison.Ordinal));
        var turns = CaseTranscript.ReadTurns(dir, caseId);
        Assert.Single(turns);
        Assert.Contains("failed", turns[0].Note ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(2, throwing.CallCount);
    }

    [Fact]
    public async Task ToolCallWithEmptyTextIsAcceptedWithoutRetry()
    {
        var models = new FakeModelClient();
        models.QueueResult(new ModelResult(
            string.Empty, [new ModelToolCall("c1", AgentTools.GetCase, """{"case_id":"case-1"}""")]));

        var result = await ResilientCalls.CompleteWithRetryAsync(
            models, new ModelRequest("pick"), NullLogger.Instance, "tool");

        Assert.NotNull(result.ToolCalls);
        Assert.Single(result.ToolCalls);
        Assert.Equal(1, models.CallCount);
    }

    [Fact]
    public void TranscriptRoundTrips()
    {
        var dir = TempDir("gate-tooloop-test");
        CaseTranscript.AppendTurn(dir, "case-9", new TranscriptTurn(
            1, DateTimeOffset.UtcNow, CaseStage.Created.ToString(),
            nameof(CaseNextStep.ComputeShortfall), "shortfall first",
            [new TranscriptToolCall("c1", AgentTools.ComputeShortfall, Args("case-9"),
                ToolCallVerdict.Executed, "Executed compute_shortfall: Created -> ShortfallComputed.")],
            ["compute_shortfall: Executed compute_shortfall: Created -> ShortfallComputed."]));

        var turns = CaseTranscript.ReadTurns(dir, "case-9");

        var turn = Assert.Single(turns);
        Assert.Equal(1, turn.Turn);
        Assert.Equal("shortfall first", turn.Reasoning);
        Assert.Equal(AgentTools.ComputeShortfall, turn.Calls[0].Name);
        Assert.Equal(ToolCallVerdict.Executed, turn.Calls[0].Verdict);
        Assert.Empty(CaseTranscript.ReadTurns(dir, "case-missing"));
    }
}

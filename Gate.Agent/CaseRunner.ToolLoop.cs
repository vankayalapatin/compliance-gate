// Spark-driven tool loop (decision 43). Each turn the runner loads the case,
// rebuilds the ERP view, and asks CasePicker (C# truth): waiting or Done
// ends the advance with no model call. Otherwise Spark receives the stable
// case JSON plus ERP view plus tool schemas and returns tool calls; C#
// validates EVERY call against CasePicker.LegalTools — legal moves execute,
// illegal ones are blocked and the refusal is fed back to Spark. The loop
// repeats until Done, a waiting state, or the turn cap.
//
// Model-deferred fallback: a text-only response (no tool calls) executes the
// picked step directly and records the deferral in the transcript. Live Spark
// returns tool calls; scripted and text-only clients keep the C# path.
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Agent;

public partial class CaseRunner
{
    private const int MaxToolTurns = 50;

    private async Task<CaseAdvanceOutcome> AdvanceWithToolsAsync(
        string caseId, DateOnly? payUpToDate, CancellationToken ct)
    {
        var history = new List<ModelMessage>();
        for (var turn = 1; turn <= MaxToolTurns; turn++)
        {
            var file = CaseStore.Load(_cacheDir, caseId)
                ?? throw new InvalidOperationException($"Unknown case {caseId}.");
            var view = await BuildViewAsync(file, ct).ConfigureAwait(false);
            var (step, reason) = CasePicker.Pick(file, view, payUpToDate);
            _logger.LogInformation(
                "Case {CaseId} at {Stage}: next step {Step} ({Reason}).",
                caseId, file.Case.Stage, step, reason);
            if (step.IsWaiting())
            {
                return new CaseAdvanceOutcome(caseId, file.Case.Stage, step, step == CaseNextStep.Done, reason);
            }

            var legal = CasePicker.LegalTools(file, view, payUpToDate);
            // Tool turns need headroom: reasoning models can burn a 2000-token
            // budget thinking (observed on a pay-vs-email turn) and starve the
            // answer, failing the advance after retry.
            var request = new ModelRequest(
                AgentTools.BuildTurnPrompt(file, view),
                AgentTools.SystemPrompt,
                MaxTokens: 4000,
                Tools: AgentTools.Catalog,
                History: history.Count == 0 ? null : history.ToList());
            ModelResult result;
            try
            {
                result = await ResilientCalls.CompleteWithRetryAsync(
                    _models, request, _logger, $"tool turn {turn} for {caseId}", ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Case {CaseId} tool turn {Turn} failed.", caseId, turn);
                RecordFailureNote(caseId, step, ex);
                CaseTranscript.AppendTurn(_cacheDir, caseId, new TranscriptTurn(
                    turn, DateTimeOffset.UtcNow, file.Case.Stage.ToString(), step.ToString(),
                    string.Empty, [], [], $"Model call failed: {ex.Message}"));
                throw;
            }

            var calls = result.ToolCalls ?? [];
            if (calls.Count == 0)
            {
                await ExecuteDeferredAsync(caseId, file, view, step, turn, result.Text, history, payUpToDate, ct)
                    .ConfigureAwait(false);
                continue;
            }

            await ExecuteTurnCallsAsync(
                caseId, file, view, step, turn, result.Text, calls, legal, history, payUpToDate, ct)
                .ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Case {caseId} did not settle within {MaxToolTurns} tool turns.");
    }

    private async Task ExecuteDeferredAsync(
        string caseId, CaseFile file, CaseErpView view, CaseNextStep step, int turn,
        string reasoning, List<ModelMessage> history, DateOnly? payUpToDate, CancellationToken ct)
    {
        var before = file.Case.Stage;
        try
        {
            await ExecuteStepAsync(file, view, step, payUpToDate, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Case {CaseId} step {Step} failed.", caseId, step);
            RecordFailureNote(caseId, step, ex);
            CaseTranscript.AppendTurn(_cacheDir, caseId, new TranscriptTurn(
                turn, DateTimeOffset.UtcNow, before.ToString(), step.ToString(), reasoning,
                [new TranscriptToolCall("-", AgentTools.ToToolName(step) ?? step.ToString(), "{}",
                    ToolCallVerdict.ModelDeferred, $"C# executed the pick; it failed: {ex.Message}")],
                [], "Model returned no tool calls."));
            throw;
        }

        var after = (CaseStore.Load(_cacheDir, caseId) ?? file).Case.Stage;
        var tool = AgentTools.ToToolName(step) ?? step.ToString();
        var summary = $"Executed {tool}: {before} -> {after}.";
        _logger.LogInformation("Case {CaseId} turn {Turn}: model deferred; {Summary}", caseId, turn, summary);
        CaseTranscript.AppendTurn(_cacheDir, caseId, new TranscriptTurn(
            turn, DateTimeOffset.UtcNow, before.ToString(), step.ToString(), reasoning,
            [new TranscriptToolCall("-", tool, $"{{\"case_id\":\"{caseId}\"}}",
                ToolCallVerdict.ModelDeferred, "Model returned no tool calls; C# executed the pick.")],
            [summary], "Model-deferred turn."));
        history.Add(new ModelMessage("assistant", reasoning));
        history.Add(new ModelMessage("user",
            $"You returned no tool calls, so C# executed the picked step on your behalf: {summary} " +
            "Call tools yourself on the next turn."));
    }

    private async Task ExecuteTurnCallsAsync(
        string caseId, CaseFile file, CaseErpView view, CaseNextStep step, int turn,
        string reasoning, IReadOnlyList<ModelToolCall> calls, IReadOnlySet<string> legal,
        List<ModelMessage> history, DateOnly? payUpToDate, CancellationToken ct)
    {
        var transcriptCalls = new List<TranscriptToolCall>();
        var results = new List<string>();
        var acted = false;
        Exception? turnError = null;
        var toolMsgs = 0;
        foreach (var call in calls)
        {
            try
            {
                var (verdict, detail, actedNow) = await ExecuteOneCallAsync(
                    caseId, call, legal, reasoning, payUpToDate, ct).ConfigureAwait(false);
                acted |= actedNow;
                transcriptCalls.Add(new TranscriptToolCall(
                    call.Id, call.Name, call.ArgumentsJson, verdict, detail));
                results.Add($"{call.Name}: {detail}");
                history.Add(new ModelMessage(
                    "tool",
                    (verdict == ToolCallVerdict.Executed ? "Executed. " : "Blocked. ") + detail,
                    ToolCallId: call.Id,
                    Name: call.Name));
                toolMsgs++;
            }
            catch (Exception ex)
            {
                turnError = ex;
                transcriptCalls.Add(new TranscriptToolCall(
                    call.Id, call.Name, call.ArgumentsJson,
                    ToolCallVerdict.Failed, $"Execution failed: {ex.Message}"));
                results.Add($"{call.Name}: execution failed: {ex.Message}");
                break;
            }
        }

        if (!acted && turnError is null)
        {
            results.Add("No action executed this turn; read the refusals and call a legal tool.");
        }

        CaseTranscript.AppendTurn(_cacheDir, caseId, new TranscriptTurn(
            turn, DateTimeOffset.UtcNow, file.Case.Stage.ToString(), step.ToString(),
            reasoning, transcriptCalls, results,
            turnError is null ? null : $"Turn aborted: {turnError.Message}"));
        history.Insert(
            history.Count - toolMsgs,
            new ModelMessage("assistant", reasoning, calls.ToList()));
        _logger.LogInformation(
            "Case {CaseId} turn {Turn}: {Executed} of {Total} call(s) executed.",
            caseId, turn, transcriptCalls.Count(c => c.Verdict == ToolCallVerdict.Executed), calls.Count);
        if (turnError is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(turnError).Throw();
        }
    }

    /// <summary>Validates one tool call against a freshly loaded case plus
    /// view and executes it when legal. Reads run against the snapshot;
    /// before every action the case is re-loaded and re-picked, so a call is
    /// never executed against stale state.</summary>
    private async Task<(ToolCallVerdict Verdict, string Detail, bool Acted)> ExecuteOneCallAsync(
        string caseId, ModelToolCall call, IReadOnlySet<string> turnLegal, string reasoning,
        DateOnly? payUpToDate, CancellationToken ct)
    {
        if (AgentTools.Catalog.All(t => !string.Equals(t.Name, call.Name, StringComparison.Ordinal)))
        {
            return (ToolCallVerdict.Blocked, $"Unknown tool '{call.Name}'.", false);
        }

        var argCase = AgentTools.ReadStringArg(call.ArgumentsJson, "case_id");
        if (!string.Equals(argCase, caseId, StringComparison.Ordinal))
        {
            return (ToolCallVerdict.Blocked,
                $"case_id must be '{caseId}'; got '{argCase ?? "missing"}'.", false);
        }

        if (!turnLegal.Contains(call.Name))
        {
            return (ToolCallVerdict.Blocked, IllegalDetail(caseId, call.Name, turnLegal), false);
        }

        if (AgentTools.IsReadTool(call.Name))
        {
            return (ToolCallVerdict.Executed, await ReadToolAsync(caseId, call.Name, ct).ConfigureAwait(false), false);
        }

        // Action: re-validate against freshly loaded state before executing.
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var view = await BuildViewAsync(file, ct).ConfigureAwait(false);
        var (freshStep, freshReason) = CasePicker.Pick(file, view, payUpToDate);
        if (freshStep.IsWaiting())
        {
            return (ToolCallVerdict.Blocked,
                $"Case now waits ({freshStep}: {freshReason}); no action runs.", false);
        }

        var freshLegal = CasePicker.LegalTools(file, view, payUpToDate);
        if (!freshLegal.Contains(call.Name))
        {
            return (ToolCallVerdict.Blocked, IllegalDetail(caseId, call.Name, freshLegal), false);
        }

        if (string.Equals(call.Name, AgentTools.SendVendorEmail, StringComparison.Ordinal))
        {
            var invoiceRef = AgentTools.ReadStringArg(call.ArgumentsJson, "invoice_ref");
            if (invoiceRef is null)
            {
                return (ToolCallVerdict.Blocked, "send_vendor_email needs invoice_ref as vendor/number.", false);
            }

            if (CasePicker.UnemailedInvoiceProblems(file).All(d =>
                !string.Equals(d.DocumentNumber, invoiceRef, StringComparison.Ordinal)))
            {
                return (ToolCallVerdict.Blocked,
                    $"Invoice '{invoiceRef}' has no unemailed AP problem on this case.", false);
            }

            var before = file.Case.Stage;
            VendorEmail email;
            try
            {
                email = await SendVendorEmailAsync(caseId, invoiceRef, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Case {CaseId} send_vendor_email failed.", caseId);
                RecordFailureNote(caseId, AgentTools.SendVendorEmail, ex);
                throw;
            }

            var after = (CaseStore.Load(_cacheDir, caseId) ?? file).Case.Stage;
            return (ToolCallVerdict.Executed,
                $"Queried {email.To} about {invoiceRef} (mail {email.EmailId}): {before} -> {after}.", true);
        }

        if (string.Equals(call.Name, AgentTools.AwardSupplier, StringComparison.Ordinal))
        {
            var vendorId = AgentTools.ReadStringArg(call.ArgumentsJson, "vendor_id");
            if (vendorId is null)
            {
                return (ToolCallVerdict.Blocked, "award_supplier needs vendor_id of a passing vendor.", false);
            }

            var raw = await _core.ScreenCandidatesAsync(
                file.Case.PrNumber!, ReadCandidates(file, file.Case.ItemId), ct).ConfigureAwait(false);
            var (screenings, passing, _) = CorpExceptionFlow.ApplyApprovedExceptions(file, raw);
            if (passing.Count > 0 && !passing.Contains(vendorId, StringComparer.Ordinal))
            {
                return (ToolCallVerdict.Blocked,
                    $"Vendor {vendorId} did not pass screening; passing: {string.Join(", ", passing)}.", false);
            }

            if (string.IsNullOrWhiteSpace(reasoning))
            {
                return (ToolCallVerdict.Blocked,
                    "award_supplier needs a one-line reason naming each losing vendor and its blocking rule id.", false);
            }

            var missing = MissingLoserTokens(reasoning, screenings, vendorId);
            if (missing.Count > 0)
            {
                return (ToolCallVerdict.Blocked,
                    $"Award reason must name each losing vendor and its blocking rule id; " +
                    $"missing: {string.Join(", ", missing)}. Read the screening events and retry.", false);
            }

            try
            {
                await AwardSupplierStepAsync(file, vendorId, reasoning, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Case {CaseId} award_supplier failed.", caseId);
                RecordFailureNote(caseId, AgentTools.AwardSupplier, ex);
                throw;
            }

            return (ToolCallVerdict.Executed, $"Awarded {file.Case.ItemId} to {vendorId}.", true);
        }

        var mapped = AgentTools.ToStep(call.Name);
        if (mapped is null || mapped != freshStep)
        {
            return (ToolCallVerdict.Blocked, IllegalDetail(caseId, call.Name, freshLegal), false);
        }

        var beforeStage = file.Case.Stage;
        try
        {
            await ExecuteStepAsync(file, view, mapped.Value, payUpToDate, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Case {CaseId} step {Step} failed.", caseId, mapped.Value);
            RecordFailureNote(caseId, mapped.Value, ex);
            throw;
        }

        var afterStage = (CaseStore.Load(_cacheDir, caseId) ?? file).Case.Stage;
        return (ToolCallVerdict.Executed, $"Executed {call.Name}: {beforeStage} -> {afterStage}.", true);
    }

    private static string IllegalDetail(string caseId, string name, IReadOnlySet<string> legal) =>
        $"Illegal move: '{name}' is not legal for {caseId} now. Legal: {string.Join(", ", legal.Order(StringComparer.Ordinal))}.";

    private async Task<string> ReadToolAsync(string caseId, string name, CancellationToken ct)
    {
        if (string.Equals(name, AgentTools.GetCase, StringComparison.Ordinal))
        {
            var file = CaseStore.Load(_cacheDir, caseId)
                ?? throw new InvalidOperationException($"Unknown case {caseId}.");
            return AgentTools.CaseJson(file);
        }

        if (string.Equals(name, AgentTools.GetErpView, StringComparison.Ordinal))
        {
            var file = CaseStore.Load(_cacheDir, caseId)
                ?? throw new InvalidOperationException($"Unknown case {caseId}.");
            return AgentTools.ErpViewJson(await BuildViewAsync(file, ct).ConfigureAwait(false));
        }

        if (string.Equals(name, AgentTools.CheckPrePay, StringComparison.Ordinal))
        {
            var file = CaseStore.Load(_cacheDir, caseId)
                ?? throw new InvalidOperationException($"Unknown case {caseId}.");
            var state = await BuildPrePayStateAsync(file, ct).ConfigureAwait(false);
            var verdicts = await _compliance.EvaluatePaymentAsync(state, ct).ConfigureAwait(false);
            return AgentTools.PrePayJson(state, verdicts);
        }

        return AgentTools.MailJson(EmailStore.Load(_cacheDir, caseId));
    }
}

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
//
// Per-step tools: each request carries only the read tools plus the picked
// step's own tool, so Spark can only reach for what the step allows.
// Two-strikes payment rule: two consecutive DraftPayment turns with no
// executed action draft the payment from the ERP payable by rule instead,
// logged on the timeline, so a flailing agent never strands a payable case.
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
        var refusedStreak = 0;
        var ruleStreak = 0;
        var repeatStreak = 0;
        string? lastIdleSignature = null;
        var idleTurns = 0;
        for (var turn = 1; turn <= MaxToolTurns; turn++)
        {
            // Presentation pace only: while the Admin toggle is on, pause
            // between turns so the activity pane shows each step as it
            // happens. No logic reads this flag; it only adds delay.
            if (turn > 1 && _pace.IsOn)
            {
                await Task.Delay(_pace.StepDelay, ct).ConfigureAwait(false);
            }

            var file = CaseStore.Load(_cacheDir, caseId)
                ?? throw new InvalidOperationException($"Unknown case {caseId}.");
            var view = await BuildViewAsync(file, ct).ConfigureAwait(false);
            var (step, reason) = CasePicker.Pick(file, view, payUpToDate, _split.IsOn);
            _logger.LogInformation(
                "Case {CaseId} at {Stage}: next step {Step} ({Reason}).",
                caseId, file.Case.Stage, step, reason);
            if (step.IsWaiting())
            {
                return new CaseAdvanceOutcome(caseId, file.Case.Stage, step, step == CaseNextStep.Done, reason);
            }

            var legal = CasePicker.LegalTools(file, view, payUpToDate, _split.IsOn);
            // Tool turns need output room: reasoning models can burn a small
            // budget thinking (observed on a pay-vs-email turn) and starve the
            // answer, failing the advance after retry. The budget arrives
            // through the constructor (app config); memo and vision budgets
            // stay on their own values.
            var request = new ModelRequest(
                AgentTools.BuildTurnPrompt(file, view, legal, step),
                AgentTools.SystemPrompt,
                MaxTokens: _turnMaxTokens,
                Tools: AgentTools.Catalog.Where(t => legal.Contains(t.Name)).ToList(),
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
                    string.Empty, [], [], $"Model call failed: {ex.Message}",
                    Model: _models.ModelName));
                throw;
            }

            var calls = result.ToolCalls ?? [];
            if (calls.Count == 0)
            {
                await ExecuteDeferredAsync(caseId, file, view, step, turn, result, history, payUpToDate, ct)
                    .ConfigureAwait(false);
                refusedStreak = 0;
                ruleStreak = 0;
                repeatStreak = 0;
                lastIdleSignature = null;
                idleTurns = 0;
                continue;
            }

            var (acted, blocked) = await ExecuteTurnCallsAsync(
                caseId, file, view, step, turn, result, calls, legal, history, payUpToDate, ct)
                .ConfigureAwait(false);
            refusedStreak = step == CaseNextStep.DraftPayment && !acted ? refusedStreak + 1 : 0;
            if (refusedStreak >= 2)
            {
                refusedStreak = 0;
                ruleStreak = 0;
                repeatStreak = 0;
                lastIdleSignature = null;
                idleTurns = 0;
                await DraftPaymentByRuleAsync(caseId, payUpToDate, ct).ConfigureAwait(false);
            }

            // Rule fallback, any step. Three ways in: three turns in a row
            // with refused calls and no executed action; three no-action
            // turns repeating the identical calls (a repeat read with no
            // intervening action can only return identical facts); or
            // eight no-action turns of any mix. An executed action or a
            // deferred-executed turn resets every count; a reads-only turn
            // resets the refusal count but keeps the idle counts, so
            // alternating reads never outwait the breaker.
            var signature = string.Join(";", calls.Select(c => $"{c.Name}|{c.ArgumentsJson}"));
            if (acted)
            {
                ruleStreak = 0;
                repeatStreak = 0;
                lastIdleSignature = null;
                idleTurns = 0;
            }
            else
            {
                ruleStreak = blocked ? ruleStreak + 1 : 0;
                repeatStreak = signature == lastIdleSignature ? repeatStreak + 1 : 1;
                lastIdleSignature = signature;
                idleTurns++;
            }

            // The split step deliberates over read facts (headroom first,
            // then the plan), so only the refusal breaker applies there;
            // every other step also trips on repeats and long idles.
            var deliberative = step == CaseNextStep.ProposeSplit;
            string? trigger = null;
            if (ruleStreak >= 3)
            {
                trigger = "three refused tries";
            }
            else if (!deliberative && repeatStreak >= 3)
            {
                trigger = "three identical tries with no action";
            }
            else if (!deliberative && idleTurns >= 8)
            {
                trigger = "eight tries with no action";
            }

            if (trigger is not null)
            {
                ruleStreak = 0;
                repeatStreak = 0;
                lastIdleSignature = null;
                idleTurns = 0;
                await FallbackByRuleAsync(caseId, step, payUpToDate, trigger, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Case {caseId} did not settle within {MaxToolTurns} tool turns.");
    }

    private async Task ExecuteDeferredAsync(
        string caseId, CaseFile file, CaseErpView view, CaseNextStep step, int turn,
        ModelResult result, List<ModelMessage> history, DateOnly? payUpToDate, CancellationToken ct)
    {
        var reasoning = result.Text;
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
                [], "Model returned no tool calls.",
                _models.ModelName, result.Usage?.InputTokens, result.Usage?.OutputTokens, result.FromCache));
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
            [summary], "Model-deferred turn.",
            _models.ModelName, result.Usage?.InputTokens, result.Usage?.OutputTokens, result.FromCache));
        history.Add(new ModelMessage("assistant", reasoning));
        history.Add(new ModelMessage("user",
            $"You returned no tool calls, so C# executed the picked step on your behalf: {summary} " +
            "Call tools yourself on the next turn."));
    }

    /// <summary>Runs one turn's calls, returning whether any call acted
    /// and whether any call was refused. Refused turns return no action
    /// so the caller can count consecutive agent failures; execution
    /// errors still abort the advance.</summary>
    private async Task<(bool Acted, bool Blocked)> ExecuteTurnCallsAsync(
        string caseId, CaseFile file, CaseErpView view, CaseNextStep step, int turn,
        ModelResult result, IReadOnlyList<ModelToolCall> calls, IReadOnlySet<string> legal,
        List<ModelMessage> history, DateOnly? payUpToDate, CancellationToken ct)
    {
        var reasoning = result.Text;
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
                if (verdict == ToolCallVerdict.Blocked)
                {
                    RecordRefusedAttempt(caseId, call, detail);
                }
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
            // A reads-only turn is not a refusal: say what to do next
            // instead of pointing at refusals that never happened.
            results.Add(transcriptCalls.Count > 0
                && transcriptCalls.All(c => c.Verdict == ToolCallVerdict.Executed)
                ? "Reads complete; now call the action tool to move the case forward."
                : "No action executed this turn; read the refusals and call a legal tool.");
        }

        CaseTranscript.AppendTurn(_cacheDir, caseId, new TranscriptTurn(
            turn, DateTimeOffset.UtcNow, file.Case.Stage.ToString(), step.ToString(),
            reasoning, transcriptCalls, results,
            turnError is null ? null : $"Turn aborted: {turnError.Message}",
            _models.ModelName, result.Usage?.InputTokens, result.Usage?.OutputTokens, result.FromCache));
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

        return (acted, transcriptCalls.Any(c => c.Verdict == ToolCallVerdict.Blocked));
    }

    /// <summary>Logs a refused tool call on the timeline in plain words so
    /// the activity feed shows what the agent tried, why it was refused,
    /// and (for awards) which vendor it tried: the tried vendor rides on
    /// the document ref so the later award can name what came before it.
    /// The transcript keeps the full reason; the tool name rides along
    /// for filtering.</summary>
    private void RecordRefusedAttempt(string caseId, ModelToolCall call, string detail)
    {
        try
        {
            var file = CaseStore.Load(_cacheDir, caseId);
            if (file is null)
            {
                return;
            }

            var tried = string.Equals(call.Name, AgentTools.AwardSupplier, StringComparison.Ordinal)
                ? AgentTools.ReadStringArg(call.ArgumentsJson, "vendor_id")
                : null;
            var summary = tried is null
                ? $"Tried to {AgentTools.ToPlainVerb(call.Name)}; refused: {detail}"
                : $"Tried to award the supplier to {tried}; refused: {detail}";
            var work = new CaseWork(file);
            work.AddEvent(
                CaseEventKind.AgentRefused,
                summary,
                ruleId: call.Name,
                documentType: tried is null ? null : "Vendor",
                documentNumber: tried);
            work.Save(_cacheDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Case {CaseId} refused-attempt note failed.", caseId);
        }
    }

    /// <summary>Two-strikes payment rule: drafts the payment from the ERP
    /// payable through the C# step both agent and rule share, then logs the
    /// rule draft on the timeline. An ERP refusal still aborts loudly: the
    /// rule path only replaces a flailing agent, never a refusing ERP.</summary>
    private async Task DraftPaymentByRuleAsync(string caseId, DateOnly? payUpToDate, CancellationToken ct)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var view = await BuildViewAsync(file, ct).ConfigureAwait(false);
        try
        {
            await ExecuteStepAsync(file, view, CaseNextStep.DraftPayment, payUpToDate, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Case {CaseId} rule payment draft failed.", caseId);
            RecordFailureNote(caseId, CaseNextStep.DraftPayment, ex);
            throw;
        }

        var work = new CaseWork(CaseStore.Load(_cacheDir, caseId) ?? file);
        work.AddEvent(CaseEventKind.Note, "Payment drafted by rule after two refused agent calls.");
        work.Save(_cacheDir);
        _logger.LogWarning(
            "Case {CaseId}: payment drafted by rule after two refused agent calls.", caseId);
    }

    /// <summary>Three-strikes fallback for any step: C# runs the picked
    /// step through the same path a model deferral uses, then logs the
    /// fallback on the timeline in plain words. At the split step the
    /// fallback is a single-vendor award to the cheapest passing vendor.
    /// An ERP refusal still aborts loudly: the rule path only replaces a
    /// flailing agent, never a refusing ERP.</summary>
    private async Task FallbackByRuleAsync(
        string caseId, CaseNextStep step, DateOnly? payUpToDate, string trigger, CancellationToken ct)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var view = await BuildViewAsync(file, ct).ConfigureAwait(false);
        try
        {
            if (step == CaseNextStep.ProposeSplit)
            {
                await FallbackSplitToSingleAwardAsync(file, ct).ConfigureAwait(false);
            }
            else
            {
                await ExecuteStepAsync(file, view, step, payUpToDate, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Case {CaseId} rule fallback for {Step} failed.", caseId, step);
            RecordFailureNote(caseId, step, ex);
            throw;
        }

        var verb = AgentTools.ToPlainVerb(AgentTools.ToToolName(step) ?? step.ToString());
        var work = new CaseWork(CaseStore.Load(_cacheDir, caseId) ?? file);
        work.AddEvent(CaseEventKind.Note,
            $"After {trigger}, {verb} ran by rule instead.");
        work.Save(_cacheDir);
        _logger.LogWarning(
            "Case {CaseId}: {Step} fell back to the rule path after {Trigger}.", caseId, step, trigger);
    }

    /// <summary>Split-step fallback: awards the full quantity to the
    /// cheapest passing vendor by ERP unit price. Passing vendors
    /// without a price cannot be ordered and are skipped; with no
    /// priced passing vendor the fallback refuses loudly instead of
    /// awarding blind. C# never invents a split on its own.</summary>
    private async Task FallbackSplitToSingleAwardAsync(CaseFile file, CancellationToken ct)
    {
        var current = file.Case;
        if (current.PrNumber is null)
        {
            throw new InvalidOperationException($"Case {current.CaseId} has no PR to award.");
        }

        var raw = await _core.ScreenCandidatesAsync(
            current.PrNumber, ReadCandidates(file, current.ItemId), ct).ConfigureAwait(false);
        var (screenings, passing, _) = CorpExceptionFlow.ApplyApprovedExceptions(file, raw);
        if (passing.Count == 0)
        {
            throw new InvalidOperationException("No compliant vendor: PO refused.");
        }

        string? cheapest = null;
        var best = 0m;
        foreach (var vendorId in passing)
        {
            var price = await _erp.GetVendorPriceAsync(vendorId, current.ItemId, ct).ConfigureAwait(false);
            if (price is null)
            {
                continue;
            }

            if (cheapest is null || price.Value < best)
            {
                cheapest = vendorId;
                best = price.Value;
            }
        }

        if (cheapest is null)
        {
            throw new InvalidOperationException(
                $"No priced passing vendor for case {current.CaseId}: the rule fallback cannot award.");
        }

        await AwardSupplierStepAsync(
            file, cheapest,
            FallbackAwardReason(cheapest, screenings, "Cheapest passing vendor"), ct).ConfigureAwait(false);
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

        // Legality before arguments: a call for a tool the step does not
        // offer is refused with the legal set (steering the next turn),
        // never coached on arguments for a tool it cannot use.
        if (!turnLegal.Contains(call.Name))
        {
            return (ToolCallVerdict.Blocked, IllegalDetail(caseId, call.Name, turnLegal), false);
        }

        var argCase = AgentTools.ReadStringArg(call.ArgumentsJson, "case_id");
        if (!string.Equals(argCase, caseId, StringComparison.Ordinal))
        {
            return (ToolCallVerdict.Blocked,
                $"case_id must be '{caseId}'; got '{argCase ?? "missing"}'.", false);
        }

        if (AgentTools.IsReadTool(call.Name))
        {
            return (ToolCallVerdict.Executed, await ReadToolAsync(caseId, call.Name, ct).ConfigureAwait(false), false);
        }

        // Action: re-validate against freshly loaded state before executing.
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var view = await BuildViewAsync(file, ct).ConfigureAwait(false);
        var (freshStep, freshReason) = CasePicker.Pick(file, view, payUpToDate, _split.IsOn);
        if (freshStep.IsWaiting())
        {
            return (ToolCallVerdict.Blocked,
                $"Case now waits ({freshStep}: {freshReason}); no action runs.", false);
        }

        var freshLegal = CasePicker.LegalTools(file, view, payUpToDate, _split.IsOn);
        if (!freshLegal.Contains(call.Name))
        {
            return (ToolCallVerdict.Blocked, IllegalDetail(caseId, call.Name, freshLegal), false);
        }

        if (string.Equals(call.Name, AgentTools.SendVendorEmail, StringComparison.Ordinal))
        {
            var invoiceRef = AgentTools.ReadStringArg(call.ArgumentsJson, "invoice_ref");
            if (invoiceRef is null || !AgentTools.IsEmailableInvoiceRef(invoiceRef))
            {
                return (ToolCallVerdict.Blocked,
                    $"send_vendor_email needs invoice_ref with an invoice number; got '{invoiceRef ?? "missing"}'. " +
                    "A blank number names no invoice: refused, never emailed or carded.", false);
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

        if (string.Equals(call.Name, AgentTools.ProposeSplit, StringComparison.Ordinal))
        {
            var vendorIds = AgentTools.ReadStringArrayArg(call.ArgumentsJson, "vendor_ids");
            var quantities = AgentTools.ReadNumberArrayArg(call.ArgumentsJson, "quantities");
            if (vendorIds is null || quantities is null)
            {
                return (ToolCallVerdict.Blocked,
                    "propose_split needs vendor_ids and quantities arrays side by side, in PO creation order.", false);
            }

            var note = AgentTools.ReadStringArg(call.ArgumentsJson, "note");
            SplitVerification verification;
            try
            {
                verification = await SplitPlanVerifier.VerifyAsync(
                    _erp, _compliance, file, ReadCandidates(file, file.Case.ItemId),
                    vendorIds, quantities, _countryNames, ct, note).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Case {CaseId} propose_split failed.", caseId);
                RecordFailureNote(caseId, AgentTools.ProposeSplit, ex);
                throw;
            }

            if (verification.Plan is null)
            {
                return (ToolCallVerdict.Blocked,
                    $"Split refused: {string.Join(" ", verification.Errors)}", false);
            }

            Decision card;
            try
            {
                card = await CreateSplitCardAsync(file, verification, note, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Case {CaseId} propose_split card failed.", caseId);
                RecordFailureNote(caseId, AgentTools.ProposeSplit, ex);
                throw;
            }

            return (ToolCallVerdict.Executed,
                $"Split proposed for {file.Case.ItemId}: decision {card.DecisionId} needs {card.RequestedTo}.", true);
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
            var verdicts = await _compliance.EvaluatePayableAsync(state, ct).ConfigureAwait(false);
            return AgentTools.PrePayJson(state, verdicts);
        }

        if (string.Equals(name, AgentTools.GetVendorHeadroom, StringComparison.Ordinal))
        {
            var facts = await _headroomSource.GetHeadroomAsync(caseId, ct).ConfigureAwait(false);
            return facts.ToJson();
        }

        return AgentTools.MailJson(EmailStore.Load(_cacheDir, caseId));
    }
}

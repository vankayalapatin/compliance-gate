// Completes human decisions from the Decisions surface by delegating to the
// runner: one transition implementation for UI and agent (approvals record,
// invoice and payment post, money rejections cancel the case, review
// rejections quarantine, bank verification releases held lines). The UI
// validates, advances after applying, and reports failures with context.
using Gate.Agent;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed record DecisionOutcome(
    bool Ok,
    string Message,
    IReadOnlyList<string> Warnings,
    bool AdvanceFailed = false);

public sealed class DecisionOutcomeService
{
    private readonly CaseCacheStore _cases;
    private readonly ILogger<DecisionOutcomeService> _log;
    private readonly CaseRunner _runner;

    public DecisionOutcomeService(
        CaseCacheStore cases, ILogger<DecisionOutcomeService> log, CaseRunner runner)
    {
        _cases = cases;
        _log = log;
        _runner = runner;
    }

    public async Task<DecisionOutcome> ApplyAsync(
        string caseId, string decisionId, bool approved, string? comment, string actedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new DecisionOutcome(false, "Acted-by user is required; pick the approver first.", []);
        }

        var file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return new DecisionOutcome(false, $"Case {caseId} was not found.", []);
        }

        var decision = file.Decisions.FirstOrDefault(d =>
            string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal));
        if (decision is null)
        {
            return new DecisionOutcome(false, "That decision is no longer on this case.", []);
        }

        if (decision.Status != DecisionStatus.Pending)
        {
            return new DecisionOutcome(false,
                $"{decision.Title} is already {ErpStatusWords.Of(decision.Status).ToLowerInvariant()} (by {ActorLabels.Display(decision.DecidedBy)}).", []);
        }

        Decision updated;
        try
        {
            updated = await _runner.ResolveDecisionAsync(
                caseId, decisionId, approved, actedBy.Trim(), comment, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Decision {DecisionId} failed.", decisionId);
            return new DecisionOutcome(false, $"{ActivityLines.KindWords(decision.Kind)} failed: {ex.Message}", []);
        }

        var warnings = new List<string>();
        var advanceFailed = false;
        try
        {
            var advanced = await _runner.AdvanceAsync(caseId, ct).ConfigureAwait(false);
            warnings.Add(AdvancedLine(advanced));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Advance after decision {DecisionId} failed; the outcome stands.", decisionId);
            warnings.Add($"Decision recorded but the case did not advance: {ex.Message}");
            advanceFailed = true;
        }

        return new DecisionOutcome(
            true,
            $"{updated.Title} {(approved ? "approved" : "rejected")} by {ActorLabels.Display(updated.DecidedBy)}.",
            warnings,
            advanceFailed);
    }

    private static string AdvancedLine(CaseAdvanceOutcome advanced) =>
        $"The case is now {CaseDisplay.StageSummary(advanced.Stage)}; next: {ActivityLines.StepWords(advanced.Step)}.";

    /// <summary>Re-runs the agent advance for a case whose post-decision
    /// advance failed (usually a transient model call). The person asks
    /// explicitly; nothing retries on its own.</summary>
    public async Task<DecisionOutcome> RetryAdvanceAsync(string caseId, CancellationToken ct = default)
    {
        var file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return new DecisionOutcome(false, $"Case {caseId} was not found.", []);
        }

        try
        {
            var advanced = await _runner.AdvanceAsync(caseId, ct).ConfigureAwait(false);
            return new DecisionOutcome(true, AdvancedLine(advanced), []);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Retry advance for case {CaseId} failed.", caseId);
            return new DecisionOutcome(
                true,
                $"Tried again but the case did not advance: {ex.Message}",
                [],
                true);
        }
    }
}

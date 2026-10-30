// Completes human decisions from the Decisions surface by delegating to the
// runner: one transition implementation for UI and agent (approvals record,
// invoice and payment post, money rejections cancel the case, review
// rejections quarantine, bank verification releases held lines). The UI
// validates, advances after applying, and reports failures with context.
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Web.Services;

public sealed record DecisionOutcome(
    bool Ok,
    string Message,
    IReadOnlyList<string> Warnings);

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
            return new DecisionOutcome(false, $"Case {caseId} has no case file.", []);
        }

        var decision = file.Decisions.FirstOrDefault(d =>
            string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal));
        if (decision is null)
        {
            return new DecisionOutcome(false, $"Decision {decisionId} not found in case {caseId}.", []);
        }

        if (decision.Status != DecisionStatus.Pending)
        {
            return new DecisionOutcome(false,
                $"Decision {decisionId} is already {decision.Status} (by {decision.DecidedBy ?? "unknown"}).", []);
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
            return new DecisionOutcome(false, $"{decision.Kind} {decisionId} failed: {ex.Message}", []);
        }

        var warnings = new List<string>();
        try
        {
            var advanced = await _runner.AdvanceAsync(caseId, ct).ConfigureAwait(false);
            warnings.Add($"Case advanced to {advanced.Stage} ({advanced.Step}).");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Advance after decision {DecisionId} failed; the outcome stands.", decisionId);
            warnings.Add($"Decision recorded but the case did not advance: {ex.Message}");
        }

        return new DecisionOutcome(
            true,
            $"{updated.Kind} {updated.DecisionId} {(approved ? "approved" : "rejected")} by {updated.DecidedBy}.",
            warnings);
    }
}

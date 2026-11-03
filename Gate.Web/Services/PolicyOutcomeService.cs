// Completes memo-to-rule human steps from the Policies surface by delegating
// to the memo runner: one transition implementation for UI and agent
// (question answers re-drive the proposal, approvals publish, declines and
// rejections close without publication, rollbacks ship restoring versions).
// The UI validates presence (approver, option, effective, evidence), then
// the runner judges everything else; refusals surface with context.
using Gate.Agent;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed record PolicyOutcome(
    bool Ok,
    string Message,
    IReadOnlyList<string> Warnings);

public sealed class PolicyOutcomeService
{
    private readonly MemoFileStore _memos;
    private readonly MemoRunner _runner;
    private readonly ILogger<PolicyOutcomeService> _log;

    public PolicyOutcomeService(
        MemoFileStore memos, MemoRunner runner, ILogger<PolicyOutcomeService> log)
    {
        _memos = memos;
        _runner = runner;
        _log = log;
    }

    /// <summary>Answers one policy question with the chosen option. The runner
    // verifies the option against the linked round and re-drives the
    // proposal with the answer in context.</summary>
    public async Task<PolicyOutcome> AnswerQuestionAsync(
        string memoId, string decisionId, string? chosenOption, string actedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new PolicyOutcome(false, "Acted-by user is required; pick the approver first.", []);
        }

        var ready = await ReadyDecisionAsync(
            memoId, decisionId, DecisionKind.PolicyQuestion, ct).ConfigureAwait(false);
        if (!ready.Ok)
        {
            return ready.Outcome;
        }

        if (string.IsNullOrWhiteSpace(chosenOption))
        {
            return new PolicyOutcome(false,
                $"Question {decisionId} needs a chosen option; pick one of the offered options first.", []);
        }

        var by = actedBy.Trim();
        try
        {
            var updated = await _runner.ResolvePolicyQuestionAsync(
                memoId, decisionId, true, by, chosenOption, ct).ConfigureAwait(false);
            _log.LogInformation(
                "Policy question {DecisionId} on memo {MemoId} answered '{Option}' by {By}.",
                decisionId, memoId, chosenOption, by);
            return new PolicyOutcome(
                true,
                $"Policy question '{QuestionRef(updated)}' answered '{chosenOption}' by {by}; " +
                "the proposal was re-driven with the answer.",
                ["Refreshed cards show the new round."]);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Policy question {DecisionId} on memo {MemoId} failed.", decisionId, memoId);
            return new PolicyOutcome(false,
                $"Answer failed: {ex.Message} (decision is now {await DecisionStatusAsync(memoId, decisionId, ct).ConfigureAwait(false)}).", []);
        }
    }

    /// <summary>Declines one policy question. A declined block closes the
    // memo: it can never publish. The note records why.</summary>
    public async Task<PolicyOutcome> DeclineQuestionAsync(
        string memoId, string decisionId, string actedBy, string? note = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new PolicyOutcome(false, "Acted-by user is required; pick the approver first.", []);
        }

        var ready = await ReadyDecisionAsync(
            memoId, decisionId, DecisionKind.PolicyQuestion, ct).ConfigureAwait(false);
        if (!ready.Ok)
        {
            return ready.Outcome;
        }

        var by = actedBy.Trim();
        try
        {
            var updated = await _runner.ResolvePolicyQuestionAsync(
                memoId, decisionId, false, by,
                string.IsNullOrWhiteSpace(note) ? null : note!.Trim(), ct).ConfigureAwait(false);
            _log.LogInformation(
                "Policy question {DecisionId} on memo {MemoId} declined by {By}.",
                decisionId, memoId, by);
            return new PolicyOutcome(
                true,
                $"Policy question '{QuestionRef(updated)}' declined by {by}; " +
                "the memo closes without publication.",
                []);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Policy question {DecisionId} on memo {MemoId} failed.", decisionId, memoId);
            return new PolicyOutcome(false, $"Decline failed: {ex.Message}", []);
        }
    }

    /// <summary>Approves or rejects one policy proposal. Approving publishes
    // (parameter update when a conflict names the live rule, brand-new rule
    // otherwise, which needs the approver's title/tier/label); publishing
    // needs at least one passing test name as evidence. Rejecting records
    // the refusal and publishes nothing. Effective datetime is mandatory;
// the engine judges backdating, KB currency, and registry membership.</summary>
    public async Task<PolicyOutcome> DecideApprovalAsync(
        string memoId, string decisionId, bool approved, string actedBy,
        DateTimeOffset? effectiveAt, RuleApplicability applicability,
        IReadOnlyDictionary<string, string>? editedParams, string? comment,
        NewRuleSpec? newRuleSpec = null, IReadOnlyList<string>? testEvidence = null,
        string? packId = null, string? ruleId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new PolicyOutcome(false, "Acted-by user is required; pick the approver first.", []);
        }

        var ready = await ReadyDecisionAsync(
            memoId, decisionId, DecisionKind.PolicyApproval, ct).ConfigureAwait(false);
        if (!ready.Ok)
        {
            return ready.Outcome;
        }

        if (effectiveAt is null)
        {
            return new PolicyOutcome(false,
                "Proposal approval needs an effective datetime; it is mandatory on every approval card.", []);
        }

        var evidence = (testEvidence ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .ToList();
        if (approved && evidence.Count == 0)
        {
            return new PolicyOutcome(false,
                "Approving publishes, and publishing needs at least one passing test name as evidence; " +
                "name the tests covering the rule first.", []);
        }

        var current = ready.File!.Proposals.LastOrDefault();
        if (current is null)
        {
            return new PolicyOutcome(false,
                $"Memo '{memoId}' has no proposal round to decide.", []);
        }

        var by = actedBy.Trim();
        var input = new PolicyApprovalInput(
            approved, by, effectiveAt.Value, applicability,
            string.IsNullOrWhiteSpace(comment) ? null : comment!.Trim(),
            approved ? NormalizeEdits(current.Proposal, editedParams) : null,
            string.IsNullOrWhiteSpace(packId) ? null : packId!.Trim(),
            string.IsNullOrWhiteSpace(ruleId) ? null : ruleId!.Trim(),
            newRuleSpec,
            approved ? evidence : null);
        try
        {
            var outcome = await _runner.ResolvePolicyApprovalAsync(
                memoId, decisionId, input, ct).ConfigureAwait(false);
            if (!approved || outcome.Activation is null)
            {
                _log.LogInformation(
                    "Policy proposal on memo {MemoId} refused by {By}; nothing publishes.",
                    memoId, by);
                return new PolicyOutcome(
                    true,
                    $"Policy proposal '{current.Proposal.ProposalId}' rejected by {by}; nothing publishes.",
                    []);
            }

            var activation = outcome.Activation;
            _log.LogInformation(
                "Policy proposal {ProposalId} approved by {By}; published {RuleId} in pack {PackId}.",
                current.Proposal.ProposalId, by, activation.RuleId, activation.PackId);
            return new PolicyOutcome(
                true,
                $"Policy proposal '{current.Proposal.ProposalId}' approved by {by}; published " +
                $"{activation.RuleId} in pack {activation.PackId} " +
                $"{activation.PriorPackVersion} -> {activation.NewPackVersion}, " +
                $"effective {activation.EffectiveAt:yyyy-MM-dd}.",
                input.EditedParams is null
                    ? []
                    : ["Approver-edited params shipped."]);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Policy approval {DecisionId} on memo {MemoId} failed.", decisionId, memoId);
            return new PolicyOutcome(false, $"Approval failed: {ex.Message}", []);
        }
    }

    /// <summary>Rolls back the memo's latest publication via the runner: a new
    // pack version restoring the named version's parameters. History is
    // untouched. The version to restore and the reason are both required.</summary>
    public async Task<PolicyOutcome> RequestRollbackAsync(
        string memoId, string? restoreVersion, string? reason, string actedBy,
        DateTimeOffset? effectiveAt = null, RuleApplicability? applicability = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new PolicyOutcome(false, "Acted-by user is required; pick the approver first.", []);
        }

        if (string.IsNullOrWhiteSpace(restoreVersion))
        {
            return new PolicyOutcome(false,
                "Rollback needs the version to restore; it is required.", []);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return new PolicyOutcome(false,
                "Rollback needs a recorded reason; it is required.", []);
        }

        var file = await _memos.ReadAsync(memoId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return new PolicyOutcome(false, $"Memo '{memoId}' has no memo file.", []);
        }

        var by = actedBy.Trim();
        try
        {
            var activation = await _runner.RequestRollbackAsync(
                memoId, restoreVersion.Trim(), reason.Trim(), by, ct,
                effectiveAt, applicability).ConfigureAwait(false);
            _log.LogInformation(
                "Pack {PackId} rolled back to {Restore} as {New} per {By}.",
                activation.PackId, restoreVersion.Trim(), activation.NewPackVersion, by);
            return new PolicyOutcome(
                true,
                $"Pack {activation.PackId} rolled back to {restoreVersion.Trim()} " +
                $"as {activation.NewPackVersion} per {by}. History is untouched.",
                []);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rollback on memo {MemoId} failed.", memoId);
            return new PolicyOutcome(false, $"Rollback failed: {ex.Message}", []);
        }
    }

    private sealed record ReadyResult(bool Ok, PolicyOutcome Outcome, MemoFile? File);

    private async Task<ReadyResult> ReadyDecisionAsync(
        string memoId, string decisionId, DecisionKind kind, CancellationToken ct)
    {
        var file = await _memos.ReadAsync(memoId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return new ReadyResult(false,
                new PolicyOutcome(false, $"Memo '{memoId}' has no memo file.", []), null);
        }

        var decision = file.Decisions.FirstOrDefault(d =>
            string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal));
        if (decision is null)
        {
            return new ReadyResult(false,
                new PolicyOutcome(false, $"That decision is no longer on memo '{memoId}'.", []), file);
        }

        if (decision.Kind != kind)
        {
            return new ReadyResult(false,
                new PolicyOutcome(false,
                    $"That card is {ActivityLines.KindWords(decision.Kind)}, not a {ActivityLines.KindWords(kind)} card.", []), file);
        }

        if (decision.Status != DecisionStatus.Pending)
        {
            return new ReadyResult(false,
                new PolicyOutcome(false,
                    $"{decision.Title} is already {ErpStatusWords.Of(decision.Status).ToLowerInvariant()} (by {ActorLabels.Display(decision.DecidedBy)}).", []), file);
        }

        return new ReadyResult(true, new PolicyOutcome(true, string.Empty, []), file);
    }

    private async Task<string> DecisionStatusAsync(string memoId, string decisionId, CancellationToken ct)
    {
        try
        {
            var file = await _memos.ReadAsync(memoId, ct).ConfigureAwait(false);
            var decision = file?.Decisions.FirstOrDefault(d =>
                string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal));
            return decision is null ? "unknown" : ErpStatusWords.Of(decision.Status).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Decision status re-read failed for {DecisionId}.", decisionId);
            return "unknown";
        }
    }

    private static string QuestionRef(Decision decision) =>
        string.IsNullOrWhiteSpace(decision.DocumentNumber)
            ? decision.DecisionId
            : decision.DocumentNumber;

    /// <summary>Null means approved as proposed. An edit map identical to the
    // proposed params normalizes to null; anything else ships to the runner,
    // whose engine judges it against the registry.</summary>
    private static IReadOnlyDictionary<string, string>? NormalizeEdits(
        RuleProposal proposal, IReadOnlyDictionary<string, string>? editedParams)
    {
        if (editedParams is null || proposal.ProposedRule is null)
        {
            return null;
        }

        var proposed = proposal.ProposedRule.Params;
        if (editedParams.Count == proposed.Count
            && editedParams.All(kv =>
                proposed.TryGetValue(kv.Key, out var value)
                && string.Equals(value, kv.Value, StringComparison.Ordinal)))
        {
            return null;
        }

        return new Dictionary<string, string>(editedParams, StringComparer.Ordinal);
    }
}

// Memo-backed view models for the Policies surface. Every value comes from
// the memo files (<MemoId>.memo.json in the case-cache dir) at runtime.
// Unlinked or missing sections surface as explicit card notices; nothing is
// silently omitted. One bad card never hides the rest: per-card failures
// attach to that card with context.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed class PolicyViewService
{
    private readonly MemoFileStore _memos;
    private readonly ILogger<PolicyViewService> _log;

    public PolicyViewService(MemoFileStore memos, ILogger<PolicyViewService> log)
    {
        _memos = memos;
        _log = log;
    }

    public async Task<IReadOnlyList<PolicyQuestionCardVm>> ListQuestionCardsAsync(
        CancellationToken ct = default)
    {
        var files = await _memos.ListAsync(ct).ConfigureAwait(false);
        var cards = new List<PolicyQuestionCardVm>();
        foreach (var file in files)
        {
            foreach (var decision in file.Decisions)
            {
                if (decision.Kind != DecisionKind.PolicyQuestion)
                {
                    continue;
                }

                cards.Add(BuildQuestionCard(file, decision));
            }
        }

        return cards
            .OrderBy(c => c.Decision.Status != DecisionStatus.Pending)
            .ThenBy(c => c.Decision.RequestedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<PolicyApprovalCardVm>> ListApprovalCardsAsync(
        CancellationToken ct = default)
    {
        var files = await _memos.ListAsync(ct).ConfigureAwait(false);
        var cards = new List<PolicyApprovalCardVm>();
        foreach (var file in files)
        {
            foreach (var decision in file.Decisions)
            {
                if (decision.Kind != DecisionKind.PolicyApproval)
                {
                    continue;
                }

                cards.Add(BuildApprovalCard(file, decision));
            }
        }

        return cards
            .OrderBy(c => c.Decision.Status != DecisionStatus.Pending)
            .ThenBy(c => c.Decision.RequestedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<PolicyActivationVm>> ListActivationsAsync(
        CancellationToken ct = default)
    {
        var files = await _memos.ListAsync(ct).ConfigureAwait(false);
        return files
            .SelectMany(file => file.Activations.Select(a =>
                new PolicyActivationVm(
                    file.Memo.MemoId, a, PolicyLabels.SourceLine(a))))
            .OrderByDescending(a => a.Activation.PublishedAt)
            .ThenBy(a => a.Activation.ActivationId, StringComparer.Ordinal)
            .ToList();
    }

    private PolicyQuestionCardVm BuildQuestionCard(MemoFile file, Decision decision)
    {
        var notices = new List<string>();
        try
        {
            var memoId = file.Memo.MemoId;
            var (proposalId, questionId) = SplitQuestionRef(decision.DocumentNumber);
            if (proposalId is null)
            {
                notices.Add(
                    $"Decision names no proposal round (expected '<proposalId>:<questionId>', got " +
                    $"'{decision.DocumentNumber ?? "—"}'). Options below come from the card's evidence " +
                    "refs, unverified.");
                return new PolicyQuestionCardVm(
                    memoId, decision, null,
                    PolicyLabels.OptionsFromEvidence(decision), false, notices);
            }

            var record = file.Proposals.FirstOrDefault(p =>
                string.Equals(p.Proposal.ProposalId, proposalId, StringComparison.Ordinal));
            if (record is null)
            {
                notices.Add(
                    $"Round '{proposalId}' is not in memo '{memoId}' (rounds: {RoundIds(file)}). " +
                    "Options below come from the card's evidence refs, unverified.");
                return new PolicyQuestionCardVm(
                    memoId, decision, null,
                    PolicyLabels.OptionsFromEvidence(decision), false, notices);
            }

            var question = record.Proposal.Questions.FirstOrDefault(q =>
                string.Equals(q.QuestionId, questionId, StringComparison.Ordinal));
            if (question is null)
            {
                notices.Add(
                    $"Round '{proposalId}' has no question '{questionId}' (it carries " +
                    $"{QuestionIds(record.Proposal)}). Options below come from the card's " +
                    "evidence refs, unverified.");
                return new PolicyQuestionCardVm(
                    memoId, decision, null,
                    PolicyLabels.OptionsFromEvidence(decision), false, notices);
            }

            var current = file.Proposals.LastOrDefault();
            if (current is not null
                && !string.Equals(current.Proposal.ProposalId, proposalId, StringComparison.Ordinal))
            {
                notices.Add(
                    $"This card asks about round '{proposalId}' but the current round is " +
                    $"'{current.Proposal.ProposalId}'. A re-proposal may supersede it.");
            }

            if (decision.Status == DecisionStatus.Pending)
            {
                notices.Add(question.Blocking
                    ? PolicyLabels.BlockingWaits
                    : PolicyLabels.AdvisoryQuestion);
            }

            return new PolicyQuestionCardVm(
                memoId, decision, question, question.Options, true, notices);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Policy question card failed for decision {DecisionId}.", decision.DecisionId);
            notices.Add($"Question card failed: {ex.Message}");
            return new PolicyQuestionCardVm(
                file.Memo.MemoId, decision, null, [], false, notices);
        }
    }

    private PolicyApprovalCardVm BuildApprovalCard(MemoFile file, Decision decision)
    {
        var notices = new List<string>();
        try
        {
            var memoId = file.Memo.MemoId;
            var current = file.Proposals.LastOrDefault();
            if (current is null)
            {
                notices.Add(
                    $"Memo '{memoId}' has no proposal rounds yet; there is nothing to approve.");
                return new PolicyApprovalCardVm(
                    memoId, decision, file.Memo, decision.RequestedTo,
                    null, false, null, [], null, false, notices);
            }

            var stale = !string.Equals(
                decision.DocumentNumber, current.Proposal.ProposalHash, StringComparison.Ordinal);
            if (stale)
            {
                notices.Add(
                    $"Stale card: it pins proposal hash '{decision.DocumentNumber}' but the current " +
                    $"round is '{current.Proposal.ProposalId}' ('{current.Proposal.ProposalHash}'). " +
                    "The runner refuses stale cards; the sections below show the current round.");
            }

            var proposal = current.Proposal;
            if (!current.Validation.Ok)
            {
                notices.Add(
                    $"Validation refuses round '{proposal.ProposalId}': " +
                    $"{current.Validation.Errors.Count} error(s) listed below. " +
                    "The runner refuses unapprovable cards.");
            }

            if (current.WhatIf is null)
            {
                notices.Add(proposal.ProposedRule is null
                    ? "What-if is not computed: there is no mappable rule."
                    : $"What-if is not computed yet for round '{proposal.ProposalId}'.");
            }

            MemoRuleTypeSpec? spec = null;
            if (proposal.ProposedRule is not null)
            {
                try
                {
                    spec = MemoRuleRegistry.Find(proposal.ProposedRule.RuleType);
                }
                catch (Exception ex)
                {
                    notices.Add($"Rule-type registry lookup failed: {ex.Message}");
                }
            }
            else if (proposal.NotMappable is not null)
            {
                notices.Add(
                    "NotMappable: there is no rule to publish, so approval is refused. " +
                    "Rejecting closes the memo.");
            }

            var unresolved = proposal.Statements
                .Where(s => !PolicyLabels.IsResolved(s, proposal.Resolutions))
                .ToList();
            var activation = file.Activations.FirstOrDefault(a =>
                string.Equals(a.ProposalHash, proposal.ProposalHash, StringComparison.Ordinal));
            var needsSpec = proposal.ProposedRule is not null
                && !current.Validation.Conflicts.Any(c =>
                    c.Kind is RuleConflictKind.Tightens
                        or RuleConflictKind.Loosens
                        or RuleConflictKind.Subsumes);

            return new PolicyApprovalCardVm(
                memoId, decision, file.Memo, current.PolicyOwner, current, stale,
                spec, unresolved, activation, needsSpec, notices);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Policy approval card failed for decision {DecisionId}.", decision.DecisionId);
            notices.Add($"Approval card failed: {ex.Message}");
            return new PolicyApprovalCardVm(
                file.Memo.MemoId, decision, file.Memo, decision.RequestedTo,
                null, false, null, [], null, false, notices);
        }
    }

    /// <summary>Splits a question card ref on the last ':' (runner
    // FindQuestion shape). A null proposal id means the ref is malformed.</summary>
    private static (string? ProposalId, string QuestionId) SplitQuestionRef(string? docNumber)
    {
        var text = docNumber ?? string.Empty;
        var separator = text.LastIndexOf(':');
        return separator < 0
            ? (null, text)
            : (text[..separator], text[(separator + 1)..]);
    }

    private static string RoundIds(MemoFile file) =>
        file.Proposals.Count == 0
            ? "(none)"
            : $"[{string.Join(", ", file.Proposals.Select(p => p.Proposal.ProposalId))}]";

    private static string QuestionIds(RuleProposal proposal) =>
        proposal.Questions.Count == 0
            ? "(none)"
            : $"[{string.Join(", ", proposal.Questions.Select(q => q.QuestionId))}]";
}

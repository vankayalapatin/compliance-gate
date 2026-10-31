// Memo next-step picker. The memo runner picks each next step from memo
// state only; no fixed step sequence lives in code. Pick is a pure function
// of the memo file. Current means the last proposal round: older rounds are
// superseded context, and only the current round's open blocks gate it.
using Gate.Contracts;

namespace Gate.Agent;

/// <summary>Every step the picker can choose, plus the waiting states it
/// parks in and the terminal Done.</summary>
public enum MemoNextStep
{
    Propose,
    ComputeWhatIf,
    RaiseApproval,
    AwaitAnswers,
    AwaitApproval,
    Done
}

public static class MemoNextStepExtensions
{
    public static bool IsWaiting(this MemoNextStep step) => step switch
    {
        MemoNextStep.AwaitAnswers => true,
        MemoNextStep.AwaitApproval => true,
        MemoNextStep.Done => true,
        _ => false
    };
}

public static class MemoPicker
{
    public const string QuestionDocumentType = "PolicyQuestion";
    public const string ApprovalDocumentType = "PolicyApproval";

    /// <summary>Picks the next step for the memo. A rejected proposal, a
    /// refused approval, or a published activation is Done; anything needing
    /// a policy owner returns a waiting step naming it; otherwise the next
    /// runnable agent step.</summary>
    public static (MemoNextStep Step, string Reason) Pick(MemoFile file)
    {
        var current = file.Proposals.LastOrDefault();
        if (current is null)
        {
            return (MemoNextStep.Propose, "Memo ingested; no proposal yet.");
        }

        if (!current.Validation.Ok)
        {
            return (MemoNextStep.Done,
                $"Proposal {current.Proposal.ProposalId} was rejected by validation; " +
                "the rejection is recorded on the timeline.");
        }

        if (CurrentBlocking(file, current).Count > 0)
        {
            return (MemoNextStep.AwaitAnswers,
                "Blocking policy questions are open; the memo waits and nothing publishes.");
        }

        if (CurrentDeclined(file, current).Count > 0)
        {
            return (MemoNextStep.Done,
                "A blocking policy question was declined; the memo is closed without publication.");
        }

        if (current.Proposal.ProposedRule is not null && current.WhatIf is null)
        {
            return (MemoNextStep.ComputeWhatIf,
                "Proposal validated with no open blocks; the what-if report is not computed yet.");
        }

        var approval = CurrentApproval(file, current);
        if (approval is null)
        {
            return (MemoNextStep.RaiseApproval,
                current.Proposal.ProposedRule is null
                    ? "NotMappable proposal with no open blocks; raise the close-out card for the policy owner."
                    : "Validated proposal with its what-if report; raise the approval card for the policy owner.");
        }

        return approval.Status switch
        {
            DecisionStatus.Pending => (MemoNextStep.AwaitApproval,
                "Policy approval is pending with the policy owner."),
            DecisionStatus.Approved => PublishedFor(file, current) is not null
                ? (MemoNextStep.Done,
                    $"Proposal {current.Proposal.ProposalId} is published as {PublishedFor(file, current)!.RuleId}.")
                : (MemoNextStep.Done,
                    "Approval recorded without a publication; the publish step refused and the refusal is on the timeline."),
            _ => (MemoNextStep.Done,
                $"Proposal {current.Proposal.ProposalId} was refused by {approval.DecidedBy ?? "the policy owner"}."),
        };
    }

    /// <summary>Pending blocking-question cards on the current round. Only
    /// these gate approval: older rounds' cards are superseded.</summary>
    public static IReadOnlyList<Decision> CurrentBlocking(MemoFile file, MemoProposalRecord current)
    {
        var blockingIds = current.Proposal.Questions
            .Where(q => q.Blocking)
            .Select(q => QuestionDocNumber(current.Proposal.ProposalId, q.QuestionId))
            .ToHashSet(StringComparer.Ordinal);
        return file.Decisions
            .Where(d => d.Kind == DecisionKind.PolicyQuestion
                && d.Status == DecisionStatus.Pending
                && string.Equals(d.DocumentType, QuestionDocumentType, StringComparison.Ordinal)
                && d.DocumentNumber is not null
                && blockingIds.Contains(d.DocumentNumber))
            .ToList();
    }

    /// <summary>Declined (rejected) blocking-question cards on the current
    /// round. A declined block closes the memo: it can never publish.</summary>
    public static IReadOnlyList<Decision> CurrentDeclined(MemoFile file, MemoProposalRecord current)
    {
        var prefix = current.Proposal.ProposalId + ":";
        return file.Decisions
            .Where(d => d.Kind == DecisionKind.PolicyQuestion
                && d.Status == DecisionStatus.Rejected
                && string.Equals(d.DocumentType, QuestionDocumentType, StringComparison.Ordinal)
                && (d.DocumentNumber ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
    }

    public static Decision? CurrentApproval(MemoFile file, MemoProposalRecord current) =>
        file.Decisions.FirstOrDefault(d =>
            d.Kind == DecisionKind.PolicyApproval
            && string.Equals(d.DocumentType, ApprovalDocumentType, StringComparison.Ordinal)
            && string.Equals(d.DocumentNumber, current.Proposal.ProposalHash, StringComparison.Ordinal));

    public static RuleActivation? PublishedFor(MemoFile file, MemoProposalRecord current) =>
        file.Activations.FirstOrDefault(a =>
            string.Equals(a.ProposalHash, current.Proposal.ProposalHash, StringComparison.Ordinal));

    public static string QuestionDocNumber(string proposalId, string questionId) =>
        $"{proposalId}:{questionId}";
}

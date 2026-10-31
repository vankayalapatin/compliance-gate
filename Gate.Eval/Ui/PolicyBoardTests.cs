// Pure Policies-board logic: per-user filtering of question and approval
// cards plus decided-by-me history. Test data is local and never depends
// on the demo numbers. No live calls.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Eval.Ui;

public sealed class PolicyBoardTests
{
    private static Decision Decision(
        string decisionId, DecisionKind kind, string requestedTo,
        DecisionStatus status, string? decidedBy = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Decision(
            decisionId, "MEMO-T", kind, "Test policy card", status,
            requestedTo, now, decidedBy,
            decidedBy is null ? null : now,
            kind == DecisionKind.PolicyQuestion ? "PolicyQuestion" : "PolicyApproval",
            "ref", null, null, null, null, [], null);
    }

    private static PolicyQuestionCardVm Question(
        string decisionId, string requestedTo, DecisionStatus status, string? decidedBy = null) =>
        new("MEMO-T", Decision(decisionId, DecisionKind.PolicyQuestion, requestedTo, status, decidedBy),
            null, ["A"], false, []);

    private static PolicyApprovalCardVm Approval(
        string decisionId, string requestedTo, DecisionStatus status, string? decidedBy = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new PolicyApprovalCardVm(
            "MEMO-T", Decision(decisionId, DecisionKind.PolicyApproval, requestedTo, status, decidedBy),
            new PolicyMemo("MEMO-T", now, "Board", "Excerpt."),
            requestedTo, null, false, null, [], null, false, []);
    }

    [Fact]
    public void PendingQuestionsFilterExactRequesteeOnly()
    {
        var cards = new[]
        {
            Question("D-1", "u-me", DecisionStatus.Pending),
            Question("D-2", "u-other", DecisionStatus.Pending),
            Question("D-3", "u-me", DecisionStatus.Approved, "u-me"),
        };

        var pending = PolicyBoard.PendingQuestionsFor(cards, "u-me");

        Assert.Equal("D-1", Assert.Single(pending).Decision.DecisionId);
    }

    [Fact]
    public void PendingApprovalsFilterExactRequesteeOnly()
    {
        var cards = new[]
        {
            Approval("D-1", "u-me", DecisionStatus.Pending),
            Approval("D-2", "u-other", DecisionStatus.Pending),
            Approval("D-3", "u-me", DecisionStatus.Rejected, "u-me"),
        };

        var pending = PolicyBoard.PendingApprovalsFor(cards, "u-me");

        Assert.Equal("D-1", Assert.Single(pending).Decision.DecisionId);
    }

    [Fact]
    public void DecidedQuestionsShowMineNewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var mine = (string id, DateTimeOffset at) => Question(id, "u-me", DecisionStatus.Approved, "u-me")
            with { Decision = Question(id, "u-me", DecisionStatus.Approved, "u-me").Decision with { DecidedAt = at } };
        var cards = new[]
        {
            mine("D-old", now.AddHours(-2)),
            Question("D-pending", "u-me", DecisionStatus.Pending),
            Question("D-theirs", "u-other", DecisionStatus.Approved, "u-other"),
            mine("D-new", now),
        };

        var history = PolicyBoard.DecidedQuestionsBy(cards, "u-me");

        Assert.Equal(["D-new", "D-old"], history.Select(c => c.Decision.DecisionId).ToList());
    }

    [Fact]
    public void DecidedApprovalsShowMineNewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var mine = (string id, DateTimeOffset at) =>
        {
            var card = Approval(id, "u-me", DecisionStatus.Rejected, "u-me");
            return card with { Decision = card.Decision with { DecidedAt = at } };
        };
        var cards = new[]
        {
            mine("D-old", now.AddHours(-2)),
            Approval("D-pending", "u-me", DecisionStatus.Pending),
            Approval("D-theirs", "u-other", DecisionStatus.Approved, "u-other"),
            mine("D-new", now),
        };

        var history = PolicyBoard.DecidedApprovalsBy(cards, "u-me");

        Assert.Equal(["D-new", "D-old"], history.Select(c => c.Decision.DecisionId).ToList());
    }

    [Fact]
    public void EmptyBoardsStayEmpty()
    {
        Assert.Empty(PolicyBoard.PendingQuestionsFor([], "u-me"));
        Assert.Empty(PolicyBoard.PendingApprovalsFor([], "u-me"));
        Assert.Empty(PolicyBoard.DecidedQuestionsBy([], "u-me"));
        Assert.Empty(PolicyBoard.DecidedApprovalsBy([], "u-me"));
    }
}

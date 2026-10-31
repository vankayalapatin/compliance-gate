// Pure Decisions-board logic: default-user pick, delegation coverage, and
// per-user filtering. Test data is local and never depends on demo numbers.
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Eval.Ui;

public sealed class DecisionBoardTests
{
    private static UserDto User(string id, string role) => new(id, role, 1000m, "TST");

    private static DecisionCardVm Card(
        string decisionId, string requestedTo, DecisionStatus status, string? decidedBy = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new DecisionCardVm(
            new Decision(
                decisionId, "CASE-T", DecisionKind.PrApproval, "Test decision", status,
                requestedTo, now, decidedBy,
                decidedBy is null ? null : now,
                "PR", "PR-T", 100m, "TST", null, null, [], null),
            "SO-T", "ITEM-T", CaseStage.Created, [], [], null);
    }

    [Fact]
    public void DefaultPrefersApproverRole()
    {
        var users = new[] { User("u-req", "requester"), User("u-app", "approver"), User("u-fin", "payment-approver") };

        Assert.Equal("u-app", DecisionBoard.PickDefaultUserId(users));
    }

    [Fact]
    public void DefaultFallsBackToFirstById()
    {
        var users = new[] { User("u-b", "requester"), User("u-a", "delegate") };

        Assert.Equal("u-a", DecisionBoard.PickDefaultUserId(users));
    }

    [Fact]
    public void DefaultWithNoUsersIsNull()
    {
        Assert.Null(DecisionBoard.PickDefaultUserId([]));
    }

    [Fact]
    public void CoveringFindsActiveDelegation()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var delegations = new[] { new DelegationDto("u-from", "u-to", today.AddDays(-1), today.AddDays(1)) };

        Assert.Equal<string>(["u-from"], DecisionBoard.CoveringFrom(delegations, "u-to", today));
    }

    [Fact]
    public void CoveringIncludesBoundaryDays()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var delegations = new[]
        {
            new DelegationDto("u-start", "u-to", today, today.AddDays(5)),
            new DelegationDto("u-end", "u-to", today.AddDays(-5), today),
        };

        Assert.Equal<string>(["u-end", "u-start"], DecisionBoard.CoveringFrom(delegations, "u-to", today));
    }

    [Fact]
    public void CoveringIgnoresExpiredFutureAndForeign()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var delegations = new[]
        {
            new DelegationDto("u-old", "u-to", today.AddDays(-10), today.AddDays(-1)),
            new DelegationDto("u-new", "u-to", today.AddDays(1), today.AddDays(10)),
            new DelegationDto("u-other", "u-else", today.AddDays(-1), today.AddDays(1)),
        };

        Assert.Empty(DecisionBoard.CoveringFrom(delegations, "u-to", today));
    }

    [Fact]
    public void PendingFiltersExactRequesteeOnly()
    {
        var cards = new[]
        {
            Card("D-1", "u-me", DecisionStatus.Pending),
            Card("D-2", "u-other", DecisionStatus.Pending),
            Card("D-3", "u-me", DecisionStatus.Approved, "u-me"),
        };

        var pending = DecisionBoard.PendingFor(cards, "u-me");

        Assert.Equal("D-1", Assert.Single(pending).Decision.DecisionId);
    }

    [Fact]
    public void HistoryShowsDecidedByMe()
    {
        var cards = new[]
        {
            Card("D-1", "u-me", DecisionStatus.Pending),
            Card("D-2", "u-me", DecisionStatus.Approved, "u-me"),
            Card("D-3", "u-other", DecisionStatus.Rejected, "u-other"),
        };

        var history = DecisionBoard.DecidedBy(cards, "u-me");

        Assert.Equal("D-2", Assert.Single(history).Decision.DecisionId);
    }
}

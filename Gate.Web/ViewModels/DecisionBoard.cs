// Pure Decisions-board logic: default-user pick, delegation coverage, and
// per-user card filtering. No IO here; every value arrives as arguments
// from ERP reads (users, delegations) and the case cache (cards).
using Gate.Contracts;

namespace Gate.Web.ViewModels;

public static class DecisionBoard
{
    /// <summary>Picks the default acting user: the approver-role user when
    /// one exists, else the first user by id. Null when ERP has no users.
    /// The role literal mirrors the agent router and scenario loader.</summary>
    public static string? PickDefaultUserId(IReadOnlyList<UserDto> users)
    {
        var approver = users
            .Where(u => string.Equals(u.Role, "approver", StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.UserId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (approver is not null)
        {
            return approver.UserId;
        }

        return users
            .OrderBy(u => u.UserId, StringComparer.Ordinal)
            .FirstOrDefault()?.UserId;
    }

    /// <summary>Active coverage: distinct FromUser ids whose delegation to the
    /// acting user covers today (inclusive bounds), mirroring the ERP
    /// approval check. Empty when the user covers nobody.</summary>
    public static IReadOnlyList<string> CoveringFrom(
        IReadOnlyList<DelegationDto> delegations, string actingUser, DateOnly today) =>
        delegations
            .Where(d => string.Equals(d.ToUser, actingUser, StringComparison.OrdinalIgnoreCase)
                && today >= d.Start && today <= d.End)
            .Select(d => d.FromUser)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToList();

    /// <summary>Pending cards routed exactly to the acting user. Routing
    /// already resolves delegation, so this is an exact match.</summary>
    public static IReadOnlyList<DecisionCardVm> PendingFor(
        IReadOnlyList<DecisionCardVm> cards, string actingUser) =>
        cards
            .Where(c => c.Decision.Status == DecisionStatus.Pending
                && string.Equals(c.Decision.RequestedTo, actingUser, StringComparison.Ordinal))
            .OrderBy(c => c.Decision.RequestedAt)
            .ToList();

    /// <summary>Decided cards the acting user resolved, newest first.</summary>
    public static IReadOnlyList<DecisionCardVm> DecidedBy(
        IReadOnlyList<DecisionCardVm> cards, string actingUser) =>
        cards
            .Where(c => c.Decision.Status != DecisionStatus.Pending
                && string.Equals(c.Decision.DecidedBy, actingUser, StringComparison.Ordinal))
            .OrderByDescending(c => c.Decision.DecidedAt ?? c.Decision.RequestedAt)
            .ToList();
}

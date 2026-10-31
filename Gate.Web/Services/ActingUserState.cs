// Circuit-scoped acting-user selection shared by the shell switcher and the
// Decisions board. The switcher writes (ERP users plus local-storage
// restore); Decisions reads and re-filters on change. No business values.
namespace Gate.Web.Services;

public sealed class ActingUserState
{
    private string? _currentUserId;

    public string? CurrentUserId => _currentUserId;

    public event Action? OnChange;

    public void SetCurrent(string? userId)
    {
        if (string.Equals(_currentUserId, userId, StringComparison.Ordinal))
        {
            return;
        }

        _currentUserId = userId;
        OnChange?.Invoke();
    }
}

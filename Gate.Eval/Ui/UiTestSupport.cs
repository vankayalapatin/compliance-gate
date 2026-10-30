// Shared UI-test support: temp directories, a no-op logger, and small
// case-file builders. Test data is local to these tests and never depends
// on the demo numbers.
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

internal sealed class UiTestLogger<T> : ILogger<T>
{
    public static readonly UiTestLogger<T> Instance = new();

    IDisposable? ILogger.BeginScope<TState>(TState state) => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }
}

internal static class UiTestSupport
{
    public static string NewDir(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));

    public static CaseFile CaseFileWith(
        string caseId, string soNumber, string itemId,
        IReadOnlyList<Decision>? decisions = null,
        Case? kase = null)
    {
        var now = DateTimeOffset.UtcNow;
        var c = kase ?? new Case(
            caseId, soNumber, itemId, "ui-test", CaseStage.Created, now, now,
            null, null, null, null, [], [], null, null);
        return new CaseFile(c, [], decisions ?? []);
    }

    public static Decision PendingDecision(
        string decisionId, string caseId,
        DecisionKind kind = DecisionKind.PrApproval,
        string? documentType = "PR",
        string? documentNumber = "PR-TEST") =>
        new(decisionId, caseId, kind, "Test decision", DecisionStatus.Pending,
            "approver", DateTimeOffset.UtcNow, null, null,
            documentType, documentNumber, 100m, "TST",
            null, null, [], null);
}

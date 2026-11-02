// Agent activity feed: builds the right-side pane from existing case
// events and decisions plus vendor names for plain language. No new data
// or storage: every line renders from what cases already recorded. Any
// single failure degrades to an honest placeholder line (the pane sits on
// every page and must never break one); failures are logged, never
// swallowed. Render rules live in ActivityLines; this class only reads.
using Gate.Agent;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed class AgentActivityService
{
    private const int RecentTake = 15;
    private const string IdleNow = "Idle.";
    private static readonly TimeSpan ActiveWithin = TimeSpan.FromSeconds(90);

    private readonly CaseCacheStore _cases;
    private readonly IErpAdapter _erp;
    private readonly CaseRunner _runner;
    private readonly ILogger<AgentActivityService> _log;

    public AgentActivityService(
        CaseCacheStore cases, IErpAdapter erp, CaseRunner runner, ILogger<AgentActivityService> log)
    {
        _cases = cases;
        _erp = erp;
        _runner = runner;
        _log = log;
    }

    public async Task<AgentActivityVm> BuildAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CaseFile> files;
        try
        {
            files = await _cases.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Activity pane could not list cases.");
            return new AgentActivityVm(IdleNow, [], []);
        }

        var vendors = await VendorNamesAsync(ct).ConfigureAwait(false);
        var now = NowLine(files);
        return new AgentActivityVm(
            now,
            await WaitingRowsAsync(files, vendors, string.Equals(now, IdleNow, StringComparison.Ordinal), ct)
                .ConfigureAwait(false),
            RecentLines(files, vendors));
    }

    private async Task<Dictionary<string, string>> VendorNamesAsync(CancellationToken ct)
    {
        try
        {
            return (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
                .ToDictionary(v => v.VendorId, v => v.Name, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Activity pane could not list vendors; ids render raw.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string NowLine(IReadOnlyList<CaseFile> files)
    {
        var newest = files
            .SelectMany(f => f.Events.Select(e => (File: f, Event: e)))
            .Where(x => x.Event.Kind != CaseEventKind.Note)
            .OrderByDescending(x => x.Event.Timestamp)
            .FirstOrDefault();
        if (newest.Event is null || DateTimeOffset.UtcNow - newest.Event.Timestamp > ActiveWithin)
        {
            return IdleNow;
        }

        return ActivityLines.Cap($"{ActivityLines.NowVerb(newest.Event.Kind)} for order {newest.File.Case.SoNumber}.");
    }

    private async Task<IReadOnlyList<ActivityWaitingRow>> WaitingRowsAsync(
        IReadOnlyList<CaseFile> files, Dictionary<string, string> vendors, bool nowIdle, CancellationToken ct)
    {
        var rows = new List<ActivityWaitingRow>();
        var open = files
            .Where(f => f.Case.Stage is not CaseStage.Resolved and not CaseStage.Closed and not CaseStage.Cancelled)
            .OrderBy(f => f.Case.SoNumber, StringComparer.Ordinal)
            .ThenBy(f => f.Case.ItemId, StringComparer.Ordinal)
            .ToList();
        foreach (var file in open)
        {
            CaseNextStep step;
            try
            {
                (step, _) = await _runner.NextStepAsync(file.Case.CaseId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Activity pane could not read the next step for {CaseId}.", file.Case.CaseId);
                rows.Add(new ActivityWaitingRow(
                    ActivityLines.Cap($"Order {file.Case.SoNumber}: status unavailable."), $"cases/{file.Case.CaseId}"));
                continue;
            }

            rows.Add(ActivityLines.WaitingRow(file, step, vendors, nowIdle));
        }

        return rows;
    }

    private static IReadOnlyList<ActivityRecentLine> RecentLines(
        IReadOnlyList<CaseFile> files, Dictionary<string, string> vendors)
    {
        var rows = new List<(DateTimeOffset At, string Text)>();
        foreach (var file in files)
        {
            foreach (var e in file.Events)
            {
                var line = ActivityLines.EventLine(file, e, vendors);
                if (line is not null)
                {
                    rows.Add((e.Timestamp, line));
                }
            }

            foreach (var d in file.Decisions)
            {
                var line = ActivityLines.DecidedLine(vendors, d);
                if (line is not null)
                {
                    rows.Add((d.DecidedAt ?? d.RequestedAt, line));
                }
            }
        }

        return rows
            .OrderByDescending(r => r.At)
            .Take(RecentTake)
            .Select(r => new ActivityRecentLine(ActivityLines.Cap(r.Text), ActivityLines.Ago(r.At)))
            .ToList();
    }
}

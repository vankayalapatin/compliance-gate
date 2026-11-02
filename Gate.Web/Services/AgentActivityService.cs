// Agent activity feed: builds the right-side pane from existing case
// events and decisions plus vendor names and item descriptions for plain
// language. No new data or storage: every line renders from what cases
// already recorded. Any single failure degrades to an honest placeholder
// line (the pane sits on every page and must never break one); failures
// are logged, never swallowed. Render rules live in ActivityLines; this
// class only reads.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
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
    private readonly CountryNames? _countries;

    public AgentActivityService(
        CaseCacheStore cases, IErpAdapter erp, CaseRunner runner, ILogger<AgentActivityService> log,
        CountryNames? countries = null)
    {
        _cases = cases;
        _erp = erp;
        _runner = runner;
        _log = log;
        _countries = countries;
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

        var lookups = await LookupsAsync(files, ct).ConfigureAwait(false);
        var now = NowLine(files, lookups);
        return new AgentActivityVm(
            now,
            await WaitingRowsAsync(files, lookups, string.Equals(now, IdleNow, StringComparison.Ordinal), ct)
                .ConfigureAwait(false),
            await RecentGroupsAsync(files, lookups, ct).ConfigureAwait(false));
    }

    private async Task<ActivityLookups> LookupsAsync(IReadOnlyList<CaseFile> files, CancellationToken ct)
    {
        Dictionary<string, string> vendors;
        try
        {
            vendors = (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
                .ToDictionary(v => v.VendorId, v => v.Name, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Activity pane could not list vendors; ids render raw.");
            vendors = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var items = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var itemId in files.Select(f => f.Case.ItemId).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var item = await _erp.GetItemAsync(itemId, ct).ConfigureAwait(false);
                if (item is not null)
                {
                    items[itemId] = item.Description;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Activity pane could not read item {ItemId}; the id renders raw.", itemId);
            }
        }

        decimal? threshold;
        try
        {
            threshold = await _erp.GetAutoApproveThresholdAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Activity pane could not read the auto-approve threshold; policy lines omit it.");
            threshold = null;
        }

        return new ActivityLookups(vendors, items, threshold);
    }

    private static string NowLine(IReadOnlyList<CaseFile> files, ActivityLookups lookups)
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

        return ActivityLines.Cap(
            $"{ActivityLines.CaseHeader(newest.File, lookups)}: {ActivityLines.NowVerb(newest.Event.Kind)}.");
    }

    private async Task<IReadOnlyList<ActivityWaitingRow>> WaitingRowsAsync(
        IReadOnlyList<CaseFile> files, ActivityLookups lookups, bool nowIdle, CancellationToken ct)
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
                    ActivityLines.Cap($"{ActivityLines.CaseHeader(file, lookups)}: status unavailable."),
                    $"cases/{file.Case.CaseId}"));
                continue;
            }

            var poHint = step == CaseNextStep.AwaitSupplierAck
                ? await PoHintAsync(file, lookups, ct).ConfigureAwait(false)
                : null;
            rows.Add(ActivityLines.WaitingRow(file, step, lookups, nowIdle, poHint));
        }

        return rows;
    }

    /// <summary>Latest purchase-order approval outcome for a supplier-ack
    /// wait: the named approver behind a decided card, else the policy
    /// auto-approval with its threshold, else the ERP approver of record.
    /// Null when the case has no order or the outcome is unreadable.</summary>
    private async Task<string?> PoHintAsync(CaseFile file, ActivityLookups lookups, CancellationToken ct)
    {
        var poNumber = file.Case.PoNumber;
        if (poNumber is null)
        {
            return null;
        }

        var decided = file.Decisions
            .Where(d => d.Kind == DecisionKind.PoApproval
                && d.Status != DecisionStatus.Pending
                && string.Equals(d.DocumentNumber, poNumber, StringComparison.Ordinal))
            .MaxBy(d => d.DecidedAt ?? d.RequestedAt);
        if (decided?.DecidedBy is not null)
        {
            var verdict = decided.Status == DecisionStatus.Approved ? "approved" : "rejected";
            return $"Purchase order {poNumber} {verdict} by {ActorLabels.Display(decided.DecidedBy)}.";
        }

        try
        {
            var po = await _erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false);
            if (po?.ApprovedBy is null)
            {
                return null;
            }

            if (string.Equals(po.ApprovedBy, ErpApprovalActors.AutoApprovePolicy, StringComparison.Ordinal))
            {
                return lookups.AutoApproveThreshold is { } threshold
                    ? $"Purchase order {poNumber} auto-approved (under {ActivityLines.Money(threshold, null)})."
                    : $"Purchase order {poNumber} auto-approved by policy.";
            }

            return $"Purchase order {poNumber} approved by {ActorLabels.Display(po.ApprovedBy)}.";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Activity pane could not read purchase order {Po} for its approval hint.", poNumber);
            return null;
        }
    }

    private async Task<IReadOnlyList<ActivityRecentGroup>> RecentGroupsAsync(
        IReadOnlyList<CaseFile> files, ActivityLookups lookups, CancellationToken ct)
    {
        var mathByFile = new Dictionary<string, IReadOnlyList<OriginCapLine>>(StringComparer.Ordinal);
        var rows = new List<(CaseFile File, DateTimeOffset At, string Text, ActivitySide Side, bool Refused, string Href)>();
        foreach (var file in files)
        {
            var caseHref = $"cases/{file.Case.CaseId}";
            foreach (var e in file.Events)
            {
                var line = ActivityLines.EventLine(file, e, lookups);
                if (line is null)
                {
                    continue;
                }

                var href = caseHref;
                if (e.Kind == CaseEventKind.VendorBlocked
                    && string.Equals(e.RuleId, OriginCapMath.OriginCapRuleId, StringComparison.Ordinal))
                {
                    var math = await MathForAsync(file, mathByFile, ct).ConfigureAwait(false);
                    var vendorId = OriginCapMath.TryParseBlockedVendor(e.Summary);
                    var hit = vendorId is null ? null : math.FirstOrDefault(m =>
                        string.Equals(m.VendorId, vendorId, StringComparison.Ordinal));
                    if (hit is not null)
                    {
                        line = hit.Line.PlainText;
                        href = hit.Line.HistoryHref;
                    }
                }

                rows.Add((file, e.Timestamp, line, ActivitySide.Agent,
                    e.Kind == CaseEventKind.AgentRefused, href));
            }

            foreach (var d in file.Decisions)
            {
                var line = ActivityLines.DecidedLine(lookups, d);
                if (line is not null)
                {
                    rows.Add((file, d.DecidedAt ?? d.RequestedAt, line, ActivitySide.Person, false, caseHref));
                }
            }
        }

        return rows
            .OrderByDescending(r => r.At)
            .Take(RecentTake)
            .GroupBy(r => r.File.Case.CaseId, StringComparer.Ordinal)
            .OrderByDescending(g => g.Max(r => r.At))
            .Select(g =>
            {
                var file = g.First().File;
                var caseHref = $"cases/{file.Case.CaseId}";
                return new ActivityRecentGroup(
                    ActivityLines.CaseHeader(file, lookups),
                    caseHref,
                    g.OrderByDescending(r => r.At)
                        .Select(r => new ActivityRecentLine(
                            ActivityLines.Cap(r.Text), ActivityLines.Ago(r.At), r.At, r.Side, r.Refused, r.Href))
                        .ToList());
            })
            .ToList();
    }

    /// <summary>Screening-math lines for one file, computed once and shared
    /// by its blocked-vendor entries. Failures log and yield nothing: the
    /// plain block line stays as the fallback.</summary>
    private async Task<IReadOnlyList<OriginCapLine>> MathForAsync(
        CaseFile file,
        Dictionary<string, IReadOnlyList<OriginCapLine>> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(file.Case.CaseId, out var lines))
        {
            return lines;
        }

        try
        {
            lines = await OriginCapExplainer.LinesAsync(_erp, file, _log, _countries?.Map, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Activity pane could not build screening math for {CaseId}.", file.Case.CaseId);
            lines = [];
        }

        cache[file.Case.CaseId] = lines;
        return lines;
    }
}

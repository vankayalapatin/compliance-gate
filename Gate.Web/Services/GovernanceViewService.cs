// Rules and Exceptions views for the Governance area. Live pack rules
// (with pack owner, adoption date, and public source) and the current KB
// version arrive through the engine seam; block counts come from the
// case-cache scan; company rows join memo-file activations to their
// proposals by proposal hash.
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed record GovernanceRulesVm(
    IReadOnlyList<GovernanceRuleRow> Rows,
    string CurrentKbVersion,
    string ScanLabel,
    string DockNote,
    IReadOnlyList<string> Notes);

public sealed class GovernanceViewService
{
    private readonly IComplianceEngine _compliance;
    private readonly CaseCacheStore _cases;
    private readonly MemoFileStore _memos;
    private readonly ILogger<GovernanceViewService> _log;

    public GovernanceViewService(
        IComplianceEngine compliance, CaseCacheStore cases, MemoFileStore memos,
        ILogger<GovernanceViewService> log)
    {
        _compliance = compliance;
        _cases = cases;
        _memos = memos;
        _log = log;
    }

    public async Task<GovernanceRulesVm> ListRuleRowsAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();
        IReadOnlyList<LivePackRule> live;
        string currentKb;
        try
        {
            live = await _compliance.ListLivePackRulesAsync(ct).ConfigureAwait(false);
            currentKb = (await _compliance.LoadKbSnapshotAsync(ct).ConfigureAwait(false)).KbVersion;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Governance rules failed to load from the engine seam.");
            throw new InvalidOperationException($"Governance rules failed to load: {ex.Message}", ex);
        }

        IReadOnlyList<CaseFile> caseFiles;
        IReadOnlyList<Agent.MemoFile> memoFiles;
        try
        {
            caseFiles = await _cases.ListAsync(ct).ConfigureAwait(false);
            memoFiles = await _memos.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Governance rules cache scan failed.");
            throw new InvalidOperationException($"Governance rules cache scan failed: {ex.Message}", ex);
        }

        var scan = RuleBlockScan.CountBlocks(caseFiles);
        IReadOnlyList<GovernanceRuleRow> rows;
        try
        {
            rows = GovernanceRuleBoard.BuildRows(live, memoFiles, scan.Counts, currentKb);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Governance rules board failed to build from seam data.");
            throw new InvalidOperationException($"Governance rules board failed to build: {ex.Message}", ex);
        }

        return new GovernanceRulesVm(
            rows, currentKb,
            GovernanceLabels.ScanLabel(scan.FilesScanned), scan.DockNote, notes);
    }

    public async Task<IReadOnlyList<ExceptionRowVm>> ListExceptionsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CaseFile> caseFiles;
        try
        {
            caseFiles = await _cases.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Governance exceptions scan failed.");
            throw new InvalidOperationException($"Governance exceptions scan failed: {ex.Message}", ex);
        }

        return ExceptionBoard.Rows(caseFiles);
    }
}

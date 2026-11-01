// KB reads for the Governance area. Every KB value flows through the
// IComplianceEngine seam only (spaces, current plus pinned snapshots,
// live rules): this service never touches KB files, the loader, or the
// writer. One bad entry never hides its space: per-entry failures attach
// to that entry's notes with context.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed record KbSpaceVm(
    KbSpace Space,
    string CurrentVersion,
    IReadOnlyList<string> Sections,
    IReadOnlyDictionary<string, IReadOnlyList<KbEntryVm>> EntriesBySection,
    IReadOnlyList<string> Notes);

public sealed record KbEntryDetailVm(
    KbEntryVm Entry,
    string SnapshotVersion,
    string CurrentVersion,
    KbUsedByVm UsedBy,
    IReadOnlyList<string> Notes);

public sealed class KbViewService
{
    private readonly IComplianceEngine _compliance;
    private readonly ILogger<KbViewService> _log;

    /// <summary>Upper bound on the version probe: versions are kbv1..kbvN
    // without gaps, so the first unknown version ends the probe and the
    // cap only bounds a pathological store.</summary>
    public const int MaxVersionProbe = 500;

    public KbViewService(IComplianceEngine compliance, ILogger<KbViewService> log)
    {
        _compliance = compliance;
        _log = log;
    }

    public async Task<IReadOnlyList<KbSpace>> ListSpacesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _compliance.ListKbSpacesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Governance KB spaces failed to load.");
            throw new InvalidOperationException($"KB spaces failed to load: {ex.Message}", ex);
        }
    }

    public async Task<KbSnapshot> LoadCurrentAsync(CancellationToken ct = default)
    {
        try
        {
            return await _compliance.LoadKbSnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Governance current KB snapshot failed to load.");
            throw new InvalidOperationException($"Current KB snapshot failed to load: {ex.Message}", ex);
        }
    }

    public async Task<KbSnapshot> LoadAtAsync(string version, CancellationToken ct = default)
    {
        try
        {
            return await _compliance.LoadKbSnapshotAtAsync(version, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Governance KB snapshot {Version} failed to load.", version);
            throw new InvalidOperationException($"KB snapshot '{version}' failed to load: {ex.Message}", ex);
        }
    }

    /// <summary>Known KB versions, oldest first, probed seam-only from kbv1
    // upward until the first unknown version refuses.</summary>
    public async Task<IReadOnlyList<string>> ListVersionsAsync(CancellationToken ct = default)
    {
        var versions = new List<string>();
        for (var n = 1; n <= MaxVersionProbe; n++)
        {
            ct.ThrowIfCancellationRequested();
            var version = $"{CompanyKbVersions.Prefix}{n}";
            try
            {
                var snapshot = await _compliance.LoadKbSnapshotAtAsync(version, ct).ConfigureAwait(false);
                versions.Add(snapshot.KbVersion);
            }
            catch (Exception ex)
            {
                _log.LogInformation(
                    ex, "Governance KB version probe stopped at {Version} ({Count} known).",
                    version, versions.Count);
                break;
            }
        }

        return versions;
    }

    /// <summary>One space view: the space plus every section's entries from
    // the current snapshot. Unknown spaces refuse with the known list.</summary>
    public async Task<KbSpaceVm> BuildSpaceAsync(
        string space, IReadOnlyList<MemoFile>? memoFiles = null, CancellationToken ct = default)
    {
        _ = memoFiles;
        var spaces = await ListSpacesAsync(ct).ConfigureAwait(false);
        var match = spaces.FirstOrDefault(s =>
            string.Equals(s.Space, space, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new InvalidOperationException(
                $"Unknown knowledge space '{space}'. Known spaces: {string.Join(", ", spaces.Select(s => s.Space))}.");
        }

        var snapshot = await LoadCurrentAsync(ct).ConfigureAwait(false);
        var notes = new List<string>();
        var entries = new Dictionary<string, IReadOnlyList<KbEntryVm>>(StringComparer.Ordinal);
        foreach (var section in match.Sections)
        {
            try
            {
                entries[section] = KbEntries.FromSnapshot(snapshot, section);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "KB space {Space} section {Section} failed.", space, section);
                notes.Add($"Section '{section}' failed: {ex.Message}");
                entries[section] = [];
            }
        }

        return new KbSpaceVm(match, snapshot.KbVersion, match.Sections, entries, notes);
    }

    /// <summary>One pinned entry view: the entry at its cited version plus
    // its used-by index. Unknown versions and entries refuse loudly.</summary>
    public async Task<KbEntryDetailVm> FindEntryAsync(
        string version, string section, string entryId,
        IReadOnlyList<MemoFile> memoFiles, CancellationToken ct = default)
    {
        var snapshot = await LoadAtAsync(version, ct).ConfigureAwait(false);
        var current = await LoadCurrentAsync(ct).ConfigureAwait(false);
        var notes = new List<string>();
        if (!string.Equals(snapshot.KbVersion, version, StringComparison.Ordinal))
        {
            notes.Add($"Asked for '{version}'; the seam returned '{snapshot.KbVersion}'.");
        }

        IReadOnlyList<KbEntryVm> rows;
        try
        {
            rows = KbEntries.FromSnapshot(snapshot, section);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"KB '{snapshot.KbVersion}' section '{section}' failed: {ex.Message}", ex);
        }

        var entry = rows.FirstOrDefault(e =>
            string.Equals(e.EntryId, entryId, StringComparison.Ordinal));
        if (entry is null)
        {
            throw new InvalidOperationException(
                $"KB '{snapshot.KbVersion}' section '{section}' carries no entry '{entryId}' " +
                $"({rows.Count} entr(y/ies) present).");
        }

        if (!string.Equals(snapshot.KbVersion, current.KbVersion, StringComparison.Ordinal))
        {
            notes.Add(
                $"Pinned at {snapshot.KbVersion}; current is {current.KbVersion}. " +
                "Grounding may be stale.");
        }

        var usedBy = UsedByIndex.FindUsedBy(section, entryId, memoFiles);
        return new KbEntryDetailVm(entry, snapshot.KbVersion, current.KbVersion, usedBy, notes);
    }
}

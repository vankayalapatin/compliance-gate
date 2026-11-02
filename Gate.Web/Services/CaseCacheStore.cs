// UI-side case-cache access. Byte-exact shared convention with the agent
// lane: directory is GATE_CASE_CACHE else <content-root>/data/case-cache,
// one file per case named <CaseId>.json holding the CaseFile record via
// System.Text.Json with JsonSerializerDefaults.Web. Single-user demo,
// last-writer-wins: callers re-read on every page load and before applying
// a decision outcome. No business values here; all ids arrive at runtime.
using System.Text.Json;
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Web.Services;

public sealed class CaseCacheStore
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _dir;

    public CaseCacheStore(string cacheDir)
    {
        _dir = cacheDir;
    }

    public string CacheDir => _dir;

    public static string ResolveDir(string contentRoot) =>
        Environment.GetEnvironmentVariable("GATE_CASE_CACHE")
        ?? Path.Combine(contentRoot, "data", "case-cache");

    public async Task<IReadOnlyList<CaseFile>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_dir))
        {
            return [];
        }

        // Mail files (<CaseId>.mail.json), vendor-identity pendings
        // (<CaseId>.vendor-identity.json), dock evidence sidecars
        // (<CaseId>.dock.json), and memo files (<MemoId>.memo.json) share
        // the dir but are not cases (mirrors the agent CaseStore
        // exclusions; memo files belong to MemoFileStore).
        var files = Directory.GetFiles(_dir, "*.json")
            .Where(f => !f.EndsWith(".mail.json", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith(".vendor-identity.json", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith(".dock.json", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith(".memo.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Array.Sort(files, StringComparer.Ordinal);
        var result = new List<CaseFile>(files.Length);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<CaseFile>(json, JsonOpts)
                ?? throw new InvalidOperationException($"Case cache file did not parse: {file}.");
            result.Add(parsed);
        }

        return result;
    }

    public async Task<CaseFile?> ReadAsync(string caseId, CancellationToken ct = default)
    {
        var file = Path.Combine(_dir, caseId + ".json");
        if (!File.Exists(file))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<CaseFile>(json, JsonOpts)
            ?? throw new InvalidOperationException($"Case cache file did not parse: {file}.");
    }

    public async Task WriteAsync(CaseFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(file, JsonOpts);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, file.Case.CaseId + ".json"), json, ct).ConfigureAwait(false);
    }

    /// <summary>Reads one case's dock-evidence sidecar, or null when the
    /// case recorded no dock evidence yet. Small local file; sync read.</summary>
    public Task<DockEvidence?> ReadDockAsync(string caseId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(CaseStore.LoadDock(_dir, caseId));
    }

    /// <summary>Deletes every cached case file. Returns the cleared count.</summary>
    public Task<int> ClearAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_dir))
        {
            return Task.FromResult(0);
        }

        var files = Directory.GetFiles(_dir, "*.json");
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(file);
        }

        return Task.FromResult(files.Length);
    }
}

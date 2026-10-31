// UI-side memo-file access. Memo files live next to the case files in the
// case-cache dir (agent MemoStore convention): one file per board memo named
// <MemoId>.memo.json holding the MemoFile record (memo, proposal rounds,
// decisions, timeline, activations) via System.Text.Json with
// JsonSerializerDefaults.Web. The memo runner owns all writes; the app only
// reads (tests seed via WriteAsync). No business values here.
using System.Text.Json;
using Gate.Agent;

namespace Gate.Web.Services;

public sealed class MemoFileStore
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _dir;

    public MemoFileStore(string cacheDir)
    {
        _dir = cacheDir;
    }

    public string CacheDir => _dir;

    /// <summary>Memo files share the case-cache dir; one resolver for both.</summary>
    public static string ResolveDir(string contentRoot) =>
        CaseCacheStore.ResolveDir(contentRoot);

    public async Task<IReadOnlyList<MemoFile>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_dir))
        {
            return [];
        }

        var files = Directory.GetFiles(_dir, "*.memo.json");
        Array.Sort(files, StringComparer.Ordinal);
        var result = new List<MemoFile>(files.Length);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<MemoFile>(json, JsonOpts)
                ?? throw new InvalidOperationException($"Memo file did not parse: {file}.");
            result.Add(parsed);
        }

        return result;
    }

    public async Task<MemoFile?> ReadAsync(string memoId, CancellationToken ct = default)
    {
        var file = Path.Combine(_dir, memoId + ".memo.json");
        if (!File.Exists(file))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<MemoFile>(json, JsonOpts)
            ?? throw new InvalidOperationException($"Memo file did not parse: {file}.");
    }

    /// <summary>Test seeding only. App writes go through MemoRunner, the one
    // transition implementation for memo files.</summary>
    public async Task WriteAsync(MemoFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(file, JsonOpts);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, file.Memo.MemoId + ".memo.json"), json, ct).ConfigureAwait(false);
    }
}

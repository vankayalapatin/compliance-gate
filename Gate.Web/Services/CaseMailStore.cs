// UI-side vendor-mail-thread access. Same directory and web JSON options
// as the case files: one file per case named <CaseId>.mail.json holding
// the CaseMail record. Missing file means no thread (null), never an
// error. No business values here; all ids arrive at runtime.
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Web.Services;

public sealed class CaseMailStore
{
    private readonly string _dir;

    public CaseMailStore(string cacheDir)
    {
        _dir = cacheDir;
    }

    public string CacheDir => _dir;

    public async Task<CaseMail?> ReadAsync(string caseId, CancellationToken ct = default)
    {
        var file = Path.Combine(_dir, caseId + ".mail.json");
        if (!File.Exists(file))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<CaseMail>(json, CaseCacheStore.JsonOpts)
            ?? throw new InvalidOperationException($"Case mail file did not parse: {file}.");
    }

    public async Task WriteAsync(CaseMail mail, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(mail, CaseCacheStore.JsonOpts);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, mail.CaseId + ".mail.json"), json, ct).ConfigureAwait(false);
    }
}

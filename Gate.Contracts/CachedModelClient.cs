// File-backed response cache. Identical requests are served from disk so a
// paid call never runs twice. Cache entries are keyed by SHA256 over the
// model name plus normalized prompts.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gate.Contracts;

public sealed class CachedModelClient : IModelClient
{
    private readonly IModelClient _inner;
    private readonly string _cacheDir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public CachedModelClient(IModelClient inner, string cacheDir)
    {
        _inner = inner;
        _cacheDir = cacheDir;
    }

    public string ModelName => _inner.ModelName;

    public static string CacheKey(string modelName, ModelRequest request)
    {
        var normalized = modelName + "\n" + (request.SystemPrompt ?? string.Empty) + "\n" + request.Prompt;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public async Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_cacheDir);
        var path = Path.Combine(_cacheDir, CacheKey(ModelName, request) + ".json");
        if (File.Exists(path))
        {
            var cached = JsonSerializer.Deserialize<CachedEntry>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
            if (cached is not null)
            {
                return new ModelResult(cached.Text);
            }
        }

        var result = await _inner.CompleteAsync(request, ct).ConfigureAwait(false);
        var entry = new CachedEntry(ModelName, request.Prompt, result.Text, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry, JsonOpts), ct).ConfigureAwait(false);
        return result;
    }

    private sealed record CachedEntry(string Model, string Prompt, string Text, DateTimeOffset CachedAt);
}

// Vision cache behavior: round-trip, version miss, loud invalidation on a
// tampered hash, and zero-live-call replay through the real file cache.
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class VisionCacheTests
{
    private static string NewDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteThenReadRoundTrips()
    {
        var cache = new VisionReadCache(NewDir("gate-vision-cache"));
        var hash = VisionFiles.Sha256Hex([1, 2, 3]);
        cache.Write(hash, VisionReadCache.CartonReadV1, "probe-model", "CTN-01,BAT-1");

        var entry = cache.TryRead(hash, VisionReadCache.CartonReadV1);
        Assert.NotNull(entry);
        Assert.Equal("CTN-01,BAT-1", entry.Text);
        Assert.Equal("probe-model", entry.Model);
        Assert.Equal(hash, entry.ImageSha256);
    }

    [Fact]
    public void DifferentPromptVersionMisses()
    {
        var cache = new VisionReadCache(NewDir("gate-vision-cache"));
        var hash = VisionFiles.Sha256Hex([4, 5, 6]);
        cache.Write(hash, VisionReadCache.CartonReadV1, "probe-model", "read");

        Assert.Null(cache.TryRead(hash, VisionReadCache.InvoiceReadV1));
    }

    [Fact]
    public void TamperedHashInvalidatesLoudly()
    {
        var dir = NewDir("gate-vision-cache");
        var cache = new VisionReadCache(dir);
        var hash = VisionFiles.Sha256Hex([7, 8, 9]);
        cache.Write(hash, VisionReadCache.CartonReadV1, "probe-model", "read");

        var file = Path.Combine(dir, VisionReadCache.EntryFileName(hash, VisionReadCache.CartonReadV1));
        File.WriteAllText(file, File.ReadAllText(file).Replace(hash, new string('0', hash.Length)));

        var boom = Assert.Throws<InvalidOperationException>(
            () => cache.TryRead(hash, VisionReadCache.CartonReadV1));
        Assert.Contains("mismatch", boom.Message);
    }

    [Fact]
    public void TamperedTextInvalidatesLoudly()
    {
        var dir = NewDir("gate-vision-cache");
        var cache = new VisionReadCache(dir);
        var hash = VisionFiles.Sha256Hex([10, 11, 12]);
        cache.Write(hash, VisionReadCache.CartonReadV1, "probe-model", "read");

        // Tamper the cached text while keeping the recorded ResultHash stale:
        // the key still matches, so only result verification can catch this.
        var file = Path.Combine(dir, VisionReadCache.EntryFileName(hash, VisionReadCache.CartonReadV1));
        var stored = JsonSerializer.Deserialize<VisionReadEntry>(File.ReadAllText(file));
        Assert.NotNull(stored);
        File.WriteAllText(file, JsonSerializer.Serialize(stored with { Text = "tampered-read" }));

        var boom = Assert.Throws<InvalidOperationException>(
            () => cache.TryRead(hash, VisionReadCache.CartonReadV1));
        Assert.Contains("mismatch", boom.Message);
    }

    [Fact]
    public async Task CachedReplayMakesZeroLiveCalls()
    {
        // A pre-seeded file-cache entry served through the real
        // CachedModelClient never reaches the replay-guarded inner client.
        var dir = NewDir("gate-vision-replay");
        Directory.CreateDirectory(dir);
        var inner = new ReplayOnlyModelClient();
        var cached = new CachedModelClient(inner, dir);
        var request = new ModelRequest("transcribe carton labels", VisionReadCache.CartonReadV1);
        var seeded = """
            {"Model":"replay-only","Prompt":"transcribe carton labels","Text":"CTN-01,BAT-1","CachedAt":"2026-01-01T00:00:00Z"}
            """;
        File.WriteAllText(
            Path.Combine(dir, CachedModelClient.CacheKey(inner.ModelName, request) + ".json"), seeded);

        var result = await cached.CompleteAsync(request);
        Assert.Equal("CTN-01,BAT-1", result.Text);
    }

    [Fact]
    public async Task ReplayGuardThrowsOnDirectCall()
    {
        var inner = new ReplayOnlyModelClient();
        var boom = await Assert.ThrowsAsync<InvalidOperationException>(
            () => inner.CompleteAsync(new ModelRequest("anything")));
        Assert.Contains("cache only", boom.Message);
    }
}

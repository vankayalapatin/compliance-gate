// The response cache: identical requests never call the model twice.
using Gate.Contracts;

namespace Gate.Eval;

public sealed class CacheTests
{
    [Fact]
    public async Task SamePromptHitsCache()
    {
        var inner = new FakeModelClient();
        inner.QueueResponse("cached-answer");
        var dir = Path.Combine(Path.GetTempPath(), "gate-cache-test", Guid.NewGuid().ToString("N"));
        var cached = new CachedModelClient(inner, dir);
        var request = new ModelRequest("What is the lithium mark?", "You are a test.");

        var first = await cached.CompleteAsync(request);
        var second = await cached.CompleteAsync(request);

        Assert.Equal("cached-answer", first.Text);
        Assert.Equal(first.Text, second.Text);
        Assert.Equal(1, inner.CallCount);
        Assert.True(File.Exists(Path.Combine(dir, CachedModelClient.CacheKey(inner.ModelName, request) + ".json")));
    }

    [Fact]
    public async Task DifferentPromptMissesCache()
    {
        var inner = new FakeModelClient();
        inner.QueueResponse("one");
        inner.QueueResponse("two");
        var dir = Path.Combine(Path.GetTempPath(), "gate-cache-test", Guid.NewGuid().ToString("N"));
        var cached = new CachedModelClient(inner, dir);

        await cached.CompleteAsync(new ModelRequest("first?"));
        await cached.CompleteAsync(new ModelRequest("second?"));

        Assert.Equal(2, inner.CallCount);
    }
}

// Model test doubles: throwing, flaky, and empty clients for the retry and
// offline-replay proofs.
using Gate.Contracts;

namespace Gate.Eval;

internal sealed class ThrowingModelClient : IModelClient
{
    // Same model identity as the warmed FakeModelClient: the cache key covers
    // the model name, so replay hits warmed entries instead of missing them.
    public string ModelName => "fake";

    public int CallCount { get; private set; }

    public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        CallCount++;
        throw new HttpRequestException("Live calls are forbidden on replay.");
    }
}

internal sealed class FlakyModelClient : IModelClient
{
    private readonly string _text;

    public FlakyModelClient(string text) => _text = text;

    public string ModelName => "flaky";

    public int CallCount { get; private set; }

    public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        CallCount++;
        if (CallCount == 1)
        {
            throw new HttpRequestException("First attempt fails.");
        }

        return Task.FromResult(new ModelResult(_text));
    }
}

internal sealed class EmptyModelClient : IModelClient
{
    public string ModelName => "empty";

    public int CallCount { get; private set; }

    public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(new ModelResult("  "));
    }
}

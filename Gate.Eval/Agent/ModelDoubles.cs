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

/// <summary>Scripted tool-loop double: returns queued results in order and
/// records every request (prompt, tools, history) for choice-proof
/// assertions. An empty queue throws instead of echoing, so tests prove
/// exact call counts.</summary>
internal sealed class RecordingModelClient : IModelClient
{
    private readonly Queue<ModelResult> _results = new();

    public RecordingModelClient(string modelName = "fake") => ModelName = modelName;

    public string ModelName { get; }

    public int CallCount { get; private set; }

    public List<ModelRequest> Requests { get; } = new();

    public void QueueToolCalls(string reasoning, params (string Id, string Name, string Args)[] calls) =>
        _results.Enqueue(new ModelResult(
            reasoning, calls.Select(c => new ModelToolCall(c.Id, c.Name, c.Args)).ToList()));

    public void QueueText(string text) => _results.Enqueue(new ModelResult(text));

    public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        _ = ct;
        CallCount++;
        Requests.Add(request);
        if (_results.Count == 0)
        {
            throw new InvalidOperationException("No scripted response left.");
        }

        return Task.FromResult(_results.Dequeue());
    }
}

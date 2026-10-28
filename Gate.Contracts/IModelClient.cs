// Model-calling surface. The agent depends on this interface only, so cached,
// fake, and live Spark clients are interchangeable.
namespace Gate.Contracts;

public sealed record ModelRequest(
    string Prompt,
    string? SystemPrompt = null,
    /// <summary>Cap for this call. Zero means the client default
    /// (SparkOptions.MaxTokens, 2000 for the live client).</summary>
    int MaxTokens = 0);

public sealed record ModelResult(string Text);

public interface IModelClient
{
    string ModelName { get; }
    Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default);
}

/// <summary>Deterministic test double. Returns the queued response per call.</summary>
public sealed class FakeModelClient : IModelClient
{
    private readonly Queue<string> _responses = new();
    public string ModelName => "fake";
    public int CallCount { get; private set; }

    public void QueueResponse(string text) => _responses.Enqueue(text);

    public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        CallCount++;
        var text = _responses.Count > 0 ? _responses.Dequeue() : $"echo: {request.Prompt}";
        return Task.FromResult(new ModelResult(text));
    }
}

// Model-calling surface. The agent depends on this interface only, so cached,
// fake, and live Spark clients are interchangeable. Single-turn text and
// image calls behave exactly as before; tool loops add Tools plus History
// and read ToolCalls back.
namespace Gate.Contracts;

public sealed record ModelRequest(
    string Prompt,
    string? SystemPrompt = null,
    /// <summary>Cap for this call. Zero means the client default
    /// (SparkOptions.MaxTokens, 2000 for the live client).</summary>
    int MaxTokens = 0,
    /// <summary>Optional image bytes for vision reads. Null means text-only.
    /// MimeType defaults to image/jpeg when bytes are present.</summary>
    byte[]? ImageJpeg = null,
    string? ImageMimeType = null,
    /// <summary>Function definitions the model may call. Null means a plain
    /// text completion.</summary>
    IReadOnlyList<ModelTool>? Tools = null,
    /// <summary>Prior turns, oldest first: user, assistant (text plus tool
    /// calls), and tool results. Null means a fresh conversation.</summary>
    IReadOnlyList<ModelMessage>? History = null);

public sealed record ModelResult(
    string Text,
    /// <summary>Tool calls the model requested, in order. Null or empty
    /// means a plain text answer.</summary>
    IReadOnlyList<ModelToolCall>? ToolCalls = null);

public interface IModelClient
{
    string ModelName { get; }
    Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default);
}

/// <summary>Deterministic test double. Returns the queued response per call.</summary>
public sealed class FakeModelClient : IModelClient
{
    private readonly Queue<string> _responses = new();
    private readonly Queue<ModelResult> _results = new();
    public string ModelName => "fake";
    public int CallCount { get; private set; }

    public void QueueResponse(string text) => _responses.Enqueue(text);

    public void QueueResult(ModelResult result) => _results.Enqueue(result);

    public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        CallCount++;
        if (_results.Count > 0)
        {
            return Task.FromResult(_results.Dequeue());
        }

        var text = _responses.Count > 0 ? _responses.Dequeue() : $"echo: {request.Prompt}";
        return Task.FromResult(new ModelResult(text));
    }
}

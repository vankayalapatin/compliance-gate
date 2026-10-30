// Live Muse Spark client over the Meta model API (OpenAI-compatible chat
// completions). The API key arrives via SparkOptions, which the host binds
// from user secrets or environment — never from a file in the repo.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gate.Contracts;

public sealed class SparkOptions
{
    public string BaseUrl { get; set; } = "https://api.meta.ai/v1";
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Default cap when the request does not set one. Reasoning models
    /// burn hundreds of tokens before answering; keep this at 2000 unless costs demand less.</summary>
    public int MaxTokens { get; set; } = 2000;
}

public sealed class SparkModelClient : IModelClient
{
    private readonly HttpClient _http;
    private readonly SparkOptions _options;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public SparkModelClient(HttpClient http, SparkOptions options)
    {
        _http = http;
        _options = options;
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "Meta API key is missing. Set it with: dotnet user-secrets set \"MetaApi:ApiKey\" \"<key>\" (in Gate.Web).");
        }
    }

    public string ModelName => _options.Model;

    public async Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl.TrimEnd('/') + "/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var maxTokens = request.MaxTokens > 0 ? request.MaxTokens : _options.MaxTokens;
        message.Content = JsonContent.Create(new ChatRequest(
            _options.Model,
            BuildMessages(request),
            maxTokens), options: WebJson);

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(WebJson, cancellationToken: ct).ConfigureAwait(false);
        var choice = body?.Choices is { Length: > 0 } ? body.Choices[0] : null;
        var text = choice?.Message?.Content;
        if (string.IsNullOrEmpty(text))
        {
            if (string.Equals(choice?.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Spark hit the token limit ({maxTokens} max tokens): reasoning used the budget before any answer. Raise MetaApi:MaxTokens.");
            }

            throw new InvalidOperationException("Spark returned an empty completion.");
        }

        return new ModelResult(text);
    }

    private static ChatMessage[] BuildMessages(ModelRequest request)
    {
        object userContent = request.ImageJpeg is null
            ? request.Prompt
            : new object[]
            {
                new TextPart("text", request.Prompt),
                new ImagePart("image_url", new ImageUrl(
                    $"data:{request.ImageMimeType ?? "image/jpeg"};base64,{Convert.ToBase64String(request.ImageJpeg)}"))
            };
        if (string.IsNullOrEmpty(request.SystemPrompt))
        {
            return [new ChatMessage("user", userContent)];
        }

        return [new ChatMessage("system", request.SystemPrompt), new ChatMessage("user", userContent)];
    }

    private sealed record ChatRequest(
        string Model,
        ChatMessage[] Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens);

    private sealed record ChatMessage(string Role, object Content);

    private sealed record TextPart(string Type, string Text);

    private sealed record ImagePart(
        string Type,
        [property: JsonPropertyName("image_url")] ImageUrl ImageUrl);

    private sealed record ImageUrl(string Url);

    private sealed record ChatResponse(ChatChoice[]? Choices);

    private sealed record ChatChoice(
        ChatResponseMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChatResponseMessage(string? Content);
}

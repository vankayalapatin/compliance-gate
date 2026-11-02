// Live Muse Spark client over the Meta model API (OpenAI-compatible chat
// completions). The API key arrives via SparkOptions, which the host binds
// from user secrets or environment — never from a file in the repo.
// Plain text and vision calls serialize exactly as before; tool loops add
// function definitions plus conversation history and read tool calls back.
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>Per-attempt timeout for memo proposal calls, in seconds.
    /// Memo prompts carry the full KB snapshot, so they get their own
    /// budget instead of the 2-minute case default.</summary>
    public int MemoTimeoutSeconds { get; set; } = 240;
}

public sealed class SparkModelClient : IModelClient
{
    private readonly HttpClient _http;
    private readonly SparkOptions _options;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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
            maxTokens,
            request.Tools?.Select(t => new ChatToolDef("function", new ChatFunctionDef(
                t.Name, t.Description, ToolParameters(t.Parameters)))).ToList()),
            options: RequestJson);

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(WebJson, cancellationToken: ct).ConfigureAwait(false);
        var choice = body?.Choices is { Length: > 0 } ? body.Choices[0] : null;
        var text = choice?.Message?.Content;
        var calls = choice?.Message?.ToolCalls?.Select((c, i) => new ModelToolCall(
            c.Id ?? $"call-{i}",
            c.Function?.Name ?? string.Empty,
            c.Function?.Arguments ?? "{}")).ToList();
        if (string.IsNullOrEmpty(text) && (calls is null || calls.Count == 0))
        {
            if (string.Equals(choice?.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Spark hit the token limit ({maxTokens} max tokens): reasoning used the budget before any answer. Raise MetaApi:MaxTokens.");
            }

            throw new InvalidOperationException("Spark returned an empty completion.");
        }

        var usage = body?.Usage is null
            ? null
            : new ModelUsage(
                body.Usage.PromptTokens ?? 0,
                body.Usage.CompletionTokens ?? 0);
        return new ModelResult(
            text ?? string.Empty,
            calls is null || calls.Count == 0 ? null : calls,
            usage);
    }

    private static ChatMessage[] BuildMessages(ModelRequest request)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new ChatMessage("system", request.SystemPrompt));
        }

        if (request.History is not null)
        {
            foreach (var turn in request.History)
            {
                messages.Add(new ChatMessage(
                    turn.Role,
                    turn.Text ?? string.Empty,
                    turn.ToolCalls?.Select(c => new ChatToolCall(
                        c.Id, "function", new ChatFunctionCall(c.Name, c.ArgumentsJson))).ToList(),
                    turn.ToolCallId));
            }
        }

        object userContent = request.ImageJpeg is null
            ? request.Prompt
            : new object[]
            {
                new TextPart("text", request.Prompt),
                new ImagePart("image_url", new ImageUrl(
                    $"data:{request.ImageMimeType ?? "image/jpeg"};base64,{Convert.ToBase64String(request.ImageJpeg)}"))
            };
        messages.Add(new ChatMessage("user", userContent));
        return messages.ToArray();
    }

    private static JsonObject ToolParameters(IReadOnlyList<ModelToolParameter> parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in parameters)
        {
            var schema = new JsonObject
            {
                ["type"] = p.IsArray ? "array" : JsonType(p.Type),
                ["description"] = p.Description,
            };
            if (p.IsArray)
            {
                schema["items"] = new JsonObject { ["type"] = JsonType(p.Type) };
            }

            properties[p.Name] = schema;
            if (p.Required)
            {
                required.Add(p.Name);
            }
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };
    }

    private static string JsonType(ModelToolParameterType type) => type switch
    {
        ModelToolParameterType.String => "string",
        ModelToolParameterType.Number => "number",
        ModelToolParameterType.Integer => "integer",
        ModelToolParameterType.Boolean => "boolean",
        _ => "string",
    };

    private sealed record ChatRequest(
        string Model,
        ChatMessage[] Messages,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        IReadOnlyList<ChatToolDef>? Tools = null);

    private sealed record ChatMessage(
        string Role,
        object? Content,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<ChatToolCall>? ToolCalls = null,
        [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

    private sealed record ChatToolCall(
        string Id,
        string Type,
        [property: JsonPropertyName("function")] ChatFunctionCall Function);

    private sealed record ChatFunctionCall(string Name, string Arguments);

    private sealed record ChatToolDef(
        string Type,
        [property: JsonPropertyName("function")] ChatFunctionDef Function);

    private sealed record ChatFunctionDef(string Name, string Description, JsonObject Parameters);

    private sealed record TextPart(string Type, string Text);

    private sealed record ImagePart(
        string Type,
        [property: JsonPropertyName("image_url")] ImageUrl ImageUrl);

    private sealed record ImageUrl(string Url);

    private sealed record ChatResponse(ChatChoice[]? Choices, ChatUsage? Usage = null);

    private sealed record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens = null,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens = null);

    private sealed record ChatChoice(
        ChatResponseMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChatToolCallResponse(
        string? Id,
        [property: JsonPropertyName("function")] ChatFunctionCallResponse? Function = null);

    private sealed record ChatFunctionCallResponse(string? Name, string? Arguments);

    private sealed record ChatResponseMessage(
        string? Content,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<ChatToolCallResponse>? ToolCalls = null);
}

// Vision-owned timed chat client for model comparisons. Minimal
// OpenAI-compatible vision call: posts chat/completions, reads ONLY
// choices[0].message.content (provider reasoning fields ignored), captures
// usage tokens when the API returns them plus wall time. No business data.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Gate.Eval;

internal sealed record TimedUsage(int? PromptTokens, int? CompletionTokens);

internal sealed record TimedReadResult(string Text, TimedUsage Usage, long ElapsedMs);

internal sealed class DockEvalTimedClient(
    HttpClient http, string baseUrl, string model, string apiKey, int maxTokens)
{
    public string ModelName => model;

    public async Task<TimedReadResult> ReadAsync(string prompt, byte[] jpeg, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(new
        {
            model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = prompt },
                        new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) } }
                    }
                }
            },
            max_tokens = maxTokens
        });

        var sw = Stopwatch.StartNew();
        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
        sw.Stop();
        Assert.NotNull(doc);

        var root = doc!.RootElement;
        var choice = root.GetProperty("choices").EnumerateArray().First();
        var content = choice.GetProperty("message").GetProperty("content");
        string text;
        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString() ?? string.Empty;
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            text = string.Concat(content.EnumerateArray()
                .Where(part => part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString()));
        }
        else
        {
            text = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Model {model} returned empty content.");
        }

        int? promptTokens = null;
        int? completionTokens = null;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pti))
            {
                promptTokens = pti;
            }

            if (usage.TryGetProperty("completion_tokens", out var ct2) && ct2.TryGetInt32(out var cti))
            {
                completionTokens = cti;
            }
        }

        return new TimedReadResult(text, new TimedUsage(promptTokens, completionTokens), sw.ElapsedMilliseconds);
    }
}

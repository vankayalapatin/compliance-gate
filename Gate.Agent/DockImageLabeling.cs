// Image labeling abstraction: apply carton labels onto a base dock view.
// The Meta implementation calls /images/edits once per image; tests inject
// fakes. The pipeline falls back to programmatic compositing whenever the
// labeled result fails verification, and records which path each image took.
using System.Text.Json;

namespace Gate.Agent;

public interface IImageLabeler
{
    string LabelerName { get; }

    /// <summary>Applies labels onto the base view, guided by the mask and the
    /// placement prompt. Returns final JPEG bytes. Throws on transport or
    /// payload failures; the pipeline treats those as a fallback signal,
    /// never a silent pass. Endpoints without mask support place from the
    /// prompt alone; pipeline verification still enforces correctness.</summary>
    Task<byte[]> ApplyLabelsAsync(byte[] baseJpeg, byte[] maskPng, string prompt, CancellationToken ct = default);
}

public sealed record MetaImageOptions(string BaseUrl, string ImageModel, string ApiKey);

/// <summary>No-op labeler for runs without image-edit credentials: every AI
/// attempt fails fast, so the pipeline takes its verify-then-composite
/// fallback and records the reason. Composited labels still read live.</summary>
public sealed class NullImageLabeler : IImageLabeler
{
    public string LabelerName => "none";

    public Task<byte[]> ApplyLabelsAsync(byte[] baseJpeg, byte[] maskPng, string prompt, CancellationToken ct = default)
    {
        _ = baseJpeg;
        _ = maskPng;
        _ = prompt;
        _ = ct;
        throw new InvalidOperationException("no image labeler is configured");
    }
}

public sealed class MetaImageLabeler(HttpClient http, MetaImageOptions options) : IImageLabeler
{
    public string LabelerName => "meta-images-edits";

    public async Task<byte[]> ApplyLabelsAsync(byte[] baseJpeg, byte[] maskPng, string prompt, CancellationToken ct = default)
    {
        // The edits endpoint rejects `mask` and `response_format`, so the
        // request carries image + model + prompt + n only; the mask stays in
        // the interface for endpoints that support it. Placement comes from
        // the prompt and the pipeline verifies every field before accepting.
        _ = maskPng;
        var endpoint = options.BaseUrl.TrimEnd('/') + "/images/edits";
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(baseJpeg), "image", "base.jpg");
        form.Add(new StringContent(options.ImageModel), "model");
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent("1"), "n");
        message.Content = form;

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Image edits failed at {endpoint} (HTTP {(int)response.StatusCode}): {(body.Length <= 300 ? body : body[..300] + "...")}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("data").EnumerateArray().First();
            if (first.TryGetProperty("b64_json", out var b64) && !string.IsNullOrWhiteSpace(b64.GetString()))
            {
                return Convert.FromBase64String(b64.GetString()!);
            }

            if (first.TryGetProperty("url", out var url) && !string.IsNullOrWhiteSpace(url.GetString()))
            {
                return await http.GetByteArrayAsync(url.GetString(), ct).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Image edits at {endpoint} returned an unparseable payload: {ex.Message}");
        }

        throw new InvalidOperationException(
            $"Image edits at {endpoint} returned neither b64_json nor url: {(body.Length <= 300 ? body : body[..300] + "...")}");
    }
}

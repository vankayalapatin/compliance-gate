// Resilient external calls. Model calls carry a per-attempt timeout plus one
// retry on transport-shaped failures; every failure is logged with its case
// context and the last error is rethrown, never swallowed.
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Agent;

public static class ResilientCalls
{
    public static TimeSpan DefaultModelTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Completes one model request with a per-attempt timeout and a
    /// single retry. Logs attempts with context; throws the last error.</summary>
    public static async Task<ModelResult> CompleteWithRetryAsync(
        IModelClient client,
        ModelRequest request,
        ILogger logger,
        string context,
        CancellationToken ct = default)
    {
        return await CompleteWithRetryAsync(
            client, request, logger, context, DefaultModelTimeout, ct).ConfigureAwait(false);
    }

    public static async Task<ModelResult> CompleteWithRetryAsync(
        IModelClient client,
        ModelRequest request,
        ILogger logger,
        string context,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(timeout);
                var result = await client.CompleteAsync(request, attemptCts.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(result.Text) || (result.ToolCalls?.Count ?? 0) > 0)
                {
                    return result;
                }

                lastError = new InvalidOperationException("Model returned empty text.");
                logger.LogWarning("Model call for {Context} came back empty (attempt {Attempt}).", context, attempt);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                lastError = ex;
                logger.LogWarning(ex, "Model call for {Context} failed (attempt {Attempt}).", context, attempt);
            }
        }

        logger.LogError(lastError, "Model call for {Context} failed after one retry.", context);
        throw new InvalidOperationException($"Model call for {context} failed after one retry.", lastError);
    }
}

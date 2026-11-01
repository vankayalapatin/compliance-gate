// Spend-metering model client. Sits between the file cache and the live
// client, so only cache misses (real paid calls) record and only capped
// spend refuses. The fake echo client is never wrapped: free calls stay
// free by construction. Refusals throw a surfaced error the callers log;
// MeteredModelClient logs the refusal with the model name too.
using Gate.Contracts;

namespace Gate.Web.Services;

public sealed class MeteredModelClient : IModelClient
{
    private readonly IModelClient _inner;
    private readonly ModelSpendTracker _spend;
    private readonly ILogger<MeteredModelClient> _log;

    public MeteredModelClient(
        IModelClient inner, ModelSpendTracker spend, ILogger<MeteredModelClient> log)
    {
        _inner = inner;
        _spend = spend;
        _log = log;
    }

    public string ModelName => _inner.ModelName;

    public async Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        try
        {
            _spend.ThrowIfCapped();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Live model call refused: spend cap reached for model {Model}.",
                ModelName);
            throw;
        }

        // Only a successful call records: a failed call bills nothing we
        // can prove, so it accrues no spend.
        var result = await _inner.CompleteAsync(request, ct).ConfigureAwait(false);
        _spend.RecordLiveCall(ModelName);
        return result;
    }
}

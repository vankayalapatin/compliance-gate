// Replay guard: any call throws, so a replay test fails on the
// first live model call instead of silently spending it.
// (VisionReadEntry and VisionReadCache live in Gate.Contracts.)
using Gate.Contracts;

namespace Gate.Eval;

internal sealed class ReplayOnlyModelClient : IModelClient
{
    public string ModelName => "replay-only";

    public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "Replay made a live model call. Demo and replay must read cache only.");
}

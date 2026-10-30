// Shared resolution for the model response cache directory. Program.cs
// and the replay guard must agree exactly, so both call this helper.
namespace Gate.Web.Services;

public static class ModelCachePaths
{
    public static string ResolveDir() =>
        Environment.GetEnvironmentVariable("GATE_MODEL_CACHE")
        ?? Path.Combine(Path.GetTempPath(), "gate-model-cache");
}

/// <summary>Process-wide replay flag. Once a reset-plus-replay runs, the
/// session stays in replay mode: Ask-why answers from the model cache only
/// and refuses to place a live call.</summary>
public sealed class ReplayState
{
    public bool IsReplay { get; set; }
}

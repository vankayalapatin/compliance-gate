// Live-vs-library dock scene pick. When the live switch is on and a scene
// generator is wired, the dock build first tries a generated scene for the
// shipment's own carton count (generate, locate faces, draw ASN labels,
// read back and verify); any failure falls back to the library scene and
// the reason lands on the timeline plus the last-fallback slot the Admin
// status reads. The switch defaults off and the generator stays null until
// the host wires it; the app turns the switch on at startup whenever the
// image settings are present, so shipments build live by default.
namespace Gate.Agent;

public sealed class DockSceneLive
{
    private readonly object _lock = new();

    public bool LiveOn { get; set; } = false;

    public ISceneGenerator? SceneGenerator { get; set; } = null;

    public string? LastFallbackReason
    {
        get { lock (_lock) { return _lastFallbackReason; } }
    }

    private string? _lastFallbackReason;

    public void RecordFallback(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        lock (_lock)
        {
            _lastFallbackReason = reason.Trim();
        }
    }
}

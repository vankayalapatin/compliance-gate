// Presentation pace: a display-only toggle. When on, the tool loop pauses
// between turns so each step lands in the activity pane one bubble at a
// time, and the pane polls every second. Off means today's behaviour. The
// flag changes nothing but timing: no step choice, verdict, or write reads
// it. Thread-safe: the Admin toggle and the runner touch it from different
// circuits.
namespace Gate.Agent;

public sealed class PresentationPace
{
    private readonly object _gate = new();
    private bool _isOn;

    /// <summary>Pause between tool-loop turns while pacing. Two seconds in
    /// the app; settable so tests pace in milliseconds.</summary>
    public TimeSpan StepDelay { get; set; } = TimeSpan.FromSeconds(2);

    public bool IsOn
    {
        get { lock (_gate) { return _isOn; } }
        set { lock (_gate) { _isOn = value; } }
    }
}

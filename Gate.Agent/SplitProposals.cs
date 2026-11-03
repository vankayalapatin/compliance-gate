// Split proposals switch: an Admin toggle. When on, the agent may propose
// splitting a buy across vendors when the cheapest vendor is blocked for
// the full quantity; the verified plan still needs a person's approval
// before anything is ordered. When off, the picker never proposes a
// split and the flow is exactly today's single-vendor award. Thread-safe:
// the Admin toggle and the runner touch it from different circuits.
namespace Gate.Agent;

public sealed class SplitProposals
{
    private readonly object _gate = new();
    private bool _isOn = true;

    public bool IsOn
    {
        get { lock (_gate) { return _isOn; } }
        set { lock (_gate) { _isOn = value; } }
    }
}

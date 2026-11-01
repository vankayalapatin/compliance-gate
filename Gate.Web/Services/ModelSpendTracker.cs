// Process-wide model spend ledger. Every live model call flows through
// MeteredModelClient, which records here; cached answers never touch the
// meter. Dollars need a configured estimate (ModelSpend:CostPerCallUsd)
// and the cap needs ModelSpend:CapUsd — both arrive from host config or
// user secrets, never from code, so an unconfigured tracker reports call
// counts and says plainly that spend is unmetered. Thread-safe: live
// calls overlap across cases.
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Gate.Web.Services;

public sealed class ModelSpendTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _callsByModel = new(StringComparer.Ordinal);
    private int _liveCalls;

    public ModelSpendTracker(IConfiguration? config = null)
    {
        CostPerCallUsd = TryDecimal(config, "ModelSpend:CostPerCallUsd");
        CapUsd = TryDecimal(config, "ModelSpend:CapUsd");
    }

    private static decimal? TryDecimal(IConfiguration? config, string key)
    {
        var raw = config?[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Unparseable or non-positive values mean "not configured": the
        // tracker reports counts and says spend is unmetered instead of
        // enforcing a cap it cannot price.
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            && value > 0 ? value : null;
    }

    /// <summary>Estimated dollars per live call from config. Null means
    /// unmetered: counts still accrue but no dollar figure exists.</summary>
    public decimal? CostPerCallUsd { get; }

    /// <summary>Spend cap in dollars from config. Null means no cap: live
    /// calls are never refused.</summary>
    public decimal? CapUsd { get; }

    public int LiveCallCount
    {
        get { lock (_gate) { return _liveCalls; } }
    }

    public IReadOnlyDictionary<string, int> CallsByModel
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, int>(_callsByModel, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Estimated spend so far. Null while unmetered.</summary>
    public decimal? SpendUsd
    {
        get
        {
            lock (_gate)
            {
                return CostPerCallUsd is { } rate ? _liveCalls * rate : null;
            }
        }
    }

    public bool AtCap
    {
        get
        {
            lock (_gate)
            {
                return SpendUsdLocked() is { } spend && CapUsd is { } cap && spend >= cap;
            }
        }
    }

    private decimal? SpendUsdLocked() =>
        CostPerCallUsd is { } rate ? _liveCalls * rate : null;

    private string SummaryLocked()
    {
        var spend = SpendUsdLocked();
        if (spend is { } dollars && CapUsd is { } cap)
        {
            var text = string.Create(
                CultureInfo.InvariantCulture, $"${dollars:N2} of ${cap:N2}");
            return spend >= cap
                ? text + " — cap reached, live calls refused"
                : text;
        }

        if (spend is { } metered)
        {
            return string.Create(
                CultureInfo.InvariantCulture, $"${metered:N2} (no cap configured)");
        }

        return $"{_liveCalls} live call(s), spend unmetered";
    }

    /// <summary>One-line spend readout for the top bar, reset steps, and
    /// ask-why answers. Always honest about what is configured.</summary>
    public string Summary
    {
        get { lock (_gate) { return SummaryLocked(); } }
    }

    public void RecordLiveCall(string modelName)
    {
        lock (_gate)
        {
            _liveCalls++;
            _callsByModel[modelName] = _callsByModel.TryGetValue(modelName, out var count)
                ? count + 1
                : 1;
        }
    }

    /// <summary>Throws when a configured cap is reached. Unmetered or
    /// uncapped trackers never refuse.</summary>
    public void ThrowIfCapped()
    {
        lock (_gate)
        {
            if (SpendUsdLocked() is { } spend && CapUsd is { } cap && spend >= cap)
            {
                throw new InvalidOperationException(
                    $"Model spend cap reached ({SummaryLocked()}): live model calls are refused. " +
                    "Raise ModelSpend:CapUsd or replay from cache.");
            }
        }
    }
}

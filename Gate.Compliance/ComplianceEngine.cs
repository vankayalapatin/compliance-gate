// Compliance engine: vendor gate plus pre-pay gate with tiered, cited,
// deterministic verdicts. Rule ids, tiers, and sources come from RuleCatalog;
// policy values stay in named rule config, never demo literals.
using Gate.Contracts;

namespace Gate.Compliance;

public sealed class ComplianceEngine : IComplianceEngine
{
    public const string CorpOriginCapId = "CORP-02";

    public const string PrePayHoldId = "ELEC-04";

    private readonly IReadOnlyList<ScreeningEntry>? _screeningEntries;
    private readonly string? _screeningError;
    private readonly CorpPackConfig _corp = RuleCatalog.Corp;

    /// <summary>Default engine: screening runs against the bundled
    /// compliance-side snapshot, thresholds come from the CORP pack JSON.</summary>
    public ComplianceEngine()
        : this(LoadDefaultSnapshot)
    {
    }

    /// <summary>Engine with explicit pack thresholds (tests, policy trials).
    /// Screening still runs against the bundled snapshot.</summary>
    public ComplianceEngine(CorpPackConfig corp)
        : this(LoadDefaultSnapshot)
    {
        _corp = corp;
    }

    /// <summary>Engine with an explicit screening snapshot (tests, refresh).</summary>
    public ComplianceEngine(IReadOnlyList<ScreeningEntry> screeningEntries)
    {
        _screeningEntries = screeningEntries;
    }

    /// <summary>Engine with a snapshot loader. A loader failure surfaces as a
    /// screening-unavailable human review, never a silent pass or throw.</summary>
    public ComplianceEngine(Func<IReadOnlyList<ScreeningEntry>> snapshotLoader)
    {
        try
        {
            _screeningEntries = snapshotLoader();
        }
        catch (Exception ex)
        {
            _screeningError = ex.Message;
        }
    }

    private static IReadOnlyList<ScreeningEntry> LoadDefaultSnapshot() =>
        ScreeningSnapshotLoader.LoadEmbedded().Entries;

    public Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
        VendorDto vendor,
        ItemDto item,
        IReadOnlyList<VendorDocumentDto> documents,
        ProposedAward award,
        IReadOnlyList<VolumeHistory> history,
        CancellationToken ct = default) =>
        EvaluateVendorWithExceptionAsync(vendor, item, documents, award, history, null, ct);

    /// <summary>Vendor gate with an optional CORP-02 logged exception. The
    /// exception only excuses CORP-02, and only when it names this vendor,
    /// carries a named approver, and records a reason.</summary>
    public Task<IReadOnlyList<Verdict>> EvaluateVendorWithExceptionAsync(
        VendorDto vendor,
        ItemDto item,
        IReadOnlyList<VendorDocumentDto> documents,
        ProposedAward award,
        IReadOnlyList<VolumeHistory> history,
        CorpExceptionApproval? exception,
        CancellationToken ct = default)
    {
        if (!string.Equals(item.ItemId, award.ItemId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Item {item.ItemId} does not match award item {award.ItemId}; pack scope cannot be evaluated.");
        }

        var verdicts = new List<Verdict>();
        if (vendor.IsBlocked)
        {
            verdicts.Add(new Verdict("VENDOR-BLOCK", VerdictKind.Block, $"Vendor {vendor.VendorId} is blocked: {vendor.BlockReason}.", "ERP vendor master"));
        }

        verdicts.Add(EvaluateDocument(documents, item, RuleCatalog.Find("BAT-LI-01")));
        verdicts.Add(EvaluateDocument(documents, item, RuleCatalog.Find("BAT-LI-02")));
        verdicts.Add(EvaluateDocument(documents, item, RuleCatalog.Find("ELEC-01")));
        verdicts.Add(EvaluateDocument(documents, item, RuleCatalog.Find("ELEC-02")));
        verdicts.Add(EvaluateScreening(vendor));
        verdicts.Add(EvaluateOriginCap(vendor.VendorId, award, history, exception));

        return Task.FromResult<IReadOnlyList<Verdict>>(verdicts);
    }

    private static Verdict EvaluateDocument(
        IReadOnlyList<VendorDocumentDto> documents, ItemDto item, RuleDefinition rule)
    {
        if (string.IsNullOrWhiteSpace(rule.DocumentKind) || string.IsNullOrWhiteSpace(rule.DocumentLabel))
        {
            throw new InvalidOperationException(
                $"Rule {rule.RuleId} carries no document kind in its pack JSON; it cannot gate documents.");
        }

        if (!item.IsCritical)
        {
            return new Verdict(rule.RuleId, VerdictKind.Pass,
                $"{rule.DocumentLabel} not applicable to non-critical part {item.ItemId}; no pack applies.", rule.Source);
        }

        var present = documents.Any(d => d.Kind == rule.DocumentKind && d.Present);
        return present
            ? new Verdict(rule.RuleId, VerdictKind.Pass, $"{rule.DocumentLabel} present.", rule.Source)
            : new Verdict(rule.RuleId, VerdictKind.Block, $"{rule.DocumentLabel} missing for lot.", rule.Source);
    }

    private Verdict EvaluateScreening(VendorDto vendor)
    {
        var rule = RuleCatalog.Find("SCR-01");
        if (_screeningError is not null)
        {
            return new Verdict(rule.RuleId, VerdictKind.HumanReview,
                $"Restricted-party screening snapshot unavailable ({_screeningError}); route to human review.",
                rule.Source);
        }

        var match = ScreeningMatcher.Match(vendor, _screeningEntries ?? []);
        return match is null
            ? new Verdict(rule.RuleId, VerdictKind.Pass,
                $"No restricted-party match for {vendor.Name}.", rule.Source)
            : new Verdict(rule.RuleId, VerdictKind.Block,
                $"{vendor.Name} matches {match.List} ({match.Program}); award blocked.", rule.Source);
    }

    private Verdict EvaluateOriginCap(
        string vendorId, ProposedAward award, IReadOnlyList<VolumeHistory> history, CorpExceptionApproval? exception)
    {
        var total = history.Sum(h => h.Cells);
        if (total <= 0)
        {
            // No trailing history for this part: CORP-02 has no denominator.
            // Passes as not applicable so history-free parts (e.g. SCR-9 via
            // Vendor C) keep their straight-through path. The empty-history
            // human-review boundary from the scenario plan is still open.
            return new Verdict(CorpOriginCapId, VerdictKind.Pass,
                $"No 12-month volume history for {award.ItemId}; country-of-origin cap not applicable.", _corp.PolicySource);
        }

        var countryCells = history
            .Where(h => string.Equals(h.Country, award.Country, StringComparison.OrdinalIgnoreCase))
            .Sum(h => h.Cells);
        var share = (countryCells + award.Quantity) / (total + award.Quantity);
        if (share <= _corp.OriginCap)
        {
            return new Verdict(CorpOriginCapId, VerdictKind.Pass,
                $"Awarding {award.Quantity} {award.ItemId} cells to {award.Country} puts its share at {share:P1}, within the {_corp.OriginCap:P0} cap.", _corp.PolicySource);
        }

        if (exception is not null && exception.IsValidFor(CorpOriginCapId, vendorId))
        {
            return new Verdict(CorpOriginCapId, VerdictKind.Pass,
                $"Awarding {award.Quantity} {award.ItemId} cells to {award.Country} puts its share at {share:P1}, exceeding the {_corp.OriginCap:P0} cap, but overridden by logged exception from {exception.Approver} at {exception.At:u}: {exception.Reason}.",
                _corp.PolicySource);
        }

        return new Verdict(CorpOriginCapId, VerdictKind.Block,
            $"Awarding {award.Quantity} {award.ItemId} cells to {award.Country} puts its share at {share:P1} ({countryCells}+{award.Quantity} of {total}+{award.Quantity}), exceeding the {_corp.OriginCap:P0} country-of-origin cap.", _corp.PolicySource);
    }

    public Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        IReadOnlyList<HoldDto> openHolds,
        CancellationToken ct = default)
    {
        var source = RuleCatalog.Find(PrePayHoldId).Source;
        IReadOnlyList<Verdict> verdicts = openHolds.Count == 0
            ? [new Verdict(PrePayHoldId, VerdictKind.Pass, "No open holds on lot.", source)]
            : [new Verdict(PrePayHoldId, VerdictKind.Block,
                $"{openHolds.Count} open hold(s): {string.Join(", ", openHolds.Select(h => h.HoldId))}. Payment blocked.", source)];
        return Task.FromResult(verdicts);
    }

    public Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        PrePayState state,
        CancellationToken ct = default)
    {
        var source = RuleCatalog.Find(PrePayHoldId).Source;
        if (state.OpenHolds.Count == 0
            && state.QuarantinedLotRefs.Count == 0
            && state.OpenReviews.Count == 0)
        {
            IReadOnlyList<Verdict> pass =
                [new Verdict(PrePayHoldId, VerdictKind.Pass,
                    "No open holds, no quarantined lots, and no open reviews on lot or shipment; pre-pay all-clear.", source)];
            return Task.FromResult(pass);
        }

        var parts = new List<string>();
        if (state.OpenHolds.Count > 0)
        {
            parts.Add($"{state.OpenHolds.Count} open hold(s): {string.Join(", ", state.OpenHolds.Select(h => h.HoldId))}");
        }

        if (state.QuarantinedLotRefs.Count > 0)
        {
            parts.Add($"{state.QuarantinedLotRefs.Count} quarantined lot(s): {string.Join(", ", state.QuarantinedLotRefs)}");
        }

        if (state.OpenReviews.Count > 0)
        {
            parts.Add($"{state.OpenReviews.Count} open review(s): {string.Join(", ", state.OpenReviews.Select(r => $"{r.ReviewId} ({r.RuleId})"))}");
        }

        IReadOnlyList<Verdict> verdicts =
            [new Verdict(PrePayHoldId, VerdictKind.Block,
                $"{string.Join("; ", parts)}. Payment blocked.", source)];
        return Task.FromResult(verdicts);
    }
}

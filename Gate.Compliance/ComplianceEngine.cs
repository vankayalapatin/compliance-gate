// Compliance engine: vendor gate plus pre-pay gate with tiered, cited,
// deterministic verdicts. Rule ids, tiers, and sources come from the
// current pack set (published files over embedded bases, resolved per
// evaluation); policy values stay in named rule config, never literals.
using Gate.Contracts;

namespace Gate.Compliance;

public sealed partial class ComplianceEngine : IComplianceEngine
{
    public const string CorpOriginCapId = "CORP-02";

    public const string PrePayHoldId = "ELEC-04";

    private readonly IReadOnlyList<ScreeningEntry>? _screeningEntries;
    private readonly string? _screeningError;
    private readonly CorpPackConfig? _explicitCorp;

    /// <summary>Default engine: screening runs against the bundled
    /// compliance-side snapshot, thresholds come from the current CORP pack
    /// (published files over embedded bases, resolved per evaluation).</summary>
    public ComplianceEngine()
        : this(LoadDefaultSnapshot)
    {
    }

    /// <summary>Engine with explicit pack thresholds (tests, policy trials).
    /// Screening still runs against the bundled snapshot; rule records
    /// still resolve live.</summary>
    public ComplianceEngine(CorpPackConfig corp)
        : this(LoadDefaultSnapshot)
    {
        _explicitCorp = corp;
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

    /// <summary>Current CORP thresholds: the explicit override when one was
    /// supplied, else the pack directory's current winner overlaid over the
    /// embedded base, resolved fresh on every call. An approved memo
    /// enforces on the next evaluation with no rebuild and no restart.</summary>
    public CorpPackConfig CurrentCorpConfig() =>
        _explicitCorp ?? CurrentPacks().Corp;

    /// <summary>Current rule records for every pack, resolved fresh like
    /// the thresholds.</summary>
    public IReadOnlyList<RuleDefinition> CurrentRules() => CurrentPacks().Rules;

    /// <summary>Current record for one rule id. Unknown ids throw, like the
    /// shipped catalog.</summary>
    public RuleDefinition FindRule(string ruleId)
    {
        var rule = CurrentPacks().Rules.FirstOrDefault(r =>
            string.Equals(r.RuleId, ruleId, StringComparison.Ordinal));
        if (rule is null)
        {
            throw new InvalidOperationException($"Unknown compliance rule id '{ruleId}' in the current pack set.");
        }

        return rule;
    }

    /// <summary>Null when the id is not a current rule (tool names, match
    /// codes). Screens use this so unknown ids render honestly instead of
    /// throwing.</summary>
    public RuleDefinition? TryFindRule(string? ruleId) =>
        string.IsNullOrWhiteSpace(ruleId)
            ? null
            : CurrentPacks().Rules.FirstOrDefault(r =>
                string.Equals(r.RuleId, ruleId, StringComparison.Ordinal));

    private RulePackSet CurrentPacks() => RulePackLoader.LoadCurrent(TryResolvePackDirectory());

    public Task<IReadOnlyList<Verdict>> EvaluateVendorAsync(
        VendorDto vendor,
        ItemDto item,
        IReadOnlyList<VendorDocumentDto> documents,
        ProposedAward award,
        IReadOnlyList<VolumeHistory> history,
        IReadOnlyList<OpenOrderQty>? openOrders = null,
        CancellationToken ct = default) =>
        EvaluateVendorWithExceptionAsync(vendor, item, documents, award, history, null, ct, openOrders);

    /// <summary>Document-rule check behind screening consequences: true
    /// when the catalog carries the rule with a document kind. Unknown ids
    /// (including the ERP-block marker) are not document rules.</summary>
    public bool IsDocumentRule(string ruleId) =>
        !string.IsNullOrWhiteSpace(ruleId)
        && CurrentRules().Any(r =>
            string.Equals(r.RuleId, ruleId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(r.DocumentKind));

    /// <summary>Vendor gate with an optional CORP-02 logged exception. The
    /// exception only excuses CORP-02, and only when it names this vendor,
    /// carries a named approver, and records a reason. Open orders join
    /// the cap math; none means no open quantity.</summary>
    public Task<IReadOnlyList<Verdict>> EvaluateVendorWithExceptionAsync(
        VendorDto vendor,
        ItemDto item,
        IReadOnlyList<VendorDocumentDto> documents,
        ProposedAward award,
        IReadOnlyList<VolumeHistory> history,
        CorpExceptionApproval? exception,
        CancellationToken ct = default,
        IReadOnlyList<OpenOrderQty>? openOrders = null)
    {
        if (!string.Equals(item.ItemId, award.ItemId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Item {item.ItemId} does not match award item {award.ItemId}; pack scope cannot be evaluated.");
        }

        // One pack resolution per evaluation: every verdict below cites the
        // same current set, and the next evaluation re-resolves.
        var packs = CurrentPacks();
        var corp = _explicitCorp ?? packs.Corp;
        var verdicts = new List<Verdict>();
        if (vendor.IsBlocked)
        {
            verdicts.Add(new Verdict(ComplianceVerdictIds.VendorBlock, VerdictKind.Block, $"Vendor {vendor.VendorId} is blocked: {vendor.BlockReason}.", "ERP vendor master"));
        }

        verdicts.Add(EvaluateDocument(documents, item, FindIn(packs, "BAT-LI-01")));
        verdicts.Add(EvaluateDocument(documents, item, FindIn(packs, "BAT-LI-02")));
        verdicts.Add(EvaluateDocument(documents, item, FindIn(packs, "ELEC-01")));
        verdicts.Add(EvaluateDocument(documents, item, FindIn(packs, "ELEC-02")));
        verdicts.Add(EvaluateScreening(vendor, FindIn(packs, "SCR-01")));
        verdicts.Add(EvaluateOriginCap(vendor.VendorId, item, award, history, exception, openOrders ?? [], corp));

        return Task.FromResult<IReadOnlyList<Verdict>>(verdicts);
    }

    private static RuleDefinition FindIn(RulePackSet packs, string ruleId)
    {
        var rule = packs.Rules.FirstOrDefault(r =>
            string.Equals(r.RuleId, ruleId, StringComparison.Ordinal));
        if (rule is null)
        {
            throw new InvalidOperationException($"Unknown compliance rule id '{ruleId}' in the current pack set.");
        }

        return rule;
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

    private Verdict EvaluateScreening(VendorDto vendor, RuleDefinition rule)
    {
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
        string vendorId, ItemDto item, ProposedAward award,
        IReadOnlyList<VolumeHistory> history, CorpExceptionApproval? exception,
        IReadOnlyList<OpenOrderQty> openOrders, CorpPackConfig corp)
    {
        var total = history.Sum(h => h.Cells);
        var countryCells = history
            .Where(h => string.Equals(h.Country, award.Country, StringComparison.OrdinalIgnoreCase))
            .Sum(h => h.Cells);
        var totalOpen = openOrders.Sum(o => o.Cells);
        var countryOpen = openOrders
            .Where(o => string.Equals(o.Country, award.Country, StringComparison.OrdinalIgnoreCase))
            .Sum(o => o.Cells);
        // Figures ride every CORP-02 verdict with a denominator, including
        // the non-critical pass: the award stores them so decided orders
        // show the shares as they stood, matching the live sentences.
        OriginCapFigures? capFigures = total + totalOpen + award.Quantity > 0
            ? new OriginCapFigures(
                award.ItemId, countryCells, total, countryOpen, totalOpen,
                award.Quantity, corp.OriginCap,
                (countryCells + countryOpen + award.Quantity) / (total + totalOpen + award.Quantity))
            : null;
        if (!item.IsCritical)
        {
            return new Verdict(CorpOriginCapId, VerdictKind.Pass,
                $"CORP-02 caps critical battery volume only; {award.ItemId} is not critical, so the cap is not applicable.", corp.PolicySource, capFigures);
        }

        if (total + totalOpen <= 0)
        {
            // No trailing history and no open orders for this part: CORP-02
            // has no denominator. Passes as not applicable so history-free
            // parts (e.g. SCR-9 via Vendor C) keep their straight-through
            // path. The empty-history human-review boundary from the
            // scenario plan is still open.
            return new Verdict(CorpOriginCapId, VerdictKind.Pass,
                $"No 12-month volume history or open orders for {award.ItemId}; country-of-origin cap not applicable.", corp.PolicySource);
        }

        var share = capFigures!.Share;
        var noun = DisplayWords.CountNoun(item.Description);
        var openNote = totalOpen > 0
            ? $", including {DisplayWords.Qty(totalOpen)} {noun} on open orders"
            : string.Empty;
        var figures =
            $"({DisplayWords.Qty(countryCells)}" +
            (countryOpen > 0 ? $"+{DisplayWords.Qty(countryOpen)} open" : string.Empty) +
            $"+{DisplayWords.Qty(award.Quantity)} of " +
            $"{DisplayWords.Qty(total)}" +
            (totalOpen > 0 ? $"+{DisplayWords.Qty(totalOpen)} open" : string.Empty) +
            $"+{DisplayWords.Qty(award.Quantity)} {noun})";
        var cap = capFigures;
        if (share <= corp.OriginCap)
        {
            return new Verdict(CorpOriginCapId, VerdictKind.Pass,
                $"Awarding {DisplayWords.Qty(award.Quantity)} {noun} ({award.ItemId}) to {award.Country} " +
                $"puts its share at {share:P1} {figures}{openNote}, within the {corp.OriginCap:P0} cap.", corp.PolicySource, cap);
        }

        if (exception is not null && exception.IsValidFor(CorpOriginCapId, vendorId))
        {
            return new Verdict(CorpOriginCapId, VerdictKind.Pass,
                $"Awarding {DisplayWords.Qty(award.Quantity)} {noun} ({award.ItemId}) to {award.Country} " +
                $"puts its share at {share:P1} {figures}{openNote}, exceeding the {corp.OriginCap:P0} cap, " +
                $"but overridden by logged exception from {exception.Approver} at {DisplayWords.When(exception.At)}: {exception.Reason}.",
                corp.PolicySource, cap);
        }

        return new Verdict(CorpOriginCapId, VerdictKind.Block,
            $"Awarding {DisplayWords.Qty(award.Quantity)} {noun} ({award.ItemId}) to {award.Country} " +
            $"puts its share at {share:P1} {figures}{openNote}, exceeding the {corp.OriginCap:P0} country-of-origin cap.", corp.PolicySource, cap);
    }

    public Task<IReadOnlyList<Verdict>> EvaluatePayableAsync(
        PrePayState state,
        CancellationToken ct = default)
    {
        _ = ct;
        var source = FindRule(PrePayHoldId).Source;
        if (state.OpenReviews.Count == 0)
        {
            IReadOnlyList<Verdict> pass =
                [new Verdict(PrePayHoldId, VerdictKind.Pass,
                    "No open reviews on lot or shipment; accepted value is payable while held value waits on its reshot.", source)];
            return Task.FromResult(pass);
        }

        IReadOnlyList<Verdict> block =
            [new Verdict(PrePayHoldId, VerdictKind.Block, DescribePayBlock([DescribeReviews(state)], "payment"), source)];
        return Task.FromResult(block);
    }

    public Task<IReadOnlyList<Verdict>> EvaluatePaymentAsync(
        PrePayState state,
        CancellationToken ct = default)
    {
        var source = FindRule(PrePayHoldId).Source;
        if (state.OpenHolds.Count == 0
            && state.QuarantinedLotRefs.Count == 0
            && state.OpenReviews.Count == 0
            && state.DockPendingLotRefs.Count == 0)
        {
            IReadOnlyList<Verdict> pass =
                [new Verdict(PrePayHoldId, VerdictKind.Pass,
                    "No open holds, no quarantined lots, no lots awaiting dock check, and no open reviews on lot or shipment; pre-pay all-clear.", source)];
            return Task.FromResult(pass);
        }

        var parts = new List<string>();
        if (state.OpenHolds.Count > 0)
        {
            parts.Add($"{DisplayWords.Plural(state.OpenHolds.Count, "open hold")}: {string.Join(", ", state.OpenHolds.Select(h => h.HoldId))}");
        }

        if (state.QuarantinedLotRefs.Count > 0)
        {
            parts.Add($"{DisplayWords.Plural(state.QuarantinedLotRefs.Count, "quarantined lot")}: {string.Join(", ", state.QuarantinedLotRefs)}");
        }

        if (state.DockPendingLotRefs.Count > 0)
        {
            parts.Add($"{DisplayWords.Plural(state.DockPendingLotRefs.Count, "lot")} awaiting dock check: {string.Join(", ", state.DockPendingLotRefs)}");
        }

        if (state.OpenReviews.Count > 0)
        {
            parts.Add(DescribeReviews(state));
        }

        IReadOnlyList<Verdict> verdicts =
            [new Verdict(PrePayHoldId, VerdictKind.Block,
                DescribePayBlock(parts, "payment"), source)];
        return Task.FromResult(verdicts);
    }

    private static string DescribeReviews(PrePayState state) =>
        $"{DisplayWords.Plural(state.OpenReviews.Count, "open review")}: " +
        string.Join(", ", state.OpenReviews.Select(r => $"{r.ReviewId} ({r.RuleId})"));

    private static string DescribePayBlock(IReadOnlyList<string> parts, string what) =>
        $"{string.Join("; ", parts)}. {char.ToUpper(what[0]) + what[1..]} blocked.";
}

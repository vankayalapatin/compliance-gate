// What-if report computation (docs/company-kb/WHAT-IF.md): a pure function
// over SUPPLIED rows. History rows plus pipeline rows plus old/new rule
// config produce a WhatIfReport. No adapter reads, no gather orchestration:
// wave-2 ERP/agent work supplies the rows; this module only computes.
//
// Windows are always reported separately, never blended. Until the KB
// fiscal calendar exists, the history label is the mandatory
// trailing-12-month approximation label. The unviable flag is explicit:
// it is true only when the caller supplies forward-path probes and every
// one blocks under the new config. Same inputs replay byte-identical
// (inputs are sorted, the timestamp is supplied, pack versions are copied
// into a sorted map; no clock, no randomness).
//
// Share-cap history semantics: totals come from the supplied history rows
// themselves, and each row is evaluated as the marginal award on top of
// those totals, exactly as the engine evaluates (countryCells + award) /
// (total + award). Old-vs-new comparison is what matters for flips.
// Rows that miss an observable the rule type needs (e.g. a pipeline row
// with no country under a share cap) evaluate to HumanReview on both
// sides: unevaluable rows never flip, never vanish.
using Gate.Contracts;

namespace Gate.Compliance;

// What-if shapes (WhatIfRuleConfig, WhatIfHistoryRow, WhatIfPipelineRow)
// live in Gate.Contracts; this file holds the calculator only.

public static class WhatIfCalculator
{
    /// <summary>Mandatory history label while the KB fiscal calendar is
    /// absent. An approver must never mistake the approximation for the
    /// fiscal year.</summary>
    public const string TrailingTwelveMonthLabel =
        "trailing-12-month approximation, FY window pending calendar";

    public const string HistoryDocumentType = "Award";

    /// <summary>Blocking-rule ref for pass-to-block flips: the proposed rule
    /// has no pack id until publish, so the flip cites the proposed type.</summary>
    public static string ProposedRuleRef(string ruleType) => $"proposed:{ruleType}";

    /// <summary>Computes the read-only old-vs-new impact report. OldRuleId is
    /// required exactly when oldConfig is supplied (it names the live rule
    /// that had blocked block-to-pass flips); probes are forward-path
    /// candidates evaluated under the new config for the unviable flag and
    /// are excluded from every output list.</summary>
    public static WhatIfReport Compute(
        IReadOnlyList<WhatIfHistoryRow> historyRows,
        IReadOnlyList<WhatIfPipelineRow> pipelineRows,
        WhatIfRuleConfig? oldConfig,
        WhatIfRuleConfig newConfig,
        string? oldRuleId,
        KbFiscalCalendar? fiscalCalendar,
        string proposalHash,
        string kbVersion,
        IReadOnlyDictionary<string, string> packVersions,
        DateTimeOffset computedAt,
        IReadOnlyList<WhatIfPipelineRow>? probes = null)
    {
        ArgumentNullException.ThrowIfNull(historyRows);
        ArgumentNullException.ThrowIfNull(pipelineRows);
        ArgumentNullException.ThrowIfNull(newConfig);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(kbVersion);
        ArgumentNullException.ThrowIfNull(packVersions);

        if (!MemoRuleRegistry.IsKnownType(newConfig.RuleType))
        {
            throw new InvalidOperationException(
                $"What-if new config names unknown rule type '{newConfig.RuleType}'; the closed registry decides.");
        }

        if (oldConfig is not null && !string.Equals(oldConfig.RuleType, newConfig.RuleType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"What-if compares old type '{oldConfig.RuleType}' against new type '{newConfig.RuleType}'; " +
                "both sides must name the same registry type.");
        }

        if (oldConfig is not null && string.IsNullOrWhiteSpace(oldRuleId))
        {
            throw new InvalidOperationException(
                "What-if with a live old config requires oldRuleId (the live rule that had blocked block-to-pass flips).");
        }

        if (oldConfig is null && oldRuleId is not null)
        {
            throw new InvalidOperationException(
                "What-if with no live old config takes no oldRuleId; the rule is absent live, so every old verdict is Pass.");
        }

        var newErrors = MemoRuleRegistry.ValidateParams(newConfig.RuleType, newConfig.Params);
        if (newErrors.Count > 0)
        {
            throw new InvalidOperationException("What-if new config is invalid: " + string.Join("; ", newErrors));
        }

        if (oldConfig is not null)
        {
            var oldErrors = MemoRuleRegistry.ValidateParams(oldConfig.RuleType, oldConfig.Params);
            if (oldErrors.Count > 0)
            {
                throw new InvalidOperationException("What-if old config is invalid: " + string.Join("; ", oldErrors));
            }
        }

        var orderedHistory = historyRows
            .OrderBy(r => r.DocumentNumber, StringComparer.Ordinal)
            .ToList();
        var orderedPipeline = pipelineRows
            .OrderBy(r => r.DocumentType, StringComparer.Ordinal)
            .ThenBy(r => r.DocumentNumber, StringComparer.Ordinal)
            .ToList();

        var totals = ShareTotals.FromHistory(orderedHistory);
        var historyFlips = new List<WhatIfFlip>();
        foreach (var row in orderedHistory)
        {
            var old = EvaluateHistory(oldConfig, row, totals);
            var @new = EvaluateHistory(newConfig, row, totals);
            if (old != @new)
            {
                historyFlips.Add(new WhatIfFlip(
                    HistoryDocumentType, row.DocumentNumber, old, @new,
                    BlockingRef(@new, newConfig.RuleType, oldRuleId),
                    row.Quantity, row.Quantity * row.UnitPrice, row.Currency));
            }
        }

        var pipelineImpacts = new List<PipelineRowImpact>();
        var pipelineFlips = new List<WhatIfFlip>();
        foreach (var row in orderedPipeline)
        {
            var old = EvaluatePipeline(oldConfig, row, totals);
            var @new = EvaluatePipeline(newConfig, row, totals);
            pipelineImpacts.Add(new PipelineRowImpact(
                row.DocumentType, row.DocumentNumber, old, @new,
                @new == VerdictKind.Pass ? null : ProposedRuleRef(newConfig.RuleType)));
            if (old != @new)
            {
                pipelineFlips.Add(new WhatIfFlip(
                    row.DocumentType, row.DocumentNumber, old, @new,
                    BlockingRef(@new, newConfig.RuleType, oldRuleId),
                    row.Quantity, row.Quantity * (row.UnitPrice ?? 0m), row.Currency));
            }
        }

        var flips = historyFlips.Concat(pipelineFlips).ToList();
        var unviable = probes is not null && probes.Count > 0
            && probes.All(p => EvaluatePipeline(newConfig, p, totals) != VerdictKind.Pass);

        var sortedPacks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in packVersions)
        {
            sortedPacks[kv.Key] = kv.Value;
        }

        return new WhatIfReport(
            proposalHash,
            kbVersion,
            sortedPacks,
            fiscalCalendar is null
                ? TrailingTwelveMonthLabel
                : $"current-fiscal-year window ({fiscalCalendar.CurrentFy})",
            orderedHistory.Count,
            historyFlips,
            pipelineImpacts,
            flips,
            SumTotals(orderedHistory.Count + orderedPipeline.Count, flips),
            unviable,
            computedAt);
    }

    /// <summary>Flip attribution: pass-to-block names the proposed rule (it
    /// blocks now); block-to-pass names the old rule (it had blocked);
    /// lateral flips name the proposed rule (the current blocker).</summary>
    private static string? BlockingRef(
        VerdictKind @new, string ruleType, string? oldRuleId) =>
        @new == VerdictKind.Pass ? oldRuleId : ProposedRuleRef(ruleType);

    private static VerdictKind EvaluateHistory(
        WhatIfRuleConfig? config, WhatIfHistoryRow row, ShareTotals totals)
    {
        if (config is null)
        {
            return VerdictKind.Pass;
        }

        var obs = new Observables(
            row.IsCritical, row.VendorId, row.Country, row.Quantity,
            row.DocPresent, row.MarkPresent, row.MarkLegible,
            row.HandlingRequired, row.HandlingVisible, row.DamageVisible, row.ScreeningHit,
            HasOpenHold: false, HasQuarantine: false, HasOpenReview: false, IsPostedHistory: true);
        return Evaluate(config, obs, totals);
    }

    private static VerdictKind EvaluatePipeline(
        WhatIfRuleConfig? config, WhatIfPipelineRow row, ShareTotals totals)
    {
        if (config is null)
        {
            return VerdictKind.Pass;
        }

        var obs = new Observables(
            row.IsCritical, row.VendorId, row.Country, row.Quantity,
            row.DocPresent, row.MarkPresent, row.MarkLegible,
            row.HandlingRequired, row.HandlingVisible, row.DamageVisible, row.ScreeningHit,
            row.HasOpenHold, row.HasQuarantine, row.HasOpenReview, IsPostedHistory: false);
        return Evaluate(config, obs, totals);
    }

    private sealed record Observables(
        bool IsCritical,
        string? VendorId,
        string? Country,
        decimal Quantity,
        bool DocPresent,
        bool MarkPresent,
        bool MarkLegible,
        bool HandlingRequired,
        bool HandlingVisible,
        bool DamageVisible,
        bool ScreeningHit,
        bool HasOpenHold,
        bool HasQuarantine,
        bool HasOpenReview,
        bool IsPostedHistory);

    private static VerdictKind Evaluate(WhatIfRuleConfig config, Observables obs, ShareTotals totals) =>
        config.RuleType switch
        {
            MemoRuleRegistry.OriginShareCap => EvaluateShareCap(config, obs, totals.CountryCells(obs.Country), totals.TotalCells),
            MemoRuleRegistry.VendorShareCap => EvaluateShareCap(config, obs, totals.VendorCells(obs.VendorId), totals.TotalCells),
            MemoRuleRegistry.DocumentPresence => EvaluateDocument(config, obs),
            MemoRuleRegistry.ScreeningMatch => obs.ScreeningHit ? VerdictKind.Block : VerdictKind.Pass,
            MemoRuleRegistry.DockMarkPresence => EvaluateMark(obs),
            MemoRuleRegistry.DockHandlingPresence => obs.HandlingRequired && !obs.HandlingVisible
                ? VerdictKind.Hold : VerdictKind.Pass,
            MemoRuleRegistry.DamageReview => obs.DamageVisible ? VerdictKind.HumanReview : VerdictKind.Pass,
            MemoRuleRegistry.PrePayBlockOnOpen => EvaluatePrePay(config, obs),
            _ => throw new InvalidOperationException($"What-if cannot evaluate unknown rule type '{config.RuleType}'."),
        };

    private static VerdictKind EvaluateShareCap(
        WhatIfRuleConfig config, Observables obs, decimal? groupCells, decimal totalCells)
    {
        if (IsOutOfScope(config, obs))
        {
            return VerdictKind.Pass;
        }

        if (groupCells is null)
        {
            return VerdictKind.HumanReview;
        }

        var cap = decimal.Parse(config.Params["cap"], System.Globalization.CultureInfo.InvariantCulture);
        var share = (groupCells.Value + obs.Quantity) / (totalCells + obs.Quantity);
        return share <= cap ? VerdictKind.Pass : VerdictKind.Block;
    }

    private static VerdictKind EvaluateDocument(WhatIfRuleConfig config, Observables obs)
    {
        if (IsOutOfScope(config, obs))
        {
            return VerdictKind.Pass;
        }

        return obs.DocPresent ? VerdictKind.Pass : VerdictKind.Block;
    }

    private static VerdictKind EvaluateMark(Observables obs)
    {
        if (!obs.MarkPresent)
        {
            return VerdictKind.Hold;
        }

        return obs.MarkLegible ? VerdictKind.Pass : VerdictKind.HumanReview;
    }

    private static VerdictKind EvaluatePrePay(WhatIfRuleConfig config, Observables obs)
    {
        if (obs.IsPostedHistory)
        {
            return VerdictKind.Pass;
        }

        if (IsTrue(config.Params, "holds") && obs.HasOpenHold)
        {
            return VerdictKind.Block;
        }

        if (IsTrue(config.Params, "quarantine") && obs.HasQuarantine)
        {
            return VerdictKind.Block;
        }

        if (IsTrue(config.Params, "reviews") && obs.HasOpenReview)
        {
            return VerdictKind.Block;
        }

        return VerdictKind.Pass;
    }

    private static bool IsOutOfScope(WhatIfRuleConfig config, Observables obs) =>
        string.Equals(config.Params.GetValueOrDefault("partScope"), MemoRuleRegistry.ScopeCritical, StringComparison.Ordinal)
        && !obs.IsCritical;

    private static bool IsTrue(IReadOnlyDictionary<string, string> paramsMap, string key) =>
        paramsMap.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value) && value;

    private static WhatIfTotals SumTotals(int documentsEvaluated, IReadOnlyList<WhatIfFlip> flips)
    {
        var currencies = flips
            .Select(f => f.Currency)
            .Where(c => c is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new WhatIfTotals(
            documentsEvaluated,
            flips.Count(f => f.OldKind == VerdictKind.Pass && f.NewKind != VerdictKind.Pass),
            flips.Count(f => f.OldKind != VerdictKind.Pass && f.NewKind == VerdictKind.Pass),
            flips.Sum(f => f.Quantity),
            flips.Sum(f => f.Amount),
            flips.Count > 0 && currencies.Count == 1 ? currencies[0] : null);
    }

    /// <summary>Share denominators from the supplied history rows: the rows
    /// define the window. Country lookup is case-insensitive (engine
    /// behavior); vendor lookup is exact.</summary>
    private sealed class ShareTotals
    {
        private readonly Dictionary<string, decimal> _byCountry;
        private readonly Dictionary<string, decimal> _byVendor;

        private ShareTotals(Dictionary<string, decimal> byCountry, Dictionary<string, decimal> byVendor, decimal total)
        {
            _byCountry = byCountry;
            _byVendor = byVendor;
            TotalCells = total;
        }

        public decimal TotalCells { get; }

        public static ShareTotals FromHistory(IReadOnlyList<WhatIfHistoryRow> rows)
        {
            var byCountry = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var byVendor = new Dictionary<string, decimal>(StringComparer.Ordinal);
            decimal total = 0m;
            foreach (var row in rows)
            {
                byCountry[row.Country] = byCountry.GetValueOrDefault(row.Country) + row.Quantity;
                if (row.VendorId is not null)
                {
                    byVendor[row.VendorId] = byVendor.GetValueOrDefault(row.VendorId) + row.Quantity;
                }

                total += row.Quantity;
            }

            return new ShareTotals(byCountry, byVendor, total);
        }

        public decimal? CountryCells(string? country) =>
            country is null ? null : _byCountry.GetValueOrDefault(country);

        public decimal? VendorCells(string? vendorId) =>
            vendorId is null ? null : _byVendor.GetValueOrDefault(vendorId);
    }
}

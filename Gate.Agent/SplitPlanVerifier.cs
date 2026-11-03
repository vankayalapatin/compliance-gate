// Split-plan verifier: pure C# judgment over a Spark-proposed whole-carton
// split. Every line must pass the full policy check at its own quantity
// with the earlier lines counted as open (the same order the POs are
// created in, so same-country combinations are covered), the quantities
// must add up to the request in whole cartons, and the plan must beat
// the single-vendor award. A valid but costlier plan is blocked naming
// only the dollars left on the table, never the better composition: Spark
// either revises the quantities or explains in note why the plan stands.
// Anything less comes back as refusal reasons; nothing reaches a person
// until the whole plan verifies.
using Gate.Contracts;

namespace Gate.Agent;

/// <summary>Verified split plus the full-quantity screenings behind it: the
/// parent stores those as its decision evidence with no single winner.</summary>
public sealed record SplitVerification(
    SplitPlan? Plan,
    IReadOnlyList<string> Errors,
    IReadOnlyList<VendorScreening> FullScreenings,
    IReadOnlyDictionary<string, CorpExceptionGrant> Grants);

public static class SplitPlanVerifier
{
    /// <summary>Verifies a proposed split line by line, in order. Returns the
    /// plan with no errors, or the refusal reasons with no plan. A valid
    /// plan that a cheaper valid plan beats is refused with only the
    /// dollars left on the table unless note explains why it stands. Throws
    /// only for broken preconditions (no PR, unknown item), never for a bad
    /// plan.</summary>
    public static async Task<SplitVerification> VerifyAsync(
        IErpAdapter erp,
        IComplianceEngine compliance,
        CaseFile file,
        IReadOnlyList<string> candidates,
        IReadOnlyList<string> vendorIds,
        IReadOnlyList<decimal> quantities,
        IReadOnlyDictionary<string, string> countryNames,
        CancellationToken ct = default,
        string? note = null)
    {
        var errors = new List<string>();
        FailWhen(vendorIds.Count != quantities.Count, errors,
            "The proposal names a different count of vendors and quantities.");
        FailWhen(vendorIds.Count < 2, errors,
            "A split needs at least two lines; award a single vendor instead.");
        var current = file.Case;
        if (current.PrNumber is null)
        {
            throw new InvalidOperationException($"Case {current.CaseId} has no PR to split.");
        }

        var pr = await erp.GetPrAsync(current.PrNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PR {current.PrNumber} vanished before the split.");
        var item = await erp.GetItemAsync(current.ItemId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown item {current.ItemId}.");
        var others = pr.Lines
            .Where(l => !string.Equals(l.ItemId, current.ItemId, StringComparison.Ordinal))
            .Select(l => l.ItemId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        FailWhen(others.Count > 0, errors,
            $"The request also asks for {string.Join(", ", others)}; splits cover one part only.");
        var fullQty = pr.Lines
            .Where(l => string.Equals(l.ItemId, current.ItemId, StringComparison.Ordinal))
            .Sum(l => l.Quantity);
        FailWhen(fullQty <= 0, errors, "The request asks for no quantity of the case part.");
        FailWhen(quantities.Sum() != fullQty, errors,
            $"The split adds up to {DisplayWords.Qty(quantities.Sum())} but the request asks for {DisplayWords.Qty(fullQty)}.");

        var vendors = (await erp.GetVendorsAsync(ct).ConfigureAwait(false))
            .ToDictionary(v => v.VendorId, StringComparer.Ordinal);
        var carton = item.UnitsPerCarton <= 0 ? 1 : item.UnitsPerCarton;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < vendorIds.Count && i < quantities.Count; i++)
        {
            var vendorId = vendorIds[i];
            var qty = quantities[i];
            FailWhen(qty <= 0, errors, $"Line {i + 1} orders no quantity.");
            FailWhen(qty % carton != 0, errors,
                $"Line {i + 1} ({vendorId} x{DisplayWords.Qty(qty)}) is not whole cartons of {DisplayWords.Count(carton)}.");
            FailWhen(!vendors.ContainsKey(vendorId), errors, $"Line {i + 1} names unknown vendor {vendorId}.");
            FailWhen(!seen.Add(vendorId), errors, $"The plan names {vendorId} twice; combine it into one line.");
            FailWhen(!candidates.Contains(vendorId, StringComparer.Ordinal), errors,
                $"Line {i + 1} names {vendorId}, who was not screened for this buy.");
        }

        var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var vendorId in vendorIds.Concat(candidates).Distinct(StringComparer.Ordinal))
        {
            if (!vendors.ContainsKey(vendorId))
            {
                continue;
            }

            var price = await erp.GetVendorPriceAsync(vendorId, current.ItemId, ct).ConfigureAwait(false);
            if (price is null)
            {
                if (vendorIds.Contains(vendorId, StringComparer.Ordinal))
                {
                    errors.Add($"{vendorId} names no price for {current.ItemId}.");
                }
            }
            else
            {
                prices[vendorId] = price.Value;
            }
        }

        if (errors.Count > 0)
        {
            return new SplitVerification(null, errors, [], new Dictionary<string, CorpExceptionGrant>());
        }

        var history = await erp.GetVolumeHistoryAsync(current.ItemId, ct).ConfigureAwait(false);
        var preOpen = await erp.GetOpenOrderQuantitiesAsync(current.ItemId, ct).ConfigureAwait(false);
        var full = await ScreenAtAsync(
            erp, compliance, file, candidates, vendors, item, history, preOpen, fullQty, ct).ConfigureAwait(false);
        string? cheapestVendor = null;
        var cheapestPrice = decimal.MaxValue;
        foreach (var candidate in candidates)
        {
            if (!vendors.ContainsKey(candidate))
            {
                continue;
            }

            var price = await erp.GetVendorPriceAsync(candidate, current.ItemId, ct).ConfigureAwait(false);
            if (price is not null && price.Value < cheapestPrice)
            {
                cheapestVendor = candidate;
                cheapestPrice = price.Value;
            }
        }

        if (cheapestVendor is null)
        {
            errors.Add("No candidate names a price; nothing can be ordered.");
            return new SplitVerification(null, errors, full.Screenings, full.Grants);
        }

        var cheapestScreening = full.Screenings.FirstOrDefault(s =>
            string.Equals(s.VendorId, cheapestVendor, StringComparison.Ordinal));
        if (cheapestScreening is not null && cheapestScreening.Passed)
        {
            errors.Add(
                $"Cheapest vendor {cheapestVendor} passes for the full {DisplayWords.Qty(fullQty)}; " +
                "award it instead of splitting.");
            return new SplitVerification(null, errors, full.Screenings, full.Grants);
        }

        decimal? baseline = null;
        foreach (var screening in full.Screenings)
        {
            if (!screening.Passed)
            {
                continue;
            }

            var price = await erp.GetVendorPriceAsync(screening.VendorId, current.ItemId, ct).ConfigureAwait(false);
            if (price is not null)
            {
                var total = price.Value * fullQty;
                baseline = baseline is null ? total : Math.Min(baseline.Value, total);
            }
        }

        // Each line passes on its own with the earlier lines counted as
        // open: the same order the POs are created in, so a same-country
        // combination can never slip through the per-line checks.
        var (lineErrors, lines) = await CheckLinesAsync(
            erp, compliance, file, vendors, item, history, preOpen,
            vendorIds, quantities, ct).ConfigureAwait(false);
        errors.AddRange(lineErrors);
        if (errors.Count > 0)
        {
            return new SplitVerification(null, errors, full.Screenings, full.Grants);
        }

        var splitTotal = lines.Sum(l => prices[l.VendorId] * l.Quantity);
        decimal? saving = baseline is null ? null : baseline.Value - splitTotal;
        if (saving is not null && saving.Value <= 0)
        {
            errors.Add(
                $"The split costs {DisplayWords.Money(splitTotal, pr.Currency)} against a single award of " +
                $"{DisplayWords.Money(baseline!.Value, pr.Currency)}: no saving.");
            return new SplitVerification(null, errors, full.Screenings, full.Grants);
        }

        var best = await BestTotalAsync(
            erp, compliance, file, vendors, item, history, preOpen,
            candidates, prices, fullQty, carton, ct).ConfigureAwait(false);
        if (best is not null && best.Value < splitTotal && string.IsNullOrWhiteSpace(note))
        {
            errors.Add(
                $"A valid cheaper plan exists with " +
                $"{DisplayWords.Money(splitTotal - best.Value, pr.Currency)} left on the table; " +
                "revise the quantities or explain in note why this plan stands.");
            return new SplitVerification(null, errors, full.Screenings, full.Grants);
        }

        var splitCells = lines.Sum(l => l.Quantity);
        var historyTotal = history.Sum(h => h.Cells);
        var openTotal = preOpen.Sum(o => o.Cells);
        var shares = lines
            .Select(l => vendors[l.VendorId].CountryOfOrigin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(country =>
            {
                var historyCells = history
                    .Where(h => string.Equals(h.Country, country, StringComparison.OrdinalIgnoreCase))
                    .Sum(h => h.Cells);
                var openCells = preOpen
                    .Where(o => string.Equals(o.Country, country, StringComparison.OrdinalIgnoreCase))
                    .Sum(o => o.Cells);
                var splitCountry = lines
                    .Where(l => string.Equals(
                        vendors[l.VendorId].CountryOfOrigin, country, StringComparison.OrdinalIgnoreCase))
                    .Sum(l => l.Quantity);
                return new SplitCountryShare(
                    country,
                    countryNames.TryGetValue(country, out var name) ? name : country,
                    (historyCells + openCells + splitCountry) / (historyTotal + openTotal + splitCells));
            })
            .OrderBy(s => s.Country, StringComparer.Ordinal)
            .ToList();
        var plan = new SplitPlan(lines, shares, splitTotal, baseline, saving, pr.Currency);
        return new SplitVerification(plan, [], full.Screenings, full.Grants);
    }

    /// <summary>Checks lines in order with the earlier lines counted as
    /// open, exactly as the POs would create. Returns the refusal reasons
    /// with no lines, or the lines with no reasons.</summary>
    private static async Task<(IReadOnlyList<string> Errors, IReadOnlyList<SplitPlanLine> Lines)> CheckLinesAsync(
        IErpAdapter erp,
        IComplianceEngine compliance,
        CaseFile file,
        IReadOnlyDictionary<string, VendorDto> vendors,
        ItemDto item,
        IReadOnlyList<VolumeHistory> history,
        IReadOnlyList<OpenOrderQty> preOpen,
        IReadOnlyList<string> vendorIds,
        IReadOnlyList<decimal> quantities,
        CancellationToken ct)
    {
        var errors = new List<string>();
        var running = preOpen.ToList();
        var lines = new List<SplitPlanLine>();
        for (var i = 0; i < vendorIds.Count; i++)
        {
            var vendorId = vendorIds[i];
            var qty = quantities[i];
            var vendor = vendors[vendorId];
            var docs = await erp.GetVendorDocumentsAsync(vendorId, ct).ConfigureAwait(false);
            var award = new ProposedAward(item.ItemId, qty, vendor.CountryOfOrigin);
            var verdicts = await compliance.EvaluateVendorAsync(
                vendor, item, docs, award, history, running, ct).ConfigureAwait(false);
            var raw = new VendorScreening(vendorId, verdicts.All(v => v.Kind == VerdictKind.Pass), verdicts);
            var effective = CorpExceptionFlow.ApplyApprovedExceptions(file, [raw]).EffectiveScreenings[0];
            if (!effective.Passed)
            {
                var blocks = effective.Verdicts
                    .Where(v => v.Kind == VerdictKind.Block)
                    .Select(v => v.RuleId)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                errors.Add(blocks.Count == 0
                    ? $"Line {i + 1} ({vendorId} x{DisplayWords.Qty(qty)}) does not pass."
                    : $"Line {i + 1} ({vendorId} x{DisplayWords.Qty(qty)}) fails: {string.Join(", ", blocks)}.");
                continue;
            }

            lines.Add(new SplitPlanLine(vendorId, vendor.Name, qty, effective.Verdicts));
            running.Add(new OpenOrderQty($"Split line {i + 1}", vendor.CountryOfOrigin, qty));
        }

        return errors.Count > 0
            ? (errors, [])
            : (errors, lines);
    }

    /// <summary>Lowest valid total over single-vendor awards plus every
    /// two-vendor whole-carton split in both line orders, each judged by
    /// the same per-line check in creation order; null when no valid plan
    /// exists. Ties keep the earliest plan via strict comparison, so the
    /// search is deterministic.</summary>
    private static async Task<decimal?> BestTotalAsync(
        IErpAdapter erp,
        IComplianceEngine compliance,
        CaseFile file,
        IReadOnlyDictionary<string, VendorDto> vendors,
        ItemDto item,
        IReadOnlyList<VolumeHistory> history,
        IReadOnlyList<OpenOrderQty> preOpen,
        IReadOnlyList<string> candidates,
        IReadOnlyDictionary<string, decimal> prices,
        decimal fullQty,
        int carton,
        CancellationToken ct)
    {
        decimal? best = null;
        foreach (var candidate in candidates)
        {
            if (!prices.TryGetValue(candidate, out var price))
            {
                continue;
            }

            var (singleErrors, _) = await CheckLinesAsync(
                erp, compliance, file, vendors, item, history, preOpen,
                [candidate], [fullQty], ct).ConfigureAwait(false);
            if (singleErrors.Count == 0)
            {
                var total = price * fullQty;
                if (best is null || total < best.Value)
                {
                    best = total;
                }
            }
        }

        if (fullQty % carton != 0)
        {
            return best;
        }

        var totalCartons = (int)(fullQty / carton);
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = 0; j < candidates.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                if (!prices.TryGetValue(candidates[i], out var firstPrice)
                    || !prices.TryGetValue(candidates[j], out var secondPrice))
                {
                    continue;
                }

                for (var cartons = 1; cartons < totalCartons; cartons++)
                {
                    var firstQty = cartons * carton;
                    var secondQty = (totalCartons - cartons) * carton;
                    var (pairErrors, _) = await CheckLinesAsync(
                        erp, compliance, file, vendors, item, history, preOpen,
                        [candidates[i], candidates[j]], [firstQty, secondQty], ct)
                        .ConfigureAwait(false);
                    if (pairErrors.Count > 0)
                    {
                        continue;
                    }

                    var total = (firstPrice * firstQty) + (secondPrice * secondQty);
                    if (best is null || total < best.Value)
                    {
                        best = total;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>One-line card prose for a verified plan: each line, the final
    /// country shares, and the saving (or why there is no single award to
    /// beat). Percents use the fixed no-space shape, like the sentences.</summary>
    public static string DescribePlan(SplitPlan plan, string itemId, string unitNoun)
    {
        var total = plan.Lines.Sum(l => l.Quantity);
        var lines = string.Join(", ", plan.Lines.Select(l =>
            $"{DisplayWords.Qty(l.Quantity)} to {l.VendorName}"));
        var shares = string.Join(", ", plan.FinalShares.Select(s =>
            $"{s.Country} {Percent(s.Share)}"));
        var money = plan.Saving is null || plan.SingleVendorTotal is null
            ? $"Split {DisplayWords.Money(plan.SplitTotal, plan.Currency)}. " +
              "Single award: no vendor passes for the full quantity."
            : $"Split {DisplayWords.Money(plan.SplitTotal, plan.Currency)} against a single award of " +
              $"{DisplayWords.Money(plan.SingleVendorTotal.Value, plan.Currency)}: " +
              $"saves {DisplayWords.Money(plan.Saving.Value, plan.Currency)}.";
        return $"Split {DisplayWords.Qty(total)} {unitNoun} ({itemId}): {lines}. " +
            $"Final shares: {shares}. {money}";
    }

    private static string Percent(decimal share) =>
        (share * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";

    private static void FailWhen(bool condition, List<string> errors, string message)
    {
        if (condition)
        {
            errors.Add(message);
        }
    }

    private static async Task<(IReadOnlyList<VendorScreening> Screenings, IReadOnlyDictionary<string, CorpExceptionGrant> Grants)>
        ScreenAtAsync(
            IErpAdapter erp, IComplianceEngine compliance, CaseFile file,
            IReadOnlyList<string> candidates, IReadOnlyDictionary<string, VendorDto> vendors,
            ItemDto item, IReadOnlyList<VolumeHistory> history, IReadOnlyList<OpenOrderQty> open,
            decimal qty, CancellationToken ct)
    {
        var raw = new List<VendorScreening>();
        foreach (var vendorId in candidates)
        {
            if (!vendors.TryGetValue(vendorId, out var vendor))
            {
                continue;
            }

            var docs = await erp.GetVendorDocumentsAsync(vendorId, ct).ConfigureAwait(false);
            var award = new ProposedAward(item.ItemId, qty, vendor.CountryOfOrigin);
            var verdicts = await compliance.EvaluateVendorAsync(
                vendor, item, docs, award, history, open, ct).ConfigureAwait(false);
            raw.Add(new VendorScreening(vendorId, verdicts.All(v => v.Kind == VerdictKind.Pass), verdicts));
        }

        var applied = CorpExceptionFlow.ApplyApprovedExceptions(file, raw);
        return (applied.EffectiveScreenings, applied.Grants);
    }

}

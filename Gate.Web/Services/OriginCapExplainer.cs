// Origin-cap explainer: one case's screening-math sentences for the feed
// and the case page. Decided orders render the stored award-time
// sentences with only the link targets resolving live; only orders not
// yet decided gather inputs from the platform db and the ERP adapter at
// render time. Every screened vendor gets the same sentence shape,
// blocked or passing, except on non-critical items, where the cap does
// not apply and no sentence shows (the screening table already says so);
// anything unreadable (vendor, PR, history, rule config) skips that line
// with a log entry, never a throw, so the feed and the case page always
// render.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed record OriginCapLine(string VendorId, MathLineVm Line);

public static class OriginCapExplainer
{
    public static async Task<IReadOnlyList<OriginCapLine>> LinesAsync(
        IErpAdapter erp, CaseFile file, ILogger? log,
        IReadOnlyDictionary<string, string>? countryNames = null,
        CancellationToken ct = default,
        ComplianceEngine? engine = null)
    {
        // Origin-cap sentences only: vendors blocked by the origin-cap rule
        // plus vendors who passed screening entirely. Vendors blocked by
        // other rules tell their story in the screening table instead.
        var screened = file.Events
            .Where(e => e.Kind == CaseEventKind.VendorScreened
                && e.Summary.StartsWith("Vendor ", StringComparison.Ordinal)
                && (e.RuleId is null
                    || string.Equals(e.RuleId, OriginCapMath.OriginCapRuleId, StringComparison.Ordinal)))
            .OrderBy(e => e.Timestamp)
            .ToList();
        if (screened.Count == 0 || file.Case.PrNumber is null)
        {
            return [];
        }

        // The cap applies to critical items only: on anything else the
        // table's not-applicable reason stands alone, with no sentence
        // beside it. Unknown items fail open to the sentence (status quo).
        var capApplies = true;
        try
        {
            var gated = await erp.GetItemAsync(file.Case.ItemId, ct).ConfigureAwait(false);
            if (gated is not null)
            {
                capApplies = gated.IsCritical;
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Screening math for case {CaseId} could not read item {Item}.", file.Case.CaseId, file.Case.ItemId);
        }

        if (!capApplies)
        {
            return [];
        }

        // Decided orders render the stored sentences verbatim: the figures
        // are the award's, only the hrefs resolve live. The event filter
        // still applies, so vendors blocked by other rules keep telling
        // their story in the screening table instead of here.
        if (file.ScreeningSnapshot is not null)
        {
            var stored = file.ScreeningSnapshot.Rows
                .Where(r => r.Math is not null)
                .ToDictionary(r => r.VendorId, r => r.Math!, StringComparer.Ordinal);
            var storedHistoryHref = OriginCapMath.HistoryHref(file.ScreeningSnapshot.ItemId);
            var storedQtyHref = ErpRoutes.RecordHref("pr", file.ScreeningSnapshot.PrNumber);
            var kept = new List<OriginCapLine>();
            foreach (var e in screened)
            {
                var vendorId = OriginCapMath.TryParseScreenedVendor(e.Summary);
                if (vendorId is null
                    || kept.Any(l => string.Equals(l.VendorId, vendorId, StringComparison.Ordinal))
                    || !stored.TryGetValue(vendorId, out var math))
                {
                    continue;
                }

                kept.Add(new OriginCapLine(vendorId, OriginCapMath.FromStored(math, storedHistoryHref, storedQtyHref)));
            }

            return kept;
        }

        // The count noun comes from the item description; a missing item
        // still yields lines with the fallback noun.
        string noun = "units";
        try
        {
            var item = await erp.GetItemAsync(file.Case.ItemId, ct).ConfigureAwait(false);
            if (item is not null)
            {
                noun = DisplayWords.CountNoun(item.Description);
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Screening math for case {CaseId} could not read item {Item}.", file.Case.CaseId, file.Case.ItemId);
        }

        Dictionary<string, VendorDto> vendors;
        try
        {
            vendors = (await erp.GetVendorsAsync(ct).ConfigureAwait(false))
                .ToDictionary(v => v.VendorId, v => v, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Screening math for case {CaseId} could not list vendors.", file.Case.CaseId);
            return [];
        }

        IReadOnlyList<VolumeHistory> history;
        try
        {
            history = await erp.GetVolumeHistoryAsync(file.Case.ItemId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Screening math for case {CaseId} could not read volume history.", file.Case.CaseId);
            return [];
        }

        IReadOnlyList<OpenOrderQty> open;
        try
        {
            open = await erp.GetOpenOrderQuantitiesAsync(file.Case.ItemId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Screening math for case {CaseId} could not read open orders.", file.Case.CaseId);
            return [];
        }

        PurchaseRequisition? pr;
        try
        {
            pr = await erp.GetPrAsync(file.Case.PrNumber, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Screening math for case {CaseId} could not read PR {Pr}.", file.Case.CaseId, file.Case.PrNumber);
            return [];
        }

        if (pr is null)
        {
            return [];
        }

        decimal cap;
        try
        {
            cap = engine?.CurrentCorpConfig().OriginCap ?? RuleCatalog.Corp.OriginCap;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Screening math for case {CaseId} could not read the rule config.", file.Case.CaseId);
            return [];
        }

        var total = history.Sum(h => h.Cells);
        var qty = pr.Lines
            .Where(l => string.Equals(l.ItemId, file.Case.ItemId, StringComparison.Ordinal))
            .Sum(l => l.Quantity);
        var historyHref = OriginCapMath.HistoryHref(file.Case.ItemId);
        var qtyHref = ErpRoutes.RecordHref("pr", pr.PrNumber);
        var excused = file.Decisions
            .Where(d => d.Kind == DecisionKind.ExceptionApproval
                && d.Status == DecisionStatus.Approved
                && string.Equals(d.RuleId, OriginCapMath.OriginCapRuleId, StringComparison.Ordinal)
                && d.DocumentNumber is not null)
            .Select(d => d.DocumentNumber!)
            .ToHashSet(StringComparer.Ordinal);
        var lines = new List<OriginCapLine>();
        foreach (var e in screened)
        {
            var vendorId = OriginCapMath.TryParseScreenedVendor(e.Summary);
            if (vendorId is null)
            {
                log?.LogWarning(
                    "Screening math for case {CaseId} could not parse the blocked vendor from: {Summary}.",
                    file.Case.CaseId, e.Summary);
                continue;
            }

            if (lines.Any(l => string.Equals(l.VendorId, vendorId, StringComparison.Ordinal)))
            {
                continue;
            }

            if (!vendors.TryGetValue(vendorId, out var vendor))
            {
                log?.LogWarning(
                    "Screening math for case {CaseId} could not find vendor {VendorId}.",
                    file.Case.CaseId, vendorId);
                continue;
            }

            // Shares need a real denominator: without history or open
            // orders the sentence would divide by the award alone and
            // misstate the outcome.
            var totalOpen = open.Sum(o => o.Cells);
            if (total + totalOpen <= 0)
            {
                continue;
            }

            var countryCells = history
                .Where(h => string.Equals(h.Country, vendor.CountryOfOrigin, StringComparison.OrdinalIgnoreCase))
                .Sum(h => h.Cells);
            var countryOpen = open
                .Where(o => string.Equals(o.Country, vendor.CountryOfOrigin, StringComparison.OrdinalIgnoreCase))
                .Sum(o => o.Cells);
            var line = OriginCapMath.Build(
                OriginCapMath.OriginCapRuleId, vendor.Name, vendor.CountryOfOrigin,
                countryCells, total, qty, cap, historyHref, qtyHref, noun, countryNames,
                excused.Contains(vendorId, StringComparer.Ordinal),
                countryOpen, totalOpen);
            if (line is null)
            {
                continue;
            }

            lines.Add(new OriginCapLine(vendorId, line));
        }

        return lines;
    }
}

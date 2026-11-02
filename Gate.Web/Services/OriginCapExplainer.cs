// Origin-cap explainer: gathers one case's screening-math inputs from the
// platform db and the ERP adapter at render time. Vendors blocked under the
// origin-cap rule get a math line each; anything unreadable (vendor, PR,
// history, rule config) skips that line with a log entry, never a throw,
// so the feed and the case page always render.
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
        CancellationToken ct = default)
    {
        var blocked = file.Events
            .Where(e => e.Kind is CaseEventKind.VendorBlocked or CaseEventKind.Note
                && string.Equals(e.RuleId, OriginCapMath.OriginCapRuleId, StringComparison.Ordinal)
                && e.Summary.StartsWith("Vendor ", StringComparison.Ordinal))
            .OrderBy(e => e.Timestamp)
            .ToList();
        if (blocked.Count == 0 || file.Case.PrNumber is null)
        {
            return [];
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
            cap = RuleCatalog.Corp.OriginCap;
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
        var lines = new List<OriginCapLine>();
        foreach (var e in blocked)
        {
            var vendorId = OriginCapMath.TryParseBlockedVendor(e.Summary);
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

            var countryCells = history
                .Where(h => string.Equals(h.Country, vendor.CountryOfOrigin, StringComparison.OrdinalIgnoreCase))
                .Sum(h => h.Cells);
            var line = OriginCapMath.Build(
                OriginCapMath.OriginCapRuleId, vendor.Name, vendor.CountryOfOrigin,
                countryCells, total, qty, cap, historyHref, qtyHref, countryNames);
            if (line is null)
            {
                continue;
            }

            lines.Add(new OriginCapLine(vendorId, line));
        }

        return lines;
    }
}

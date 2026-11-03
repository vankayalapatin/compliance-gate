// Vendor screening table: one row per screened candidate for the case
// page, the PR/PO records, and the approval cards. Decided orders show
// the stored award-time copy (verdicts, sentences, and figures as they
// stood); only orders not yet decided re-run screening live (pure reads
// plus deterministic compliance verdicts, the same call the award
// validates against). Anything unreadable skips with a log entry, never
// a throw.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public static class VendorScreeningTable
{
    public static async Task<IReadOnlyList<ScreeningRowVm>> RowsAsync(
        IErpAdapter erp,
        P2pAgent agent,
        CaseFile file,
        ILogger? log,
        IReadOnlyDictionary<string, string>? countryNames = null,
        CancellationToken ct = default)
    {
        var kase = file.Case;
        if (kase.PrNumber is null)
        {
            return [];
        }

        // Decided orders render the stored copy with no live calls, so
        // later receipts and postings cannot rewrite the decision's
        // evidence. Only the link targets resolve live, in the math lines.
        if (file.ScreeningSnapshot is not null)
        {
            return file.ScreeningSnapshot.Rows
                .Select(r => new ScreeningRowVm(
                    r.VendorId,
                    r.VendorName,
                    r.Country,
                    r.UnitPrice is null ? "no price" : ErpFormat.Money(r.UnitPrice.Value, r.Currency),
                    r.Passed,
                    r.Result,
                    r.Reason,
                    r.Awarded))
                .ToList();
        }

        var orderedIds = file.Events
            .Where(e => e.Kind == CaseEventKind.VendorScreened
                && e.Summary.StartsWith("Vendor ", StringComparison.Ordinal))
            .OrderBy(e => e.Timestamp)
            .Select(e => OriginCapMath.TryParseScreenedVendor(e.Summary))
            .Where(id => id is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (orderedIds.Count == 0)
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
            log?.LogWarning(ex, "Screening table for case {CaseId} could not list vendors.", kase.CaseId);
            return [];
        }

        string? currency;
        try
        {
            currency = (await erp.GetPrAsync(kase.PrNumber, ct).ConfigureAwait(false))?.Currency;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Screening table for case {CaseId} could not read PR {Pr}.", kase.CaseId, kase.PrNumber);
            return [];
        }

        IReadOnlyList<VendorScreening> screenings;
        try
        {
            var raw = await agent.ScreenCandidatesAsync(kase.PrNumber, orderedIds, ct).ConfigureAwait(false);
            screenings = CorpExceptionFlow.ApplyApprovedExceptions(file, raw).EffectiveScreenings;
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Screening table for case {CaseId} could not re-run screening.", kase.CaseId);
            return [];
        }

        var rows = new List<ScreeningRowVm>();
        foreach (var screening in screenings)
        {
            if (!vendors.TryGetValue(screening.VendorId, out var vendor))
            {
                log?.LogWarning(
                    "Screening table for case {CaseId} could not find vendor {VendorId}.",
                    kase.CaseId, screening.VendorId);
                continue;
            }

            string price;
            try
            {
                var unit = await erp.GetVendorPriceAsync(screening.VendorId, kase.ItemId, ct).ConfigureAwait(false);
                price = unit is null ? "no price" : ErpFormat.Money(unit.Value, currency);
            }
            catch (NotSupportedException)
            {
                price = "no price";
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "Screening table for case {CaseId} could not read the price for {Vendor}.", kase.CaseId, screening.VendorId);
                price = "no price";
            }

            var country = OriginCapMath.CountryName(countryNames, vendor.CountryOfOrigin);
            rows.Add(new ScreeningRowVm(
                screening.VendorId,
                vendor.Name,
                country,
                price,
                screening.Passed,
                ScreeningRowWords.ResultFor(screening.Passed, screening.Verdicts),
                ScreeningRowWords.ReasonFor(screening.Verdicts, ComplianceEngine.CorpOriginCapId),
                string.Equals(kase.AwardedVendorId, screening.VendorId, StringComparison.Ordinal)));
        }

        return rows;
    }
}

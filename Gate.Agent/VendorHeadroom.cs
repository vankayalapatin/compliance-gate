// Vendor headroom facts (agent side). Spark plans quantity splits from facts,
// not guesses: per candidate vendor, the largest whole-carton quantity the
// vendor can still take with the rest of the request already open (last
// position), the unit price, and the rule binding it when the full request
// does not fit. Quantities come from the compliance engine; prices come from
// the ERP adapter; everything is read, never invented.
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Agent;

/// <summary>One priced headroom row behind the get_vendor_headroom result.</summary>
public sealed record VendorHeadroomRow(
    string VendorId,
    string VendorName,
    decimal UnitPrice,
    decimal HeadroomQty,
    int HeadroomCartons,
    bool FullRequestPossible,
    string? BindingRuleId,
    string? BindingReason);

/// <summary>Headroom facts for the case item, in candidate order.</summary>
public sealed record VendorHeadroomFacts(
    string Item,
    decimal RequestedQty,
    int CartonSize,
    IReadOnlyList<VendorHeadroomRow> Vendors)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Pinned result shape: item, requestedQty, cartonSize, and one
    /// row per vendor with vendorId, vendorName, unitPrice, headroomQty,
    /// headroomCartons, fullRequestPossible, bindingRuleId, bindingReason
    /// (nulls explicit).</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(new
        {
            Item,
            RequestedQty,
            CartonSize,
            Vendors = Vendors.Select(v => new
            {
                v.VendorId,
                v.VendorName,
                v.UnitPrice,
                v.HeadroomQty,
                v.HeadroomCartons,
                v.FullRequestPossible,
                v.BindingRuleId,
                v.BindingReason,
            }).ToList(),
        }, JsonOpts);
}

/// <summary>Headroom source behind the get_vendor_headroom read tool.</summary>
public interface IVendorHeadroomSource
{
    Task<VendorHeadroomFacts> GetHeadroomAsync(string caseId, CancellationToken ct = default);
}

/// <summary>Headroom over adapter reads plus the compliance engine. The
/// requested quantity comes from the case PR line for the item; candidates
/// come from the case record; history, open orders, and the carton size
/// come from the ERP masters. A vendor without a price refuses loudly.</summary>
public sealed class VendorHeadroomService : IVendorHeadroomSource
{
    private readonly IErpAdapter _erp;
    private readonly IComplianceEngine _compliance;
    private readonly string _cacheDir;

    public VendorHeadroomService(
        IErpAdapter erp,
        IComplianceEngine compliance,
        string cacheDir)
    {
        _erp = erp;
        _compliance = compliance;
        _cacheDir = cacheDir;
    }

    public async Task<VendorHeadroomFacts> GetHeadroomAsync(string caseId, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var current = file.Case;
        if (current.PrNumber is null)
        {
            throw new InvalidOperationException(
                $"Case {caseId} has no PR; get_vendor_headroom needs the requested quantity from the PR line.");
        }

        var pr = await _erp.GetPrAsync(current.PrNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Case {caseId} PR {current.PrNumber} vanished.");
        var requestedQty = pr.Lines
            .Where(l => string.Equals(l.ItemId, current.ItemId, StringComparison.Ordinal))
            .Sum(l => l.Quantity);
        if (requestedQty <= 0)
        {
            throw new InvalidOperationException(
                $"Case {caseId} PR {pr.PrNumber} carries no quantity for {current.ItemId}; headroom needs the requested quantity.");
        }

        var item = await _erp.GetItemAsync(current.ItemId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown item {current.ItemId}.");
        if (item.UnitsPerCarton <= 0)
        {
            throw new InvalidOperationException(
                $"Item {item.ItemId} carries no carton size; headroom needs whole cartons.");
        }

        var candidates = CaseRunner.ReadCandidates(file, current.ItemId);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No candidate vendors recorded for {current.ItemId}: the order webhook must carry sourcing candidates per item.");
        }

        var vendorById = (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
            .ToDictionary(v => v.VendorId, StringComparer.Ordinal);
        var history = await _erp.GetVolumeHistoryAsync(item.ItemId, ct).ConfigureAwait(false);
        var open = (await _erp.GetOpenOrderQuantitiesAsync(item.ItemId, ct).ConfigureAwait(false))
            .Select(o => new VolumeHistory(o.Source, o.Country, o.Cells))
            .ToList();
        var rows = new List<VendorHeadroomRow>();
        foreach (var vendorId in candidates)
        {
            if (!vendorById.TryGetValue(vendorId, out var vendor))
            {
                throw new InvalidOperationException($"Unknown vendor {vendorId}.");
            }

            var unitPrice = await _erp.GetVendorPriceAsync(vendorId, item.ItemId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Vendor {vendorId} names no price for {item.ItemId}; headroom facts cannot be priced.");
            var documents = await _erp.GetVendorDocumentsAsync(vendorId, ct).ConfigureAwait(false);
            var headroom = await _compliance.HeadroomAsync(
                vendor, item, documents, requestedQty, history, open, item.UnitsPerCarton, ct)
                .ConfigureAwait(false);
            rows.Add(new VendorHeadroomRow(
                vendor.VendorId, vendor.Name, unitPrice,
                headroom.HeadroomQty, headroom.HeadroomCartons, headroom.FullRequestPossible,
                headroom.BindingRuleId, headroom.BindingReason));
        }

        return new VendorHeadroomFacts(item.ItemId, requestedQty, item.UnitsPerCarton, rows);
    }
}

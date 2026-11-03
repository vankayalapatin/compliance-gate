// Vendor headroom: the most a supplier can provide under every cap,
// open orders counted, in whole cartons. The engine decides each verdict;
// this method binary-searches whole-carton quantities for the largest one
// at which every verdict passes. Binding names the rule failing just above
// headroom, cited from the engine's own verdicts, never invented text.
using Gate.Contracts;

namespace Gate.Compliance;

public sealed partial class ComplianceEngine
{
    /// <summary>Headroom for one supplier: the largest whole-carton quantity
    /// at or below the request at which every vendor-gate verdict passes.
    /// Open orders join history in the cap denominator and numerator, so the
    /// share counts posted plus committed volume. The rest of the request
    /// rides as open rest volume, so the figure reads as last-position
    /// headroom: the most the vendor can supply when the remainder ships
    /// around it. FullRequestPossible is true only when the exact request
    /// passes; headroom itself always rounds down to whole cartons. Binding
    /// names the first rule failing just above headroom (null when the full
    /// request passes); headroom zero with a binding reason means unusable at
    /// any quantity.</summary>
    public async Task<VendorHeadroom> HeadroomAsync(
        VendorDto vendor,
        ItemDto item,
        IReadOnlyList<VendorDocumentDto> documents,
        decimal requestedQty,
        IReadOnlyList<VolumeHistory> history,
        IReadOnlyList<VolumeHistory> openOrders,
        int cartonSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(openOrders);
        if (cartonSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cartonSize), cartonSize, "Carton size must be a positive unit count.");
        }

        if (requestedQty < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedQty), requestedQty, "Requested quantity cannot be negative.");
        }

        ct.ThrowIfCancellationRequested();

        var effectiveHistory = history.Concat(openOrders).ToList();

        async Task<IReadOnlyList<Verdict>> EvaluateAtAsync(decimal qty)
        {
            // The rest of the request ships around this vendor: it counts as
            // open rest volume in the denominator (never in this vendor's own
            // country numerator). At the full request the rest is zero and the
            // check is the plain single-vendor screen.
            var rest = requestedQty - qty;
            var positioned = rest <= 0
                ? effectiveHistory
                : effectiveHistory
                    .Concat([new VolumeHistory("Rest of request", "OTHER", rest)])
                    .ToList();
            var award = new ProposedAward(item.ItemId, qty, vendor.CountryOfOrigin);
            return await EvaluateVendorWithExceptionAsync(
                vendor, item, documents, award, positioned, null, ct).ConfigureAwait(false);
        }

        static Verdict? FirstFailure(IReadOnlyList<Verdict> verdicts)
        {
            foreach (var verdict in verdicts)
            {
                if (verdict.Kind != VerdictKind.Pass)
                {
                    return verdict;
                }
            }

            return null;
        }

        var maxCartons = (int)Math.Floor(requestedQty / cartonSize);
        var maxQty = (decimal)maxCartons * cartonSize;

        var fullVerdicts = await EvaluateAtAsync(requestedQty).ConfigureAwait(false);
        var fullFailure = FirstFailure(fullVerdicts);
        if (fullFailure is null)
        {
            return new VendorHeadroom(vendor.VendorId, maxQty, maxCartons, true, null, null);
        }

        var zeroVerdicts = requestedQty == 0
            ? fullVerdicts
            : await EvaluateAtAsync(0).ConfigureAwait(false);
        var zeroFailure = FirstFailure(zeroVerdicts);
        if (zeroFailure is not null)
        {
            return new VendorHeadroom(vendor.VendorId, 0, 0, false, zeroFailure.RuleId, zeroFailure.Reason);
        }

        if (maxCartons == 0)
        {
            return new VendorHeadroom(vendor.VendorId, 0, 0, false, fullFailure.RuleId, fullFailure.Reason);
        }

        if (maxQty != requestedQty)
        {
            var roundedVerdicts = await EvaluateAtAsync(maxQty).ConfigureAwait(false);
            if (FirstFailure(roundedVerdicts) is null)
            {
                return new VendorHeadroom(vendor.VendorId, maxQty, maxCartons, false, fullFailure.RuleId, fullFailure.Reason);
            }
        }

        var low = 0;
        var high = maxCartons;
        while (high - low > 1)
        {
            ct.ThrowIfCancellationRequested();
            var mid = (low + high) / 2;
            var qty = (decimal)mid * cartonSize;
            var verdicts = await EvaluateAtAsync(qty).ConfigureAwait(false);
            if (FirstFailure(verdicts) is null)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        var headQty = (decimal)low * cartonSize;
        var highQty = (decimal)high * cartonSize;
        var highVerdicts = await EvaluateAtAsync(highQty).ConfigureAwait(false);
        var binding = FirstFailure(highVerdicts) ?? fullFailure;
        return new VendorHeadroom(vendor.VendorId, headQty, low, false, binding.RuleId, binding.Reason);
    }
}

// ERP control-point policy hooks. At each control point the ERP gathers its
// own state and calls one policy check, refusing with the rule id, pack
// version, and plain reason the check returns. Sourcing (PO create and PO
// approve) runs per PO line with CORP-02 computed for that line's quantity.
// Payment draft, approve, and post run the whole-payment gate (open reviews
// block the whole payment); held-line release runs the held-value gate
// (any quarantine, hold, or review refuses). Held value never enters a
// draft: it stays unpayable until its reshot or review closes. Platform
// inputs the ERP cannot read itself (exception grants, open reviews)
// arrive through IErpPolicyContext. Reads use the ambient call context:
// one context per adapter call, no shared state. A null policy skips
// checks (unit tests without a policy keep their old behavior); the app
// always wires one.
using Gate.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Gate.Erp;

public sealed partial class EfCoreErpAdapter
{
    private readonly IErpPolicy? _policy;
    private readonly IErpPolicyContext? _policyContext;

    private async Task RequireSourcingAsync(
        ErpDbContext db, string vendorId, string itemId, decimal awardQuantity,
        string? prNumber, CancellationToken ct)
    {
        if (_policy is null)
        {
            return;
        }

        var vendor = await db.Vendors.FindAsync([vendorId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
        var item = await db.Items.FindAsync([itemId], ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown item {itemId}.");
        var docs = (await db.VendorDocuments
            .Where(d => d.VendorId == vendorId).OrderBy(d => d.Kind)
            .ToListAsync(ct).ConfigureAwait(false))
            .Select(d => new VendorDocumentDto(d.VendorId, d.Kind, d.Reference, d.Present))
            .ToList();
        var history = await VolumeHistoryForAsync(db, itemId, ct).ConfigureAwait(false);
        var grant = _policyContext is null
            ? null
            : await _policyContext.FindExceptionGrantAsync(
                ErpPolicyRules.CorpOriginCap, vendorId, prNumber, ct).ConfigureAwait(false);
        var result = await _policy.CheckSourcingAsync(new SourcingCheckInput(
            vendor.VendorId, vendor.Name, vendor.CountryOfOrigin, vendor.PaymentTermsId,
            item.ItemId, item.Description, item.IsCritical, awardQuantity,
            docs, history, grant), ct).ConfigureAwait(false);
        if (!result.Allowed)
        {
            throw new InvalidOperationException(result.Refusal!.Display);
        }
    }

    private async Task RequirePayableAsync(
        ErpDbContext db, IReadOnlyList<InvoiceKey> invoices, CancellationToken ct)
    {
        var input = await BuildPayableInputAsync(db, invoices, ct).ConfigureAwait(false);
        if (input is null)
        {
            return;
        }

        var result = await _policy!.CheckPayableAsync(input, ct).ConfigureAwait(false);
        if (!result.Allowed)
        {
            throw new InvalidOperationException(result.Refusal!.Display);
        }
    }

    private async Task RequireReleasableAsync(
        ErpDbContext db, IReadOnlyList<InvoiceKey> invoices, CancellationToken ct)
    {
        var input = await BuildPayableInputAsync(db, invoices, ct).ConfigureAwait(false);
        if (input is null)
        {
            return;
        }

        var result = await _policy!.CheckReleasableAsync(input, ct).ConfigureAwait(false);
        if (!result.Allowed)
        {
            throw new InvalidOperationException(result.Refusal!.Display);
        }
    }

    private async Task<PayableCheckInput?> BuildPayableInputAsync(
        ErpDbContext db, IReadOnlyList<InvoiceKey> invoices, CancellationToken ct)
    {
        if (_policy is null || invoices.Count == 0)
        {
            return null;
        }

        var invDocs = invoices
            .Select(k => InvoiceDoc(k.VendorId, k.SupplierInvoiceNumber))
            .ToHashSet(StringComparer.Ordinal);
        var poNumbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in invoices)
        {
            var inv = await db.VendorInvoices
                .FindAsync([key.VendorId, key.SupplierInvoiceNumber], ct).ConfigureAwait(false);
            if (inv?.PoNumber is not null)
            {
                poNumbers.Add(inv.PoNumber);
            }
        }

        var receiptNumbers = new HashSet<string>(StringComparer.Ordinal);
        var lots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var po in poNumbers)
        {
            var receipts = await db.ShipmentReceipts
                .Where(r => r.PoNumber == po).ToListAsync(ct).ConfigureAwait(false);
            foreach (var receipt in receipts)
            {
                receiptNumbers.Add(receipt.ReceiptNumber);
            }

            var receiptLines = await db.ShipmentReceiptLines
                .Where(l => db.ShipmentReceipts
                    .Where(r => r.PoNumber == po)
                    .Select(r => r.ReceiptNumber)
                    .Contains(l.ReceiptNumber))
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var line in receiptLines)
            {
                lots.Add(line.LotRef);
            }

            var mapped = await db.LotMap
                .Where(m => m.PoNumber == po).ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in mapped)
            {
                lots.Add(row.LotRef);
            }
        }

        var cartonIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var po in poNumbers)
        {
            var asnNumbers = await db.Asns
                .Where(a => a.PoNumber == po).Select(a => a.AsnNumber)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var asn in asnNumbers)
            {
                var cartons = await db.AsnCartons
                    .Where(c => c.AsnNumber == asn).ToListAsync(ct).ConfigureAwait(false);
                foreach (var carton in cartons)
                {
                    cartonIds.Add(carton.CartonId);
                }
            }
        }

        var holds = (await db.Holds
            .Where(h => h.Status == nameof(HoldStatus.Set))
            .OrderBy(h => h.HoldId).ToListAsync(ct).ConfigureAwait(false))
            .Where(h => (h.DocumentNumber is not null
                    && (invDocs.Contains(h.DocumentNumber)
                        || poNumbers.Contains(h.DocumentNumber)
                        || receiptNumbers.Contains(h.DocumentNumber)))
                || (h.LotRef is not null && lots.Contains(h.LotRef))
                || (h.CartonId is not null && cartonIds.Contains(h.CartonId)))
            .Select(h => new HoldDto(h.HoldId, h.ReasonCode, h.Note, h.RuleId, h.CaseId, h.SetBy,
                DateTime(h.SetAt), Enum<HoldStatus>(h.Status), h.DocumentType, h.DocumentNumber,
                h.PoLineRef, h.LotRef, h.CartonId))
            .ToList();
        // Client-side sum: SQLite cannot aggregate decimals server-side.
        var quarantined = (await db.InventoryTransactions
            .Where(t => t.Status == nameof(InventoryStatus.Quarantined))
            .ToListAsync(ct).ConfigureAwait(false))
            .Where(t => lots.Contains(t.LotRef))
            .GroupBy(t => t.LotRef, StringComparer.Ordinal)
            .Where(g => g.Sum(t => t.Quantity) > 0)
            .Select(g => g.Key)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();
        var reviews = _policyContext is null
            ? (IReadOnlyList<OpenReview>)[]
            : await _policyContext.FindOpenReviewsAsync(invoices, ct).ConfigureAwait(false);
        return new PayableCheckInput(invoices, holds, quarantined, reviews);
    }
}

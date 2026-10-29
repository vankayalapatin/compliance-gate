// Core P2P agent logic. Depends on IErpAdapter, IModelClient, and
// IComplianceEngine only — never on ERP tables or a database.
using Gate.Contracts;

namespace Gate.Agent;

public sealed record ShortfallLine(string ItemId, decimal Required, decimal Covered, decimal Shortfall);

public sealed record VendorScreening(string VendorId, bool Passed, IReadOnlyList<Verdict> Verdicts);

public sealed class P2pAgent
{
    public const decimal PriceTolerance = 0.02m;

    private readonly IErpAdapter _erp;
    private readonly IModelClient _models;
    private readonly IComplianceEngine _compliance;

    public P2pAgent(IErpAdapter erp, IModelClient models, IComplianceEngine compliance)
    {
        _erp = erp;
        _models = models;
        _compliance = compliance;
    }

    public async Task<IReadOnlyList<ShortfallLine>> ComputeShortfallAsync(string soNumber, CancellationToken ct = default)
    {
        var so = await _erp.GetSalesOrderAsync(soNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown sales order {soNumber}.");

        var result = new List<ShortfallLine>();
        foreach (var line in so.Lines)
        {
            var bom = await _erp.GetBomAsync(line.ItemId, ct).ConfigureAwait(false);
            var components = bom.Count > 0 ? bom : [new BomLine(line.ItemId, 1)];
            foreach (var comp in components)
            {
                var required = line.Quantity * comp.QtyPer;
                var stock = await _erp.GetStockAsync(comp.ComponentItemId, ct).ConfigureAwait(false);
                var openPos = await _erp.GetOpenPurchaseOrdersAsync(comp.ComponentItemId, ct).ConfigureAwait(false);
                var onOrder = openPos.SelectMany(p => p.Lines)
                    .Where(l => l.ItemId == comp.ComponentItemId)
                    .Sum(l => l.QtyOrdered - l.QtyReceived);
                var covered = stock + onOrder;
                result.Add(new ShortfallLine(comp.ComponentItemId, required, covered, Math.Max(0, required - covered)));
            }
        }

        return result;
    }

    public async Task<PurchaseRequisition> RaisePrAsync(
        string requester, string approver, string source,
        IReadOnlyList<ShortfallLine> shortfalls, CancellationToken ct = default)
    {
        var lines = shortfalls
            .Where(s => s.Shortfall > 0)
            .Select(s => new PurchaseRequisitionLine(s.ItemId, s.Shortfall, "EA"))
            .ToList();
        if (lines.Count == 0)
        {
            throw new InvalidOperationException("No shortfall: PR refused.");
        }

        var pr = await _erp.CreatePrAsync(requester, source, lines, ct).ConfigureAwait(false);
        await _erp.SubmitForApprovalAsync("PR", pr.PrNumber, approver, ct).ConfigureAwait(false);
        return await _erp.GetPrAsync(pr.PrNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PR {pr.PrNumber} vanished after submit.");
    }

    public async Task<(PurchaseOrder Po, IReadOnlyList<VendorScreening> Screenings)> ScreenAndOrderAsync(
        string prNumber, IReadOnlyList<string> candidateVendorIds, string currency, string source, string caseId, CancellationToken ct = default)
    {
        var pr = await _erp.GetPrAsync(prNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown PR {prNumber}.");
        var screenings = new List<VendorScreening>();
        string? winner = null;
        foreach (var vendorId in candidateVendorIds)
        {
            var vendors = await _erp.GetVendorsAsync(string.Empty, ct).ConfigureAwait(false);
            var vendor = vendors.FirstOrDefault(v => v.VendorId == vendorId)
                ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
            var docs = await _erp.GetVendorDocumentsAsync(vendorId, ct).ConfigureAwait(false);
            var verdicts = new List<Verdict>();
            foreach (var line in pr.Lines)
            {
                var award = new ProposedAward(line.ItemId, line.Quantity, vendor.CountryOfOrigin);
                var history = await _erp.GetVolumeHistoryAsync(line.ItemId, ct).ConfigureAwait(false);
                verdicts.AddRange(await _compliance.EvaluateVendorAsync(vendor, docs, award, history, ct).ConfigureAwait(false));
            }
            var passed = verdicts.All(v => v.Kind == VerdictKind.Pass);
            screenings.Add(new VendorScreening(vendorId, passed, verdicts));
            if (passed && winner is null)
            {
                winner = vendorId;
            }
            else if (!passed)
            {
                var block = verdicts.First(v => v.Kind is VerdictKind.Block);
                await _erp.BlockVendorAsync(vendorId, $"{block.RuleId}: {block.Reason}", ct).ConfigureAwait(false);
                await _erp.AddPoNoteAsync(prNumber, $"Vendor {vendorId} rejected for {caseId}: {block.RuleId}.", ct).ConfigureAwait(false);
            }
        }

        if (winner is null)
        {
            throw new InvalidOperationException("No compliant vendor: PO refused.");
        }

        var po = await _erp.CreatePoAsync(prNumber, winner, currency, source, ct).ConfigureAwait(false);
        return (po, screenings);
    }

    public async Task<VendorInvoice> MatchInvoiceAsync(VendorInvoice invoice, CancellationToken ct = default)
    {
        var problems = new List<string>();
        if (await _erp.GetInvoiceAsync(invoice.InvoiceNumber, ct).ConfigureAwait(false) is not null)
        {
            problems.Add("AP-C1: duplicate invoice number.");
        }

        if (string.IsNullOrWhiteSpace(invoice.VendorId) || string.IsNullOrWhiteSpace(invoice.PoNumber))
        {
            problems.Add("AP-C3: vendor or PO number missing; goods receipt required before pay.");
        }

        var po = invoice.PoNumber is null ? null : await _erp.GetPurchaseOrderAsync(invoice.PoNumber, ct).ConfigureAwait(false);
        var matched = new List<VendorInvoiceLine>();
        var results = new List<LineMatchResult>();
        foreach (var line in invoice.Lines)
        {
            var poLine = po?.Lines.FirstOrDefault(l => l.PoLineRef == line.PoLineRef);
            if (poLine is null)
            {
                matched.Add(line with { Disposition = LineDisposition.Held, HoldReason = "AP-C3: no PO line match." });
                results.Add(new LineMatchResult(line.PoLineRef, false, "No PO line match."));
                continue;
            }

            var priceOk = poLine.UnitPrice == 0 || Math.Abs(line.UnitPrice - poLine.UnitPrice) / poLine.UnitPrice <= PriceTolerance;
            var qtyOk = line.Quantity <= poLine.QtyReceived;
            if (priceOk && qtyOk)
            {
                matched.Add(line with { Disposition = LineDisposition.Accepted, HoldReason = null });
                results.Add(new LineMatchResult(line.PoLineRef, true, "Matched."));
            }
            else
            {
                var why = !priceOk ? $"AP-C2: price {line.UnitPrice} vs PO {poLine.UnitPrice}." : $"Qty {line.Quantity} exceeds received {poLine.QtyReceived}.";
                matched.Add(line with { Disposition = LineDisposition.Held, HoldReason = why });
                results.Add(new LineMatchResult(line.PoLineRef, false, why));
            }
        }

        foreach (var p in problems)
        {
            results.Add(new LineMatchResult(null, false, p));
        }

        var heldAll = problems.Count > 0
            ? matched.Select(l => l with { Disposition = LineDisposition.Held, HoldReason = string.Join(" ", problems) }).ToList()
            : matched;
        var stored = invoice with { Lines = heldAll, MatchResults = results };
        await _erp.CreateInvoiceAsync(stored, ct).ConfigureAwait(false);
        await _erp.RecordMatchResultAsync(stored.InvoiceNumber, results, ct).ConfigureAwait(false);
        return stored;
    }

    public async Task<(Payment? Payment, IReadOnlyList<string> SkippedHeld)> RequestPaymentAsync(
        IReadOnlyList<string> invoiceNumbers, string currency, string source, CancellationToken ct = default)
    {
        var lines = new List<PaymentLine>();
        var skipped = new List<string>();
        foreach (var number in invoiceNumbers)
        {
            var inv = await _erp.GetInvoiceAsync(number, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Unknown invoice {number}.");
            var accepted = inv.Lines.Where(l => l.Disposition == LineDisposition.Accepted).Sum(l => l.Quantity * l.UnitPrice);
            if (accepted <= 0)
            {
                skipped.Add(number);
                continue;
            }

            lines.Add(new PaymentLine(number, accepted));
        }

        if (lines.Count == 0)
        {
            return (null, skipped);
        }

        var payment = await _erp.CreatePaymentAsync(currency, source, lines, ct).ConfigureAwait(false);
        return (payment, skipped);
    }

    public Task<string> ExplainAsync(string question, string evidence, CancellationToken ct = default) =>
        _models.CompleteAsync(new ModelRequest(question, $"Decide using only this evidence: {evidence}"), ct)
            .ContinueWith(t => t.Result.Text, ct);
}

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

        var pr = await _erp.CreatePrAsync(requester, source, requester, lines, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        if (pr.Status is PrStatus.Draft or PrStatus.Rejected)
        {
            await _erp.SubmitForApprovalAsync("PR", pr.PrNumber, approver, requester, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        }
        return await _erp.GetPrAsync(pr.PrNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PR {pr.PrNumber} vanished after submit.");
    }

    /// <summary>Routes a draft PR to the lowest-limit approver covering its
    /// ERP-estimated amount, following an active outbound delegation.
    /// Returns null when the PR needs no human (auto-approved under threshold).</summary>
    public async Task<string?> RoutePrAsync(string prNumber, CancellationToken ct = default)
    {
        var pr = await _erp.GetPrAsync(prNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown PR {prNumber}.");
        if (pr.Status != PrStatus.Draft)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var delegations = await _erp.GetDelegationsAsync(ct).ConfigureAwait(false);
        var candidates = (await _erp.GetUsersAsync(ct).ConfigureAwait(false))
            .Where(u => !string.Equals(u.UserId, pr.Requester, StringComparison.OrdinalIgnoreCase))
            .Where(u => !string.Equals(u.Role, "requester", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(u.Role, "ap-clerk", StringComparison.OrdinalIgnoreCase))
            .Where(u => u.SigningLimit >= pr.EstimatedAmount)
            .OrderBy(u => u.SigningLimit)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException($"No approver covers PR {prNumber} estimated at {pr.EstimatedAmount}.");
        }

        var chosen = candidates[0];
        var outbound = delegations.FirstOrDefault(d =>
            string.Equals(d.FromUser, chosen.UserId, StringComparison.OrdinalIgnoreCase) && today >= d.Start && today <= d.End);
        return outbound?.ToUser ?? chosen.UserId;
    }

    public async Task<(PurchaseOrder Po, IReadOnlyList<VendorScreening> Screenings)> ScreenAndOrderAsync(
        string prNumber, IReadOnlyList<string> candidateVendorIds, string source, string caseId, CancellationToken ct = default)
    {
        var pr = await _erp.GetPrAsync(prNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Unknown PR {prNumber}.");
        var screenings = new List<VendorScreening>();
        string? winner = null;
        foreach (var vendorId in candidateVendorIds)
        {
            var vendors = await _erp.GetVendorsAsync(ct).ConfigureAwait(false);
            var vendor = vendors.FirstOrDefault(v => v.VendorId == vendorId)
                ?? throw new InvalidOperationException($"Unknown vendor {vendorId}.");
            var docs = await _erp.GetVendorDocumentsAsync(vendorId, ct).ConfigureAwait(false);
            var verdicts = new List<Verdict>();
            foreach (var line in pr.Lines)
            {
                var item = await _erp.GetItemAsync(line.ItemId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Unknown item {line.ItemId}.");
                var award = new ProposedAward(line.ItemId, line.Quantity, vendor.CountryOfOrigin);
                var history = await _erp.GetVolumeHistoryAsync(line.ItemId, ct).ConfigureAwait(false);
                verdicts.AddRange(await _compliance.EvaluateVendorAsync(vendor, item, docs, award, history, ct).ConfigureAwait(false));
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
                await _erp.BlockVendorAsync(vendorId, $"{block.RuleId}: {block.Reason}", RecordSource.P2pAgent, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
                await _erp.AddPoNoteAsync(prNumber, $"Vendor {vendorId} rejected for {caseId}: {block.RuleId}.", RecordSource.P2pAgent, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
            }
        }

        if (winner is null)
        {
            throw new InvalidOperationException("No compliant vendor: PO refused.");
        }

        var po = await _erp.CreatePoAsync(prNumber, winner, source, pr.Requester, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        return (po, screenings);
    }

    public async Task<VendorInvoice> MatchInvoiceAsync(
        string vendorId, string supplierInvoiceNumber, string? poNumber,
        string source, string createdBy, IReadOnlyList<InvoiceLineInput> lines, CancellationToken ct = default)
    {
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(vendorId) && !string.IsNullOrWhiteSpace(supplierInvoiceNumber))
        {
            if (await _erp.GetInvoiceAsync(vendorId, supplierInvoiceNumber, ct).ConfigureAwait(false) is not null)
                problems.Add("AP-C1: duplicate invoice number.");
        }
        if (string.IsNullOrWhiteSpace(vendorId) || string.IsNullOrWhiteSpace(poNumber))
            problems.Add("AP-C3: vendor or PO number missing; goods receipt required before pay.");

        PurchaseOrder? po = null;
        if (problems.Count == 0 && poNumber is not null)
            po = await _erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false);

        if (problems.Count > 0 || po is null)
        {
            var held = lines.Select(l => new VendorInvoiceLine(l.PoLineRef, l.Quantity, l.UnitPrice,
                LineDisposition.Held, string.Join(" ", problems.Count > 0 ? problems : ["AP-C3: no PO match."]))).ToList();
            var results = held.Select(l => new LineMatchResult(l.PoLineRef, false, l.HoldReason ?? "Held.")).ToList();
            foreach (var p in problems) results.Add(new LineMatchResult(null, false, p));
            return new VendorInvoice(supplierInvoiceNumber, vendorId, poNumber, InvoiceStatus.Received, null,
                po?.Currency ?? string.Empty, source, createdBy, string.Empty, held, results);
        }

        await _erp.CreateInvoiceAsync(supplierInvoiceNumber, vendorId, poNumber, source, createdBy, lines, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        var matched = new List<LineMatchResult>();
        foreach (var line in lines)
        {
            var poLine = po.Lines.FirstOrDefault(l => l.PoLineRef == line.PoLineRef);
            if (poLine is null)
            {
                matched.Add(new LineMatchResult(line.PoLineRef, false, "No PO line match."));
                continue;
            }
            var priceOk = poLine.UnitPrice == 0 || Math.Abs(line.UnitPrice - poLine.UnitPrice) / poLine.UnitPrice <= PriceTolerance;
            var qtyOk = line.Quantity <= poLine.QtyReceived;
            if (priceOk && qtyOk) matched.Add(new LineMatchResult(line.PoLineRef, true, "Matched."));
            else matched.Add(new LineMatchResult(line.PoLineRef, false,
                !priceOk ? $"AP-C2: price {line.UnitPrice} vs PO {poLine.UnitPrice}." : $"Qty {line.Quantity} exceeds received {poLine.QtyReceived}."));
        }
        await _erp.RecordMatchResultAsync(vendorId, supplierInvoiceNumber, matched, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        return await _erp.GetInvoiceAsync(vendorId, supplierInvoiceNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Invoice {supplierInvoiceNumber} vanished after match.");
    }

    public async Task<(Payment? Payment, IReadOnlyList<string> SkippedHeld)> RequestPaymentAsync(
        IReadOnlyList<InvoiceKey> invoiceKeys, string source, CancellationToken ct = default)
    {
        var payable = new List<InvoiceKey>();
        var skipped = new List<string>();
        foreach (var key in invoiceKeys)
        {
            var inv = await _erp.GetInvoiceAsync(key.VendorId, key.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            if (inv is null) { skipped.Add(key.SupplierInvoiceNumber); continue; }
            var accepted = inv.Lines.Where(l => l.Disposition == LineDisposition.Accepted).Sum(l => l.Quantity * l.UnitPrice);
            if (accepted <= 0) { skipped.Add(key.SupplierInvoiceNumber); continue; }
            payable.Add(key);
        }

        if (payable.Count == 0)
        {
            return (null, skipped);
        }

        var draft = await _erp.CreatePaymentDraftAsync(payable, source, RecordSource.P2pAgent, Guid.NewGuid().ToString("N"), ct).ConfigureAwait(false);
        return (draft, skipped);
    }

    public Task<string> ExplainAsync(string question, string evidence, CancellationToken ct = default) =>
        _models.CompleteAsync(new ModelRequest(question, $"Decide using only this evidence: {evidence}"), ct)
            .ContinueWith(t => t.Result.Text, ct);
}

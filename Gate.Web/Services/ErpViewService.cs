// Adapter-backed view models for the ERP area: module list pages plus
// record pages with header, lines, status, related documents, and the
// candidate actions for the current state. Full-list reads come straight
// from the IErpAdapter interface; when the ERP layer has not implemented
// one yet (NotSupportedException) the list renders an honest note instead
// of failing. Nothing is silently omitted.
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed class ErpViewService
{
    private readonly IErpAdapter _erp;
    private readonly CaseViewService _views;
    private readonly ILogger<ErpViewService> _log;

    public ErpViewService(IErpAdapter erp, CaseViewService views, ILogger<ErpViewService> log)
    {
        _erp = erp;
        _views = views;
        _log = log;
    }

    public (IReadOnlySet<string> Opening, IReadOnlyList<string> Notes) GetOpeningNumbers() =>
        _views.GetOpeningNumbers();

    private async Task<ErpListVm<T>> ListAsync<T>(
        string label, Func<CancellationToken, Task<IReadOnlyList<T>>> read,
        Func<IReadOnlyList<T>, IReadOnlyList<T>>? order, CancellationToken ct)
    {
        try
        {
            var rows = await read(ct).ConfigureAwait(false);
            return new ErpListVm<T>(order is null ? rows : order(rows), []);
        }
        catch (NotSupportedException ex)
        {
            _log.LogWarning(ex, "{Label} list is not implemented by the ERP layer yet.", label);
            return new ErpListVm<T>([],
                [$"{label} list is not implemented by the ERP layer yet."]);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Label} list failed.", label);
            return new ErpListVm<T>([], [$"{label} list failed: {ex.Message}."]);
        }
    }

    private static IReadOnlyList<T> By<T>(IReadOnlyList<T> rows, Func<T, string> key) =>
        rows.OrderBy(key, StringComparer.Ordinal).ToList();

    public Task<ErpListVm<SalesOrder>> ListSalesOrdersAsync(CancellationToken ct = default) =>
        ListAsync("Sales order", ct => _erp.GetSalesOrdersAsync(ct),
            rows => By(rows, r => r.SoNumber), ct);

    public Task<ErpListVm<PurchaseRequisition>> ListPurchaseRequisitionsAsync(CancellationToken ct = default) =>
        ListAsync("Purchase requisition", ct => _erp.GetPurchaseRequisitionsAsync(ct),
            rows => By(rows, r => r.PrNumber), ct);

    public Task<ErpListVm<PurchaseOrder>> ListPurchaseOrdersAsync(CancellationToken ct = default) =>
        ListAsync("Purchase order", ct => _erp.GetPurchaseOrdersAsync(ct),
            rows => By(rows, r => r.PoNumber), ct);

    public Task<ErpListVm<AsnDto>> ListAsnsAsync(CancellationToken ct = default) =>
        ListAsync("ASN", ct => _erp.GetAsnsAsync(ct),
            rows => By(rows, r => r.AsnNumber), ct);

    public Task<ErpListVm<ShipmentReceipt>> ListReceiptsAsync(CancellationToken ct = default) =>
        ListAsync("Receipt", ct => _erp.GetShipmentReceiptsAsync(ct),
            rows => By(rows, r => r.ReceiptNumber), ct);

    public Task<ErpListVm<VendorInvoice>> ListInvoicesAsync(CancellationToken ct = default) =>
        ListAsync("Invoice", ct => _erp.GetVendorInvoicesAsync(ct),
            rows => By(rows, r => ErpDocTypes.InvoiceDocNumber(r.VendorId, r.SupplierInvoiceNumber)), ct);

    public Task<ErpListVm<Payment>> ListPaymentsAsync(CancellationToken ct = default) =>
        ListAsync("Payment", ct => _erp.GetPaymentsAsync(ct),
            rows => By(rows, r => r.PaymentNumber), ct);

    public Task<ErpListVm<HoldDto>> ListHoldsAsync(CancellationToken ct = default) =>
        ListAsync("Hold", ct => _erp.GetOpenHoldsAsync(null, ct),
            rows => By(rows, r => r.HoldId), ct);

    public Task<ErpListVm<VendorDto>> ListVendorsAsync(CancellationToken ct = default) =>
        ListAsync("Vendor", ct => _erp.GetVendorsAsync(ct),
            rows => By(rows, r => r.VendorId), ct);

    public Task<ErpListVm<ItemDto>> ListItemsAsync(CancellationToken ct = default) =>
        ListAsync("Item", ct => _erp.GetItemsAsync(ct),
            rows => By(rows, r => r.ItemId), ct);

    public Task<ErpListVm<PurchaseOrder>> ListPurchaseOrdersByVendorAsync(
        string vendorId, CancellationToken ct = default) =>
        ListAsync("Purchase order", ct => _erp.GetPurchaseOrdersByVendorAsync(vendorId, ct),
            rows => By(rows, r => r.PoNumber), ct);

    public Task<ErpListVm<VendorInvoice>> ListOpenInvoicesAsync(CancellationToken ct = default) =>
        ListAsync("Open invoice", ct => _erp.GetOpenInvoicesAsync(ct),
            rows => By(rows, r => ErpDocTypes.InvoiceDocNumber(r.VendorId, r.SupplierInvoiceNumber)), ct);

    public Task<ErpListVm<AsnDto>> ListUnreceivedAsnsAsync(CancellationToken ct = default) =>
        ListAsync("Unreceived ASN", ct => _erp.GetUnreceivedAsnsAsync(ct),
            rows => By(rows, r => r.AsnNumber), ct);

    public async Task<ErpListVm<PurchaseRequisition>> ListApprovedPrsAsync(CancellationToken ct = default)
    {
        var all = await ListPurchaseRequisitionsAsync(ct).ConfigureAwait(false);
        return new ErpListVm<PurchaseRequisition>(
            all.Rows.Where(p => p.Status == PrStatus.Approved).ToList(), all.Notes);
    }

    public async Task<ErpListVm<ErpStockRow>> ListStockAsync(CancellationToken ct = default)
    {
        IReadOnlyList<InventoryTransaction> txns;
        try
        {
            txns = await _erp.GetAllInventoryTransactionsAsync(ct).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            _log.LogWarning(ex, "Stock list is not implemented by the ERP layer yet.");
            return new ErpListVm<ErpStockRow>([],
                ["Stock list is not implemented by the ERP layer yet."]);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stock list failed.");
            return new ErpListVm<ErpStockRow>([], [$"Stock list failed: {ex.Message}."]);
        }

        var notes = new List<string>();
        Dictionary<string, ItemDto> items = new(StringComparer.Ordinal);
        try
        {
            foreach (var item in await _erp.GetItemsAsync(ct).ConfigureAwait(false))
            {
                items[item.ItemId] = item;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Item lookup for the stock list failed; descriptions stay blank.");
            notes.Add($"Item descriptions unavailable: {ex.Message}.");
        }

        var rows = new List<ErpStockRow>();
        foreach (var group in txns.GroupBy(t => t.ItemId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            decimal stock = 0;
            try
            {
                stock = await _erp.GetStockAsync(group.Key, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Stock lookup failed for {Item}.", group.Key);
                notes.Add($"Stock for {group.Key} failed: {ex.Message}.");
            }

            items.TryGetValue(group.Key, out var item);
            rows.Add(new ErpStockRow(
                group.Key, item?.Description, item?.Unit, stock,
                group.OrderBy(t => t.LotRef, StringComparer.Ordinal).ToList()));
        }

        return new ErpListVm<ErpStockRow>(rows, notes);
    }

    // New-form passthroughs for preset prefill (single reads, null when missing).
    public async Task<AsnDto?> GetAsnAsync(string asnNumber, CancellationToken ct = default)
    {
        try
        {
            return await _erp.GetAsnAsync(asnNumber, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ASN {Asn} lookup failed.", asnNumber);
            return null;
        }
    }

    public async Task<PurchaseRequisition?> GetPrAsync(string prNumber, CancellationToken ct = default)
    {
        try
        {
            return await _erp.GetPrAsync(prNumber, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PR {Pr} lookup failed.", prNumber);
            return null;
        }
    }

    public async Task<PurchaseOrder?> GetPoAsync(string poNumber, CancellationToken ct = default)
    {
        try
        {
            return await _erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PO {Po} lookup failed.", poNumber);
            return null;
        }
    }

    // Record pages: header facts, line tables, related documents, candidate
    // actions, media. A missing document returns a shell carrying the note
    // so the page renders honestly; only an unknown kind returns null.
    public async Task<ErpRecordVm?> GetRecordAsync(
        string kind, string key, string? key2 = null, CancellationToken ct = default)
    {
        var (opening, openingNotes) = _views.GetOpeningNumbers();
        var notes = new List<string>(openingNotes);
        try
        {
            return kind switch
            {
                "so" => await SoRecordAsync(key, opening, notes, ct).ConfigureAwait(false),
                "pr" => await PrRecordAsync(key, opening, notes, ct).ConfigureAwait(false),
                "po" => await PoRecordAsync(key, opening, notes, ct).ConfigureAwait(false),
                "asn" => await AsnRecordAsync(key, opening, notes, ct).ConfigureAwait(false),
                "receipt" => await ReceiptRecordAsync(key, opening, notes, ct).ConfigureAwait(false),
                "stock" => await StockRecordAsync(key, opening, notes, ct).ConfigureAwait(false),
                "invoice" => key2 is null
                    ? Shell(kind, key, $"Invoice {key} needs its vendor: open it as vendor/number.", opening, notes)
                    : await InvoiceRecordAsync(key, key2, opening, notes, ct).ConfigureAwait(false),
                "payment" => await PaymentRecordAsync(key, opening, notes, ct).ConfigureAwait(false),
                "hold" => await HoldRecordAsync(key, opening, notes, ct).ConfigureAwait(false),
                "vendor" => await VendorRecordAsync(key, opening, notes, ct).ConfigureAwait(false),
                "item" => await ItemRecordAsync(key, opening, notes, ct).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Record {Kind} {Key} failed.", kind, key);
            notes.Add($"Record {kind} {key} failed: {ex.Message}.");
            return Shell(kind, key, null, opening, notes);
        }
    }

    private static ErpRecordVm Shell(
        string kind, string key, string? note,
        IReadOnlySet<string> opening, IReadOnlyList<string> notes)
    {
        var all = note is null ? notes : notes.Append(note).ToList();
        return new ErpRecordVm(kind, key, $"{ErpModules.TitleFor(kind)} {key}",
            [], null, key, [], [], [], [], [], [], all, opening);
    }

    private static ErpActionDef SubmitAction() => new(ErpActions.Submit, "Submit for approval",
        "Routes the document to its approver. The ERP enforces state and routing.",
        [new(ErpActions.InRoutedTo, "Route to (user id)", true)]);

    private static ErpActionDef ApproveAction() => new(ErpActions.Approve, "Approve",
        "Records an approval as the acting user. SoD, limits, and delegation are enforced by the ERP.",
        [new(ErpActions.InComment, "Comment", false)]);

    private static ErpActionDef RejectAction() => new(ErpActions.Reject, "Reject",
        "Records a rejection as the acting user. The ERP enforces who may act.",
        [new(ErpActions.InComment, "Comment", false)]);

    private async Task<ErpLineTable?> ApprovalsTableAsync(
        string docType, string docNumber, List<string> notes, CancellationToken ct)
    {
        IReadOnlyList<ApprovalDto> rows;
        try
        {
            rows = await _erp.GetApprovalsAsync(docType, docNumber, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Approval list failed for {DocType} {DocNumber}.", docType, docNumber);
            notes.Add($"Approval history for {docType} {docNumber} failed: {ex.Message}.");
            return null;
        }

        if (rows.Count == 0)
        {
            return null;
        }

        return new ErpLineTable("Approvals",
            [new("Routed to"), new("Acted by"), new("Outcome"),
             new("Amount / limit", true), new("Comment")],
            rows.Select(a => (IReadOnlyList<ErpCell>)
            [
                new(a.RoutedTo), new(a.ActedBy ?? "—"),
                new(a.Approved ? "approved" : "rejected"),
                new($"{ErpFormat.Amount(a.DocumentAmount)} / {ErpFormat.Amount(a.LimitChecked)}", true),
                new(a.Comment ?? "—"),
            ]).ToList());
    }

    private async Task AddBlockingHoldsAsync(
        string? documentNumber, List<ErpRelatedDoc> related, List<string> notes, CancellationToken ct)
    {
        if (documentNumber is null)
        {
            return;
        }

        IReadOnlyList<HoldDto> holds;
        try
        {
            holds = await _erp.GetOpenHoldsAsync(documentNumber, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Open-hold lookup failed for {Doc}.", documentNumber);
            notes.Add($"Open holds for {documentNumber} failed: {ex.Message}.");
            return;
        }

        related.AddRange(holds.Select(h => new ErpRelatedDoc(
            $"Hold {h.HoldId} ({h.ReasonCode})", "hold", h.HoldId, null,
            h.RuleId ?? h.ReasonCode)));
    }

    private async Task<ErpRecordVm> SoRecordAsync(
        string key, IReadOnlySet<string> opening, List<string> notes, CancellationToken ct)
    {
        var so = await _erp.GetSalesOrderAsync(key, ct).ConfigureAwait(false);
        if (so is null)
        {
            return Shell("so", key, $"Sales order {key} not found in ERP.", opening, notes);
        }

        var related = so.Lines.Select(l => l.ItemId).Distinct(StringComparer.Ordinal)
            .Select(item => new ErpRelatedDoc($"Item {item}", "item", item, null, "master"))
            .ToList<ErpRelatedDoc>();
        await AddBlockingHoldsAsync(so.SoNumber, related, notes, ct).ConfigureAwait(false);
        var statuses = Enum.GetNames<SalesOrderStatus>().ToList();
        return new ErpRecordVm("so", key, $"Sales order {so.SoNumber}",
            [$"Status {so.Status}"], null, so.SoNumber,
            [
                new("Sales order", so.SoNumber),
                new("Status", so.Status.ToString()),
                new("Currency", so.Currency),
                new("Lines", ErpFormat.Count(so.Lines.Count)),
                new("Total quantity", ErpFormat.Qty(so.Lines.Sum(l => l.Quantity))),
            ],
            [new ErpLineTable("Lines",
                [new("Item"), new("Quantity", true)],
                so.Lines.Select(l => (IReadOnlyList<ErpCell>)
                [
                    new(l.ItemId, Href: ErpRoutes.RecordHref("item", l.ItemId)),
                    new(ErpFormat.Qty(l.Quantity), true),
                ]).ToList())],
            related,
            [new(ErpActions.UpdateSoStatus, "Update status",
                "Sets the sales-order status. The ERP enforces legal transitions.",
                [new(ErpActions.InStatus, "New status", true, statuses)])],
            [], [], notes, opening);
    }

    private async Task<ErpRecordVm> PrRecordAsync(
        string key, IReadOnlySet<string> opening, List<string> notes, CancellationToken ct)
    {
        var pr = await _erp.GetPrAsync(key, ct).ConfigureAwait(false);
        if (pr is null)
        {
            return Shell("pr", key, $"Purchase requisition {key} not found in ERP.", opening, notes);
        }

        var related = new List<ErpRelatedDoc>();
        var pos = await ListPurchaseOrdersAsync(ct).ConfigureAwait(false);
        notes.AddRange(pos.Notes);
        related.AddRange(pos.Rows
            .Where(p => string.Equals(p.PrNumber, pr.PrNumber, StringComparison.Ordinal))
            .Select(p => new ErpRelatedDoc($"Purchase order {p.PoNumber}", "po", p.PoNumber, null,
                $"{p.ApprovalStatus} / {p.DocStatus}")));
        await AddBlockingHoldsAsync(pr.PrNumber, related, notes, ct).ConfigureAwait(false);

        var actions = new List<ErpActionDef>();
        if (pr.Status is PrStatus.Draft or PrStatus.Rejected)
        {
            actions.Add(SubmitAction());
        }
        else if (pr.Status == PrStatus.Submitted)
        {
            actions.Add(ApproveAction());
            actions.Add(RejectAction());
        }
        else if (pr.Status == PrStatus.Approved)
        {
            related.Add(new ErpRelatedDoc("New purchase order from this PR", "po", pr.PrNumber, null,
                "create", HrefOverride: ErpRoutes.NewHref("po", pr.PrNumber)));
        }

        var tables = new List<ErpLineTable>
        {
            new("Lines",
                [new("Item"), new("Quantity", true), new("Unit")],
                pr.Lines.Select(l => (IReadOnlyList<ErpCell>)
                [
                    new(l.ItemId, Href: ErpRoutes.RecordHref("item", l.ItemId)),
                    new(ErpFormat.Qty(l.Quantity), true),
                    new(l.Unit),
                ]).ToList()),
        };
        var approvals = await ApprovalsTableAsync("PR", pr.PrNumber, notes, ct).ConfigureAwait(false);
        if (approvals is not null)
        {
            tables.Add(approvals);
        }

        return new ErpRecordVm("pr", key, $"Purchase requisition {pr.PrNumber}",
            [$"Status {pr.Status}"], pr.Source, pr.PrNumber,
            [
                new("Purchase requisition", pr.PrNumber),
                new("Status", pr.Status.ToString()),
                new("Requester", pr.Requester),
                new("Estimated amount", ErpFormat.Money(pr.EstimatedAmount, pr.Currency)),
                new("Created by", pr.CreatedBy),
            ],
            tables, related, actions, [], [], notes, opening);
    }

    private async Task<ErpRecordVm> PoRecordAsync(
        string key, IReadOnlySet<string> opening, List<string> notes, CancellationToken ct)
    {
        var po = await _erp.GetPurchaseOrderAsync(key, ct).ConfigureAwait(false);
        if (po is null)
        {
            return Shell("po", key, $"Purchase order {key} not found in ERP.", opening, notes);
        }

        var related = new List<ErpRelatedDoc>
        {
            new($"Purchase requisition {po.PrNumber}", "pr", po.PrNumber, null, "source"),
            new($"Vendor {po.VendorId}", "vendor", po.VendorId, null, "supplier"),
        };
        var asns = await ListAsnsAsync(ct).ConfigureAwait(false);
        notes.AddRange(asns.Notes);
        AsnDto? asn = null;
        foreach (var row in asns.Rows.Where(a =>
            string.Equals(a.PoNumber, po.PoNumber, StringComparison.Ordinal)))
        {
            asn ??= row;
            related.Add(new ErpRelatedDoc($"ASN {row.AsnNumber}", "asn", row.AsnNumber, null,
                $"{ErpFormat.Count(row.Cartons.Count)} carton(s)"));
        }

        var receipts = await ListReceiptsAsync(ct).ConfigureAwait(false);
        notes.AddRange(receipts.Notes);
        related.AddRange(receipts.Rows
            .Where(r => string.Equals(r.PoNumber, po.PoNumber, StringComparison.Ordinal))
            .Select(r => new ErpRelatedDoc($"Receipt {r.ReceiptNumber}", "receipt", r.ReceiptNumber, null,
                r.Status.ToString())));
        await AddBlockingHoldsAsync(po.PoNumber, related, notes, ct).ConfigureAwait(false);

        var header = new List<ErpFact>
        {
            new("Purchase order", po.PoNumber),
            new("Purchase requisition", po.PrNumber, ErpRoutes.RecordHref("pr", po.PrNumber)),
            new("Vendor", po.VendorId, ErpRoutes.RecordHref("vendor", po.VendorId)),
            new("Payment terms", po.PaymentTermsId),
            new("Approval", po.ApprovalStatus.ToString()),
            new("Document status", po.DocStatus.ToString()),
            new("Total", ErpFormat.Money(po.Lines.Sum(l => l.LineAmount), po.Currency)),
            new("Created by", po.CreatedBy),
        };
        try
        {
            var confirm = await _erp.GetVendorConfirmationAsync(po.PoNumber, ct).ConfigureAwait(false);
            header.Add(new("Vendor confirmation",
                confirm is null ? "none yet" : $"confirmed {confirm.ConfirmDate}"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vendor confirmation lookup failed for {Po}.", po.PoNumber);
            notes.Add($"Vendor confirmation for {po.PoNumber} failed: {ex.Message}.");
        }

        var actions = new List<ErpActionDef>();
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            actions.Add(SubmitAction());
        }
        else if (po.ApprovalStatus == PoApprovalStatus.InReview)
        {
            actions.Add(ApproveAction());
            actions.Add(RejectAction());
        }
        else if (po.ApprovalStatus is PoApprovalStatus.Approved or PoApprovalStatus.Confirmed)
        {
            actions.Add(new(ErpActions.ClosePo, "Close PO",
                "Closes the order. The ERP enforces what may close.", []));
            actions.Add(new(ErpActions.AddPoNote, "Add note",
                "Appends a free-text note to the order.",
                [new(ErpActions.InNote, "Note", true)]));
        }

        var tables = new List<ErpLineTable>
        {
            new("Lines",
                [new("Line"), new("Item"), new("Ordered", true), new("Received", true),
                 new("Invoiced", true), new("Quarantined", true), new("Rejected", true),
                 new("Unit price", true), new("Amount", true)],
                po.Lines.Select(l => (IReadOnlyList<ErpCell>)
                [
                    new(l.PoLineRef), new(l.ItemId, Href: ErpRoutes.RecordHref("item", l.ItemId)),
                    new(ErpFormat.Qty(l.QtyOrdered), true), new(ErpFormat.Qty(l.QtyReceived), true),
                    new(ErpFormat.Qty(l.QtyInvoiced), true), new(ErpFormat.Qty(l.QtyQuarantined), true),
                    new(ErpFormat.Qty(l.QtyRejected), true),
                    new(ErpFormat.Amount(l.UnitPrice), true),
                    new(ErpFormat.Amount(l.LineAmount), true),
                ]).ToList()),
        };
        var approvals = await ApprovalsTableAsync("PO", po.PoNumber, notes, ct).ConfigureAwait(false);
        if (approvals is not null)
        {
            tables.Add(approvals);
        }

        if (asn is not null)
        {
            related.Add(new ErpRelatedDoc("Post receipt from this ASN", "receipt", asn.AsnNumber, null,
                "create", HrefOverride: ErpRoutes.NewHref("receipt", asn.AsnNumber)));
        }

        return new ErpRecordVm("po", key, $"Purchase order {po.PoNumber}",
            [$"Approval {po.ApprovalStatus}", $"Document {po.DocStatus}"],
            po.Source, po.PoNumber, header, tables, related, actions, [], [], notes, opening);
    }

    private async Task<ErpRecordVm> AsnRecordAsync(
        string key, IReadOnlySet<string> opening, List<string> notes, CancellationToken ct)
    {
        var asn = await _erp.GetAsnAsync(key, ct).ConfigureAwait(false);
        if (asn is null)
        {
            return Shell("asn", key, $"ASN {key} not found in ERP.", opening, notes);
        }

        var related = new List<ErpRelatedDoc>
        {
            new($"Purchase order {asn.PoNumber}", "po", asn.PoNumber, null, "source"),
            new("Post receipt from this ASN", "receipt", asn.AsnNumber, null,
                "create", HrefOverride: ErpRoutes.NewHref("receipt", asn.AsnNumber)),
        };
        var receipts = await ListReceiptsAsync(ct).ConfigureAwait(false);
        notes.AddRange(receipts.Notes);
        related.AddRange(receipts.Rows
            .Where(r => string.Equals(r.PoNumber, asn.PoNumber, StringComparison.Ordinal))
            .Select(r => new ErpRelatedDoc($"Receipt {r.ReceiptNumber}", "receipt", r.ReceiptNumber, null,
                r.Status.ToString())));

        return new ErpRecordVm("asn", key, $"ASN {asn.AsnNumber}",
            [$"{ErpFormat.Count(asn.Cartons.Count)} carton(s)"], asn.Source, asn.AsnNumber,
            [
                new("ASN", asn.AsnNumber),
                new("Purchase order", asn.PoNumber, ErpRoutes.RecordHref("po", asn.PoNumber)),
                new("Created by", asn.CreatedBy),
            ],
            [new ErpLineTable("Cartons",
                [new("Carton"), new("Item"), new("Units / carton", true), new("Lot")],
                asn.Cartons.Select(c => (IReadOnlyList<ErpCell>)
                [
                    new(c.CartonId), new(c.ItemId, Href: ErpRoutes.RecordHref("item", c.ItemId)),
                    new(ErpFormat.Count(c.UnitsPerCarton), true), new(c.LotRef),
                ]).ToList())],
            related, [], [], [], notes, opening);
    }

    private async Task<ErpRecordVm> ReceiptRecordAsync(
        string key, IReadOnlySet<string> opening, List<string> notes, CancellationToken ct)
    {
        var receipt = await _erp.GetReceiptAsync(key, ct).ConfigureAwait(false);
        if (receipt is null)
        {
            return Shell("receipt", key, $"Receipt {key} not found in ERP.", opening, notes);
        }

        var related = new List<ErpRelatedDoc>
        {
            new($"Purchase order {receipt.PoNumber}", "po", receipt.PoNumber, null, "source"),
        };
        var asns = await ListAsnsAsync(ct).ConfigureAwait(false);
        notes.AddRange(asns.Notes);
        var asn = asns.Rows.FirstOrDefault(a =>
            string.Equals(a.PoNumber, receipt.PoNumber, StringComparison.Ordinal));
        if (asn is not null)
        {
            related.Add(new ErpRelatedDoc($"ASN {asn.AsnNumber}", "asn", asn.AsnNumber, null,
                $"{ErpFormat.Count(asn.Cartons.Count)} carton(s)"));
        }

        var invoices = await ListInvoicesAsync(ct).ConfigureAwait(false);
        notes.AddRange(invoices.Notes);
        related.AddRange(invoices.Rows
            .Where(i => string.Equals(i.PoNumber, receipt.PoNumber, StringComparison.Ordinal))
            .Select(i => new ErpRelatedDoc(
                $"Invoice {i.SupplierInvoiceNumber}", "invoice", i.VendorId, i.SupplierInvoiceNumber,
                i.Status.ToString())));
        await AddBlockingHoldsAsync(receipt.ReceiptNumber, related, notes, ct).ConfigureAwait(false);

        var media = _views.GetReceiptMedia(receipt, asn);
        return new ErpRecordVm("receipt", key, $"Receipt {receipt.ReceiptNumber}",
            [$"Status {receipt.Status}"], receipt.Source, receipt.ReceiptNumber,
            [
                new("Receipt", receipt.ReceiptNumber),
                new("Purchase order", receipt.PoNumber, ErpRoutes.RecordHref("po", receipt.PoNumber)),
                new("Supplier packing slip", receipt.SupplierPackingSlipNumber),
                new("Status", receipt.Status.ToString()),
                new("Created by", receipt.CreatedBy),
            ],
            [new ErpLineTable("Lines",
                [new("PO line"), new("Received", true), new("Quarantined", true),
                 new("Rejected", true), new("Lot"), new("Condition")],
                receipt.Lines.Select(l => (IReadOnlyList<ErpCell>)
                [
                    new(l.PoLineRef), new(ErpFormat.Qty(l.QtyReceived), true),
                    new(ErpFormat.Qty(l.QtyQuarantined), true), new(ErpFormat.Qty(l.QtyRejected), true),
                    new(l.LotRef), new(l.ConditionNote ?? "—"),
                ]).ToList())],
            related, [], media.ImageFileNames, media.CachedReads, notes, opening);
    }

    private async Task<ErpRecordVm> StockRecordAsync(
        string key, IReadOnlySet<string> opening, List<string> notes, CancellationToken ct)
    {
        ItemDto? item = null;
        try
        {
            item = await _erp.GetItemAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Item {Item} lookup failed.", key);
            notes.Add($"Item {key} lookup failed: {ex.Message}.");
        }

        IReadOnlyList<InventoryTransaction> txns = [];
        try
        {
            txns = await _erp.GetInventoryTransactionsAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Lot lookup failed for {Item}.", key);
            notes.Add($"Lots for {key} failed: {ex.Message}.");
        }

        decimal stock = 0;
        try
        {
            stock = await _erp.GetStockAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stock lookup failed for {Item}.", key);
            notes.Add($"Stock for {key} failed: {ex.Message}.");
        }

        if (item is null && txns.Count == 0)
        {
            return Shell("stock", key, $"No stock or lots for item {key} in ERP.", opening, notes);
        }

        var related = new List<ErpRelatedDoc>();
        if (item is not null)
        {
            related.Add(new ErpRelatedDoc($"Item master {item.ItemId}", "item", item.ItemId, null,
                item.IsCritical ? "critical" : "non-critical"));
        }

        return new ErpRecordVm("stock", key, $"Stock {key}",
            [$"On hand {ErpFormat.Qty(stock)}"], null, null,
            [
                new("Item", key, ErpRoutes.RecordHref("item", key)),
                new("Description", item?.Description ?? "—"),
                new("Unit", item?.Unit ?? "—"),
                new("On hand", ErpFormat.Qty(stock)),
                new("Lots", ErpFormat.Count(txns.Count)),
            ],
            [new ErpLineTable("Lots",
                [new("Receipt"), new("Lot"), new("Status"), new("Quantity", true)],
                txns.Select(t => (IReadOnlyList<ErpCell>)
                [
                    new(t.ReceiptNumber), new(t.LotRef), new(t.Status.ToString()),
                    new(ErpFormat.Qty(t.Quantity), true),
                ]).ToList())],
            related,
            [
                new(ErpActions.QuarantineLot, "Quarantine lot",
                    "Moves a lot to quarantine under an existing hold. The ERP validates the hold and lot.",
                    [new(ErpActions.InLotRef, "Lot", true), new(ErpActions.InHoldId, "Hold id", true)]),
                new(ErpActions.ReleaseLot, "Release lot",
                    "Releases a quarantined lot under its hold. The ERP validates the hold and lot.",
                    [new(ErpActions.InLotRef, "Lot", true), new(ErpActions.InHoldId, "Hold id", true)]),
                new(ErpActions.ConsumeLot, "Consume lot",
                    "Issues lot quantity to a sales order. The ERP validates stock and state.",
                    [new(ErpActions.InLotRef, "Lot", true), new(ErpActions.InSoNumber, "Sales order", true),
                     new(ErpActions.InQuantity, "Quantity", true)]),
            ],
            [], [], notes, opening);
    }

    private async Task<ErpRecordVm> InvoiceRecordAsync(
        string vendor, string number, IReadOnlySet<string> opening, List<string> notes,
        CancellationToken ct)
    {
        var inv = await _erp.GetInvoiceAsync(vendor, number, ct).ConfigureAwait(false);
        if (inv is null)
        {
            return Shell("invoice", ErpDocTypes.InvoiceDocNumber(vendor, number),
                $"Invoice {vendor}/{number} not found in ERP.", opening, notes);
        }

        var docNumber = ErpDocTypes.InvoiceDocNumber(inv.VendorId, inv.SupplierInvoiceNumber);
        var total = inv.Lines.Sum(l => l.Quantity * l.UnitPrice);
        var related = new List<ErpRelatedDoc>
        {
            new($"Vendor {inv.VendorId}", "vendor", inv.VendorId, null, "supplier"),
        };
        if (inv.PoNumber is not null)
        {
            related.Add(new ErpRelatedDoc($"Purchase order {inv.PoNumber}", "po", inv.PoNumber, null,
                "source"));
        }

        var payments = await ListPaymentsAsync(ct).ConfigureAwait(false);
        notes.AddRange(payments.Notes);
        related.AddRange(payments.Rows
            .Where(p => p.Lines.Any(l =>
                string.Equals(l.VendorId, inv.VendorId, StringComparison.Ordinal)
                && string.Equals(l.SupplierInvoiceNumber, inv.SupplierInvoiceNumber, StringComparison.Ordinal)))
            .Select(p => new ErpRelatedDoc($"Payment {p.PaymentNumber}", "payment", p.PaymentNumber, null,
                p.Status.ToString())));
        await AddBlockingHoldsAsync(docNumber, related, notes, ct).ConfigureAwait(false);

        var tables = new List<ErpLineTable>
        {
            new("Lines",
                [new("PO line"), new("Quantity", true), new("Unit price", true),
                 new("Amount", true), new("Disposition"), new("Hold reason")],
                inv.Lines.Select(l => (IReadOnlyList<ErpCell>)
                [
                    new(l.PoLineRef ?? "—"), new(ErpFormat.Qty(l.Quantity), true),
                    new(ErpFormat.Amount(l.UnitPrice), true),
                    new(ErpFormat.Amount(l.Quantity * l.UnitPrice), true),
                    new(l.Disposition.ToString()), new(l.HoldReason ?? "—"),
                ]).ToList()),
        };
        if (inv.MatchResults.Count > 0)
        {
            tables.Add(new ErpLineTable("Match results",
                [new("PO line"), new("Matched"), new("Message")],
                inv.MatchResults.Select(m => (IReadOnlyList<ErpCell>)
                [
                    new(m.PoLineRef ?? "header"), new(m.Matched ? "yes" : "NO"), new(m.Message),
                ]).ToList()));
        }

        try
        {
            var settlements = await _erp.GetSettlementsAsync(
                inv.VendorId, inv.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            if (settlements.Count > 0)
            {
                tables.Add(new ErpLineTable("Settlements",
                    [new("Payment"), new("Settled", true), new("Remaining", true)],
                    settlements.Select(s => (IReadOnlyList<ErpCell>)
                    [
                        new(s.PaymentNumber, Href: ErpRoutes.RecordHref("payment", s.PaymentNumber)),
                        new(ErpFormat.Amount(s.SettledAmount), true),
                        new(ErpFormat.Amount(s.RemainingBalance), true),
                    ]).ToList()));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Settlement lookup failed for {Doc}.", docNumber);
            notes.Add($"Settlements for {docNumber} failed: {ex.Message}.");
        }

        var approvals = await ApprovalsTableAsync("Invoice", docNumber, notes, ct).ConfigureAwait(false);
        if (approvals is not null)
        {
            tables.Add(approvals);
        }

        var actions = new List<ErpActionDef>();
        if (inv.Status is InvoiceStatus.Received or InvoiceStatus.Matched or InvoiceStatus.Approved)
        {
            actions.Add(new(ErpActions.PostInvoice, "Post invoice",
                "Posts the invoice to the ledger. Matching, holds, and state are enforced by the ERP.",
                []));
        }

        if (inv.Lines.Any(l => l.Disposition == LineDisposition.Held))
        {
            actions.Add(new(ErpActions.ReleaseInvoiceLine, "Release held line",
                "Releases a held quantity back to payable. The ERP validates the hold.",
                [new(ErpActions.InPoLineRef, "PO line", true),
                 new(ErpActions.InQuantity, "Quantity", true)]));
        }

        var badges = new List<string> { $"Status {inv.Status}" };
        if (inv.Voucher is not null)
        {
            badges.Add($"Voucher {inv.Voucher}");
        }

        return new ErpRecordVm("invoice", docNumber, $"Invoice {inv.SupplierInvoiceNumber}",
            badges, inv.Source, inv.SupplierInvoiceNumber,
            [
                new("Supplier invoice", inv.SupplierInvoiceNumber),
                new("Vendor", inv.VendorId, ErpRoutes.RecordHref("vendor", inv.VendorId)),
                new("Purchase order", inv.PoNumber ?? "—",
                    inv.PoNumber is null ? null : ErpRoutes.RecordHref("po", inv.PoNumber)),
                new("Status", inv.Status.ToString()),
                new("Voucher", inv.Voucher ?? "none yet"),
                new("Total", ErpFormat.Money(total, inv.Currency)),
                new("Created by", inv.CreatedBy),
            ],
            tables, related, actions, [], [], notes, opening);
    }

    private async Task<ErpRecordVm> PaymentRecordAsync(
        string key, IReadOnlySet<string> opening, List<string> notes, CancellationToken ct)
    {
        var pay = await _erp.GetPaymentAsync(key, ct).ConfigureAwait(false);
        if (pay is null)
        {
            return Shell("payment", key, $"Payment {key} not found in ERP.", opening, notes);
        }

        var related = pay.Lines
            .Select(l => ErpDocTypes.InvoiceDocNumber(l.VendorId, l.SupplierInvoiceNumber))
            .Distinct(StringComparer.Ordinal)
            .Select(doc =>
            {
                var parts = doc.Split('/', 2);
                return new ErpRelatedDoc($"Invoice {parts[1]} ({parts[0]})",
                    "invoice", parts[0], parts[1], "settled line");
            })
            .ToList<ErpRelatedDoc>();
        await AddBlockingHoldsAsync(pay.PaymentNumber, related, notes, ct).ConfigureAwait(false);

        var actions = new List<ErpActionDef>();
        if (pay.Status == PaymentStatus.Draft)
        {
            actions.Add(SubmitAction());
        }
        else if (pay.Status == PaymentStatus.Submitted)
        {
            actions.Add(ApproveAction());
            actions.Add(RejectAction());
        }
        else if (pay.Status == PaymentStatus.Approved)
        {
            actions.Add(new(ErpActions.PostPayment, "Post payment",
                "Posts the payment. Bank verification and state are enforced by the ERP.", []));
        }

        var tables = new List<ErpLineTable>
        {
            new("Lines",
                [new("Vendor"), new("Invoice"), new("Amount", true)],
                pay.Lines.Select(l => (IReadOnlyList<ErpCell>)
                [
                    new(l.VendorId, Href: ErpRoutes.RecordHref("vendor", l.VendorId)),
                    new(l.SupplierInvoiceNumber,
                        Href: ErpRoutes.RecordHref("invoice", l.VendorId, l.SupplierInvoiceNumber)),
                    new(ErpFormat.Amount(l.Amount), true),
                ]).ToList()),
        };
        var approvals = await ApprovalsTableAsync("Payment", pay.PaymentNumber, notes, ct).ConfigureAwait(false);
        if (approvals is not null)
        {
            tables.Add(approvals);
        }

        return new ErpRecordVm("payment", key, $"Payment {pay.PaymentNumber}",
            [$"Status {pay.Status}"], pay.Source, pay.PaymentNumber,
            [
                new("Payment", pay.PaymentNumber),
                new("Status", pay.Status.ToString()),
                new("Total", ErpFormat.Money(pay.Lines.Sum(l => l.Amount), pay.Currency)),
                new("Due", pay.DueDate.ToString()),
                new("Payee bank", $"{pay.PayeeBank.AccountRef} (verified: {pay.PayeeBank.Verified})"),
                new("Created by", pay.CreatedBy),
            ],
            tables, related, actions, [], [], notes, opening);
    }

    private async Task<ErpRecordVm> HoldRecordAsync(
        string key, IReadOnlySet<string> opening, List<string> notes, CancellationToken ct)
    {
        var holds = await ListHoldsAsync(ct).ConfigureAwait(false);
        notes.AddRange(holds.Notes);
        var hold = holds.Rows.FirstOrDefault(h =>
            string.Equals(h.HoldId, key, StringComparison.Ordinal));
        if (hold is null)
        {
            return Shell("hold", key,
                $"Hold {key} is not open (released holds leave the open list).", opening, notes);
        }

        var blocks = string.Join(" ", new[]
            { hold.DocumentType, hold.DocumentNumber, hold.PoLineRef, hold.LotRef, hold.CartonId }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var related = new List<ErpRelatedDoc>();
        if (hold.DocumentType is not null
            && ErpDocTypes.RecordFor(hold.DocumentType, hold.DocumentNumber) is { } target)
        {
            related.Add(new ErpRelatedDoc(
                $"Blocked {hold.DocumentType} {hold.DocumentNumber}",
                target.Kind, target.Key, target.Key2, "blocked"));
        }

        var tables = new List<ErpLineTable>();
        try
        {
            var history = await _erp.GetHoldHistoryAsync(hold.HoldId, ct).ConfigureAwait(false);
            if (history.Count > 0)
            {
                tables.Add(new ErpLineTable("Release history",
                    [new("Released by"), new("Released at"), new("Approval ref")],
                    history.Select(h => (IReadOnlyList<ErpCell>)
                    [
                        new(h.ReleasedBy), new(h.ReleasedAt.ToString("u")), new(h.ApprovalRef ?? "—"),
                    ]).ToList()));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hold history failed for {Hold}.", hold.HoldId);
            notes.Add($"Hold history for {hold.HoldId} failed: {ex.Message}.");
        }

        return new ErpRecordVm("hold", key, $"Hold {hold.HoldId}",
            [hold.ReasonCode, hold.Status.ToString()], null, null,
            [
                new("Hold", hold.HoldId),
                new("Reason", $"{hold.ReasonCode}: {hold.Note}"),
                new("Rule", hold.RuleId ?? "—"),
                new("Case", hold.CaseId, $"cases/{hold.CaseId}"),
                new("Set by", $"{hold.SetBy} at {hold.SetAt:u}"),
                new("Blocks", string.IsNullOrWhiteSpace(blocks) ? "—" : blocks),
            ],
            tables, related,
            [new(ErpActions.ReleaseHold, "Release hold",
                "Releases the hold. The ERP enforces who may release and what it unblocks.",
                [new(ErpActions.InApprovalRef, "Approval ref", false)])],
            [], [], notes, opening);
    }

    private async Task<ErpRecordVm> VendorRecordAsync(
        string key, IReadOnlySet<string> opening, List<string> notes, CancellationToken ct)
    {
        var vendors = await ListVendorsAsync(ct).ConfigureAwait(false);
        notes.AddRange(vendors.Notes);
        var vendor = vendors.Rows.FirstOrDefault(v =>
            string.Equals(v.VendorId, key, StringComparison.Ordinal));
        if (vendor is null)
        {
            return Shell("vendor", key, $"Vendor {key} not found in ERP.", opening, notes);
        }

        var header = new List<ErpFact>
        {
            new("Vendor", vendor.VendorId),
            new("Name", vendor.Name),
            new("Country of origin", vendor.CountryOfOrigin),
            new("Payment terms", vendor.PaymentTermsId),
            new("Email", VendorLabels.EmailOrPlaceholder(vendor.Email)),
            new("Blocked", vendor.IsBlocked ? $"yes: {vendor.BlockReason}" : "no"),
        };
        try
        {
            var bank = await _erp.GetVendorBankAsync(vendor.VendorId, ct).ConfigureAwait(false);
            header.Add(new("Bank",
                bank is null
                    ? "none on file"
                    : $"{bank.AccountRef} (verified: {bank.Verified})"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vendor bank lookup failed for {Vendor}.", vendor.VendorId);
            notes.Add($"Vendor bank for {vendor.VendorId} failed: {ex.Message}.");
        }

        var tables = new List<ErpLineTable>();
        try
        {
            var docs = await _erp.GetVendorDocumentsAsync(vendor.VendorId, ct).ConfigureAwait(false);
            if (docs.Count > 0)
            {
                tables.Add(new ErpLineTable("Documents",
                    [new("Kind"), new("Reference"), new("Present")],
                    docs.Select(d => (IReadOnlyList<ErpCell>)
                    [
                        new(d.Kind), new(d.Reference), new(d.Present ? "yes" : "NO"),
                    ]).ToList()));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vendor document lookup failed for {Vendor}.", vendor.VendorId);
            notes.Add($"Vendor documents for {vendor.VendorId} failed: {ex.Message}.");
        }

        var related = new List<ErpRelatedDoc>();
        var pos = await ListPurchaseOrdersByVendorAsync(vendor.VendorId, ct).ConfigureAwait(false);
        if (pos.Notes.Count > 0)
        {
            notes.Add("Related purchase orders unavailable: " + string.Join(" ", pos.Notes));
        }

        related.AddRange(pos.Rows.Select(p => new ErpRelatedDoc(
            $"Purchase order {p.PoNumber}", "po", p.PoNumber, null,
            $"{p.ApprovalStatus} / {p.DocStatus}")));
        var invoices = await ListInvoicesAsync(ct).ConfigureAwait(false);
        notes.AddRange(invoices.Notes);
        related.AddRange(invoices.Rows
            .Where(i => string.Equals(i.VendorId, vendor.VendorId, StringComparison.Ordinal))
            .Select(i => new ErpRelatedDoc(
                $"Invoice {i.SupplierInvoiceNumber}", "invoice", i.VendorId, i.SupplierInvoiceNumber,
                i.Status.ToString())));

        var actions = new List<ErpActionDef>();
        if (vendor.IsBlocked)
        {
            actions.Add(new(ErpActions.UnblockVendor, "Unblock vendor",
                "Clears the vendor block. The ERP records who unblocked.", []));
        }
        else
        {
            actions.Add(new(ErpActions.BlockVendor, "Block vendor",
                "Blocks the vendor with a reason. The ERP records who blocked.",
                [new(ErpActions.InReason, "Reason", true)]));
        }

        actions.Add(new(ErpActions.VerifyBank, "Verify bank details",
            "Marks the vendor bank details verified out of band.",
            []));

        return new ErpRecordVm("vendor", key, $"Vendor {vendor.VendorId}",
            [vendor.IsBlocked ? "blocked" : "active"], null, vendor.VendorId,
            header, tables, related, actions, [], [], notes, opening);
    }

    private async Task<ErpRecordVm> ItemRecordAsync(
        string key, IReadOnlySet<string> opening, List<string> notes, CancellationToken ct)
    {
        ItemDto? item;
        try
        {
            item = await _erp.GetItemAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Item {Item} lookup failed.", key);
            notes.Add($"Item {key} lookup failed: {ex.Message}.");
            return Shell("item", key, null, opening, notes);
        }

        if (item is null)
        {
            return Shell("item", key, $"Item {key} not found in ERP.", opening, notes);
        }

        var related = new List<ErpRelatedDoc>
        {
            new($"Stock and lots for {item.ItemId}", "stock", item.ItemId, null, "inventory"),
        };
        var tables = new List<ErpLineTable>();
        try
        {
            var bom = await _erp.GetBomAsync(item.ItemId, ct).ConfigureAwait(false);
            if (bom.Count > 0)
            {
                tables.Add(new ErpLineTable("Bill of materials",
                    [new("Component"), new("Quantity per", true)],
                    bom.Select(b => (IReadOnlyList<ErpCell>)
                    [
                        new(b.ComponentItemId, Href: ErpRoutes.RecordHref("item", b.ComponentItemId)),
                        new(ErpFormat.Qty(b.QtyPer), true),
                    ]).ToList()));
                related.AddRange(bom.Select(b => new ErpRelatedDoc(
                    $"Component {b.ComponentItemId}", "item", b.ComponentItemId, null,
                    $"{ErpFormat.Qty(b.QtyPer)} per")));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "BOM lookup failed for {Item}.", item.ItemId);
            notes.Add($"Bill of materials for {item.ItemId} failed: {ex.Message}.");
        }

        return new ErpRecordVm("item", key, $"Item {item.ItemId}",
            [item.IsCritical ? "critical part" : "non-critical part"], null, null,
            [
                new("Item", item.ItemId),
                new("Description", item.Description),
                new("Unit", item.Unit),
                new("Critical", item.IsCritical ? "yes" : "no"),
                new("Units per carton", ErpFormat.Count(item.UnitsPerCarton)),
            ],
            tables, related, [], [], [], notes, opening);
    }
}

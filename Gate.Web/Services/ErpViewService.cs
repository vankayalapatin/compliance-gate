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
    private readonly CountryNames? _countries;

    public ErpViewService(
        IErpAdapter erp, CaseViewService views, ILogger<ErpViewService> log, CountryNames? countries = null)
    {
        _erp = erp;
        _views = views;
        _log = log;
        _countries = countries;
    }

    /// <summary>Read-only purchase history for one item: the volume-history
    /// rows the country-share math reads, grouped by vendor country with
    /// totals. Same adapter source as the math line, so the page totals
    /// always equal the numbers in the line.</summary>
    public async Task<PurchaseHistoryVm> GetPurchaseHistoryAsync(string itemId, CancellationToken ct = default)
    {
        var notes = new List<string>();
        string? description = null;
        try
        {
            description = (await _erp.GetItemAsync(itemId, ct).ConfigureAwait(false))?.Description;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Purchase history could not read item {ItemId}.", itemId);
            notes.Add($"Item {itemId} lookup failed: {ex.Message}.");
        }

        IReadOnlyList<VolumeHistory> history;
        try
        {
            history = await _erp.GetVolumeHistoryAsync(itemId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Purchase history could not read volume history for {ItemId}.", itemId);
            notes.Add($"Purchase history for {itemId} failed: {ex.Message}.");
            history = [];
        }

        var rows = history
            .Select(h => new PurchaseHistoryRow(
                h.Source, h.Country, OriginCapMath.CountryName(_countries?.Map, h.Country), h.Cells,
                ReceiptHref(h.Source)))
            .ToList();
        var totals = rows
            .GroupBy(r => r.CountryCode, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PurchaseHistoryCountry(
                g.Key, OriginCapMath.CountryName(_countries?.Map, g.Key), g.Sum(r => r.Cells)))
            .ToList();
        if (rows.Count == 0 && notes.Count == 0)
        {
            notes.Add($"No purchase history for item {itemId} in the window.");
        }

        return new PurchaseHistoryVm(
            itemId, description,
            "One row per posted receipt line in the trailing 12 months — the same rows the country-share math reads.",
            rows, totals, rows.Sum(r => r.Cells),
            ErpRoutes.RecordHref("item", itemId), notes);
    }

    private static string? ReceiptHref(string source)
    {
        const string prefix = "Receipt ";
        if (!source.StartsWith(prefix, StringComparison.Ordinal)
            || source.Length <= prefix.Length)
        {
            return null;
        }

        return ErpRoutes.RecordHref("receipt", source.Substring(prefix.Length));
    }

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
        var notes = new List<string>();
        try
        {
            return kind switch
            {
                "so" => await SoRecordAsync(key, notes, ct).ConfigureAwait(false),
                "pr" => await PrRecordAsync(key, notes, ct).ConfigureAwait(false),
                "po" => await PoRecordAsync(key, notes, ct).ConfigureAwait(false),
                "asn" => await AsnRecordAsync(key, notes, ct).ConfigureAwait(false),
                "receipt" => await ReceiptRecordAsync(key, notes, ct).ConfigureAwait(false),
                "stock" => await StockRecordAsync(key, notes, ct).ConfigureAwait(false),
                "invoice" => key2 is null
                    ? Shell(kind, key, $"Invoice {key} needs its vendor: open it as vendor/number.", notes)
                    : await InvoiceRecordAsync(key, key2, notes, ct).ConfigureAwait(false),
                "payment" => await PaymentRecordAsync(key, notes, ct).ConfigureAwait(false),
                "hold" => await HoldRecordAsync(key, notes, ct).ConfigureAwait(false),
                "vendor" => await VendorRecordAsync(key, notes, ct).ConfigureAwait(false),
                "item" => await ItemRecordAsync(key, notes, ct).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Record {Kind} {Key} failed.", kind, key);
            notes.Add($"Record {kind} {key} failed: {ex.Message}.");
            return Shell(kind, key, null, notes);
        }
    }

    private static ErpRecordVm Shell(
        string kind, string key, string? note, IReadOnlyList<string> notes)
    {
        var all = note is null ? notes : notes.Append(note).ToList();
        return new ErpRecordVm(kind, key, $"{ErpModules.TitleFor(kind)} {key}",
            [], null, key, [], [], [], [], [], [], all);
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

    /// <summary>One audit stamp for display: who plus when, with the unset
    /// placeholder for either half. Stored actors never render as "system".</summary>
    private static string VendorNameFrom(IReadOnlyDictionary<string, string> map, string vendorId) =>
        map.TryGetValue(vendorId, out var name) ? name : vendorId;

    private async Task<IReadOnlyDictionary<string, string>> VendorMapAsync(CancellationToken ct)
    {
        try
        {
            var vendors = await _erp.GetVendorsAsync(ct).ConfigureAwait(false);
            return vendors.ToDictionary(v => v.VendorId, v => v.Name, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vendor map lookup failed.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task<string> VendorNameAsync(string vendorId, CancellationToken ct)
    {
        try
        {
            var vendors = await _erp.GetVendorsAsync(ct).ConfigureAwait(false);
            return vendors.FirstOrDefault(
                v => string.Equals(v.VendorId, vendorId, StringComparison.Ordinal))?.Name
                ?? vendorId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vendor name lookup failed for {Vendor}.", vendorId);
            return vendorId;
        }
    }

    private static string CartonWords(int count) =>
        count == 1 ? "1 carton" : $"{ErpFormat.Count(count)} cartons";

    private static string Stamp(string? by, DateTimeOffset? at) =>
        $"{ActorLabels.Display(by)} · {(at is null ? ActorLabels.Unset : ErpFormat.When(at.Value))}";

    /// <summary>Acting-user role for role-gated views (the AP clerk hides
    /// quarantine-lot views plus vendor-block actions). Null when the user
    /// or role cannot be read; the ERP still enforces every control.</summary>
    public async Task<string?> GetUserRoleAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            return (await _erp.GetUserAsync(userId, ct).ConfigureAwait(false))?.Role;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Role lookup failed for {User}.", userId);
            return null;
        }
    }

    private async Task<ErpLineTable?> ApprovalsTableAsync(
        string docType, string docNumber, string? currency, List<string> notes, CancellationToken ct)
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
                new(a.RoutedTo), new(ActorLabels.Display(a.ActedBy)),
                new(a.Approved ? "approved" : "rejected"),
                new($"{ErpFormat.Money(a.DocumentAmount, currency)} / {ErpFormat.Money(a.LimitChecked, currency)}", true),
                new(CommentLabels.Display(a.Comment)),
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

    /// <summary>Company header for PO documents: entity code plus legal
    /// name plus bill-to/ship-to addresses from the ERP company info.
    /// Missing company info surfaces as a note; nothing is guessed.</summary>
    private async Task AddCompanyFactsAsync(
        List<ErpFact> header, List<string> notes, CancellationToken ct)
    {
        CompanyInfoDto? company;
        try
        {
            company = await _erp.GetCompanyInfoAsync(ct).ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            _log.LogWarning(ex, "Company header is not implemented by the ERP layer yet.");
            notes.Add("Company header is not implemented by the ERP layer yet.");
            return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Company info lookup failed.");
            notes.Add($"Company header failed: {ex.Message}.");
            return;
        }

        if (company is null)
        {
            notes.Add("ERP has no company info; the document header shows no company.");
            return;
        }

        header.Add(new ErpFact("Company", $"{company.EntityCode} · {company.LegalName}"));
        header.Add(new ErpFact("Bill to", company.BillToAddress));
        header.Add(new ErpFact("Ship to", company.ShipToAddress));
    }

    private async Task<ErpRecordVm> SoRecordAsync(
        string key, List<string> notes, CancellationToken ct)
    {
        var so = await _erp.GetSalesOrderAsync(key, ct).ConfigureAwait(false);
        if (so is null)
        {
            return Shell("so", key, $"Sales order {key} not found in ERP.", notes);
        }

        var related = so.Lines.Select(l => l.ItemId).Distinct(StringComparer.Ordinal)
            .Select(item => new ErpRelatedDoc($"Item {item}", "item", item, null, "Master record"))
            .ToList<ErpRelatedDoc>();
        await AddBlockingHoldsAsync(so.SoNumber, related, notes, ct).ConfigureAwait(false);
        var statuses = Enum.GetValues<SalesOrderStatus>()
            .Select(s => new ErpActionOption(s.ToString(), ErpStatusWords.Of(s)))
            .ToList();
        return new ErpRecordVm("so", key, $"Sales order {so.SoNumber}",
            [$"Status {ErpStatusWords.Of(so.Status)}"], null, so.SoNumber,
            [
                new("Sales order", so.SoNumber),
                new("Status", ErpStatusWords.Of(so.Status)),
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
            [], [], notes);
    }

    private async Task<ErpRecordVm> PrRecordAsync(
        string key, List<string> notes, CancellationToken ct)
    {
        var pr = await _erp.GetPrAsync(key, ct).ConfigureAwait(false);
        if (pr is null)
        {
            return Shell("pr", key, $"Purchase requisition {key} not found in ERP.", notes);
        }

        var related = new List<ErpRelatedDoc>();
        var pos = await ListPurchaseOrdersAsync(ct).ConfigureAwait(false);
        notes.AddRange(pos.Notes);
        related.AddRange(pos.Rows
            .Where(p => string.Equals(p.PrNumber, pr.PrNumber, StringComparison.Ordinal))
            .Select(p => new ErpRelatedDoc($"Purchase order {p.PoNumber}", "po", p.PoNumber, null,
                ErpStatusWords.Of(p.ApprovalStatus) + " / " + ErpStatusWords.Of(p.DocStatus))));
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
                "New", HrefOverride: ErpRoutes.NewHref("po", pr.PrNumber)));
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
        var approvals = await ApprovalsTableAsync("PR", pr.PrNumber, pr.Currency, notes, ct).ConfigureAwait(false);
        if (approvals is not null)
        {
            tables.Add(approvals);
        }

        var screening = await _views.ScreeningSectionAsync(pr.PrNumber, null, ct).ConfigureAwait(false);
        if (screening is not null)
        {
            tables.Add(screening);
        }

        return new ErpRecordVm("pr", key, $"Purchase requisition {pr.PrNumber}",
            [$"Status {ErpStatusWords.Of(pr.Status)}"], pr.Source, pr.PrNumber,
            [
                new("Purchase requisition", pr.PrNumber),
                new("Status", ErpStatusWords.Of(pr.Status)),
                new("Requester", pr.Requester),
                new("Estimated amount", ErpFormat.Money(pr.EstimatedAmount, pr.Currency)),
                new("Created", Stamp(pr.CreatedBy, pr.CreatedAt)),
                new("Approved", Stamp(pr.ApprovedBy, pr.ApprovedAt)),
            ],
            tables, related, actions, [], [], notes);
    }

    private async Task<ErpRecordVm> PoRecordAsync(
        string key, List<string> notes, CancellationToken ct)
    {
        var po = await _erp.GetPurchaseOrderAsync(key, ct).ConfigureAwait(false);
        if (po is null)
        {
            return Shell("po", key, $"Purchase order {key} not found in ERP.", notes);
        }

        // The vendor stays out of related documents: the header links it.
        var related = new List<ErpRelatedDoc>
        {
            new($"Purchase requisition {po.PrNumber}", "pr", po.PrNumber, null, "Source document"),
        };
        var asns = await ListAsnsAsync(ct).ConfigureAwait(false);
        notes.AddRange(asns.Notes);
        AsnDto? asn = null;
        foreach (var row in asns.Rows.Where(a =>
            string.Equals(a.PoNumber, po.PoNumber, StringComparison.Ordinal)))
        {
            asn ??= row;
            related.Add(new ErpRelatedDoc($"ASN {row.AsnNumber}", "asn", row.AsnNumber, null,
                CartonWords(row.Cartons.Count)));
        }

        var receipts = await ListReceiptsAsync(ct).ConfigureAwait(false);
        notes.AddRange(receipts.Notes);
        related.AddRange(receipts.Rows
            .Where(r => string.Equals(r.PoNumber, po.PoNumber, StringComparison.Ordinal))
            .Select(r => new ErpRelatedDoc($"Receipt {r.ReceiptNumber}", "receipt", r.ReceiptNumber, null,
                ErpStatusWords.Of(r.Status))));
        await AddBlockingHoldsAsync(po.PoNumber, related, notes, ct).ConfigureAwait(false);

        var header = new List<ErpFact>
        {
            new("Purchase order", po.PoNumber),
        };
        await AddCompanyFactsAsync(header, notes, ct).ConfigureAwait(false);
        header.AddRange(
        [
            new("Purchase requisition", po.PrNumber, ErpRoutes.RecordHref("pr", po.PrNumber)),
            new("Vendor", po.VendorId, ErpRoutes.RecordHref("vendor", po.VendorId)),
            new("Payment terms", po.PaymentTermsId),
            new("Approval", ErpStatusWords.Of(po.ApprovalStatus)),
            new("Document status", ErpStatusWords.Of(po.DocStatus)),
            new("Total", ErpFormat.Money(po.Lines.Sum(l => l.LineAmount), po.Currency)),
            new("Created", Stamp(po.CreatedBy, po.CreatedAt)),
            new("Approved", Stamp(po.ApprovedBy, po.ApprovedAt)),
        ]);
        try
        {
            var confirm = await _erp.GetVendorConfirmationAsync(po.PoNumber, ct).ConfigureAwait(false);
            header.Add(new("Vendor confirmation",
                confirm is null ? "none yet" : "confirmed " + ErpFormat.Day(confirm.ConfirmDate)));
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
                    new(ErpFormat.Money(l.UnitPrice, po.Currency), true),
                    new(ErpFormat.Money(l.LineAmount, po.Currency), true),
                ]).ToList()),
        };
        var approvals = await ApprovalsTableAsync("PO", po.PoNumber, po.Currency, notes, ct).ConfigureAwait(false);
        if (approvals is not null)
        {
            tables.Add(approvals);
        }

        if (asn is not null)
        {
            related.Add(new ErpRelatedDoc("Post receipt from this ASN", "receipt", asn.AsnNumber, null,
                "New", HrefOverride: ErpRoutes.NewHref("receipt", asn.AsnNumber)));
        }

        var screening = await _views.ScreeningSectionAsync(po.PrNumber, po.PoNumber, ct).ConfigureAwait(false);
        if (screening is not null)
        {
            tables.Add(screening);
        }

        return new ErpRecordVm("po", key, $"Purchase order {po.PoNumber}",
            [$"Approval {ErpStatusWords.Of(po.ApprovalStatus)}", $"Document {ErpStatusWords.Of(po.DocStatus)}"],
            po.Source, po.PoNumber, header, tables, related, actions, [], [], notes);
    }

    private async Task<ErpRecordVm> AsnRecordAsync(
        string key, List<string> notes, CancellationToken ct)
    {
        var asn = await _erp.GetAsnAsync(key, ct).ConfigureAwait(false);
        if (asn is null)
        {
            return Shell("asn", key, $"ASN {key} not found in ERP.", notes);
        }

        var related = new List<ErpRelatedDoc>
        {
            new($"Purchase order {asn.PoNumber}", "po", asn.PoNumber, null, "Source document"),
            new("Post receipt from this ASN", "receipt", asn.AsnNumber, null,
                "New", HrefOverride: ErpRoutes.NewHref("receipt", asn.AsnNumber)),
        };
        var receipts = await ListReceiptsAsync(ct).ConfigureAwait(false);
        notes.AddRange(receipts.Notes);
        related.AddRange(receipts.Rows
            .Where(r => string.Equals(r.PoNumber, asn.PoNumber, StringComparison.Ordinal))
            .Select(r => new ErpRelatedDoc($"Receipt {r.ReceiptNumber}", "receipt", r.ReceiptNumber, null,
                ErpStatusWords.Of(r.Status))));

        return new ErpRecordVm("asn", key, $"ASN {asn.AsnNumber}",
            [CartonWords(asn.Cartons.Count)], asn.Source, asn.AsnNumber,
            [
                new("ASN", asn.AsnNumber),
                new("Purchase order", asn.PoNumber, ErpRoutes.RecordHref("po", asn.PoNumber)),
                new("Created by", ActorLabels.Display(asn.CreatedBy)),
            ],
            [new ErpLineTable("Cartons",
                [new("Carton"), new("Item"), new("Units / carton", true), new("Lot")],
                asn.Cartons.Select(c => (IReadOnlyList<ErpCell>)
                [
                    new(c.CartonId), new(c.ItemId, Href: ErpRoutes.RecordHref("item", c.ItemId)),
                    new(ErpFormat.Count(c.UnitsPerCarton), true), new(c.LotRef),
                ]).ToList())],
            related, [], [], [], notes);
    }

    private async Task<ErpRecordVm> ReceiptRecordAsync(
        string key, List<string> notes, CancellationToken ct)
    {
        var receipt = await _erp.GetReceiptAsync(key, ct).ConfigureAwait(false);
        if (receipt is null)
        {
            return Shell("receipt", key, $"Receipt {key} not found in ERP.", notes);
        }

        var related = new List<ErpRelatedDoc>
        {
            new($"Purchase order {receipt.PoNumber}", "po", receipt.PoNumber, null, "Source document"),
        };
        var asns = await ListAsnsAsync(ct).ConfigureAwait(false);
        notes.AddRange(asns.Notes);
        var asn = asns.Rows.FirstOrDefault(a =>
            string.Equals(a.PoNumber, receipt.PoNumber, StringComparison.Ordinal));
        if (asn is not null)
        {
            related.Add(new ErpRelatedDoc($"ASN {asn.AsnNumber}", "asn", asn.AsnNumber, null,
                CartonWords(asn.Cartons.Count)));
        }

        var invoices = await ListInvoicesAsync(ct).ConfigureAwait(false);
        notes.AddRange(invoices.Notes);
        related.AddRange(invoices.Rows
            .Where(i => string.Equals(i.PoNumber, receipt.PoNumber, StringComparison.Ordinal))
            .Select(i => new ErpRelatedDoc(
                $"Invoice {i.SupplierInvoiceNumber}", "invoice", i.VendorId, i.SupplierInvoiceNumber,
                ErpStatusWords.Of(i.Status))));
        await AddBlockingHoldsAsync(receipt.ReceiptNumber, related, notes, ct).ConfigureAwait(false);

        var media = await _views.GetReceiptMediaAsync(receipt, asn, ct).ConfigureAwait(false);
        return new ErpRecordVm("receipt", key, $"Receipt {receipt.ReceiptNumber}",
            [$"Status {ErpStatusWords.Of(receipt.Status)}"], receipt.Source, receipt.ReceiptNumber,
            [
                new("Receipt", receipt.ReceiptNumber),
                new("Purchase order", receipt.PoNumber, ErpRoutes.RecordHref("po", receipt.PoNumber)),
                new("Supplier packing slip", receipt.SupplierPackingSlipNumber),
                new("Status", ErpStatusWords.Of(receipt.Status)),
                new("Posting date", receipt.PostingDate is null ? ActorLabels.Unset : ErpFormat.Day(receipt.PostingDate.Value)),
                new("Created", Stamp(receipt.CreatedBy, receipt.CreatedAt)),
                new("Posted", Stamp(receipt.PostedBy, receipt.PostedAt)),
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
            related, [], media.ImageFileNames, media.CachedReads, notes);
    }

    private async Task<ErpRecordVm> StockRecordAsync(
        string key, List<string> notes, CancellationToken ct)
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
            return Shell("stock", key, $"No stock or lots for item {key} in ERP.", notes);
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
                    new(t.ReceiptNumber), new(t.LotRef), new(ErpStatusWords.Of(t.Status)),
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
            [], [], notes);
    }

    private async Task<ErpRecordVm> InvoiceRecordAsync(
        string vendor, string number, List<string> notes, CancellationToken ct)
    {
        var inv = await _erp.GetInvoiceAsync(vendor, number, ct).ConfigureAwait(false);
        if (inv is null)
        {
            return Shell("invoice", ErpDocTypes.InvoiceDocNumber(vendor, number),
                $"Invoice {vendor}/{number} not found in ERP.", notes);
        }

        var docNumber = ErpDocTypes.InvoiceDocNumber(inv.VendorId, inv.SupplierInvoiceNumber);
        var total = inv.Lines.Sum(l => l.Quantity * l.UnitPrice);
        var vendorName = await VendorNameAsync(inv.VendorId, ct).ConfigureAwait(false);
        // The vendor stays out of related documents: the header links it.
        var related = new List<ErpRelatedDoc>();
        if (inv.PoNumber is not null)
        {
            related.Add(new ErpRelatedDoc($"Purchase order {inv.PoNumber}", "po", inv.PoNumber, null,
                "Source document"));
        }

        var payments = await ListPaymentsAsync(ct).ConfigureAwait(false);
        notes.AddRange(payments.Notes);
        related.AddRange(payments.Rows
            .Where(p => p.Lines.Any(l =>
                string.Equals(l.VendorId, inv.VendorId, StringComparison.Ordinal)
                && string.Equals(l.SupplierInvoiceNumber, inv.SupplierInvoiceNumber, StringComparison.Ordinal)))
            .Select(p => new ErpRelatedDoc($"Payment {p.PaymentNumber}", "payment", p.PaymentNumber, null,
                ErpStatusWords.Of(p.Status))));
        await AddBlockingHoldsAsync(docNumber, related, notes, ct).ConfigureAwait(false);

        var tables = new List<ErpLineTable>
        {
            new("Lines",
                [new("PO line"), new("Quantity", true), new("Unit price", true),
                 new("Amount", true), new("Disposition"), new("Hold reason")],
                inv.Lines.Select(l => (IReadOnlyList<ErpCell>)
                [
                    new(l.PoLineRef ?? "—"), new(ErpFormat.Qty(l.Quantity), true),
                    new(ErpFormat.Money(l.UnitPrice, inv.Currency), true),
                    new(ErpFormat.Money(l.Quantity * l.UnitPrice, inv.Currency), true),
                    new(ErpStatusWords.Of(l.Disposition)), new(l.HoldReason ?? "—"),
                ]).ToList()),
        };
        if (inv.MatchResults.Count > 0)
        {
            tables.Add(new ErpLineTable("Match results",
                [new("PO line"), new("Matched"), new("Message")],
                inv.MatchResults.Select(m => (IReadOnlyList<ErpCell>)
                [
                    new(m.PoLineRef ?? "header"), new(m.Matched ? "yes" : "no"), new(m.Message),
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
                        new(ErpFormat.Money(s.SettledAmount, inv.Currency), true),
                        new(ErpFormat.Money(s.RemainingBalance, inv.Currency), true),
                    ]).ToList()));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Settlement lookup failed for {Doc}.", docNumber);
            notes.Add($"Settlements for {docNumber} failed: {ex.Message}.");
        }

        try
        {
            var releases = await _erp.GetHeldReleasesAsync(
                inv.VendorId, inv.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            if (releases.Count > 0)
            {
                tables.Add(new ErpLineTable("Release history",
                    [new("PO line"), new("Quantity", true), new("Released by"), new("Released at")],
                    releases.Select(r => (IReadOnlyList<ErpCell>)
                    [
                        new(r.PoLineRef ?? "—"), new(ErpFormat.Qty(r.Quantity), true),
                        new(ActorLabels.Display(r.ReleasedBy)), new(ErpFormat.When(r.ReleasedAt)),
                    ]).ToList()));
            }
        }
        catch (NotSupportedException)
        {
            notes.Add("Release history is unavailable from this ERP read.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Release-history lookup failed for {Doc}.", docNumber);
            notes.Add($"Release history for {docNumber} failed: {ex.Message}.");
        }

        var approvals = await ApprovalsTableAsync("Invoice", docNumber, inv.Currency, notes, ct).ConfigureAwait(false);
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

        // No release-held-line form: held lines release only when the
        // quarantine or review behind them closes (reshot photo, warehouse
        // review), never from this page.
        if (inv.Lines.Any(l => l.Disposition == LineDisposition.Held))
        {
            notes.Add("Held lines release only when the quarantine or review behind them closes.");
        }

        var badges = new List<string> { $"Status {ErpStatusWords.Of(inv.Status)}" };
        if (inv.Voucher is not null)
        {
            badges.Add($"Voucher {inv.Voucher}");
        }

        return new ErpRecordVm("invoice", docNumber, $"Invoice {inv.SupplierInvoiceNumber}",
            badges, inv.Source, inv.SupplierInvoiceNumber,
            [
                new("Supplier invoice", inv.SupplierInvoiceNumber),
                new("Vendor", vendorName, ErpRoutes.RecordHref("vendor", inv.VendorId)),
                new("Purchase order", inv.PoNumber ?? "—",
                    inv.PoNumber is null ? null : ErpRoutes.RecordHref("po", inv.PoNumber)),
                new("Status", ErpStatusWords.Of(inv.Status)),
                new("Voucher", inv.Voucher ?? "none yet"),
                new("Total", ErpFormat.Money(total, inv.Currency)),
                new("Due", inv.DueDate is null ? ActorLabels.Unset : ErpFormat.Day(inv.DueDate.Value)),
                new("Created", Stamp(inv.CreatedBy, inv.CreatedAt)),
                new("Approved", Stamp(inv.ApprovedBy, inv.ApprovedAt)),
                new("Posted", Stamp(inv.PostedBy, inv.PostedAt)),
            ],
            tables, related, actions, [], [], notes);
    }

    private async Task<ErpRecordVm> PaymentRecordAsync(
        string key, List<string> notes, CancellationToken ct)
    {
        var pay = await _erp.GetPaymentAsync(key, ct).ConfigureAwait(false);
        if (pay is null)
        {
            return Shell("payment", key, $"Payment {key} not found in ERP.", notes);
        }

        var related = pay.Lines
            .Select(l => ErpDocTypes.InvoiceDocNumber(l.VendorId, l.SupplierInvoiceNumber))
            .Distinct(StringComparer.Ordinal)
            .Select(doc =>
            {
                var parts = doc.Split('/', 2);
                return new ErpRelatedDoc($"Invoice {parts[1]} ({parts[0]})",
                    "invoice", parts[0], parts[1], "Settled line");
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

        var vendorMap = await VendorMapAsync(ct).ConfigureAwait(false);
        var tables = new List<ErpLineTable>
        {
            new("Lines",
                [new("Vendor"), new("Invoice"), new("Amount", true)],
                pay.Lines.Select(l => (IReadOnlyList<ErpCell>)
                [
                    new(VendorNameFrom(vendorMap, l.VendorId), Href: ErpRoutes.RecordHref("vendor", l.VendorId)),
                    new(l.SupplierInvoiceNumber,
                        Href: ErpRoutes.RecordHref("invoice", l.VendorId, l.SupplierInvoiceNumber)),
                    new(ErpFormat.Money(l.Amount, pay.Currency), true),
                ]).ToList()),
        };
        var approvals = await ApprovalsTableAsync("Payment", pay.PaymentNumber, pay.Currency, notes, ct).ConfigureAwait(false);
        if (approvals is not null)
        {
            tables.Add(approvals);
        }

        return new ErpRecordVm("payment", key, $"Payment {pay.PaymentNumber}",
            [$"Status {ErpStatusWords.Of(pay.Status)}"], pay.Source, pay.PaymentNumber,
            [
                new("Payment", pay.PaymentNumber),
                new("Status", ErpStatusWords.Of(pay.Status)),
                new("Total", ErpFormat.Money(pay.Lines.Sum(l => l.Amount), pay.Currency)),
                new("Due", ErpFormat.Day(pay.DueDate)),
                new("Payee bank", $"{pay.PayeeBank.AccountRef} ({(pay.PayeeBank.Verified ? "verified" : "not verified")})"),
                new("Created", Stamp(pay.CreatedBy, pay.CreatedAt)),
                new("Approved", Stamp(pay.ApprovedBy, pay.ApprovedAt)),
                new("Posted", Stamp(pay.PostedBy, pay.PostedAt)),
            ],
            tables, related, actions, [], [], notes);
    }

    private async Task<ErpRecordVm> HoldRecordAsync(
        string key, List<string> notes, CancellationToken ct)
    {
        var holds = await ListHoldsAsync(ct).ConfigureAwait(false);
        notes.AddRange(holds.Notes);
        var hold = holds.Rows.FirstOrDefault(h =>
            string.Equals(h.HoldId, key, StringComparison.Ordinal));
        if (hold is null)
        {
            return Shell("hold", key,
                $"Hold {key} is not open (released holds leave the open list).", notes);
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
                target.Kind, target.Key, target.Key2, "Blocked document"));
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
                        new(ActorLabels.Display(h.ReleasedBy)), new(ErpFormat.When(h.ReleasedAt)), new(h.ApprovalRef ?? "—"),
                    ]).ToList()));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hold history failed for {Hold}.", hold.HoldId);
            notes.Add($"Hold history for {hold.HoldId} failed: {ex.Message}.");
        }

        return new ErpRecordVm("hold", key, $"Hold {hold.HoldId}",
            [hold.ReasonCode, ErpStatusWords.Of(hold.Status)], null, null,
            [
                new("Hold", hold.HoldId),
                new("Reason", $"{hold.ReasonCode}: {hold.Note}"),
                new("Rule", hold.RuleId ?? "—"),
                new("Case", hold.CaseId, $"cases/{hold.CaseId}"),
                new("Set by", $"{ActorLabels.Display(hold.SetBy)} at {ErpFormat.When(hold.SetAt)}"),
                new("Blocks", string.IsNullOrWhiteSpace(blocks) ? "—" : blocks),
            ],
            tables, related,
            [new(ErpActions.ReleaseHold, "Release hold",
                "Releases the hold. The ERP enforces who may release and what it unblocks.",
                [new(ErpActions.InApprovalRef, "Approval ref", false)])],
            [], [], notes);
    }

    private async Task<ErpRecordVm> VendorRecordAsync(
        string key, List<string> notes, CancellationToken ct)
    {
        var vendors = await ListVendorsAsync(ct).ConfigureAwait(false);
        notes.AddRange(vendors.Notes);
        var vendor = vendors.Rows.FirstOrDefault(v =>
            string.Equals(v.VendorId, key, StringComparison.Ordinal));
        if (vendor is null)
        {
            return Shell("vendor", key, $"Vendor {key} not found in ERP.", notes);
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
                    : $"{bank.AccountRef} ({(bank.Verified ? "verified" : "not verified")})"));
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
            ErpStatusWords.Of(p.ApprovalStatus) + " / " + ErpStatusWords.Of(p.DocStatus))));
        var invoices = await ListInvoicesAsync(ct).ConfigureAwait(false);
        notes.AddRange(invoices.Notes);
        related.AddRange(invoices.Rows
            .Where(i => string.Equals(i.VendorId, vendor.VendorId, StringComparison.Ordinal))
            .Select(i => new ErpRelatedDoc(
                $"Invoice {i.SupplierInvoiceNumber}", "invoice", i.VendorId, i.SupplierInvoiceNumber,
                ErpStatusWords.Of(i.Status))));

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
            [vendor.IsBlocked ? "Blocked" : "Active"], null, vendor.VendorId,
            header, tables, related, actions, [], [], notes);
    }

    private async Task<ErpRecordVm> ItemRecordAsync(
        string key, List<string> notes, CancellationToken ct)
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
            return Shell("item", key, null, notes);
        }

        if (item is null)
        {
            return Shell("item", key, $"Item {key} not found in ERP.", notes);
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
                new("Purchase history", "View", OriginCapMath.HistoryHref(item.ItemId)),
            ],
            tables, related, [], [], [], notes);
    }
}

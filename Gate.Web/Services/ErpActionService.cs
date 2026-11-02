// ERP record actions plus New-form creates, attempted for real through the
// adapter. The UI never pre-enforces: candidates come from document state,
// the ERP enforces controls and validation, and every refusal comes back
// as the outcome message with its reason. Human creates travel with the
// human source and the acting user as creator.
using System.Globalization;
using Gate.Agent;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed record ErpOutcome(bool Ok, string Message, string? CreatedKey = null, string? CreatedKey2 = null);

public sealed class ErpActionService
{
    private readonly IErpAdapter _erp;
    private readonly CaseRunner _runner;
    private readonly ILogger<ErpActionService> _log;

    public ErpActionService(IErpAdapter erp, CaseRunner runner, ILogger<ErpActionService> log)
    {
        _erp = erp;
        _runner = runner;
        _log = log;
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private static string? Required(IReadOnlyDictionary<string, string> inputs, string key) =>
        inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static string? Optional(IReadOnlyDictionary<string, string> inputs, string key) =>
        inputs.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    // One attempt-for-real entry point for every record-page action. Input
    // presence is form completeness (the page cannot send what it never
    // asked for); every ERP rule refusal surfaces as the failure message.
    public async Task<ErpOutcome> AttemptAsync(
        string kind, string key, string? key2, string actionId,
        IReadOnlyDictionary<string, string> inputs, string? actedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new ErpOutcome(false, "Pick the acting user first (top bar).");
        }

        var actor = actedBy.Trim();
        try
        {
            return actionId switch
            {
                ErpActions.Submit => await SubmitAsync(kind, key, key2, inputs, actor, ct).ConfigureAwait(false),
                ErpActions.Approve => await DecideAsync(kind, key, key2, true, inputs, actor, ct).ConfigureAwait(false),
                ErpActions.Reject => await DecideAsync(kind, key, key2, false, inputs, actor, ct).ConfigureAwait(false),
                ErpActions.PostInvoice when key2 is not null =>
                    await PostInvoiceAsync(key, key2, actor, ct).ConfigureAwait(false),
                ErpActions.ReleaseInvoiceLine when key2 is not null =>
                    await ReleaseInvoiceLineAsync(key, key2, inputs, actor, ct).ConfigureAwait(false),
                ErpActions.PostPayment => await PostPaymentAsync(key, actor, ct).ConfigureAwait(false),
                ErpActions.ReleaseHold => await ReleaseHoldAsync(key, inputs, actor, ct).ConfigureAwait(false),
                ErpActions.BlockVendor => await BlockVendorAsync(key, inputs, actor, ct).ConfigureAwait(false),
                ErpActions.UnblockVendor => await UnblockVendorAsync(key, actor, ct).ConfigureAwait(false),
                ErpActions.VerifyBank => await VerifyBankAsync(key, actor, ct).ConfigureAwait(false),
                ErpActions.ClosePo => await ClosePoAsync(key, actor, ct).ConfigureAwait(false),
                ErpActions.AddPoNote => await AddPoNoteAsync(key, inputs, actor, ct).ConfigureAwait(false),
                ErpActions.UpdateSoStatus => await UpdateSoStatusAsync(key, inputs, actor, ct).ConfigureAwait(false),
                ErpActions.QuarantineLot => await MoveLotAsync(true, key, inputs, actor, ct).ConfigureAwait(false),
                ErpActions.ReleaseLot => await MoveLotAsync(false, key, inputs, actor, ct).ConfigureAwait(false),
                ErpActions.ConsumeLot => await ConsumeLotAsync(key, inputs, actor, ct).ConfigureAwait(false),
                _ => new ErpOutcome(false, $"Unknown action '{actionId}' for {kind} {key}."),
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ERP action {Action} failed for {Kind} {Key}.", actionId, kind, key);
            return new ErpOutcome(false, $"{ex.Message}");
        }
    }

    private static string DocNumber(string kind, string key, string? key2) =>
        kind == "invoice" && key2 is not null
            ? ErpDocTypes.InvoiceDocNumber(key, key2)
            : key;

    private async Task<ErpOutcome> SubmitAsync(
        string kind, string key, string? key2,
        IReadOnlyDictionary<string, string> inputs, string actor, CancellationToken ct)
    {
        var docType = ErpDocTypes.ForKind(kind);
        if (docType is null)
        {
            return new ErpOutcome(false, $"Submit is not a workflow action for {kind} {key}.");
        }

        var routedTo = Required(inputs, ErpActions.InRoutedTo);
        if (routedTo is null)
        {
            return new ErpOutcome(false, "Route-to user is required.");
        }

        var docNumber = DocNumber(kind, key, key2);
        await _erp.SubmitForApprovalAsync(docType, docNumber, routedTo, actor, NewKey(), ct)
            .ConfigureAwait(false);
        return new ErpOutcome(true, $"{docType} {docNumber} submitted to {routedTo}.");
    }

    private async Task<ErpOutcome> DecideAsync(
        string kind, string key, string? key2, bool approved,
        IReadOnlyDictionary<string, string> inputs, string actor, CancellationToken ct)
    {
        var docType = ErpDocTypes.ForKind(kind);
        if (docType is null)
        {
            return new ErpOutcome(false, $"Decide is not a workflow action for {kind} {key}.");
        }

        var docNumber = DocNumber(kind, key, key2);
        var comment = Optional(inputs, ErpActions.InComment);
        await _erp.RecordApprovalAsync(docType, docNumber, actor, approved, comment, NewKey(), ct)
            .ConfigureAwait(false);
        // The ERP decision is committed: settle the linked case cards through
        // the same resolver the Decisions page uses, so no card stays pending
        // on an already-decided document. Settle failures surface as notes;
        // they never roll back the committed approval.
        var (settled, notes) = await _runner.SettleErpDecisionsAsync(
            docType, docNumber, approved, actor, comment, ct).ConfigureAwait(false);
        var message = $"{docType} {docNumber} {(approved ? "approved" : "rejected")} by {actor}.";
        if (settled.Count > 0)
        {
            message += $" Settled {settled.Count} linked case card(s).";
        }

        message += string.Concat(notes.Select(n => $" {n}"));
        return new ErpOutcome(true, message);
    }

    private async Task<ErpOutcome> PostInvoiceAsync(
        string vendor, string number, string actor, CancellationToken ct)
    {
        var posted = await _erp.PostInvoiceAsync(vendor, number, actor, NewKey(), ct: ct)
            .ConfigureAwait(false);
        return new ErpOutcome(true,
            $"Invoice {vendor}/{number} posted (voucher {posted.Voucher ?? "none"}).");
    }

    private async Task<ErpOutcome> ReleaseInvoiceLineAsync(
        string vendor, string number,
        IReadOnlyDictionary<string, string> inputs, string actor, CancellationToken ct)
    {
        var poLineRef = Required(inputs, ErpActions.InPoLineRef);
        if (poLineRef is null)
        {
            return new ErpOutcome(false, "PO line is required.");
        }

        if (!TryQty(Required(inputs, ErpActions.InQuantity), out var qty))
        {
            return new ErpOutcome(false, "Quantity is required and must be above zero.");
        }

        await _erp.ReleaseHeldInvoiceLineAsync(vendor, number, poLineRef, qty, actor, NewKey(), ct)
            .ConfigureAwait(false);
        return new ErpOutcome(true,
            $"Held line {poLineRef} released by {ErpFormat.Qty(qty)} on {vendor}/{number}.");
    }

    private async Task<ErpOutcome> PostPaymentAsync(string key, string actor, CancellationToken ct)
    {
        await _erp.PostPaymentAsync(key, actor, NewKey(), ct: ct).ConfigureAwait(false);
        return new ErpOutcome(true, $"Payment {key} posted.");
    }

    private async Task<ErpOutcome> ReleaseHoldAsync(
        string key, IReadOnlyDictionary<string, string> inputs, string actor, CancellationToken ct)
    {
        var approvalRef = Optional(inputs, ErpActions.InApprovalRef);
        await _erp.ReleaseHoldAsync(key, actor, approvalRef, NewKey(), ct).ConfigureAwait(false);
        return new ErpOutcome(true, $"Hold {key} released.");
    }

    private async Task<ErpOutcome> BlockVendorAsync(
        string key, IReadOnlyDictionary<string, string> inputs, string actor, CancellationToken ct)
    {
        var reason = Required(inputs, ErpActions.InReason);
        if (reason is null)
        {
            return new ErpOutcome(false, "Block reason is required.");
        }

        await _erp.BlockVendorAsync(key, reason, actor, NewKey(), ct).ConfigureAwait(false);
        return new ErpOutcome(true, $"Vendor {key} blocked.");
    }

    private async Task<ErpOutcome> UnblockVendorAsync(string key, string actor, CancellationToken ct)
    {
        await _erp.UnblockVendorAsync(key, actor, NewKey(), ct).ConfigureAwait(false);
        return new ErpOutcome(true, $"Vendor {key} unblocked.");
    }

    private async Task<ErpOutcome> VerifyBankAsync(string key, string actor, CancellationToken ct)
    {
        var bank = await _erp.VerifyVendorBankAsync(key, actor, NewKey(), ct).ConfigureAwait(false);
        return new ErpOutcome(true, $"Vendor {key} bank {bank.AccountRef} verified.");
    }

    private async Task<ErpOutcome> ClosePoAsync(string key, string actor, CancellationToken ct)
    {
        await _erp.ClosePoAsync(key, actor, NewKey(), ct).ConfigureAwait(false);
        return new ErpOutcome(true, $"Purchase order {key} closed.");
    }

    private async Task<ErpOutcome> AddPoNoteAsync(
        string key, IReadOnlyDictionary<string, string> inputs, string actor, CancellationToken ct)
    {
        var note = Required(inputs, ErpActions.InNote);
        if (note is null)
        {
            return new ErpOutcome(false, "Note text is required.");
        }

        await _erp.AddPoNoteAsync(key, note, actor, NewKey(), ct).ConfigureAwait(false);
        return new ErpOutcome(true, $"Note added to purchase order {key}.");
    }

    private async Task<ErpOutcome> UpdateSoStatusAsync(
        string key, IReadOnlyDictionary<string, string> inputs, string actor, CancellationToken ct)
    {
        var raw = Required(inputs, ErpActions.InStatus);
        if (raw is null || !Enum.TryParse<SalesOrderStatus>(raw, out var status))
        {
            return new ErpOutcome(false, "New status is required.");
        }

        await _erp.UpdateSalesOrderStatusAsync(key, status, actor, NewKey(), ct).ConfigureAwait(false);
        return new ErpOutcome(true, $"Sales order {key} set to {status}.");
    }

    private async Task<ErpOutcome> MoveLotAsync(
        bool quarantine, string itemId,
        IReadOnlyDictionary<string, string> inputs, string actor, CancellationToken ct)
    {
        var lotRef = Required(inputs, ErpActions.InLotRef);
        var holdId = Required(inputs, ErpActions.InHoldId);
        if (lotRef is null || holdId is null)
        {
            return new ErpOutcome(false, "Lot and hold id are required.");
        }

        if (quarantine)
        {
            await _erp.QuarantineLotAsync(lotRef, holdId, actor, NewKey(), ct).ConfigureAwait(false);
            return new ErpOutcome(true, $"Lot {lotRef} quarantined under {holdId}.");
        }

        await _erp.ReleaseLotAsync(lotRef, holdId, actor, NewKey(), ct).ConfigureAwait(false);
        return new ErpOutcome(true, $"Lot {lotRef} released under {holdId}.");
    }

    private async Task<ErpOutcome> ConsumeLotAsync(
        string itemId,
        IReadOnlyDictionary<string, string> inputs, string actor, CancellationToken ct)
    {
        var lotRef = Required(inputs, ErpActions.InLotRef);
        var soNumber = Required(inputs, ErpActions.InSoNumber);
        if (lotRef is null || soNumber is null)
        {
            return new ErpOutcome(false, "Lot and sales order are required.");
        }

        if (!TryQty(Required(inputs, ErpActions.InQuantity), out var qty))
        {
            return new ErpOutcome(false, "Quantity is required and must be above zero.");
        }

        await _erp.ConsumeLotAsync(lotRef, soNumber, qty, actor, NewKey(), ct).ConfigureAwait(false);
        return new ErpOutcome(true,
            $"Lot {lotRef} consumed {ErpFormat.Qty(qty)} to {soNumber}.");
    }

    private static bool TryQty(string? raw, out decimal qty)
    {
        qty = 0;
        return !string.IsNullOrWhiteSpace(raw)
            && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out qty)
            && qty > 0;
    }

    // New-form creates. Mandatory-field presence is form completeness; every
    // ERP validation (unknown vendor, missing price, bad terms, duplicate
    // numbers) surfaces as the failure message with the ERP's reason.
    public async Task<ErpOutcome> CreateSalesOrderAsync(
        string? currency, IReadOnlyList<SalesOrderLine> lines, string? actedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new ErpOutcome(false, "Pick the acting user first (top bar).");
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            return new ErpOutcome(false, "Currency is required.");
        }

        if (lines.Count == 0)
        {
            return new ErpOutcome(false, "At least one line is required.");
        }

        try
        {
            var so = await _erp.CreateSalesOrderAsync(
                currency.Trim(), lines, RecordSource.Human, actedBy.Trim(), NewKey(), ct)
                .ConfigureAwait(false);
            return new ErpOutcome(true, $"Sales order {so.SoNumber} created.", so.SoNumber);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Create sales order failed.");
            return new ErpOutcome(false, ex.Message);
        }
    }

    public async Task<ErpOutcome> CreatePrAsync(
        string? requester, IReadOnlyList<PurchaseRequisitionLine> lines, string? actedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new ErpOutcome(false, "Pick the acting user first (top bar).");
        }

        if (string.IsNullOrWhiteSpace(requester))
        {
            return new ErpOutcome(false, "Requester is required.");
        }

        if (lines.Count == 0)
        {
            return new ErpOutcome(false, "At least one line is required.");
        }

        try
        {
            var pr = await _erp.CreatePrAsync(
                requester.Trim(), RecordSource.Human, actedBy.Trim(), lines, NewKey(), ct)
                .ConfigureAwait(false);
            return new ErpOutcome(true, $"Purchase requisition {pr.PrNumber} created.", pr.PrNumber);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Create PR failed.");
            return new ErpOutcome(false, ex.Message);
        }
    }

    public async Task<ErpOutcome> CreatePoAsync(
        string? prNumber, string? vendorId, string? actedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new ErpOutcome(false, "Pick the acting user first (top bar).");
        }

        if (string.IsNullOrWhiteSpace(prNumber) || string.IsNullOrWhiteSpace(vendorId))
        {
            return new ErpOutcome(false, "Approved PR and vendor are required.");
        }

        try
        {
            var po = await _erp.CreatePoAsync(
                prNumber.Trim(), vendorId.Trim(), RecordSource.Human, actedBy.Trim(), NewKey(), ct)
                .ConfigureAwait(false);
            return new ErpOutcome(true, $"Purchase order {po.PoNumber} created.", po.PoNumber);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Create PO failed for PR {Pr}.", prNumber);
            return new ErpOutcome(false, ex.Message);
        }
    }

    public async Task<ErpOutcome> CreateInvoiceAsync(
        string? supplierInvoiceNumber, string? vendorId, string? poNumber,
        IReadOnlyList<InvoiceLineInput> lines, string? actedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new ErpOutcome(false, "Pick the acting user first (top bar).");
        }

        if (string.IsNullOrWhiteSpace(supplierInvoiceNumber) || string.IsNullOrWhiteSpace(vendorId))
        {
            return new ErpOutcome(false, "Supplier invoice number and vendor are required.");
        }

        if (lines.Count == 0)
        {
            return new ErpOutcome(false, "At least one line is required.");
        }

        try
        {
            var inv = await _erp.CreateInvoiceAsync(
                supplierInvoiceNumber.Trim(), vendorId.Trim(),
                string.IsNullOrWhiteSpace(poNumber) ? null : poNumber.Trim(),
                RecordSource.Human, actedBy.Trim(), lines, NewKey(), ct: ct).ConfigureAwait(false);
            return new ErpOutcome(true,
                $"Invoice {inv.VendorId}/{inv.SupplierInvoiceNumber} created.",
                inv.VendorId, inv.SupplierInvoiceNumber);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Create invoice failed.");
            return new ErpOutcome(false, ex.Message);
        }
    }

    public async Task<ErpOutcome> CreatePaymentDraftAsync(
        IReadOnlyList<InvoiceKey> invoices, DateOnly? payUpToDate, string? actedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new ErpOutcome(false, "Pick the acting user first (top bar).");
        }

        if (invoices.Count == 0)
        {
            return new ErpOutcome(false, "At least one invoice is required.");
        }

        try
        {
            var pay = await _erp.CreatePaymentDraftAsync(
                invoices, payUpToDate, RecordSource.Human, actedBy.Trim(), NewKey(), ct: ct)
                .ConfigureAwait(false);
            return new ErpOutcome(true, $"Payment draft {pay.PaymentNumber} created.", pay.PaymentNumber);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Create payment draft failed.");
            return new ErpOutcome(false, ex.Message);
        }
    }

    public async Task<ErpOutcome> PostReceiptAsync(
        string? poNumber, string? packingSlip, IReadOnlyList<ReceiptLine> lines, string? actedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new ErpOutcome(false, "Pick the acting user first (top bar).");
        }

        if (string.IsNullOrWhiteSpace(poNumber) || string.IsNullOrWhiteSpace(packingSlip))
        {
            return new ErpOutcome(false, "Purchase order and packing slip are required.");
        }

        if (lines.Count == 0)
        {
            return new ErpOutcome(false, "At least one line is required.");
        }

        try
        {
            var receipt = await _erp.PostReceiptAsync(
                poNumber.Trim(), packingSlip.Trim(), RecordSource.Human, actedBy.Trim(),
                lines, NewKey(), ct: ct).ConfigureAwait(false);
            return new ErpOutcome(true,
                $"Receipt {receipt.ReceiptNumber} posted.", receipt.ReceiptNumber);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Post receipt failed for PO {Po}.", poNumber);
            return new ErpOutcome(false, ex.Message);
        }
    }

    public async Task<ErpOutcome> CreateVendorAsync(
        string? vendorId, string? name, string? countryOfOrigin, string? paymentTermsId,
        string? email, string? actedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new ErpOutcome(false, "Pick the acting user first (top bar).");
        }

        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(countryOfOrigin)
            || string.IsNullOrWhiteSpace(paymentTermsId))
        {
            return new ErpOutcome(false, "Name, country, and payment terms are required.");
        }

        try
        {
            var vendor = await _erp.CreateVendorAsync(
                string.IsNullOrWhiteSpace(vendorId) ? null : vendorId.Trim(),
                name.Trim(), countryOfOrigin.Trim(), paymentTermsId.Trim(),
                string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
                RecordSource.Human, actedBy.Trim(), NewKey(), ct).ConfigureAwait(false);
            return new ErpOutcome(true, $"Vendor {vendor.VendorId} created.", vendor.VendorId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Create vendor failed.");
            return new ErpOutcome(false, ex.Message);
        }
    }

    public async Task<ErpOutcome> CreateItemAsync(
        string? itemId, string? description, string? unit, bool isCritical,
        int unitsPerCarton, string? actedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new ErpOutcome(false, "Pick the acting user first (top bar).");
        }

        if (string.IsNullOrWhiteSpace(itemId)
            || string.IsNullOrWhiteSpace(description)
            || string.IsNullOrWhiteSpace(unit))
        {
            return new ErpOutcome(false, "Item id, description, and unit are required.");
        }

        if (unitsPerCarton <= 0)
        {
            return new ErpOutcome(false, "Units per carton must be above zero.");
        }

        try
        {
            var item = await _erp.CreateItemAsync(
                itemId.Trim(), description.Trim(), unit.Trim(), isCritical, unitsPerCarton,
                RecordSource.Human, actedBy.Trim(), NewKey(), ct).ConfigureAwait(false);
            return new ErpOutcome(true, $"Item {item.ItemId} created.", item.ItemId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Create item failed.");
            return new ErpOutcome(false, ex.Message);
        }
    }
}

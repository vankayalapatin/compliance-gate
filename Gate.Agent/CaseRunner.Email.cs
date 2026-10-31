// Vendor email: queries about held invoice problems the agent cannot resolve
// itself, plus the simulation-appended replies. Outbound mail is composed by
// Spark and recorded in the case mail file (simulation only, no SMTP); the
// recipient is always the vendor master email, and a missing address blocks
// sending with a gap error, never an invented address. Replies are read via
// Spark and acted on: corrected invoice fields re-enter intake, anything
// else is summarized onto the timeline for the clerk. Every model call is
// cached; every send and reply is idempotent.
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Agent;

public partial class CaseRunner
{
    /// <summary>Sends a Spark-composed query about a held invoice to the
    /// vendor master email. Repeating the same query returns the stored mail
    /// with no new model call. A missing vendor or address is a gap error.</summary>
    public async Task<VendorEmail> SendVendorEmailAsync(
        string caseId, string invoiceRef, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var emailId = $"{caseId}-q-{Slug(invoiceRef)}";
        var stored = EmailStore.Load(_cacheDir, caseId).Messages.FirstOrDefault(m =>
            string.Equals(m.EmailId, emailId, StringComparison.Ordinal));
        if (stored is not null)
        {
            _logger.LogInformation(
                "Case {CaseId}: query {EmailId} already sent; replay returns it unchanged.",
                caseId, emailId);
            EnsureEmailedEvent(caseId, invoiceRef, emailId);
            return stored;
        }

        var problem = CasePicker.UnemailedInvoiceProblems(file).FirstOrDefault(d =>
            string.Equals(d.DocumentNumber, invoiceRef, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Invoice '{invoiceRef}' has no unemailed AP problem on case {caseId}.");
        var vendorId = await ResolveQueryVendorAsync(file, invoiceRef, ct).ConfigureAwait(false);
        var vendor = (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(v => string.Equals(v.VendorId, vendorId, StringComparison.Ordinal));
        if (vendor is null)
        {
            _logger.LogError(
                "Case {CaseId}: PO vendor {VendorId} for invoice '{InvoiceRef}' is missing from the vendor master.",
                caseId, vendorId, invoiceRef);
            throw new InvalidOperationException(
                $"PO vendor {vendorId} for invoice '{invoiceRef}' is missing from the vendor master; cannot query it.");
        }

        if (string.IsNullOrWhiteSpace(vendor.Email))
        {
            throw new InvalidOperationException(
                $"Vendor {vendorId} has no email in the vendor master; cannot query invoice '{invoiceRef}'.");
        }

        var (subject, body, fellBack) = await ComposeQueryAsync(file, vendor, invoiceRef, problem, ct).ConfigureAwait(false);
        var email = EmailStore.Append(_cacheDir, new VendorEmail(
            emailId, caseId, vendorId, RecordSource.P2pAgent, vendor.Email!,
            subject, body, DateTimeOffset.UtcNow, InvoiceRef: invoiceRef));
        var work = new CaseWork(CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Case {caseId} vanished while emailing."));
        if (fellBack)
        {
            work.AddEvent(CaseEventKind.Note,
                $"Vendor query composition fell back to raw text instead of structured Spark composition " +
                $"for {invoiceRef} (mail {emailId}).",
                documentType: "Invoice", documentNumber: invoiceRef,
                evidenceRef: $"mail:{emailId}");
        }

        work.AddEvent(CaseEventKind.VendorEmailed,
            $"Queried {vendor.Email} about held invoice {invoiceRef}: {problem.Reason ?? problem.Title}",
            documentType: "Invoice", documentNumber: invoiceRef,
            evidenceRef: $"mail:{emailId}");
        work.Save(_cacheDir);
        _logger.LogInformation(
            "Case {CaseId}: queried {To} about {InvoiceRef} (mail {EmailId}).",
            caseId, vendor.Email, invoiceRef, emailId);
        return email;
    }

    /// <summary>Appends a vendor reply (simulation input), reads it via Spark,
    /// and acts on it: corrected invoice fields re-enter intake, otherwise the
    /// Spark summary lands on the timeline. Repeating the same reply returns
    /// the stored mail with no new model call.</summary>
    public async Task<VendorEmail> OnVendorReplyAsync(
        VendorReplyInput input, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, input.CaseId)
            ?? throw new InvalidOperationException($"Unknown case {input.CaseId}.");
        var emailId = input.EmailId
            ?? input.IdempotencyKey
            ?? $"{input.CaseId}-r-{EmailStore.Load(_cacheDir, input.CaseId).Messages.Count + 1:000}";
        var stored = EmailStore.Load(_cacheDir, input.CaseId).Messages.FirstOrDefault(m =>
            string.Equals(m.EmailId, emailId, StringComparison.Ordinal));
        if (stored is not null)
        {
            _logger.LogInformation(
                "Case {CaseId}: reply {EmailId} already recorded; replay returns it unchanged.",
                input.CaseId, emailId);
            return stored;
        }

        var vendorId = await ResolveReplyVendorAsync(file, input.From, ct).ConfigureAwait(false);
        var reply = EmailStore.Append(_cacheDir, new VendorEmail(
            emailId, input.CaseId, vendorId, input.From, RecordSource.P2pAgent,
            input.Subject ?? $"Re: vendor query for case {input.CaseId}",
            input.Body, DateTimeOffset.UtcNow, input.InReplyTo,
            InvoiceRef: ReplyInvoiceRef(file, input.InReplyTo)));
        var work = new CaseWork(CaseStore.Load(_cacheDir, input.CaseId)
            ?? throw new InvalidOperationException($"Case {input.CaseId} vanished during the reply."));
        work.AddEvent(CaseEventKind.VendorReplied,
            $"Vendor reply {emailId} from {input.From}: {Trim(input.Body)}",
            documentType: "Invoice", documentNumber: reply.InvoiceRef,
            evidenceRef: $"mail:{emailId}");
        work.Save(_cacheDir);

        await ActOnReplyAsync(input.CaseId, reply, ct).ConfigureAwait(false);
        return EmailStore.Load(_cacheDir, input.CaseId).Messages.First(m =>
            string.Equals(m.EmailId, emailId, StringComparison.Ordinal));
    }

    private async Task<string> ResolveQueryVendorAsync(CaseFile file, string invoiceRef, CancellationToken ct)
    {
        var vendors = await _erp.GetVendorsAsync(ct).ConfigureAwait(false);
        var docVendor = invoiceRef.Split('/', 2) is [var v, _] ? v : string.Empty;
        if (!string.IsNullOrWhiteSpace(docVendor)
            && vendors.Any(x => string.Equals(x.VendorId, docVendor, StringComparison.Ordinal)))
        {
            return docVendor;
        }

        var current = file.Case;
        if (current.PoNumber is not null
            && await _erp.GetPurchaseOrderAsync(current.PoNumber, ct).ConfigureAwait(false) is { } po)
        {
            return po.VendorId;
        }

        throw new InvalidOperationException(
            $"No vendor to query about '{invoiceRef}' on case {current.CaseId}: " +
            "the invoice names none and the case has no PO vendor.");
    }

    private async Task<string> ResolveReplyVendorAsync(CaseFile file, string from, CancellationToken ct)
    {
        var vendors = await _erp.GetVendorsAsync(ct).ConfigureAwait(false);
        var match = vendors.FirstOrDefault(v =>
            string.Equals(v.Email, from, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return match.VendorId;
        }

        var current = file.Case;
        if (current.PoNumber is not null
            && await _erp.GetPurchaseOrderAsync(current.PoNumber, ct).ConfigureAwait(false) is { } po)
        {
            return po.VendorId;
        }

        throw new InvalidOperationException(
            $"Vendor reply from '{from}' matches no vendor master email and case {current.CaseId} has no PO vendor.");
    }

    /// <summary>Resolves a transcribed vendor id or name to the master vendor
    /// id, or null when blank or unmatched (missing-fields path).</summary>
    private async Task<string?> ResolveVendorIdAsync(string? vendorIdOrName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(vendorIdOrName))
        {
            return null;
        }

        var vendors = await _erp.GetVendorsAsync(ct).ConfigureAwait(false);
        return vendors.FirstOrDefault(v =>
                string.Equals(v.VendorId, vendorIdOrName, StringComparison.Ordinal))?.VendorId
            ?? vendors.FirstOrDefault(v =>
                string.Equals(v.Name, vendorIdOrName, StringComparison.Ordinal))?.VendorId
            ?? vendors.FirstOrDefault(v =>
                string.Equals(v.VendorId, vendorIdOrName, StringComparison.OrdinalIgnoreCase))?.VendorId
            ?? vendors.FirstOrDefault(v =>
                string.Equals(v.Name, vendorIdOrName, StringComparison.OrdinalIgnoreCase))?.VendorId;
    }

    private static string? ReplyInvoiceRef(CaseFile file, string? inReplyTo)
    {
        if (inReplyTo is null)
        {
            return null;
        }

        return file.Events
            .Where(e => e.Kind == CaseEventKind.VendorEmailed
                && string.Equals(e.EvidenceRef, $"mail:{inReplyTo}", StringComparison.Ordinal))
            .Select(e => e.DocumentNumber)
            .FirstOrDefault();
    }

    private void EnsureEmailedEvent(string caseId, string invoiceRef, string emailId)
    {
        var file = CaseStore.Load(_cacheDir, caseId);
        if (file is null || file.Events.Any(e =>
            e.Kind == CaseEventKind.VendorEmailed
            && string.Equals(e.EvidenceRef, $"mail:{emailId}", StringComparison.Ordinal)))
        {
            return;
        }

        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.VendorEmailed,
            $"Queried about held invoice {invoiceRef} (replay).",
            documentType: "Invoice", documentNumber: invoiceRef,
            evidenceRef: $"mail:{emailId}");
        work.Save(_cacheDir);
    }

    private async Task<(string Subject, string Body, bool FellBack)> ComposeQueryAsync(
        CaseFile file, VendorDto vendor, string invoiceRef, Decision problem, CancellationToken ct)
    {
        var current = file.Case;
        var prompt =
            $"Compose a short supplier query email about held invoice {invoiceRef} " +
            $"for case {current.CaseId} (sales order {current.SoNumber}, item {current.ItemId}, " +
            $"our PO {current.PoNumber ?? "none yet"}). Vendor: {vendor.VendorId} {vendor.Name}. " +
            $"Problem: {problem.Reason ?? problem.Title}. " +
            "Ask for the corrected invoice reference, PO number, or missing details. " +
            "Return a JSON object with exactly these keys: subject (string), body (string). " +
            "Return ONLY the JSON object, no other text.";
        var result = await ResilientCalls.CompleteWithRetryAsync(
            _models, new ModelRequest(prompt, "You write short supplier query emails."),
            _logger, $"vendor query for {invoiceRef}", ct).ConfigureAwait(false);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result.Text.Trim());
            var root = doc.RootElement;
            var subject = root.TryGetProperty("subject", out var s) ? s.GetString() : null;
            var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
            if (!string.IsNullOrWhiteSpace(subject) && !string.IsNullOrWhiteSpace(body))
            {
                return (subject, body, false);
            }

            _logger.LogWarning(
                "Case {CaseId}: query composition came back without a subject and body; using the raw text as the body.",
                current.CaseId);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex,
                "Case {CaseId}: query composition did not parse as JSON; using the raw text as the body.",
                current.CaseId);
        }

        return ($"Query on invoice {invoiceRef} (case {current.CaseId})", result.Text.Trim(), true);
    }

    private async Task ActOnReplyAsync(string caseId, VendorEmail reply, CancellationToken ct)
    {
        var prompt =
            $"Read this vendor reply and extract corrected invoice fields. Reply:\n{reply.Body}\n" +
            "Return a JSON object with exactly these keys: supplier_invoice_number (string or null), " +
            "vendor_id_or_name (string or null: the supplier id code such as V-B when one is stated, " +
            "else the supplier name as printed), po_number (string or null), " +
            "lines (array of {po_line_ref (string or null), quantity (number), unit_price (number)}), " +
            "bank_account_ref (string or null), summary (one sentence). " +
            "Transcribe only what the reply states; never guess; use null. " +
            "Return ONLY the JSON object, no other text.";
        var result = await ResilientCalls.CompleteWithRetryAsync(
            _models, new ModelRequest(prompt, "You extract invoice corrections from vendor replies."),
            _logger, $"vendor reply read for {reply.EmailId}", ct).ConfigureAwait(false);
        TranscribedInvoice? extracted;
        string summary;
        try
        {
            extracted = ParseTranscribedInvoice(result.Text);
            summary = ReadReplySummary(result.Text);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex,
                "Case {CaseId}: reply {EmailId} did not parse; kept for the clerk.",
                caseId, reply.EmailId);
            NoteOn(caseId,
                $"Vendor reply {reply.EmailId} needs clerk reading: the model output did not parse.",
                "Invoice", reply.InvoiceRef);
            return;
        }

        if (string.IsNullOrWhiteSpace(extracted.SupplierInvoiceNumber) || extracted.Lines.Count == 0)
        {
            NoteOn(caseId, $"Vendor reply {reply.EmailId}: {summary}", "Invoice", reply.InvoiceRef);
            return;
        }

        var vendorId = await ResolveVendorIdAsync(extracted.VendorIdOrName, ct).ConfigureAwait(false);
        var poNumber = await ResolvePoNumberAsync(extracted.PoNumber, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(extracted.VendorIdOrName) && vendorId is null)
        {
            var card = await ProposeVendorIdentityAsync(
                caseId, extracted.VendorIdOrName, [$"mail:{reply.EmailId}"],
                new VendorIdentityIntake(
                    extracted.SupplierInvoiceNumber, poNumber,
                    extracted.Lines, extracted.BankAccountRef), ct).ConfigureAwait(false);
            NoteOn(caseId,
                $"Vendor reply {reply.EmailId} proposes vendor '{extracted.VendorIdOrName}': " +
                $"clerk confirm needed (decision {card?.DecisionId ?? "unknown"}); intake waits.",
                "Invoice", reply.InvoiceRef);
            return;
        }

        var outcome = await OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, extracted.SupplierInvoiceNumber!, vendorId, poNumber,
            extracted.Lines, extracted.BankAccountRef,
            IdempotencyKey: $"{caseId}:reply:{reply.EmailId}"), ct).ConfigureAwait(false);
        var doc = outcome.Key is null
            ? $"{vendorId ?? "?"}/{extracted.SupplierInvoiceNumber}"
            : $"{outcome.Key.VendorId}/{outcome.Key.SupplierInvoiceNumber}";
        NoteOn(caseId,
            $"Vendor reply {reply.EmailId} re-intaked as {doc}: accepted {outcome.AcceptedValue}, held {outcome.HeldValue}.",
            "Invoice", doc);
    }

    private async Task<string?> ResolvePoNumberAsync(string? poNumber, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(poNumber)
        || await _erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false) is null
            ? null
            : poNumber;

    private void NoteOn(string caseId, string summary, string? documentType, string? documentNumber)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Case {caseId} vanished while noting the reply.");
        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.Note, summary,
            documentType: documentType, documentNumber: documentNumber);
        work.Save(_cacheDir);
    }

    private static string Slug(string value) =>
        new(value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private static string Trim(string value, int max = 160) =>
        value.Length <= max ? value : value[..max] + "...";
}

// Vendor-identity confirm: transcribed vendor text that is non-blank but
// matches no master vendor id or name exactly (exact matching stays C#)
// gets a Spark suggestion plus a clerk card. Spark suggests which master
// vendor the text means (a stated id code wins, else the printed supplier
// name); C# validates the suggestion names a real master id (unknown is a
// loud gap error, never invented) and raises a VendorIdentity card carrying
// the proposed id, the raw text, and the photo or mail refs. Approve
// confirms the match into intake, reject keeps the missing-fields path. No
// auto-matching, ever: the vendor stays null until the clerk approves.
using System.Text.Json;
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Agent;

/// <summary>Extracted invoice details waiting on a vendor-identity confirm.
/// Stored at propose time so the human wait (hours or days) never loses the
// intake; the resolve step completes it.</summary>
public sealed record VendorIdentityIntake(
    string? SupplierInvoiceNumber,
    string? PoNumber,
    IReadOnlyList<SupplierInvoiceLineInput> Lines,
    string? BankAccountRef);

public partial class CaseRunner
{
    /// <summary>Proposes the master vendor for raw transcribed text. Blank
    /// text or an exact C# id/name match returns null with no model call and
    /// no card (the caller proceeds with the exact id or the missing-fields
    /// path). Otherwise Spark suggests one master id, C# validates it, and a
    /// VendorIdentity card is raised for the clerk; the vendor stays null
    /// until approval. Repeating the same raw text plus evidence returns the
    /// stored card with no new model call. Pending intake details (when the
    /// call comes from transcription or a vendor reply) are stored for the
    /// resolve step to complete.</summary>
    public async Task<Decision?> ProposeVendorIdentityAsync(
        string caseId, string? rawVendorText, IReadOnlyList<string> evidenceRefs,
        VendorIdentityIntake? pendingIntake = null, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        if (string.IsNullOrWhiteSpace(rawVendorText))
        {
            return null;
        }

        var exact = await ResolveVendorIdAsync(rawVendorText, ct).ConfigureAwait(false);
        if (exact is not null)
        {
            return null;
        }

        var raw = rawVendorText.Trim();
        var replay = file.Decisions.FirstOrDefault(d =>
            d.Kind == DecisionKind.VendorIdentity
            && d.Status == DecisionStatus.Pending
            && string.Equals(d.Reason, raw, StringComparison.Ordinal)
            && d.EvidenceRefs.SequenceEqual(evidenceRefs, StringComparer.Ordinal));
        if (replay is not null)
        {
            _logger.LogInformation(
                "Case {CaseId}: vendor identity for '{Raw}' already proposed as {Proposed}; replay returns it unchanged.",
                caseId, raw, replay.DocumentNumber);
            return replay;
        }

        var proposed = await SuggestVendorIdAsync(raw, ct).ConfigureAwait(false);
        var clerk = await ResolveApClerkAsync(ResolveRequester(file) ?? RecordSource.P2pAgent, ct)
            .ConfigureAwait(false);
        var work = new CaseWork(CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Case {caseId} vanished while proposing vendor identity."));
        var card = work.CreateDecision(
            DecisionKind.VendorIdentity, $"Confirm vendor identity for '{raw}'", clerk,
            "Vendor", proposed, reason: raw, evidenceRefs: evidenceRefs);
        work.AddEvent(CaseEventKind.Note,
            $"Vendor identity suggested: '{raw}' proposes {proposed}; clerk confirm needed.",
            documentType: "Vendor", documentNumber: proposed,
            evidenceRef: evidenceRefs.FirstOrDefault());
        work.Save(_cacheDir);
        _logger.LogInformation(
            "Case {CaseId}: vendor identity '{Raw}' proposes {Proposed} (decision {DecisionId}).",
            caseId, raw, proposed, card.DecisionId);
        if (pendingIntake is not null)
        {
            VendorIdentityStore.Add(_cacheDir, new VendorIdentityPending(
                card.DecisionId, caseId, pendingIntake.SupplierInvoiceNumber, raw,
                proposed, pendingIntake.PoNumber, pendingIntake.Lines, pendingIntake.BankAccountRef));
        }

        return CaseStore.Load(_cacheDir, caseId)?.Decisions
            .FirstOrDefault(d => string.Equals(d.DecisionId, card.DecisionId, StringComparison.Ordinal))
            ?? card;
    }

    /// <summary>Asks Spark which master vendor the raw text means. The master
    /// list comes from the ERP at runtime; the criteria are fixed: a stated
    /// vendor id code wins, else the supplier name as printed. C# validates
    /// the suggestion names a real master id; anything else is a loud gap
    /// error, never an invented match.</summary>
    private async Task<string> SuggestVendorIdAsync(string rawVendorText, CancellationToken ct)
    {
        var vendors = await _erp.GetVendorsAsync(ct).ConfigureAwait(false);
        var roster = string.Join("; ", vendors.Select(v => $"{v.VendorId} ({v.Name})"));
        var prompt =
            $"Transcribed supplier reference: '{rawVendorText}'. " +
            $"Master vendors (id plus name): {roster}. " +
            "Criteria: a stated vendor id code in the reference " +
            "(for example an id in Name (ID) prose) wins; " +
            "else match the supplier name as printed. " +
            "Return a JSON object with exactly one key: vendor_id " +
            "(string, one of the master ids). " +
            "Return ONLY the JSON object, no other text.";
        var result = await ResilientCalls.CompleteWithRetryAsync(
            _models,
            new ModelRequest(prompt, "You match transcribed supplier references to vendor master ids."),
            _logger, $"vendor suggestion for '{rawVendorText}'", ct).ConfigureAwait(false);
        string? suggested;
        try
        {
            using var doc = JsonDocument.Parse(StripSuggestionFences(result.Text));
            var root = doc.RootElement;
            suggested = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("vendor_id", out var id)
                && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Vendor suggestion for '{Raw}' did not parse; refusing to invent a match.",
                rawVendorText);
            throw new InvalidOperationException(
                $"Vendor suggestion for '{rawVendorText}' did not parse as {{\"vendor_id\": ...}}; refusing to invent a match.", ex);
        }

        if (string.IsNullOrWhiteSpace(suggested))
        {
            _logger.LogError(
                "Vendor suggestion for '{Raw}' came back blank; refusing to invent a match.",
                rawVendorText);
            throw new InvalidOperationException(
                $"Vendor suggestion for '{rawVendorText}' came back blank; refusing to invent a match.");
        }

        var match = vendors.FirstOrDefault(v =>
            string.Equals(v.VendorId, suggested, StringComparison.Ordinal));
        if (match is null)
        {
            _logger.LogError(
                "Vendor suggestion for '{Raw}' names '{Suggested}', which is not in the vendor master; refusing to invent a match.",
                rawVendorText, suggested);
            throw new InvalidOperationException(
                $"Vendor suggestion for '{rawVendorText}' names '{suggested}', which is not in the vendor master; refusing to invent a match.");
        }

        return match.VendorId;
    }

    private static string StripSuggestionFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var lines = trimmed.Split('\n').ToList();
        lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Trim().StartsWith("```", StringComparison.Ordinal))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    /// <summary>Completes a decided vendor-identity card's stored intake:
    /// approve intakes with the confirmed vendor, reject intakes with a null
    /// vendor (the missing-fields path). No stored intake means a direct
    /// propose with nothing to complete. Runs before the decision is marked
    /// so a failure leaves the card pending and retryable.</summary>
    private async Task<bool> CompleteVendorIdentityIntakeAsync(
        string caseId, Decision decision, bool approved, CancellationToken ct)
    {
        var pending = VendorIdentityStore.Find(_cacheDir, caseId, decision.DecisionId);
        if (pending is null)
        {
            return false;
        }

        var vendor = approved ? decision.DocumentNumber : null;
        await OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, pending.SupplierInvoiceNumber ?? string.Empty, vendor, pending.PoNumber,
            pending.Lines, pending.BankAccountRef,
            IdempotencyKey: $"{caseId}:vendor-identity:{decision.DecisionId}:{(approved ? "approve" : "reject")}"),
            ct, vendorIdentityCompletion: true).ConfigureAwait(false);
        VendorIdentityStore.Remove(_cacheDir, caseId, decision.DecisionId);
        var outcome = approved ? "approved" : "rejected";
        _logger.LogInformation(
            "Case {CaseId}: vendor identity {DecisionId} {Outcome}: " +
            "intake for '{Invoice}' completed with vendor '{Vendor}'.",
            caseId, decision.DecisionId, outcome,
            pending.SupplierInvoiceNumber ?? "null", vendor ?? "null");
        return true;
    }

    private Decision ResolveVendorIdentity(
        CaseWork work, Decision decision, bool approved, string decidedBy, string? comment)
    {
        var updated = work.Decide(decision, approved, decidedBy, comment);
        var raw = decision.Reason ?? "unknown text";
        var proposed = decision.DocumentNumber ?? "unknown vendor";
        work.AddEvent(CaseEventKind.Note,
            approved
                ? $"Vendor identity confirmed: '{raw}' is {proposed} (decision {decision.DecisionId} by {decidedBy})."
                : $"Vendor identity rejected: '{raw}' is not {proposed}; missing-fields path kept (decision {decision.DecisionId} by {decidedBy}).",
            documentType: "Vendor", documentNumber: proposed);
        return updated;
    }
}

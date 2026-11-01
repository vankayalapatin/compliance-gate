// Opening-decision seed: a deterministic projector from ERP pending
// approvals to opening case shells plus one Decision card each. Pure C#:
// no model calls, no replay, no agent-authored timeline entries. The
// scenario-reset path calls it once after the ERP reload; re-runs skip
// shells whose card already exists (pending or decided) and never rewrite
// them, so human decisions are never resurrected or duplicated.
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Agent;

public partial class CaseRunner
{
    /// <summary>Projects every ERP pending approval into an opening case
    /// shell (IsOpeningData, zero events) plus its 1:1 Decision card.
    /// Routed-to, requester, and amount come from the row; the reason is
    /// factual only. Idempotent: re-runs skip existing shells/cards without
    /// rewriting them. Returns the projected case ids in row order.
    /// Scenario-reset wiring (UI lane): await
    /// runner.SeedOpeningDecisionsAsync(manifest.Scenario, ct);</summary>
    public async Task<IReadOnlyList<string>> SeedOpeningDecisionsAsync(
        string scenarioName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scenarioName))
        {
            throw new ArgumentException("Scenario name is required for opening shells.", nameof(scenarioName));
        }

        try
        {
            // Deterministic row order (plus doc-identity dedupe) so the same
            // ERP state always seeds the same shells in the same order.
            var pending = (await _erp.GetPendingApprovalsAsync(ct).ConfigureAwait(false))
                .OrderBy(r => r.DocumentType, StringComparer.Ordinal)
                .ThenBy(r => r.DocumentNumber, StringComparer.Ordinal)
                .DistinctBy(r => (r.DocumentType, r.DocumentNumber))
                .ToList();

            var caseIds = new List<string>(pending.Count);
            var created = 0;
            foreach (var row in pending)
            {
                RequireRowIdentity(row);
                var caseId = CaseStore.MintCaseId(scenarioName, row.DocumentType, row.DocumentNumber);
                if (SeedOpeningRow(scenarioName, caseId, row))
                {
                    created++;
                }

                caseIds.Add(caseId);
            }

            _logger.LogInformation(
                "Opening-decision seed for {Scenario}: {Total} pending, {Created} new shells.",
                scenarioName, pending.Count, created);
            return caseIds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening-decision seed failed for scenario {Scenario}.", scenarioName);
            throw;
        }
    }

    private static void RequireRowIdentity(PendingApprovalDto row)
    {
        if (string.IsNullOrWhiteSpace(row.DocumentType) || string.IsNullOrWhiteSpace(row.DocumentNumber))
        {
            throw new InvalidOperationException(
                "Pending approval rows need a document type and number; cannot seed an opening shell without identity.");
        }
    }

    /// <summary>Seeds one shell plus its card. Returns false when the shell
    /// already carries the card (any status), leaving the file untouched.</summary>
    private bool SeedOpeningRow(string scenarioName, string caseId, PendingApprovalDto row)
    {
        var existing = CaseStore.Load(_cacheDir, caseId);
        if (existing is not null)
        {
            if (!existing.Case.IsOpeningData)
            {
                throw new InvalidOperationException(
                    $"Case {caseId} is a live case, not an opening shell; refusing to seed over it.");
            }

            if (existing.Decisions.Any(d =>
                string.Equals(d.DocumentType, row.DocumentType, StringComparison.Ordinal)
                && string.Equals(d.DocumentNumber, row.DocumentNumber, StringComparison.Ordinal)))
            {
                return false;
            }

            // Partial shell (card missing): heal by appending the card only;
            // events stay untouched so no agent entry can ever appear here.
            var healed = existing.Decisions.ToList();
            healed.Add(BuildOpeningCard(caseId, healed.Count, row));
            CaseStore.Save(_cacheDir, existing with { Decisions = healed });
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var shell = new Case(
            caseId, SoNumber: string.Empty, ItemId: string.Empty, scenarioName,
            CaseStage.Created, now, now,
            PrNumber: row.DocumentType == "PR" ? row.DocumentNumber : null,
            PoNumber: row.DocumentType == "PO" ? row.DocumentNumber : null,
            AsnNumber: null, ReceiptNumber: null,
            Invoices: OpeningInvoiceKeys(row),
            PaymentNumbers: row.DocumentType == "Payment" ? [row.DocumentNumber] : [],
            BlockedByRuleId: null, BlockedReason: null, IsOpeningData: true);
        CaseStore.Save(_cacheDir, new CaseFile(shell, [], [BuildOpeningCard(caseId, 0, row)]));
        return true;
    }

    /// <summary>Invoice link when the row carries the vendor/number composite
    /// the UI splits on; otherwise no link (the card still carries the full
    /// document ref, so nothing is lost).</summary>
    private static IReadOnlyList<InvoiceKey> OpeningInvoiceKeys(PendingApprovalDto row)
    {
        if (row.DocumentType != "Invoice")
        {
            return [];
        }

        var parts = row.DocumentNumber.Split('/', 2);
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0
            ? [new InvoiceKey(parts[0], parts[1])]
            : [];
    }

    private static Decision BuildOpeningCard(string caseId, int existingCount, PendingApprovalDto row) =>
        new(
            $"{caseId}-d{existingCount + 1:000}", caseId, OpeningKind(row.DocumentType),
            $"Approve {row.DocumentType} {row.DocumentNumber}",
            DecisionStatus.Pending, row.RoutedTo, DateTimeOffset.UtcNow,
            null, null, row.DocumentType, row.DocumentNumber,
            row.DocumentAmount, Currency: null, RuleId: null,
            $"Seeded pending approval for {row.DocumentType} {row.DocumentNumber} submitted by {row.SubmittedBy}.",
            [], Comment: null);

    private static DecisionKind OpeningKind(string documentType) =>
        documentType switch
        {
            "PR" => DecisionKind.PrApproval,
            "PO" => DecisionKind.PoApproval,
            "Invoice" => DecisionKind.InvoiceApproval,
            "Payment" => DecisionKind.PaymentApproval,
            _ => DecisionKind.ExceptionApproval,
        };
}

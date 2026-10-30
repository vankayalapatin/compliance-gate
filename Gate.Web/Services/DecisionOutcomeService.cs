// Completes human decisions from the Decisions surface. ERP-backed
// outcomes go through the adapter with fresh idempotency keys; agent-side
// outcomes update the case file only. The case file is re-read immediately
// before applying, and every outcome appends a CaseEvent. Failures return
// a result carrying the full context; nothing is thrown away or omitted.
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Web.Services;

public sealed record DecisionOutcome(
    bool Ok,
    string Message,
    IReadOnlyList<string> Warnings);

public sealed class DecisionOutcomeService
{
    private readonly IErpAdapter _erp;
    private readonly CaseCacheStore _cases;
    private readonly ILogger<DecisionOutcomeService> _log;
    private readonly CaseRunner? _runner;

    public DecisionOutcomeService(
        IErpAdapter erp, CaseCacheStore cases, ILogger<DecisionOutcomeService> log, CaseRunner? runner = null)
    {
        _erp = erp;
        _cases = cases;
        _log = log;
        _runner = runner;
    }

    public async Task<DecisionOutcome> ApplyAsync(
        string caseId, string decisionId, bool approved, string? comment, string actedBy,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actedBy))
        {
            return new DecisionOutcome(false, "Acted-by user is required; pick the approver first.", []);
        }

        var file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return new DecisionOutcome(false, $"Case {caseId} has no case file.", []);
        }

        var decision = file.Decisions.FirstOrDefault(d =>
            string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal));
        if (decision is null)
        {
            return new DecisionOutcome(false, $"Decision {decisionId} not found in case {caseId}.", []);
        }
        if (decision.Status != DecisionStatus.Pending)
        {
            return new DecisionOutcome(false,
                $"Decision {decisionId} is already {decision.Status} (by {decision.DecidedBy ?? "unknown"}).", []);
        }

        var warnings = new List<string>();
        string erpNote;
        try
        {
            erpNote = await ApplyErpOutcomeAsync(file.Case, decision, approved, comment, actedBy.Trim(), warnings, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ERP outcome failed for decision {DecisionId}.", decisionId);
            return new DecisionOutcome(false,
                $"ERP outcome failed for {decision.Kind} {decisionId}: {ex.Message}", warnings);
        }

        var now = DateTimeOffset.UtcNow;
        var updated = decision with
        {
            Status = approved ? DecisionStatus.Approved : DecisionStatus.Rejected,
            DecidedBy = actedBy.Trim(),
            DecidedAt = now,
            Comment = string.IsNullOrWhiteSpace(comment) ? decision.Comment : comment.Trim(),
        };
        var decisions = file.Decisions
            .Select(d => string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal) ? updated : d)
            .ToList();
        var events = file.Events.ToList();
        events.Add(new CaseEvent(
            Guid.NewGuid().ToString("N"), caseId, now, actedBy.Trim(),
            EventKindFor(decision.Kind, approved),
            $"{decision.Kind} {decisionId} {(approved ? "approved" : "rejected")}: {erpNote}",
            decision.RuleId, decision.DocumentType, decision.DocumentNumber,
            null, decision.Amount, decision.Currency));

        try
        {
            await _cases.WriteAsync(file with { Decisions = decisions, Events = events }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Case file write failed for decision {DecisionId} after ERP outcome.", decisionId);
            return new DecisionOutcome(false,
                $"ERP outcome applied ({erpNote}) but the case file write failed: {ex.Message}. " +
                "Re-open Decisions and retry; the ERP call used a fresh idempotency key.", warnings);
        }

        if (_runner is not null)
        {
            try
            {
                var advanced = await _runner.AdvanceAsync(caseId, ct).ConfigureAwait(false);
                warnings.Add($"Case advanced to {advanced.Stage} ({advanced.Step}).");
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Advance after decision {DecisionId} failed; the outcome stands.", decisionId);
                warnings.Add($"Decision recorded but the case did not advance: {ex.Message}");
            }
        }

        return new DecisionOutcome(true, erpNote, warnings);
    }

    private async Task<string> ApplyErpOutcomeAsync(
        Case kase, Decision decision, bool approved, string? comment, string actedBy,
        List<string> warnings, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("N");
        switch (decision.Kind)
        {
            case DecisionKind.PrApproval:
            case DecisionKind.PoApproval:
            case DecisionKind.InvoiceApproval:
            case DecisionKind.PaymentApproval:
            {
                if (string.IsNullOrWhiteSpace(decision.DocumentType)
                    || string.IsNullOrWhiteSpace(decision.DocumentNumber))
                {
                    throw new InvalidOperationException(
                        $"Decision {decision.DecisionId} carries no document reference; cannot record approval.");
                }

                var recorded = await _erp.RecordApprovalAsync(
                    decision.DocumentType, decision.DocumentNumber, actedBy,
                    approved, comment, key, ct).ConfigureAwait(false);
                return $"{decision.DocumentType} {decision.DocumentNumber} " +
                    $"{(approved ? "approved" : "rejected")} by {recorded.ActedBy ?? actedBy} " +
                    $"(amount {recorded.DocumentAmount}, limit {recorded.LimitChecked}).";
            }
            case DecisionKind.BankVerification:
            {
                // Convention (pending lead confirmation): the verified vendor
                // travels in DocumentNumber; DocumentType "Vendor"/"VendorBank".
                if (string.IsNullOrWhiteSpace(decision.DocumentNumber))
                {
                    throw new InvalidOperationException(
                        $"Bank verification {decision.DecisionId} names no vendor; cannot verify.");
                }

                if (!approved)
                {
                    return $"Bank change for vendor {decision.DocumentNumber} rejected; master left unverified.";
                }

                var bank = await _erp.VerifyVendorBankAsync(decision.DocumentNumber, actedBy, key, ct)
                    .ConfigureAwait(false);
                return $"Vendor {bank.VendorId} bank {bank.AccountRef} verified.";
            }
            case DecisionKind.DockReview:
            case DecisionKind.DamageReview:
            case DecisionKind.ExceptionApproval:
            case DecisionKind.ApReview:
            default:
            {
                if (!approved)
                {
                    return $"{decision.Kind} {decision.DecisionId} rejected; holds left in place.";
                }

                var released = await ReleaseMatchingHoldsAsync(kase, decision, actedBy, ct).ConfigureAwait(false);
                if (released.Count == 0)
                {
                    warnings.Add("No open hold matched this review; recorded agent-side only.");
                    return $"{decision.Kind} {decision.DecisionId} closed; no open hold matched.";
                }

                return $"{decision.Kind} {decision.DecisionId} closed; released hold(s) {string.Join(", ", released)}.";
            }
        }
    }

    private async Task<IReadOnlyList<string>> ReleaseMatchingHoldsAsync(
        Case kase, Decision decision, string actedBy, CancellationToken ct)
    {
        var open = await _erp.GetOpenHoldsAsync(decision.DocumentNumber, ct).ConfigureAwait(false);
        var matching = open
            .Where(h => string.Equals(h.CaseId, kase.CaseId, StringComparison.Ordinal))
            .Where(h => decision.RuleId is null
                || string.Equals(h.RuleId, decision.RuleId, StringComparison.Ordinal))
            .ToList();
        var released = new List<string>();
        foreach (var hold in matching)
        {
            await _erp.ReleaseHoldAsync(
                hold.HoldId, actedBy, decision.DecisionId, Guid.NewGuid().ToString("N"), ct)
                .ConfigureAwait(false);
            released.Add(hold.HoldId);
        }

        return released;
    }

    private static CaseEventKind EventKindFor(DecisionKind kind, bool approved) => kind switch
    {
        DecisionKind.PrApproval => approved ? CaseEventKind.PrApproved : CaseEventKind.PrRejected,
        DecisionKind.PoApproval => approved ? CaseEventKind.PoApproved : CaseEventKind.Note,
        DecisionKind.DockReview or DecisionKind.DamageReview
            or DecisionKind.ExceptionApproval or DecisionKind.ApReview
            => CaseEventKind.ReviewClosed,
        _ => CaseEventKind.Note,
    };
}

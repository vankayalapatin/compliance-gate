// Platform-side ERP policy inputs. The ERP cannot read the platform db, so
// the two inputs that live on case decisions arrive through here: logged
// rule exceptions granted for an award, and open dock/damage reviews behind
// the invoices a payment would settle. Reads case files only; never ERP.
using Gate.Contracts;

namespace Gate.Agent;

public sealed class CasePolicyContext(string cacheDir) : IErpPolicyContext
{
    public Task<PolicyExceptionGrant?> FindExceptionGrantAsync(
        string ruleId, string vendorId, string? prNumber, CancellationToken ct = default)
    {
        PolicyExceptionGrant? best = null;
        foreach (var caseId in CaseStore.ListCaseIds(cacheDir))
        {
            ct.ThrowIfCancellationRequested();
            var file = CaseStore.Load(cacheDir, caseId);
            if (file is null)
            {
                continue;
            }

            foreach (var decision in file.Decisions
                .Where(d => d.Kind == DecisionKind.ExceptionApproval
                    && d.Status == DecisionStatus.Approved
                    && string.Equals(d.RuleId, ruleId, StringComparison.Ordinal)
                    && string.Equals(d.DocumentNumber, vendorId, StringComparison.Ordinal))
                .OrderByDescending(d => d.DecidedAt ?? DateTimeOffset.MinValue))
            {
                if (prNumber is not null
                    && !decision.EvidenceRefs.Any(e =>
                        string.Equals(e, $"pr:{prNumber}", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (decision.DecidedBy is null || decision.DecidedAt is null || decision.Comment is null)
                {
                    continue;
                }

                var grant = new PolicyExceptionGrant(
                    ruleId, vendorId, decision.DecidedBy, decision.DecidedAt.Value, decision.Comment);
                if (best is null || grant.At > best.At)
                {
                    best = grant;
                }
            }
        }

        return Task.FromResult(best);
    }

    public Task<IReadOnlyList<OpenReview>> FindOpenReviewsAsync(
        IReadOnlyList<InvoiceKey> invoices, CancellationToken ct = default)
    {
        var keys = invoices
            .Select(k => $"{k.VendorId}/{k.SupplierInvoiceNumber}")
            .ToHashSet(StringComparer.Ordinal);
        var reviews = new List<OpenReview>();
        foreach (var caseId in CaseStore.ListCaseIds(cacheDir))
        {
            ct.ThrowIfCancellationRequested();
            var file = CaseStore.Load(cacheDir, caseId);
            if (file is null)
            {
                continue;
            }

            var touched = file.Case.Invoices
                .Any(k => keys.Contains($"{k.VendorId}/{k.SupplierInvoiceNumber}"));
            if (!touched)
            {
                continue;
            }

            reviews.AddRange(file.Decisions
                .Where(d => d.Status == DecisionStatus.Pending
                    && d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview)
                .Select(d => new OpenReview(
                    d.DecisionId,
                    d.RuleId ?? d.Kind.ToString(),
                    file.Case.CaseId,
                    CartonFromEvidence(d),
                    null,
                    d.RequestedAt,
                    d.Reason ?? d.Title)));
        }

        return Task.FromResult<IReadOnlyList<OpenReview>>(reviews);
    }

    private static string? CartonFromEvidence(Decision decision) =>
        decision.EvidenceRefs
            .Where(r => r.StartsWith("carton:", StringComparison.Ordinal))
            .Select(r => r["carton:".Length..])
            .FirstOrDefault();
}

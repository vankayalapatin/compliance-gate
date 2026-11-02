// Ask-the-agent-why: answers a human question over one decision's
// evidence through the injected model client (via P2pAgent.ExplainAsync).
// The evidence is built in C# from the case file, the ERP consequence
// preview, and the ERP document chain, then passed to Spark; the style
// prompt asks for 2-4 plain sentences for business users. In replay mode
// the cached answer must already exist on disk; a miss surfaces as an
// error instead of placing a live call, so replay makes zero live calls
// by construction.
using System.Text;
using Gate.Agent;
using Gate.Contracts;

namespace Gate.Web.Services;

public sealed record WhyAnswer(bool Ok, string Text);

public sealed class AskWhyService
{
    private readonly P2pAgent _agent;
    private readonly IModelClient _models;
    private readonly IErpAdapter _erp;
    private readonly CaseCacheStore _cases;
    private readonly DockPhotoService _photos;
    private readonly ReplayState _replay;
    private readonly ILogger<AskWhyService> _log;
    private readonly string _modelCacheDir;

    public AskWhyService(
        P2pAgent agent,
        IModelClient models,
        IErpAdapter erp,
        CaseCacheStore cases,
        DockPhotoService photos,
        ReplayState replay,
        ILogger<AskWhyService> log,
        string modelCacheDir)
    {
        _agent = agent;
        _models = models;
        _erp = erp;
        _cases = cases;
        _photos = photos;
        _replay = replay;
        _log = log;
        _modelCacheDir = modelCacheDir;
    }

    public async Task<WhyAnswer> AskAsync(
        string caseId, string decisionId, string question, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new WhyAnswer(false, "Type a question first.");
        }

        var file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return new WhyAnswer(false, $"Case {caseId} was not found.");
        }

        var decision = file.Decisions.FirstOrDefault(d =>
            string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal));
        if (decision is null)
        {
            return new WhyAnswer(false, $"Decision {decisionId} not found in case {caseId}.");
        }

        var evidence = await BuildEvidenceAsync(file.Case, decision, ct).ConfigureAwait(false);
        var trimmed = question.Trim();
        if (_replay.IsReplay && !HasCachedAnswer(trimmed, evidence))
        {
            return new WhyAnswer(false,
                "Offline answers are on and no cached answer exists for this exact question. " +
                "Rephrase to a previously answered question or switch back to live answers.");
        }

        try
        {
            var text = await _agent.ExplainAsync(trimmed, evidence, ct).ConfigureAwait(false);
            return new WhyAnswer(true, text);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Ask-why failed for decision {DecisionId}.", decisionId);
            return new WhyAnswer(false, $"The agent call failed: {ex.Message}");
        }
    }

    private async Task<string> BuildEvidenceAsync(Case kase, Decision decision, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("Case ").Append(kase.CaseId)
            .Append(" (SO ").Append(kase.SoNumber)
            .Append(", item ").Append(kase.ItemId)
            .Append(", stage ").Append(kase.Stage).Append("). ");
        sb.Append("Decision ").Append(decision.DecisionId)
            .Append(": ").Append(decision.Kind)
            .Append(" '").Append(decision.Title).Append("', status ").Append(decision.Status)
            .Append(", requested to ").Append(decision.RequestedTo).Append(". ");
        if (decision.RuleId is not null) sb.Append("Rule ").Append(decision.RuleId).Append(". ");
        if (decision.Reason is not null) sb.Append("Reason: ").Append(decision.Reason).Append(' ');
        if (decision.Amount is not null)
        {
            sb.Append("Amount ").Append(decision.Amount).Append(' ').Append(decision.Currency ?? "?").Append(". ");
        }
        if (decision.DocumentType is not null)
        {
            sb.Append("Document ").Append(decision.DocumentType).Append(' ').Append(decision.DocumentNumber).Append(". ");
        }

        sb.Append(await DescribeChainAsync(kase, ct).ConfigureAwait(false));
        sb.Append(await DescribePreviewAsync(decision, ct).ConfigureAwait(false));

        foreach (var evidence in decision.EvidenceRefs)
        {
            var image = _photos.ResolveImageFileName(evidence);
            if (image is null)
            {
                sb.Append("Evidence: ").Append(evidence).Append(". ");
                continue;
            }

            sb.Append("Photo ").Append(image).Append(". ");
            try
            {
                var read = _photos.TryCachedRead(image);
                if (read is not null) sb.Append("Cached read: ").Append(read).Append(". ");
            }
            catch (Exception ex)
            {
                sb.Append("Cached read refused: ").Append(ex.Message).Append(". ");
            }
        }

        return sb.ToString();
    }

    /// <summary>SO-to-payment document chain for the case, read live from
    /// the ERP: each linked document plus its status. Failures land in the
    /// evidence as gaps; nothing is silently omitted.</summary>
    private async Task<string> DescribeChainAsync(Case kase, CancellationToken ct)
    {
        var parts = new List<string>();
        await AddChainPartAsync(parts, "sales order", kase.SoNumber,
            async () => (await _erp.GetSalesOrderAsync(kase.SoNumber, ct).ConfigureAwait(false))?.Status.ToString(),
            ct).ConfigureAwait(false);
        if (kase.PrNumber is not null)
        {
            var pr = kase.PrNumber;
            await AddChainPartAsync(parts, "purchase requisition", pr,
                async () => (await _erp.GetPrAsync(pr, ct).ConfigureAwait(false))?.Status.ToString(),
                ct).ConfigureAwait(false);
        }

        if (kase.PoNumber is not null)
        {
            var po = kase.PoNumber;
            await AddChainPartAsync(parts, "purchase order", po,
                async () =>
                {
                    var order = await _erp.GetPurchaseOrderAsync(po, ct).ConfigureAwait(false);
                    return order is null ? null : $"{order.ApprovalStatus}/{order.DocStatus}";
                }, ct).ConfigureAwait(false);
        }

        if (kase.ReceiptNumber is not null)
        {
            var receipt = kase.ReceiptNumber;
            await AddChainPartAsync(parts, "receipt", receipt,
                async () => (await _erp.GetReceiptAsync(receipt, ct).ConfigureAwait(false))?.Status.ToString(),
                ct).ConfigureAwait(false);
        }

        foreach (var key in kase.Invoices)
        {
            await AddChainPartAsync(parts, "invoice", $"{key.VendorId}/{key.SupplierInvoiceNumber}",
                async () => (await _erp.GetInvoiceAsync(key.VendorId, key.SupplierInvoiceNumber, ct).ConfigureAwait(false))?.Status.ToString(),
                ct).ConfigureAwait(false);
        }

        foreach (var payment in kase.PaymentNumbers)
        {
            await AddChainPartAsync(parts, "payment", payment,
                async () => (await _erp.GetPaymentAsync(payment, ct).ConfigureAwait(false))?.Status.ToString(),
                ct).ConfigureAwait(false);
        }

        return parts.Count == 0
            ? "Document chain: no linked documents yet. "
            : $"Document chain: {string.Join("; ", parts)}. ";
    }

    private static async Task AddChainPartAsync(
        List<string> parts, string label, string number,
        Func<Task<string?>> readStatus, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string status;
        try
        {
            status = await readStatus().ConfigureAwait(false) ?? "not found in ERP";
        }
        catch (Exception ex)
        {
            status = $"read failed ({ex.Message})";
        }

        parts.Add($"{label} {number} ({status})");
    }

    /// <summary>ERP consequence preview for the decision's document: what
    /// approval would post, the due date, the payment run, the next
    /// approver, and what stays held. Missing previews land as gaps.</summary>
    private async Task<string> DescribePreviewAsync(Decision decision, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(decision.DocumentType)
            || string.IsNullOrWhiteSpace(decision.DocumentNumber))
        {
            return "Consequence preview: this decision links no ERP document. ";
        }

        ConsequencePreview? preview;
        try
        {
            preview = await _erp.GetConsequencePreviewAsync(
                decision.DocumentType, decision.DocumentNumber, ct).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return "Consequence preview: not implemented by the ERP layer yet. ";
        }
        catch (Exception ex)
        {
            return $"Consequence preview: read failed ({ex.Message}). ";
        }

        if (preview is null)
        {
            return "Consequence preview: the ERP returned none for this document. ";
        }

        var sb = new StringBuilder("Consequence preview: ");
        sb.Append(preview.Postings.Count == 0
            ? "approval posts nothing. "
            : $"approval would post {string.Join("; ", preview.Postings)}. ");
        if (preview.DueDate is not null)
        {
            sb.Append("Due ").Append(preview.DueDate).Append(". ");
        }

        if (preview.PaymentRun is not null)
        {
            sb.Append("Joins payment run ").Append(preview.PaymentRun).Append(". ");
        }

        if (preview.NextApprover is not null)
        {
            sb.Append("Next approver ").Append(preview.NextApprover).Append(". ");
        }

        if (preview.HeldValue > 0)
        {
            sb.Append("Stays held: ").Append(preview.HeldValue);
            if (preview.Currency is not null)
            {
                sb.Append(' ').Append(preview.Currency);
            }

            if (preview.HeldReason is not null)
            {
                sb.Append(" (").Append(preview.HeldReason).Append(')');
            }

            sb.Append(". ");
        }

        return sb.ToString();
    }

    /// <summary>True when the model cache already holds this exact call.
    /// Mirrors the request P2pAgent.ExplainAsync builds; if that shape
    /// changes, this guard must change with it.</summary>
    private bool HasCachedAnswer(string question, string evidence)
    {
        var request = new ModelRequest(question,
            "Using only this evidence, answer in 2 to 4 plain sentences for a business user. " +
            "Use no markdown formatting, no internal ids, and do not mention model spend. " +
            $"Evidence: {evidence}");
        var path = Path.Combine(_modelCacheDir, CachedModelClient.CacheKey(_models.ModelName, request) + ".json");
        return File.Exists(path);
    }
}

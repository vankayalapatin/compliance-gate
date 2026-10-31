// Full P2P case runner: order webhook to payment with dock holds, AP
// catches, a cited pre-pay gate, partial pay, and the reshot remainder loop.
// Reads raw ERP data through the adapter only; explosion, shortfall netting,
// screening, and matching run in the composed P2pAgent core. Every write the
// runner performs sets Source to p2p-agent. Every step is picked from case
// state plus fresh ERP reads plus pending decisions (see CaseSteps.cs); the
// file is re-read before each pick. Failures are logged with case context and
// recorded as Note events, then rethrown, never swallowed.
using Gate.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gate.Agent;

public sealed record CaseAdvanceOutcome(
    string CaseId,
    CaseStage Stage,
    CaseNextStep Step,
    bool Complete,
    string Summary);

public partial class CaseRunner
{
    private readonly IErpAdapter _erp;
    private readonly P2pAgent _core;
    private readonly IModelClient _models;
    private readonly IComplianceEngine _compliance;
    private readonly IDockEvaluator _dock;
    private readonly ILogger<CaseRunner> _logger;
    private readonly string _cacheDir;
    private readonly string _visionReadCacheDir;

    public CaseRunner(
        IErpAdapter erp,
        P2pAgent core,
        IModelClient models,
        IComplianceEngine compliance,
        IDockEvaluator dock,
        string cacheDir,
        ILogger<CaseRunner>? logger = null,
        string? visionReadCacheDir = null)
    {
        _erp = erp;
        _core = core;
        _models = models;
        _compliance = compliance;
        _dock = dock;
        _cacheDir = cacheDir;
        _logger = logger ?? NullLogger<CaseRunner>.Instance;
        _visionReadCacheDir = visionReadCacheDir ?? Path.Combine(cacheDir, "vision-reads");
    }

    /// <summary>Advances the case through the Spark-driven tool loop until it
    /// parks in a waiting step (human, supplier, camera, vendor reply, or due
    /// date) or finishes. External inputs arrive via the simulation handlers
    /// between advances. payUpToDate caps the payment run date (null means no
    /// limit); every turn is appended to the case transcript.</summary>
    public async Task<CaseAdvanceOutcome> AdvanceAsync(
        string caseId, CancellationToken ct = default, DateOnly? payUpToDate = null) =>
        await AdvanceWithToolsAsync(caseId, payUpToDate, ct).ConfigureAwait(false);

    private void RecordFailureNote(string caseId, CaseNextStep step, Exception ex) =>
        RecordFailureNote(caseId, step.ToString(), ex);

    private void RecordFailureNote(string caseId, string action, Exception ex)
    {
        try
        {
            var file = CaseStore.Load(_cacheDir, caseId);
            if (file is null)
            {
                return;
            }

            var events = file.Events.ToList();
            events.Add(new CaseEvent(
                $"{caseId}-e{events.Count + 1:000}", caseId, DateTimeOffset.UtcNow,
                RecordSource.P2pAgent, CaseEventKind.Note,
                $"Step {action} failed: {ex.Message}", null, null, null, null, null, null));
            CaseStore.Save(_cacheDir, file with { Events = events });
        }
        catch (Exception noteEx)
        {
            _logger.LogError(noteEx, "Case {CaseId}: could not record the failure note.", caseId);
        }
    }

    private async Task ExecuteStepAsync(
        CaseFile file, CaseErpView view, CaseNextStep step, DateOnly? payUpToDate, CancellationToken ct)
    {
        switch (step)
        {
            case CaseNextStep.ComputeShortfall:
                await ComputeShortfallStepAsync(file, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.RaisePr:
                await RaisePrStepAsync(file, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.ScreenVendors:
                await ScreenVendorsStepAsync(file, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.SubmitPoApproval:
                await SubmitPoApprovalStepAsync(file, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.EvaluatePrePay:
                await EvaluatePrePayStepAsync(file, view, payUpToDate, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.NotifyCustomer:
                await NotifyCustomerStepAsync(file, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.FinishCase:
                await FinishCaseStepAsync(file, view, ct).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Step {step} is not runnable.");
        }
    }

    private async Task<CaseErpView> BuildViewAsync(CaseFile file, CancellationToken ct)
    {
        var current = file.Case;
        var so = await _erp.GetSalesOrderAsync(current.SoNumber, ct).ConfigureAwait(false);
        PrStatus? prStatus = null;
        if (current.PrNumber is not null)
        {
            prStatus = (await _erp.GetPrAsync(current.PrNumber, ct).ConfigureAwait(false))?.Status;
        }

        PoApprovalStatus? poApproval = null;
        if (current.PoNumber is not null)
        {
            poApproval = (await _erp.GetPurchaseOrderAsync(current.PoNumber, ct).ConfigureAwait(false))?.ApprovalStatus;
        }

        var confirmation = current.PoNumber is null
            ? null
            : await _erp.GetVendorConfirmationAsync(current.PoNumber, ct).ConfigureAwait(false);
        var asn = current.AsnNumber is null
            ? null
            : await _erp.GetAsnAsync(current.AsnNumber, ct).ConfigureAwait(false);
        var receipt = current.ReceiptNumber is null
            ? null
            : await _erp.GetReceiptAsync(current.ReceiptNumber, ct).ConfigureAwait(false);

        var matched = 0;
        decimal accepted = 0;
        decimal paid = 0;
        var held = false;
        foreach (var key in current.Invoices)
        {
            var invoice = await _erp.GetInvoiceAsync(key.VendorId, key.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            if (invoice is null)
            {
                continue;
            }

            if (invoice.MatchResults.Count > 0)
            {
                matched++;
            }

            accepted += invoice.Lines
                .Where(l => l.Disposition == LineDisposition.Accepted)
                .Sum(l => l.Quantity * l.UnitPrice);
            if (invoice.Lines.Any(l => l.Disposition == LineDisposition.Held && l.Quantity > 0))
            {
                held = true;
            }

            var settlements = await _erp.GetSettlementsAsync(key.VendorId, key.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            paid += settlements.Sum(s => s.SettledAmount);
        }

        var holds = await _erp.GetOpenHoldsAsync(null, ct).ConfigureAwait(false);
        var openCaseHolds = holds.Count(h => string.Equals(h.CaseId, current.CaseId, StringComparison.Ordinal));
        var notified = file.Events.Any(e => e.Kind == CaseEventKind.CustomerNotified);
        return new CaseErpView(
            so is not null, prStatus, poApproval, confirmation is not null,
            asn is not null, receipt is not null, matched, accepted, paid,
            held, openCaseHolds, notified);
    }

    private async Task ComputeShortfallStepAsync(CaseFile file, CancellationToken ct)
    {
        var current = file.Case;
        var shortfalls = await _core.ComputeShortfallAsync(current.SoNumber, ct).ConfigureAwait(false);
        var row = shortfalls.FirstOrDefault(s =>
            string.Equals(s.ItemId, current.ItemId, StringComparison.Ordinal));
        var work = new CaseWork(file);
        if (row is null || row.Shortfall <= 0)
        {
            work.Close($"No shortfall for {current.ItemId} on {current.SoNumber}; no PR.");
        }
        else
        {
            work.AddEvent(CaseEventKind.ShortfallComputed,
                $"{row.ItemId}: required {row.Required}, covered {row.Covered}, shortfall {row.Shortfall}.");
            work.Stage(CaseStage.ShortfallComputed);
        }

        work.Save(_cacheDir);
    }

    private async Task RaisePrStepAsync(CaseFile file, CancellationToken ct)
    {
        var current = file.Case;
        var requester = ResolveRequester(file)
            ?? await DefaultRequesterAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No requester for case {current.CaseId}: the webhook carried none and the ERP has no requester-role user.");
        var shortfalls = await _core.ComputeShortfallAsync(current.SoNumber, ct).ConfigureAwait(false);
        var row = shortfalls.First(s => string.Equals(s.ItemId, current.ItemId, StringComparison.Ordinal));
        var unit = (await _erp.GetItemAsync(current.ItemId, ct).ConfigureAwait(false))?.Unit ?? "EA";
        var work = new CaseWork(file);

        PurchaseRequisition pr;
        if (current.PrNumber is not null
            && await _erp.GetPrAsync(current.PrNumber, ct).ConfigureAwait(false) is { } existing)
        {
            pr = existing;
        }
        else
        {
            pr = await _erp.CreatePrAsync(
                requester, RecordSource.P2pAgent, requester,
                [new PurchaseRequisitionLine(current.ItemId, row.Shortfall, unit)],
                $"{current.CaseId}:pr", ct).ConfigureAwait(false);
            work.AddEvent(CaseEventKind.PrCreated,
                $"PR {pr.PrNumber} for {row.Shortfall} {unit} of {current.ItemId}, estimated {pr.EstimatedAmount} {pr.Currency}.",
                documentType: "PR", documentNumber: pr.PrNumber);
        }

        work.Patch(current with { PrNumber = pr.PrNumber });
        var existingApproval = file.Decisions.FirstOrDefault(d =>
            d.Kind == DecisionKind.PrApproval
            && string.Equals(d.DocumentNumber, pr.PrNumber, StringComparison.Ordinal));
        if (existingApproval is not null)
        {
            work.Stage(pr.Status == PrStatus.Approved ? CaseStage.PrApproved : CaseStage.PrSubmitted);
            work.Save(_cacheDir);
            return;
        }

        if (pr.Status == PrStatus.Approved)
        {
            work.AddEvent(CaseEventKind.PrApproved,
                $"PR {pr.PrNumber} auto-approved under threshold at {pr.EstimatedAmount} {pr.Currency}.",
                documentType: "PR", documentNumber: pr.PrNumber);
            work.Stage(CaseStage.PrApproved);
            work.Save(_cacheDir);
            return;
        }

        var routedTo = await _core.RoutePrAsync(pr.PrNumber, ct).ConfigureAwait(false);
        if (routedTo is null)
        {
            throw new InvalidOperationException($"PR {pr.PrNumber} needs no routing but is not approved; cannot proceed.");
        }

        await _erp.SubmitForApprovalAsync("PR", pr.PrNumber, routedTo, requester, $"{current.CaseId}:pr-submit", ct)
            .ConfigureAwait(false);
        work.AddEvent(CaseEventKind.PrSubmitted,
            $"PR {pr.PrNumber} submitted to {routedTo}.",
            documentType: "PR", documentNumber: pr.PrNumber);
        work.FindOrCreateDecision(
            DecisionKind.PrApproval, $"Approve PR {pr.PrNumber}", routedTo,
            "PR", pr.PrNumber, pr.EstimatedAmount, pr.Currency,
            $"PR {pr.PrNumber} needs a human above the requester limit.");
        work.Stage(CaseStage.PrSubmitted);
        work.Save(_cacheDir);
    }

    private async Task ScreenVendorsStepAsync(CaseFile file, CancellationToken ct)
    {
        var current = file.Case;
        if (current.PrNumber is null)
        {
            throw new InvalidOperationException($"Case {current.CaseId} has no PR to screen vendors for.");
        }

        var candidates = ReadCandidates(file, current.ItemId);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No candidate vendors recorded for {current.ItemId}: the order webhook must carry sourcing candidates per item.");
        }

        var (po, screenings) = await _core.ScreenAndOrderAsync(
            current.PrNumber, candidates, RecordSource.P2pAgent, current.CaseId, ct,
            idempotencyScope: $"{current.CaseId}:screen").ConfigureAwait(false);
        var rationale = await AwardRationaleAsync(current, po, screenings, ct).ConfigureAwait(false);
        var work = new CaseWork(file);
        foreach (var screening in screenings)
        {
            var block = screening.Verdicts.FirstOrDefault(v => v.Kind == VerdictKind.Block);
            work.AddEvent(CaseEventKind.VendorScreened,
                screening.Passed
                    ? $"Vendor {screening.VendorId} passed screening."
                    : $"Vendor {screening.VendorId} failed screening: {block?.RuleId}.",
                ruleId: block?.RuleId);
            if (!screening.Passed && block is not null)
            {
                work.AddEvent(CaseEventKind.VendorBlocked,
                    $"Vendor {screening.VendorId} blocked: {block.RuleId}: {block.Reason}",
                    ruleId: block.RuleId);
            }
        }

        work.AddEvent(CaseEventKind.PoCreated,
            $"PO {po.PoNumber} to {po.VendorId} for {po.Lines.Sum(l => l.LineAmount)} {po.Currency}. {rationale.Text}",
            documentType: "PO", documentNumber: po.PoNumber,
            evidenceRef: $"model-cache:{rationale.CacheKey}");
        work.Patch(current with { PoNumber = po.PoNumber });
        work.Stage(CaseStage.PoPlaced);
        if (po.ApprovalStatus == PoApprovalStatus.Draft)
        {
            await EnsurePoSubmittedAsync(work, po, ct).ConfigureAwait(false);
        }

        work.Save(_cacheDir);
    }

    private async Task SubmitPoApprovalStepAsync(CaseFile file, CancellationToken ct)
    {
        var current = file.Case;
        var po = current.PoNumber is null
            ? null
            : await _erp.GetPurchaseOrderAsync(current.PoNumber, ct).ConfigureAwait(false);
        if (po is null)
        {
            throw new InvalidOperationException($"Case {current.CaseId} has no PO to submit.");
        }

        var work = new CaseWork(file);
        await EnsurePoSubmittedAsync(work, po, ct).ConfigureAwait(false);
        work.Save(_cacheDir);
    }

    private async Task EnsurePoSubmittedAsync(CaseWork work, PurchaseOrder po, CancellationToken ct)
    {
        if (po.ApprovalStatus != PoApprovalStatus.Draft)
        {
            return;
        }

        var current = work.Current;
        var pr = await _erp.GetPrAsync(po.PrNumber, ct).ConfigureAwait(false);
        var amount = po.Lines.Sum(l => l.LineAmount);
        var routedTo = await RouteDocumentAsync(
            amount, pr?.Requester is string requester ? [requester] : [], null, ct).ConfigureAwait(false);
        await _erp.SubmitForApprovalAsync(
            "PO", po.PoNumber, routedTo, RecordSource.P2pAgent, $"{current.CaseId}:po-submit", ct).ConfigureAwait(false);
        work.AddEvent(CaseEventKind.Note,
            $"PO {po.PoNumber} submitted to {routedTo}.",
            documentType: "PO", documentNumber: po.PoNumber);
        work.FindOrCreateDecision(
            DecisionKind.PoApproval, $"Approve PO {po.PoNumber}", routedTo,
            "PO", po.PoNumber, amount, po.Currency,
            $"PO {po.PoNumber} needs a human approval.");
    }

    private async Task<(string Text, string CacheKey)> AwardRationaleAsync(
        Case current, PurchaseOrder po, IReadOnlyList<VendorScreening> screenings, CancellationToken ct)
    {
        var losers = screenings.Where(s => !s.Passed).Select(s => s.VendorId).ToList();
        var qty = po.Lines.Sum(l => l.QtyOrdered);
        var outcomes = screenings.Select(s =>
        {
            if (s.Passed)
            {
                return $"{s.VendorId}: passed screening.";
            }

            var block = s.Verdicts.FirstOrDefault(v => v.Kind == VerdictKind.Block);
            return block is null
                ? $"{s.VendorId}: blocked."
                : $"{s.VendorId}: blocked by {block.RuleId}: {block.Reason}";
        }).ToList();
        // Default token cap: reasoning models burn hundreds of tokens before
        // answering (see SparkOptions), so a small cap starves this call.
        var request = new ModelRequest(
            $"In one short sentence, why award {current.ItemId} x{qty} to {po.VendorId}"
            + (losers.Count == 0 ? "?" : $" over {string.Join(", ", losers)}?")
            + $" Screening outcomes: {string.Join(" ", outcomes)}"
            + " Ground your answer only in these screening outcomes; do not invent cost, quality, or delivery reasons.",
            "You write one-sentence sourcing rationales grounded only in the given screening outcomes.");
        var result = await ResilientCalls.CompleteWithRetryAsync(
            _models, request, _logger, $"award rationale for {current.CaseId}", ct).ConfigureAwait(false);
        return (result.Text.Trim(), CachedModelClient.CacheKey(_models.ModelName, request));
    }

    private async Task EvaluatePrePayStepAsync(
        CaseFile file, CaseErpView view, DateOnly? payUpToDate, CancellationToken ct)
    {
        var current = file.Case;
        var state = await BuildPrePayStateAsync(file, ct).ConfigureAwait(false);
        var verdicts = await _compliance.EvaluatePaymentAsync(state, ct).ConfigureAwait(false);
        var block = verdicts.FirstOrDefault(v => v.Kind == VerdictKind.Block);
        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.Note,
            $"Pre-pay gate over {state.OpenHolds.Count} hold(s), {state.QuarantinedLotRefs.Count} quarantined lot(s), " +
            $"{state.OpenReviews.Count} open review(s): " +
            string.Join("; ", verdicts.Select(v => $"{v.RuleId} {v.Kind}: {v.Reason}")),
            ruleId: block?.RuleId ?? verdicts.FirstOrDefault()?.RuleId,
            documentType: "Payment");

        var payable = new List<InvoiceKey>();
        foreach (var key in current.Invoices)
        {
            var invoice = await _erp.GetInvoiceAsync(key.VendorId, key.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            if (invoice is null)
            {
                continue;
            }

            var accepted = invoice.Lines
                .Where(l => l.Disposition == LineDisposition.Accepted)
                .Sum(l => l.Quantity * l.UnitPrice);
            if (accepted > 0)
            {
                payable.Add(key);
            }
        }

        if (payable.Count == 0)
        {
            work.AddEvent(CaseEventKind.Note,
                "No payable invoice value; everything is held. Waiting on a review or reshot.",
                documentType: "Payment");
            work.Patch(current with { BlockedByRuleId = block?.RuleId, BlockedReason = block?.Reason });
            work.Save(_cacheDir);
            return;
        }

        var round = file.Decisions.Count(d => d.Kind == DecisionKind.PaymentApproval) + 1;
        Payment? draft;
        try
        {
            (draft, _) = await _core.RequestPaymentAsync(
                payable, RecordSource.P2pAgent, ct,
                idempotencyScope: $"{current.CaseId}:pay{round}", payUpToDate: payUpToDate)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Case {CaseId}: ERP refused the payment draft.", current.CaseId);
            work.AddEvent(CaseEventKind.Note,
                $"{CasePicker.PaymentNotDueMarkerFor(payUpToDate)}: ERP refused the draft: {ex.Message}",
                documentType: "Payment");
            work.Save(_cacheDir);
            return;
        }

        if (draft is null)
        {
            work.AddEvent(CaseEventKind.Note,
                "No payable invoice value; everything is held. Waiting on a review or reshot.",
                documentType: "Payment");
            work.Save(_cacheDir);
            return;
        }

        var excluded = new List<string>();
        foreach (var key in payable)
        {
            var approvals = await _erp.GetApprovalsAsync(
                "Invoice", $"{key.VendorId}/{key.SupplierInvoiceNumber}", ct).ConfigureAwait(false);
            excluded.AddRange(approvals
                .Where(a => a.Approved && a.ActedBy is not null)
                .Select(a => a.ActedBy!));
        }

        var routedTo = await RouteDocumentAsync(
            draft.Lines.Sum(l => l.Amount), excluded, "payment-approver", ct).ConfigureAwait(false);
        await _erp.SubmitForApprovalAsync(
            "Payment", draft.PaymentNumber, routedTo, RecordSource.P2pAgent,
            $"{current.CaseId}:pay{round}-submit", ct).ConfigureAwait(false);
        var pending = file.Decisions.Where(d => d.Status == DecisionStatus.Pending).ToList();
        var partial = view.HasHeldLines || view.OpenHoldCount > 0
            || pending.Any(d => d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview);
        work.AddEvent(CaseEventKind.PaymentRequested,
            (partial ? "Partial payment" : "Payment") +
            $" {draft.PaymentNumber} drafted for {draft.Lines.Sum(l => l.Amount)} {draft.Currency} " +
            $"(accepted {view.AcceptedTotal}, paid {view.PaidTotal}); held value stays open.",
            ruleId: block?.RuleId, documentType: "Payment", documentNumber: draft.PaymentNumber,
            amount: draft.Lines.Sum(l => l.Amount), currency: draft.Currency);
        work.FindOrCreateDecision(
            DecisionKind.PaymentApproval, $"Approve payment {draft.PaymentNumber}", routedTo,
            "Payment", draft.PaymentNumber, draft.Lines.Sum(l => l.Amount), draft.Currency,
            partial
                ? $"Partial pay of accepted value; {block?.RuleId ?? "held lines"} hold the rest."
                : $"Payment of {draft.Lines.Sum(l => l.Amount)} {draft.Currency}.");
        work.Patch(current with { BlockedByRuleId = block?.RuleId, BlockedReason = block?.Reason });
        work.Stage(partial ? CaseStage.PaymentPartial : CaseStage.PaymentRequested);
        work.Save(_cacheDir);
    }

    private async Task NotifyCustomerStepAsync(CaseFile file, CancellationToken ct)
    {
        await RecordCustomerNotifiedAsync(file, ct).ConfigureAwait(false);
    }

    private async Task RecordCustomerNotifiedAsync(CaseFile file, CancellationToken ct)
    {
        _ = ct;
        if (file.Events.Any(e => e.Kind == CaseEventKind.CustomerNotified))
        {
            return;
        }

        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.CustomerNotified,
            $"Customer notified: materials secured for {file.Case.SoNumber} / {file.Case.ItemId}.");
        work.Save(_cacheDir);
        await Task.CompletedTask;
    }

    private async Task FinishCaseStepAsync(CaseFile file, CaseErpView view, CancellationToken ct)
    {
        _ = ct;
        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.Note,
            $"Case resolved: paid {view.PaidTotal} of {view.AcceptedTotal} accepted with no open holds or reviews.",
            amount: view.PaidTotal, currency: null);
        work.Patch(file.Case with { BlockedByRuleId = null, BlockedReason = null });
        work.Stage(CaseStage.Resolved);
        work.Save(_cacheDir);
        await Task.CompletedTask;
    }

    /// <summary>Builds the pre-pay gate input: ERP open holds for this case,
    /// quarantined lots from inventory transactions, and open reviews from
    /// pending dock/damage decisions.</summary>
    private async Task<PrePayState> BuildPrePayStateAsync(CaseFile file, CancellationToken ct)
    {
        var current = file.Case;
        var holds = await _erp.GetOpenHoldsAsync(null, ct).ConfigureAwait(false);
        var caseHolds = holds
            .Where(h => string.Equals(h.CaseId, current.CaseId, StringComparison.Ordinal))
            .ToList();
        var transactions = await _erp.GetInventoryTransactionsAsync(current.ItemId, ct).ConfigureAwait(false);
        var quarantined = transactions
            .Where(t => t.Status == InventoryStatus.Quarantined)
            .Select(t => t.LotRef)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        AsnDto? asn = current.AsnNumber is null
            ? null
            : await _erp.GetAsnAsync(current.AsnNumber, ct).ConfigureAwait(false);
        var reviews = file.Decisions
            .Where(d => d.Status == DecisionStatus.Pending
                && d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview)
            .Select(d => new OpenReview(
                d.DecisionId,
                d.RuleId ?? d.Kind.ToString(),
                current.CaseId,
                CartonFromEvidence(d),
                asn?.Cartons.FirstOrDefault(c =>
                    string.Equals(c.CartonId, CartonFromEvidence(d), StringComparison.Ordinal))?.LotRef,
                d.RequestedAt,
                d.Reason ?? d.Title))
            .ToList();
        return new PrePayState(caseHolds, quarantined, reviews);
    }

    private static string? CartonFromEvidence(Decision decision) =>
        decision.EvidenceRefs
            .Where(r => r.StartsWith("carton:", StringComparison.Ordinal))
            .Select(r => r["carton:".Length..])
            .FirstOrDefault();

    /// <summary>Routes a money document to the lowest-limit user covering the
    /// amount, following an active outbound delegation. Requester and AP-clerk
    /// roles never approve; extra user ids (e.g. invoice approvers for SoD)
    /// are excluded too. All limits, roles, and delegations come from ERP.</summary>
    private async Task<string> RouteDocumentAsync(
        decimal amount, IReadOnlyList<string> excludeUserIds, string? preferRole, CancellationToken ct)
    {
        var users = await _erp.GetUsersAsync(ct).ConfigureAwait(false);
        var candidates = users
            .Where(u => !excludeUserIds.Contains(u.UserId, StringComparer.OrdinalIgnoreCase))
            .Where(u => !string.Equals(u.Role, "requester", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(u.Role, "ap-clerk", StringComparison.OrdinalIgnoreCase))
            .Where(u => u.SigningLimit >= amount)
            .OrderByDescending(u => preferRole is not null
                && string.Equals(u.Role, preferRole, StringComparison.OrdinalIgnoreCase))
            .ThenBy(u => u.SigningLimit)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException($"No approver covers {amount}.");
        }

        var chosen = candidates[0];
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        var delegations = await _erp.GetDelegationsAsync(ct).ConfigureAwait(false);
        var outbound = delegations.FirstOrDefault(d =>
            string.Equals(d.FromUser, chosen.UserId, StringComparison.OrdinalIgnoreCase)
            && today >= d.Start && today <= d.End);
        return outbound?.ToUser ?? chosen.UserId;
    }

    private static string? ResolveRequester(CaseFile file) =>
        file.Events.FirstOrDefault(e => e.Kind == CaseEventKind.OrderReceived)?.Actor;

    private async Task<string?> DefaultRequesterAsync(CancellationToken ct)
    {
        var users = await _erp.GetUsersAsync(ct).ConfigureAwait(false);
        return users
            .Where(u => string.Equals(u.Role, "requester", StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.UserId, StringComparer.Ordinal)
            .FirstOrDefault()?.UserId;
    }

    private async Task<string> ResolveApClerkAsync(string fallback, CancellationToken ct)
    {
        var users = await _erp.GetUsersAsync(ct).ConfigureAwait(false);
        return users
            .Where(u => string.Equals(u.Role, "ap-clerk", StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.UserId, StringComparer.Ordinal)
            .FirstOrDefault()?.UserId ?? fallback;
    }

    private const string CandidatesDocType = "Candidates";

    private static void WriteCandidates(CaseWork work, string itemId, IReadOnlyList<string> vendorIds) =>
        work.AddEvent(CaseEventKind.Note, string.Join(",", vendorIds),
            documentType: CandidatesDocType, documentNumber: itemId);

    private static IReadOnlyList<string> ReadCandidates(CaseFile file, string itemId)
    {
        var note = file.Events.FirstOrDefault(e => e.Kind == CaseEventKind.Note
            && string.Equals(e.DocumentType, CandidatesDocType, StringComparison.Ordinal)
            && string.Equals(e.DocumentNumber, itemId, StringComparison.Ordinal));
        if (note is null || string.IsNullOrWhiteSpace(note.Summary))
        {
            return [];
        }

        return note.Summary.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Mutable accumulator over one case-file save: patch the case,
    /// append events, find-or-create decisions, then save once.</summary>
    private sealed class CaseWork
    {
        private readonly string _caseId;
        private Case _current;
        private readonly List<CaseEvent> _events;
        private readonly List<Decision> _decisions;

        public CaseWork(CaseFile file)
        {
            _caseId = file.Case.CaseId;
            _current = file.Case;
            _events = file.Events.ToList();
            _decisions = file.Decisions.ToList();
        }

        public Case Current => _current;

        public void Patch(Case patched) => _current = patched with { UpdatedAt = DateTimeOffset.UtcNow };

        public void Stage(CaseStage stage) => _current = _current with { Stage = stage, UpdatedAt = DateTimeOffset.UtcNow };

        public void Close(string summary)
        {
            AddEvent(CaseEventKind.Note, summary);
            Stage(CaseStage.Closed);
        }

        public void Cancel(string summary, string? ruleId = null)
        {
            AddEvent(CaseEventKind.Note, summary, ruleId: ruleId);
            Patch(_current with { BlockedByRuleId = ruleId, BlockedReason = summary });
            Stage(CaseStage.Cancelled);
        }

        public CaseEvent AddEvent(
            CaseEventKind kind, string summary, string? ruleId = null,
            string? documentType = null, string? documentNumber = null,
            string? evidenceRef = null, decimal? amount = null, string? currency = null,
            string? actor = null)
        {
            var entry = new CaseEvent(
                $"{_caseId}-e{_events.Count + 1:000}", _caseId, DateTimeOffset.UtcNow,
                actor ?? RecordSource.P2pAgent, kind, summary, ruleId,
                documentType, documentNumber, evidenceRef, amount, currency);
            _events.Add(entry);
            return entry;
        }

        public Decision FindOrCreateDecision(
            DecisionKind kind, string title, string requestedTo,
            string? documentType, string? documentNumber,
            decimal? amount = null, string? currency = null, string? reason = null,
            string? ruleId = null, IReadOnlyList<string>? evidenceRefs = null)
        {
            var existing = _decisions.FirstOrDefault(d =>
                d.Kind == kind
                && d.Status == DecisionStatus.Pending
                && string.Equals(d.DocumentNumber, documentNumber, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing;
            }

            var decision = new Decision(
                $"{_caseId}-d{_decisions.Count + 1:000}", _caseId, kind, title,
                DecisionStatus.Pending, requestedTo, DateTimeOffset.UtcNow,
                null, null, documentType, documentNumber, amount, currency,
                ruleId, reason, evidenceRefs ?? [], null);
            _decisions.Add(decision);
            return decision;
        }

        public Decision Decide(Decision decision, bool approved, string decidedBy, string? comment)
        {
            var index = _decisions.FindIndex(d =>
                string.Equals(d.DecisionId, decision.DecisionId, StringComparison.Ordinal));
            var updated = decision with
            {
                Status = approved ? DecisionStatus.Approved : DecisionStatus.Rejected,
                DecidedBy = decidedBy,
                DecidedAt = DateTimeOffset.UtcNow,
                Comment = comment
            };
            _decisions[index] = updated;
            return updated;
        }

        public void Save(string cacheDir)
        {
            Patch(_current);
            CaseStore.Save(cacheDir, new CaseFile(_current, _events, _decisions));
        }
    }
}

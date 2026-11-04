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
    private readonly PresentationPace _pace;
    private readonly IImageLabeler? _imageLabeler;
    private readonly DockImageOptions? _dockImageOptions;
    private readonly IReadOnlyDictionary<string, string> _countryNames;
    private readonly SplitProposals _split;
    private readonly int _turnMaxTokens;
    private readonly IVendorHeadroomSource _headroomSource;
    private readonly DockSceneLive _dockSceneLive;

    /// <summary>Default per-turn output budget when no override arrives
    /// (the app wires its configured value through turnMaxTokens).</summary>
    private const int DefaultTurnMaxTokens = 16000;

    public CaseRunner(
        IErpAdapter erp,
        P2pAgent core,
        IModelClient models,
        IComplianceEngine compliance,
        IDockEvaluator dock,
        string cacheDir,
        ILogger<CaseRunner>? logger = null,
        string? visionReadCacheDir = null,
        PresentationPace? pace = null,
        IImageLabeler? imageLabeler = null,
        DockImageOptions? dockImageOptions = null,
        IReadOnlyDictionary<string, string>? countryNames = null,
        SplitProposals? splitProposals = null,
        int? turnMaxTokens = null,
        IVendorHeadroomSource? headroomSource = null,
        DockSceneLive? dockSceneLive = null)
    {
        _erp = erp;
        _core = core;
        _models = models;
        _compliance = compliance;
        _dock = dock;
        _cacheDir = cacheDir;
        _logger = logger ?? NullLogger<CaseRunner>.Instance;
        _visionReadCacheDir = visionReadCacheDir ?? Path.Combine(cacheDir, "vision-reads");
        _pace = pace ?? new PresentationPace();
        _imageLabeler = imageLabeler;
        _dockImageOptions = dockImageOptions;
        _countryNames = countryNames ?? new Dictionary<string, string>();
        _split = splitProposals ?? new SplitProposals();
        _turnMaxTokens = turnMaxTokens ?? DefaultTurnMaxTokens;
        _headroomSource = headroomSource
            ?? new VendorHeadroomService(erp, compliance, cacheDir);
        _dockSceneLive = dockSceneLive ?? new DockSceneLive();
    }

    /// <summary>Advances the case through the Spark-driven tool loop until it
    /// parks in a waiting step (human, supplier, camera, vendor reply, or due
    /// date) or finishes. External inputs arrive via the simulation handlers
    /// between advances. payUpToDate caps the payment run date (null means no
    /// limit); every turn is appended to the case transcript.</summary>
    public async Task<CaseAdvanceOutcome> AdvanceAsync(
        string caseId, CancellationToken ct = default, DateOnly? payUpToDate = null) =>
        await AdvanceWithToolsAsync(caseId, payUpToDate, ct).ConfigureAwait(false);

    /// <summary>Reads the case's current next step without acting: the same
    /// pick the tool loop would advance. Undated (no payment-run cap).
    /// Surfaces use it to say what each open case waits on.</summary>
    public async Task<(CaseNextStep Step, string Reason)> NextStepAsync(
        string caseId, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var view = await BuildViewAsync(file, ct).ConfigureAwait(false);
        return CasePicker.Pick(file, view);
    }

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
            case CaseNextStep.AwardSupplier:
                await AwardSupplierStepAsync(file, vendorId: null, reason: null, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.ProposeSplit:
                ProposeSplitDeferred(file);
                break;
            case CaseNextStep.CreateSplitChildren:
                await CreateSplitChildrenStepAsync(file, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.CreatePo:
                await CreatePoStepAsync(file, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.SubmitPoApproval:
                await SubmitPoApprovalStepAsync(file, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.DraftPayment:
                await DraftPaymentStepAsync(file, view, payUpToDate, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.NotifyCustomer:
                await NotifyCustomerStepAsync(file, ct).ConfigureAwait(false);
                break;
            case CaseNextStep.QueryVendor:
                await QueryVendorStepAsync(file, ct).ConfigureAwait(false);
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
        var prepay = await BuildPrePayStateAsync(file, ct).ConfigureAwait(false);
        var prepayVerdicts = await _compliance.EvaluatePayableAsync(prepay, ct).ConfigureAwait(false);
        var prepayBlock = prepayVerdicts.FirstOrDefault(v => v.Kind == VerdictKind.Block);
        return new CaseErpView(
            so is not null, prStatus, poApproval, confirmation is not null,
            asn is not null, receipt is not null, matched, accepted, paid,
            held, openCaseHolds, notified,
            prepayBlock is not null, prepayBlock?.Reason, OrderCasesResolved(file));
    }

    /// <summary>True when every other case for this case's order is terminal
    /// (resolved, closed, or cancelled). The customer-secured message goes
    /// out once per order, on the last case to finish.</summary>
    private bool OrderCasesResolved(CaseFile file)
    {
        foreach (var caseId in CaseStore.ListCaseIds(_cacheDir))
        {
            if (string.Equals(caseId, file.Case.CaseId, StringComparison.Ordinal))
            {
                continue;
            }

            var sibling = CaseStore.Load(_cacheDir, caseId);
            if (sibling is null
                || !string.Equals(sibling.Case.SoNumber, file.Case.SoNumber, StringComparison.Ordinal))
            {
                continue;
            }

            if (sibling.Case.Stage is not CaseStage.Resolved
                and not CaseStage.Closed
                and not CaseStage.Cancelled)
            {
                return false;
            }
        }

        return true;
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
                $"PR {pr.PrNumber} for {DisplayWords.Qty(row.Shortfall)} {unit} of {current.ItemId}, estimated {DisplayWords.Money(pr.EstimatedAmount, pr.Currency)}.",
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
                $"PR {pr.PrNumber} auto-approved under threshold at {DisplayWords.Money(pr.EstimatedAmount, pr.Currency)}.",
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

        if (file.Events.Any(e => e.Kind == CaseEventKind.VendorScreened))
        {
            _logger.LogInformation("Case {CaseId}: vendors already screened; skipping re-screen.", current.CaseId);
            return;
        }

        var screenings = await _core.ScreenCandidatesAsync(current.PrNumber, candidates, ct).ConfigureAwait(false);
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
                // The block belongs to the award, not the vendor: CORP-02
                // depends on the order quantity, so a losing vendor loses
                // this award with the rule cited and stays buyable. Vendor
                // master blocks stay manual (ERP vendor screen only).
                await _erp.AddPoNoteAsync(current.PrNumber,
                    $"Vendor {screening.VendorId} rejected for {current.CaseId}: {block.RuleId}.",
                    RecordSource.P2pAgent, $"{current.CaseId}:screen:note:{screening.VendorId}", ct).ConfigureAwait(false);
                work.AddEvent(CaseEventKind.Note,
                    $"Vendor {screening.VendorId} rejected for the award: {block.RuleId}: {block.Reason}",
                    ruleId: block.RuleId);
            }
        }

        if (screenings.All(s => !s.Passed))
        {
            await RaiseCorpExceptionCardsAsync(work, current, screenings, ct).ConfigureAwait(false);
        }

        work.Save(_cacheDir);
    }

    /// <summary>Raises CORP-02 exception cards when the award is blocked (no
    /// passing vendor) and CORP-02 blocks at least one candidate. Each card
    /// routes to the sourcing-policy matrix owner (data-driven) and timelines
    /// with rule, vendor, and owner. A missing matrix row refuses loudly.</summary>
    private async Task RaiseCorpExceptionCardsAsync(
        CaseWork work, Case current, IReadOnlyList<VendorScreening> screenings,
        CancellationToken ct)
    {
        var corpBlocked = screenings
            .Where(s => !s.Passed
                && s.Verdicts.Any(v => v.Kind == VerdictKind.Block
                    && v.RuleId == CorpExceptionFlow.CorpOriginCapRuleId))
            .ToList();
        if (corpBlocked.Count == 0)
        {
            return;
        }

        var snapshot = await _compliance.LoadKbSnapshotAsync(ct).ConfigureAwait(false);
        var owner = snapshot.ApprovalMatrix
            .FirstOrDefault(a => a.Action == CorpExceptionFlow.SourcingPolicyAction)?.Owner;
        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new InvalidOperationException(
                $"Case {current.CaseId}: KB '{snapshot.KbVersion}' carries no approval-matrix row " +
                $"for '{CorpExceptionFlow.SourcingPolicyAction}'; " +
                $"{CorpExceptionFlow.CorpOriginCapRuleId} exception cannot route.");
        }

        foreach (var screening in corpBlocked)
        {
            var block = screening.Verdicts.First(v =>
                v.Kind == VerdictKind.Block && v.RuleId == CorpExceptionFlow.CorpOriginCapRuleId);
            var card = work.FindOrCreateDecision(
                DecisionKind.ExceptionApproval,
                $"{CorpExceptionFlow.CorpOriginCapRuleId} exception for vendor {screening.VendorId}",
                owner,
                CorpExceptionFlow.VendorDocumentType,
                screening.VendorId,
                reason: $"Vendor {screening.VendorId} blocked by {block.RuleId} for {current.ItemId} " +
                    $"on {current.PrNumber}: {block.Reason} Exception requires {owner} approval with reason.",
                ruleId: block.RuleId,
                evidenceRefs: current.PrNumber is null ? [] : [$"pr:{current.PrNumber}"]);
            work.AddEvent(CaseEventKind.Note,
                $"{CorpExceptionFlow.CorpOriginCapRuleId} exception approval card {card.DecisionId} " +
                $"raised to {owner} for vendor {screening.VendorId}.",
                ruleId: block.RuleId,
                documentType: CorpExceptionFlow.VendorDocumentType,
                documentNumber: screening.VendorId);
        }
    }

    /// <summary>Records the supplier award: the winner must come from the
    /// screened-passing set (re-derived, never trusted, plus CORP-02 vendors
    /// excused by approved exception grants), and the reason must name every
    /// other candidate plus each blocking rule id from its screening. Spark
    /// writes that one-line prose on the tool path (C# validates the loser
    /// tokens, never the style); a null vendor (C# fallback only) awards the
    /// first passing vendor and records the same loser detail
    /// deterministically. Exception awards timeline the grant approver.</summary>
    private async Task AwardSupplierStepAsync(
        CaseFile file, string? vendorId, string? reason, CancellationToken ct)
    {
        var current = file.Case;
        if (current.PrNumber is null)
        {
            throw new InvalidOperationException($"Case {current.CaseId} has no PR to award.");
        }

        var candidates = ReadCandidates(file, current.ItemId);
        var raw = await _core.ScreenCandidatesAsync(current.PrNumber, candidates, ct).ConfigureAwait(false);
        var (screenings, passing, grants) = CorpExceptionFlow.ApplyApprovedExceptions(file, raw);
        if (passing.Count == 0)
        {
            throw new InvalidOperationException("No compliant vendor: PO refused.");
        }

        var winner = vendorId;
        var why = reason;
        if (winner is null)
        {
            winner = passing[0];
            why = FallbackAwardReason(winner, screenings);
        }
        else
        {
            if (!passing.Contains(winner, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Vendor {winner} did not pass screening for case {current.CaseId}; passing: {string.Join(", ", passing)}.");
            }

            RequireLosersInReason(why, screenings, winner, current.CaseId);
        }

        if (grants.TryGetValue(winner, out var grant))
        {
            why += $" {CorpExceptionFlow.CorpOriginCapRuleId} exception for {winner} granted by {grant.Approver}.";
        }

        var refused = file.Events
            .Where(e => e.Kind == CaseEventKind.AgentRefused
                && string.Equals(e.RuleId, AgentTools.AwardSupplier, StringComparison.Ordinal)
                && string.Equals(e.DocumentType, "Vendor", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(e.DocumentNumber)
                && !string.Equals(e.DocumentNumber, winner, StringComparison.Ordinal))
            .MaxBy(e => e.Timestamp);
        if (refused?.DocumentNumber is not null)
        {
            why += $" (Tried {refused.DocumentNumber} first; it was refused, so the award went to {winner}.)";
        }

        var pr = await _erp.GetPrAsync(current.PrNumber, ct).ConfigureAwait(false);
        var qty = pr?.Lines.Sum(l => l.Quantity) ?? 0;
        var work = new CaseWork(file);
        var awarded = work.AddEvent(CaseEventKind.SupplierAwarded,
            $"Awarded {current.ItemId} x{DisplayWords.Qty(qty)} to {winner}: {why}",
            documentType: "PR", documentNumber: current.PrNumber);
        work.Patch(current with { AwardedVendorId = winner });
        work.SetSnapshot(await BuildScreeningSnapshotAsync(
            file, screenings, grants, winner, pr, awarded.Timestamp, ct).ConfigureAwait(false));
        work.Save(_cacheDir);
    }

    /// <summary>Freezes the screening evidence as it stood at the award: one
    /// row per screened candidate with vendor facts, unit price, the shared
    /// result and reason words, the full verdicts, and, for critical items,
    /// the origin-cap sentence built from the verdict's own figures (never
    /// recomputed, so the sentence always matches the verdict's share).
    /// Non-critical items store no sentence: the cap does not apply to
    /// them, and a share sentence would contradict the table's reason.
    /// Decided orders display this stored copy; only undecided orders
    /// re-run live.</summary>
    private async Task<AwardScreeningSnapshot> BuildScreeningSnapshotAsync(
        CaseFile file,
        IReadOnlyList<VendorScreening> screenings,
        IReadOnlyDictionary<string, CorpExceptionGrant> grants,
        string? winner,
        PurchaseRequisition? pr,
        DateTimeOffset takenAt,
        CancellationToken ct)
    {
        var current = file.Case;
        var vendors = (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
            .ToDictionary(v => v.VendorId, StringComparer.Ordinal);
        string noun = "units";
        var capApplies = true;
        try
        {
            var item = await _erp.GetItemAsync(current.ItemId, ct).ConfigureAwait(false);
            if (item is not null)
            {
                noun = DisplayWords.CountNoun(item.Description);
                capApplies = item.IsCritical;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Screening snapshot for case {CaseId} could not read item {Item}; using the fallback noun.",
                current.CaseId, current.ItemId);
        }

        var rows = new List<AwardScreeningRow>();
        foreach (var screening in screenings)
        {
            if (!vendors.TryGetValue(screening.VendorId, out var vendor))
            {
                throw new InvalidOperationException(
                    $"Vendor {screening.VendorId} was screened but is unknown to the ERP; cannot record the award for case {current.CaseId}.");
            }

            decimal? unit;
            try
            {
                unit = await _erp.GetVendorPriceAsync(screening.VendorId, current.ItemId, ct).ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                unit = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Screening snapshot for case {CaseId} could not read the price for {Vendor}; storing no price.",
                    current.CaseId, screening.VendorId);
                unit = null;
            }

            var country = DisplayCountry(vendor.CountryOfOrigin);
            var figures = screening.Verdicts
                .Where(v => string.Equals(v.RuleId, CorpExceptionFlow.CorpOriginCapRuleId, StringComparison.Ordinal))
                .Select(v => v.OriginCap)
                .FirstOrDefault(f => f is not null
                    && string.Equals(f.ItemId, current.ItemId, StringComparison.Ordinal));
            rows.Add(new AwardScreeningRow(
                screening.VendorId,
                vendor.Name,
                vendor.CountryOfOrigin,
                country,
                unit,
                pr?.Currency,
                screening.Passed,
                ScreeningRowWords.ResultFor(screening.Passed, screening.Verdicts),
                ScreeningRowWords.ReasonFor(screening.Verdicts, CorpExceptionFlow.CorpOriginCapRuleId),
                string.Equals(screening.VendorId, winner, StringComparison.Ordinal),
                screening.Verdicts,
                figures is null || !capApplies
                    ? null
                    : ScreeningSentence.Build(
                        CorpExceptionFlow.CorpOriginCapRuleId, vendor.Name, country,
                        figures.CountryCells, figures.TotalCells,
                        figures.CountryOpenCells, figures.TotalOpenCells,
                        figures.AwardQty, figures.Cap, noun,
                        grants.ContainsKey(screening.VendorId))));
        }

        var awardQty = pr?.Lines
            .Where(l => string.Equals(l.ItemId, current.ItemId, StringComparison.Ordinal))
            .Sum(l => l.Quantity) ?? 0;
        return new AwardScreeningSnapshot(
            takenAt, current.PrNumber!, current.ItemId, awardQty, winner, rows);
    }

    private string DisplayCountry(string code) =>
        _countryNames.TryGetValue(code, out var name) ? name : code;

    /// <summary>Screening block rule ids for one vendor: distinct Block
    /// verdicts excluding the ERP-block marker (losers are already blocked
    /// when the award re-derives screenings, and the marker is not a rule
    /// the screening events ever showed).</summary>
    private static IReadOnlyList<string> ScreeningBlockRuleIds(VendorScreening screening) =>
        screening.Verdicts
            .Where(v => v.Kind == VerdictKind.Block)
            .Select(v => v.RuleId)
            .Where(id => !string.Equals(id, ComplianceVerdictIds.VendorBlock, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    /// <summary>Deterministic fallback award prose: names the first passing
    /// vendor plus every other candidate with its honest outcome (blocking
    /// rule ids for blocked candidates, passed-but-not-chosen otherwise).</summary>
    private static string FallbackAwardReason(
        string winner, IReadOnlyList<VendorScreening> screenings, string lead = "First passing vendor")
    {
        var losers = screenings
            .Where(s => !string.Equals(s.VendorId, winner, StringComparison.Ordinal))
            .ToList();
        if (losers.Count == 0)
        {
            return $"{lead} {winner} takes the award.";
        }

        var clauses = losers.Select(loser =>
        {
            var blocks = ScreeningBlockRuleIds(loser);
            return blocks.Count > 0
                ? $"{loser.VendorId} blocked by {string.Join(", ", blocks)}"
                : $"{loser.VendorId} passed but not chosen";
        });
        return $"{lead} {winner} takes the award. Other candidates: {string.Join("; ", clauses)}.";
    }

    /// <summary>Validates a Spark-written award reason names every other
    /// candidate plus each blocking rule id from its screening. Loser detail
    /// is deterministic from the verdicts; only the prose is the model's.</summary>
    private static void RequireLosersInReason(
        string? reason, IReadOnlyList<VendorScreening> screenings, string winner, string caseId)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException(
                $"Award for case {caseId} needs a one-line reason naming each losing vendor and its blocking rule id.");
        }

        var missing = MissingLoserTokens(reason, screenings, winner);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Award reason for case {caseId} must name each losing vendor and its blocking rule id; " +
                $"missing: {string.Join(", ", missing)}.");
        }
    }

    private static IReadOnlyList<string> MissingLoserTokens(
        string reason, IReadOnlyList<VendorScreening> screenings, string winner)
    {
        var missing = new List<string>();
        foreach (var loser in screenings.Where(s =>
            !string.Equals(s.VendorId, winner, StringComparison.Ordinal)))
        {
            if (!reason.Contains(loser.VendorId, StringComparison.Ordinal))
            {
                missing.Add(loser.VendorId);
            }

            foreach (var ruleId in ScreeningBlockRuleIds(loser))
            {
                if (!reason.Contains(ruleId, StringComparison.Ordinal))
                {
                    missing.Add(ruleId);
                }
            }
        }

        return missing;
    }

    /// <summary>Creates the PO, recovering across vendors: the awarded
    /// vendor goes first, then every other passing vendor in candidate
    /// order. Vendors without a price or blocked in the master are
    /// skipped with a timeline note; an ERP refusal for one vendor is
    /// noted and the next vendor is tried. Only when no passing vendor
    /// can supply does the step fail, with every refusal on the
    /// timeline. A fallback award lands on the PO vendor.</summary>
    private async Task CreatePoStepAsync(CaseFile file, CancellationToken ct)
    {
        var current = file.Case;
        if (current.PrNumber is null || current.AwardedVendorId is null)
        {
            throw new InvalidOperationException(
                $"Case {current.CaseId} has no awarded supplier to create a PO for.");
        }

        var pr = await _erp.GetPrAsync(current.PrNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PR {current.PrNumber} vanished before PO creation.");
        var candidates = ReadCandidates(file, current.ItemId);
        var raw = await _core.ScreenCandidatesAsync(current.PrNumber, candidates, ct).ConfigureAwait(false);
        var (_, passing, _) = CorpExceptionFlow.ApplyApprovedExceptions(file, raw);
        var rank = candidates
            .Select((id, i) => (id, i))
            .ToDictionary(p => p.id, p => p.i, StringComparer.Ordinal);
        var ordered = passing
            .OrderByDescending(v => string.Equals(v, current.AwardedVendorId, StringComparison.Ordinal))
            .ThenBy(v => rank.TryGetValue(v, out var i) ? i : int.MaxValue)
            .ToList();
        if (!ordered.Contains(current.AwardedVendorId, StringComparer.Ordinal))
        {
            ordered.Insert(0, current.AwardedVendorId);
        }

        HashSet<string>? priced = null;
        try
        {
            priced = (await _erp.GetPricedVendorIdsAsync(current.ItemId, ct).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (NotSupportedException)
        {
            // Layers without the priced-vendor read skip the pre-check;
            // the per-vendor try below still recovers on refusal.
        }

        var vendorById = (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
            .ToDictionary(v => v.VendorId, StringComparer.Ordinal);
        var work = new CaseWork(file);
        var failures = new List<string>();
        foreach (var vendorId in ordered)
        {
            if (priced is not null && !priced.Contains(vendorId))
            {
                failures.Add($"{vendorId}: no price for {current.ItemId}");
                work.AddEvent(CaseEventKind.Note,
                    $"PO skipped for {vendorId}: no price for {current.ItemId}; trying next vendor.",
                    documentType: "PR", documentNumber: current.PrNumber);
                continue;
            }

            if (vendorById.TryGetValue(vendorId, out var vendor) && vendor.IsBlocked)
            {
                failures.Add($"{vendorId}: vendor is blocked ({vendor.BlockReason})");
                work.AddEvent(CaseEventKind.Note,
                    $"PO skipped for {vendorId}: vendor is blocked ({vendor.BlockReason}); trying next vendor.",
                    documentType: "PR", documentNumber: current.PrNumber);
                continue;
            }

            try
            {
                var po = await _erp.CreatePoAsync(current.PrNumber, vendorId,
                    RecordSource.P2pAgent, pr.Requester, $"{current.CaseId}:create-po:{vendorId}", ct: ct).ConfigureAwait(false);
                if (!string.Equals(vendorId, current.AwardedVendorId, StringComparison.Ordinal))
                {
                    var cause = failures.FirstOrDefault(f =>
                        f.StartsWith(current.AwardedVendorId + ":", StringComparison.Ordinal));
                    work.AddEvent(CaseEventKind.SupplierAwarded,
                        cause is null
                            ? $"Re-awarded {current.ItemId} to {vendorId} (PO could not go to {current.AwardedVendorId})."
                            : $"Re-awarded {current.ItemId} to {vendorId} ({cause}).",
                        documentType: "PR", documentNumber: current.PrNumber);
                    work.Patch(work.Current with { AwardedVendorId = vendorId });
                    work.SetSnapshot(file.ScreeningSnapshot?.WithWinner(vendorId));
                }

                work.AddEvent(CaseEventKind.PoCreated,
                    $"PO {po.PoNumber} to {po.VendorId} for {DisplayWords.Money(po.Lines.Sum(l => l.LineAmount), po.Currency)}.",
                    documentType: "PO", documentNumber: po.PoNumber);
                work.Patch(work.Current with { PoNumber = po.PoNumber });
                work.Stage(CaseStage.PoPlaced);
                work.Save(_cacheDir);
                return;
            }
            catch (InvalidOperationException ex)
            {
                failures.Add($"{vendorId}: {ex.Message}");
                work.AddEvent(CaseEventKind.Note,
                    $"PO refused for {vendorId}: {ex.Message}; trying next vendor.",
                    documentType: "PR", documentNumber: current.PrNumber);
            }
        }

        work.AddEvent(CaseEventKind.Note,
            $"No vendor could supply {current.ItemId} for case {current.CaseId}." +
            (failures.Count == 0
                ? " No priced, unblocked passing vendor remained."
                : $" Refusals: {string.Join(" | ", failures)}."),
            documentType: "PR", documentNumber: current.PrNumber);
        work.Save(_cacheDir);
        throw new InvalidOperationException(
            $"No vendor could supply {current.ItemId} x{DisplayWords.Qty(pr.Lines.Sum(l => l.Quantity))} for case {current.CaseId}.");
    }

    /// <summary>Deferred split step: the model returned no tool calls, so it
    /// declined to propose. Records the skip so the case falls through to
    /// today's single-vendor award instead of proposing forever. C# never
    /// invents a split on its own.</summary>
    private void ProposeSplitDeferred(CaseFile file)
    {
        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.Note,
            $"Split skipped [{CasePicker.SplitSkippedMarker}]: no proposal; single-vendor award proceeds.");
        work.Save(_cacheDir);
    }

    /// <summary>Cards a verified split plan for a person. The plan rides on
    /// the decision; the full-quantity screenings are stored as the parent's
    /// evidence with no single winner, exactly what the card shows.</summary>
    private async Task<Decision> CreateSplitCardAsync(
        CaseFile file, SplitVerification verification, string? note, CancellationToken ct)
    {
        var current = file.Case;
        var plan = verification.Plan!;
        var pr = await _erp.GetPrAsync(current.PrNumber!, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PR {current.PrNumber} vanished before the split card.");
        var routedTo = await RouteDocumentAsync(
            plan.SplitTotal, pr.Requester is string requester ? [requester] : [], null, ct).ConfigureAwait(false);
        var item = await _erp.GetItemAsync(current.ItemId, ct).ConfigureAwait(false);
        var prose = SplitPlanVerifier.DescribePlan(plan, current.ItemId, DisplayWords.CountNoun(item?.Description));
        if (!string.IsNullOrWhiteSpace(note))
        {
            prose += $" Agent note: {note.Trim()}";
        }
        var work = new CaseWork(file);
        var card = work.FindOrCreateDecision(
            DecisionKind.SplitApproval, $"Approve split award ({current.PrNumber})", routedTo,
            "PR", current.PrNumber, plan.SplitTotal, plan.Currency, prose,
            CorpExceptionFlow.CorpOriginCapRuleId, split: plan);
        var proposed = work.AddEvent(CaseEventKind.Note,
            $"Split proposed: {prose} Decision {card.DecisionId} needs {routedTo}.",
            documentType: "PR", documentNumber: current.PrNumber);
        work.SetSnapshot(await BuildScreeningSnapshotAsync(
            file, verification.FullScreenings, verification.Grants, null, pr, proposed.Timestamp, ct).ConfigureAwait(false));
        work.Save(_cacheDir);
        return card;
    }

    /// <summary>Creates one child case per approved split line and places
    /// their POs in plan order, each passing the ERP's check on its own
    /// with the earlier lines counted as open. Children run today's
    /// single-PO flow from placed onward. Idempotent: existing children
    /// and placed POs are skipped, so a retry finishes a partial run.</summary>
    private async Task CreateSplitChildrenStepAsync(CaseFile file, CancellationToken ct)
    {
        var current = file.Case;
        var approved = file.Decisions.FirstOrDefault(d =>
            d.Kind == DecisionKind.SplitApproval && d.Status == DecisionStatus.Approved)
            ?? throw new InvalidOperationException($"Case {current.CaseId} has no approved split.");
        var plan = approved.Split
            ?? throw new InvalidOperationException($"Case {current.CaseId} approved split has no plan.");
        if (current.PrNumber is null)
        {
            throw new InvalidOperationException($"Case {current.CaseId} has no PR to split.");
        }

        var pr = await _erp.GetPrAsync(current.PrNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PR {current.PrNumber} vanished before the split orders.");
        var parentSnapshot = file.ScreeningSnapshot
            ?? throw new InvalidOperationException($"Case {current.CaseId} split has no stored screening.");
        var now = DateTimeOffset.UtcNow;
        var made = new List<string>();
        for (var i = 0; i < plan.Lines.Count; i++)
        {
            var line = plan.Lines[i];
            var childId = CaseStore.MintCaseId(current.CaseId, $"split-{i + 1}");
            var child = CaseStore.Load(_cacheDir, childId);
            if (child is not null
                && !string.Equals(child.Case.AwardedVendorId, line.VendorId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Split child {childId} names a different vendor than the approved plan.");
            }

            if (child is null)
            {
                var born = new Case(
                    childId, current.SoNumber, current.ItemId, current.ScenarioName,
                    CaseStage.PrApproved, now, now, current.PrNumber, null, null, null,
                    [], [], null, null, line.VendorId, false, current.CaseId);
                var screened = file.Events
                    .Where(e => e.Kind == CaseEventKind.VendorScreened)
                    .ToList();
                var birth = new CaseWork(new CaseFile(born, screened, []));
                birth.AddEvent(CaseEventKind.SupplierAwarded,
                    $"Split line awarded: {current.ItemId} x{DisplayWords.Qty(line.Quantity)} to {line.VendorId} " +
                    $"under {current.CaseId} split approved by {approved.DecidedBy}.",
                    documentType: "PR", documentNumber: current.PrNumber);
                birth.SetSnapshot(parentSnapshot.WithWinner(line.VendorId));
                birth.Save(_cacheDir);
                child = CaseStore.Load(_cacheDir, childId)!;
            }

            if (child.Case.PoNumber is null)
            {
                var po = await _erp.CreatePoAsync(current.PrNumber, line.VendorId,
                    RecordSource.P2pAgent, pr.Requester, $"{current.CaseId}:split-po:{i + 1}",
                    [new PoOrderLine(current.ItemId, line.Quantity)], ct).ConfigureAwait(false);
                var placed = new CaseWork(child);
                placed.AddEvent(CaseEventKind.PoCreated,
                    $"PO {po.PoNumber} to {po.VendorId} for {DisplayWords.Money(po.Lines.Sum(l => l.LineAmount), po.Currency)}.",
                    documentType: "PO", documentNumber: po.PoNumber);
                placed.Patch(placed.Current with { PoNumber = po.PoNumber });
                placed.Stage(CaseStage.PoPlaced);
                placed.Save(_cacheDir);
            }

            made.Add(childId);
        }

        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.Note,
            $"Split ordered: {made.Count} child cases ({string.Join(", ", made)}) place the approved lines in plan order.",
            documentType: "PR", documentNumber: current.PrNumber);
        work.Stage(CaseStage.SplitOrdered);
        work.Save(_cacheDir);
    }

    /// <summary>Settles a split parent once every child reaches a terminal
    /// stage. All resolved: the parent resolves and, when every other case
    /// for the order is resolved too, sends the one customer message. Any
    /// child cancelled or closed: the parent cancels with the reason, since
    /// the split no longer covers the request.</summary>
    private async Task TryResolveParentAsync(string parentId, CancellationToken ct)
    {
        var parent = CaseStore.Load(_cacheDir, parentId);
        if (parent is null
            || parent.Case.Stage is CaseStage.Resolved or CaseStage.Closed or CaseStage.Cancelled)
        {
            return;
        }

        var children = new List<CaseFile>();
        foreach (var id in CaseStore.ListCaseIds(_cacheDir))
        {
            var candidate = CaseStore.Load(_cacheDir, id);
            if (candidate is not null
                && string.Equals(candidate.Case.ParentCaseId, parentId, StringComparison.Ordinal))
            {
                children.Add(candidate);
            }
        }

        if (children.Count == 0
            || !children.All(c => c.Case.Stage is CaseStage.Resolved or CaseStage.Closed or CaseStage.Cancelled))
        {
            return;
        }

        var work = new CaseWork(parent);
        var failed = children
            .Where(c => c.Case.Stage is not CaseStage.Resolved)
            .Select(c => $"{c.Case.CaseId} ({c.Case.Stage})")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (failed.Count > 0)
        {
            work.Cancel($"Split incomplete: {string.Join(", ", failed)}; the split no longer covers the request.");
            work.Save(_cacheDir);
            return;
        }

        work.AddEvent(CaseEventKind.Note,
            $"Split complete: all {children.Count} split orders resolved.");
        work.Patch(parent.Case with { BlockedByRuleId = null, BlockedReason = null });
        work.Stage(CaseStage.Resolved);
        work.Save(_cacheDir);

        var settled = CaseStore.Load(_cacheDir, parentId) ?? parent;
        if (OrderCasesResolved(settled))
        {
            await RecordCustomerNotifiedAsync(settled, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Advances every split child of a parked parent in plan order
    /// (child ids sort by split line). Every sibling runs even when one
    /// fails: failures are logged, noted on the failing child's timeline,
    /// and thrown together naming the children, so Decisions shows Try
    /// again on the parent and a retry finishes the rest. Retries are
    /// idempotent: submitted POs skip the draft check and existing cards
    /// are found, never duplicated.</summary>
    private async Task AdvanceSplitChildrenAsync(
        string parentId, DateOnly? payUpToDate, CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var id in CaseStore.ListCaseIds(_cacheDir))
        {
            var candidate = CaseStore.Load(_cacheDir, id);
            if (candidate is null
                || !string.Equals(candidate.Case.ParentCaseId, parentId, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var outcome = await AdvanceWithToolsAsync(id, payUpToDate, ct, fanOut: false)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Split child {Child} of {Parent} advanced to {Step} ({Reason}).",
                    id, parentId, outcome.Step, outcome.Summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Split child {Child} of {Parent} failed to advance.", id, parentId);
                RecordFailureNote(id, "split child advance", ex);
                failures.Add($"{id}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Split {parentId} has {failures.Count} child case(s) needing attention: " +
                string.Join("; ", failures));
        }
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

    /// <summary>Pre-pay gate line in words with amounts: what is held and
    /// why (holds, quarantine, open reviews), plus what can be paid. Each
    /// currency gets its own clause; nothing is ever blended.</summary>
    private async Task<string> PrePayLineAsync(
        Case current, PrePayState state, IReadOnlyList<Verdict> verdicts, CancellationToken ct)
    {
        var heldByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var payableByCurrency = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var key in current.Invoices)
        {
            var invoice = await _erp.GetInvoiceAsync(key.VendorId, key.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            if (invoice is null)
            {
                continue;
            }

            var currency = string.IsNullOrWhiteSpace(invoice.Currency) ? "currency missing" : invoice.Currency;
            heldByCurrency.TryGetValue(currency, out var held);
            heldByCurrency[currency] = held + invoice.Lines
                .Where(l => l.Disposition == LineDisposition.Held)
                .Sum(l => l.Quantity * l.UnitPrice);
            payableByCurrency.TryGetValue(currency, out var payable);
            payableByCurrency[currency] = payable + invoice.Lines
                .Where(l => l.Disposition == LineDisposition.Accepted)
                .Sum(l => l.Quantity * l.UnitPrice);
        }

        var causes = new List<string>();
        causes.AddRange(state.OpenHolds.Select(h =>
            string.IsNullOrWhiteSpace(h.CartonId) ? $"hold {h.HoldId} is open" : $"{h.CartonId} is on hold"));
        causes.AddRange(state.QuarantinedLotRefs.Select(lot => $"{lot} is in quarantine"));
        causes.AddRange(state.DockPendingLotRefs.Select(lot => $"{lot} is awaiting the dock check"));
        causes.AddRange(state.OpenReviews.Select(r =>
            string.IsNullOrWhiteSpace(r.CartonId) ? $"review {r.ReviewId} is open" : $"{r.CartonId} is under review"));

        var verdictText = string.Join("; ", verdicts.Select(v =>
            $"{v.RuleId} {DisplayWords.Verdict(v.Kind).ToLowerInvariant()}: {v.Reason}"));
        var parts = new List<string>();
        foreach (var currency in heldByCurrency.Keys
            .Concat(payableByCurrency.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal))
        {
            heldByCurrency.TryGetValue(currency, out var held);
            payableByCurrency.TryGetValue(currency, out var payable);
            var code = string.Equals(currency, "currency missing", StringComparison.Ordinal) ? null : currency;
            if (held > 0)
            {
                parts.Add(causes.Count == 0
                    ? $"{DisplayWords.Money(held, code)} held."
                    : $"{DisplayWords.Money(held, code)} held ({string.Join("; ", causes)}).");
            }

            parts.Add(payable > 0
                ? held > 0
                    ? $"The other {DisplayWords.Money(payable, code)} can be paid."
                    : $"{DisplayWords.Money(payable, code)} can be paid."
                : "Nothing can be paid yet.");
        }

        if (parts.Count == 0)
        {
            parts.Add("No invoice value on this case yet.");
        }

        parts.Add(verdictText);
        return "Pre-pay gate: " + string.Join(" ", parts);
    }

    private async Task DraftPaymentStepAsync(
        CaseFile file, CaseErpView view, DateOnly? payUpToDate, CancellationToken ct)
    {
        var current = file.Case;
        var state = await BuildPrePayStateAsync(file, ct).ConfigureAwait(false);
        var verdicts = await _compliance.EvaluatePayableAsync(state, ct).ConfigureAwait(false);
        var block = verdicts.FirstOrDefault(v => v.Kind == VerdictKind.Block);
        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.Note,
            await PrePayLineAsync(current, state, verdicts, ct).ConfigureAwait(false),
            ruleId: block?.RuleId ?? verdicts.FirstOrDefault()?.RuleId,
            documentType: "Payment");
        if (block is not null)
        {
            // Open reviews block the whole payment: no draft until they
            // close. The picker parks here, so reaching this turn means a
            // review opened after the pick.
            work.Patch(current with { BlockedByRuleId = block.RuleId, BlockedReason = block.Reason });
            work.Save(_cacheDir);
            return;
        }

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
            if (ex.Message.StartsWith("ELEC-04", StringComparison.Ordinal))
            {
                // Policy refusal, not a terms refusal: no not-due marker.
                // The picker parks on the pre-pay block and re-picks the
                // draft once the holds, quarantine, or reviews close.
                work.AddEvent(CaseEventKind.Note,
                    $"ERP refused the draft on policy: {ex.Message}",
                    ruleId: "ELEC-04", documentType: "Payment");
                work.Save(_cacheDir);
                return;
            }

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
            $" {draft.PaymentNumber} drafted for {DisplayWords.Money(draft.Lines.Sum(l => l.Amount), draft.Currency)} " +
            $"(accepted {view.AcceptedTotal}, paid {view.PaidTotal}); held value stays open.",
            ruleId: block?.RuleId, documentType: "Payment", documentNumber: draft.PaymentNumber,
            amount: draft.Lines.Sum(l => l.Amount), currency: draft.Currency);
        work.FindOrCreateDecision(
            DecisionKind.PaymentApproval, $"Approve payment {draft.PaymentNumber}", routedTo,
            "Payment", draft.PaymentNumber, draft.Lines.Sum(l => l.Amount), draft.Currency,
            partial
                ? $"Partial pay of accepted value; {block?.RuleId ?? "held lines"} hold the rest."
                : $"Payment of {DisplayWords.Money(draft.Lines.Sum(l => l.Amount), draft.Currency)}.");
        work.Patch(current with { BlockedByRuleId = block?.RuleId, BlockedReason = block?.Reason });
        work.Stage(partial ? CaseStage.PaymentPartial : CaseStage.PaymentRequested);
        work.Save(_cacheDir);
    }

    /// <summary>C# fallback for the vendor-query step: emails the first
    /// unemailed invoice problem. The tool path carries Spark's own
    /// invoice_ref instead and never reaches this default.</summary>
    private async Task QueryVendorStepAsync(CaseFile file, CancellationToken ct)
    {
        var problem = CasePicker.UnemailedInvoiceProblems(file).FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Case {file.Case.CaseId} has no unemailed invoice problem to query.");
        await SendVendorEmailAsync(file.Case.CaseId, problem.DocumentNumber!, ct).ConfigureAwait(false);
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
            $"Customer notified: order {file.Case.SoNumber} is secured; every case for the order is resolved.");
        work.Save(_cacheDir);
        await Task.CompletedTask;
    }

    private async Task FinishCaseStepAsync(CaseFile file, CaseErpView view, CancellationToken ct)
    {
        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.Note,
            $"Case resolved: paid {view.PaidTotal} of {view.AcceptedTotal} accepted with no open holds or reviews.",
            amount: view.PaidTotal, currency: null);
        work.Patch(file.Case with { BlockedByRuleId = null, BlockedReason = null });
        work.Stage(CaseStage.Resolved);
        work.Save(_cacheDir);
        if (file.Case.ParentCaseId is not null)
        {
            await TryResolveParentAsync(file.Case.ParentCaseId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Builds the pre-pay gate input: ERP open holds for this case,
    /// quarantined lots from inventory transactions, dock-pending lots
    /// from ASN cartons without an all-pass verdict, and open reviews from
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
        var dockPending = DockPendingLots(current.CaseId, asn, quarantined);
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
        return new PrePayState(caseHolds, quarantined, reviews, dockPending);
    }

    /// <summary>Lots on the case's ASN cartons without a complete all-pass
    /// dock verdict. Quarantined lots are excluded: they already hold under
    /// quarantine, so naming them pending too would count one carton twice.
    /// No ASN (or no cartons) means nothing pending: nothing photographable
    /// was declared.</summary>
    private IReadOnlyList<string> DockPendingLots(
        string caseId, AsnDto? asn, IReadOnlyList<string> quarantined)
    {
        if (asn is null || asn.Cartons.Count == 0)
        {
            return [];
        }

        var passed = new HashSet<string>(StringComparer.Ordinal);
        var evidence = CaseStore.LoadDock(_cacheDir, caseId);
        if (evidence is not null)
        {
            foreach (var (cartonId, verdicts) in evidence.Verdicts)
            {
                if (verdicts.Count > 0 && verdicts.All(v => v.Kind == VerdictKind.Pass))
                {
                    passed.Add(cartonId);
                }
            }
        }

        var quarantinedSet = quarantined.ToHashSet(StringComparer.Ordinal);
        return asn.Cartons
            .Where(c => !passed.Contains(c.CartonId))
            .Select(c => c.LotRef)
            .Where(lot => !quarantinedSet.Contains(lot))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();
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

    internal static IReadOnlyList<string> ReadCandidates(CaseFile file, string itemId)
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
        private AwardScreeningSnapshot? _snapshot;

        public CaseWork(CaseFile file)
        {
            _caseId = file.Case.CaseId;
            _current = file.Case;
            _events = file.Events.ToList();
            _decisions = file.Decisions.ToList();
            _snapshot = file.ScreeningSnapshot;
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
            string? ruleId = null, IReadOnlyList<string>? evidenceRefs = null,
            SplitPlan? split = null)
        {
            var existing = _decisions.FirstOrDefault(d =>
                d.Kind == kind
                && d.Status == DecisionStatus.Pending
                && string.Equals(d.DocumentNumber, documentNumber, StringComparison.Ordinal));
            if (existing is not null)
            {
                return existing;
            }

            return CreateDecision(
                kind, title, requestedTo, documentType, documentNumber,
                amount, currency, reason, ruleId, evidenceRefs, split);
        }

        /// <summary>Always appends a fresh decision card. Vendor-identity
        /// proposals use this (their document number is the proposed vendor
        /// id, which repeats across invoices), after their own dedupe on raw
        /// text plus evidence refs.</summary>
        public Decision CreateDecision(
            DecisionKind kind, string title, string requestedTo,
            string? documentType, string? documentNumber,
            decimal? amount = null, string? currency = null, string? reason = null,
            string? ruleId = null, IReadOnlyList<string>? evidenceRefs = null,
            SplitPlan? split = null)
        {
            var decision = new Decision(
                $"{_caseId}-d{_decisions.Count + 1:000}", _caseId, kind, title,
                DecisionStatus.Pending, requestedTo, DateTimeOffset.UtcNow,
                null, null, documentType, documentNumber, amount, currency,
                ruleId, reason, evidenceRefs ?? [], null, split);
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

        /// <summary>Stores the award-time screening snapshot: verdicts,
        /// sentences, and figures as they stood when decided.</summary>
        public void SetSnapshot(AwardScreeningSnapshot? snapshot) => _snapshot = snapshot;

        public void Save(string cacheDir)
        {
            Patch(_current);
            CaseStore.Save(cacheDir, new CaseFile(_current, _events, _decisions, _snapshot));
        }
    }
}

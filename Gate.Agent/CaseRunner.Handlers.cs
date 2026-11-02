// Simulation handlers: external inputs in, ERP plus case-file writes out.
// Order webhook, supplier ACK, supplier ASN (with receipt posting, since the
// packing slip travels with the ASN), dock reads, reshot reads, supplier
// invoices including the tricky inbox, human decision resolution, and the
// customer-secured notification. Every handler is idempotent: repeating the
// same input returns the stored outcome, ERP writes carry deterministic keys,
// and replays from the case cache make zero live calls.
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Agent;

public sealed record InvoiceIntakeOutcome(
    InvoiceKey? Key,
    bool Persisted,
    string? DecisionId,
    decimal AcceptedValue,
    decimal HeldValue);

public partial class CaseRunner
{
    // Bank-change callback script (DECISIONS.md 41): the human calls the number
    // on file, never a number from the change request.
    private const string BankVerifyGuidance =
        "Call the vendor at the number on file. Ask: (1) confirm the new account ref and bank name; " +
        "(2) confirm why the account changed; (3) confirm the effective date. Approve only after the callback confirms.";

    /// <summary>Shopify-style order webhook. Matches the pre-seeded SO, or
    /// creates it through the ERP when the shop order is unknown and carries
    /// lines plus currency (the ERP mints the number; the idempotency key
    /// comes from the shop order id). Then starts one case per shortfall
    /// item. Repeating the webhook returns the existing cases.</summary>
    public async Task<IReadOnlyList<string>> OnOrderWebhookAsync(
        OrderWebhookInput input, CancellationToken ct = default)
    {
        var so = await _erp.GetSalesOrderAsync(input.SoNumber, ct).ConfigureAwait(false);
        if (so is null)
        {
            if (input.Lines is null || input.Lines.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Unknown sales order {input.SoNumber}: the webhook carried no lines to create it.");
            }

            if (string.IsNullOrWhiteSpace(input.Currency))
            {
                throw new InvalidOperationException(
                    $"Unknown sales order {input.SoNumber}: the webhook carried no currency to create it.");
            }

            var creator = input.Requester
                ?? await DefaultRequesterAsync(ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"No requester for order {input.SoNumber}: the webhook carried none and the ERP has no requester-role user.");
            so = await _erp.CreateSalesOrderAsync(
                input.Currency, input.Lines, RecordSource.P2pAgent, creator,
                input.IdempotencyKey ?? input.SoNumber, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Order webhook created sales order {SoNumber} from shop order {ShopOrder}.",
                so.SoNumber, input.SoNumber);
        }
        await _erp.UpdateSalesOrderStatusAsync(
            so.SoNumber, SalesOrderStatus.Confirmed, RecordSource.P2pAgent,
            input.IdempotencyKey ?? $"{input.ScenarioName}:{so.SoNumber}:confirm", ct).ConfigureAwait(false);
        var requester = input.Requester
            ?? await DefaultRequesterAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No requester for order {so.SoNumber}: the webhook carried none and the ERP has no requester-role user.");
        var shortfalls = await _core.ComputeShortfallAsync(so.SoNumber, ct).ConfigureAwait(false);
        var caseIds = new List<string>();
        foreach (var row in shortfalls.Where(s => s.Shortfall > 0))
        {
            var caseId = CaseStore.MintCaseId(input.ScenarioName, so.SoNumber, row.ItemId);
            var existing = CaseStore.Load(_cacheDir, caseId);
            if (existing is not null)
            {
                caseIds.Add(caseId);
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            var fresh = new CaseFile(
                new Case(caseId, so.SoNumber, row.ItemId, input.ScenarioName,
                    CaseStage.Created, now, now, null, null, null, null, [], [], null, null),
                [], []);
            var work = new CaseWork(fresh);
            work.AddEvent(CaseEventKind.OrderReceived,
                $"Order {so.SoNumber} received for {row.ItemId}.",
                actor: requester);
            if (input.CandidateVendorsByItem is not null
                && input.CandidateVendorsByItem.TryGetValue(row.ItemId, out var candidates))
            {
                WriteCandidates(work, row.ItemId, candidates);
            }

            work.Save(_cacheDir);
            caseIds.Add(caseId);
        }

        // Idempotent enumeration: in-flight cases stay listed even after their shortfall closes (e.g. PO placed).
        foreach (var id in CaseStore.ListCaseIds(_cacheDir))
        {
            if (caseIds.Contains(id, StringComparer.Ordinal))
            {
                continue;
            }

            var file = CaseStore.Load(_cacheDir, id);
            if (file is not null && string.Equals(file.Case.SoNumber, so.SoNumber, StringComparison.Ordinal))
            {
                caseIds.Add(id);
            }
        }

        _logger.LogInformation(
            "Order webhook for {SoNumber} started {Count} case(s).", so.SoNumber, caseIds.Count);
        return caseIds;
    }

    /// <summary>Supplier ACK for the case PO. Defaults to confirming the full
    /// ordered quantity read back from the PO.</summary>
    public async Task<VendorConfirmation> OnSupplierAckAsync(
        SupplierAckInput input, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, input.CaseId)
            ?? throw new InvalidOperationException($"Unknown case {input.CaseId}.");
        var current = file.Case;
        if (current.PoNumber is null)
        {
            throw new InvalidOperationException($"Case {current.CaseId} has no PO to acknowledge.");
        }

        var po = await _erp.GetPurchaseOrderAsync(current.PoNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PO {current.PoNumber} vanished.");
        var stored = await _erp.GetVendorConfirmationAsync(po.PoNumber, ct).ConfigureAwait(false);
        if (stored is not null)
        {
            var replay = new CaseWork(file);
            if (!file.Events.Any(e => e.Kind == CaseEventKind.VendorAcknowledged))
            {
                replay.AddEvent(CaseEventKind.VendorAcknowledged,
                    $"Supplier confirmed PO {po.PoNumber}.",
                    documentType: "PO", documentNumber: po.PoNumber);
            }

            replay.Stage(CaseStage.PoAcknowledged);
            replay.Save(_cacheDir);
            return stored;
        }

        var lines = input.Lines
            ?? po.Lines.Select(l => new VendorConfirmLine(l.PoLineRef, l.QtyOrdered, null)).ToList();
        var confirmation = await _erp.RecordVendorConfirmationAsync(
            po.PoNumber,
            input.ConfirmDate ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime),
            RecordSource.P2pAgent, po.VendorId, lines,
            input.IdempotencyKey ?? $"{current.CaseId}:ack", ct).ConfigureAwait(false);
        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.VendorAcknowledged,
            $"Supplier confirmed PO {po.PoNumber}.",
            documentType: "PO", documentNumber: po.PoNumber);
        work.Stage(CaseStage.PoAcknowledged);
        work.Save(_cacheDir);
        return confirmation;
    }

    /// <summary>Supplier ASN for the case PO. Posts the receipt in the same
    /// idempotent step: one receipt line per carton on its PO line, with the
    /// supplier lot refs from the ASN.</summary>
    public async Task<(AsnDto Asn, ShipmentReceipt? Receipt)> OnAsnAsync(
        SupplierAsnInput input, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, input.CaseId)
            ?? throw new InvalidOperationException($"Unknown case {input.CaseId}.");
        var current = file.Case;
        if (current.PoNumber is null)
        {
            throw new InvalidOperationException($"Case {current.CaseId} has no PO to receive against.");
        }

        if (current.AsnNumber is not null && !string.Equals(current.AsnNumber, input.AsnNumber, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Case {current.CaseId} already carries ASN {current.AsnNumber}; {input.AsnNumber} needs its own case.");
        }

        var po = await _erp.GetPurchaseOrderAsync(current.PoNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PO {current.PoNumber} vanished.");
        var asn = await _erp.GetAsnAsync(input.AsnNumber, ct).ConfigureAwait(false)
            ?? await _erp.CreateAsnAsync(
                input.AsnNumber, po.PoNumber, RecordSource.P2pAgent, po.VendorId,
                input.Cartons.Select(c => new AsnCarton(c.CartonId, c.ItemId, c.UnitsPerCarton, c.LotRef)).ToList(),
                input.IdempotencyKey ?? $"{current.CaseId}:asn", ct).ConfigureAwait(false);

        ShipmentReceipt? receipt = current.ReceiptNumber is null
            ? null
            : await _erp.GetReceiptAsync(current.ReceiptNumber, ct).ConfigureAwait(false);
        if (receipt is null)
        {
            var pr = await _erp.GetPrAsync(po.PrNumber, ct).ConfigureAwait(false);
            var createdBy = pr?.Requester ?? po.CreatedBy;
            var lines = asn.Cartons.Select(c =>
            {
                var poLine = po.Lines.FirstOrDefault(l =>
                    string.Equals(l.ItemId, c.ItemId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"ASN carton {c.CartonId} item {c.ItemId} matches no PO {po.PoNumber} line.");
                return new ReceiptLine(poLine.PoLineRef, c.UnitsPerCarton, 0, 0, c.LotRef, null);
            }).ToList();
            receipt = await _erp.PostReceiptAsync(
                po.PoNumber, input.SupplierPackingSlipNumber, RecordSource.P2pAgent, createdBy,
                lines, $"{current.CaseId}:receipt:{asn.AsnNumber}", ct: ct).ConfigureAwait(false);
        }

        var work = new CaseWork(file);
        if (!file.Events.Any(e => e.Kind == CaseEventKind.AsnRecorded
            && string.Equals(e.DocumentNumber, asn.AsnNumber, StringComparison.Ordinal)))
        {
            work.AddEvent(CaseEventKind.AsnRecorded,
                $"ASN {asn.AsnNumber} recorded with {asn.Cartons.Count} carton(s).",
                documentType: "ASN", documentNumber: asn.AsnNumber);
        }

        if (!file.Events.Any(e => e.Kind == CaseEventKind.ReceiptPosted
            && string.Equals(e.DocumentNumber, receipt.ReceiptNumber, StringComparison.Ordinal)))
        {
            work.AddEvent(CaseEventKind.ReceiptPosted,
                $"Receipt {receipt.ReceiptNumber} posted for {receipt.Lines.Sum(l => l.QtyReceived)} units " +
                $"in {receipt.Lines.Count} lot(s).",
                documentType: "Receipt", documentNumber: receipt.ReceiptNumber);
        }

        work.Patch(current with { AsnNumber = asn.AsnNumber, ReceiptNumber = receipt.ReceiptNumber });
        work.Stage(CaseStage.ReceiptPosted);
        work.Save(_cacheDir);
        return (asn, receipt);
    }

    /// <summary>First dock evaluation for the case receipt. Hold verdicts set
    /// ERP holds plus lot quarantine; human-review verdicts open dock/damage
    /// decisions (damage never auto-quarantines). Follow-up reads go through
    /// the reshot handler.</summary>
    /// <summary>Photo-provenance gate: the dock check evaluates only reads
    /// that name the photo they were transcribed from. Transcription is the
    /// sole producer (it stamps every read with its photo id); anything else
    /// is refused before any verdict, hold, or event is written.</summary>
    private void RequirePhotoProvenance(string caseId, IReadOnlyList<DockCartonRead> reads)
    {
        foreach (var read in reads)
        {
            if (!string.IsNullOrWhiteSpace(read.ImageId))
            {
                continue;
            }

            _logger.LogError(
                "Case {CaseId}: dock read for carton {CartonId} names no photo; refusing reads that did not come from a photo.",
                caseId, read.CartonId);
            throw new InvalidOperationException(
                $"Dock read for carton {read.CartonId} on case {caseId} names no photo; the dock check runs only on photo transcriptions.");
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Verdict>>> OnDockReadsAsync(
        string caseId, IReadOnlyList<DockCartonRead> reads, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        RequirePhotoProvenance(caseId, reads);
        if (file.Events.Any(e => e.Kind == CaseEventKind.DockEvaluated))
        {
            IReadOnlyList<string> asnIds = [];
            var replayPo = file.Case.PoNumber ?? string.Empty;
            IReadOnlyDictionary<string, string> replayLots =
                new Dictionary<string, string>(StringComparer.Ordinal);
            if (file.Case.AsnNumber is not null)
            {
                var replayAsn = await _erp.GetAsnAsync(file.Case.AsnNumber, ct).ConfigureAwait(false);
                if (replayAsn is not null)
                {
                    asnIds = replayAsn.Cartons.Select(c => c.CartonId).ToList();
                    replayPo = replayAsn.PoNumber;
                    replayLots = replayAsn.Cartons
                        .ToDictionary(c => c.CartonId, c => c.LotRef, StringComparer.Ordinal);
                }
            }

            var replayed = _dock.EvaluateDock(asnIds, reads, replayPo, replayLots);
            CaseStore.SaveDock(_cacheDir, new DockEvidence(caseId, reads, replayed, DateTimeOffset.UtcNow));
            return replayed;
        }

        var outcome = await EvaluateCartonsAsync(file, null, reads, isReshot: false, ct).ConfigureAwait(false);
        var work = new CaseWork(CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Case {caseId} vanished during dock evaluation."));
        var counts = CountVerdicts(outcome);
        work.AddEvent(CaseEventKind.DockEvaluated,
            $"Dock evaluated: {counts}.",
            documentType: "Receipt", documentNumber: work.Current.ReceiptNumber);
        work.Stage(CaseStage.DockChecked);
        work.Save(_cacheDir);
        CaseStore.SaveDock(_cacheDir, new DockEvidence(caseId, reads, outcome, DateTimeOffset.UtcNow));
        return outcome;
    }

    /// <summary>Relabel reshot (or any follow-up read set): re-evaluates the
    /// listed cartons, releases passing carton holds plus lot quarantine plus
    /// the held invoice value for the next pay run. Review decisions stay
    /// human-owned and are never auto-closed here.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Verdict>>> OnReshotAsync(
        ReshotInput input, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, input.CaseId)
            ?? throw new InvalidOperationException($"Unknown case {input.CaseId}.");
        RequirePhotoProvenance(input.CaseId, input.Reads);
        var outcome = await EvaluateCartonsAsync(file, input.CartonIds, input.Reads, isReshot: true, ct)
            .ConfigureAwait(false);
        var reloaded = CaseStore.Load(_cacheDir, input.CaseId)
            ?? throw new InvalidOperationException($"Case {input.CaseId} vanished during the reshot.");
        var work = new CaseWork(reloaded);
        foreach (var cartonId in input.CartonIds)
        {
            if (!outcome.TryGetValue(cartonId, out var verdicts))
            {
                continue;
            }

            if (verdicts.All(v => v.Kind == VerdictKind.Pass))
            {
                await ReleaseCartonAsync(work, cartonId, ct).ConfigureAwait(false);
            }
            else
            {
                work.AddEvent(CaseEventKind.Note,
                    $"Reshot for {cartonId} still failing: " +
                    string.Join("; ", verdicts.Where(v => v.Kind != VerdictKind.Pass)
                        .Select(v => $"{v.RuleId} {v.Kind}.")),
                    ruleId: verdicts.First(v => v.Kind != VerdictKind.Pass).RuleId);
            }
        }

        work.Save(_cacheDir);
        var reshot = new HashSet<string>(input.CartonIds, StringComparer.Ordinal);
        var prior = CaseStore.LoadDock(_cacheDir, input.CaseId);
        var mergedReads = (prior?.Reads ?? [])
            .Where(r => !reshot.Contains(r.CartonId))
            .Concat(input.Reads)
            .ToList();
        var mergedVerdicts = new Dictionary<string, IReadOnlyList<Verdict>>(
            prior?.Verdicts ?? new Dictionary<string, IReadOnlyList<Verdict>>(),
            StringComparer.Ordinal);
        foreach (var (cartonId, verdicts) in outcome)
        {
            mergedVerdicts[cartonId] = verdicts;
        }

        CaseStore.SaveDock(_cacheDir, new DockEvidence(input.CaseId, mergedReads, mergedVerdicts, DateTimeOffset.UtcNow));
        return outcome;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<Verdict>>> EvaluateCartonsAsync(
        CaseFile file, IReadOnlyList<string>? cartonSubset, IReadOnlyList<DockCartonRead> reads,
        bool isReshot, CancellationToken ct)
    {
        var current = file.Case;
        if (current.AsnNumber is null)
        {
            throw new InvalidOperationException($"Case {current.CaseId} has no ASN to evaluate reads against.");
        }

        var asn = await _erp.GetAsnAsync(current.AsnNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ASN {current.AsnNumber} vanished.");
        var cartonIds = cartonSubset ?? asn.Cartons.Select(c => c.CartonId).ToList();
        var outcome = _dock.EvaluateDock(
            cartonIds, reads, asn.PoNumber,
            asn.Cartons.ToDictionary(c => c.CartonId, c => c.LotRef, StringComparer.Ordinal));
        if (isReshot)
        {
            return outcome;
        }

        var po = current.PoNumber is null
            ? null
            : await _erp.GetPurchaseOrderAsync(current.PoNumber, ct).ConfigureAwait(false);
        var reviewer = file.Decisions.FirstOrDefault(d =>
                d.Kind == DecisionKind.PrApproval && d.Status == DecisionStatus.Approved)?.DecidedBy
            ?? ResolveRequester(file)
            ?? await DefaultRequesterAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No reviewer for case {current.CaseId}.");
        var work = new CaseWork(file);
        foreach (var (cartonId, verdicts) in outcome)
        {
            var asnCarton = asn.Cartons.FirstOrDefault(c =>
                string.Equals(c.CartonId, cartonId, StringComparison.Ordinal));
            foreach (var verdict in verdicts.Where(v => v.Kind == VerdictKind.Hold))
            {
                if (asnCarton is null)
                {
                    work.AddEvent(CaseEventKind.Note,
                        $"Read carton {cartonId} is not on ASN {asn.AsnNumber}; hold {verdict.RuleId} kept agent-side.",
                        ruleId: verdict.RuleId);
                    continue;
                }

                await HoldCartonAsync(work, po, asnCarton, verdict, ct).ConfigureAwait(false);
            }

            foreach (var verdict in verdicts.Where(v => v.Kind == VerdictKind.HumanReview))
            {
                var kind = string.Equals(verdict.RuleId, _dock.DamageRuleId, StringComparison.Ordinal)
                    ? DecisionKind.DamageReview
                    : DecisionKind.DockReview;
                var images = reads
                    .Where(r => string.Equals(r.CartonId, cartonId, StringComparison.Ordinal))
                    .Select(r => r.ImageId)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var evidence = new List<string> { $"carton:{cartonId}" };
                evidence.AddRange(images.Select(i => $"image:{i}"));
                var decision = work.FindOrCreateDecision(
                    kind, $"{(kind == DecisionKind.DamageReview ? "Damage" : "Dock")} review for {cartonId}",
                    reviewer, "Receipt", current.ReceiptNumber,
                    reason: verdict.Reason, ruleId: verdict.RuleId, evidenceRefs: evidence);
                work.AddEvent(CaseEventKind.ReviewOpened,
                    $"{decision.DecisionId}: {verdict.RuleId} human review for {cartonId}: {verdict.Reason}",
                    ruleId: verdict.RuleId, documentType: "Receipt", documentNumber: current.ReceiptNumber);
            }
        }

        work.Save(_cacheDir);
        return outcome;
    }

    private async Task HoldCartonAsync(
        CaseWork work, PurchaseOrder? po, AsnCarton carton, Verdict verdict,
        CancellationToken ct)
    {
        var current = work.Current;
        var open = await _erp.GetOpenHoldsAsync(null, ct).ConfigureAwait(false);
        var stored = open.FirstOrDefault(h =>
            string.Equals(h.CaseId, current.CaseId, StringComparison.Ordinal)
            && string.Equals(h.CartonId, carton.CartonId, StringComparison.Ordinal)
            && string.Equals(h.RuleId, verdict.RuleId, StringComparison.Ordinal));
        if (stored is null)
        {
            var poLineRef = po?.Lines.FirstOrDefault(l =>
                string.Equals(l.ItemId, carton.ItemId, StringComparison.Ordinal))?.PoLineRef;
            stored = await _erp.SetHoldAsync(
                verdict.RuleId, verdict.Reason, verdict.RuleId, current.CaseId,
                RecordSource.P2pAgent, "Receipt", current.ReceiptNumber,
                poLineRef, carton.LotRef, carton.CartonId,
                $"{current.CaseId}:hold:{carton.CartonId}:{verdict.RuleId}", ct).ConfigureAwait(false);
            await _erp.QuarantineLotAsync(
                carton.LotRef, stored.HoldId, RecordSource.P2pAgent,
                $"{current.CaseId}:quar:{carton.LotRef}", ct).ConfigureAwait(false);
        }

        work.AddEvent(CaseEventKind.HoldSet,
            $"Hold {stored.HoldId} on {carton.CartonId} (lot {carton.LotRef}): {verdict.RuleId}: {verdict.Reason}",
            ruleId: verdict.RuleId, documentType: "Receipt", documentNumber: current.ReceiptNumber);
    }

    private async Task ReleaseCartonAsync(
        CaseWork work, string cartonId, CancellationToken ct, string releaseNote = "after the passing reshot")
    {
        var current = work.Current;
        var open = await _erp.GetOpenHoldsAsync(null, ct).ConfigureAwait(false);
        var holds = open
            .Where(h => string.Equals(h.CaseId, current.CaseId, StringComparison.Ordinal)
                && string.Equals(h.CartonId, cartonId, StringComparison.Ordinal))
            .ToList();
        AsnDto? asn = current.AsnNumber is null
            ? null
            : await _erp.GetAsnAsync(current.AsnNumber, ct).ConfigureAwait(false);
        var cartonUnits = asn?.Cartons.FirstOrDefault(c =>
            string.Equals(c.CartonId, cartonId, StringComparison.Ordinal))?.UnitsPerCarton ?? 0;
        var remaining = (decimal)cartonUnits;
        foreach (var hold in holds)
        {
            await _erp.ReleaseHoldAsync(
                hold.HoldId, RecordSource.P2pAgent, null,
                $"{current.CaseId}:unhold:{hold.HoldId}", ct).ConfigureAwait(false);
            if (hold.LotRef is not null)
            {
                await _erp.ReleaseLotAsync(
                    hold.LotRef, hold.HoldId, RecordSource.P2pAgent,
                    $"{current.CaseId}:unquar:{hold.LotRef}", ct).ConfigureAwait(false);
            }

            work.AddEvent(CaseEventKind.HoldReleased,
                $"Hold {hold.HoldId} on {cartonId} released {releaseNote}.",
                ruleId: hold.RuleId, documentType: "Receipt", documentNumber: current.ReceiptNumber);
        }

        foreach (var key in current.Invoices)
        {
            if (remaining <= 0)
            {
                break;
            }

            var invoice = await _erp.GetInvoiceAsync(key.VendorId, key.SupplierInvoiceNumber, ct).ConfigureAwait(false);
            if (invoice is null)
            {
                continue;
            }

            foreach (var line in invoice.Lines
                .Where(l => l.Disposition == LineDisposition.Held && l.Quantity > 0)
                .ToList())
            {
                if (remaining <= 0)
                {
                    break;
                }

                var release = Math.Min(line.Quantity, remaining);
                remaining -= release;
                await _erp.ReleaseHeldInvoiceLineAsync(
                    key.VendorId, key.SupplierInvoiceNumber, line.PoLineRef, release,
                    RecordSource.P2pAgent,
                    $"{current.CaseId}:unline:{cartonId}:{key.SupplierInvoiceNumber}:{line.PoLineRef}", ct)
                    .ConfigureAwait(false);
                work.AddEvent(CaseEventKind.Note,
                    $"Released {release} held units on invoice {key.SupplierInvoiceNumber}; remainder pays next run.",
                    documentType: "Invoice", documentNumber: $"{key.VendorId}/{key.SupplierInvoiceNumber}");
            }
        }
    }

    private static string CountVerdicts(IReadOnlyDictionary<string, IReadOnlyList<Verdict>> outcome)
    {
        var pass = 0;
        var hold = 0;
        var review = 0;
        foreach (var verdicts in outcome.Values)
        {
            if (verdicts.Any(v => v.Kind == VerdictKind.Hold))
            {
                hold++;
            }
            else if (verdicts.Any(v => v.Kind == VerdictKind.HumanReview))
            {
                review++;
            }
            else
            {
                pass++;
            }
        }

        return $"{pass} passed, {hold} held, {review} in human review";
    }

    /// <summary>Supplier invoice intake, main or tricky-inbox. Duplicates,
    /// bank changes, and missing fields become held lines plus ApReview or
    /// BankVerification decisions; clean invoices get line-level 3-way match
    /// with tolerances recorded per line, then an invoice approval decision
    /// when any value is payable. AP catches are workflow, never regulation.
    /// Link tricky invoices to the case whose vendor flow they affect: a
    /// pending BankVerification parks that case's pre-pay leg.</summary>
    public async Task<InvoiceIntakeOutcome> OnInvoiceAsync(
        SupplierInvoiceInput input, CancellationToken ct = default, bool vendorIdentityCompletion = false)
    {
        // A numberless invoice names nothing to match, pay, query, or card:
        // refuse it with its reason instead of parking a blank AP review.
        if (string.IsNullOrWhiteSpace(input.SupplierInvoiceNumber))
        {
            throw new InvalidOperationException(
                "Supplier invoice number is missing: resend the invoice with its printed number. " +
                "Blank numbers are refused, never held or carded.");
        }

        var file = CaseStore.Load(_cacheDir, input.CaseId)
            ?? throw new InvalidOperationException($"Unknown case {input.CaseId}.");
        if (!vendorIdentityCompletion)
        {
            var waiting = VendorIdentityStore.FindByInvoiceNumber(
                _cacheDir, input.CaseId, input.SupplierInvoiceNumber);
            if (waiting is not null && file.Decisions.Any(d =>
                string.Equals(d.DecisionId, waiting.DecisionId, StringComparison.Ordinal)
                && d.Status == DecisionStatus.Pending))
            {
                _logger.LogError(
                    "Case {CaseId}: invoice '{Invoice}' has a pending vendor identity confirm ({DecisionId}); refusing direct intake.",
                    input.CaseId, input.SupplierInvoiceNumber, waiting.DecisionId);
                throw new InvalidOperationException(
                    $"Invoice '{input.SupplierInvoiceNumber}' has a pending vendor identity confirm " +
                    $"(decision {waiting.DecisionId}); resolve it first instead of intaking directly.");
            }
        }

        var current = file.Case;
        var lines = input.Lines
            .Select(l => new InvoiceLineInput(l.PoLineRef, l.Quantity, l.UnitPrice))
            .ToList();
        if (string.IsNullOrWhiteSpace(input.SupplierInvoiceNumber))
        {
            var blankVendor = input.VendorId ?? string.Empty;
            var blankDoc = $"{blankVendor}/{input.SupplierInvoiceNumber}";
            var blankScope = input.IdempotencyKey ?? $"{current.CaseId}:inv:{blankDoc}";
            var blankBy = await CreatedByAsync(file, ct).ConfigureAwait(false);
            var blankHeld = await _core.MatchInvoiceAsync(
                blankVendor, input.SupplierInvoiceNumber, input.PoNumber,
                RecordSource.P2pAgent, blankBy, lines, ct,
                idempotencyScope: blankScope).ConfigureAwait(false);
            return await FinishHeldIntakeAsync(file,
                "AP-C3: supplier invoice number missing; held for the AP clerk.",
                blankDoc, blankHeld, apReview: true, bankVerification: false, ct).ConfigureAwait(false);
        }

        var vendorId = input.VendorId ?? string.Empty;
        var docKey = $"{vendorId}/{input.SupplierInvoiceNumber}";
        var scope = input.IdempotencyKey ?? $"{current.CaseId}:inv:{docKey}";
        var createdBy = await CreatedByAsync(file, ct).ConfigureAwait(false);

        var duplicate = !string.IsNullOrWhiteSpace(vendorId)
            && await _erp.GetInvoiceAsync(vendorId, input.SupplierInvoiceNumber, ct).ConfigureAwait(false) is not null;
        if (duplicate)
        {
            var duplicateHeld = await _core.MatchInvoiceAsync(
                vendorId, input.SupplierInvoiceNumber, input.PoNumber,
                RecordSource.P2pAgent, createdBy, lines, ct,
                idempotencyScope: scope).ConfigureAwait(false);
            return await FinishHeldIntakeAsync(file,
                "AP-C1: duplicate invoice number; refused, held for the AP clerk.",
                docKey, duplicateHeld, apReview: true, bankVerification: false, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(vendorId) || string.IsNullOrWhiteSpace(input.PoNumber))
        {
            var missingHeld = await _core.MatchInvoiceAsync(
                vendorId, input.SupplierInvoiceNumber, input.PoNumber,
                RecordSource.P2pAgent, createdBy, lines, ct,
                idempotencyScope: scope).ConfigureAwait(false);
            return await FinishHeldIntakeAsync(file,
                "AP-C3: vendor or PO missing; held for the AP clerk.",
                docKey, missingHeld, apReview: true, bankVerification: false, ct).ConfigureAwait(false);
        }

        var po = await _erp.GetPurchaseOrderAsync(input.PoNumber, ct).ConfigureAwait(false);
        if (po is null)
        {
            var unresolvableHeld = await _core.MatchInvoiceAsync(
                vendorId, input.SupplierInvoiceNumber, input.PoNumber,
                RecordSource.P2pAgent, createdBy, lines, ct,
                idempotencyScope: scope).ConfigureAwait(false);
            return await FinishHeldIntakeAsync(file,
                $"AP-C3: PO {input.PoNumber} is unresolvable; held for the AP clerk.",
                docKey, unresolvableHeld, apReview: true, bankVerification: false, ct).ConfigureAwait(false);
        }

        var master = await _erp.GetVendorBankAsync(vendorId, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(input.BankAccountRef)
            && master is not null
            && !string.Equals(master.AccountRef, input.BankAccountRef, StringComparison.Ordinal))
        {
            await _erp.RecordVendorBankChangeAsync(
                vendorId, input.BankAccountRef, RecordSource.P2pAgent, createdBy,
                $"{scope}:bank", ct).ConfigureAwait(false);
            var bankHeld = await _core.MatchInvoiceAsync(
                vendorId, input.SupplierInvoiceNumber, input.PoNumber,
                RecordSource.P2pAgent, createdBy, lines, ct,
                idempotencyScope: scope,
                holdAllReason: "Bank details differ from the vendor master; held for verification.").ConfigureAwait(false);
            var persisted = await _erp.GetInvoiceAsync(
                vendorId, input.SupplierInvoiceNumber, ct).ConfigureAwait(false) is not null;
            if (persisted)
            {
                return await FinishMatchedIntakeAsync(file, bankHeld, bankHold: true, ct).ConfigureAwait(false);
            }

            return await FinishHeldIntakeAsync(file,
                "Bank details differ from the vendor master; held for verification.",
                docKey, bankHeld, apReview: false, bankVerification: true, ct).ConfigureAwait(false);
        }

        var invoice = await _core.MatchInvoiceAsync(
            vendorId, input.SupplierInvoiceNumber, input.PoNumber,
            RecordSource.P2pAgent, createdBy, lines, ct,
            idempotencyScope: scope).ConfigureAwait(false);
        var created = await _erp.GetInvoiceAsync(
            vendorId, input.SupplierInvoiceNumber, ct).ConfigureAwait(false) is not null;
        if (!created)
        {
            return await FinishHeldIntakeAsync(file,
                "Invoice refused by the ERP; held for the AP clerk.",
                docKey, invoice, apReview: true, bankVerification: false, ct).ConfigureAwait(false);
        }

        return await FinishMatchedIntakeAsync(file, invoice, bankHold: false, ct).ConfigureAwait(false);
    }

    private async Task<string> CreatedByAsync(CaseFile file, CancellationToken ct)
    {
        var current = file.Case;
        if (current.PrNumber is not null)
        {
            var pr = await _erp.GetPrAsync(current.PrNumber, ct).ConfigureAwait(false);
            if (pr is not null)
            {
                return pr.Requester;
            }
        }

        return ResolveRequester(file) ?? RecordSource.P2pAgent;
    }

    private async Task<InvoiceIntakeOutcome> FinishHeldIntakeAsync(
        CaseFile file, string reason, string docKey, VendorInvoice held,
        bool apReview, bool bankVerification, CancellationToken ct)
    {
        var heldValue = held.Lines.Sum(l => l.Quantity * l.UnitPrice);
        var currency = string.IsNullOrWhiteSpace(held.Currency) ? null : held.Currency;
        var clerk = await ResolveApClerkAsync(ResolveRequester(file) ?? RecordSource.P2pAgent, ct)
            .ConfigureAwait(false);
        var work = new CaseWork(file);
        work.AddEvent(CaseEventKind.Note,
            $"Invoice {docKey} held: {reason} Held value {heldValue} {held.Currency}.",
            documentType: "Invoice", documentNumber: docKey,
            amount: heldValue, currency: currency);
        Decision? decision = null;
        if (apReview)
        {
            decision = work.FindOrCreateDecision(
                DecisionKind.ApReview, $"AP review for {docKey}", clerk,
                "Invoice", docKey, heldValue, currency, reason,
                evidenceRefs: [$"invoice:{docKey}"]);
        }

        if (bankVerification)
        {
            var vendor = docKey.Split('/')[0];
            decision = work.FindOrCreateDecision(
                DecisionKind.BankVerification, $"Verify bank change for {docKey}", clerk,
                "VendorBank", vendor, heldValue, currency, reason + " " + BankVerifyGuidance,
                evidenceRefs: [$"invoice:{docKey}"]);
        }

        work.Save(_cacheDir);
        return new InvoiceIntakeOutcome(
            string.IsNullOrWhiteSpace(held.VendorId) ? null : new InvoiceKey(held.VendorId, held.SupplierInvoiceNumber),
            Persisted: false, decision?.DecisionId, AcceptedValue: 0, HeldValue: heldValue);
    }

    private async Task<InvoiceIntakeOutcome> FinishMatchedIntakeAsync(
        CaseFile file, VendorInvoice invoice, bool bankHold, CancellationToken ct)
    {
        var current = file.Case;
        var key = new InvoiceKey(invoice.VendorId, invoice.SupplierInvoiceNumber);
        var docKey = $"{key.VendorId}/{key.SupplierInvoiceNumber}";
        var acceptedValue = invoice.Lines
            .Where(l => l.Disposition == LineDisposition.Accepted)
            .Sum(l => l.Quantity * l.UnitPrice);
        var heldValue = invoice.Lines
            .Where(l => l.Disposition == LineDisposition.Held)
            .Sum(l => l.Quantity * l.UnitPrice);
        var clerk = await ResolveApClerkAsync(ResolveRequester(file) ?? RecordSource.P2pAgent, ct)
            .ConfigureAwait(false);
        var work = new CaseWork(file);
        var isCaseInvoice = string.Equals(invoice.PoNumber, current.PoNumber, StringComparison.Ordinal);
        if (isCaseInvoice && !current.Invoices.Contains(key))
        {
            work.Patch(current with { Invoices = current.Invoices.Append(key).ToList() });
        }

        if (!file.Events.Any(e => e.Kind == CaseEventKind.InvoiceCreated
            && string.Equals(e.DocumentNumber, docKey, StringComparison.Ordinal)))
        {
            work.AddEvent(CaseEventKind.InvoiceCreated,
                $"Invoice {docKey} created against PO {invoice.PoNumber}.",
                documentType: "Invoice", documentNumber: docKey);
        }

        work.AddEvent(CaseEventKind.InvoiceMatched,
            $"Invoice {docKey} matched: accepted {acceptedValue}, held {heldValue} {invoice.Currency}. " +
            string.Join("; ", invoice.MatchResults.Select(r => $"{r.PoLineRef ?? "?"}: {(r.Matched ? "match" : r.Message)}")),
            documentType: "Invoice", documentNumber: docKey,
            amount: acceptedValue, currency: invoice.Currency);
        Decision? decision = null;
        if (bankHold)
        {
            decision = work.FindOrCreateDecision(
                DecisionKind.BankVerification, $"Verify bank change for {docKey}", clerk,
                "VendorBank", invoice.VendorId, acceptedValue + heldValue, invoice.Currency,
                "Bank details differ from the vendor master; the master never auto-updates. " + BankVerifyGuidance,
                evidenceRefs: [$"invoice:{docKey}"]);
        }
        else if (acceptedValue > 0 && isCaseInvoice)
        {
            decision = await SubmitInvoiceForApprovalAsync(work, invoice, docKey, acceptedValue, ct)
                .ConfigureAwait(false);
        }
        else if (acceptedValue <= 0 && !isCaseInvoice)
        {
            decision = work.FindOrCreateDecision(
                DecisionKind.ApReview, $"AP review for {docKey}", clerk,
                "Invoice", docKey, heldValue, invoice.Currency,
                "No payable lines; held for the AP clerk.",
                evidenceRefs: [$"invoice:{docKey}"]);
        }

        var priceHeld = invoice.Lines.Any(l =>
            l.Disposition == LineDisposition.Held
            && (l.HoldReason ?? string.Empty).StartsWith("AP-C2", StringComparison.Ordinal));
        if (priceHeld && !bankHold)
        {
            work.FindOrCreateDecision(
                DecisionKind.ApReview, $"AP price review for {docKey}", clerk,
                "Invoice", docKey, heldValue, invoice.Currency,
                "Price beyond tolerance; held for the AP clerk.",
                evidenceRefs: [$"invoice:{docKey}"]);
        }

        if (isCaseInvoice && current.Stage < CaseStage.InvoiceMatched)
        {
            work.Stage(CaseStage.InvoiceMatched);
        }

        work.Save(_cacheDir);
        return new InvoiceIntakeOutcome(key, Persisted: true, decision?.DecisionId, acceptedValue, heldValue);
    }

    private async Task<Decision> SubmitInvoiceForApprovalAsync(
        CaseWork work, VendorInvoice invoice, string docKey, decimal acceptedValue, CancellationToken ct)
    {
        var current = work.Current;
        var total = invoice.Lines.Sum(l => l.Quantity * l.UnitPrice);
        string? requester = null;
        if (current.PrNumber is not null)
        {
            requester = (await _erp.GetPrAsync(current.PrNumber, ct).ConfigureAwait(false))?.Requester;
        }

        var routedTo = await RouteDocumentAsync(
            total, requester is null ? [] : [requester], null, ct).ConfigureAwait(false);
        await _erp.SubmitForApprovalAsync(
            "Invoice", docKey, routedTo, requester ?? RecordSource.P2pAgent,
            $"{current.CaseId}:inv-submit:{invoice.SupplierInvoiceNumber}", ct).ConfigureAwait(false);
        return work.FindOrCreateDecision(
            DecisionKind.InvoiceApproval, $"Approve invoice {docKey}", routedTo,
            "Invoice", docKey, acceptedValue, invoice.Currency,
            $"Invoice {docKey} needs a human approval before posting.");
    }

    /// <summary>Resolves a pending human decision and executes its linked ERP
    /// action: approvals record plus (invoice) post or (payment) post; money
    /// rejections cancel the case; review rejections quarantine plus hold the
    /// carton; bank verification re-verifies the master and releases the
    /// bank-held lines; vendor identity completes the stored intake with the
    /// confirmed vendor on approve or the missing-fields path on reject;
    /// CORP-02 exception grants re-screen with the exception and unblock on
    /// pass, denies keep the rejection. Resolving a decided decision returns
    /// it unchanged.</summary>
    public async Task<Decision> ResolveDecisionAsync(
        string caseId, string decisionId, bool approved, string decidedBy,
        string? comment = null, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var decision = file.Decisions.FirstOrDefault(d =>
            string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unknown decision {decisionId} on case {caseId}.");
        if (decision.Status != DecisionStatus.Pending)
        {
            _logger.LogInformation(
                "Decision {DecisionId} on case {CaseId} already {Status}; replay returns it unchanged.",
                decisionId, caseId, decision.Status);
            return decision;
        }

        if (decision.Kind == DecisionKind.VendorIdentity)
        {
            var completed = await CompleteVendorIdentityIntakeAsync(caseId, decision, approved, ct)
                .ConfigureAwait(false);
            if (completed)
            {
                file = CaseStore.Load(_cacheDir, caseId)
                    ?? throw new InvalidOperationException($"Case {caseId} vanished during vendor identity completion.");
                decision = file.Decisions.FirstOrDefault(d =>
                    string.Equals(d.DecisionId, decision.DecisionId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"Decision {decision.DecisionId} vanished during vendor identity completion.");
            }
        }

        var work = new CaseWork(file);
        var updated = decision.Kind switch
        {
            DecisionKind.PrApproval => await ResolvePrApprovalAsync(work, decision, approved, decidedBy, comment, ct)
                .ConfigureAwait(false),
            DecisionKind.PoApproval => await ResolvePoApprovalAsync(work, decision, approved, decidedBy, comment, ct)
                .ConfigureAwait(false),
            DecisionKind.InvoiceApproval => await ResolveInvoiceApprovalAsync(work, decision, approved, decidedBy, comment, ct)
                .ConfigureAwait(false),
            DecisionKind.PaymentApproval => await ResolvePaymentApprovalAsync(work, decision, approved, decidedBy, comment, ct)
                .ConfigureAwait(false),
            DecisionKind.DockReview or DecisionKind.DamageReview =>
                await ResolveDockReviewAsync(work, decision, approved, decidedBy, comment, ct).ConfigureAwait(false),
            DecisionKind.ApReview => ResolveApReview(work, decision, approved, decidedBy, comment),
            DecisionKind.BankVerification => await ResolveBankVerificationAsync(work, decision, approved, decidedBy, comment, ct)
                .ConfigureAwait(false),
            DecisionKind.VendorIdentity => ResolveVendorIdentity(work, decision, approved, decidedBy, comment),
            DecisionKind.ExceptionApproval => await ResolveCorpExceptionAsync(work, decision, approved, decidedBy, comment, ct)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Decision kind {decision.Kind} is not resolvable by the runner."),
        };
        work.Save(_cacheDir);
        return updated;
    }

    /// <summary>Settles pending case cards linked to an ERP document that was
    /// just decided outside the Decisions page (the ERP record page). Each
    /// linked approval decision resolves through the same resolver the
    /// Decisions page uses; the ERP re-call is a same-actor no-op. Returns
    /// the settled decision ids plus one note per decision that could not
    /// settle, so the committed ERP decision still stands with its warnings
    /// surfaced.</summary>
    public async Task<(IReadOnlyList<string> Settled, IReadOnlyList<string> Notes)> SettleErpDecisionsAsync(
        string documentType, string documentNumber, bool approved, string decidedBy,
        string? comment = null, CancellationToken ct = default)
    {
        var settled = new List<string>();
        var notes = new List<string>();
        foreach (var caseId in CaseStore.ListCaseIds(_cacheDir))
        {
            var file = CaseStore.Load(_cacheDir, caseId);
            if (file is null)
            {
                continue;
            }

            var linked = file.Decisions
                .Where(d => d.Status == DecisionStatus.Pending
                    && d.Kind is DecisionKind.PrApproval or DecisionKind.PoApproval
                        or DecisionKind.InvoiceApproval or DecisionKind.PaymentApproval
                    && string.Equals(d.DocumentType, documentType, StringComparison.Ordinal)
                    && string.Equals(d.DocumentNumber, documentNumber, StringComparison.Ordinal))
                .ToList();
            foreach (var decision in linked)
            {
                try
                {
                    await ResolveDecisionAsync(
                        caseId, decision.DecisionId, approved, decidedBy, comment, ct).ConfigureAwait(false);
                    settled.Add($"{caseId}:{decision.DecisionId}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Linked decision {DecisionId} on case {CaseId} did not settle.", decision.DecisionId, caseId);
                    notes.Add($"Card {decision.DecisionId} on case {caseId} did not settle: {ex.Message}");
                }
            }
        }

        return (settled, notes);
    }

    private async Task<Decision> ResolvePrApprovalAsync(
        CaseWork work, Decision decision, bool approved, string decidedBy, string? comment,
        CancellationToken ct)
    {
        var doc = RequireDoc(decision);
        await _erp.RecordApprovalAsync(
            "PR", doc, decidedBy, approved, comment,
            $"{work.Current.CaseId}:{decision.DecisionId}:approve", ct).ConfigureAwait(false);
        var updated = work.Decide(decision, approved, decidedBy, comment);
        if (approved)
        {
            work.AddEvent(CaseEventKind.PrApproved,
                $"PR {doc} approved by {decidedBy}.",
                documentType: "PR", documentNumber: doc);
            work.Stage(CaseStage.PrApproved);
        }
        else
        {
            work.AddEvent(CaseEventKind.PrRejected,
                $"PR {doc} rejected by {decidedBy}: {comment ?? "no reason given"}.",
                documentType: "PR", documentNumber: doc);
            work.Cancel($"PR {doc} rejected; case cancelled.");
        }

        return updated;
    }

    private async Task<Decision> ResolvePoApprovalAsync(
        CaseWork work, Decision decision, bool approved, string decidedBy, string? comment,
        CancellationToken ct)
    {
        var doc = RequireDoc(decision);
        await _erp.RecordApprovalAsync(
            "PO", doc, decidedBy, approved, comment,
            $"{work.Current.CaseId}:{decision.DecisionId}:approve", ct).ConfigureAwait(false);
        var updated = work.Decide(decision, approved, decidedBy, comment);
        if (approved)
        {
            work.AddEvent(CaseEventKind.PoApproved,
                $"PO {doc} approved by {decidedBy}.",
                documentType: "PO", documentNumber: doc);
        }
        else
        {
            work.AddEvent(CaseEventKind.Note,
                $"PO {doc} rejected by {decidedBy}: {comment ?? "no reason given"}.",
                documentType: "PO", documentNumber: doc);
            work.Cancel($"PO {doc} rejected; case cancelled.");
        }

        return updated;
    }

    private async Task<Decision> ResolveInvoiceApprovalAsync(
        CaseWork work, Decision decision, bool approved, string decidedBy, string? comment,
        CancellationToken ct)
    {
        var doc = RequireDoc(decision);
        await _erp.RecordApprovalAsync(
            "Invoice", doc, decidedBy, approved, comment,
            $"{work.Current.CaseId}:{decision.DecisionId}:approve", ct).ConfigureAwait(false);
        var updated = work.Decide(decision, approved, decidedBy, comment);
        if (!approved)
        {
            work.AddEvent(CaseEventKind.Note,
                $"Invoice {doc} rejected by {decidedBy}: {comment ?? "no reason given"}.",
                documentType: "Invoice", documentNumber: doc);
            work.Cancel($"Invoice {doc} rejected; case cancelled.");
            return updated;
        }

        var (vendorId, invoiceNumber) = SplitInvoiceDoc(doc);
        var posted = await _erp.PostInvoiceAsync(
            vendorId, invoiceNumber, decidedBy,
            $"{work.Current.CaseId}:{decision.DecisionId}:post", ct: ct).ConfigureAwait(false);
        work.AddEvent(CaseEventKind.Note,
            $"Invoice {doc} approved by {decidedBy} and posted as voucher {posted.Voucher}.",
            documentType: "Invoice", documentNumber: doc);
        return updated;
    }

    private async Task<Decision> ResolvePaymentApprovalAsync(
        CaseWork work, Decision decision, bool approved, string decidedBy, string? comment,
        CancellationToken ct)
    {
        var doc = RequireDoc(decision);
        await _erp.RecordApprovalAsync(
            "Payment", doc, decidedBy, approved, comment,
            $"{work.Current.CaseId}:{decision.DecisionId}:approve", ct).ConfigureAwait(false);
        var updated = work.Decide(decision, approved, decidedBy, comment);
        if (!approved)
        {
            work.AddEvent(CaseEventKind.Note,
                $"Payment {doc} rejected by {decidedBy}: {comment ?? "no reason given"}.",
                documentType: "Payment", documentNumber: doc);
            work.Cancel($"Payment {doc} rejected; case cancelled.");
            return updated;
        }

        var posted = await _erp.PostPaymentAsync(
            doc, decidedBy, $"{work.Current.CaseId}:{decision.DecisionId}:post", ct: ct).ConfigureAwait(false);
        var current = work.Current;
        work.Patch(current with { PaymentNumbers = current.PaymentNumbers.Append(posted.PaymentNumber).ToList() });
        var total = posted.Lines.Sum(l => l.Amount);
        work.AddEvent(CaseEventKind.Note,
            $"Payment {doc} approved by {decidedBy}.",
            documentType: "Payment", documentNumber: doc);
        work.AddEvent(CaseEventKind.PaymentPosted,
            $"Payment {posted.PaymentNumber} posted for {total} {posted.Currency}.",
            documentType: "Payment", documentNumber: posted.PaymentNumber,
            amount: total, currency: posted.Currency);
        return updated;
    }

    private async Task<Decision> ResolveDockReviewAsync(
        CaseWork work, Decision decision, bool approved, string decidedBy, string? comment,
        CancellationToken ct)
    {
        var updated = work.Decide(decision, approved, decidedBy, comment);
        var current = work.Current;
        if (approved)
        {
            var acceptedCarton = CartonFromEvidence(decision);
            if (acceptedCarton is not null)
            {
                await ReleaseCartonAsync(work, acceptedCarton, ct, "after review approval").ConfigureAwait(false);
            }

            work.AddEvent(CaseEventKind.ReviewClosed,
                $"{decision.DecisionId} accepted by {decidedBy}: {comment ?? "confirmed"}.",
                ruleId: decision.RuleId, documentType: "Receipt", documentNumber: current.ReceiptNumber);
            return updated;
        }

        var cartonId = CartonFromEvidence(decision);
        AsnDto? asn = current.AsnNumber is null
            ? null
            : await _erp.GetAsnAsync(current.AsnNumber, ct).ConfigureAwait(false);
        var lot = asn?.Cartons.FirstOrDefault(c =>
            string.Equals(c.CartonId, cartonId, StringComparison.Ordinal))?.LotRef;
        var rule = decision.RuleId ?? decision.Kind.ToString();
        if (lot is not null)
        {
            var hold = await _erp.SetHoldAsync(
                rule, $"Review {decision.DecisionId} rejected by {decidedBy}: {comment ?? "goods failed review"}.",
                rule, current.CaseId, RecordSource.P2pAgent, "Receipt", current.ReceiptNumber,
                null, lot, cartonId, $"{current.CaseId}:{decision.DecisionId}:hold", ct).ConfigureAwait(false);
            await _erp.QuarantineLotAsync(
                lot, hold.HoldId, RecordSource.P2pAgent,
                $"{current.CaseId}:{decision.DecisionId}:quar", ct).ConfigureAwait(false);
            work.AddEvent(CaseEventKind.HoldSet,
                $"Hold {hold.HoldId} on {cartonId} (lot {lot}): review rejected, goods quarantined.",
                ruleId: rule, documentType: "Receipt", documentNumber: current.ReceiptNumber);
        }

        work.AddEvent(CaseEventKind.ReviewClosed,
            $"{decision.DecisionId} rejected by {decidedBy}: {comment ?? "goods failed review"}.",
            ruleId: decision.RuleId, documentType: "Receipt", documentNumber: current.ReceiptNumber);
        return updated;
    }

    private Decision ResolveApReview(
        CaseWork work, Decision decision, bool approved, string decidedBy, string? comment)
    {
        var updated = work.Decide(decision, approved, decidedBy, comment);
        work.AddEvent(CaseEventKind.ReviewClosed,
            $"{decision.DecisionId} {(approved ? "resolved" : "rejected")} by {decidedBy}: {comment ?? decision.Reason}.",
            documentType: "Invoice", documentNumber: decision.DocumentNumber);
        return updated;
    }

    private async Task<Decision> ResolveBankVerificationAsync(
        CaseWork work, Decision decision, bool approved, string decidedBy, string? comment,
        CancellationToken ct)
    {
        var vendorId = RequireDoc(decision);
        var updated = work.Decide(decision, approved, decidedBy, comment);
        if (!approved)
        {
            work.AddEvent(CaseEventKind.Note,
                $"Bank change for {vendorId} rejected by {decidedBy}; the master stays unverified.",
                documentType: "VendorBank", documentNumber: vendorId);
            return updated;
        }

        await _erp.VerifyVendorBankAsync(
            vendorId, decidedBy, $"{work.Current.CaseId}:{decision.DecisionId}:verify", ct).ConfigureAwait(false);
        var invoiceRef = decision.EvidenceRefs
            .FirstOrDefault(r => r.StartsWith("invoice:", StringComparison.Ordinal))?["invoice:".Length..];
        if (invoiceRef is not null)
        {
            var (refVendor, refNumber) = SplitInvoiceDoc(invoiceRef);
            var invoice = await _erp.GetInvoiceAsync(refVendor, refNumber, ct).ConfigureAwait(false);
            if (invoice is not null)
            {
                foreach (var line in invoice.Lines
                    .Where(l => l.Disposition == LineDisposition.Held && l.Quantity > 0)
                    .ToList())
                {
                    await _erp.ReleaseHeldInvoiceLineAsync(
                        refVendor, refNumber, line.PoLineRef, line.Quantity, decidedBy,
                        $"{work.Current.CaseId}:{decision.DecisionId}:unline:{line.PoLineRef}", ct)
                        .ConfigureAwait(false);
                }
            }
        }

        work.AddEvent(CaseEventKind.Note,
            $"Bank change for {vendorId} verified out of band by {decidedBy}; held lines released.",
            documentType: "VendorBank", documentNumber: vendorId);
        return updated;
    }

    /// <summary>Resolves a CORP-02 exception card. A deny keeps the rejection
    /// (vendor stays blocked, no award). A grant presence-checks the named
    /// approver plus reason, re-screens the vendor with the exception, and on
    /// pass unblocks the vendor so the award can continue; a re-screen still
    /// blocked on other rules refuses loudly and leaves the card pending.
    /// Every outcome timelines with rule, vendor, and approver.</summary>
    private async Task<Decision> ResolveCorpExceptionAsync(
        CaseWork work, Decision decision, bool approved, string decidedBy, string? comment,
        CancellationToken ct)
    {
        var ruleId = decision.RuleId ?? string.Empty;
        if (!string.Equals(ruleId, CorpExceptionFlow.CorpOriginCapRuleId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Decision {decision.DecisionId} is an ExceptionApproval for rule '{ruleId}'; " +
                $"only {CorpExceptionFlow.CorpOriginCapRuleId} exceptions are supported.");
        }

        var vendorId = RequireDoc(decision);
        if (!approved)
        {
            var denied = work.Decide(decision, false, decidedBy, comment);
            work.AddEvent(CaseEventKind.Note,
                $"{ruleId} exception for vendor {vendorId} denied by {decidedBy}: " +
                $"{comment ?? "no reason given"}. Rejection stands.",
                ruleId: ruleId,
                documentType: CorpExceptionFlow.VendorDocumentType,
                documentNumber: vendorId);
            return denied;
        }

        if (string.IsNullOrWhiteSpace(decidedBy))
        {
            throw new InvalidOperationException(
                $"Decision {decision.DecisionId} grants {ruleId} exception for vendor {vendorId} " +
                "with no approver; a named approver is required.");
        }

        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new InvalidOperationException(
                $"Decision {decision.DecisionId} grants {ruleId} exception for vendor {vendorId} " +
                "with no reason; a reason is required.");
        }

        var prNumber = work.Current.PrNumber
            ?? throw new InvalidOperationException(
                $"Case {work.Current.CaseId} has no PR to re-screen vendor {vendorId} for.");
        var grant = new CorpExceptionGrant(
            ruleId, vendorId, decidedBy, DateTimeOffset.UtcNow, comment);
        var excused = await _core.RescreenVendorWithExceptionAsync(
            prNumber, vendorId, grant, ct).ConfigureAwait(false);
        if (!excused.Passed)
        {
            var blocks = excused.Verdicts
                .Where(v => v.Kind == VerdictKind.Block)
                .Select(v => v.RuleId)
                .Where(id => !string.Equals(id, ComplianceVerdictIds.VendorBlock, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            throw new InvalidOperationException(
                $"Decision {decision.DecisionId} cannot grant {ruleId} exception for vendor {vendorId}: " +
                $"re-screen still blocks on {string.Join(", ", blocks)} " +
                $"(the exception excuses only {ruleId}).");
        }

        var granted = work.Decide(decision, true, decidedBy, comment);
        work.AddEvent(CaseEventKind.Note,
            $"{ruleId} exception for vendor {vendorId} granted by {decidedBy}: {comment}. " +
            "Re-screen passed with exception; the award may proceed.",
            ruleId: ruleId,
            documentType: CorpExceptionFlow.VendorDocumentType,
            documentNumber: vendorId);
        return granted;
    }

    private static string RequireDoc(Decision decision) =>
        decision.DocumentNumber ?? throw new InvalidOperationException(
            $"Decision {decision.DecisionId} carries no document number.");

    private static (string VendorId, string InvoiceNumber) SplitInvoiceDoc(string doc)
    {
        var parts = doc.Split('/', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new InvalidOperationException($"Invoice document key '{doc}' is not vendor/number.");
        }

        return (parts[0], parts[1]);
    }

    /// <summary>Answers a why-question about a decision from case evidence
    /// only (the UI "ask the agent why"). One cached model call.</summary>
    public async Task<string> ExplainDecisionAsync(
        string caseId, string decisionId, string question, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var decision = file.Decisions.FirstOrDefault(d =>
            string.Equals(d.DecisionId, decisionId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Unknown decision {decisionId} on case {caseId}.");
        var related = file.Events
            .Where(e => string.Equals(e.DocumentNumber, decision.DocumentNumber, StringComparison.Ordinal))
            .TakeLast(10)
            .Select(e => e.Summary)
            .ToList();
        var evidence =
            $"Decision {decision.DecisionId} ({decision.Kind}, {decision.Status}): {decision.Title}. " +
            $"Requested of {decision.RequestedTo} at {decision.RequestedAt:u}. " +
            $"Document {decision.DocumentType} {decision.DocumentNumber}. " +
            $"Reason: {decision.Reason ?? "none recorded"}. Rule: {decision.RuleId ?? "none"}. " +
            $"Timeline: {string.Join(" | ", related)}";
        var result = await ResilientCalls.CompleteWithRetryAsync(
            _models,
            new ModelRequest(question, $"Answer using only this case evidence: {evidence}", MaxTokens: 300),
            _logger, $"explanation for {decisionId}", ct).ConfigureAwait(false);
        return result.Text;
    }
}

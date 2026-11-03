// Simulation endpoints: external inputs in only (order webhooks, supplier
// ACKs, ASNs, dock photos, invoice photos or EDI lines, vendor replies).
// Every POST answers from its ERP write the moment it lands, then the
// agent works off the request: the outside party gets an instant
// confirmation and agent work never blocks the button. Background work
// runs one task per case (gated, logged, never silent), and the panels
// poll the local case-progress read for dock and invoice outcomes.
// Photos are transcribed by Spark (cached) and judged by C#; there is no
// reads-without-photo path.
using System.Collections.Concurrent;
using Gate.Agent;
using Gate.Contracts;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gate.Web;

public sealed record DockPhotosRequest(string CaseId, List<DockPhoto> Photos);

public sealed record ReshotPhotosRequest(string CaseId, List<string> CartonIds, List<DockPhoto> Photos);

public sealed record ReshotImageRequest(string CaseId, string CartonId);

public sealed record InvoicePhotosRequest(string CaseId, List<InvoicePhoto> Photos);

/// <summary>One simulated carton problem on a shipment notice or a dock
/// view rebuild: the carton plus the kind (a DockProblemKinds wire name).
/// Kinds validate against the canonical names; unknown kinds are refused
/// with their reason, never silently dropped.</summary>
public sealed record DockProblemInput(string CartonId, string Kind);

public sealed record DockRebuildViewsRequest(string CaseId, List<DockProblemInput>? Problems);

/// <summary>Supplier ASN submission plus the per-carton simulated problems
/// from the portal picker. Problems is optional: payloads without it bind
/// exactly like the plain ASN input.</summary>
public sealed record SupplierAsnWithProblemsRequest(
    string CaseId,
    string? AsnNumber,
    string? SupplierPackingSlipNumber,
    List<AsnCartonInput>? Cartons,
    string? IdempotencyKey,
    List<DockProblemInput>? Problems);

/// <summary>Instant shop confirmation: the ERP minted the order and opened
/// these cases. The agent advances each case in the background.</summary>
public sealed record ShopOrderAccepted(
    bool Ok, string? Error, IReadOnlyList<string> CaseIds);

/// <summary>Instant shipment-notice confirmation: the ASN and receipt are
/// recorded. Views build and the case advances in the background.</summary>
public sealed record SupplierAsnAccepted(
    bool Ok, string? Error, string? AsnNumber, string? ReceiptNumber);

/// <summary>Instant photo confirmation: the photos are in hand and read in
/// the background. The panel polls case progress for the outcome.</summary>
public sealed record PhotosAccepted(bool Ok, string? Error, int PhotoCount);

/// <summary>Instant rebuild confirmation: the problems validated and the
/// rebuild runs in the background. The panel polls the dock options for
/// the new files.</summary>
public sealed record RebuildAccepted(
    bool Ok, string? Error, string CaseId, IReadOnlyList<DockProblemForm> Problems);

/// <summary>One non-passing carton verdict for the progress poll.</summary>
public sealed record CaseProgressIssue(string CartonId, string RuleId, string Kind, string Reason);

/// <summary>Newest dock outcome on the case, when it lands after since.</summary>
public sealed record CaseProgressDock(
    DateTimeOffset ReceivedAt,
    IReadOnlyList<string> Cartons,
    IReadOnlyList<CaseProgressIssue> Issues);

/// <summary>One case invoice newer than since, with its match values.</summary>
public sealed record CaseProgressInvoice(
    string VendorId,
    string SupplierInvoiceNumber,
    string Currency,
    decimal AcceptedValue,
    decimal HeldValue,
    DateTimeOffset ReceivedAt);

/// <summary>Local progress read for the outside-world panels: the newest
/// dock outcome plus invoices and timeline lines newer than since.
/// Empty sections mean nothing new yet, never an error.</summary>
public sealed record CaseProgress(
    CaseProgressDock? Dock,
    IReadOnlyList<CaseProgressInvoice> Invoices,
    IReadOnlyList<string> Events);

public static class SimulationEndpoints
{
    /// <summary>One gate per case: two background tasks never advance the
    /// same case at once, however fast the buttons are clicked.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CaseGates = new();

    /// <summary>Runs agent work off the request on its own scope: a fresh
    /// runner per task, gated per case, decoupled from request
    /// cancellation, and logged with the case on failure, never silent.</summary>
    public static void RunAgentBackground(
        IServiceScopeFactory scopes, ILogger log, string caseId, string what,
        Func<CaseRunner, CancellationToken, Task> work)
    {
        _ = Task.Run(async () =>
        {
            var gate = CaseGates.GetOrAdd(caseId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var scope = scopes.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<CaseRunner>();
                await work(runner, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Background {What} for case {CaseId} failed.", what, caseId);
            }
            finally
            {
                gate.Release();
            }
        });
    }

    public static void MapSimulation(this WebApplication app)
    {
        app.MapPost("/simulation/shopify/order", async (
            OrderWebhookInput input, CaseRunner runner, IServiceScopeFactory scopes,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var accepted = await AcceptShopOrderAsync(input, runner, ct).ConfigureAwait(false);
            if (!accepted.Ok)
            {
                return Results.BadRequest(new { error = accepted.Error });
            }

            var log = logs.CreateLogger("SimulationEndpoints");
            foreach (var caseId in accepted.CaseIds)
            {
                RunAgentBackground(scopes, log, caseId, "order advance",
                    (r, background) => r.AdvanceAsync(caseId, background));
            }

            return Results.Ok(new { accepted.CaseIds });
        });

        app.MapPost("/simulation/supplier/ack", async (
            SupplierAckInput input, CaseRunner runner, IServiceScopeFactory scopes,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            VendorConfirmation ack;
            try
            {
                ack = await runner.OnSupplierAckAsync(input, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            RunAgentBackground(
                scopes, logs.CreateLogger("SimulationEndpoints"), input.CaseId, "ack advance",
                (r, background) => r.AdvanceAsync(input.CaseId, background));
            return Results.Ok(new { ack.PoNumber });
        });

        app.MapPost("/simulation/supplier/asn", async (
            SupplierAsnWithProblemsRequest request, CaseRunner runner, IServiceScopeFactory scopes,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var accepted = await AcceptSupplierAsnAsync(request, runner, ct).ConfigureAwait(false);
            if (!accepted.Ok)
            {
                return Results.BadRequest(new { error = accepted.Error });
            }

            RunAgentBackground(
                scopes, logs.CreateLogger("SimulationEndpoints"), request.CaseId.Trim(), "asn views and advance",
                (r, background) => BuildAndAdvanceAsnAsync(r, request.CaseId.Trim(), background));
            return Results.Ok(new
            {
                asnNumber = accepted.AsnNumber,
                receiptNumber = accepted.ReceiptNumber,
            });
        });

        app.MapPost("/simulation/dock/rebuild-views", (
            DockRebuildViewsRequest request, IServiceScopeFactory scopes,
            ILoggerFactory logs) =>
        {
            var accepted = AcceptRebuildAsync(request);
            if (!accepted.Ok)
            {
                return Results.BadRequest(new { error = accepted.Error });
            }

            var problems = ToSimulatedProblems(accepted.Problems);
            RunAgentBackground(
                scopes, logs.CreateLogger("SimulationEndpoints"), accepted.CaseId, "dock rebuild",
                (r, background) => r.RebuildDockViewsAsync(
                    new RebuildDockViewsInput(accepted.CaseId, problems), background));
            return Results.Ok(new { accepted = true });
        });

        app.MapPost("/simulation/dock/reshot-image", async (
            ReshotImageRequest request, CaseRunner runner, CancellationToken ct) =>
        {
            try
            {
                var minted = await runner.MintReshotImageAsync(
                    request.CaseId, request.CartonId, ct).ConfigureAwait(false);
                return Results.Ok(new
                {
                    minted.FileName,
                    path = minted.Path.ToString(),
                    minted.FallbackReason,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/simulation/dock/photos", (
            DockPhotosRequest request, IServiceScopeFactory scopes,
            ILoggerFactory logs) =>
        {
            var accepted = AcceptDockPhotosAsync(request);
            if (!accepted.Ok)
            {
                return Results.BadRequest(new { error = accepted.Error });
            }

            RunAgentBackground(
                scopes, logs.CreateLogger("SimulationEndpoints"), request.CaseId.Trim(), "dock photos",
                (r, background) => JudgeDockPhotosAsync(r, request.CaseId.Trim(), request.Photos, background));
            return Results.Ok(new { accepted = true, accepted.PhotoCount });
        });

        app.MapPost("/simulation/dock/reshot", (
            ReshotPhotosRequest request, IServiceScopeFactory scopes,
            ILoggerFactory logs) =>
        {
            var accepted = AcceptReshotAsync(request);
            if (!accepted.Ok)
            {
                return Results.BadRequest(new { error = accepted.Error });
            }

            RunAgentBackground(
                scopes, logs.CreateLogger("SimulationEndpoints"), request.CaseId.Trim(), "dock reshot",
                (r, background) => JudgeReshotAsync(
                    r, request.CaseId.Trim(), request.CartonIds, request.Photos, background));
            return Results.Ok(new { accepted = true, accepted.PhotoCount });
        });

        app.MapPost("/simulation/supplier/invoice", async (
            SupplierInvoiceInput input, CaseRunner runner, IServiceScopeFactory scopes,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            InvoiceIntakeOutcome outcome;
            try
            {
                outcome = await runner.OnInvoiceAsync(input, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            RunAgentBackground(
                scopes, logs.CreateLogger("SimulationEndpoints"), input.CaseId, "invoice advance",
                (r, background) => r.AdvanceAsync(input.CaseId, background));
            return Results.Ok(new
            {
                invoice = outcome.Key is null
                    ? null
                    : $"{outcome.Key.VendorId}/{outcome.Key.SupplierInvoiceNumber}",
                outcome.Persisted,
                outcome.AcceptedValue,
                outcome.HeldValue,
            });
        });

        app.MapPost("/simulation/supplier/invoice-photo", (
            InvoicePhotosRequest request, IServiceScopeFactory scopes,
            ILoggerFactory logs) =>
        {
            var accepted = AcceptInvoicePhotoAsync(request);
            if (!accepted.Ok)
            {
                return Results.BadRequest(new { error = accepted.Error });
            }

            RunAgentBackground(
                scopes, logs.CreateLogger("SimulationEndpoints"), request.CaseId.Trim(), "invoice photo",
                (r, background) => IntakeInvoicePhotoAsync(
                    r, request.CaseId.Trim(), request.Photos, background));
            return Results.Ok(new { accepted = true, accepted.PhotoCount });
        });

        app.MapPost("/simulation/supplier/email-reply", async (
            VendorReplyInput input, CaseRunner runner, IServiceScopeFactory scopes,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            VendorEmail reply;
            try
            {
                reply = await runner.OnVendorReplyAsync(input, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            RunAgentBackground(
                scopes, logs.CreateLogger("SimulationEndpoints"), input.CaseId, "reply advance",
                (r, background) => r.AdvanceAsync(input.CaseId, background));
            return Results.Ok(new { reply.EmailId });
        });

        app.MapGet("/simulation/case-progress", async (
            string caseId, string? since, CaseCacheStore cases, IErpAdapter erp,
            CancellationToken ct) =>
        {
            var progress = await CaseProgressAsync(cases, erp, caseId, since, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                dock = progress.Dock is null
                    ? null
                    : new
                    {
                        receivedAt = progress.Dock.ReceivedAt,
                        progress.Dock.Cartons,
                        issues = progress.Dock.Issues.Select(i => new
                        {
                            cartonId = i.CartonId,
                            ruleId = i.RuleId,
                            kind = i.Kind,
                            reason = i.Reason,
                        }).ToList(),
                    },
                invoices = progress.Invoices.Select(i => new
                {
                    vendorId = i.VendorId,
                    supplierInvoiceNumber = i.SupplierInvoiceNumber,
                    currency = i.Currency,
                    acceptedValue = i.AcceptedValue,
                    heldValue = i.HeldValue,
                    receivedAt = i.ReceivedAt,
                }).ToList(),
                events = progress.Events,
            });
        });
    }

    /// <summary>Shop intake without the advance: mints the sales order and
    /// opens the cases, then answers. The agent advances each case in the
    /// background.</summary>
    public static async Task<ShopOrderAccepted> AcceptShopOrderAsync(
        OrderWebhookInput? input, CaseRunner runner, CancellationToken ct = default)
    {
        if (input is null)
        {
            return new ShopOrderAccepted(false, "No order was provided.", []);
        }

        try
        {
            var caseIds = await runner.OnOrderWebhookAsync(input, ct).ConfigureAwait(false);
            return new ShopOrderAccepted(true, null, caseIds);
        }
        catch (InvalidOperationException ex)
        {
            return new ShopOrderAccepted(false, ex.Message, []);
        }
    }

    /// <summary>Shipment-notice intake without the views or the advance:
    /// validates the picker problems and records the ASN, then answers.
    /// Views build and the case advances in the background.</summary>
    public static async Task<SupplierAsnAccepted> AcceptSupplierAsnAsync(
        SupplierAsnWithProblemsRequest? request, CaseRunner runner,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CaseId))
        {
            return new SupplierAsnAccepted(
                false, "Case is required for a shipment notice.", null, null);
        }

        var invalid = ValidateDockProblems(request.Problems, out var problems);
        if (invalid is not null)
        {
            return new SupplierAsnAccepted(false, invalid, null, null);
        }

        var input = new SupplierAsnInput(
            request.CaseId.Trim(),
            request.AsnNumber?.Trim() ?? string.Empty,
            request.SupplierPackingSlipNumber?.Trim() ?? string.Empty,
            request.Cartons ?? [],
            request.IdempotencyKey,
            ToSimulatedProblems(problems));
        try
        {
            var (asn, receipt) = await runner.OnAsnAsync(input, ct).ConfigureAwait(false);
            return new SupplierAsnAccepted(true, null, asn.AsnNumber, receipt?.ReceiptNumber);
        }
        catch (InvalidOperationException ex)
        {
            return new SupplierAsnAccepted(false, ex.Message, null, null);
        }
    }

    /// <summary>Background half of the ASN: labels the pallet views from
    /// the recorded notice (minted PO plus ASN lots) and advances.</summary>
    public static async Task BuildAndAdvanceAsnAsync(
        CaseRunner runner, string caseId, CancellationToken ct = default)
    {
        await runner.BuildDockImagesAsync(caseId, ct).ConfigureAwait(false);
        await runner.AdvanceAsync(caseId, ct).ConfigureAwait(false);
    }

    /// <summary>Rebuild validation without the build: bad cases and unknown
    /// kinds are refused here, before anything runs in the background.</summary>
    public static RebuildAccepted AcceptRebuildAsync(DockRebuildViewsRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CaseId))
        {
            return new RebuildAccepted(false, "Case is required to rebuild dock views.", string.Empty, []);
        }

        var invalid = ValidateDockProblems(request.Problems, out var problems);
        if (invalid is not null)
        {
            return new RebuildAccepted(false, invalid, request.CaseId.Trim(), []);
        }

        return new RebuildAccepted(true, null, request.CaseId.Trim(), problems);
    }

    /// <summary>Dock-photo validation without the read: the photos travel
    /// to the background task, which transcribes, judges, and advances.</summary>
    public static PhotosAccepted AcceptDockPhotosAsync(DockPhotosRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CaseId))
        {
            return new PhotosAccepted(false, "Case is required for dock photos.", 0);
        }

        if (request.Photos is null || request.Photos.Count == 0)
        {
            return new PhotosAccepted(false, "At least one dock photo is required.", 0);
        }

        return new PhotosAccepted(true, null, request.Photos.Count);
    }

    /// <summary>Background half of the dock send: transcribes the photos,
    /// judges the cartons, and advances. The panel polls case progress
    /// for the outcome.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<Verdict>>> JudgeDockPhotosAsync(
        CaseRunner runner, string caseId, IReadOnlyList<DockPhoto> photos,
        CancellationToken ct = default)
    {
        var reads = await runner.TranscribeDockPhotosAsync(caseId, photos, ct).ConfigureAwait(false);
        var verdicts = await runner.OnDockReadsAsync(caseId, reads, ct).ConfigureAwait(false);
        await runner.AdvanceAsync(caseId, ct).ConfigureAwait(false);
        return verdicts;
    }

    /// <summary>Reshot validation without the read.</summary>
    public static PhotosAccepted AcceptReshotAsync(ReshotPhotosRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CaseId))
        {
            return new PhotosAccepted(false, "Case is required for a reshot.", 0);
        }

        if (request.CartonIds is null || request.CartonIds.Count == 0)
        {
            return new PhotosAccepted(false, "At least one carton is required for a reshot.", 0);
        }

        if (request.Photos is null || request.Photos.Count == 0)
        {
            return new PhotosAccepted(false, "At least one reshot photo is required.", 0);
        }

        return new PhotosAccepted(true, null, request.Photos.Count);
    }

    /// <summary>Background half of the reshot: transcribes the close-ups,
    /// re-judges the listed cartons, and advances.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<Verdict>>> JudgeReshotAsync(
        CaseRunner runner, string caseId, IReadOnlyList<string> cartonIds,
        IReadOnlyList<DockPhoto> photos, CancellationToken ct = default)
    {
        var reads = await runner.TranscribeDockPhotosAsync(caseId, photos, ct).ConfigureAwait(false);
        var verdicts = await runner
            .OnReshotAsync(new ReshotInput(caseId, cartonIds, reads), ct).ConfigureAwait(false);
        await runner.AdvanceAsync(caseId, ct).ConfigureAwait(false);
        return verdicts;
    }

    /// <summary>Invoice-photo validation without the read.</summary>
    public static PhotosAccepted AcceptInvoicePhotoAsync(InvoicePhotosRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CaseId))
        {
            return new PhotosAccepted(false, "Case is required for an invoice photo.", 0);
        }

        if (request.Photos is null || request.Photos.Count == 0)
        {
            return new PhotosAccepted(false, "At least one invoice photo is required.", 0);
        }

        return new PhotosAccepted(true, null, request.Photos.Count);
    }

    /// <summary>Background half of the invoice photo: transcribes the
    /// pages, intakes the invoice, and advances.</summary>
    public static async Task<InvoiceIntakeOutcome> IntakeInvoicePhotoAsync(
        CaseRunner runner, string caseId, IReadOnlyList<InvoicePhoto> photos,
        CancellationToken ct = default)
    {
        var transcribed = await runner
            .TranscribeInvoicePhotosAsync(caseId, photos, ct).ConfigureAwait(false);
        var outcome = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId,
            transcribed.SupplierInvoiceNumber ?? string.Empty,
            transcribed.VendorIdOrName,
            transcribed.PoNumber,
            transcribed.Lines,
            transcribed.BankAccountRef), ct).ConfigureAwait(false);
        await runner.AdvanceAsync(caseId, ct).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>Local progress read for the outside-world panels: the
    /// newest dock outcome when it lands after since, plus case invoices
    /// newer than since with their match values. Blank cases and
    /// unparseable timestamps answer empty, never an error.</summary>
    public static async Task<CaseProgress> CaseProgressAsync(
        CaseCacheStore cases, IErpAdapter erp, string? caseId, string? since,
        CancellationToken ct = default)
    {
        var after = DateTimeOffset.MinValue;
        if (!string.IsNullOrWhiteSpace(since))
        {
            DateTimeOffset.TryParse(since, out after);
        }

        if (string.IsNullOrWhiteSpace(caseId))
        {
            return new CaseProgress(null, [], []);
        }

        var id = caseId.Trim();
        CaseFile? file;
        try
        {
            file = await cases.ReadAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return new CaseProgress(null, [], []);
        }

        if (file is null)
        {
            return new CaseProgress(null, [], []);
        }

        CaseProgressDock? dock = null;
        DockEvidence? evidence = null;
        try
        {
            evidence = await cases.ReadDockAsync(id, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }

        if (evidence is not null && evidence.RecordedAt > after)
        {
            var issues = new List<CaseProgressIssue>();
            foreach (var (cartonId, verdicts) in evidence.Verdicts
                .OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                foreach (var verdict in verdicts.Where(v => v.Kind != VerdictKind.Pass))
                {
                    issues.Add(new CaseProgressIssue(
                        cartonId, verdict.RuleId, verdict.Kind.ToString(), verdict.Reason));
                }
            }

            dock = new CaseProgressDock(
                evidence.RecordedAt,
                evidence.Verdicts.Keys.Order(StringComparer.Ordinal).ToList(),
                issues);
        }

        var invoices = new List<CaseProgressInvoice>();
        foreach (var key in file.Case.Invoices)
        {
            ct.ThrowIfCancellationRequested();
            var invoice = await erp.GetInvoiceAsync(key.VendorId, key.SupplierInvoiceNumber, ct)
                .ConfigureAwait(false);
            if (invoice?.CreatedAt is not { } createdAt || createdAt <= after)
            {
                continue;
            }

            invoices.Add(new CaseProgressInvoice(
                invoice.VendorId, invoice.SupplierInvoiceNumber, invoice.Currency,
                invoice.Lines
                    .Where(l => l.Disposition == LineDisposition.Accepted)
                    .Sum(l => l.Quantity * l.UnitPrice),
                invoice.Lines
                    .Where(l => l.Disposition == LineDisposition.Held)
                    .Sum(l => l.Quantity * l.UnitPrice),
                createdAt));
        }

        var events = file.Events
            .Where(e => e.Timestamp > after)
            .OrderBy(e => e.Timestamp)
            .TakeLast(25)
            .Select(e => e.Summary)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return new CaseProgress(
            dock,
            invoices.OrderBy(i => i.ReceivedAt).ToList(),
            events);
    }

    /// <summary>Validates simulated problems against the canonical kind
    /// names. Returns the refusal reason, or null with the normalized
    /// problems when every entry is valid.</summary>
    public static string? ValidateDockProblems(
        IEnumerable<DockProblemInput>? problems, out List<DockProblemForm> normalized)
    {
        normalized = [];
        if (problems is null)
        {
            return null;
        }

        foreach (var problem in problems)
        {
            if (problem is null || string.IsNullOrWhiteSpace(problem.CartonId))
            {
                return "Every simulated problem needs a carton id.";
            }

            if (!DockProblemKinds.TryParse(problem.Kind, out var canonical))
            {
                return $"Unknown carton problem '{problem.Kind}' for carton {problem.CartonId.Trim()}; " +
                    $"pick {DockProblemKinds.DescribeOptions()}.";
            }

            normalized.Add(new DockProblemForm(problem.CartonId.Trim(), canonical));
        }

        return null;
    }

    /// <summary>Maps validated picker problems onto the canonical staged
    /// problem shape. Kinds arrive canonical from validation, so the parse
    /// cannot fail; the guard keeps the mapping honest.</summary>
    public static List<SimulatedProblem> ToSimulatedProblems(IEnumerable<DockProblemForm> problems) =>
        problems.Select(p =>
            Enum.TryParse<SimulatedProblemKind>(p.Kind, out var kind)
                ? new SimulatedProblem(p.CartonId, kind)
                : throw new InvalidOperationException(
                    $"Unknown carton problem '{p.Kind}' for carton {p.CartonId}.")).ToList();
}

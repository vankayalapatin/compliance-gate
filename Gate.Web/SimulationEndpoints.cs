// Simulation endpoints: external inputs in only (order webhooks, supplier
// ACKs, ASNs, dock photos, invoice photos or EDI lines, vendor replies).
// Photos are transcribed by Spark (cached) and judged by C#; there is no
// reads-without-photo path. Each handler runs, then the runner advances
// until it waits on external input or completes.
using Gate.Agent;
using Gate.Web.Services;
using Gate.Web.ViewModels;

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

/// <summary>Outcome of rebuilding dock views: the rebuilt image file names
/// plus the normalized problems, or the failure reason.</summary>
public sealed record DockRebuildOutcome(
    bool Ok,
    string? Error,
    string CaseId,
    IReadOnlyList<string> Images,
    IReadOnlyList<DockProblemForm> Problems);

/// <summary>Outcome of a supplier shipment notice: the minted ASN plus the
/// receipt, the labeled dock images, and the advance, or the failure
/// reason. Response shape matches the plain ASN post.</summary>
public sealed record SupplierAsnOutcome(
    bool Ok,
    string? Error,
    string? AsnNumber,
    string? ReceiptNumber,
    IReadOnlyList<string> DockImages,
    string Step,
    bool Complete);

public static class SimulationEndpoints
{
    public static void MapSimulation(this WebApplication app)
    {
        app.MapPost("/simulation/shopify/order", async (
            OrderWebhookInput input, CaseRunner runner, CancellationToken ct) =>
        {
            var caseIds = await runner.OnOrderWebhookAsync(input, ct).ConfigureAwait(false);
            var advances = new List<object>();
            foreach (var caseId in caseIds)
            {
                var advanced = await runner.AdvanceAsync(caseId, ct).ConfigureAwait(false);
                advances.Add(new
                {
                    caseId,
                    stage = advanced.Stage.ToString(),
                    step = advanced.Step.ToString(),
                    advanced.Complete,
                });
            }

            return Results.Ok(new { caseIds, advances });
        });

        app.MapPost("/simulation/supplier/ack", async (
            SupplierAckInput input, CaseRunner runner, CancellationToken ct) =>
        {
            var ack = await runner.OnSupplierAckAsync(input, ct).ConfigureAwait(false);
            var advanced = await runner.AdvanceAsync(input.CaseId, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                ack.PoNumber,
                step = advanced.Step.ToString(),
                advanced.Complete,
            });
        });

        app.MapPost("/simulation/supplier/asn", async (
            SupplierAsnWithProblemsRequest request, CaseRunner runner, CancellationToken ct) =>
        {
            var outcome = await SubmitSupplierAsnAsync(request, runner, ct).ConfigureAwait(false);
            return outcome.Ok
                ? Results.Ok(new
                {
                    asnNumber = outcome.AsnNumber,
                    receiptNumber = outcome.ReceiptNumber,
                    dockImages = outcome.DockImages,
                    step = outcome.Step,
                    complete = outcome.Complete,
                })
                : Results.BadRequest(new { error = outcome.Error });
        });

        app.MapPost("/simulation/dock/rebuild-views", async (
            DockRebuildViewsRequest request, CaseRunner runner, CancellationToken ct) =>
        {
            var outcome = await RebuildDockViewsAsync(request, runner, ct).ConfigureAwait(false);
            return outcome.Ok
                ? Results.Ok(new
                {
                    caseId = outcome.CaseId,
                    images = outcome.Images,
                    problems = outcome.Problems
                        .Select(p => new { cartonId = p.CartonId, kind = p.Kind }).ToList(),
                })
                : Results.BadRequest(new { error = outcome.Error });
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

        app.MapPost("/simulation/dock/photos", async (
            DockPhotosRequest request, CaseRunner runner, CancellationToken ct) =>
        {
            var reads = await runner.TranscribeDockPhotosAsync(request.CaseId, request.Photos, ct)
                .ConfigureAwait(false);
            var verdicts = await runner.OnDockReadsAsync(request.CaseId, reads, ct).ConfigureAwait(false);
            var advanced = await runner.AdvanceAsync(request.CaseId, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                cartons = verdicts.Keys.Order(StringComparer.Ordinal).ToList(),
                step = advanced.Step.ToString(),
                advanced.Complete,
            });
        });

        app.MapPost("/simulation/dock/reshot", async (
            ReshotPhotosRequest request, CaseRunner runner, CancellationToken ct) =>
        {
            var reads = await runner.TranscribeDockPhotosAsync(request.CaseId, request.Photos, ct)
                .ConfigureAwait(false);
            var verdicts = await runner
                .OnReshotAsync(new ReshotInput(request.CaseId, request.CartonIds, reads), ct)
                .ConfigureAwait(false);
            var advanced = await runner.AdvanceAsync(request.CaseId, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                cartons = verdicts.Keys.Order(StringComparer.Ordinal).ToList(),
                step = advanced.Step.ToString(),
                advanced.Complete,
            });
        });

        app.MapPost("/simulation/supplier/invoice", async (
            SupplierInvoiceInput input, CaseRunner runner, CancellationToken ct) =>
        {
            var outcome = await runner.OnInvoiceAsync(input, ct).ConfigureAwait(false);
            var advanced = await runner.AdvanceAsync(input.CaseId, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                invoice = outcome.Key is null
                    ? null
                    : $"{outcome.Key.VendorId}/{outcome.Key.SupplierInvoiceNumber}",
                outcome.Persisted,
                outcome.AcceptedValue,
                outcome.HeldValue,
                step = advanced.Step.ToString(),
                advanced.Complete,
            });
        });

        app.MapPost("/simulation/supplier/invoice-photo", async (
            InvoicePhotosRequest request, CaseRunner runner, CancellationToken ct) =>
        {
            var transcribed = await runner.TranscribeInvoicePhotosAsync(request.CaseId, request.Photos, ct)
                .ConfigureAwait(false);
            var outcome = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
                request.CaseId,
                transcribed.SupplierInvoiceNumber ?? string.Empty,
                transcribed.VendorIdOrName,
                transcribed.PoNumber,
                transcribed.Lines,
                transcribed.BankAccountRef), ct).ConfigureAwait(false);
            var advanced = await runner.AdvanceAsync(request.CaseId, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                invoice = outcome.Key is null
                    ? null
                    : $"{outcome.Key.VendorId}/{outcome.Key.SupplierInvoiceNumber}",
                outcome.Persisted,
                outcome.AcceptedValue,
                outcome.HeldValue,
                step = advanced.Step.ToString(),
                advanced.Complete,
            });
        });

        app.MapPost("/simulation/supplier/email-reply", async (
            VendorReplyInput input, CaseRunner runner, CancellationToken ct) =>
        {
            var reply = await runner.OnVendorReplyAsync(input, ct).ConfigureAwait(false);
            var advanced = await runner.AdvanceAsync(input.CaseId, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                reply.EmailId,
                step = advanced.Step.ToString(),
                advanced.Complete,
            });
        });
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

    /// <summary>Shipment-notice handler: validates the picker problems,
    /// runs the ASN (problems included) through the runner, then labels
    /// the pallet views from it and advances as before.</summary>
    public static async Task<SupplierAsnOutcome> SubmitSupplierAsnAsync(
        SupplierAsnWithProblemsRequest? request, CaseRunner runner,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CaseId))
        {
            return new SupplierAsnOutcome(
                false, "Case is required for a shipment notice.", null, null, [], string.Empty, false);
        }

        var invalid = ValidateDockProblems(request.Problems, out var problems);
        if (invalid is not null)
        {
            return new SupplierAsnOutcome(
                false, invalid, null, null, [], string.Empty, false);
        }

        var input = new SupplierAsnInput(
            request.CaseId.Trim(),
            request.AsnNumber?.Trim() ?? string.Empty,
            request.SupplierPackingSlipNumber?.Trim() ?? string.Empty,
            request.Cartons ?? [],
            request.IdempotencyKey,
            ToSimulatedProblems(problems));
        var (asn, receipt) = await runner.OnAsnAsync(input, ct).ConfigureAwait(false);
        // The moment the supplier sends the ASN, the pallet views are
        // labeled from it (minted PO plus ASN lots) and read live; the
        // dock counter then offers this truck's own photos.
        var images = await runner.BuildDockImagesAsync(input.CaseId, ct).ConfigureAwait(false);
        var advanced = await runner.AdvanceAsync(input.CaseId, ct).ConfigureAwait(false);
        return new SupplierAsnOutcome(
            true, null, asn.AsnNumber, receipt?.ReceiptNumber,
            images?.Images.Select(i => System.IO.Path.GetFileName(i.FilePath)).ToList()
                ?? new List<string>(),
            advanced.Step.ToString(), advanced.Complete);
    }

    /// <summary>Dock-view rebuild handler: validates the problems, rebuilds
    /// the case's labeled pallet views through the runner (which persists
    /// the problems and records any live fallback on the timeline), and
    /// returns the rebuilt image list. Every failure carries its reason.</summary>
    public static async Task<DockRebuildOutcome> RebuildDockViewsAsync(
        DockRebuildViewsRequest? request, CaseRunner runner,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CaseId))
        {
            return new DockRebuildOutcome(false, "Case is required to rebuild dock views.", string.Empty, [], []);
        }

        var invalid = ValidateDockProblems(request.Problems, out var problems);
        if (invalid is not null)
        {
            return new DockRebuildOutcome(false, invalid, request.CaseId.Trim(), [], []);
        }

        try
        {
            var set = await runner.RebuildDockViewsAsync(
                new RebuildDockViewsInput(request.CaseId.Trim(), ToSimulatedProblems(problems)), ct)
                .ConfigureAwait(false);
            if (set is null)
            {
                return new DockRebuildOutcome(
                    false,
                    $"Dock views for case {request.CaseId.Trim()} could not be rebuilt; the case timeline carries the reason.",
                    request.CaseId.Trim(), [], []);
            }

            return new DockRebuildOutcome(
                true, null, request.CaseId.Trim(),
                set.Images.Select(i => System.IO.Path.GetFileName(i.FilePath)).ToList(),
                problems);
        }
        catch (InvalidOperationException ex)
        {
            return new DockRebuildOutcome(false, ex.Message, request.CaseId.Trim(), [], []);
        }
    }
}

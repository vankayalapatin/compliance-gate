// Simulation endpoints: external inputs in only (order webhooks, supplier
// ACKs, ASNs, dock photos, invoice photos or EDI lines, vendor replies).
// Photos are transcribed by Spark (cached) and judged by C#; there is no
// reads-without-photo path. Each handler runs, then the runner advances
// until it waits on external input or completes.
using Gate.Agent;

namespace Gate.Web;

public sealed record DockPhotosRequest(string CaseId, List<DockPhoto> Photos);

public sealed record ReshotPhotosRequest(string CaseId, List<string> CartonIds, List<DockPhoto> Photos);

public sealed record ReshotImageRequest(string CaseId, string CartonId);

public sealed record InvoicePhotosRequest(string CaseId, List<InvoicePhoto> Photos);

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
            SupplierAsnInput input, CaseRunner runner, CancellationToken ct) =>
        {
            var (asn, receipt) = await runner.OnAsnAsync(input, ct).ConfigureAwait(false);
            // The moment the supplier sends the ASN, the pallet views are
            // labeled from it (minted PO plus ASN lots) and read live; the
            // dock counter then offers this truck's own photos.
            var images = await runner.BuildDockImagesAsync(input.CaseId, ct).ConfigureAwait(false);
            var advanced = await runner.AdvanceAsync(input.CaseId, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                asn.AsnNumber,
                receiptNumber = receipt?.ReceiptNumber,
                dockImages = images?.Images.Select(i => System.IO.Path.GetFileName(i.FilePath)).ToList()
                    ?? new List<string>(),
                step = advanced.Step.ToString(),
                advanced.Complete,
            });
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
}

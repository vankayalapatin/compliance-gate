// Simulation endpoints: external inputs in only (order webhooks, supplier
// ACKs, ASNs, dock reads, reshots, supplier invoices). Each handler runs,
// then the runner advances until it waits on external input or completes;
// no fixed step sequence lives here either.
using Gate.Agent;

namespace Gate.Web;

public sealed record DockReadsRequest(string CaseId, List<DockCartonRead> Reads);

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
            var advanced = await runner.AdvanceAsync(input.CaseId, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                asn.AsnNumber,
                receiptNumber = receipt?.ReceiptNumber,
                step = advanced.Step.ToString(),
                advanced.Complete,
            });
        });

        app.MapPost("/simulation/dock/reads", async (
            DockReadsRequest request, CaseRunner runner, CancellationToken ct) =>
        {
            var verdicts = await runner.OnDockReadsAsync(request.CaseId, request.Reads, ct)
                .ConfigureAwait(false);
            var advanced = await runner.AdvanceAsync(request.CaseId, ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                cartons = verdicts.Keys.Order(StringComparer.Ordinal).ToList(),
                step = advanced.Step.ToString(),
                advanced.Complete,
            });
        });

        app.MapPost("/simulation/dock/reshot", async (
            ReshotInput input, CaseRunner runner, CancellationToken ct) =>
        {
            var verdicts = await runner.OnReshotAsync(input, ct).ConfigureAwait(false);
            var advanced = await runner.AdvanceAsync(input.CaseId, ct).ConfigureAwait(false);
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
    }
}

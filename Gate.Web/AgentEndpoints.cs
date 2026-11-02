// Agent-work endpoints: internal agent operations the UI triggers
// explicitly. Adoption only: picking up ERP work opens or converts cases
// through the agent lane with zero live model calls; advancing stays with
// the case flow (Decisions/Cases), never hidden in here. Failures are
// logged with the company code and surface as problem details.
using Gate.Agent;

namespace Gate.Web;

public sealed record PickupRequest(string? EntityCode);

public static class AgentEndpoints
{
    public static void MapAgentPickup(this WebApplication app)
    {
        app.MapPost("/agent/pickup", async (
            PickupRequest request, CaseRunner runner, ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger("AgentEndpoints");
            if (request is null || string.IsNullOrWhiteSpace(request.EntityCode))
            {
                return Results.BadRequest(new { error = "Company code is required for ERP-work pickup." });
            }

            try
            {
                var touched = await runner.PickupErpWorkAsync(request.EntityCode.Trim(), ct).ConfigureAwait(false);
                return Results.Ok(new { touched, count = touched.Count });
            }
            catch (Exception ex)
            {
                log.LogError(ex, "ERP-work pickup failed for company {Entity}.", request.EntityCode);
                return Results.Problem(
                    detail: $"ERP-work pickup failed: {ex.Message}.",
                    title: "ERP-work pickup failed");
            }
        });
    }
}

// Scenario startup + reset wiring. Additive only: startup fills sqlite and
// the ERP from the scenario file when the store is empty (no-op when the
// scenario file is absent, so existing runs are unchanged), and the reset
// endpoint wipes plus reloads so every document number is regenerated.
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Web;

public static class ScenarioEndpoints
{
    public static string ScenarioPath(IConfiguration config) =>
        config["Scenario:File"]
        ?? Environment.GetEnvironmentVariable("GATE_SCENARIO")
        ?? "scenarios/demo-v1.json";

    public static string SqlitePath(IConfiguration config) =>
        config["Scenario:Sqlite"]
        ?? Environment.GetEnvironmentVariable("GATE_SQLITE")
        ?? "data/gate.db";

    public static string ManifestPath(IConfiguration config) =>
        config["Scenario:Manifest"]
        ?? Environment.GetEnvironmentVariable("GATE_MANIFEST")
        ?? "scenarios/run-manifest.json";

    /// <summary>Loads the scenario on startup when the store is empty.
    /// Returns null when no scenario file resolves (existing behavior).</summary>
    public static async Task<ScenarioManifest?> EnsureLoadedAsync(
        IServiceProvider services, IConfiguration config, CancellationToken ct = default)
    {
        var resolved = ScenarioLoader.ResolvePath(ScenarioPath(config));
        if (!File.Exists(resolved))
        {
            return null;
        }

        var erp = services.GetRequiredService<IErpAdapter>();
        return await ScenarioLoader.LoadAsync(erp, resolved, SqlitePath(config), ManifestPath(config), ct)
            .ConfigureAwait(false);
    }

    public static void MapScenarioReset(this WebApplication app)
    {
        app.MapPost("/scenario/reset", async (
            IErpAdapter erp, IConfiguration config, CancellationToken ct) =>
        {
            var manifest = await ScenarioLoader.ResetAsync(
                erp, ScenarioPath(config), SqlitePath(config), ManifestPath(config), ct);
            return Results.Ok(manifest);
        });
    }
}

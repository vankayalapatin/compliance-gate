// Scenario reset determinism: wipe plus reload regenerates the same
// sequence-minted document ids per scenario event (timestamps excluded).
using Gate.Erp;

namespace Gate.Eval;

public sealed class ScenarioResetTests
{
    [Fact]
    public async Task TwoResetsProduceIdenticalManifests()
    {
        var erp = new InMemoryErpAdapter();
        var scenario = ScenarioLoader.ResolvePath("scenarios/demo-v1.json");
        Assert.True(File.Exists(scenario), $"Scenario file not found: {scenario}");
        var workDir = Path.Combine(Path.GetTempPath(), "gate-reset-test", Guid.NewGuid().ToString("N"));
        var sqlite = Path.Combine(workDir, "gate.db");
        var manifestFile = Path.Combine(workDir, "run-manifest.json");
        try
        {
            var first = await ScenarioLoader.ResetAsync(erp, scenario, sqlite, manifestFile);
            var second = await ScenarioLoader.ResetAsync(erp, scenario, sqlite, manifestFile);

            Assert.NotEmpty(first.Documents);
            Assert.Equal(first.Documents, second.Documents);
            Assert.True(File.Exists(manifestFile));
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                Directory.Delete(workDir, recursive: true);
            }
        }
    }
}

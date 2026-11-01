// Live vendor-identity proof: one real Spark suggestion for a transcribed
// "Name (ID)" reference, validated by C# against the vendor master, plus the
// clerk card. The key comes from user secrets (MetaApi:ApiKey) or the
// environment, never from a file in the repo.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class VendorIdentityLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveSparkSuggestsTheVendorForANamePlusIdReference()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        var model = config["MetaApi:Model"];
        var baseUrl = config["MetaApi:BaseUrl"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "MetaApi:ApiKey is missing.");
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing.");

        using var http = new HttpClient();
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl,
            Model = model!,
            ApiKey = apiKey!,
        });
        var modelCache = Path.Combine(Path.GetTempPath(), "gate-vendorid-live", Guid.NewGuid().ToString("N"));
        var caseDir = Path.Combine(Path.GetTempPath(), "gate-vendorid-live-cases", Guid.NewGuid().ToString("N"));
        var erp = SeededErp.Create();
        erp.SeedUser(new UserDto("clerk", "ap-clerk", 0, "USD"));
        var models = new CachedModelClient(spark, modelCache);
        var compliance = new ComplianceEngine();
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), caseDir);

        var cases = await runner.OnOrderWebhookAsync(new OrderWebhookInput(
            "live", "SO-1001", "planner",
            new Dictionary<string, IReadOnlyList<string>> { [SeededErp.Battery] = ["V-A", "V-B"] }));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        var card = await runner.ProposeVendorIdentityAsync(
            caseId, "Meridian Cellworks (V-B)", ["image:INV-LIVE-1"]);

        Assert.NotNull(card);
        Assert.Equal(DecisionKind.VendorIdentity, card.Kind);
        Assert.Equal("Vendor", card.DocumentType);
        Assert.Equal("V-B", card.DocumentNumber);
        Assert.Equal("Meridian Cellworks (V-B)", card.Reason);
        var approved = await runner.ResolveDecisionAsync(
            caseId, card.DecisionId, true, card.RequestedTo, "confirmed live");
        Assert.Equal(DecisionStatus.Approved, approved.Status);
    }
}

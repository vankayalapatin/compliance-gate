// Live proof (ONE targeted live test): one board memo end to end through
// real Spark — intake, propose, engine validation, what-if, approval card.
// The KB snapshot is the real company-kb.json, history is the seeded ERP,
// and the engine seam is the fake over real deterministic cores; only the
// proposal call itself is live. The key comes from user secrets
// (MetaApi:ApiKey) or the environment — never from a file in the repo.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class MemoSparkLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task CapMemoEndToEnd_Live()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        var model = config["MetaApi:Model"];
        var baseUrl = config["MetaApi:BaseUrl"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey),
            "MetaApi:ApiKey is missing. Set it with: dotnet user-secrets set \"MetaApi:ApiKey\" \"<key>\" --project Gate.Web (plus MetaApi:Model).");
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing. Set it with: dotnet user-secrets set \"MetaApi:Model\" \"<model>\" --project Gate.Web.");

        using var http = new HttpClient();
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl,
            Model = model!,
            ApiKey = apiKey!
        });
        var kbPath = CompanyKbLoader.ResolvePath("company/company-kb.json");
        var snapshot = CompanyKbLoader.Load(File.ReadAllText(kbPath)).ToSnapshot();
        var erp = SeededErp.Create();
        var compliance = new RecordingComplianceEngine { Snapshot = snapshot };
        var cacheDir = MemoTestKit.TempDir("gate-memo-live");
        var inboxDir = Path.Combine(MemoTestKit.TempDir("gate-memo-live-inbox"), "inbox");
        var runner = new MemoRunner(erp, spark, compliance, cacheDir);

        var name = MemoTestKit.WriteInboxMemo(inboxDir, "MEMO-LIVE-1",
            "No more than 40% of critical battery cells from any single country.",
            "Effective 2026-12-01, no more than 40% of critical battery cells from any single country, trailing 12 months. Policy owner: Dana Director.");
        await runner.IngestMemoAsync(inboxDir, name);

        var outcome = await runner.AdvanceAsync("MEMO-LIVE-1");

        Assert.Equal(MemoNextStep.AwaitApproval, outcome.Step);
        var file = MemoStore.Load(cacheDir, "MEMO-LIVE-1")!;
        var record = Assert.Single(file.Proposals);
        Assert.True(record.Validation.Ok,
            "Live Spark proposal failed engine validation:\n- " + string.Join("\n- ", record.Validation.Errors));
        Assert.Equal("OriginShareCap", record.Proposal.ProposedRule!.RuleType);
        Assert.Equal(0.40m, decimal.Parse(record.Proposal.ProposedRule.Params["cap"],
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("critical", record.Proposal.ProposedRule.Params["partScope"]);
        Assert.Equal("Dana Director", record.PolicyOwner);
        Assert.Empty(MemoPicker.CurrentBlocking(file, record));
        var tighten = Assert.Single(record.Validation.Conflicts);
        Assert.Equal("CORP-02", tighten.RuleId);
        Assert.Equal(RuleConflictKind.Tightens, tighten.Kind);
        Assert.NotNull(record.WhatIf);
        Assert.Equal(4, record.WhatIf.HistoryRowCount);
        Assert.NotEmpty(record.WhatIf.HistoryFlips);
        var card = Assert.Single(file.Decisions);
        Assert.Equal(DecisionKind.PolicyApproval, card.Kind);
        Assert.Equal("Dana Director", card.RequestedTo);
    }
}

// Live proof: Dana enters a memo cutting the battery-cell country cap to
// 40%, Finn (another owner) approves it, and the published rule refuses
// the next breaching battery order at the ERP. Only the proposal call is
// live Spark; intake, validation, approval, publish, and the ERP refusal
// are deterministic C#. Enforcement is wired explicitly from the
// published pack (loader -> thresholds -> engine -> policy): the app's
// shared engine keeps enforcing its embedded thresholds until something
// rebuilds it after publish, which is a lead call, not this test.
using System.Text.Json;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.Extensions.Configuration;
using Xunit.Abstractions;

namespace Gate.Eval;

public sealed class MemoCapLiveTests
{
    private readonly ITestOutputHelper _output;

    public MemoCapLiveTests(ITestOutputHelper output) => _output = output;

    private sealed class MemoSummary
    {
        public string MemoId { get; set; } = "MEMO-LIVE-40";
        public string Author { get; set; } = string.Empty;
        public string RuleType { get; set; } = string.Empty;
        public string Cap { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string ApprovedBy { get; set; } = string.Empty;
        public string RuleId { get; set; } = string.Empty;
        public string PackId { get; set; } = string.Empty;
        public string PriorPackVersion { get; set; } = string.Empty;
        public string NewPackVersion { get; set; } = string.Empty;
        public decimal PublishedCap { get; set; }
        public string Refusal { get; set; } = string.Empty;
        public string ControlPo { get; set; } = string.Empty;
        public string? Error { get; set; }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "company", "company-kb.json")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root with company/company-kb.json not found.");
    }

    private static string FindOutboxDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".agent-checks", "outbox");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.GetTempPath();
    }

    private void WriteSummary(MemoSummary summary)
    {
        var path = Path.Combine(FindOutboxDir(), "memo-cap-live.summary.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        _output.WriteLine(
            $"[memo-cap-live] rule={summary.RuleId} pack={summary.PackId} {summary.PriorPackVersion}->{summary.NewPackVersion} " +
            $"cap={summary.PublishedCap} approved-by={summary.ApprovedBy} err={summary.Error ?? "none"}");
    }

    private static async Task<string> ApprovedPrAsync(InMemoryErpAdapter erp, decimal qty)
    {
        var pr = await erp.CreatePrAsync("planner", RecordSource.Human, "planner",
            [new PurchaseRequisitionLine(SeededErp.Battery, qty, "EA")], Guid.NewGuid().ToString("N"));
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "delegate", "planner", Guid.NewGuid().ToString("N"));
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "delegate", true, "ok", Guid.NewGuid().ToString("N"));
        }

        return pr.PrNumber;
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task DanaMemoFinnApprovesErpRefuses()
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

        var summary = new MemoSummary();
        try
        {
            var root = FindRepoRoot();
            var work = Path.Combine(Path.GetTempPath(), "gate-memo-cap-live", Guid.NewGuid().ToString("N"));
            var kbDir = Path.Combine(work, "kb");
            var packDir = Path.Combine(work, "packs");
            var cacheDir = Path.Combine(work, "memos");
            var inboxDir = Path.Combine(work, "inbox");
            Directory.CreateDirectory(kbDir);
            Directory.CreateDirectory(packDir);
            Directory.CreateDirectory(inboxDir);
            foreach (var file in Directory.GetFiles(Path.Combine(root, "company"), "*.json"))
            {
                File.Copy(file, Path.Combine(kbDir, Path.GetFileName(file)));
            }

            foreach (var file in Directory.GetFiles(Path.Combine(root, "Gate.Compliance", "Packs"), "*.json"))
            {
                File.Copy(file, Path.Combine(packDir, Path.GetFileName(file)));
            }

            using var http = new HttpClient();
            var spark = new SparkModelClient(http, new SparkOptions
            {
                BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl,
                Model = model!,
                ApiKey = apiKey!
            });
            var engine = new ComplianceEngine(new ComplianceMemoOptions(kbDir, packDir));
            var runner = new MemoRunner(SeededErp.Create(), spark, engine, cacheDir);

            var name = MemoTestKit.WriteInboxMemo(inboxDir, summary.MemoId,
                "Cut battery-cell country share to 40%.",
                "China's share of our battery cells must come down. Effective 2026-12-01, " +
                "no more than 40% of critical battery cells from any single country, trailing 12 months. " +
                "Policy owner: Dana Director.",
                "Dana Director");
            summary.Author = "Dana Director";
            await runner.IngestMemoAsync(inboxDir, name);

            var outcome = await runner.AdvanceAsync(summary.MemoId);
            Assert.Equal(MemoNextStep.AwaitApproval, outcome.Step);
            var memoFile = MemoStore.Load(cacheDir, summary.MemoId)!;
            var record = Assert.Single(memoFile.Proposals);
            Assert.True(record.Validation.Ok,
                "Live Spark proposal failed engine validation:\n- " + string.Join("\n- ", record.Validation.Errors));
            Assert.Equal("OriginShareCap", record.Proposal.ProposedRule!.RuleType);
            Assert.Equal("0.40", record.Proposal.ProposedRule.Params["cap"]);
            Assert.Equal("critical", record.Proposal.ProposedRule.Params["partScope"]);
            Assert.Equal("Dana Director", record.PolicyOwner);
            Assert.Empty(MemoPicker.CurrentBlocking(memoFile, record));
            var tighten = Assert.Single(record.Validation.Conflicts);
            Assert.Equal("CORP-02", tighten.RuleId);
            Assert.Equal(RuleConflictKind.Tightens, tighten.Kind);
            Assert.NotNull(record.WhatIf);
            summary.RuleType = record.Proposal.ProposedRule.RuleType;
            summary.Cap = record.Proposal.ProposedRule.Params["cap"];
            summary.Owner = record.PolicyOwner;

            var card = Assert.Single(memoFile.Decisions);
            Assert.Equal(DecisionKind.PolicyApproval, card.Kind);
            var approved = await runner.ResolvePolicyApprovalAsync(summary.MemoId, card.DecisionId,
                new PolicyApprovalInput(
                    true, "Finn Finance", DateTimeOffset.UtcNow.AddDays(30),
                    RuleApplicability.ProspectiveOnly, "Approved by Finn Finance.",
                    TestEvidence: ["MemoCapLiveTests.DanaMemoFinnApprovesErpRefuses"]));
            Assert.NotNull(approved.Activation);
            Assert.Equal("CORP-02", approved.Activation.RuleId);
            Assert.Equal("CORP", approved.Activation.PackId);
            Assert.NotEqual(approved.Activation.PriorPackVersion, approved.Activation.NewPackVersion);
            summary.ApprovedBy = "Finn Finance";
            summary.RuleId = approved.Activation.RuleId;
            summary.PackId = approved.Activation.PackId;
            summary.PriorPackVersion = approved.Activation.PriorPackVersion;
            summary.NewPackVersion = approved.Activation.NewPackVersion;

            var published = RulePackLoader.LoadPacks(
                Directory.GetFiles(packDir, "*.json")
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .Select(f => (Path.GetFileName(f), File.ReadAllText(f))));
            Assert.Equal(0.40m, published.Corp.OriginCap);
            summary.PublishedCap = published.Corp.OriginCap;

            var enforcing = new InMemoryErpAdapter(new ErpPolicy(new ComplianceEngine(published.Corp)));
            enforcing.SeedBaseCurrency("USD");
            enforcing.SeedAutoApproveThreshold(500m);
            enforcing.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
            enforcing.SeedItem(new ItemDto(SeededErp.Battery, "Battery cell", true, 10, "EA"));
            enforcing.SeedVendor(new VendorDto("V-A", "CinderVolt Energy Ltd", "CN", false, null, "NET0"));
            enforcing.SeedVendor(new VendorDto("V-B", "Meridian Cellworks Inc", "MX", false, null, "NET0"));
            enforcing.SeedVendorBank(new VendorBankDto("V-A", "BANK-A-TEST", true, DateTimeOffset.UtcNow));
            enforcing.SeedVendorBank(new VendorBankDto("V-B", "BANK-B-TEST", true, DateTimeOffset.UtcNow));
            enforcing.SeedVendorPrice("V-A", SeededErp.Battery, 11m);
            enforcing.SeedVendorPrice("V-B", SeededErp.Battery, 12m);
            enforcing.SeedVendorDocuments("V-A",
                [new VendorDocumentDto("V-A", "UN38.3", "un383-a", true),
                    new VendorDocumentDto("V-A", "SDS", "sds-a", true),
                    new VendorDocumentDto("V-A", "RoHS", "rohs-a", true),
                    new VendorDocumentDto("V-A", "REACH", "reach-a", true)]);
            enforcing.SeedVendorDocuments("V-B",
                [new VendorDocumentDto("V-B", "UN38.3", "un383-b", true),
                    new VendorDocumentDto("V-B", "SDS", "sds-b", true),
                    new VendorDocumentDto("V-B", "RoHS", "rohs-b", true),
                    new VendorDocumentDto("V-B", "REACH", "reach-b", true)]);
            enforcing.SeedVolumeHistory(SeededErp.Battery,
                [new VolumeHistory("V-A CN active", "CN", 6000),
                    new VolumeHistory("V-B MX active", "MX", 3700),
                    new VolumeHistory("Other non-CN", "OTHER", 300)]);
            enforcing.SeedUser(new UserDto("planner", "requester", 5000m, "USD"));
            enforcing.SeedUser(new UserDto("approver", "approver", 100_000m, "USD"));
            enforcing.SeedUser(new UserDto("delegate", "delegate", 50_000m, "USD"));
            enforcing.SeedUser(new UserDto("finance", "payment-approver", 100_000m, "USD"));
            var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
            enforcing.SeedDelegation(new DelegationDto("approver", "delegate", today.AddDays(-30), today.AddDays(30)));

            var pr = await ApprovedPrAsync(enforcing, 90);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                enforcing.CreatePoAsync(pr, "V-A", RecordSource.Human, "planner", Guid.NewGuid().ToString("N")));
            Assert.StartsWith(ComplianceEngine.CorpOriginCapId, ex.Message, StringComparison.Ordinal);
            Assert.Contains("40", ex.Message, StringComparison.Ordinal);
            summary.Refusal = ex.Message;

            var controlPr = await ApprovedPrAsync(enforcing, 100);
            var control = await enforcing.CreatePoAsync(
                controlPr, "V-B", RecordSource.Human, "planner", Guid.NewGuid().ToString("N"));
            Assert.Equal("V-B", control.VendorId);
            summary.ControlPo = control.PoNumber;
        }
        catch (Exception ex)
        {
            summary.Error = ex.Message;
            throw;
        }
        finally
        {
            WriteSummary(summary);
        }
    }
}

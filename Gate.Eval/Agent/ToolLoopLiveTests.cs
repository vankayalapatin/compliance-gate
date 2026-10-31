// Live tool-loop proof: demo-v1 and alt-v1 run webhook to Resolved with
// live Spark choosing every step through tool calls (the transcript proves
// it: every executed action names its model-proposed tool, zero
// model-deferred turns), then the identical runs replay from scratch with
// zero live calls. Keys come from user secrets or the environment, never
// from a file in the repo.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace Gate.Eval;

public sealed class ToolLoopLiveTests
{
    private sealed record ScenarioCarton(
        string Id, string Part, int Units, bool MarkPresent, string Damage, IReadOnlyList<string> VisibleIn,
        string? ReshotIn);

    private sealed record LiveSecrets(string ApiKey, string Model, string BaseUrl);

    private static LiveSecrets Secrets()
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
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing.");
        return new LiveSecrets(
            apiKey!, model!,
            string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl);
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return base.SendAsync(request, ct);
        }
    }

    private static List<DockCartonRead> ReadsFor(
        IReadOnlyList<ScenarioCarton> cartons, string poNumber, Func<string, string> lotOf)
    {
        var reads = new List<DockCartonRead>();
        foreach (var carton in cartons)
        {
            var damaged = carton.Damage != "none" && carton.Damage != "none, hidden";
            foreach (var image in carton.VisibleIn)
            {
                reads.Add(new DockCartonRead(
                    carton.Id, carton.Part, poNumber, lotOf(carton.Id), carton.Units,
                    carton.MarkPresent, carton.MarkPresent, "UN3480", true, true,
                    damaged, damaged ? carton.Damage : null, image, 0.95m));
            }
        }

        return reads;
    }

    [Theory]
    [Trait("Category", "Live")]
    [InlineData("scenarios/demo-v1.json")]
    [InlineData("scenarios/alt-v1.json")]
    public async Task LiveSparkRunGoesWebhookToResolvedThenReplaysWithZeroLiveCalls(string scenarioPath)
    {
        var secrets = Secrets();
        var counter = new CountingHandler(new HttpClientHandler());
        using var http = new HttpClient(counter);
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = secrets.BaseUrl,
            Model = secrets.Model,
            ApiKey = secrets.ApiKey,
        });
        var modelCache = Path.Combine(Path.GetTempPath(), "gate-tooloop-live", Guid.NewGuid().ToString("N"));
        var liveCaseDir = Path.Combine(Path.GetTempPath(), "gate-tooloop-live-cases", Guid.NewGuid().ToString("N"));

        await RunScenarioAsync(scenarioPath, new CachedModelClient(spark, modelCache), liveCaseDir, live: true);
        Assert.True(counter.Count > 0, "Expected live Spark calls on the first run.");
        var liveCalls = counter.Count;
        AssertSparkChoseEveryStep(liveCaseDir);

        var replayCaseDir = Path.Combine(Path.GetTempPath(), "gate-tooloop-live-cases", Guid.NewGuid().ToString("N"));
        var guard = new RecordingModelClient(secrets.Model);
        await RunScenarioAsync(scenarioPath, new CachedModelClient(guard, modelCache), replayCaseDir, live: false);
        Assert.Equal(0, guard.CallCount);
        Assert.Equal(liveCalls, counter.Count);
        AssertSparkChoseEveryStep(replayCaseDir);
    }

    private static void AssertSparkChoseEveryStep(string caseDir)
    {
        foreach (var caseId in CaseStore.ListCaseIds(caseDir))
        {
            var turns = CaseTranscript.ReadTurns(caseDir, caseId);
            Assert.NotEmpty(turns);
            Assert.DoesNotContain(turns.SelectMany(t => t.Calls),
                c => c.Verdict == ToolCallVerdict.ModelDeferred);
            var executed = turns.SelectMany(t => t.Calls)
                .Where(c => c.Verdict == ToolCallVerdict.Executed)
                .Select(c => c.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains(AgentTools.ComputeShortfall, executed);
            Assert.Contains(AgentTools.RaisePr, executed);
            Assert.Contains(AgentTools.ScreenVendors, executed);
            Assert.Contains(AgentTools.EvaluatePrePay, executed);
            Assert.Contains(AgentTools.FinishCase, executed);
        }
    }

    private static async Task RunScenarioAsync(
        string scenarioPath, IModelClient models, string caseDir, bool live)
    {
        var resolved = ScenarioLoader.ResolvePath(scenarioPath);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(resolved));
        var root = json.RootElement;
        var scenarioName = root.GetProperty("name").GetString()!;
        var cartons = root.GetProperty("cartons").EnumerateArray().Select(c => new ScenarioCarton(
            c.GetProperty("id").GetString()!,
            c.GetProperty("part").GetString()!,
            c.GetProperty("units").GetInt32(),
            c.GetProperty("markPresent").GetBoolean(),
            c.GetProperty("damage").GetString()!,
            c.GetProperty("visibleIn").EnumerateArray().Select(v => v.GetString()!).ToList(),
            c.TryGetProperty("reshotIn", out var reshot) ? reshot.GetString() : null)).ToList();
        var main = root.GetProperty("invoiceIntents").GetProperty("main");
        var mainVendor = main.GetProperty("vendor").GetString()!;
        var mainQty = main.GetProperty("quantity").GetDecimal();
        var mainPrice = main.GetProperty("unitPrice").GetDecimal();
        var candidates = root.GetProperty("vendors").EnumerateArray()
            .GroupBy(v => v.GetProperty("item").GetString()!)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(v => v.GetProperty("id").GetString()!).ToList());
        var users = root.GetProperty("users").EnumerateArray().ToList();
        var requester = users.First(u => u.GetProperty("role").GetString() == "requester")
            .GetProperty("name").GetString()!;

        var tmp = Path.Combine(Path.GetTempPath(), "gate-tooloop-live-erp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var erp = new InMemoryErpAdapter();
        var manifest = await ScenarioLoader.LoadAsync(
            erp, resolved, Path.Combine(tmp, "gate.db"), Path.Combine(tmp, "manifest.json"));
        string Doc(string eventKey) => manifest.Documents.Single(d => d.Event == eventKey).Number;
        var soNumber = Doc("salesOrder");

        var compliance = new ComplianceEngine();
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), caseDir);

        var caseIds = await runner.OnOrderWebhookAsync(
            new OrderWebhookInput(scenarioName, soNumber, requester, candidates));
        Assert.Equal(2, caseIds.Count);
        var batItem = cartons[0].Part;
        var bat = caseIds.Single(c => c.EndsWith("-" + batItem, StringComparison.Ordinal));
        var screw = caseIds.Single(c => c != bat);

        await RunBatteryAsync(runner, erp, caseDir, bat, cartons, mainVendor, mainQty, mainPrice, live);
        await RunScrewAsync(runner, erp, caseDir, screw);
    }

    private static async Task RunBatteryAsync(
        CaseRunner runner, InMemoryErpAdapter erp, string caseDir, string bat,
        IReadOnlyList<ScenarioCarton> cartons, string mainVendor, decimal mainQty, decimal mainPrice,
        bool live)
    {
        CaseFile Load() => CaseStore.Load(caseDir, bat)!;
        static Decision Pending(CaseFile file, DecisionKind kind) =>
            file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

        await runner.AdvanceAsync(bat);
        var prDecision = Pending(Load(), DecisionKind.PrApproval);
        await runner.ResolveDecisionAsync(bat, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");

        await runner.AdvanceAsync(bat);
        var poDecision = Pending(Load(), DecisionKind.PoApproval);
        await runner.ResolveDecisionAsync(bat, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");

        var batPo = Load().Case.PoNumber!;
        if (live)
        {
            Assert.Equal(mainVendor, (await erp.GetPurchaseOrderAsync(batPo))!.VendorId);
            Assert.Equal(
                ComplianceEngine.CorpOriginCapId,
                Load().Events.Single(e => e.Kind == CaseEventKind.VendorBlocked).RuleId);
        }

        await runner.AdvanceAsync(bat);
        await runner.OnSupplierAckAsync(new SupplierAckInput(bat));
        await runner.AdvanceAsync(bat);
        string LotOf(string cartonId) => $"{batPo}-{cartonId}";
        await runner.OnAsnAsync(new SupplierAsnInput(bat, $"ASN-{batPo}", $"PACK-{batPo}",
            cartons.Select(c => new AsnCartonInput(c.Id, c.Part, c.Units, LotOf(c.Id))).ToList()));

        await runner.AdvanceAsync(bat);
        await runner.OnDockReadsAsync(bat, ReadsFor(cartons, batPo, LotOf));
        foreach (var review in Load().Decisions
            .Where(d => d.Status == DecisionStatus.Pending
                && d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview)
            .ToList())
        {
            await runner.ResolveDecisionAsync(bat, review.DecisionId, true, review.RequestedTo, "confirmed");
        }

        await runner.AdvanceAsync(bat);
        var batLine = (await erp.GetPurchaseOrderAsync(batPo))!.Lines.Single();
        var unmarked = cartons.Single(c => !c.MarkPresent);
        var acceptedUnits = cartons.Sum(c => c.Units) - unmarked.Units;
        var intake = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            bat, $"INV-{batPo}", mainVendor, batPo,
            [new SupplierInvoiceLineInput(batLine.PoLineRef, mainQty, mainPrice)]));
        Assert.True(intake.Persisted);
        Assert.Equal(acceptedUnits * mainPrice, intake.AcceptedValue);

        var invDecision = Pending(Load(), DecisionKind.InvoiceApproval);
        await runner.ResolveDecisionAsync(bat, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");

        await runner.AdvanceAsync(bat);
        var pay1 = Pending(Load(), DecisionKind.PaymentApproval);
        Assert.Equal(acceptedUnits * mainPrice, pay1.Amount);
        await runner.ResolveDecisionAsync(bat, pay1.DecisionId, true, pay1.RequestedTo, "ok");

        var reshot = await runner.OnReshotAsync(new ReshotInput(bat, [unmarked.Id],
            [new DockCartonRead(
                unmarked.Id, unmarked.Part, batPo, LotOf(unmarked.Id), unmarked.Units,
                true, true, "UN3480", true, true,
                false, null, unmarked.ReshotIn ?? "RESHOT", 0.95m)]));
        Assert.All(reshot[unmarked.Id], v => Assert.Equal(VerdictKind.Pass, v.Kind));

        await runner.AdvanceAsync(bat);
        var pay2 = Pending(Load(), DecisionKind.PaymentApproval);
        Assert.NotEqual(pay1.DecisionId, pay2.DecisionId);
        Assert.Equal(unmarked.Units * mainPrice, pay2.Amount);
        await runner.ResolveDecisionAsync(bat, pay2.DecisionId, true, pay2.RequestedTo, "ok");

        var done = await runner.AdvanceAsync(bat);
        Assert.Equal(CaseNextStep.Done, done.Step);
        Assert.True(done.Complete);
        var final = Load();
        Assert.Equal(CaseStage.Resolved, final.Case.Stage);
        Assert.Equal(2, final.Case.PaymentNumbers.Count);
        var settlements = await erp.GetSettlementsAsync(mainVendor, $"INV-{batPo}");
        Assert.Equal(mainQty * mainPrice, settlements.Sum(s => s.SettledAmount));
    }

    private static async Task RunScrewAsync(
        CaseRunner runner, InMemoryErpAdapter erp, string caseDir, string screw)
    {
        CaseFile Load() => CaseStore.Load(caseDir, screw)!;
        static Decision Pending(CaseFile file, DecisionKind kind) =>
            file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

        await runner.AdvanceAsync(screw);
        var poNumber = Load().Case.PoNumber!;
        var po = (await erp.GetPurchaseOrderAsync(poNumber))!;
        var item = Load().Case.ItemId;
        var qty = po.Lines.Single().QtyOrdered;

        await runner.OnSupplierAckAsync(new SupplierAckInput(screw));
        await runner.AdvanceAsync(screw);
        await runner.OnAsnAsync(new SupplierAsnInput(screw, $"ASN-{poNumber}", $"PACK-{poNumber}",
            [new AsnCartonInput($"CTN-{poNumber}", item, (int)qty, $"LOT-{poNumber}")]));
        await runner.AdvanceAsync(screw);
        await runner.OnDockReadsAsync(screw,
            [new DockCartonRead($"CTN-{poNumber}", item, poNumber, $"LOT-{poNumber}", (int)qty,
                true, true, "UN3480", false, false, false, null, "IMG-S1", 0.95m)]);

        await runner.AdvanceAsync(screw);
        var line = (await erp.GetPurchaseOrderAsync(poNumber))!.Lines.Single();
        await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            screw, $"INV-{poNumber}", po.VendorId, poNumber,
            [new SupplierInvoiceLineInput(line.PoLineRef, qty, line.UnitPrice)]));

        var invDecision = Pending(Load(), DecisionKind.InvoiceApproval);
        await runner.ResolveDecisionAsync(screw, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");

        await runner.AdvanceAsync(screw);
        var pay = Pending(Load(), DecisionKind.PaymentApproval);
        Assert.Equal(qty * line.UnitPrice, pay.Amount);
        await runner.ResolveDecisionAsync(screw, pay.DecisionId, true, pay.RequestedTo, "ok");

        var done = await runner.AdvanceAsync(screw);
        Assert.Equal(CaseNextStep.Done, done.Step);
        Assert.True(done.Complete);
        Assert.Equal(CaseStage.Resolved, Load().Case.Stage);
    }
}

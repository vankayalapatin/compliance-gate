// New-order full-cycle live proof: four variety runs from a NEW customer shop
// order (unknown SO minted by the ERP) through payment with real Spark calls.
// Run 1: demo-v1 battery EDI full cycle with reshot and two payments.
// Run 2: alt-v1 battery EDI full cycle (same driver, different numbers).
// Run 3: demo-v1 screw straight-through with a Spark vendor-identity suggest.
// Run 4: demo-v1 battery with a tricky invoice plus vendor email round-trip.
// Every run uses a fresh model cache so every model call is live; a shared
// host-configured spend tracker meters the total and each run writes its own
// step/cost summary to .agent-checks/outbox. Keys and rates come from user
// secrets or the environment, never from the repo.
using System.Globalization;
using System.Text.Json;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Gate.Eval;

public sealed class NewOrderFullCycleLiveTests
{
    private readonly ITestOutputHelper _output;

    public NewOrderFullCycleLiveTests(ITestOutputHelper output) => _output = output;

    private sealed record ScenarioCarton(
        string Id, string Part, int Units, bool MarkPresent, string Damage, IReadOnlyList<string> VisibleIn,
        string? ReshotIn);

    private sealed record LiveSecrets(string ApiKey, string Model, string BaseUrl);

    private sealed record ScenarioData(
        string Name,
        string Currency,
        string SalesItem,
        decimal SalesQty,
        IReadOnlyList<ScenarioCarton> Cartons,
        string MainVendor,
        decimal MainQty,
        decimal MainPrice,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates,
        string Requester,
        string Finance,
        string DelegateTo);

    private sealed class RunSummary
    {
        public string Run { get; set; } = string.Empty;
        public string Scenario { get; set; } = string.Empty;
        public string ShopOrder { get; set; } = string.Empty;
        public string SoNumber { get; set; } = string.Empty;
        public string CaseId { get; set; } = string.Empty;
        public string PoNumber { get; set; } = string.Empty;
        public List<string> Steps { get; } = [];
        public int HttpCalls { get; set; }
        public int MeteredCalls { get; set; }
        public decimal? SpendDeltaUsd { get; set; }
        public decimal? SpendTotalUsd { get; set; }
        public string MeterSummary { get; set; } = string.Empty;
        public List<string> PaymentNumbers { get; } = [];
        public decimal SettledTotal { get; set; }
        public bool Completed { get; set; }
        public string? Error { get; set; }
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

    private static readonly Lazy<ModelSpendTracker> SharedSpend =
        new(() => new ModelSpendTracker(SpendConfig()));

    private static IConfiguration SpendConfig() => new ConfigurationBuilder()
        .AddUserSecrets("compliancegate-gate-web")
        .AddEnvironmentVariables()
        .Build();

    private static LiveSecrets Secrets()
    {
        var config = SpendConfig();
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

    private static async Task<ScenarioData> LoadScenarioAsync(string scenarioPath)
    {
        var resolved = ScenarioLoader.ResolvePath(scenarioPath);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(resolved));
        var root = json.RootElement;
        var cartons = root.GetProperty("cartons").EnumerateArray().Select(c => new ScenarioCarton(
            c.GetProperty("id").GetString()!,
            c.GetProperty("part").GetString()!,
            c.GetProperty("units").GetInt32(),
            c.GetProperty("markPresent").GetBoolean(),
            c.GetProperty("damage").GetString()!,
            c.GetProperty("visibleIn").EnumerateArray().Select(v => v.GetString()!).ToList(),
            c.TryGetProperty("reshotIn", out var reshot) ? reshot.GetString() : null)).ToList();
        var main = root.GetProperty("invoiceIntents").GetProperty("main");
        var candidates = root.GetProperty("vendors").EnumerateArray()
            .GroupBy(v => v.GetProperty("item").GetString()!)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(v => v.GetProperty("id").GetString()!).ToList());
        var users = root.GetProperty("users").EnumerateArray().ToList();
        return new ScenarioData(
            root.GetProperty("name").GetString()!,
            root.GetProperty("currency").GetString()!,
            root.GetProperty("salesOrder").GetProperty("item").GetString()!,
            root.GetProperty("salesOrder").GetProperty("quantity").GetDecimal(),
            cartons,
            main.GetProperty("vendor").GetString()!,
            main.GetProperty("quantity").GetDecimal(),
            main.GetProperty("unitPrice").GetDecimal(),
            candidates,
            users.First(u => u.GetProperty("role").GetString() == "requester").GetProperty("name").GetString()!,
            users.First(u => u.GetProperty("role").GetString() == "payment-approver").GetProperty("name").GetString()!,
            root.GetProperty("delegations").EnumerateArray().First().GetProperty("to").GetString()!);
    }

    private sealed record LiveHarness(
        InMemoryErpAdapter Erp,
        CaseRunner Runner,
        string CaseDir,
        CountingHandler Counter,
        HttpClient Http);

    private static LiveHarness CreateHarness(LiveSecrets secrets, out string tmp)
    {
        var counter = new CountingHandler(new HttpClientHandler());
        var http = new HttpClient(counter);
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = secrets.BaseUrl,
            Model = secrets.Model,
            ApiKey = secrets.ApiKey,
        });
        var metered = new MeteredModelClient(spark, SharedSpend.Value, NullLogger<MeteredModelClient>.Instance);
        var modelCache = Path.Combine(Path.GetTempPath(), "gate-neworder-live", Guid.NewGuid().ToString("N"));
        var models = new CachedModelClient(metered, modelCache);
        tmp = Path.Combine(Path.GetTempPath(), "gate-neworder-live-erp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var erp = new InMemoryErpAdapter();
        var caseDir = Path.Combine(tmp, "cases");
        var compliance = new ComplianceEngine();
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), caseDir);
        return new LiveHarness(erp, runner, caseDir, counter, http);
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

    private static void AssertSparkChoseEveryStep(string caseDir, string caseId)
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
        Assert.Contains(AgentTools.AwardSupplier, executed);
        Assert.Contains(AgentTools.CreatePo, executed);
        Assert.Contains(AgentTools.DraftPayment, executed);
        Assert.Contains(AgentTools.FinishCase, executed);
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

    private void WriteSummary(RunSummary summary)
    {
        var path = Path.Combine(FindOutboxDir(), $"neworder-{summary.Run}.summary.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[neworder-live] run={0} scenario={1} completed={2} steps={3} http={4} metered={5} spend={6} total={7} settled={8} err={9}",
            summary.Run, summary.Scenario, summary.Completed, summary.Steps.Count,
            summary.HttpCalls, summary.MeteredCalls,
            summary.SpendDeltaUsd?.ToString(CultureInfo.InvariantCulture) ?? "unmetered",
            summary.SpendTotalUsd?.ToString(CultureInfo.InvariantCulture) ?? "unmetered",
            summary.SettledTotal.ToString(CultureInfo.InvariantCulture),
            summary.Error ?? "none"));
    }

    private static void FinishMetering(RunSummary summary, CountingHandler counter, int meterBefore, decimal? dollarsBefore)
    {
        var tracker = SharedSpend.Value;
        summary.HttpCalls = counter.Count;
        summary.MeteredCalls = tracker.LiveCallCount - meterBefore;
        summary.SpendDeltaUsd = (tracker.SpendUsd, dollarsBefore) switch
        {
            ({ } after, { } before) => after - before,
            ({ } after, null) => after,
            _ => null,
        };
        summary.SpendTotalUsd = tracker.SpendUsd;
        summary.MeterSummary = tracker.Summary;
        Assert.True(counter.Count > 0, "Expected live Spark calls on a fresh cache.");
        Assert.True(summary.MeteredCalls > 0, "Expected the spend meter to record live calls.");
        Assert.True(summary.MeteredCalls <= counter.Count,
            $"Metered {summary.MeteredCalls} calls but only {counter.Count} HTTP sends were observed.");
    }

    [Fact]
    [Trait("Category", "Live")]
    public Task Run1_DemoV1_NewOrderBatteryFullCycle() =>
        RunBatteryFullCycleAsync("scenarios/demo-v1.json", "run1");

    [Fact]
    [Trait("Category", "Live")]
    public Task Run2_AltV1_NewOrderBatteryFullCycle() =>
        RunBatteryFullCycleAsync("scenarios/alt-v1.json", "run2");

    private async Task RunBatteryFullCycleAsync(string scenarioPath, string run)
    {
        var secrets = Secrets();
        var scenario = await LoadScenarioAsync(scenarioPath);
        using var harness = CreateHarnessWithSeed(secrets, scenarioPath, out _);
        var (erp, runner, caseDir, counter) = harness;
        var summary = new RunSummary { Run = run, Scenario = scenario.Name };
        var meterBefore = SharedSpend.Value.LiveCallCount;
        var dollarsBefore = SharedSpend.Value.SpendUsd;
        try
        {
            summary.ShopOrder = $"SHOP-NEW-{Guid.NewGuid():N}";
            var caseIds = await runner.OnOrderWebhookAsync(new OrderWebhookInput(
                scenario.Name, summary.ShopOrder, scenario.Requester, scenario.Candidates,
                Lines: [new SalesOrderLine(scenario.SalesItem, scenario.SalesQty)],
                Currency: scenario.Currency));
            Assert.Equal(2, caseIds.Count);
            var batItem = scenario.Cartons[0].Part;
            var bat = caseIds.Single(c => c.EndsWith("-" + batItem, StringComparison.Ordinal));
            summary.CaseId = bat;
            summary.SoNumber = CaseStore.Load(caseDir, bat)!.Case.SoNumber;
            Assert.NotEqual(summary.ShopOrder, summary.SoNumber);
            summary.Steps.Add("webhook:2-cases");

            CaseFile Load() => CaseStore.Load(caseDir, bat)!;
            static Decision Pending(CaseFile file, DecisionKind kind) =>
                file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

            await runner.AdvanceAsync(bat);
            var prDecision = Pending(Load(), DecisionKind.PrApproval);
            Assert.Equal(scenario.DelegateTo, prDecision.RequestedTo);
            await runner.ResolveDecisionAsync(bat, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");
            summary.Steps.Add("pr-approved");

            await runner.AdvanceAsync(bat);
            var poDecision = Pending(Load(), DecisionKind.PoApproval);
            Assert.Equal(scenario.DelegateTo, poDecision.RequestedTo);
            await runner.ResolveDecisionAsync(bat, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");
            summary.Steps.Add("po-approved");

            var batPo = Load().Case.PoNumber!;
            summary.PoNumber = batPo;
            Assert.Equal(scenario.MainVendor, (await erp.GetPurchaseOrderAsync(batPo))!.VendorId);
            Assert.Equal(
                ComplianceEngine.CorpOriginCapId,
                Load().Events.Single(e => e.Kind == CaseEventKind.VendorBlocked).RuleId);
            summary.Steps.Add("po-placed-vendor-blocked");

            await runner.AdvanceAsync(bat);
            await runner.OnSupplierAckAsync(new SupplierAckInput(bat));
            summary.Steps.Add("ack");
            await runner.AdvanceAsync(bat);
            string LotOf(string cartonId) => $"{batPo}-{cartonId}";
            await runner.OnAsnAsync(new SupplierAsnInput(bat, $"ASN-{batPo}", $"PACK-{batPo}",
                scenario.Cartons.Select(c => new AsnCartonInput(c.Id, c.Part, c.Units, LotOf(c.Id))).ToList()));
            summary.Steps.Add("asn");

            await runner.AdvanceAsync(bat);
            var verdicts = await runner.OnDockReadsAsync(bat, ReadsFor(scenario.Cartons, batPo, LotOf));
            var unmarked = scenario.Cartons.Single(c => !c.MarkPresent);
            Assert.Contains(verdicts[unmarked.Id],
                v => v.Kind == VerdictKind.Hold && v.RuleId == DockEvaluator.MarkRuleId);
            foreach (var review in Load().Decisions
                .Where(d => d.Status == DecisionStatus.Pending
                    && d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview)
                .ToList())
            {
                await runner.ResolveDecisionAsync(bat, review.DecisionId, true, review.RequestedTo, "confirmed");
            }

            summary.Steps.Add("dock");

            await runner.AdvanceAsync(bat);
            var batLine = (await erp.GetPurchaseOrderAsync(batPo))!.Lines.Single();
            var heldUnits = unmarked.Units;
            var acceptedUnits = scenario.Cartons.Sum(c => c.Units) - heldUnits;
            var intake = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
                bat, $"INV-{batPo}", scenario.MainVendor, batPo,
                [new SupplierInvoiceLineInput(batLine.PoLineRef, scenario.MainQty, scenario.MainPrice)]));
            Assert.True(intake.Persisted);
            Assert.Equal(acceptedUnits * scenario.MainPrice, intake.AcceptedValue);
            Assert.Equal(heldUnits * scenario.MainPrice, intake.HeldValue);
            summary.Steps.Add("invoice");

            var invDecision = Pending(Load(), DecisionKind.InvoiceApproval);
            Assert.Equal("Invoice", invDecision.DocumentType);
            Assert.Equal($"{scenario.MainVendor}/INV-{batPo}", invDecision.DocumentNumber);
            await runner.ResolveDecisionAsync(bat, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");
            summary.Steps.Add("inv-approved");

            await runner.AdvanceAsync(bat);
            var pay1 = Pending(Load(), DecisionKind.PaymentApproval);
            Assert.Equal(scenario.Finance, pay1.RequestedTo);
            Assert.Equal(acceptedUnits * scenario.MainPrice, pay1.Amount);
            await runner.ResolveDecisionAsync(bat, pay1.DecisionId, true, pay1.RequestedTo, "ok");
            summary.Steps.Add("pay1");

            var reshot = await runner.OnReshotAsync(new ReshotInput(bat, [unmarked.Id],
                [new DockCartonRead(
                    unmarked.Id, unmarked.Part, batPo, LotOf(unmarked.Id), unmarked.Units,
                    true, true, "UN3480", true, true,
                    false, null, unmarked.ReshotIn ?? "RESHOT", 0.95m)]));
            Assert.All(reshot[unmarked.Id], v => Assert.Equal(VerdictKind.Pass, v.Kind));
            summary.Steps.Add("reshot");

            await runner.AdvanceAsync(bat);
            var pay2 = Pending(Load(), DecisionKind.PaymentApproval);
            Assert.NotEqual(pay1.DecisionId, pay2.DecisionId);
            Assert.Equal(scenario.Finance, pay2.RequestedTo);
            Assert.Equal(heldUnits * scenario.MainPrice, pay2.Amount);
            await runner.ResolveDecisionAsync(bat, pay2.DecisionId, true, pay2.RequestedTo, "ok");
            summary.Steps.Add("pay2");

            var done = await runner.AdvanceAsync(bat);
            Assert.Equal(CaseNextStep.Done, done.Step);
            Assert.True(done.Complete);
            var final = Load();
            Assert.Equal(CaseStage.Resolved, final.Case.Stage);
            Assert.Equal(2, final.Case.PaymentNumbers.Count);
            summary.PaymentNumbers.AddRange(final.Case.PaymentNumbers);
            var settlements = await erp.GetSettlementsAsync(scenario.MainVendor, $"INV-{batPo}");
            summary.SettledTotal = settlements.Sum(s => s.SettledAmount);
            Assert.Equal(scenario.MainQty * scenario.MainPrice, summary.SettledTotal);
            Assert.Equal(InvoiceStatus.Paid, (await erp.GetInvoiceAsync(scenario.MainVendor, $"INV-{batPo}"))!.Status);
            summary.Steps.Add("resolved");

            AssertSparkChoseEveryStep(caseDir, bat);
            FinishMetering(summary, counter, meterBefore, dollarsBefore);
            summary.Completed = true;
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

    private sealed class SeededHarness : IDisposable
    {
        private readonly HttpClient _http;

        public SeededHarness(LiveHarness inner)
        {
            Erp = inner.Erp;
            Runner = inner.Runner;
            CaseDir = inner.CaseDir;
            Counter = inner.Counter;
            _http = inner.Http;
        }

        public InMemoryErpAdapter Erp { get; }

        public CaseRunner Runner { get; }

        public string CaseDir { get; }

        public CountingHandler Counter { get; }

        public void Deconstruct(
            out InMemoryErpAdapter erp, out CaseRunner runner, out string caseDir, out CountingHandler counter)
        {
            erp = Erp;
            runner = Runner;
            caseDir = CaseDir;
            counter = Counter;
        }

        public void Dispose() => _http.Dispose();
    }

    private static SeededHarness CreateHarnessWithSeed(LiveSecrets secrets, string scenarioPath, out string tmp)
    {
        var inner = CreateHarness(secrets, out tmp);
        var resolved = ScenarioLoader.ResolvePath(scenarioPath);
        ScenarioLoader.LoadAsync(
            inner.Erp, resolved, Path.Combine(tmp, "gate.db"), Path.Combine(tmp, "manifest.json"))
            .GetAwaiter().GetResult();
        return new SeededHarness(inner);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Run3_DemoV1_NewOrderScrewIdentityFullCycle()
    {
        const string run = "run3";
        const string scenarioPath = "scenarios/demo-v1.json";
        var secrets = Secrets();
        var scenario = await LoadScenarioAsync(scenarioPath);
        using var harness = CreateHarnessWithSeed(secrets, scenarioPath, out _);
        var (erp, runner, caseDir, counter) = harness;
        var summary = new RunSummary { Run = run, Scenario = scenario.Name };
        var meterBefore = SharedSpend.Value.LiveCallCount;
        var dollarsBefore = SharedSpend.Value.SpendUsd;
        try
        {
            summary.ShopOrder = $"SHOP-NEW-{Guid.NewGuid():N}";
            var caseIds = await runner.OnOrderWebhookAsync(new OrderWebhookInput(
                scenario.Name, summary.ShopOrder, scenario.Requester, scenario.Candidates,
                Lines: [new SalesOrderLine(scenario.SalesItem, scenario.SalesQty)],
                Currency: scenario.Currency));
            Assert.Equal(2, caseIds.Count);
            var batItem = scenario.Cartons[0].Part;
            var screw = caseIds.Single(c => !c.EndsWith("-" + batItem, StringComparison.Ordinal));
            summary.CaseId = screw;
            summary.SoNumber = CaseStore.Load(caseDir, screw)!.Case.SoNumber;
            Assert.NotEqual(summary.ShopOrder, summary.SoNumber);
            summary.Steps.Add("webhook:2-cases");

            CaseFile Load() => CaseStore.Load(caseDir, screw)!;
            static Decision Pending(CaseFile file, DecisionKind kind) =>
                file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

            await runner.AdvanceAsync(screw);
            var ordered = Load();
            Assert.DoesNotContain(ordered.Decisions,
                d => d.Kind is DecisionKind.PrApproval or DecisionKind.PoApproval);
            var poNumber = ordered.Case.PoNumber!;
            summary.PoNumber = poNumber;
            var po = (await erp.GetPurchaseOrderAsync(poNumber))!;
            var item = ordered.Case.ItemId;
            var qty = po.Lines.Single().QtyOrdered;
            summary.Steps.Add("auto-po");

            await runner.OnSupplierAckAsync(new SupplierAckInput(screw));
            summary.Steps.Add("ack");
            await runner.AdvanceAsync(screw);
            await runner.OnAsnAsync(new SupplierAsnInput(screw, $"ASN-{poNumber}", $"PACK-{poNumber}",
                [new AsnCartonInput($"CTN-{poNumber}", item, (int)qty, $"LOT-{poNumber}")]));
            summary.Steps.Add("asn");
            await runner.AdvanceAsync(screw);
            var verdicts = await runner.OnDockReadsAsync(screw,
                [new DockCartonRead($"CTN-{poNumber}", item, poNumber, $"LOT-{poNumber}", (int)qty,
                    true, true, "UN3480", false, false, false, null, "IMG-S1", 0.95m)]);
            Assert.All(verdicts.Values.SelectMany(v => v), v => Assert.Equal(VerdictKind.Pass, v.Kind));
            summary.Steps.Add("dock");

            await runner.AdvanceAsync(screw);
            var line = (await erp.GetPurchaseOrderAsync(poNumber))!.Lines.Single();
            var vendorMaster = (await erp.GetVendorsAsync()).Single(v =>
                string.Equals(v.VendorId, po.VendorId, StringComparison.Ordinal));
            var bank = (await erp.GetVendorBankAsync(po.VendorId))?.AccountRef;
            Assert.False(string.IsNullOrWhiteSpace(bank));
            var invNumber = $"INV-{poNumber}";
            var fuzzyVendorText = $"{vendorMaster.Name} ({vendorMaster.VendorId})";
            var meterAtPropose = SharedSpend.Value.LiveCallCount;
            var counterAtPropose = counter.Count;
            var card = await runner.ProposeVendorIdentityAsync(
                screw, fuzzyVendorText, [$"edi:{invNumber}"],
                new VendorIdentityIntake(
                    invNumber, poNumber,
                    [new SupplierInvoiceLineInput(line.PoLineRef, qty, line.UnitPrice)],
                    bank));
            Assert.NotNull(card);
            Assert.Equal(DecisionKind.VendorIdentity, card.Kind);
            Assert.Equal("Vendor", card.DocumentType);
            Assert.Equal(po.VendorId, card.DocumentNumber);
            Assert.True(SharedSpend.Value.LiveCallCount > meterAtPropose,
                "Vendor-identity propose made no live Spark call.");
            Assert.True(counter.Count > counterAtPropose,
                "Vendor-identity propose made no HTTP call.");
            summary.Steps.Add("identity-proposed");
            await runner.ResolveDecisionAsync(screw, card.DecisionId, true, card.RequestedTo, "confirmed");
            var identified = await erp.GetInvoiceAsync(po.VendorId, invNumber);
            Assert.NotNull(identified);
            Assert.All(identified.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
            summary.Steps.Add("identity-approved");

            var invoiced = Load();
            var invDecision = Pending(invoiced, DecisionKind.InvoiceApproval);
            await runner.ResolveDecisionAsync(screw, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");
            summary.Steps.Add("inv-approved");

            await runner.AdvanceAsync(screw);
            var drafted = Load();
            Assert.Equal(CaseStage.PaymentRequested, drafted.Case.Stage);
            var pay = Pending(drafted, DecisionKind.PaymentApproval);
            Assert.Equal(scenario.Finance, pay.RequestedTo);
            Assert.Equal(qty * line.UnitPrice, pay.Amount);
            await runner.ResolveDecisionAsync(screw, pay.DecisionId, true, pay.RequestedTo, "ok");
            summary.Steps.Add("pay");

            var done = await runner.AdvanceAsync(screw);
            Assert.Equal(CaseNextStep.Done, done.Step);
            Assert.True(done.Complete);
            var final = Load();
            Assert.Equal(CaseStage.Resolved, final.Case.Stage);
            Assert.Single(final.Case.PaymentNumbers);
            summary.PaymentNumbers.AddRange(final.Case.PaymentNumbers);
            Assert.Equal(
                qty * line.UnitPrice,
                (await erp.GetPaymentAsync(final.Case.PaymentNumbers[0]))!.Lines.Sum(l => l.Amount));
            var settlements = await erp.GetSettlementsAsync(po.VendorId, invNumber);
            summary.SettledTotal = settlements.Sum(s => s.SettledAmount);
            Assert.Equal(qty * line.UnitPrice, summary.SettledTotal);
            summary.Steps.Add("resolved");

            AssertSparkChoseEveryStep(caseDir, screw);
            FinishMetering(summary, counter, meterBefore, dollarsBefore);
            summary.Completed = true;
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

    [Fact]
    [Trait("Category", "Live")]
    public async Task Run4_DemoV1_NewOrderBatteryEmailRoundTrip()
    {
        const string run = "run4";
        const string scenarioPath = "scenarios/demo-v1.json";
        var secrets = Secrets();
        var scenario = await LoadScenarioAsync(scenarioPath);
        using var harness = CreateHarnessWithSeed(secrets, scenarioPath, out _);
        var (erp, runner, caseDir, counter) = harness;
        var summary = new RunSummary { Run = run, Scenario = scenario.Name };
        var meterBefore = SharedSpend.Value.LiveCallCount;
        var dollarsBefore = SharedSpend.Value.SpendUsd;
        try
        {
            summary.ShopOrder = $"SHOP-NEW-{Guid.NewGuid():N}";
            var caseIds = await runner.OnOrderWebhookAsync(new OrderWebhookInput(
                scenario.Name, summary.ShopOrder, scenario.Requester, scenario.Candidates,
                Lines: [new SalesOrderLine(scenario.SalesItem, scenario.SalesQty)],
                Currency: scenario.Currency));
            Assert.Equal(2, caseIds.Count);
            var batItem = scenario.Cartons[0].Part;
            var bat = caseIds.Single(c => c.EndsWith("-" + batItem, StringComparison.Ordinal));
            summary.CaseId = bat;
            summary.SoNumber = CaseStore.Load(caseDir, bat)!.Case.SoNumber;
            Assert.NotEqual(summary.ShopOrder, summary.SoNumber);
            summary.Steps.Add("webhook:2-cases");

            CaseFile Load() => CaseStore.Load(caseDir, bat)!;
            static Decision Pending(CaseFile file, DecisionKind kind) =>
                file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

            await runner.AdvanceAsync(bat);
            var prDecision = Pending(Load(), DecisionKind.PrApproval);
            await runner.ResolveDecisionAsync(bat, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");
            summary.Steps.Add("pr-approved");

            await runner.AdvanceAsync(bat);
            var poDecision = Pending(Load(), DecisionKind.PoApproval);
            await runner.ResolveDecisionAsync(bat, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");
            summary.Steps.Add("po-approved");

            var batPo = Load().Case.PoNumber!;
            summary.PoNumber = batPo;
            Assert.Equal(scenario.MainVendor, (await erp.GetPurchaseOrderAsync(batPo))!.VendorId);
            summary.Steps.Add("po-placed");

            await runner.AdvanceAsync(bat);
            await runner.OnSupplierAckAsync(new SupplierAckInput(bat));
            summary.Steps.Add("ack");
            await runner.AdvanceAsync(bat);
            string LotOf(string cartonId) => $"{batPo}-{cartonId}";
            await runner.OnAsnAsync(new SupplierAsnInput(bat, $"ASN-{batPo}", $"PACK-{batPo}",
                scenario.Cartons.Select(c => new AsnCartonInput(c.Id, c.Part, c.Units, LotOf(c.Id))).ToList()));
            summary.Steps.Add("asn");

            await runner.AdvanceAsync(bat);
            var verdicts = await runner.OnDockReadsAsync(bat, ReadsFor(scenario.Cartons, batPo, LotOf));
            var unmarked = scenario.Cartons.Single(c => !c.MarkPresent);
            Assert.Contains(verdicts[unmarked.Id],
                v => v.Kind == VerdictKind.Hold && v.RuleId == DockEvaluator.MarkRuleId);
            foreach (var review in Load().Decisions
                .Where(d => d.Status == DecisionStatus.Pending
                    && d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview)
                .ToList())
            {
                await runner.ResolveDecisionAsync(bat, review.DecisionId, true, review.RequestedTo, "confirmed");
            }

            summary.Steps.Add("dock");

            await runner.AdvanceAsync(bat);
            var batLine = (await erp.GetPurchaseOrderAsync(batPo))!.Lines.Single();
            var acceptedUnits = scenario.Cartons.Sum(c => c.Units) - unmarked.Units;
            var shortQty = 5m;
            var intake = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
                bat, $"INV-{batPo}", scenario.MainVendor, batPo,
                [new SupplierInvoiceLineInput(batLine.PoLineRef, scenario.MainQty - shortQty, scenario.MainPrice)]));
            Assert.True(intake.Persisted);
            Assert.Equal(acceptedUnits * scenario.MainPrice, intake.AcceptedValue);
            summary.Steps.Add("invoice-underbilled");

            var invDecision = Pending(Load(), DecisionKind.InvoiceApproval);
            await runner.ResolveDecisionAsync(bat, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");
            summary.Steps.Add("inv-approved");

            await runner.AdvanceAsync(bat);
            var pay1 = Pending(Load(), DecisionKind.PaymentApproval);
            Assert.Equal(acceptedUnits * scenario.MainPrice, pay1.Amount);
            await runner.ResolveDecisionAsync(bat, pay1.DecisionId, true, pay1.RequestedTo, "ok");
            summary.Steps.Add("pay1");

            var reshot = await runner.OnReshotAsync(new ReshotInput(bat, [unmarked.Id],
                [new DockCartonRead(
                    unmarked.Id, unmarked.Part, batPo, LotOf(unmarked.Id), unmarked.Units,
                    true, true, "UN3480", true, true,
                    false, null, unmarked.ReshotIn ?? "RESHOT", 0.95m)]));
            Assert.All(reshot[unmarked.Id], v => Assert.Equal(VerdictKind.Pass, v.Kind));
            summary.Steps.Add("reshot");

            var trickyNumber = $"INV-MISS-{batPo}";
            var wrongPo = $"{batPo}-WRONG";
            var tricky = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
                bat, trickyNumber, scenario.MainVendor, wrongPo,
                [new SupplierInvoiceLineInput(batLine.PoLineRef, shortQty, scenario.MainPrice)]));
            Assert.False(tricky.Persisted);
            var problem = Load().Decisions.Single(d =>
                d.Kind == DecisionKind.ApReview && d.Status == DecisionStatus.Pending);
            Assert.Equal($"{scenario.MainVendor}/{trickyNumber}", problem.DocumentNumber);
            summary.Steps.Add("tricky-invoice");

            var laterPays = new List<decimal>();
            var meterAtMail = SharedSpend.Value.LiveCallCount;
            for (var round = 0; round < 6; round++)
            {
                await runner.AdvanceAsync(bat);
                if (Load().Events.Any(e => e.Kind == CaseEventKind.VendorEmailed))
                {
                    break;
                }

                var pendingPay = Load().Decisions.FirstOrDefault(d =>
                    d.Kind == DecisionKind.PaymentApproval && d.Status == DecisionStatus.Pending);
                if (pendingPay is not null)
                {
                    if (pendingPay.Amount is not decimal loopAmount)
                    {
                        throw new InvalidOperationException("Pay decision carries no amount.");
                    }

                    laterPays.Add(loopAmount);
                    await runner.ResolveDecisionAsync(bat, pendingPay.DecisionId, true, pendingPay.RequestedTo, "ok");
                }
            }

            var query = EmailStore.Load(caseDir, bat).Messages.SingleOrDefault();
            Assert.True(query is not null, "Spark never emailed the vendor about the tricky invoice.");
            Assert.Equal($"{scenario.MainVendor}/{trickyNumber}", query.InvoiceRef);
            Assert.True(SharedSpend.Value.LiveCallCount > meterAtMail,
                "Vendor email query made no live Spark call.");
            summary.Steps.Add("vendor-queried");

            var vendorMaster = (await erp.GetVendorsAsync()).Single(v =>
                string.Equals(v.VendorId, scenario.MainVendor, StringComparison.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(vendorMaster.Email));
            var fixNumber = $"INV-FIX-{batPo}";
            var reply = await runner.OnVendorReplyAsync(new VendorReplyInput(
                bat, vendorMaster.Email!,
                CorporateReplyBody(
                    vendorMaster.Name, scenario.MainVendor, batPo, batLine.PoLineRef,
                    scenario.MainPrice, shortQty, fixNumber, $"INV-{batPo}"),
                InReplyTo: query.EmailId, EmailId: $"{bat}-r-001"));
            Assert.Equal(scenario.MainVendor, reply.VendorId);
            var fixedInvoice = await erp.GetInvoiceAsync(scenario.MainVendor, fixNumber);
            Assert.NotNull(fixedInvoice);
            Assert.All(fixedInvoice.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
            Assert.Contains(Load().Events, e =>
                e.Kind == CaseEventKind.Note
                && (e.Summary ?? string.Empty).Contains(
                    $"re-intaked as {scenario.MainVendor}/{fixNumber}", StringComparison.Ordinal));
            summary.Steps.Add("vendor-replied");

            var fixApproval = Load().Decisions.Single(d =>
                d.Kind == DecisionKind.InvoiceApproval
                && d.Status == DecisionStatus.Pending
                && string.Equals(d.DocumentNumber, $"{scenario.MainVendor}/{fixNumber}", StringComparison.Ordinal));
            await runner.ResolveDecisionAsync(bat, fixApproval.DecisionId, true, fixApproval.RequestedTo, "ok");
            var apReview = Load().Decisions.Single(d =>
                d.Kind == DecisionKind.ApReview && d.Status == DecisionStatus.Pending);
            await runner.ResolveDecisionAsync(bat, apReview.DecisionId, true, apReview.RequestedTo, "correction accepted");
            summary.Steps.Add("fix-approved");

            CaseAdvanceOutcome done;
            for (var round = 0; ; round++)
            {
                done = await runner.AdvanceAsync(bat);
                if (done.Complete)
                {
                    break;
                }

                Assert.True(round < 6, "Pay rounds did not converge; see the transcript.");
                var pay = Pending(Load(), DecisionKind.PaymentApproval);
                if (round == 0)
                {
                    Assert.NotEqual(pay1.DecisionId, pay.DecisionId);
                }

                if (pay.Amount is not decimal roundAmount)
                {
                    throw new InvalidOperationException("Pay decision carries no amount.");
                }

                laterPays.Add(roundAmount);
                await runner.ResolveDecisionAsync(bat, pay.DecisionId, true, pay.RequestedTo, "ok");
            }

            Assert.Equal(CaseNextStep.Done, done.Step);
            Assert.Equal(unmarked.Units * scenario.MainPrice, laterPays.Sum());
            var final = Load();
            Assert.Equal(CaseStage.Resolved, final.Case.Stage);
            Assert.InRange(final.Case.PaymentNumbers.Count, 2, 3);
            summary.PaymentNumbers.AddRange(final.Case.PaymentNumbers);
            var mainSettled = await erp.GetSettlementsAsync(scenario.MainVendor, $"INV-{batPo}");
            var fixSettled = await erp.GetSettlementsAsync(scenario.MainVendor, fixNumber);
            summary.SettledTotal = mainSettled.Sum(s => s.SettledAmount) + fixSettled.Sum(s => s.SettledAmount);
            Assert.Equal(scenario.MainQty * scenario.MainPrice, summary.SettledTotal);
            summary.Steps.Add("resolved");

            AssertSparkChoseEveryStep(caseDir, bat);
            FinishMetering(summary, counter, meterBefore, dollarsBefore);
            summary.Completed = true;
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

    private static string CorporateReplyBody(
        string vendorName, string vendorId, string poNumber, string poLineRef, decimal unitPrice,
        decimal shortQty, string fixNumber, string mainNumber) =>
        "Hi,\n" +
        "\n" +
        "Thanks for flagging this — you're right, the last invoice went out with an old PO reference on our side. " +
        "Sorry for the confusion.\n" +
        "\n" +
        $"Please book those {shortQty.ToString(CultureInfo.InvariantCulture)} units as invoice {fixNumber} " +
        $"under {vendorName} ({vendorId}) against your PO {poNumber}, " +
        $"line {poLineRef}, at {unitPrice.ToString(CultureInfo.InvariantCulture)} per unit. " +
        $"Everything else on the shipment stays as billed on {mainNumber}.\n" +
        "\n" +
        "Let me know if you need anything else from us.\n" +
        "\n" +
        "Best regards,\n" +
        "Maria Santos\n" +
        $"Accounts Receivable, {vendorName}";
}

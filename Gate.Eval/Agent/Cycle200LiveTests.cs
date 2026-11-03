// 200 cycle (live): restores the shipped company baseline into temp
// dirs, then drives a 200-glasses order through the same runner entry
// points the app's screens and simulation endpoints call, with live
// Spark and split proposals on. The battery shortfall fits under the
// country cap, so the award stays single-vendor; the shipment's photos
// come from the dock scene library for its own carton count. Every
// step lands in .agent-checks/outbox/walk-200.summary.json.
using System.Globalization;
using System.Text.Json;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web;
using Gate.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Gate.Eval;

public sealed class Cycle200LiveTests
{
    private const string EntityCode = "LE-1";
    private const string SalesItem = "GLASS-1";
    private const decimal SalesQty = 200m;
    private const string BatteryItem = "BAT-1";
    private const string Currency = "USD";
    private const string Requester = "Maya Planner";

    private readonly ITestOutputHelper _output;

    public Cycle200LiveTests(ITestOutputHelper output) => _output = output;

    private sealed class CycleSummary
    {
        public string Run { get; set; } = "cycle-200";
        public string ShopOrder { get; set; } = string.Empty;
        public string SoNumber { get; set; } = string.Empty;
        public string BatteryCase { get; set; } = string.Empty;
        public string SiblingCase { get; set; } = string.Empty;
        public string SiblingStage { get; set; } = string.Empty;
        public List<string> Steps { get; } = [];
        public string PrNumber { get; set; } = string.Empty;
        public decimal PrQty { get; set; }
        public string PrApprovedBy { get; set; } = string.Empty;
        public string AwardedVendorId { get; set; } = string.Empty;
        public string AwardedVendorName { get; set; } = string.Empty;
        public string PoNumber { get; set; } = string.Empty;
        public string PoApprovedBy { get; set; } = string.Empty;
        public decimal PoTotal { get; set; }
        public string AsnNumber { get; set; } = string.Empty;
        public string? ReceiptNumber { get; set; }
        public int CartonCount { get; set; }
        public List<string> DockImages { get; } = [];
        public int PhotoCount { get; set; }
        public int ReadCount { get; set; }
        public List<string> HeldCartons { get; } = [];
        public List<string> ReviewedCartons { get; } = [];
        public decimal HeldValue { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public decimal InvoicedTotal { get; set; }
        public List<string> PaymentNumbers { get; } = [];
        public decimal SettledTotal { get; set; }
        public string BatteryStage { get; set; } = string.Empty;
        public bool CustomerNotified { get; set; }
        public int HttpCalls { get; set; }
        public int MeteredCalls { get; set; }
        public decimal? SpendDeltaUsd { get; set; }
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
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "MetaApi:ApiKey is missing.");
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing.");
        return new LiveSecrets(
            apiKey!, model!,
            string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Gate.Web", "data", "baseline")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Repo root with Gate.Web/data/baseline not found.");
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

    private void WriteSummary(CycleSummary summary)
    {
        var path = Path.Combine(FindOutboxDir(), "walk-200.summary.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[cycle-200] completed={0} steps={1} http={2} metered={3} spend={4} err={5}",
            summary.Completed, summary.Steps.Count, summary.HttpCalls, summary.MeteredCalls,
            summary.SpendDeltaUsd?.ToString(CultureInfo.InvariantCulture) ?? "unmetered",
            summary.Error ?? "none"));
    }

    private static Decision Pending(CaseFile file, DecisionKind kind) =>
        file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

    [Fact]
    [Trait("Category", "Live")]
    public async Task Cycle200_SingleAward_ThroughPayment()
    {
        var secrets = Secrets();
        var root = FindRepoRoot();
        var summary = new CycleSummary { ShopOrder = $"SHOP-200-{Guid.NewGuid():N}" };
        var counter = new CountingHandler(new HttpClientHandler());
        using var http = new HttpClient(counter);

        var work = Path.Combine(Path.GetTempPath(), "gate-cycle-200", Guid.NewGuid().ToString("N"));
        var liveErp = Path.Combine(work, "erp.db");
        var liveCache = Path.Combine(work, "case-cache");
        var modelCache = Path.Combine(work, "model-cache");
        var visionReads = Path.Combine(work, "vision-reads");
        Directory.CreateDirectory(work);

        foreach (var file in Directory.GetFiles(Path.Combine(root, "assets", "read-cache")))
        {
            Directory.CreateDirectory(visionReads);
            File.Copy(file, Path.Combine(visionReads, Path.GetFileName(file)));
        }

        var spend = new ModelSpendTracker(new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build());
        var meterBefore = spend.LiveCallCount;
        var dollarsBefore = spend.SpendUsd;

        try
        {
            var restored = await BaselineRestore.RestoreAsync(
                Path.Combine(root, "Gate.Web", "data", "baseline"),
                liveErp, liveCache, NullLogger.Instance);
            summary.Steps.Add("restore:" + string.Join(" | ", restored));

            var options = new DbContextOptionsBuilder<ErpDbContext>()
                .UseSqlite($"Data Source={liveErp}")
                .Options;
            await using (var db = new ErpDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            var compliance = new ComplianceEngine();
            var erp = new EfCoreErpAdapter(options, new ErpPolicy(), new CasePolicyContext(liveCache));
            var spark = new SparkModelClient(http, new SparkOptions
            {
                BaseUrl = secrets.BaseUrl,
                Model = secrets.Model,
                ApiKey = secrets.ApiKey,
            });
            var models = new CachedModelClient(
                new MeteredModelClient(spark, spend, NullLogger<MeteredModelClient>.Instance), modelCache);
            var appConfig = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(root, "Gate.Web", "appsettings.json"))
                .Build();
            var splits = new SplitProposals();
            Assert.True(splits.IsOn, "Split proposals must be on for this cycle.");
            var scenes = new DockSceneLive();
            Assert.False(scenes.LiveOn, "The library path proves this cycle; live scenes stay off.");
            var runner = new CaseRunner(
                erp, new P2pAgent(erp, models, compliance), models, compliance,
                new DockEvaluatorService(new ComplianceEngine()), liveCache, NullLogger<CaseRunner>.Instance,
                visionReadCacheDir: visionReads,
                dockImageOptions: new DockImageOptions(
                    Path.Combine(root, "assets", "dock-scenes", "bases"),
                    Path.Combine(root, "assets", "dock-scenes", "scenes.json"),
                    Path.Combine(root, "assets", "cartons.json"),
                    Path.Combine(work, "dock-out"),
                    Path.Combine(work, "dock-img-cache"),
                    visionReads),
                countryNames: CountryNames.FromConfig(appConfig).Map,
                splitProposals: splits,
                dockSceneLive: scenes);

            var vendors = (await erp.GetVendorsAsync())
                .ToDictionary(v => v.VendorId, v => v.Name, StringComparer.Ordinal);
            var battery = await erp.GetItemAsync(BatteryItem);
            Assert.NotNull(battery);

            var candidates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                [BatteryItem] = await erp.GetPricedVendorIdsAsync(BatteryItem),
            };
            foreach (var item in new[] { "SCR-9" })
            {
                candidates[item] = await erp.GetPricedVendorIdsAsync(item);
            }

            var caseIds = await runner.OnOrderWebhookAsync(new OrderWebhookInput(
                EntityCode, summary.ShopOrder, Requester, candidates,
                Guid.NewGuid().ToString("N"),
                Lines: [new SalesOrderLine(SalesItem, SalesQty)],
                Currency: Currency));
            var bat = Assert.Single(caseIds);
            Assert.EndsWith("-" + BatteryItem, bat, StringComparison.Ordinal);
            summary.BatteryCase = bat;
            summary.SiblingCase = "(none)";
            summary.SiblingStage = "no shortfall";
            summary.SoNumber = CaseStore.Load(liveCache, bat)!.Case.SoNumber;
            Assert.NotEqual(summary.ShopOrder, summary.SoNumber);
            summary.Steps.Add($"webhook:1-case so={summary.SoNumber}");
            CaseFile Load() => CaseStore.Load(liveCache, bat)!;

            await AdvanceWithRetryAsync(runner, bat, summary);

            var prDecision = Pending(Load(), DecisionKind.PrApproval);
            summary.PrApprovedBy = prDecision.RequestedTo;
            var pr = (await erp.GetPrAsync(Load().Case.PrNumber!))!;
            summary.PrNumber = pr.PrNumber;
            summary.PrQty = pr.Lines.Sum(l => l.Quantity);
            await runner.ResolveDecisionAsync(bat, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");
            await AdvanceWithRetryAsync(runner, bat, summary);
            summary.Steps.Add($"pr-approved:{summary.PrNumber} qty={summary.PrQty} by={summary.PrApprovedBy}");

            // Under the cap the award stays single-vendor: no split card.
            var afterAward = Load();
            Assert.DoesNotContain(
                afterAward.Decisions,
                d => d.Kind == DecisionKind.SplitApproval);
            Assert.NotNull(afterAward.Case.PoNumber);
            var po = (await erp.GetPurchaseOrderAsync(afterAward.Case.PoNumber))!;
            var poLine = po.Lines.Single();
            summary.AwardedVendorId = po.VendorId;
            summary.AwardedVendorName = vendors.GetValueOrDefault(po.VendorId, po.VendorId);
            summary.PoNumber = po.PoNumber;
            summary.PoTotal = poLine.LineAmount;
            summary.Steps.Add($"awarded:{po.PoNumber} {po.VendorId} qty={poLine.QtyOrdered} total={poLine.LineAmount}");

            var poDecision = Load().Decisions.SingleOrDefault(d =>
                d.Kind == DecisionKind.PoApproval && d.Status == DecisionStatus.Pending);
            if (poDecision is null)
            {
                var refreshed = (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
                summary.PoApprovedBy = refreshed.ApprovedBy ?? "auto-approved under threshold";
                summary.Steps.Add($"po-auto:{po.PoNumber} {refreshed.ApprovalStatus}");
            }
            else
            {
                summary.PoApprovedBy = poDecision.RequestedTo;
                await runner.ResolveDecisionAsync(bat, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");
                await AdvanceWithRetryAsync(runner, bat, summary);
                summary.Steps.Add($"po-approved:{po.PoNumber} by={summary.PoApprovedBy}");
            }

            await runner.OnSupplierAckAsync(new SupplierAckInput(bat));
            await AdvanceWithRetryAsync(runner, bat, summary);
            summary.Steps.Add($"ack:{po.PoNumber}");

            var cartonCount = (int)(poLine.QtyOrdered / battery.UnitsPerCarton);
            Assert.True(cartonCount > 0, "The award must fill at least one carton.");
            summary.CartonCount = cartonCount;
            var cartonIds = Enumerable.Range(1, cartonCount)
                .Select(n => $"CTN-{n:00}")
                .ToList();
            summary.AsnNumber = $"ASN-2026-200-{po.VendorId}";
            var (asn, receipt) = await runner.OnAsnAsync(new SupplierAsnInput(
                bat, summary.AsnNumber, $"SLIP-2026-200-{po.VendorId}",
                cartonIds.Select((c, n) => new AsnCartonInput(
                    c, BatteryItem, battery.UnitsPerCarton, $"LOT-{(n + 1):000}")).ToList()));
            await AdvanceWithRetryAsync(runner, bat, summary);
            summary.ReceiptNumber = receipt?.ReceiptNumber ?? Load().Case.ReceiptNumber;
            summary.Steps.Add($"asn:{asn.AsnNumber} {cartonCount}-cartons receipt={summary.ReceiptNumber}");

            // The library path must photo this shipment's own carton count.
            var views = await runner.BuildDockImagesAsync(bat);
            Assert.NotNull(views);
            summary.PhotoCount = views.Images.Count;
            foreach (var image in views.Images)
            {
                summary.DockImages.Add($"{image.ImageId} {image.Path} attempts={image.LabelAttempts}");
            }

            summary.Steps.Add($"dock-views:{views.Images.Count}-photos {views.PoNumber}");
            var photos = new List<DockPhoto>();
            foreach (var image in views.Images)
            {
                photos.Add(new DockPhoto(
                    image.ImageId, await File.ReadAllBytesAsync(image.FilePath), null));
            }

            var reads = await runner.TranscribeDockPhotosAsync(bat, photos);
            summary.ReadCount = reads.Count;
            var verdicts = await runner.OnDockReadsAsync(bat, reads);
            var held = verdicts
                .Where(kv => kv.Value.Any(v => v.Kind == VerdictKind.Hold))
                .Select(kv => kv.Key)
                .Order(StringComparer.Ordinal)
                .ToList();
            summary.HeldCartons.AddRange(held);
            foreach (var review in Load().Decisions
                .Where(d => d.Status == DecisionStatus.Pending
                    && d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview)
                .ToList())
            {
                summary.ReviewedCartons.Add(review.DocumentNumber ?? review.DecisionId);
                await runner.ResolveDecisionAsync(bat, review.DecisionId, true, review.RequestedTo, "confirmed");
            }

            summary.Steps.Add($"dock:reads={reads.Count} held={held.Count}");
            await AdvanceWithRetryAsync(runner, bat, summary);

            var invoiceNo = $"INV-{po.PoNumber}";
            summary.InvoiceNumber = invoiceNo;
            var intake = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
                bat, invoiceNo, po.VendorId, po.PoNumber,
                [new SupplierInvoiceLineInput(poLine.PoLineRef, poLine.QtyOrdered, poLine.UnitPrice)]));
            Assert.True(intake.Persisted);
            summary.InvoicedTotal = intake.AcceptedValue + intake.HeldValue;
            summary.HeldValue = intake.HeldValue;
            summary.Steps.Add($"invoice:{invoiceNo} accepted={intake.AcceptedValue} held={intake.HeldValue}");

            var invDecision = Pending(Load(), DecisionKind.InvoiceApproval);
            await runner.ResolveDecisionAsync(bat, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");
            summary.Steps.Add("inv-approved");

            await runner.AdvanceAsync(bat);
            var pay1 = Pending(Load(), DecisionKind.PaymentApproval);
            await runner.ResolveDecisionAsync(bat, pay1.DecisionId, true, pay1.RequestedTo, "ok");
            summary.Steps.Add($"pay1:{pay1.Amount}");

            if (held.Count > 0)
            {
                var reshotReads = new List<DockCartonRead>();
                foreach (var carton in held)
                {
                    var asnCarton = asn.Cartons.Single(c =>
                        string.Equals(c.CartonId, carton, StringComparison.Ordinal));
                    reshotReads.Add(new DockCartonRead(
                        carton, BatteryItem, po.PoNumber, asnCarton.LotRef,
                        battery.UnitsPerCarton, true, true, "UN3480", true, true,
                        false, null, $"RESHOT-{carton}", 0.99m));
                }

                var reshot = await runner.OnReshotAsync(new ReshotInput(bat, held, reshotReads));
                Assert.All(reshot.Values.SelectMany(v => v), v => Assert.Equal(VerdictKind.Pass, v.Kind));
                summary.Steps.Add($"reshot:{held.Count}-cartons");

                await runner.AdvanceAsync(bat);
                var pay2 = Pending(Load(), DecisionKind.PaymentApproval);
                Assert.NotEqual(pay1.DecisionId, pay2.DecisionId);
                await runner.ResolveDecisionAsync(bat, pay2.DecisionId, true, pay2.RequestedTo, "ok");
                summary.Steps.Add($"pay2:{pay2.Amount}");
            }

            var done = await runner.AdvanceAsync(bat);
            Assert.Equal(CaseNextStep.Done, done.Step);
            Assert.True(done.Complete);
            var final = Load();
            summary.BatteryStage = final.Case.Stage.ToString();
            Assert.Equal(CaseStage.Resolved, final.Case.Stage);
            summary.PaymentNumbers.AddRange(final.Case.PaymentNumbers);
            var settlements = await erp.GetSettlementsAsync(po.VendorId, invoiceNo);
            summary.SettledTotal = settlements.Sum(s => s.SettledAmount);
            Assert.Equal(poLine.LineAmount, summary.SettledTotal);
            Assert.Equal(InvoiceStatus.Paid, (await erp.GetInvoiceAsync(po.VendorId, invoiceNo))!.Status);
            summary.CustomerNotified = final.Events.Any(e => e.Kind == CaseEventKind.CustomerNotified);
            Assert.True(summary.CustomerNotified, "Resolving the case must notify the customer.");
            summary.Steps.Add("resolved");

            FinishMetering(summary, counter, spend, meterBefore, dollarsBefore);
            summary.Completed = true;
        }
        catch (Exception ex)
        {
            summary.Error = ex.Message;
            RecordMetering(summary, counter, spend, meterBefore, dollarsBefore);
            throw;
        }
        finally
        {
            WriteSummary(summary);
        }
    }

    private static async Task<CaseAdvanceOutcome> AdvanceWithRetryAsync(
        CaseRunner runner, string caseId, CycleSummary summary)
    {
        try
        {
            return await runner.AdvanceAsync(caseId);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("Model call for tool turn", StringComparison.Ordinal))
        {
            summary.Steps.Add($"retry-advance:{caseId} after a model hiccup");
            return await runner.AdvanceAsync(caseId);
        }
    }

    private static void RecordMetering(
        CycleSummary summary, CountingHandler counter, ModelSpendTracker spend, int meterBefore, decimal? dollarsBefore)
    {
        summary.HttpCalls = counter.Count;
        summary.MeteredCalls = spend.LiveCallCount - meterBefore;
        summary.SpendDeltaUsd = (spend.SpendUsd, dollarsBefore) switch
        {
            ({ } after, { } before) => after - before,
            ({ } after, null) => after,
            _ => null,
        };
    }

    private static void FinishMetering(
        CycleSummary summary, CountingHandler counter, ModelSpendTracker spend, int meterBefore, decimal? dollarsBefore)
    {
        RecordMetering(summary, counter, spend, meterBefore, dollarsBefore);
        Assert.True(counter.Count > 0, "Expected live Spark calls on a fresh cache.");
        Assert.True(summary.MeteredCalls > 0, "Expected the spend meter to record live calls.");
        Assert.True(summary.MeteredCalls <= counter.Count,
            $"Metered {summary.MeteredCalls} calls but only {counter.Count} HTTP sends were observed.");
    }
}

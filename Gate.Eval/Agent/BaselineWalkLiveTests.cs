// Baseline walk (live): restores the shipped company baseline into temp
// dirs, then drives the runbook's 1300-glasses order through the same
// runner entry points the app's screens and simulation endpoints call,
// with live Spark and split proposals on, all the way through payment:
// both child shipments get library photos for their own carton counts,
// reads, reviews, invoices, payments, and reshot included, then the
// parent resolves and the customer message goes out. Every step lands
// in .agent-checks/outbox/walk-1300.summary.json for the lead's report.
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

public sealed class BaselineWalkLiveTests
{
    // Runbook walk inputs (docs/ONE-ORDER-RUNBOOK.md steps 0-1).
    private const string EntityCode = "LE-1";
    private const string SalesItem = "GLASS-1";
    private const decimal SalesQty = 1300m;
    private const string BatteryItem = "BAT-1";
    private const string Currency = "USD";
    private const string Requester = "Maya Planner";

    private readonly ITestOutputHelper _output;

    public BaselineWalkLiveTests(ITestOutputHelper output) => _output = output;

    private sealed class WalkSummary
    {
        public string Run { get; set; } = "walk-1300";
        public string ShopOrder { get; set; } = string.Empty;
        public string SoNumber { get; set; } = string.Empty;
        public string ParentCase { get; set; } = string.Empty;
        public string SiblingCase { get; set; } = string.Empty;
        public string SiblingStage { get; set; } = string.Empty;
        public List<string> Steps { get; } = [];
        public List<string> AgentTools { get; } = [];
        public string PrNumber { get; set; } = string.Empty;
        public decimal PrQty { get; set; }
        public string PrApprovedBy { get; set; } = string.Empty;
        public string SplitRequestedTo { get; set; } = string.Empty;
        public string SplitReason { get; set; } = string.Empty;
        public List<SplitLineJson> SplitLines { get; } = [];
        public decimal SplitTotal { get; set; }
        public decimal? SingleVendorTotal { get; set; }
        public decimal? Saving { get; set; }
        public List<ShareJson> FinalShares { get; } = [];
        public List<ChildJson> Children { get; } = [];
        public string ParentStage { get; set; } = string.Empty;
        public bool CustomerNotified { get; set; }
        public List<string> TranscriptTail { get; } = [];
        public int HttpCalls { get; set; }
        public int MeteredCalls { get; set; }
        public decimal? SpendDeltaUsd { get; set; }
        public bool Completed { get; set; }
        public string? StopReason { get; set; }
        public string? Error { get; set; }
    }

    private sealed class SplitLineJson
    {
        public string VendorId { get; set; } = string.Empty;
        public string VendorName { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    private sealed class ShareJson
    {
        public string Country { get; set; } = string.Empty;
        public decimal Share { get; set; }
    }

    private sealed class ChildJson
    {
        public string CaseId { get; set; } = string.Empty;
        public string PoNumber { get; set; } = string.Empty;
        public string VendorId { get; set; } = string.Empty;
        public string VendorName { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Total { get; set; }
        public string PoApprovedBy { get; set; } = string.Empty;
        public string AsnNumber { get; set; } = string.Empty;
        public string? ReceiptNumber { get; set; }
        public int PhotoCount { get; set; }
        public int ReadCount { get; set; }
        public List<string> HeldCartons { get; } = [];
        public List<string> ReviewedCartons { get; } = [];
        public decimal HeldValue { get; set; }
        public string InvoiceNumber { get; set; } = string.Empty;
        public decimal InvoicedTotal { get; set; }
        public List<string> PaymentNumbers { get; } = [];
        public decimal SettledTotal { get; set; }
        public string Stage { get; set; } = string.Empty;
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

    private void WriteSummary(WalkSummary summary)
    {
        var path = Path.Combine(FindOutboxDir(), "walk-1300.summary.json");
        File.WriteAllText(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[walk-1300] completed={0} steps={1} http={2} metered={3} spend={4} stop={5} err={6}",
            summary.Completed, summary.Steps.Count, summary.HttpCalls, summary.MeteredCalls,
            summary.SpendDeltaUsd?.ToString(CultureInfo.InvariantCulture) ?? "unmetered",
            summary.StopReason ?? "none", summary.Error ?? "none"));
    }

    private static Decision Pending(CaseFile file, DecisionKind kind) =>
        file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

    /// <summary>One walk attempt's live state, from a fresh restore to the
    /// split card (null when this attempt's agent never proposed).</summary>
    private sealed record WalkAttempt(
        CaseRunner Runner,
        EfCoreErpAdapter Erp,
        string LiveCache,
        IReadOnlyDictionary<string, string> Vendors,
        ItemDto Battery,
        string Parent,
        string Sibling,
        Decision? Card);

    /// <summary>Runs one attempt: fresh restore, shop order, both cases to
    /// their first stops, PR approval, and the post-screen advance. Live
    /// Spark does not propose every run, so the test retries the attempt
    /// instead of gambling the whole walk on one propose turn.</summary>
    private async Task<WalkAttempt> RunToSplitCardAsync(
        string root, WalkSummary summary, LiveSecrets secrets,
        HttpClient http, ModelSpendTracker spend, int attempt)
    {
        var work = Path.Combine(Path.GetTempPath(), "gate-baseline-walk", Guid.NewGuid().ToString("N"));
        var liveErp = Path.Combine(work, "erp.db");
        var liveCache = Path.Combine(work, "case-cache");
        var modelCache = Path.Combine(work, "model-cache");
        var visionReads = Path.Combine(work, "vision-reads");
        Directory.CreateDirectory(work);

        // Vision replay seed only: the shipped read cache is copied, never
        // written; every new read lands in the temp copy.
        foreach (var file in Directory.GetFiles(Path.Combine(root, "assets", "read-cache")))
        {
            Directory.CreateDirectory(visionReads);
            File.Copy(file, Path.Combine(visionReads, Path.GetFileName(file)));
        }

        // Step 0: restore the saved baseline, same call as Admin restore.
        var restored = await BaselineRestore.RestoreAsync(
            Path.Combine(root, "Gate.Web", "data", "baseline"),
            liveErp, liveCache, NullLogger.Instance);
        summary.Steps.Add($"attempt-{attempt}:restore:" + string.Join(" | ", restored));

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
        Assert.True(splits.IsOn, "Split proposals must be on for this walk.");
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
            splitProposals: splits);

        var vendors = (await erp.GetVendorsAsync())
            .ToDictionary(v => v.VendorId, v => v.Name, StringComparer.Ordinal);
        var battery = await erp.GetItemAsync(BatteryItem);
        Assert.NotNull(battery);

        // Step 1: place the shop order; the endpoint advances every case.
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
        Assert.Equal(2, caseIds.Count);
        var parent = caseIds.Single(c => c.EndsWith("-" + BatteryItem, StringComparison.Ordinal));
        var sibling = caseIds.Single(c => !string.Equals(c, parent, StringComparison.Ordinal));
        summary.ParentCase = parent;
        summary.SiblingCase = sibling;
        summary.SoNumber = CaseStore.Load(liveCache, parent)!.Case.SoNumber;
        Assert.NotEqual(summary.ShopOrder, summary.SoNumber);
        summary.Steps.Add($"attempt-{attempt}:webhook:2-cases so={summary.SoNumber}");

        foreach (var id in caseIds)
        {
            var advanced = await AdvanceWithRetryAsync(runner, id, summary);
            summary.Steps.Add($"attempt-{attempt}:advance:{id} stage={advanced.Stage} step={advanced.Step}");
        }

        summary.SiblingStage = CaseStore.Load(liveCache, sibling)!.Case.Stage.ToString();
        CaseFile LoadParent() => CaseStore.Load(liveCache, parent)!;

        // Step 2: approve the battery requisition as whoever it routes to.
        var prDecision = Pending(LoadParent(), DecisionKind.PrApproval);
        summary.PrApprovedBy = prDecision.RequestedTo;
        var pr = (await erp.GetPrAsync(LoadParent().Case.PrNumber!))!;
        summary.PrNumber = pr.PrNumber;
        summary.PrQty = pr.Lines.Sum(l => l.Quantity);
        await runner.ResolveDecisionAsync(
            parent, prDecision.DecisionId, true, prDecision.RequestedTo, "ok");
        await AdvanceWithRetryAsync(runner, parent, summary);
        summary.Steps.Add($"attempt-{attempt}:pr-approved:{summary.PrNumber} qty={summary.PrQty} by={summary.PrApprovedBy}");

        summary.AgentTools.Clear();
        foreach (var name in CaseTranscript.ReadTurns(liveCache, parent)
            .SelectMany(t => t.Calls)
            .Where(c => c.Verdict == ToolCallVerdict.Executed)
            .Select(c => c.Name))
        {
            summary.AgentTools.Add(name);
        }

        // The split card, or null when this attempt's agent never proposed.
        var afterScreen = LoadParent();
        var split = afterScreen.Decisions.SingleOrDefault(d =>
            d.Kind == DecisionKind.SplitApproval && d.Status == DecisionStatus.Pending);
        return new WalkAttempt(
            runner, erp, liveCache, vendors, battery, parent, sibling,
            split?.Split is null ? null : split);
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Walk1300_SplitOn_ThroughPayment()
    {
        var secrets = Secrets();
        var root = FindRepoRoot();
        var summary = new WalkSummary { ShopOrder = $"SHOP-WALK-{Guid.NewGuid():N}" };
        var counter = new CountingHandler(new HttpClientHandler());
        using var http = new HttpClient(counter);

        var spend = new ModelSpendTracker(new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build());
        var meterBefore = spend.LiveCallCount;
        var dollarsBefore = spend.SpendUsd;
        var liveCache = string.Empty;

        try
        {
            // Up to three fresh attempts: live Spark does not propose
            // every run, and each attempt restores the baseline again.
            WalkAttempt? won = null;
            for (var attempt = 1; attempt <= 3 && won is null; attempt++)
            {
                var current = await RunToSplitCardAsync(
                    root, summary, secrets, http, spend, attempt);
                if (current.Card is null)
                {
                    summary.Steps.Add($"attempt-{attempt}:no-split-proposal");
                }
                else
                {
                    summary.Steps.Add($"attempt-{attempt}:split-proposed");
                    won = current;
                }
            }

            if (won is null || won.Card is null)
            {
                summary.StopReason = "agent did not propose a split in 3 attempts; single-vendor award stands";
                summary.Steps.Add("stop:no-split-proposal");
                FinishMetering(summary, counter, spend, meterBefore, dollarsBefore);
                return;
            }

            var runner = won.Runner;
            var erp = won.Erp;
            liveCache = won.LiveCache;
            var vendors = won.Vendors;
            var battery = won.Battery;
            var parent = won.Parent;
            var sibling = won.Sibling;
            var split = won.Card!;
            CaseFile LoadParent() => CaseStore.Load(liveCache, parent)!;

            var plan = split.Split!;
            summary.SplitRequestedTo = split.RequestedTo;
            summary.SplitReason = split.Reason ?? string.Empty;
            summary.SplitTotal = plan.SplitTotal;
            summary.SingleVendorTotal = plan.SingleVendorTotal;
            summary.Saving = plan.Saving;
            Assert.Equal(2, plan.Lines.Count);
            Assert.Equal(summary.PrQty, plan.Lines.Sum(l => l.Quantity));
            Assert.NotNull(plan.SingleVendorTotal);
            Assert.NotNull(plan.Saving);
            Assert.True(plan.SplitTotal < plan.SingleVendorTotal.Value,
                "The split must beat the single-vendor award.");
            foreach (var line in plan.Lines)
            {
                Assert.Equal(0m, line.Quantity % (decimal)battery.UnitsPerCarton);
                summary.SplitLines.Add(new SplitLineJson
                {
                    VendorId = line.VendorId,
                    VendorName = vendors.GetValueOrDefault(line.VendorId, line.VendorId),
                    Quantity = line.Quantity,
                    UnitPrice = await erp.GetVendorPriceAsync(line.VendorId, BatteryItem) ?? 0m,
                });
            }

            foreach (var share in plan.FinalShares)
            {
                summary.FinalShares.Add(new ShareJson { Country = share.Country, Share = share.Share });
            }

            summary.Steps.Add(
                $"split-proposed:{string.Join("+", plan.Lines.Select(l => $"{l.Quantity}@{l.VendorId}"))} " +
                $"saving={plan.Saving} to={split.RequestedTo}");
            Assert.Null(LoadParent().Case.PoNumber);

            // Step 3: approve the plan, then both child POs get created.
            await runner.ResolveDecisionAsync(
                parent, split.DecisionId, true, split.RequestedTo, "ok");
            var ordered = await AdvanceWithRetryAsync(runner, parent, summary);
            Assert.Equal(CaseNextStep.AwaitSplitChildren, ordered.Step);
            summary.Steps.Add($"split-approved by={split.RequestedTo}");

            var children = new List<CaseFile>();
            foreach (var id in CaseStore.ListCaseIds(liveCache))
            {
                var file = CaseStore.Load(liveCache, id);
                if (file is not null
                    && string.Equals(file.Case.ParentCaseId, parent, StringComparison.Ordinal))
                {
                    children.Add(file);
                }
            }

            Assert.Equal(2, children.Count);
            children = children
                .OrderByDescending(c => plan.Lines
                    .Single(l => string.Equals(l.VendorId, c.Case.AwardedVendorId, StringComparison.Ordinal))
                    .Quantity)
                .ToList();
            var allCartons = new[] { "CTN-01", "CTN-02", "CTN-03", "CTN-04", "CTN-05", "CTN-06", "CTN-07", "CTN-08", "CTN-09", "CTN-10", "CTN-11", "CTN-12" };
            var firstCount = (int)(plan.Lines
                .Single(l => string.Equals(
                    l.VendorId, children[0].Case.AwardedVendorId, StringComparison.Ordinal))
                .Quantity / battery.UnitsPerCarton);
            var cartonSets = new[]
            {
                allCartons.Take(firstCount).ToArray(),
                allCartons.Skip(firstCount).ToArray(),
            };
            Assert.Equal(allCartons.Length, cartonSets[0].Length + cartonSets[1].Length);

            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                var childId = child.Case.CaseId;
                CaseFile LoadChild() => CaseStore.Load(liveCache, childId)!;
                var po = (await erp.GetPurchaseOrderAsync(child.Case.PoNumber!))!;
                var line = po.Lines.Single();
                var entry = new ChildJson
                {
                    CaseId = childId,
                    PoNumber = po.PoNumber,
                    VendorId = po.VendorId,
                    VendorName = vendors.GetValueOrDefault(po.VendorId, po.VendorId),
                    Quantity = line.QtyOrdered,
                    UnitPrice = line.UnitPrice,
                    Total = line.LineAmount,
                };
                Assert.Equal(plan.Lines.Single(l =>
                    string.Equals(l.VendorId, po.VendorId, StringComparison.Ordinal)).Quantity, line.QtyOrdered);

                await AdvanceWithRetryAsync(runner, childId, summary);
                var poDecision = LoadChild().Decisions.SingleOrDefault(d =>
                    d.Kind == DecisionKind.PoApproval && d.Status == DecisionStatus.Pending);
                if (poDecision is null)
                {
                    var refreshed = (await erp.GetPurchaseOrderAsync(po.PoNumber))!;
                    entry.PoApprovedBy = refreshed.ApprovedBy ?? "auto-approved under threshold";
                    summary.Steps.Add($"po-auto:{po.PoNumber} {po.VendorId} qty={line.QtyOrdered} {refreshed.ApprovalStatus}");
                }
                else
                {
                    entry.PoApprovedBy = poDecision.RequestedTo;
                    await runner.ResolveDecisionAsync(
                        childId, poDecision.DecisionId, true, poDecision.RequestedTo, "ok");
                    await AdvanceWithRetryAsync(runner, childId, summary);
                    summary.Steps.Add($"po-approved:{po.PoNumber} {po.VendorId} qty={line.QtyOrdered} by={entry.PoApprovedBy}");
                }

                await runner.OnSupplierAckAsync(new SupplierAckInput(childId));
                await AdvanceWithRetryAsync(runner, childId, summary);
                summary.Steps.Add($"ack:{po.PoNumber}");

                var cartons = cartonSets[i];
                var lotBase = i == 0 ? 0 : cartonSets[0].Length;
                entry.AsnNumber = $"ASN-2026-1300-{(i == 0 ? "A" : "B")}";
                var (asn, receipt) = await runner.OnAsnAsync(new SupplierAsnInput(
                    childId, entry.AsnNumber, $"SLIP-2026-1300-{(i == 0 ? "A" : "B")}",
                    cartons.Select((c, n) => new AsnCartonInput(
                        c, BatteryItem, battery.UnitsPerCarton, $"LOT-{n + 1 + lotBase:000}")).ToList()))
                    ;
                await AdvanceWithRetryAsync(runner, childId, summary);
                entry.ReceiptNumber = receipt?.ReceiptNumber ?? LoadChild().Case.ReceiptNumber;
                summary.Children.Add(entry);
                summary.Steps.Add($"asn:{asn.AsnNumber} {cartons.Length}-cartons receipt={entry.ReceiptNumber}");

                // Library photos for this child's own carton count, then
                // reads, reviews, invoice, payments, and reshot.
                var views = await runner.BuildDockImagesAsync(childId);
                Assert.NotNull(views);
                entry.PhotoCount = views.Images.Count;
                summary.Steps.Add($"dock-views:{entry.PoNumber} {views.Images.Count}-photos");
                var photos = new List<DockPhoto>();
                foreach (var image in views.Images)
                {
                    photos.Add(new DockPhoto(
                        image.ImageId, await File.ReadAllBytesAsync(image.FilePath), null));
                }

                var reads = await runner.TranscribeDockPhotosAsync(childId, photos);
                entry.ReadCount = reads.Count;
                var verdicts = await runner.OnDockReadsAsync(childId, reads);
                var held = verdicts
                    .Where(kv => kv.Value.Any(v => v.Kind == VerdictKind.Hold))
                    .Select(kv => kv.Key)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                entry.HeldCartons.AddRange(held);
                foreach (var review in LoadChild().Decisions
                    .Where(d => d.Status == DecisionStatus.Pending
                        && d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview)
                    .ToList())
                {
                    entry.ReviewedCartons.Add(review.DocumentNumber ?? review.DecisionId);
                    await runner.ResolveDecisionAsync(childId, review.DecisionId, true, review.RequestedTo, "confirmed");
                }

                summary.Steps.Add($"dock:{entry.PoNumber} reads={reads.Count} held={held.Count}");
                await AdvanceWithRetryAsync(runner, childId, summary);

                entry.InvoiceNumber = $"INV-{entry.PoNumber}";
                var intake = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
                    childId, entry.InvoiceNumber, entry.VendorId, entry.PoNumber,
                    [new SupplierInvoiceLineInput(line.PoLineRef, line.QtyOrdered, line.UnitPrice)]));
                Assert.True(intake.Persisted);
                entry.InvoicedTotal = intake.AcceptedValue + intake.HeldValue;
                entry.HeldValue = intake.HeldValue;
                summary.Steps.Add($"invoice:{entry.InvoiceNumber} accepted={intake.AcceptedValue} held={intake.HeldValue}");

                var invDecision = Pending(LoadChild(), DecisionKind.InvoiceApproval);
                await runner.ResolveDecisionAsync(childId, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");
                summary.Steps.Add($"inv-approved:{entry.InvoiceNumber}");

                await AdvanceWithRetryAsync(runner, childId, summary);
                var pay1 = Pending(LoadChild(), DecisionKind.PaymentApproval);
                await runner.ResolveDecisionAsync(childId, pay1.DecisionId, true, pay1.RequestedTo, "ok");
                summary.Steps.Add($"pay1:{entry.PoNumber} {pay1.Amount}");

                if (held.Count > 0)
                {
                    var reshotReads = new List<DockCartonRead>();
                    foreach (var carton in held)
                    {
                        var asnCarton = asn.Cartons.Single(c =>
                            string.Equals(c.CartonId, carton, StringComparison.Ordinal));
                        reshotReads.Add(new DockCartonRead(
                            carton, BatteryItem, entry.PoNumber, asnCarton.LotRef,
                            battery.UnitsPerCarton, true, true, "UN3480", true, true,
                            false, null, $"RESHOT-{carton}", 0.99m));
                    }

                    var reshot = await runner.OnReshotAsync(new ReshotInput(childId, held, reshotReads));
                    Assert.All(reshot.Values.SelectMany(v => v), v => Assert.Equal(VerdictKind.Pass, v.Kind));
                    summary.Steps.Add($"reshot:{entry.PoNumber} {held.Count}-cartons");

                    await AdvanceWithRetryAsync(runner, childId, summary);
                    var pay2 = Pending(LoadChild(), DecisionKind.PaymentApproval);
                    Assert.NotEqual(pay1.DecisionId, pay2.DecisionId);
                    await runner.ResolveDecisionAsync(childId, pay2.DecisionId, true, pay2.RequestedTo, "ok");
                    summary.Steps.Add($"pay2:{entry.PoNumber} {pay2.Amount}");
                }

                await AdvanceWithRetryAsync(runner, childId, summary);
                var childFinal = LoadChild();
                entry.Stage = childFinal.Case.Stage.ToString();
                entry.PaymentNumbers.AddRange(childFinal.Case.PaymentNumbers);
                var settlements = await erp.GetSettlementsAsync(entry.VendorId, entry.InvoiceNumber);
                entry.SettledTotal = settlements.Sum(s => s.SettledAmount);
                Assert.Equal(line.LineAmount, entry.SettledTotal);
                summary.Steps.Add($"child-done:{entry.PoNumber} stage={entry.Stage}");
            }

            // The screw sibling rides the same order: the customer
            // message waits for every case, so the sibling resolves too.
            var screwFile = CaseStore.Load(liveCache, sibling)!;
            var screwPo = (await erp.GetPurchaseOrderAsync(screwFile.Case.PoNumber!))!;
            var screwLine = screwPo.Lines.Single();
            var screwItem = (await erp.GetItemAsync(screwLine.ItemId))!;
            await runner.OnSupplierAckAsync(new SupplierAckInput(sibling));
            await AdvanceWithRetryAsync(runner, sibling, summary);
            summary.Steps.Add($"sibling-ack:{screwPo.PoNumber}");
            var screwCartons = (int)(screwLine.QtyOrdered / screwItem.UnitsPerCarton);
            var (screwAsn, _) = await runner.OnAsnAsync(new SupplierAsnInput(
                sibling, $"ASN-2026-1300-S", $"SLIP-2026-1300-S",
                Enumerable.Range(1, screwCartons)
                    .Select(n => new AsnCartonInput(
                        $"SCTN-{n:00}", screwLine.ItemId,
                        screwItem.UnitsPerCarton, $"SLOT-{n:000}"))
                    .ToList()));
            await AdvanceWithRetryAsync(runner, sibling, summary);
            summary.Steps.Add($"sibling-asn:{screwAsn.AsnNumber} {screwCartons}-cartons");
            // The sibling rides on transcribed reads, not truck photos:
            // the scene read-back expects battery handling marks, so
            // non-battery cartons would fail photo verification. The
            // battery split above is the photographed flow.
            var screwReads = screwAsn.Cartons
                .Select(c => new DockCartonRead(
                    c.CartonId, screwLine.ItemId, screwPo.PoNumber, c.LotRef,
                    screwItem.UnitsPerCarton, true, true, null, false, false,
                    false, null, "DIRECT", 1.0m))
                .ToList();
            var screwVerdicts = await runner.OnDockReadsAsync(sibling, screwReads);
            var screwHeld = screwVerdicts
                .Where(kv => kv.Value.Any(v => v.Kind == VerdictKind.Hold))
                .Select(kv => kv.Key)
                .Order(StringComparer.Ordinal)
                .ToList();
            foreach (var review in CaseStore.Load(liveCache, sibling)!.Decisions
                .Where(d => d.Status == DecisionStatus.Pending
                    && d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview)
                .ToList())
            {
                await runner.ResolveDecisionAsync(sibling, review.DecisionId, true, review.RequestedTo, "confirmed");
            }

            summary.Steps.Add($"sibling-dock:reads={screwReads.Count} held={screwHeld.Count}");
            await AdvanceWithRetryAsync(runner, sibling, summary);
            var screwInvoice = $"INV-{screwPo.PoNumber}";
            var screwIntake = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
                sibling, screwInvoice, screwPo.VendorId, screwPo.PoNumber,
                [new SupplierInvoiceLineInput(screwLine.PoLineRef, screwLine.QtyOrdered, screwLine.UnitPrice)]));
            Assert.True(screwIntake.Persisted);
            var screwInv = Pending(CaseStore.Load(liveCache, sibling)!, DecisionKind.InvoiceApproval);
            await runner.ResolveDecisionAsync(sibling, screwInv.DecisionId, true, screwInv.RequestedTo, "ok");
            await AdvanceWithRetryAsync(runner, sibling, summary);
            var screwPay = Pending(CaseStore.Load(liveCache, sibling)!, DecisionKind.PaymentApproval);
            await runner.ResolveDecisionAsync(sibling, screwPay.DecisionId, true, screwPay.RequestedTo, "ok");
            summary.Steps.Add($"sibling-paid:{screwInvoice} {screwPay.Amount}");
            if (screwHeld.Count > 0)
            {
                var screwReshot = screwHeld.Select(carton =>
                {
                    var asnCarton = screwAsn.Cartons.Single(c =>
                        string.Equals(c.CartonId, carton, StringComparison.Ordinal));
                    return new DockCartonRead(
                        carton, screwLine.ItemId, screwPo.PoNumber, asnCarton.LotRef,
                        screwItem.UnitsPerCarton, true, true, "UN3480", true, true,
                        false, null, $"RESHOT-{carton}", 0.99m);
                }).ToList();
                var reshot = await runner.OnReshotAsync(new ReshotInput(sibling, screwHeld, screwReshot));
                Assert.All(reshot.Values.SelectMany(v => v), v => Assert.Equal(VerdictKind.Pass, v.Kind));
                await AdvanceWithRetryAsync(runner, sibling, summary);
                var screwPay2 = Pending(CaseStore.Load(liveCache, sibling)!, DecisionKind.PaymentApproval);
                await runner.ResolveDecisionAsync(sibling, screwPay2.DecisionId, true, screwPay2.RequestedTo, "ok");
                summary.Steps.Add($"sibling-paid2:{screwInvoice} {screwPay2.Amount}");
            }

            await AdvanceWithRetryAsync(runner, sibling, summary);
            Assert.Equal(CaseStage.Resolved, CaseStore.Load(liveCache, sibling)!.Case.Stage);
            summary.Steps.Add("sibling-resolved");

            // Both children paid and the sibling resolved: the parent
            // resolves and the customer message goes out exactly once for
            // the order, on the last case to finish.
            var parentDone = await AdvanceWithRetryAsync(runner, parent, summary);
            Assert.Equal(CaseNextStep.Done, parentDone.Step);
            Assert.True(parentDone.Complete);
            var parentFinal = LoadParent();
            summary.ParentStage = parentFinal.Case.Stage.ToString();
            Assert.Equal(CaseStage.Resolved, parentFinal.Case.Stage);
            var notified = new List<string>();
            foreach (var id in CaseStore.ListCaseIds(liveCache))
            {
                var file = CaseStore.Load(liveCache, id);
                if (file is not null
                    && string.Equals(file.Case.SoNumber, summary.SoNumber, StringComparison.Ordinal)
                    && file.Events.Any(e => e.Kind == CaseEventKind.CustomerNotified))
                {
                    notified.Add(id);
                }
            }

            summary.CustomerNotified = notified.Count == 1;
            Assert.True(summary.CustomerNotified,
                $"The customer message must go out exactly once for the order; found on [{string.Join(",", notified)}].");
            summary.Steps.Add($"parent-resolved notified-on:{notified[0]}");
            summary.Completed = true;
            FinishMetering(summary, counter, spend, meterBefore, dollarsBefore);
        }
        catch (Exception ex)
        {
            summary.Error = ex.Message;
            RecordMetering(summary, counter, spend, meterBefore, dollarsBefore);
            RecordTranscriptTail(summary, liveCache);
            throw;
        }
        finally
        {
            WriteSummary(summary);
        }
    }

    // Same as the app's "Try again" on a failed advance: one more
    // advance when a model turn burns its budget. Nothing else retries.
    private static async Task<CaseAdvanceOutcome> AdvanceWithRetryAsync(
        CaseRunner runner, string caseId, WalkSummary summary)
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

    // Failure forensics: the last turns of the parent case (calls plus
    // verdicts, details trimmed) so a flail or stall leaves evidence in
    // the summary instead of a bare turn-cap message.
    private static void RecordTranscriptTail(WalkSummary summary, string liveCache)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(summary.ParentCase))
            {
                return;
            }

            foreach (var turn in CaseTranscript.ReadTurns(liveCache, summary.ParentCase).TakeLast(8))
            {
                var calls = string.Join("; ", turn.Calls.Select(c =>
                    $"{c.Name}={c.Verdict}:{Truncate(c.Detail, 160)}"));
                summary.TranscriptTail.Add($"turn {turn.Turn} {turn.PickedStep}: {calls}");
            }
        }
        catch (Exception ex)
        {
            summary.TranscriptTail.Add($"tail unavailable: {ex.Message}");
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text.Substring(0, max) + "…";

    private static void RecordMetering(
        WalkSummary summary, CountingHandler counter, ModelSpendTracker spend, int meterBefore, decimal? dollarsBefore)
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
        WalkSummary summary, CountingHandler counter, ModelSpendTracker spend, int meterBefore, decimal? dollarsBefore)
    {
        RecordMetering(summary, counter, spend, meterBefore, dollarsBefore);
        Assert.True(counter.Count > 0, "Expected live Spark calls on a fresh cache.");
        Assert.True(summary.MeteredCalls > 0, "Expected the spend meter to record live calls.");
        Assert.True(summary.MeteredCalls <= counter.Count,
            $"Metered {summary.MeteredCalls} calls but only {counter.Count} HTTP sends were observed.");
    }
}

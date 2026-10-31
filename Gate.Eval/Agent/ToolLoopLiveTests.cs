// Live tool-loop proof: demo-v1 and alt-v1 run webhook to Resolved with
// live Spark choosing every step through tool calls (the transcript proves
// it: every executed action names its model-proposed tool, zero
// model-deferred turns), then the identical runs replay from scratch with
// zero live calls. Keys come from user secrets or the environment, never
// from a file in the repo. The battery path runs on photos: dock posts real
// JPEGs from assets/dock, the reshot posts its reshotIn photo, and the
// invoice is rendered at runtime and transcribed before intake. The screw
// path stays EDI/hand-reads as the non-photo variant.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.Extensions.Configuration;
using SkiaSharp;
using System.Globalization;
using System.Text.Json;
using Xunit.Abstractions;

namespace Gate.Eval;

public sealed class ToolLoopLiveTests
{
    private readonly ITestOutputHelper _output;

    public ToolLoopLiveTests(ITestOutputHelper output) => _output = output;

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
            Assert.Contains(AgentTools.AwardSupplier, executed);
            Assert.Contains(AgentTools.CreatePo, executed);
            Assert.Contains(AgentTools.DraftPayment, executed);
            Assert.Contains(AgentTools.FinishCase, executed);
        }
    }

    private async Task RunScenarioAsync(
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
        var images = root.GetProperty("images").EnumerateArray().Select(e => e.GetString()!).ToList();
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

        await RunBatteryAsync(runner, erp, caseDir, bat, cartons, images, mainVendor, mainQty, mainPrice, live);
        await RunScrewAsync(runner, erp, caseDir, screw);
    }

    private async Task RunBatteryAsync(
        CaseRunner runner, InMemoryErpAdapter erp, string caseDir, string bat,
        IReadOnlyList<ScenarioCarton> cartons, IReadOnlyList<string> images,
        string mainVendor, decimal mainQty, decimal mainPrice,
        bool live)
    {
        CaseFile Load() => CaseStore.Load(caseDir, bat)!;
        static Decision Pending(CaseFile file, DecisionKind kind) =>
            file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

        var photoReads = await TranscribeLivePhotosAsync(runner, bat, cartons, images);

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
        await runner.OnDockReadsAsync(bat, photoReads.DockReads);
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
        // The vendor under-bills by 5 units on the main invoice; the missing 5
        // arrive later as a misaddressed invoice, corrected over vendor email.
        var shortQty = 5m;
        var transcribed = await TranscribeLiveInvoiceAsync(
            runner, erp, bat, $"INV-{batPo}", mainVendor, batPo,
            batLine.PoLineRef, mainQty - shortQty, mainPrice);
        var intake = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            bat, transcribed.SupplierInvoiceNumber!, transcribed.VendorIdOrName, transcribed.PoNumber,
            transcribed.Lines, transcribed.BankAccountRef));
        Assert.True(intake.Persisted);
        Assert.Equal(acceptedUnits * mainPrice, intake.AcceptedValue);

        var invDecision = Pending(Load(), DecisionKind.InvoiceApproval);
        await runner.ResolveDecisionAsync(bat, invDecision.DecisionId, true, invDecision.RequestedTo, "ok");

        await runner.AdvanceAsync(bat);
        var pay1 = Pending(Load(), DecisionKind.PaymentApproval);
        Assert.Equal(acceptedUnits * mainPrice, pay1.Amount);
        await runner.ResolveDecisionAsync(bat, pay1.DecisionId, true, pay1.RequestedTo, "ok");

        var reshot = await runner.OnReshotAsync(new ReshotInput(bat, [unmarked.Id], photoReads.ReshotReads));
        Assert.All(reshot[unmarked.Id], v => Assert.Equal(VerdictKind.Pass, v.Kind));

        var tricky = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            bat, "INV-MISS", mainVendor, "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", shortQty, mainPrice)]));
        Assert.False(tricky.Persisted);
        var problem = Load().Decisions.Single(d =>
            d.Kind == DecisionKind.ApReview && d.Status == DecisionStatus.Pending);
        Assert.Equal($"{mainVendor}/INV-MISS", problem.DocumentNumber);
        _output.WriteLine($"[mail-live] AP problem: {problem.DocumentNumber} :: {problem.Reason ?? problem.Title}");

        var laterPays = new List<decimal>();
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
        Assert.Equal($"{mainVendor}/INV-MISS", query.InvoiceRef);
        _output.WriteLine($"[mail-live] query to {query.To}: {query.Subject}");

        var vendorMaster = (await erp.GetVendorsAsync()).Single(v =>
            string.Equals(v.VendorId, mainVendor, StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(vendorMaster.Email));
        var reply = await runner.OnVendorReplyAsync(new VendorReplyInput(
            bat, vendorMaster.Email!, CorporateReplyBody(vendorMaster.Name, mainVendor, batPo, batLine.PoLineRef, mainPrice),
            InReplyTo: query.EmailId, EmailId: $"{bat}-r-001"));
        Assert.Equal(mainVendor, reply.VendorId);
        var fixedInvoice = await erp.GetInvoiceAsync(mainVendor, "INV-FIX");
        Assert.NotNull(fixedInvoice);
        Assert.All(fixedInvoice.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
        Assert.Contains(Load().Events, e =>
            e.Kind == CaseEventKind.Note
            && (e.Summary ?? string.Empty).Contains($"re-intaked as {mainVendor}/INV-FIX", StringComparison.Ordinal));

        var fixApproval = Load().Decisions.Single(d =>
            d.Kind == DecisionKind.InvoiceApproval
            && d.Status == DecisionStatus.Pending
            && string.Equals(d.DocumentNumber, $"{mainVendor}/INV-FIX", StringComparison.Ordinal));
        await runner.ResolveDecisionAsync(bat, fixApproval.DecisionId, true, fixApproval.RequestedTo, "ok");
        var apReview = Load().Decisions.Single(d =>
            d.Kind == DecisionKind.ApReview && d.Status == DecisionStatus.Pending);
        await runner.ResolveDecisionAsync(bat, apReview.DecisionId, true, apReview.RequestedTo, "correction accepted");

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
        Assert.Equal(unmarked.Units * mainPrice, laterPays.Sum());
        var final = Load();
        Assert.Equal(CaseStage.Resolved, final.Case.Stage);
        Assert.InRange(final.Case.PaymentNumbers.Count, 2, 3);
        var mainSettled = await erp.GetSettlementsAsync(mainVendor, $"INV-{batPo}");
        var fixSettled = await erp.GetSettlementsAsync(mainVendor, "INV-FIX");
        Assert.Equal(mainQty * mainPrice,
            mainSettled.Sum(s => s.SettledAmount) + fixSettled.Sum(s => s.SettledAmount));
    }

    private static string CorporateReplyBody(
        string vendorName, string vendorId, string poNumber, string poLineRef, decimal unitPrice) =>
        "Hi,\n" +
        "\n" +
        "Thanks for flagging this — you're right, INV-MISS went out with an old PO reference on our side. " +
        "Sorry for the confusion.\n" +
        "\n" +
        $"Please book those 5 units as invoice INV-FIX under {vendorName} ({vendorId}) against your PO {poNumber}, " +
        $"line {poLineRef}, at {unitPrice.ToString(CultureInfo.InvariantCulture)} per unit. " +
        $"Everything else on the shipment stays as billed on INV-{poNumber}.\n" +
        "\n" +
        "Let me know if you need anything else from us.\n" +
        "\n" +
        "Best regards,\n" +
        "Maria Santos\n" +
        $"Accounts Receivable, {vendorName}";

    private sealed record LivePhotoReads(
        IReadOnlyList<DockCartonRead> DockReads,
        IReadOnlyList<DockCartonRead> ReshotReads);

    private static string FindDockDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "assets", "dock");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find assets/dock by walking up from {AppContext.BaseDirectory}; " +
            "run the live photo flow from the repo checkout.");
    }

    private static bool IsDamaged(ScenarioCarton carton) =>
        carton.Damage != "none" && carton.Damage != "none, hidden";

    /// <summary>Live accuracy step: transcribes every scenario dock image plus
    /// the reshot image, logs per-image carton/mark/damage results against
    /// scenario ground truth, and returns the reads for the flow. The dock
    /// step runs on these transcribed reads only: a misread fails the run
    /// with the exact diffs, never falls back to hand reads. Carton sets,
    /// marks, and Class 9 must match on every view; damage unions across
    /// views (any view showing damage counts, mirroring the BAT-LI-05
    /// any-view rule), since a crushed face may hide from one angle.</summary>
    private async Task<LivePhotoReads> TranscribeLivePhotosAsync(
        CaseRunner runner, string bat, IReadOnlyList<ScenarioCarton> cartons,
        IReadOnlyList<string> images)
    {
        var dockDir = FindDockDir();
        var unmarked = cartons.Single(c => !c.MarkPresent);
        var palletIds = cartons.SelectMany(c => c.VisibleIn).Distinct(StringComparer.Ordinal).ToList();
        var reshotId = unmarked.ReshotIn
            ?? throw new InvalidOperationException("Scenario carton without markPresent has no reshotIn image.");
        var perImage = new Dictionary<string, IReadOnlyList<DockCartonRead>>(StringComparer.Ordinal);
        foreach (var imageId in images)
        {
            var file = Path.Combine(dockDir, imageId + ".jpg");
            if (!File.Exists(file))
            {
                throw new InvalidOperationException(
                    $"Live photo flow needs {file} but it is missing; run from the repo checkout with assets/dock intact.");
            }

            var jpeg = await File.ReadAllBytesAsync(file);
            var reads = await runner.TranscribeDockPhotosAsync(bat, [new DockPhoto(imageId, jpeg, null)]);
            perImage[imageId] = reads;
        }

        var mismatches = new List<string>();
        var damageViews = cartons.ToDictionary(c => c.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var imageId in palletIds)
        {
            var expected = cartons.Where(c => c.VisibleIn.Contains(imageId, StringComparer.Ordinal)).ToList();
            var reads = perImage.TryGetValue(imageId, out var found) ? found : [];
            var readIds = reads.Select(r => r.CartonId).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var expectedIds = expected.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
            _output.WriteLine($"[photo-accuracy] {imageId}: read [{string.Join(",", readIds)}] expected [{string.Join(",", expectedIds)}]");
            foreach (var carton in expected)
            {
                var read = reads.SingleOrDefault(r =>
                    string.Equals(r.CartonId, carton.Id, StringComparison.Ordinal));
                _output.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "[photo-accuracy] {0} {1}: mark read {2} expected {3}; damage read {4} expected {5}; legible {6}; class9 {7}",
                    imageId, carton.Id,
                    read?.MarkPresent.ToString() ?? "(missing)", carton.MarkPresent,
                    read?.DamageVisible.ToString() ?? "(missing)", IsDamaged(carton),
                    read?.MarkLegible.ToString() ?? "(missing)",
                    read?.Class9Visible.ToString() ?? "(missing)"));
                if (read is null)
                {
                    mismatches.Add($"{imageId} {carton.Id}: no read transcribed.");
                }
                else
                {
                    if (read.MarkPresent != carton.MarkPresent)
                    {
                        mismatches.Add($"{imageId} {carton.Id}: mark read {read.MarkPresent}, expected {carton.MarkPresent}.");
                    }

                    if (read.MarkLegible != carton.MarkPresent)
                    {
                        mismatches.Add($"{imageId} {carton.Id}: legible read {read.MarkLegible}, expected {carton.MarkPresent}.");
                    }

                    if (read.DamageVisible)
                    {
                        damageViews[carton.Id].Add(imageId);
                    }

                    if (!read.Class9Visible)
                    {
                        mismatches.Add($"{imageId} {carton.Id}: class9 read False, expected True.");
                    }
                }
            }

            if (!new HashSet<string>(readIds, StringComparer.Ordinal).SetEquals(expectedIds))
            {
                mismatches.Add($"{imageId}: carton set read [{string.Join(",", readIds)}], expected [{string.Join(",", expectedIds)}].");
            }
        }

        foreach (var carton in cartons)
        {
            var seen = damageViews[carton.Id];
            var expectedDamage = IsDamaged(carton);
            _output.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "[photo-accuracy] {0}: damage visible in [{1}] expected {2}",
                carton.Id, string.Join(",", seen), expectedDamage));
            if ((seen.Count > 0) != expectedDamage)
            {
                mismatches.Add($"{carton.Id}: damage visible in [{string.Join(",", seen)}], expected damaged={expectedDamage}.");
            }
        }

        var reshotReads = perImage.TryGetValue(reshotId, out var reshot) ? reshot : [];
        var reshotLine = string.Join(";", reshotReads.Select(r =>
            $"{r.CartonId} mark={r.MarkPresent} legible={r.MarkLegible} damage={r.DamageVisible}"));
        _output.WriteLine($"[photo-accuracy] {reshotId} (reshot of {unmarked.Id}): [{reshotLine}] expected [{unmarked.Id} mark=True damage=False]");
        var reshotRead = Assert.Single(reshotReads);
        Assert.Equal(unmarked.Id, reshotRead.CartonId);
        Assert.True(reshotRead.MarkPresent);
        Assert.True(reshotRead.MarkLegible);
        Assert.False(reshotRead.DamageVisible);

        Assert.True(mismatches.Count == 0,
            "Pallet views misread; the dock check runs on photo transcriptions only:\n"
            + string.Join("\n", mismatches));

        _output.WriteLine("[photo-accuracy] Pallet views read correctly; dock runs on transcribed photo reads.");
        var dockReads = palletIds.SelectMany(id => perImage[id]).ToList();
        return new LivePhotoReads(dockReads, reshotReads);
    }

    /// <summary>Renders the battery invoice at runtime with SkiaSharp (white
    /// background, large black text; every value from live ERP state, never
    /// hardcoded), transcribes it, and asserts field-for-field before intake.</summary>
    private async Task<TranscribedInvoice> TranscribeLiveInvoiceAsync(
        CaseRunner runner, InMemoryErpAdapter erp, string bat,
        string number, string vendorId, string poNumber,
        string poLineRef, decimal qty, decimal price)
    {
        var vendorName = (await erp.GetVendorsAsync()).Single(v =>
            string.Equals(v.VendorId, vendorId, StringComparison.Ordinal)).Name;
        var bank = (await erp.GetVendorBankAsync(vendorId))?.AccountRef;
        Assert.False(string.IsNullOrWhiteSpace(bank));
        var jpeg = RenderInvoiceJpeg(number, vendorId, vendorName, poNumber, poLineRef, qty, price, bank!);
        var transcribed = await runner.TranscribeInvoicePhotosAsync(bat, [new InvoicePhoto("INV-LIVE", jpeg)]);
        _output.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[photo-accuracy] invoice: number '{0}' vendor '{1}' po '{2}' lines {3} bank '{4}'",
            transcribed.SupplierInvoiceNumber, transcribed.VendorIdOrName,
            transcribed.PoNumber, transcribed.Lines.Count, transcribed.BankAccountRef));
        Assert.Equal(number, transcribed.SupplierInvoiceNumber);
        Assert.Equal(vendorId, transcribed.VendorIdOrName);
        Assert.Equal(poNumber, transcribed.PoNumber);
        var line = Assert.Single(transcribed.Lines);
        Assert.Equal(poLineRef, line.PoLineRef);
        Assert.True(qty == line.Quantity);
        Assert.True(price == line.UnitPrice);
        Assert.Equal(bank, transcribed.BankAccountRef);
        return transcribed;
    }

    private static byte[] RenderInvoiceJpeg(
        string number, string vendorId, string vendorName, string poNumber,
        string poLineRef, decimal qty, decimal price, string bankRef)
    {
        const int W = 2000;
        const int H = 1500;
        using var bitmap = new SKBitmap(W, H);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var title = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = true, Typeface = SKTypeface.Default,
            TextSize = 72, FakeBoldText = true
        };
        using var text = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = true, Typeface = SKTypeface.Default, TextSize = 60
        };
        float y = 150;
        canvas.DrawText(vendorName, 80, y, title);
        y += 130;
        canvas.DrawText("SUPPLIER INVOICE", 80, y, title);
        y += 130;
        canvas.DrawText($"supplier_invoice_number: {number}", 80, y, text);
        y += 100;
        canvas.DrawText($"vendor_id_or_name: {vendorId}", 80, y, text);
        y += 100;
        canvas.DrawText($"po_number: {poNumber}", 80, y, text);
        y += 110;
        canvas.DrawText("lines:", 80, y, text);
        y += 100;
        canvas.DrawText($"po_line_ref: {poLineRef}", 80, y, text);
        y += 100;
        canvas.DrawText(
            $"quantity: {qty.ToString(CultureInfo.InvariantCulture)} unit_price: {price.ToString(CultureInfo.InvariantCulture)}",
            80, y, text);
        y += 100;
        canvas.DrawText($"bank_account_ref: {bankRef}", 80, y, text);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
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

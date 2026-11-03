// Dock-problem threading tests (offline): ASN problems persist per case,
// rebuild overrides win per carton and land on the timeline, the live
// switch picks the live scene with a recorded library fallback, and photo
// uploads structurally take no problems. Test-double data only.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using SkiaSharp;

namespace Gate.Eval;

public sealed class DockProblemsTests
{
    private sealed record Harness(
        InMemoryErpAdapter Erp,
        CaseRunner Runner,
        RecordingModelClient Models,
        RecordingComplianceEngine Compliance,
        string CaseDir);

    private sealed class StubSceneGenerator(Func<int, byte[]> generate) : ISceneGenerator
    {
        public string GeneratorName => "stub-scenes";

        public int CallCount { get; private set; }

        public Task<byte[]> GenerateSceneAsync(int cartonCount, CancellationToken ct = default)
        {
            _ = ct;
            CallCount++;
            return Task.FromResult(generate(cartonCount));
        }
    }

    private static string TempDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    private static Harness Create(DockImageOptions? options = null, DockSceneLive? scene = null)
    {
        var erp = SeededErp.Create();
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir("gate-dock-problems-test");
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir,
            dockImageOptions: options, dockSceneLive: scene);
        return new Harness(erp, runner, models, compliance, dir);
    }

    private static DockImageOptions BadSpecsOptions(string dir) =>
        new(
            BasesDir: Path.Combine(dir, "bases"),
            ImageSpecsPath: Path.Combine(dir, "missing-specs.json"),
            CartonsPath: Path.Combine(dir, "missing-cartons.json"),
            OutputDir: Path.Combine(dir, "out"),
            ImageCacheDir: Path.Combine(dir, "imgcache"),
            ReadCacheDir: Path.Combine(dir, "readcache"));

    private static DockImageOptions LibraryOptions(string dir) =>
        new(
            BasesDir: VisionFiles.DockScenesBasesDir(),
            ImageSpecsPath: VisionFiles.DockScenesPath(),
            CartonsPath: Path.Combine(dir, "unused-truth.json"),
            OutputDir: Path.Combine(dir, "dockout"),
            ImageCacheDir: Path.Combine(dir, "dockimg"),
            ReadCacheDir: Path.Combine(dir, "dockreads"));

    private static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";

    private static string AwardArgs(string caseId, string vendorId) =>
        $$"""{"case_id":"{{caseId}}","vendor_id":"{{vendorId}}"}""";

    /// <summary>Drives one battery case to an ASN with the given staged
    /// problems persisted: webhook, shortfall, PR, screen, award, PO, ACK,
    /// ASN plus receipt. Cartons default to the single test carton.</summary>
    private static async Task<string> OpenAsnCaseAsync(
        Harness h, IReadOnlyList<SimulatedProblem>? problems = null,
        IReadOnlyList<AsnCartonInput>? cartons = null)
    {
        var input = new OrderWebhookInput(
            "test",
            $"SHOP-{Guid.NewGuid():N}",
            "planner",
            new Dictionary<string, IReadOnlyList<string>> { [SeededErp.Battery] = ["V-B"] },
            $"idem-{Guid.NewGuid():N}",
            [new SalesOrderLine(SeededErp.Battery, 15)],
            "USD");
        var caseId = Assert.Single(await h.Runner.OnOrderWebhookAsync(input));
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        h.Models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        h.Models.QueueToolCalls("Award to V-B; sole candidate.", ("c4", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        h.Models.QueueToolCalls("order", ("c5", AgentTools.CreatePo, Args(caseId)));
        var outcome = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitSupplierAck, outcome.Step);

        await h.Runner.OnSupplierAckAsync(new SupplierAckInput(caseId));
        var (asn, receipt) = await h.Runner.OnAsnAsync(new SupplierAsnInput(
            caseId,
            $"ASN-{Guid.NewGuid():N}",
            "PACK-T1",
            cartons ?? [new AsnCartonInput("C-1", SeededErp.Battery, 5, "LOT-T1")],
            SimulatedProblems: problems));
        Assert.NotNull(receipt);
        _ = asn;
        return caseId;
    }

    [Fact]
    public void ProblemsSidecarRoundTrips()
    {
        var dir = TempDir("gate-dock-problems-test");
        Assert.Null(CaseStore.LoadProblems(dir, "case-x"));

        var stamp = DateTimeOffset.UtcNow;
        CaseStore.SaveProblems(dir, new DockProblems(
            "case-x",
            [new SimulatedProblem("C-1", SimulatedProblemKind.MissingMark)],
            stamp));

        var loaded = CaseStore.LoadProblems(dir, "case-x")!;
        Assert.Equal("case-x", loaded.CaseId);
        var problem = Assert.Single(loaded.Problems);
        Assert.Equal("C-1", problem.CartonId);
        Assert.Equal(SimulatedProblemKind.MissingMark, problem.Kind);
    }

    [Fact]
    public void ProblemsSidecarExcludedFromCaseList()
    {
        var dir = TempDir("gate-dock-problems-test");
        var stamp = DateTimeOffset.UtcNow;
        CaseStore.SaveProblems(dir, new DockProblems(
            "case-x", [new SimulatedProblem("C-1", SimulatedProblemKind.Crushed)], stamp));
        CaseStore.Save(dir, new CaseFile(
            new Case("case-y", "SO-1", SeededErp.Battery, "test", CaseStage.Created, stamp, stamp,
                null, null, null, null, [], [], null, null),
            [], []));

        Assert.Equal("case-y", Assert.Single(CaseStore.ListCaseIds(dir)));
    }

    [Fact]
    public async Task OnAsnPersistsProblems()
    {
        var h = Create();
        var caseId = await OpenAsnCaseAsync(h, [new SimulatedProblem("C-1", SimulatedProblemKind.MissingMark)]);

        var stored = CaseStore.LoadProblems(h.CaseDir, caseId)!;
        var problem = Assert.Single(stored.Problems);
        Assert.Equal("C-1", problem.CartonId);
        Assert.Equal(SimulatedProblemKind.MissingMark, problem.Kind);
    }

    [Fact]
    public async Task BuildDockImagesOverrideWinsPerCartonAndRecords()
    {
        var first = Create();
        var caseId = await OpenAsnCaseAsync(first, [new SimulatedProblem("C-1", SimulatedProblemKind.MissingMark)]);
        var h = new Harness(
            first.Erp,
            new CaseRunner(
                first.Erp,
                new P2pAgent(first.Erp, first.Models, first.Compliance),
                first.Models, first.Compliance, new DockEvaluatorAdapter(), first.CaseDir,
                dockImageOptions: BadSpecsOptions(first.CaseDir)),
            first.Models, first.Compliance, first.CaseDir);

        var set = await h.Runner.BuildDockImagesAsync(
            caseId, default, [new SimulatedProblem("C-1", SimulatedProblemKind.Crushed),
                new SimulatedProblem("C-2", SimulatedProblemKind.Hidden)]);

        Assert.Null(set);
        var timeline = CaseStore.Load(h.CaseDir, caseId)!.Events
            .Where(e => e.Kind == CaseEventKind.Note)
            .Select(e => e.Summary)
            .ToList();
        var problems = Assert.Single(timeline, s => s.Contains("simulated problem(s)", StringComparison.Ordinal));
        Assert.Contains("2 simulated problem(s)", problems, StringComparison.Ordinal);
        Assert.Contains("C-1 (Crushed)", problems, StringComparison.Ordinal);
        Assert.Contains("C-2 (Hidden)", problems, StringComparison.Ordinal);
        Assert.DoesNotContain("MissingMark", problems, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LibraryReadMismatchWarnsOnTimelineAndKeepsViews()
    {
        // Code-drawn labels are certain: a persistent reader miss records
        // a timeline warning and the views still build.
        var first = Create();
        var caseId = await OpenAsnCaseAsync(first);
        var h = new Harness(
            first.Erp,
            new CaseRunner(
                first.Erp,
                new P2pAgent(first.Erp, first.Models, first.Compliance),
                first.Models, first.Compliance, new DockEvaluatorAdapter(), first.CaseDir,
                dockImageOptions: LibraryOptions(first.CaseDir)),
            first.Models, first.Compliance, first.CaseDir);
        var wrong = $$"""[{"carton_id":"C-1","part":"{{SeededErp.Battery}}","po_number":"PO-WRONG","lot_code":"LOT-T1","units_per_carton":5,"mark_present":true,"mark_legible":true,"un_number":"UN3480","class9_visible":true,"damage_visible":false,"damage_note":null,"read_confidence":0.99}]""";
        h.Models.QueueText(wrong);
        h.Models.QueueText(wrong);

        var set = await h.Runner.BuildDockImagesAsync(caseId);

        Assert.NotNull(set);
        var image = Assert.Single(set.Images);
        Assert.NotNull(image.ReadWarning);
        Assert.Contains("C-1", image.ReadWarning, StringComparison.Ordinal);
        var timeline = CaseStore.Load(h.CaseDir, caseId)!.Events
            .Where(e => e.Kind == CaseEventKind.Note)
            .Select(e => e.Summary)
            .ToList();
        var view = Assert.Single(timeline, s => s.Contains("Dock view", StringComparison.Ordinal));
        Assert.Contains("Read warning:", view, StringComparison.Ordinal);
        Assert.Contains("C-1", view, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveFallbackRecordsReason()
    {
        var first = Create();
        var caseId = await OpenAsnCaseAsync(first);
        var generator = new StubSceneGenerator(_ =>
            throw new InvalidOperationException("live painter offline"));
        var scene = new DockSceneLive { LiveOn = true, SceneGenerator = generator };
        var h = new Harness(
            first.Erp,
            new CaseRunner(
                first.Erp,
                new P2pAgent(first.Erp, first.Models, first.Compliance),
                first.Models, first.Compliance, new DockEvaluatorAdapter(), first.CaseDir,
                dockImageOptions: BadSpecsOptions(first.CaseDir), dockSceneLive: scene),
            first.Models, first.Compliance, first.CaseDir);

        var set = await h.Runner.BuildDockImagesAsync(caseId);

        Assert.Null(set);
        Assert.Equal(1, generator.CallCount);
        var timeline = CaseStore.Load(h.CaseDir, caseId)!.Events
            .Where(e => e.Kind == CaseEventKind.Note)
            .Select(e => e.Summary)
            .ToList();
        var fallback = Assert.Single(timeline, s => s.Contains("library views used", StringComparison.Ordinal));
        Assert.Contains("live painter offline", fallback, StringComparison.Ordinal);
        Assert.Contains("live painter offline", scene.LastFallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveHitReturnsLiveSet()
    {
        var first = Create();
        var caseId = await OpenAsnCaseAsync(first);
        var poNumber = CaseStore.Load(first.CaseDir, caseId)!.Case.PoNumber!;
        var baseBytes = File.ReadAllBytes(Path.Combine(VisionFiles.DockScenesBasesDir(), "scene-01.jpg"));
        var generator = new StubSceneGenerator(_ => baseBytes);
        var scene = new DockSceneLive { LiveOn = true, SceneGenerator = generator };
        var h = new Harness(
            first.Erp,
            new CaseRunner(
                first.Erp,
                new P2pAgent(first.Erp, first.Models, first.Compliance),
                first.Models, first.Compliance, new DockEvaluatorAdapter(), first.CaseDir,
                dockImageOptions: BadSpecsOptions(first.CaseDir), dockSceneLive: scene),
            first.Models, first.Compliance, first.CaseDir);
        h.Models.QueueText("""[{"x":0.1,"y":0.1,"w":0.8,"h":0.8}]""");
        h.Models.QueueText(
            $$"""[{"carton_id":"C-1","part":"{{SeededErp.Battery}}","po_number":"{{poNumber}}","lot_code":"LOT-T1","units_per_carton":5,"mark_present":true,"mark_legible":true,"un_number":"UN3480","class9_visible":true,"damage_visible":false,"damage_note":null,"read_confidence":0.99}]""");

        var set = await h.Runner.BuildDockImagesAsync(caseId);

        Assert.NotNull(set);
        Assert.Equal(poNumber, set.PoNumber);
        var image = Assert.Single(set.Images);
        Assert.Equal("LIVE-01", image.ImageId);
        var timeline = CaseStore.Load(h.CaseDir, caseId)!.Events
            .Where(e => e.Kind == CaseEventKind.Note)
            .Select(e => e.Summary)
            .ToList();
        Assert.Contains(timeline, s => s.Contains("built live", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveOffSkipsGenerator()
    {
        var first = Create();
        var caseId = await OpenAsnCaseAsync(first);
        var generator = new StubSceneGenerator(_ =>
            throw new InvalidOperationException("Generator must not run while live is off."));
        var h = new Harness(
            first.Erp,
            new CaseRunner(
                first.Erp,
                new P2pAgent(first.Erp, first.Models, first.Compliance),
                first.Models, first.Compliance, new DockEvaluatorAdapter(), first.CaseDir,
                dockImageOptions: BadSpecsOptions(first.CaseDir),
                dockSceneLive: new DockSceneLive { SceneGenerator = generator }),
            first.Models, first.Compliance, first.CaseDir);

        var set = await h.Runner.BuildDockImagesAsync(caseId);

        Assert.Null(set);
        Assert.Equal(0, generator.CallCount);
    }

    [Fact]
    public async Task LiveOnWithoutGeneratorFallsBackCleanly()
    {
        var first = Create();
        var caseId = await OpenAsnCaseAsync(first);
        var scene = new DockSceneLive { LiveOn = true };
        var h = new Harness(
            first.Erp,
            new CaseRunner(
                first.Erp,
                new P2pAgent(first.Erp, first.Models, first.Compliance),
                first.Models, first.Compliance, new DockEvaluatorAdapter(), first.CaseDir,
                dockImageOptions: BadSpecsOptions(first.CaseDir), dockSceneLive: scene),
            first.Models, first.Compliance, first.CaseDir);

        var set = await h.Runner.BuildDockImagesAsync(caseId);

        Assert.Null(set);
        var timeline = CaseStore.Load(h.CaseDir, caseId)!.Events
            .Where(e => e.Kind == CaseEventKind.Note)
            .Select(e => e.Summary)
            .ToList();
        var fallback = Assert.Single(timeline, s => s.Contains("library views used", StringComparison.Ordinal));
        Assert.Contains(DockImagePipeline.NoLiveScenesMessage, fallback, StringComparison.Ordinal);
        Assert.Equal(DockImagePipeline.NoLiveScenesMessage, scene.LastFallbackReason);
    }

    [Fact]
    public async Task RebuildMergesAndPersistsProblems()
    {
        // The specs stay missing on purpose (the pipeline failure is the
        // test path); the problems sidecar and timeline carry the
        // assertions.
        var plain = Create();
        var h = plain with
        {
            Runner = new CaseRunner(
                plain.Erp,
                new P2pAgent(plain.Erp, plain.Models, plain.Compliance),
                plain.Models, plain.Compliance, new DockEvaluatorAdapter(), plain.CaseDir,
                dockImageOptions: BadSpecsOptions(plain.CaseDir)),
        };
        var caseId = await OpenAsnCaseAsync(h, [new SimulatedProblem("C-1", SimulatedProblemKind.MissingMark)]);

        var set = await h.Runner.RebuildDockViewsAsync(new RebuildDockViewsInput(
            caseId, [new SimulatedProblem("C-1", SimulatedProblemKind.Hidden)]));

        Assert.Null(set);
        var stored = CaseStore.LoadProblems(h.CaseDir, caseId)!;
        var problem = Assert.Single(stored.Problems);
        Assert.Equal(SimulatedProblemKind.Hidden, problem.Kind);
        var timeline = CaseStore.Load(h.CaseDir, caseId)!.Events
            .Where(e => e.Kind == CaseEventKind.Note)
            .Select(e => e.Summary)
            .ToList();
        Assert.Contains(timeline, s => s.Contains("C-1 (Hidden)", StringComparison.Ordinal));
    }

    [Fact]
    public void UploadTranscriptionsTakeNoProblems()
    {
        foreach (var name in new[]
            {
                nameof(CaseRunner.TranscribeDockPhotosAsync),
                nameof(CaseRunner.TranscribeInvoicePhotosAsync),
            })
        {
            var method = typeof(CaseRunner).GetMethod(name);
            Assert.NotNull(method);
            foreach (var parameter in method!.GetParameters())
            {
                Assert.DoesNotContain("problem", parameter.ParameterType.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("problem", parameter.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string ReadsJson(string poNumber, bool c2MarkMissing, bool c3Crushed)
    {
        static string Read(string carton, string lot, bool mark, bool damage, string po) =>
            "{\"carton_id\":\"" + carton
            + "\",\"part\":\"" + SeededErp.Battery
            + "\",\"po_number\":\"" + po
            + "\",\"lot_code\":\"" + lot
            + "\",\"units_per_carton\":5"
            + ",\"mark_present\":" + (mark ? "true" : "false")
            + ",\"mark_legible\":" + (mark ? "true" : "false")
            + ",\"un_number\":" + (mark ? "\"UN3480\"" : "null")
            + ",\"class9_visible\":true"
            + ",\"damage_visible\":" + (damage ? "true" : "false")
            + ",\"damage_note\":" + (damage ? "\"crushed corner with tear\"" : "null")
            + ",\"read_confidence\":0.99}";
        return "["
            + Read("C-1", "LOT-T1", true, false, poNumber) + ","
            + Read("C-2", "LOT-T2", !c2MarkMissing, false, poNumber) + ","
            + Read("C-3", "LOT-T3", true, c3Crushed, poNumber) + "]";
    }

    [Fact]
    public async Task TwoProblemsOnOneShipmentLandExactly()
    {
        // Three cartons, problems on two: the missing mark holds C-2 for
        // relabel, the crushed carton routes C-3 to damage review, and C-1
        // passes every rule. Pixels change only on the two chosen cartons.
        var first = Create();
        var caseId = await OpenAsnCaseAsync(first, cartons:
        [
            new AsnCartonInput("C-1", SeededErp.Battery, 5, "LOT-T1"),
            new AsnCartonInput("C-2", SeededErp.Battery, 5, "LOT-T2"),
            new AsnCartonInput("C-3", SeededErp.Battery, 5, "LOT-T3"),
        ]);
        var h = new Harness(
            first.Erp,
            new CaseRunner(
                first.Erp,
                new P2pAgent(first.Erp, first.Models, first.Compliance),
                first.Models, first.Compliance, new DockEvaluatorAdapter(), first.CaseDir,
                dockImageOptions: LibraryOptions(first.CaseDir)),
            first.Models, first.Compliance, first.CaseDir);
        var poNumber = CaseStore.Load(first.CaseDir, caseId)!.Case.PoNumber!;
        h.Models.QueueText(ReadsJson(poNumber, c2MarkMissing: false, c3Crushed: false));

        var clean = await h.Runner.BuildDockImagesAsync(caseId);
        Assert.NotNull(clean);
        var cleanImage = Assert.Single(clean.Images);
        Assert.Contains("SCENE-04", cleanImage.FilePath, StringComparison.Ordinal);
        var cleanBytes = await File.ReadAllBytesAsync(cleanImage.FilePath);

        // The faithful problem read mismatches the manifest (no UN text
        // where the mark is missing), so mint reads twice, warns, and keeps
        // the views without caching the read.
        var problemReads = ReadsJson(poNumber, c2MarkMissing: true, c3Crushed: true);
        h.Models.QueueText(problemReads);
        h.Models.QueueText(problemReads);
        var set = await h.Runner.RebuildDockViewsAsync(new RebuildDockViewsInput(caseId,
        [
            new SimulatedProblem("C-2", SimulatedProblemKind.MissingMark),
            new SimulatedProblem("C-3", SimulatedProblemKind.Crushed),
        ]));
        Assert.NotNull(set);
        var image = Assert.Single(set.Images);
        Assert.Equal(cleanImage.FilePath, image.FilePath);
        var problemBytes = await File.ReadAllBytesAsync(image.FilePath);
        Assert.NotEqual(cleanBytes, problemBytes);

        var recorded = CaseStore.LoadDockViews(first.CaseDir, caseId);
        Assert.NotNull(recorded);
        Assert.Equal([Path.GetFileName(image.FilePath)], recorded.Images.ToList());

        var timeline = CaseStore.Load(h.CaseDir, caseId)!.Events
            .Where(e => e.Kind == CaseEventKind.Note)
            .Select(e => e.Summary)
            .ToList();
        var problems = Assert.Single(timeline, s => s.Contains("2 simulated problem(s)", StringComparison.Ordinal));
        Assert.Contains("C-2 (MissingMark)", problems, StringComparison.Ordinal);
        Assert.Contains("C-3 (Crushed)", problems, StringComparison.Ordinal);

        PixelsChangeOnlyOnChosenCartons(cleanBytes, problemBytes);

        h.Models.QueueText(problemReads);
        var reads = await h.Runner.TranscribeDockPhotosAsync(
            caseId, [new DockPhoto(image.ImageId, problemBytes, null)]);
        Assert.Equal(3, reads.Count);
        var outcome = await h.Runner.OnDockReadsAsync(caseId, reads);

        Assert.Equal(["C-1", "C-2", "C-3"], outcome.Keys.Order(StringComparer.Ordinal).ToList());
        Assert.All(outcome["C-1"], v => Assert.Equal(VerdictKind.Pass, v.Kind));
        var dock = new DockEvaluatorAdapter();
        var markHold = Assert.Single(outcome["C-2"], v => v.Kind != VerdictKind.Pass);
        Assert.Equal(VerdictKind.Hold, markHold.Kind);
        Assert.Equal(dock.MarkRuleId, markHold.RuleId);
        Assert.Contains("hold for relabel", markHold.Reason, StringComparison.Ordinal);
        var damageReview = Assert.Single(outcome["C-3"], v => v.Kind != VerdictKind.Pass);
        Assert.Equal(VerdictKind.HumanReview, damageReview.Kind);
        Assert.Equal(dock.DamageRuleId, damageReview.RuleId);

        var holds = await first.Erp.GetOpenHoldsAsync(null);
        var hold = Assert.Single(holds);
        Assert.Equal("C-2", hold.CartonId);
        var decisions = CaseStore.Load(first.CaseDir, caseId)!.Decisions;
        Assert.Contains(decisions, d =>
            d.Kind == DecisionKind.DamageReview
            && d.Status == DecisionStatus.Pending
            && d.Title.Contains("C-3", StringComparison.Ordinal));
        Assert.DoesNotContain(decisions, d =>
            d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview
            && (d.Title.Contains("C-1", StringComparison.Ordinal)
                || d.Title.Contains("C-2", StringComparison.Ordinal)));
    }

    /// <summary>Decodes the clean and problem builds and proves the problem
    /// pixels land only on the chosen cartons: C-1's face is identical, both
    /// chosen faces change, and every changed pixel sits on a chosen carton
    /// (the crushed zone reaches past its face by design, plus one JPEG
    /// block of tolerance).</summary>
    private static void PixelsChangeOnlyOnChosenCartons(byte[] cleanBytes, byte[] problemBytes)
    {
        using var clean = SKBitmap.Decode(cleanBytes)
            ?? throw new InvalidOperationException("Clean dock view did not decode.");
        using var problem = SKBitmap.Decode(problemBytes)
            ?? throw new InvalidOperationException("Problem dock view did not decode.");
        Assert.Equal(clean.Width, problem.Width);
        Assert.Equal(clean.Height, problem.Height);
        Assert.Equal(clean.BytesPerPixel, problem.BytesPerPixel);
        Assert.Equal(clean.RowBytes, problem.RowBytes);
        var (w, h) = (clean.Width, clean.Height);
        var faces = DockImagePipeline.LoadScenes(VisionFiles.DockScenesPath())
            .First(s => string.Equals(s.Id, "SCENE-04", StringComparison.Ordinal)).Faces;
        Assert.True(faces.Count >= 3, "SCENE-04 carries the three test cartons.");

        // Ceilings on the far edges: the compositor's float edges plus
        // label strokes plus one JPEG block of bleed all fit inside 12px.
        static SKRectI Pixels(DockSceneFace face, int w, int h, int margin) => new(
            Math.Max(0, (int)(face.X * w) - margin),
            Math.Max(0, (int)(face.Y * h) - margin),
            Math.Min(w, (int)Math.Ceiling((face.X + face.W) * w) + margin),
            Math.Min(h, (int)Math.Ceiling((face.Y + face.H) * h) + margin));
        var untouched = Pixels(faces[0], w, h, -8);
        var chosenMark = Pixels(faces[1], w, h, 12);
        // The crushed cue washes 35% past the face on every side.
        var crushed = faces[2];
        var chosenCrushed = new SKRectI(
            Math.Max(0, (int)((crushed.X - crushed.W * 0.35) * w) - 12),
            Math.Max(0, (int)((crushed.Y - crushed.H * 0.35) * h) - 12),
            Math.Min(w, (int)Math.Ceiling((crushed.X + crushed.W * 1.35) * w) + 12),
            Math.Min(h, (int)Math.Ceiling((crushed.Y + crushed.H * 1.35) * h) + 12));

        var a = clean.GetPixelSpan();
        var b = problem.GetPixelSpan();
        var bpp = clean.BytesPerPixel;
        var rowBytes = clean.RowBytes;
        var cleanChanged = 0;
        var markChanged = 0;
        var crushedChanged = 0;
        var outsideChanged = 0;
        var outsideMin = (X: w, Y: h);
        var outsideMax = (X: -1, Y: -1);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var at = y * rowBytes + x * bpp;
                var same = true;
                for (var o = 0; o < bpp; o++)
                {
                    if (a[at + o] != b[at + o])
                    {
                        same = false;
                        break;
                    }
                }

                if (same)
                {
                    continue;
                }

                var point = new SKPointI(x, y);
                if (untouched.Contains(point))
                {
                    cleanChanged++;
                }

                if (chosenMark.Contains(point))
                {
                    markChanged++;
                }

                if (chosenCrushed.Contains(point))
                {
                    crushedChanged++;
                }

                if (!chosenMark.Contains(point) && !chosenCrushed.Contains(point))
                {
                    outsideChanged++;
                    outsideMin = (Math.Min(outsideMin.X, x), Math.Min(outsideMin.Y, y));
                    outsideMax = (Math.Max(outsideMax.X, x), Math.Max(outsideMax.Y, y));
                }
            }
        }

        Assert.Equal(0, cleanChanged);
        Assert.True(markChanged > 100, $"C-2's face changed {markChanged} pixel(s); the missing mark must show.");
        Assert.True(crushedChanged > 100, $"C-3's zone changed {crushedChanged} pixel(s); the damage cue must show.");
        Assert.True(outsideChanged == 0,
            $"{outsideChanged} changed pixel(s) outside the chosen cartons in a {w}x{h} image " +
            $"(bbox x {outsideMin.X}..{outsideMax.X}, y {outsideMin.Y}..{outsideMax.Y}; " +
            $"C-1 face inset {untouched.Left},{untouched.Top}-{untouched.Right},{untouched.Bottom}; " +
            $"C-2 rect {chosenMark.Left},{chosenMark.Top}-{chosenMark.Right},{chosenMark.Bottom}; " +
            $"C-3 zone {chosenCrushed.Left},{chosenCrushed.Top}-{chosenCrushed.Right},{chosenCrushed.Bottom}).");
    }
}

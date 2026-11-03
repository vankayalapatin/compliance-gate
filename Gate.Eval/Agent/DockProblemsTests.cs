// Dock-problem threading tests (offline): ASN problems persist per case,
// rebuild overrides win per carton and land on the timeline, the live
// switch picks the live scene with a recorded library fallback, and photo
// uploads structurally take no problems. Test-double data only.
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;

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

    private static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";

    private static string AwardArgs(string caseId, string vendorId) =>
        $$"""{"case_id":"{{caseId}}","vendor_id":"{{vendorId}}"}""";

    /// <summary>Drives one battery case to an ASN with the given staged
    /// problems persisted: webhook, shortfall, PR, screen, award, PO, ACK,
    /// ASN plus receipt.</summary>
    private static async Task<string> OpenAsnCaseAsync(
        Harness h, IReadOnlyList<SimulatedProblem>? problems = null)
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
            [new AsnCartonInput("C-1", SeededErp.Battery, 5, "LOT-T1")],
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
}

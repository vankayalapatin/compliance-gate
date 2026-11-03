// Dock library scenes: photos for every shipment (offline). The scene
// catalog covers exact and odd carton counts, problems render per carton,
// close-ups cover every visible carton, and every refusal names its cause.
// All model and generator calls go through fakes; temp dirs only.
using System.Text.Json;
using Gate.Agent;
using Gate.Contracts;
using SkiaSharp;

namespace Gate.Eval;

public sealed class DockScenesTests
{
    private sealed class FakeSceneGenerator(Func<int, int, byte[]> generate) : ISceneGenerator
    {
        public string GeneratorName => "fake-scenes";
        public int CallCount { get; private set; }
        public int LastCount { get; private set; }

        public Task<byte[]> GenerateSceneAsync(int cartonCount, int crushedCount, CancellationToken ct = default)
        {
            CallCount++;
            LastCount = cartonCount;
            return Task.FromResult(generate(cartonCount, crushedCount));
        }
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        public string? SeenBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            SeenBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return respond(request);
        }
    }

    private static string NewDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    private static DockImageOptions LibraryOptions(string tag)
    {
        var root = NewDir($"gate-scenes-{tag}");
        return new DockImageOptions(
            VisionFiles.DockScenesBasesDir(),
            VisionFiles.DockScenesPath(),
            // The library path never reads carton truth: a path that does
            // not exist proves it.
            Path.Combine(root, "unused-truth.json"),
            Path.Combine(root, "out"),
            Path.Combine(root, "imgcache"),
            Path.Combine(root, "readcache"));
    }

    private static AsnDto MakeAsn(string asnNumber, string poNumber, int count, string? lot = null)
    {
        var lotRef = lot ?? $"LOT-{poNumber}-A";
        var cartons = Enumerable.Range(1, count)
            .Select(i => new AsnCarton($"CTN-{i:00}", "BAT-1", 100, lotRef))
            .ToList();
        return new AsnDto(asnNumber, poNumber, "vendor", "V-B", Guid.NewGuid().ToString("N"), cartons);
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<SimulatedProblemKind>> KindsByCarton(
        IReadOnlyList<SimulatedProblem> problems)
    {
        var map = new Dictionary<string, HashSet<SimulatedProblemKind>>(StringComparer.Ordinal);
        foreach (var problem in problems)
        {
            map.TryAdd(problem.CartonId, new HashSet<SimulatedProblemKind>());
            map[problem.CartonId].Add(problem.Kind);
        }

        return map.ToDictionary(
            kv => kv.Key, kv => (IReadOnlySet<SimulatedProblemKind>)kv.Value, StringComparer.Ordinal);
    }

    private static string MatchingLibraryReadJson(
        IReadOnlyList<AsnCarton> cartons, IReadOnlyList<SimulatedProblem> problems, string poNumber)
    {
        var kinds = KindsByCarton(problems);
        var objects = new List<string>();
        foreach (var carton in cartons)
        {
            var set = kinds.TryGetValue(carton.CartonId, out var k)
                ? k
                : (IReadOnlySet<SimulatedProblemKind>)new HashSet<SimulatedProblemKind>();
            if (set.Contains(SimulatedProblemKind.Hidden))
            {
                continue;
            }

            var mark = !set.Contains(SimulatedProblemKind.MissingMark);
            var damaged = set.Contains(SimulatedProblemKind.Crushed);
            objects.Add(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["carton_id"] = carton.CartonId,
                ["part"] = carton.ItemId,
                ["po_number"] = poNumber,
                ["lot_code"] = carton.LotRef,
                ["units_per_carton"] = carton.UnitsPerCarton,
                ["mark_present"] = mark,
                ["mark_legible"] = mark,
                ["un_number"] = "UN3480",
                ["class9_visible"] = true,
                ["damage_visible"] = damaged,
                ["damage_note"] = damaged ? "crushed corner" : null,
                ["read_confidence"] = 0.99
            }));
        }

        return "[" + string.Join(",", objects) + "]";
    }

    private static List<JsonElement> ParseReads(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
    }

    private static IReadOnlyList<LibraryChunk> Assign(string tag, AsnDto asn) =>
        DockImagePipeline.AssignScenes(DockImagePipeline.LoadScenes(VisionFiles.DockScenesPath()), asn.Cartons);

    [Fact]
    public void SceneCatalogMatchesPinnedShape()
    {
        using var doc = VisionFiles.LoadJson(VisionFiles.DockScenesPath());
        var scenes = doc.RootElement.GetProperty("scenes").EnumerateArray().ToList();
        Assert.Equal(6, scenes.Count);
        var capacities = new List<int>();
        foreach (var scene in scenes)
        {
            Assert.False(string.IsNullOrWhiteSpace(scene.GetProperty("id").GetString()));
            Assert.True(scene.GetProperty("closeupScale").GetInt32() >= 1);
            var counts = scene.GetProperty("counts").EnumerateArray().Select(c => c.GetInt32()).ToList();
            Assert.NotEmpty(counts);
            Assert.All(counts, c => Assert.True(c > 0));
            var faces = scene.GetProperty("faces").EnumerateArray().ToList();
            Assert.NotEmpty(faces);
            capacities.Add(faces.Count);
            var position = 0;
            foreach (var face in faces)
            {
                Assert.Equal(position, face.GetProperty("index").GetInt32());
                var x = face.GetProperty("x").GetDouble();
                var y = face.GetProperty("y").GetDouble();
                var w = face.GetProperty("w").GetDouble();
                var h = face.GetProperty("h").GetDouble();
                Assert.InRange(x, 0, 1);
                Assert.InRange(y, 0, 1);
                Assert.True(w > 0 && h > 0);
                Assert.True(x + w <= 1 && y + h <= 1);
                position++;
            }

            var baseFile = Path.Combine(
                VisionFiles.DockScenesBasesDir(), scene.GetProperty("base").GetString()!);
            Assert.True(File.Exists(baseFile));
            using var bitmap = SKBitmap.Decode(File.ReadAllBytes(baseFile));
            Assert.NotNull(bitmap);
            Assert.True(bitmap!.Width >= 400 && bitmap.Height >= 300);
        }

        Assert.Equal([1, 2, 4, 6, 8, 12], capacities.OrderBy(c => c).ToList());
        Assert.Equal(6, DockImagePipeline.LoadScenes(VisionFiles.DockScenesPath()).Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public async Task ExactCountsUseTheirScene(int count)
    {
        var po = $"PO-LIB-EX-{count}";
        var asn = MakeAsn($"ASN-LIB-EX-{count}", po, count);
        var models = new FakeModelClient();
        var chunks = Assign("exact", asn);
        Assert.Single(chunks);
        Assert.Equal(count, chunks[0].Scene.Faces.Count);
        foreach (var chunk in chunks)
        {
            models.QueueResponse(MatchingLibraryReadJson(chunk.Cartons, [], po));
        }

        var set = await new DockImagePipeline(new NullImageLabeler(), models)
            .BuildLibraryAsync(asn, po, [], LibraryOptions($"exact-{count}"));

        Assert.Equal(po, set.PoNumber);
        var image = Assert.Single(set.Images);
        Assert.Equal(chunks[0].Scene.Id, image.ImageId);
        Assert.Equal(DockImagePath.Composited, image.Path);
        Assert.Equal(0, image.LabelAttempts);
        Assert.True(File.Exists(image.FilePath));
        var reads = ParseReads(image.ReadText);
        Assert.Equal(count, reads.Count);
        var ids = new HashSet<string>(reads.Select(r => r.GetProperty("carton_id").GetString()!));
        Assert.Equal(
            new HashSet<string>(asn.Cartons.Select(c => c.CartonId)),
            new HashSet<string>(ids));
        Assert.All(reads, read =>
        {
            Assert.Equal(po, read.GetProperty("po_number").GetString());
            Assert.Equal(asn.Cartons[0].LotRef, read.GetProperty("lot_code").GetString());
            Assert.True(read.GetProperty("mark_present").GetBoolean());
            Assert.False(read.GetProperty("damage_visible").GetBoolean());
        });
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(5, 6)]
    [InlineData(7, 8)]
    [InlineData(9, 12)]
    [InlineData(10, 12)]
    [InlineData(11, 12)]
    public async Task OddCountsUseNearestLargerScene(int count, int expectedCapacity)
    {
        var po = $"PO-LIB-ODD-{count}";
        var asn = MakeAsn($"ASN-LIB-ODD-{count}", po, count);
        var models = new FakeModelClient();
        var chunks = Assign("odd", asn);
        Assert.Single(chunks);
        Assert.Equal(expectedCapacity, chunks[0].Scene.Faces.Count);
        foreach (var chunk in chunks)
        {
            models.QueueResponse(MatchingLibraryReadJson(chunk.Cartons, [], po));
        }

        var set = await new DockImagePipeline(new NullImageLabeler(), models)
            .BuildLibraryAsync(asn, po, [], LibraryOptions($"odd-{count}"));

        var image = Assert.Single(set.Images);
        Assert.Equal(chunks[0].Scene.Id, image.ImageId);
        var reads = ParseReads(image.ReadText);
        Assert.Equal(count, reads.Count);
        Assert.Equal(
            asn.Cartons.Select(c => c.CartonId).OrderBy(id => id).ToList(),
            reads.Select(r => r.GetProperty("carton_id").GetString()!).OrderBy(id => id).ToList());
    }

    [Theory]
    [InlineData(13, 2)]
    [InlineData(24, 2)]
    [InlineData(25, 3)]
    public async Task LargeCountsSplitIntoViews(int count, int expectedViews)
    {
        var po = $"PO-LIB-BIG-{count}";
        var asn = MakeAsn($"ASN-LIB-BIG-{count}", po, count);
        var models = new FakeModelClient();
        foreach (var chunk in Assign("big", asn))
        {
            models.QueueResponse(MatchingLibraryReadJson(chunk.Cartons, [], po));
        }

        var set = await new DockImagePipeline(new NullImageLabeler(), models)
            .BuildLibraryAsync(asn, po, [], LibraryOptions($"big-{count}"));

        Assert.Equal(expectedViews, set.Images.Count);
        Assert.Equal(
            expectedViews,
            set.Images.Select(i => i.ImageId).Distinct(StringComparer.Ordinal).Count());
        var reads = set.Images.SelectMany(i => ParseReads(i.ReadText)).ToList();
        Assert.Equal(count, reads.Count);
        Assert.Equal(
            asn.Cartons.Select(c => c.CartonId).OrderBy(id => id).ToList(),
            reads.Select(r => r.GetProperty("carton_id").GetString()!).OrderBy(id => id).ToList());
    }

    [Fact]
    public async Task SameShipmentRebuildsDeterministically()
    {
        var po = "PO-LIB-REPLAY";
        var asn = MakeAsn("ASN-LIB-REPLAY", po, 6);
        var options = LibraryOptions("replay");
        var models = new FakeModelClient();
        foreach (var chunk in Assign("replay", asn))
        {
            models.QueueResponse(MatchingLibraryReadJson(chunk.Cartons, [], po));
        }

        var pipeline = new DockImagePipeline(new NullImageLabeler(), models);
        var first = await pipeline.BuildLibraryAsync(asn, po, [], options);
        Assert.Equal(1, models.CallCount);
        var second = await pipeline.BuildLibraryAsync(asn, po, [], options);
        Assert.Equal(1, models.CallCount);
        Assert.All(second.Images, image => Assert.True(image.ReadFromCache));
        Assert.Equal(
            first.Images.Select(image => image.ImageSha256),
            second.Images.Select(image => image.ImageSha256));
    }

    [Fact]
    public async Task ProblemsRenderPerCarton()
    {
        var po = "PO-LIB-PROB";
        var asn = MakeAsn("ASN-LIB-PROB", po, 4);
        var problems = new List<SimulatedProblem>
        {
            new("CTN-02", SimulatedProblemKind.MissingMark),
            new("CTN-03", SimulatedProblemKind.Crushed),
            new("CTN-04", SimulatedProblemKind.Hidden),
        };
        var models = new FakeModelClient();
        foreach (var chunk in Assign("prob", asn))
        {
            models.QueueResponse(MatchingLibraryReadJson(chunk.Cartons, problems, po));
        }

        var set = await new DockImagePipeline(new NullImageLabeler(), models)
            .BuildLibraryAsync(asn, po, problems, LibraryOptions("prob"));

        var image = Assert.Single(set.Images);
        var reads = ParseReads(image.ReadText).ToDictionary(
            r => r.GetProperty("carton_id").GetString()!, r => r, StringComparer.Ordinal);
        Assert.Equal(["CTN-01", "CTN-02", "CTN-03"], reads.Keys.OrderBy(id => id).ToList());
        Assert.True(reads["CTN-01"].GetProperty("mark_present").GetBoolean());
        Assert.False(reads["CTN-01"].GetProperty("damage_visible").GetBoolean());
        Assert.False(reads["CTN-02"].GetProperty("mark_present").GetBoolean());
        Assert.True(reads["CTN-03"].GetProperty("damage_visible").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(reads["CTN-03"].GetProperty("damage_note").GetString()));

        // The cues change pixels: same cartons without problems photo differently.
        var cleanModels = new FakeModelClient();
        foreach (var chunk in Assign("prob-clean", asn))
        {
            cleanModels.QueueResponse(MatchingLibraryReadJson(chunk.Cartons, [], po));
        }

        var clean = await new DockImagePipeline(new NullImageLabeler(), cleanModels)
            .BuildLibraryAsync(asn, po, [], LibraryOptions("prob-clean"));
        Assert.NotEqual(clean.Images[0].ImageSha256, image.ImageSha256);
    }

    [Theory]
    [InlineData("CTN-01")]
    [InlineData("CTN-03")]
    [InlineData("CTN-06")]
    public async Task CloseupCoversEveryCarton(string cartonId)
    {
        var po = "PO-LIB-CU";
        var asn = MakeAsn("ASN-LIB-CU", po, 6);
        var carton = asn.Cartons.Single(c => c.CartonId == cartonId);
        var models = new FakeModelClient();
        models.QueueResponse(MatchingLibraryReadJson([carton], [], po));

        var image = await new DockImagePipeline(new NullImageLabeler(), models)
            .BuildCloseupAsync(asn, po, cartonId, [], LibraryOptions($"cu-{cartonId}"));

        Assert.Contains(cartonId, image.ImageId, StringComparison.Ordinal);
        Assert.True(File.Exists(image.FilePath));
        var read = Assert.Single(ParseReads(image.ReadText));
        Assert.Equal(cartonId, read.GetProperty("carton_id").GetString());
        Assert.Equal(po, read.GetProperty("po_number").GetString());
        Assert.Equal(carton.LotRef, read.GetProperty("lot_code").GetString());
    }

    [Fact]
    public async Task CloseupRefusesHiddenAndUnknownCartons()
    {
        var po = "PO-LIB-CU-REF";
        var asn = MakeAsn("ASN-LIB-CU-REF", po, 4);
        var problems = new List<SimulatedProblem>
        {
            new("CTN-02", SimulatedProblemKind.Hidden)
        };
        var pipeline = new DockImagePipeline(new NullImageLabeler(), new FakeModelClient());
        var hidden = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.BuildCloseupAsync(asn, po, "CTN-02", problems, LibraryOptions("cu-hidden")));
        Assert.Contains("hidden", hidden.Message);
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.BuildCloseupAsync(asn, po, "CTN-99", [], LibraryOptions("cu-unknown")));
        Assert.Contains("not on ASN", unknown.Message);
    }

    [Fact]
    public async Task VerifyMismatchWarnsAndKeepsTheView()
    {
        // Library labels are code-drawn and certain: a persistent reader
        // miss warns instead of failing the build, and the mismatched
        // read is never cached, so the next run reads live again.
        var po = "PO-LIB-MM";
        var asn = MakeAsn("ASN-LIB-MM", po, 2);
        var models = new FakeModelClient();
        foreach (var chunk in Assign("mm", asn))
        {
            models.QueueResponse(MatchingLibraryReadJson(chunk.Cartons, [], "PO-WRONG"));
            models.QueueResponse(MatchingLibraryReadJson(chunk.Cartons, [], "PO-WRONG"));
        }

        var options = LibraryOptions("mm");
        var set = await new DockImagePipeline(new NullImageLabeler(), models)
            .BuildLibraryAsync(asn, po, [], options);

        var image = Assert.Single(set.Images);
        Assert.NotNull(image.ReadWarning);
        Assert.Contains("CTN-01", image.ReadWarning, StringComparison.Ordinal);
        Assert.Contains("po_number", image.ReadWarning, StringComparison.Ordinal);
        Assert.Equal(2, models.CallCount);
        Assert.True(File.Exists(image.FilePath));
        Assert.Null(new VisionReadCache(options.ReadCacheDir)
            .TryRead(image.ImageSha256, options.ReadPromptVersion));
    }

    [Fact]
    public async Task CompositedLabelCarriesALegibleClass9Diamond()
    {
        // One carton on the 1-carton scene: the Class 9 diamond must
        // render yellow with a black outline and 9, large enough to
        // read (the old solid-black mark misread repeatedly).
        var po = "PO-LIB-DIA";
        var asn = MakeAsn("ASN-LIB-DIA", po, 1);
        var models = new FakeModelClient();
        foreach (var chunk in Assign("dia", asn))
        {
            models.QueueResponse(MatchingLibraryReadJson(chunk.Cartons, [], po));
        }

        var set = await new DockImagePipeline(new NullImageLabeler(), models)
            .BuildLibraryAsync(asn, po, [], LibraryOptions("dia"));
        var image = Assert.Single(set.Images);
        Assert.Null(image.ReadWarning);

        using var doc = VisionFiles.LoadJson(VisionFiles.DockScenesPath());
        var face = doc.RootElement.GetProperty("scenes").EnumerateArray()
            .First(s => s.GetProperty("id").GetString() == image.ImageId)
            .GetProperty("faces").EnumerateArray().First();
        using var bitmap = SKBitmap.Decode(File.ReadAllBytes(image.FilePath));
        Assert.NotNull(bitmap);
        var lx = (int)(face.GetProperty("x").GetDouble() * bitmap!.Width);
        var ly = (int)(face.GetProperty("y").GetDouble() * bitmap.Height);
        var lw = (int)(face.GetProperty("w").GetDouble() * bitmap.Width);
        var lh = (int)(face.GetProperty("h").GetDouble() * bitmap.Height);

        var yellow = 0;
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        for (var y = ly; y < ly + lh && y < bitmap.Height; y++)
        {
            for (var x = lx; x < lx + lw && x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red > 200 && pixel.Green > 200 && pixel.Blue < 150)
                {
                    yellow++;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        Assert.True(yellow > 800, $"Yellow diamond pixels: {yellow}.");
        Assert.True(maxX - minX >= 55, $"Diamond width: {maxX - minX}.");
        Assert.True(maxY - minY >= 55, $"Diamond height: {maxY - minY}.");

        var dark = 0;
        for (var y = Math.Max(ly, minY - 8); y <= Math.Min(ly + lh - 1, maxY + 8) && y < bitmap.Height; y++)
        {
            for (var x = Math.Max(lx, minX - 8); x <= Math.Min(lx + lw - 1, maxX + 8) && x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red < 100 && pixel.Green < 100 && pixel.Blue < 100)
                {
                    dark++;
                }
            }
        }

        Assert.True(dark > 200, $"Dark outline/9 pixels around the diamond: {dark}.");
    }

    [Fact]
    public void TryVerifyReadsNamesEveryMismatch()
    {
        var labels = new List<DockImagePipeline.ResolvedLabel>
        {
            new("CTN-01", "BAT-1", 100, "LOT-1", true, 0, 0, 0.1, 0.1, "PO-1")
        };
        var clean = new HashSet<string>(StringComparer.Ordinal);
        string Good(string? note = null, bool damage = false) => JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object?>
            {
                ["carton_id"] = "CTN-01", ["part"] = "BAT-1", ["po_number"] = "PO-1",
                ["lot_code"] = "LOT-1", ["units_per_carton"] = 100,
                ["mark_present"] = true, ["mark_legible"] = true, ["un_number"] = "UN3480",
                ["class9_visible"] = true, ["damage_visible"] = damage,
                ["damage_note"] = note, ["read_confidence"] = 0.99
            }
        });
        Assert.True(DockImagePipeline.TryVerifyReads(Good(), labels, clean, out _));

        var wrongPo = Good().Replace("PO-1", "PO-9", StringComparison.Ordinal);
        Assert.False(DockImagePipeline.TryVerifyReads(wrongPo, labels, clean, out var poDetail));
        Assert.Contains("CTN-01", poDetail);
        Assert.Contains("po_number", poDetail);

        var wrongMark = Good().Replace("\"mark_present\":true", "\"mark_present\":false", StringComparison.Ordinal);
        Assert.False(DockImagePipeline.TryVerifyReads(wrongMark, labels, clean, out var markDetail));
        Assert.Contains("mark_present", markDetail);

        var damaged = new HashSet<string>(["CTN-01"], StringComparer.Ordinal);
        Assert.False(DockImagePipeline.TryVerifyReads(Good(), labels, damaged, out var flagDetail));
        Assert.Contains("damage_visible", flagDetail);
        Assert.False(DockImagePipeline.TryVerifyReads(Good("torn corner"), labels, damaged, out var flagOffDetail));
        Assert.Contains("damage_visible", flagOffDetail);
        Assert.False(DockImagePipeline.TryVerifyReads(Good(null, damage: true), labels, damaged, out var noteDetail));
        Assert.Contains("damage_note", noteDetail);
        Assert.True(DockImagePipeline.TryVerifyReads(Good("torn corner", damage: true), labels, damaged, out _));

        Assert.False(DockImagePipeline.TryVerifyReads("[]", labels, clean, out var countDetail));
        Assert.Contains("expected 1", countDetail);

        var wrongCarton = Good().Replace("CTN-01", "CTN-02", StringComparison.Ordinal);
        Assert.False(DockImagePipeline.TryVerifyReads(wrongCarton, labels, clean, out var idDetail));
        Assert.Contains("no read for carton CTN-01", idDetail);

        Assert.False(DockImagePipeline.TryVerifyReads("[{broken", labels, clean, out var parseDetail));
        Assert.Contains("unparseable", parseDetail);
    }

    [Fact]
    public async Task LivePathWithoutCredentialsFailsCleanly()
    {
        var asn = MakeAsn("ASN-LIB-NC", "PO-LIB-NC", 2);
        var (set, reason) = await new DockImagePipeline(new NullImageLabeler(), new FakeModelClient())
            .TryBuildLiveSceneAsync(asn, "PO-LIB-NC", [], LibraryOptions("nc"));
        Assert.Null(set);
        Assert.Equal(DockImagePipeline.NoLiveScenesMessage, reason);
    }

    [Fact]
    public async Task LivePathGenerationFailureReturnsTransportReason()
    {
        var asn = MakeAsn("ASN-LIB-GF", "PO-LIB-GF", 2);
        var generator = new FakeSceneGenerator((_, _) => throw new HttpRequestException("generation endpoint gone"));
        var (set, reason) = await new DockImagePipeline(
                new NullImageLabeler(), new FakeModelClient(), sceneGenerator: generator)
            .TryBuildLiveSceneAsync(asn, "PO-LIB-GF", [], LibraryOptions("gf"));
        Assert.Null(set);
        Assert.Contains("generation endpoint gone", reason);
        Assert.Equal(2, generator.LastCount);
    }

    [Fact]
    public async Task LivePathFaceCountMismatchReturnsReason()
    {
        var asn = MakeAsn("ASN-LIB-FC", "PO-LIB-FC", 2);
        var baseBytes = File.ReadAllBytes(Path.Combine(VisionFiles.DockScenesBasesDir(), "scene-02.jpg"));
        var generator = new FakeSceneGenerator((_, _) => baseBytes);
        var models = new FakeModelClient();
        models.QueueResponse("""[{"x":0.1,"y":0.2,"w":0.2,"h":0.3}]""");
        var (set, reason) = await new DockImagePipeline(
                new NullImageLabeler(), models, sceneGenerator: generator)
            .TryBuildLiveSceneAsync(asn, "PO-LIB-FC", [], LibraryOptions("fc"));
        Assert.Null(set);
        Assert.Contains("1 carton face(s) for 2 carton(s)", reason);
    }

    [Fact]
    public async Task LivePathSuccessVerifiesLabels()
    {
        var po = "PO-LIB-LIVE";
        var asn = MakeAsn("ASN-LIB-LIVE", po, 2);
        var baseBytes = File.ReadAllBytes(Path.Combine(VisionFiles.DockScenesBasesDir(), "scene-02.jpg"));
        var generator = new FakeSceneGenerator((_, _) => baseBytes);
        var scene = DockImagePipeline.LoadScenes(VisionFiles.DockScenesPath())
            .Single(s => s.Faces.Count == 2);
        var models = new FakeModelClient();
        models.QueueResponse(JsonSerializer.Serialize(scene.Faces.Select(f =>
            new Dictionary<string, object>
            {
                ["x"] = f.X, ["y"] = f.Y, ["w"] = f.W, ["h"] = f.H
            }).ToList()));
        models.QueueResponse(MatchingLibraryReadJson(asn.Cartons.ToList(), [], po));

        var (set, reason) = await new DockImagePipeline(
                new NullImageLabeler(), models, sceneGenerator: generator)
            .TryBuildLiveSceneAsync(asn, po, [], LibraryOptions("live"));

        Assert.Null(reason);
        Assert.NotNull(set);
        var image = Assert.Single(set!.Images);
        Assert.Equal("LIVE-01", image.ImageId);
        Assert.True(File.Exists(image.FilePath));
        var reads = ParseReads(image.ReadText);
        Assert.Equal(2, reads.Count);
        Assert.All(reads, read => Assert.Equal(po, read.GetProperty("po_number").GetString()));
    }

    [Fact]
    public async Task LibraryRefusalsNameTheirCause()
    {
        var pipeline = new DockImagePipeline(new NullImageLabeler(), new FakeModelClient());
        var empty = new AsnDto("ASN-LIB-EMPTY", "PO-LIB-EMPTY", "vendor", "V-B", "k", []);
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.BuildLibraryAsync(empty, "PO-LIB-EMPTY", [], LibraryOptions("ref-empty")));
        Assert.Contains("no cartons", none.Message);

        var asn = MakeAsn("ASN-LIB-REF", "PO-LIB-REF", 2);
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.BuildLibraryAsync(asn, "PO-LIB-REF",
                [new SimulatedProblem("CTN-99", SimulatedProblemKind.Crushed)],
                LibraryOptions("ref-unknown")));
        Assert.Contains("not on ASN", unknown.Message);

        var clash = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.BuildLibraryAsync(asn, "PO-LIB-REF",
                [new SimulatedProblem("CTN-01", SimulatedProblemKind.Hidden),
                 new SimulatedProblem("CTN-01", SimulatedProblemKind.Crushed)],
                LibraryOptions("ref-clash")));
        Assert.Contains("cannot also", clash.Message);

        var badCatalog = Path.Combine(NewDir("gate-scenes-bad"), "scenes.json");
        Directory.CreateDirectory(Path.GetDirectoryName(badCatalog)!);
        await File.WriteAllTextAsync(badCatalog,
            """{"scenes":[{"id":"BAD","counts":[2],"base":"scene-02.jpg","closeupScale":2,"faces":[{"index":1,"x":0,"y":0,"w":0.1,"h":0.1},{"index":0,"x":0.2,"y":0,"w":0.1,"h":0.1}]}]}""");
        var unordered = Assert.Throws<InvalidOperationException>(() =>
            DockImagePipeline.LoadScenes(badCatalog));
        Assert.Contains("ordered by index", unordered.Message);
    }

    [Fact]
    public async Task MetaSceneGeneratorPostsModelAndPrompt()
    {
        var prompt = MetaSceneGenerator.ScenePrompt(4);
        Assert.Contains("4", prompt);
        var png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
        var handler = new CaptureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"b64_json\":\"" + png + "\"}]}")
        });
        using var http = new HttpClient(handler);
        var bytes = await new MetaSceneGenerator(
                http, new MetaSceneOptions("https://api.meta.ai/v1", "scene-model", "KEY-9"))
            .GenerateSceneAsync(4, 1);
        Assert.NotEmpty(bytes);
        Assert.NotNull(handler.Seen);
        Assert.Equal("Bearer", handler.Seen!.Headers.Authorization?.Scheme);
        Assert.EndsWith("/images/generations", handler.Seen.RequestUri!.AbsolutePath);
        Assert.Contains("caved-in corner", handler.SeenBody, StringComparison.Ordinal);

        var failing = new CaptureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
        {
            Content = new StringContent("no such endpoint")
        });
        using var failingHttp = new HttpClient(failing);
        var boom = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MetaSceneGenerator(
                    failingHttp, new MetaSceneOptions("https://api.meta.ai/v1", "scene-model", "KEY-9"))
                .GenerateSceneAsync(2, 0));
        Assert.Contains("HTTP 404", boom.Message);

        var zero = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MetaSceneGenerator(
                    failingHttp, new MetaSceneOptions("https://api.meta.ai/v1", "scene-model", "KEY-9"))
                .GenerateSceneAsync(0, 0));
        Assert.Contains("0 cartons", zero.Message);

        var over = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new MetaSceneGenerator(
                    failingHttp, new MetaSceneOptions("https://api.meta.ai/v1", "scene-model", "KEY-9"))
                .GenerateSceneAsync(2, 3));
        Assert.Contains("3 worn", over.Message);
    }
}

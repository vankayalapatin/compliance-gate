// ASN-time dock pipeline checks (offline): labels carry the ERP-minted PO,
// verification mismatch falls back to compositing, and replays hit caches.
// All model and labeler calls go through counting fakes; temp dirs only.
using System.Text.Json;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval;

public sealed class VisionPipelineTests
{
    [Fact]
    public async Task MetaLabelerSendsBearerAuth()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var labeler = new MetaImageLabeler(http, new MetaImageOptions("https://api.meta.ai/v1", "muse-image-1.0", "KEY-9"));
        var bytes = await labeler.ApplyLabelsAsync([7], [8], "prompt");
        Assert.NotEmpty(bytes);
        Assert.NotNull(handler.Seen);
        Assert.Equal("Bearer", handler.Seen!.Headers.Authorization?.Scheme);
        Assert.Equal("KEY-9", handler.Seen.Headers.Authorization?.Parameter);
        Assert.EndsWith("/images/edits", handler.Seen.RequestUri!.AbsolutePath);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            var png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"b64_json\":\"" + png + "\"}]}")
            });
        }
    }

    private sealed class FakeImageLabeler(Func<byte[], byte[]> label) : IImageLabeler
    {
        public string LabelerName => "fake-labeler";
        public int CallCount { get; private set; }

        public Task<byte[]> ApplyLabelsAsync(byte[] baseJpeg, byte[] maskPng, string prompt, CancellationToken ct = default)
        {
            CallCount++;
            Assert.NotEmpty(maskPng);
            Assert.Contains("PO ", prompt, StringComparison.Ordinal);
            return Task.FromResult(label(baseJpeg));
        }
    }

    private static string NewDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    private static DockImageOptions Options(string tag) => new(
        Path.Combine(VisionFiles.RepoRoot(), "assets", "dock"),
        VisionFiles.ImageSpecsPath(),
        VisionFiles.CartonsPath(),
        NewDir($"gate-dock-out-{tag}"),
        NewDir($"gate-dock-imgcache-{tag}"),
        NewDir($"gate-dock-readcache-{tag}"));

    private static List<JsonElement> SpecViews()
    {
        using var specs = VisionFiles.LoadJson(VisionFiles.ImageSpecsPath());
        return specs.RootElement.GetProperty("images").EnumerateArray()
            .Select(element => element.Clone()).ToList();
    }

    private static Dictionary<string, JsonElement> CartonRows()
    {
        using var cartons = VisionFiles.LoadJson(VisionFiles.CartonsPath());
        return cartons.RootElement.GetProperty("cartons").EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetString()!, row => row.Clone(), StringComparer.Ordinal);
    }

    private static async Task<(InMemoryErpAdapter Erp, PurchaseOrder Po, AsnDto Asn, string Lot)> MintAsnAsync(
        string asnNumber, string requester = "planner")
    {
        var erp = SeededErp.Create();
        var batteryLine = new PurchaseRequisitionLine(SeededErp.Battery, 90, "EA");
        var pr = await erp.CreatePrAsync(requester, RecordSource.P2pAgent, requester,
            [batteryLine], Guid.NewGuid().ToString("N"));
        if (pr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", requester, Guid.NewGuid().ToString("N"));
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        }

        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, requester, Guid.NewGuid().ToString("N"));
        var lot = $"LOT-{po.PoNumber}-A";
        var rows = CartonRows();
        var asn = await erp.CreateAsnAsync(asnNumber, po.PoNumber, RecordSource.Vendor, "V-B",
            rows.Keys.Select(id => new AsnCarton(id, SeededErp.Battery, 100, lot)).ToList(),
            Guid.NewGuid().ToString("N"));
        return (erp, po, asn, lot);
    }

    private static string MatchingReadJson(JsonElement view, string poNumber, string lot)
    {
        var rows = CartonRows();
        var damage = new HashSet<string>(view.TryGetProperty("damageVisible", out var damageJson)
            ? damageJson.EnumerateArray().Select(element => element.GetString()!)
            : [], StringComparer.Ordinal);
        var objects = new List<string>();
        foreach (var element in view.GetProperty("clearlyVisible").EnumerateArray())
        {
            var id = element.GetString()!;
            var row = rows[id];
            var relabeled = view.GetProperty("labels").EnumerateArray()
                .Any(label => label.GetProperty("carton").GetString() == id
                    && label.TryGetProperty("relabeled", out var relabel) && relabel.GetBoolean());
            var mark = relabeled || row.GetProperty("markPresent").GetBoolean();
            var damaged = damage.Contains(id);
            objects.Add(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["carton_id"] = id,
                ["part"] = SeededErp.Battery,
                ["po_number"] = poNumber,
                ["lot_code"] = lot,
                ["units_per_carton"] = 100,
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

    [Fact]
    public async Task LabelsAndReadsCarryTheMintedPo()
    {
        var (_, po, asn, lot) = await MintAsnAsync("ASN-TEST-1");
        var models = new FakeModelClient();
        var views = SpecViews();
        foreach (var view in views)
        {
            models.QueueResponse(MatchingReadJson(view, po.PoNumber, lot));
        }

        var labeler = new FakeImageLabeler(baseJpeg => baseJpeg);
        var pipeline = new DockImagePipeline(labeler, models);
        var set = await pipeline.BuildAsync(asn, po.PoNumber, Options("po"));

        Assert.Equal(po.PoNumber, set.PoNumber);
        Assert.Equal(4, set.Images.Count);
        Assert.All(set.Images, image => Assert.Equal(DockImagePath.AiLabeled, image.Path));
        Assert.All(set.Images, image => Assert.Null(image.FallbackReason));
        Assert.Equal(4, labeler.CallCount);
        foreach (var image in set.Images)
        {
            Assert.True(File.Exists(image.FilePath));
            foreach (var read in ParseReads(image.ReadText))
            {
                Assert.Equal(po.PoNumber, read.GetProperty("po_number").GetString());
                Assert.Equal(lot, read.GetProperty("lot_code").GetString());
            }
        }

        // Dock check over the pipeline reads: 07 holds, 04 reviews, 11 reviews.
        var evaluator = new DockEvaluator();
        var byCarton = new Dictionary<string, List<CartonRead>>(StringComparer.Ordinal);
        foreach (var image in set.Images.Where(image => image.ImageId != "IMG-04"))
        {
            foreach (var read in ParseReads(image.ReadText))
            {
                var id = read.GetProperty("carton_id").GetString()!;
                byCarton.TryAdd(id, []);
                byCarton[id].Add(new CartonRead(
                    id, read.GetProperty("part").GetString()!,
                    read.GetProperty("po_number").GetString(), read.GetProperty("lot_code").GetString(),
                    read.GetProperty("units_per_carton").GetInt32(),
                    read.GetProperty("mark_present").GetBoolean(), read.GetProperty("mark_legible").GetBoolean(),
                    read.GetProperty("un_number").GetString(), true,
                    read.GetProperty("class9_visible").GetBoolean(),
                    read.GetProperty("damage_visible").GetBoolean(), read.GetProperty("damage_note").GetString(),
                    image.ImageId, (decimal)read.GetProperty("read_confidence").GetDouble()));
            }
        }

        var pipeLots = byCarton.Keys.ToDictionary(id => id, _ => lot, StringComparer.Ordinal);
        var carton07 = evaluator.EvaluateCarton("CTN-07", byCarton["CTN-07"], po.PoNumber, pipeLots);
        var mark07 = carton07.Single(v => v.RuleId == "BAT-LI-03");
        Assert.Equal(VerdictKind.Hold, mark07.Kind);
        Assert.Equal(VerdictKind.Pass, carton07.Single(v => v.RuleId == "BAT-LI-06").Kind);
        var damage04 = evaluator.EvaluateCarton("CTN-04", byCarton["CTN-04"], po.PoNumber, pipeLots)
            .Single(v => v.RuleId == "BAT-LI-05");
        Assert.Equal(VerdictKind.HumanReview, damage04.Kind);
        var missing11 = evaluator.EvaluateCarton("CTN-11", [], po.PoNumber, pipeLots);
        Assert.Equal(VerdictKind.HumanReview, missing11.Single(v => v.RuleId == "BAT-LI-03").Kind);
    }

    [Fact]
    public async Task VerificationMismatchFallsBackToComposite()
    {
        var (_, po, asn, lot) = await MintAsnAsync("ASN-TEST-2");
        var models = new FakeModelClient();
        var views = SpecViews();
        foreach (var view in views)
        {
            models.QueueResponse(MatchingReadJson(view, "PO-WRONG", lot));
            models.QueueResponse(MatchingReadJson(view, po.PoNumber, lot));
        }

        var labeler = new FakeImageLabeler(baseJpeg => baseJpeg);
        var pipeline = new DockImagePipeline(labeler, models);
        var set = await pipeline.BuildAsync(asn, po.PoNumber, Options("fallback"));

        Assert.All(set.Images, image => Assert.Equal(DockImagePath.Composited, image.Path));
        Assert.All(set.Images, image => Assert.Contains("mismatched", image.FallbackReason ?? string.Empty));
        Assert.All(set.Images, image => Assert.Equal(1, image.LabelAttempts));
        Assert.Equal(4, labeler.CallCount);
        Assert.Equal(8, models.CallCount);
        foreach (var image in set.Images)
        {
            foreach (var read in ParseReads(image.ReadText))
            {
                Assert.Equal(po.PoNumber, read.GetProperty("po_number").GetString());
            }
        }
    }

    [Fact]
    public async Task SamePoReplaysFromCachesWithZeroNewCalls()
    {
        var (_, po, asn, lot) = await MintAsnAsync("ASN-TEST-3");
        var options = Options("replay");
        var models = new FakeModelClient();
        foreach (var view in SpecViews())
        {
            models.QueueResponse(MatchingReadJson(view, po.PoNumber, lot));
        }

        var labeler = new FakeImageLabeler(baseJpeg => baseJpeg);
        var pipeline = new DockImagePipeline(labeler, models);
        var first = await pipeline.BuildAsync(asn, po.PoNumber, options);
        Assert.Equal(4, labeler.CallCount);
        Assert.Equal(4, models.CallCount);

        var second = await pipeline.BuildAsync(asn, po.PoNumber, options);
        Assert.Equal(4, labeler.CallCount);
        Assert.Equal(4, models.CallCount);
        Assert.All(second.Images, image => Assert.True(image.ReadFromCache));
        Assert.Equal(
            first.Images.Select(image => image.ImageSha256),
            second.Images.Select(image => image.ImageSha256));
    }

    [Fact]
    public async Task DifferentPoRebuildsWithNewCalls()
    {
        var options = Options("repo");
        var models = new FakeModelClient();
        var labeler = new FakeImageLabeler(baseJpeg => baseJpeg);
        var pipeline = new DockImagePipeline(labeler, models);

        async Task<DockImageSet> BuildForAsnAsync(string asnNumber, bool decoyFirst)
        {
            // Fresh ERP per ASN: carton ids are unique across ASNs by control.
            var erp = SeededErp.Create();
            if (decoyFirst)
            {
                var screwLine = new PurchaseRequisitionLine(SeededErp.Screw, 150, "EA");
                var screwPr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
                    [screwLine], Guid.NewGuid().ToString("N"));
                if (screwPr.Status == PrStatus.Draft)
                {
                    await erp.SubmitForApprovalAsync("PR", screwPr.PrNumber, "approver", "planner", Guid.NewGuid().ToString("N"));
                    await erp.RecordApprovalAsync("PR", screwPr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
                }

                await erp.CreatePoAsync(screwPr.PrNumber, "V-C", RecordSource.P2pAgent, "planner", Guid.NewGuid().ToString("N"));
            }

            var batteryLine = new PurchaseRequisitionLine(SeededErp.Battery, 90, "EA");
            var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
                [batteryLine], Guid.NewGuid().ToString("N"));
            await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Guid.NewGuid().ToString("N"));
            await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
            var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Guid.NewGuid().ToString("N"));
            var lot = $"LOT-{po.PoNumber}-A";
            var asn = await erp.CreateAsnAsync(asnNumber, po.PoNumber, RecordSource.Vendor, "V-B",
                CartonRows().Keys.Select(id => new AsnCarton(id, SeededErp.Battery, 100, lot)).ToList(),
                Guid.NewGuid().ToString("N"));
            foreach (var view in SpecViews())
            {
                models.QueueResponse(MatchingReadJson(view, po.PoNumber, lot));
            }

            return await pipeline.BuildAsync(asn, po.PoNumber, options);
        }

        var first = await BuildForAsnAsync("ASN-TEST-4A", decoyFirst: false);
        var second = await BuildForAsnAsync("ASN-TEST-4B", decoyFirst: true);
        Assert.NotEqual(first.PoNumber, second.PoNumber);
        Assert.Equal(8, labeler.CallCount);
        Assert.Equal(8, models.CallCount);
        Assert.All(second.Images, image => Assert.False(image.ReadFromCache));
        foreach (var image in second.Images)
        {
            foreach (var read in ParseReads(image.ReadText))
            {
                Assert.Equal(second.PoNumber, read.GetProperty("po_number").GetString());
            }
        }
    }

    [Fact]
    public void VerifyAcceptsExactMatch()
    {
        var labels = new List<DockImagePipeline.ResolvedLabel>
        {
            new("CTN-01", "BAT-1", 100, "LOT-PO-1-A", true, 0, 0, 0.1, 0.1, "PO-1")
        };
        var view = new ViewSpec("IMG-T", "base.jpg", [], null, new HashSet<string>());
        var json = """[{"carton_id":"CTN-01","part":"BAT-1","po_number":"PO-1","lot_code":"LOT-PO-1-A","units_per_carton":100,"mark_present":true,"mark_legible":true,"un_number":"UN3480","class9_visible":true,"damage_visible":false,"damage_note":null,"read_confidence":0.99}]""";
        Assert.True(DockImagePipeline.VerifyAgainstManifest(json, labels, view));
    }

    [Fact]
    public async Task TruncatedReadRetriesOnceThenRefusesToCache()
    {
        var (_, po, asn, lot) = await MintAsnAsync("ASN-TEST-7");
        var models = new FakeModelClient();
        var views = SpecViews();
        foreach (var view in views)
        {
            models.QueueResponse(MatchingReadJson(view, "PO-WRONG", lot));
            models.QueueResponse("[{\"carton_id\":\"CTN-01\"");
            models.QueueResponse(MatchingReadJson(view, po.PoNumber, lot));
        }

        var labeler = new FakeImageLabeler(baseJpeg => baseJpeg);
        var set = await new DockImagePipeline(labeler, models).BuildAsync(asn, po.PoNumber, Options("retry"));
        Assert.All(set.Images, image => Assert.Equal(DockImagePath.Composited, image.Path));
        Assert.Equal(12, models.CallCount);
    }

    [Fact]
    public async Task TwiceTruncatedReadThrowsInsteadOfCaching()
    {
        var (_, po, asn, lot) = await MintAsnAsync("ASN-TEST-8");
        var models = new FakeModelClient();
        foreach (var view in SpecViews())
        {
            models.QueueResponse(MatchingReadJson(view, "PO-WRONG", lot));
            models.QueueResponse("[{\"carton_id\":\"CTN-01\"");
            models.QueueResponse("[{\"carton_id\":\"CTN-02\"");
        }

        var labeler = new FakeImageLabeler(baseJpeg => baseJpeg);
        var boom = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DockImagePipeline(labeler, models).BuildAsync(asn, po.PoNumber, Options("garbage")));
        Assert.Contains("refusing to cache", boom.Message);
    }

    [Fact]
    public async Task ThrowingLabelerFallsBackWithReason()
    {
        var (_, po, asn, lot) = await MintAsnAsync("ASN-TEST-9");
        var models = new FakeModelClient();
        foreach (var view in SpecViews())
        {
            models.QueueResponse(MatchingReadJson(view, po.PoNumber, lot));
        }

        var labeler = new FakeImageLabeler(_ => throw new HttpRequestException("edits endpoint down"));
        var set = await new DockImagePipeline(labeler, models).BuildAsync(asn, po.PoNumber, Options("throw"));
        Assert.All(set.Images, image => Assert.Equal(DockImagePath.Composited, image.Path));
        Assert.All(set.Images, image => Assert.Contains("failed", image.FallbackReason ?? string.Empty));
        Assert.Equal(4, labeler.CallCount);
        Assert.Equal(4, models.CallCount);
    }

    [Fact]
    public async Task CorruptBaseThrowsMaskBuild()
    {
        var (_, po, asn, _) = await MintAsnAsync("ASN-TEST-10");
        var tag = Guid.NewGuid().ToString("N");
        var basesDir = Path.Combine(Path.GetTempPath(), "gate-dock-badbases", tag);
        Directory.CreateDirectory(basesDir);
        await File.WriteAllBytesAsync(Path.Combine(basesDir, "BROKEN-base.jpg"), "not-an-image"u8.ToArray());
        var specsFile = Path.Combine(Path.GetTempPath(), "gate-dock-badspecs", tag, "image-specs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(specsFile)!);
        await File.WriteAllTextAsync(specsFile,
            """{"images":[{"id":"IMG-T","kind":"pallet","source":"BROKEN-base.jpg","clearlyVisible":["CTN-01"],"damageVisible":[],"labels":[{"carton":"CTN-01","x":0,"y":0,"w":1,"h":1}]}]}""");
        var options = Options("badbase") with
        {
            BasesDir = basesDir,
            ImageSpecsPath = specsFile
        };
        var pipeline = new DockImagePipeline(
            new FakeImageLabeler(baseJpeg => baseJpeg), new FakeModelClient());
        var boom = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.BuildAsync(asn, po.PoNumber, options));
        Assert.Contains("mask build failed", boom.Message);
    }

    [Fact]
    public async Task AsnPoMismatchThrows()
    {
        var (_, _, asn, _) = await MintAsnAsync("ASN-TEST-5");
        var pipeline = new DockImagePipeline(
            new FakeImageLabeler(baseJpeg => baseJpeg), new FakeModelClient());
        var boom = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.BuildAsync(asn, "PO-NOT-THE-ASN-PO", Options("mismatch")));
        Assert.Contains("not PO-NOT-THE-ASN-PO", boom.Message);
    }

    [Fact]
    public async Task UnknownAsnCartonThrows()
    {
        var erp = SeededErp.Create();
        var batteryLine = new PurchaseRequisitionLine(SeededErp.Battery, 90, "EA");
        var pr = await erp.CreatePrAsync("planner", RecordSource.P2pAgent, "planner",
            [batteryLine], Guid.NewGuid().ToString("N"));
        await erp.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "planner", Guid.NewGuid().ToString("N"));
        await erp.RecordApprovalAsync("PR", pr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        var po = await erp.CreatePoAsync(pr.PrNumber, "V-B", RecordSource.P2pAgent, "planner", Guid.NewGuid().ToString("N"));
        var asn = await erp.CreateAsnAsync("ASN-TEST-6", po.PoNumber, RecordSource.Vendor, "V-B",
            [new AsnCarton("CTN-UNKNOWN", SeededErp.Battery, 100, $"LOT-{po.PoNumber}-A")],
            Guid.NewGuid().ToString("N"));
        var pipeline = new DockImagePipeline(
            new FakeImageLabeler(baseJpeg => baseJpeg), new FakeModelClient());
        var boom = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.BuildAsync(asn, po.PoNumber, Options("unknown")));
        Assert.Contains("which is not on ASN", boom.Message);
    }
}

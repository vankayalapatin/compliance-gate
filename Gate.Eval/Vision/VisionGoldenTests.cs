// Golden dock set (Live, once): mint a real battery PO through the ERP,
// create its ASN, and run the ASN-time pipeline with the live labeler and
// Spark. Labels and reads must carry the minted PO (never an assumption),
// and the dock check must pass the cartons. Skips when the golden set is
// complete; the offline accuracy test re-verifies it on every suite run.
using System.Text.Json;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class VisionGoldenTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task BuildGoldenSetForMintedPo()
    {
        var root = VisionFiles.RepoRoot();
        var dock = Path.Combine(root, "assets", "dock");
        var recordFile = Path.Combine(dock, "build-record.json");
        if (File.Exists(recordFile)
            && File.Exists(Path.Combine(root, "assets", "accuracy.csv"))
            && Directory.GetFiles(Path.Combine(root, "assets", "read-cache"), "*.carton-read-v1.json").Length >= 4)
        {
            return;
        }

        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        var model = config["MetaApi:Model"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "MetaApi:ApiKey is missing.");
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing.");
        var baseUrl = config["MetaApi:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://api.meta.ai/v1";
        }

        var imageModel = config["MetaApi:ImageModel"];
        if (string.IsNullOrWhiteSpace(imageModel))
        {
            imageModel = "muse-image-1.0";
        }

        // Mint the battery PO under a non-first number: a decoy screw PO goes
        // first so the pipeline proves it follows the ERP, not a sequence slot.
        var erp = SeededErp.Create();
        var agent = new P2pAgent(erp, new FakeModelClient(), new ComplianceEngine());
        var shortfall = await agent.ComputeShortfallAsync("SO-1001");
        var screws = shortfall.Where(s => s.ItemId == SeededErp.Screw).ToList();
        var cells = shortfall.Where(s => s.ItemId == SeededErp.Battery).ToList();
        var screwPr = await agent.RaisePrAsync("planner", "approver", RecordSource.P2pAgent, screws);
        if (screwPr.Status == PrStatus.Draft)
        {
            await erp.SubmitForApprovalAsync("PR", screwPr.PrNumber, "approver", "planner", Guid.NewGuid().ToString("N"));
            await erp.RecordApprovalAsync("PR", screwPr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        }

        await agent.ScreenAndOrderAsync(screwPr.PrNumber, ["V-C"], RecordSource.P2pAgent, "case-golden-screw");
        var batteryPr = await agent.RaisePrAsync("planner", "approver", RecordSource.P2pAgent, cells);
        await erp.SubmitForApprovalAsync("PR", batteryPr.PrNumber, "approver", "planner", Guid.NewGuid().ToString("N"));
        await erp.RecordApprovalAsync("PR", batteryPr.PrNumber, "approver", true, "ok", Guid.NewGuid().ToString("N"));
        var (po, _) = await agent.ScreenAndOrderAsync(
            batteryPr.PrNumber, ["V-A", "V-B"], RecordSource.P2pAgent, "case-golden-battery");
        Assert.Equal("V-B", po.VendorId);
        var poNumber = po.PoNumber;
        var lot = $"LOT-{poNumber}-A";

        using var cartons = VisionFiles.LoadJson(VisionFiles.CartonsPath());
        var cartonIds = cartons.RootElement.GetProperty("cartons").EnumerateArray()
            .Select(row => row.GetProperty("id").GetString()!).ToList();
        var asn = await erp.CreateAsnAsync($"ASN-{poNumber}", poNumber, RecordSource.Vendor, "V-B",
            cartonIds.Select(id => new AsnCarton(id, SeededErp.Battery, 100, lot)).ToList(),
            Guid.NewGuid().ToString("N"));

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var labeler = new MetaImageLabeler(http, new MetaImageOptions(baseUrl!, imageModel!, apiKey!));
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = baseUrl!,
            Model = model!,
            ApiKey = apiKey!
        });
        var options = new DockImageOptions(
            dock, VisionFiles.ImageSpecsPath(), VisionFiles.CartonsPath(), dock,
            Path.Combine(Path.GetTempPath(), "gate-dock-imgcache", Guid.NewGuid().ToString("N")),
            Path.Combine(root, "assets", "read-cache"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var set = await new DockImagePipeline(labeler, spark).BuildAsync(asn, poNumber, options, cts.Token);

        Assert.Equal(poNumber, set.PoNumber);
        Assert.Equal(4, set.Images.Count);
        var readsByImage = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var image in set.Images)
        {
            var reads = DockImagePipeline.ParseReads(image.ReadText);
            Assert.NotEmpty(reads);
            readsByImage[image.ImageId] = reads;
            foreach (var read in reads)
            {
                Assert.Equal(poNumber, read.GetProperty("po_number").GetString());
                Assert.Equal(lot, read.GetProperty("lot_code").GetString());
            }
        }

        // Dock check over the live reads.
        var evaluator = new DockEvaluator();
        var byCarton = new Dictionary<string, List<CartonRead>>(StringComparer.Ordinal);
        foreach (var image in set.Images.Where(image => image.ImageId != "IMG-04"))
        {
            foreach (var read in readsByImage[image.ImageId])
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

        Assert.Equal(VerdictKind.Hold,
            evaluator.EvaluateCarton("CTN-07", byCarton["CTN-07"]).Single(v => v.RuleId == "BAT-LI-03").Kind);
        Assert.Equal(VerdictKind.HumanReview,
            evaluator.EvaluateCarton("CTN-04", byCarton["CTN-04"]).Single(v => v.RuleId == "BAT-LI-05").Kind);
        Assert.False(byCarton.ContainsKey("CTN-11"));
        Assert.Equal(VerdictKind.HumanReview,
            evaluator.EvaluateCarton("CTN-11", []).Single(v => v.RuleId == "BAT-LI-03").Kind);
        var flip = readsByImage["IMG-04"];
        Assert.Single(flip);
        Assert.True(flip[0].GetProperty("mark_present").GetBoolean());

        var record = new
        {
            poNumber,
            lots = cartonIds.ToDictionary(id => id, _ => lot),
            generatedAtUtc = DateTimeOffset.UtcNow,
            labeler = labeler.LabelerName,
            reader = model,
            images = set.Images.Select(image => new
            {
                id = image.ImageId,
                sha256 = image.ImageSha256,
                path = image.Path.ToString(),
                labelAttempts = image.LabelAttempts,
                readFromCache = image.ReadFromCache,
                fallbackReason = image.FallbackReason
            }).ToList()
        };
        await File.WriteAllTextAsync(recordFile,
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), cts.Token);
    }
}

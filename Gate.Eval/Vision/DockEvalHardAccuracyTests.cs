// Offline per-carton scoring for the hard tier: each photo is read once,
// every carton on it is scored against the oracle in code, and catches are
// reported by problem type over cartons (not photos). No live calls. Writes
// the per-carton CSV plus the markdown report; a second model later scores
// its own cache dir with the same function.
using System.Text;
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class DockEvalHardAccuracyTests
{
    [Fact]
    public void ScoreSparkReadsPerCarton()
    {
        using var key = VisionFiles.LoadJson(DockEvalHardFiles.AnswerKeyPath());
        using var manifest = VisionFiles.LoadJson(DockEvalHardFiles.ManifestPath());
        var expectedPo = key.RootElement.GetProperty("expected_po").GetString()!;
        var expectedPart = key.RootElement.GetProperty("expected_part").GetString()!;
        var expectedLot = key.RootElement.GetProperty("expected_lot").GetString()!;
        var promptVersion = key.RootElement.GetProperty("prompt_version").GetString()!;
        var photos = key.RootElement.GetProperty("photos").EnumerateArray().ToList();
        var shaByImage = manifest.RootElement.GetProperty("images").EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("image").GetString()!,
                e => e.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal);
        var cache = new VisionReadCache(DockEvalHardFiles.SparkCacheDir());

        var csv = new StringBuilder();
        csv.AppendLine("image,carton,slot,problem,caught,detail");
        var byType = new Dictionary<string, (int Caught, int Total)>(StringComparer.Ordinal);
        var cartons = 0;
        foreach (var photo in photos)
        {
            var image = photo.GetProperty("image").GetString()!;
            Assert.True(shaByImage.TryGetValue(image, out var sha), $"Manifest lacks {image}.");
            var entry = cache.TryRead(sha, promptVersion);
            Assert.NotNull(entry);
            var reads = DockEval50Scoring.ParseReads(entry!.Text);
            foreach (var carton in photo.GetProperty("cartons").EnumerateArray())
            {
                var id = carton.GetProperty("carton").GetString()!;
                var slot = carton.GetProperty("slot").GetString()!;
                var problem = carton.GetProperty("problem").GetString()!;
                var (caught, detail) = DockEval50Scoring.ScoreOne(carton, expectedPo, expectedPart, expectedLot, reads);
                csv.AppendLine(string.Join(",",
                    image, id, slot, problem, caught ? "yes" : "no",
                    "\"" + detail.Replace("\"", "\"\"") + "\""));
                var slot2 = byType.TryGetValue(problem, out var prior) ? prior : (Caught: 0, Total: 0);
                byType[problem] = (slot2.Caught + (caught ? 1 : 0), slot2.Total + 1);
                cartons++;
            }
        }

        File.WriteAllText(DockEvalHardFiles.AccuracyCsvPath(), csv.ToString());

        var report = new StringBuilder();
        report.AppendLine("# Dock eval hard: Spark report (per carton)");
        report.AppendLine();
        report.AppendLine($"Scored {cartons} cartons on {photos.Count} photos against the oracle (Spark never saw it).");
        report.AppendLine();
        report.AppendLine("| problem | caught | total | rate |");
        report.AppendLine("|---|---|---|---|");
        var caughtAll = 0;
        foreach (var (problem, (caught, total)) in byType.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            caughtAll += caught;
            report.AppendLine($"| {problem} | {caught} | {total} | {(total == 0 ? 0 : caught * 100.0 / total):0}% |");
        }

        report.AppendLine();
        report.AppendLine($"Overall: {caughtAll}/{cartons} ({(cartons == 0 ? 0 : caughtAll * 100.0 / cartons):0}%).");
        report.AppendLine();
        report.AppendLine("Per-carton rows: accuracy-by-carton.csv. Second-model handoff: read-schema.json.");
        File.WriteAllText(DockEvalHardFiles.ReportPath(), report.ToString());

        Assert.Equal(cartons, byType.Values.Sum(v => v.Total));
    }
}

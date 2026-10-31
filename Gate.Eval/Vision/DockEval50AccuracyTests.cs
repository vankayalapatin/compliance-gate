// Offline scoring for the 50-photo eval: compares cached Spark reads
// against the oracle in code and reports catches by problem type. No live
// calls. Writes the per-image CSV plus the markdown report; a second model
// later scores its own cache dir with the same function.
using System.Text;
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class DockEval50AccuracyTests
{
    [Fact]
    public void ScoreSparkReadsByProblemType()
    {
        using var key = VisionFiles.LoadJson(DockEval50Files.AnswerKeyPath());
        using var manifest = VisionFiles.LoadJson(DockEval50Files.ManifestPath());
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
        var cache = new VisionReadCache(DockEval50Files.SparkCacheDir());

        var csv = new StringBuilder();
        csv.AppendLine("image,carton,problem,caught,detail");
        var byType = new Dictionary<string, (int Caught, int Total)>(StringComparer.Ordinal);
        foreach (var photo in photos)
        {
            var image = photo.GetProperty("image").GetString()!;
            var carton = photo.GetProperty("carton").GetString()!;
            var problem = photo.GetProperty("problem").GetString()!;
            Assert.True(shaByImage.TryGetValue(image, out var sha), $"Manifest lacks {image}.");
            var entry = cache.TryRead(sha, promptVersion);
            Assert.NotNull(entry);
            var reads = DockEval50Scoring.ParseReads(entry!.Text);
            var (caught, detail) = DockEval50Scoring.ScoreOne(photo, expectedPo, expectedPart, expectedLot, reads);
            csv.AppendLine(string.Join(",",
                image, carton, problem, caught ? "yes" : "no",
                "\"" + detail.Replace("\"", "\"\"") + "\""));
            var slot = byType.TryGetValue(problem, out var prior) ? prior : (Caught: 0, Total: 0);
            byType[problem] = (slot.Caught + (caught ? 1 : 0), slot.Total + 1);
        }

        File.WriteAllText(DockEval50Files.AccuracyCsvPath(), csv.ToString());

        var report = new StringBuilder();
        report.AppendLine("# Dock eval 50: Spark report");
        report.AppendLine();
        report.AppendLine($"Scored {photos.Count} photos against the oracle (Spark never saw it).");
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
        report.AppendLine($"Overall: {caughtAll}/{photos.Count} ({(photos.Count == 0 ? 0 : caughtAll * 100.0 / photos.Count):0}%).");
        report.AppendLine();
        report.AppendLine("Per-image rows: accuracy-by-type.csv. Second-model handoff: read-schema.json.");
        File.WriteAllText(DockEval50Files.ReportPath(), report.ToString());

        Assert.Equal(photos.Count, byType.Values.Sum(v => v.Total));
    }
}

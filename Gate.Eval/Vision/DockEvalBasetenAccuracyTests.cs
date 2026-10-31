// Offline three-way scoring for the model comparison: the committed
// Spark originals, the fresh Spark re-run (separate cache, originals never
// overwritten), and the Baseten reads are all scored against the same
// oracles in code. No live calls. Writes per-model CSVs plus one
// side-by-side report with accuracy by problem type, time, tokens, and
// cost per photo when rates are configured.
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gate.Contracts;
using Microsoft.Extensions.Configuration;

namespace Gate.Eval;

public sealed class DockEvalBasetenAccuracyTests
{
    private sealed record RunScore(
        string Name, Dictionary<string, (int Caught, int Total)> ByType, int Units, List<string> CsvRows);

    private sealed record RunTimings(string Model, Dictionary<string, (long Ms, int? In, int? Out)> Reads);

    [Fact]
    public void ScoreBothTiersThreeWay()
    {
        var easy = ScoreTier(
            "easy", DockEval50Files.EvalDir(), DockEval50Files.AnswerKeyPath(), DockEval50Files.ManifestPath(),
            perCarton: false,
            ("spark-orig", "read-cache-spark"), ("spark-rerun", "read-cache-spark-rerun"), ("baseten", "read-cache-baseten"));
        var hard = ScoreTier(
            "hard", DockEvalHardFiles.EvalDir(), DockEvalHardFiles.AnswerKeyPath(), DockEvalHardFiles.ManifestPath(),
            perCarton: true,
            ("spark-orig", "read-cache-spark"), ("spark-rerun", "read-cache-spark-rerun"), ("baseten", "read-cache-baseten"));

        WriteCsv(Path.Combine(DockEval50Files.EvalDir(), "accuracy-by-type-baseten.csv"),
            "image,carton,problem,caught,detail", easy["baseten"].CsvRows);
        WriteCsv(Path.Combine(DockEval50Files.EvalDir(), "accuracy-by-type-spark-rerun.csv"),
            "image,carton,problem,caught,detail", easy["spark-rerun"].CsvRows);
        WriteCsv(Path.Combine(DockEvalHardFiles.EvalDir(), "accuracy-by-carton-baseten.csv"),
            "image,carton,slot,problem,caught,detail", hard["baseten"].CsvRows);
        WriteCsv(Path.Combine(DockEvalHardFiles.EvalDir(), "accuracy-by-carton-spark-rerun.csv"),
            "image,carton,slot,problem,caught,detail", hard["spark-rerun"].CsvRows);

        var easySparkTime = LoadTimings(Path.Combine(DockEval50Files.EvalDir(), "timings-spark-rerun.json"), "easy Spark re-run");
        var easyBaseTime = LoadTimings(Path.Combine(DockEval50Files.EvalDir(), "timings-baseten.json"), "easy Baseten");
        var hardSparkTime = LoadTimings(Path.Combine(DockEvalHardFiles.EvalDir(), "timings-spark-rerun.json"), "hard Spark re-run");
        var hardBaseTime = LoadTimings(Path.Combine(DockEvalHardFiles.EvalDir(), "timings-baseten.json"), "hard Baseten");

        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var report = new StringBuilder();
        report.AppendLine("# Spark vs Baseten: dock eval comparison");
        report.AppendLine();
        report.AppendLine("Same photos, same prompt, same scorer. Spark originals are the committed");
        report.AppendLine("caches; spark-rerun is a fresh timed re-read in a separate cache folder;");
        report.AppendLine("Baseten is eta-models/muse-spark-open-1026 via inference.baseten.co.");
        report.AppendLine();
        AppendTier(report, "Easy tier (50 photos, per photo)", easy);
        AppendTier(report, "Hard tier (30 photos, 170 cartons, per carton)", hard);
        AppendTiming(report, "Easy", easySparkTime, easyBaseTime, config);
        AppendTiming(report, "Hard", hardSparkTime, hardBaseTime, config);

        var dir = Path.Combine(VisionFiles.RepoRoot(), "assets", "dock-eval-compare");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spark-vs-baseten.md"), report.ToString());

        foreach (var tier in new[] { easy, hard })
        {
            foreach (var run in tier.Values)
            {
                Assert.Equal(run.Units, run.ByType.Values.Sum(v => v.Total));
            }
        }
    }

    private static Dictionary<string, RunScore> ScoreTier(
        string tier, string evalDir, string keyFile, string manifestFile, bool perCarton,
        params (string Name, string CacheDir)[] runs)
    {
        using var key = VisionFiles.LoadJson(keyFile);
        using var manifest = VisionFiles.LoadJson(manifestFile);
        var expectedPo = key.RootElement.GetProperty("expected_po").GetString()!;
        var expectedPart = key.RootElement.GetProperty("expected_part").GetString()!;
        var expectedLot = key.RootElement.GetProperty("expected_lot").GetString()!;
        var promptVersion = key.RootElement.GetProperty("prompt_version").GetString()!;
        var photos = key.RootElement.GetProperty("photos").EnumerateArray().Select(p => p.Clone()).ToList();
        var shaByImage = manifest.RootElement.GetProperty("images").EnumerateArray()
            .ToDictionary(
                e => e.GetProperty("image").GetString()!,
                e => e.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal);

        var result = new Dictionary<string, RunScore>(StringComparer.Ordinal);
        foreach (var (name, cacheDir) in runs)
        {
            var cache = new VisionReadCache(Path.Combine(evalDir, cacheDir));
            var byType = new Dictionary<string, (int Caught, int Total)>(StringComparer.Ordinal);
            var rows = new List<string>();
            var units = 0;
            foreach (var photo in photos)
            {
                var image = photo.GetProperty("image").GetString()!;
                Assert.True(shaByImage.TryGetValue(image, out var sha), $"[{tier}/{name}] Manifest lacks {image}.");
                var entry = cache.TryRead(sha, promptVersion);
                Assert.NotNull(entry);
                var reads = DockEval50Scoring.ParseReads(entry!.Text);
                var cartons = perCarton
                    ? photo.GetProperty("cartons").EnumerateArray().Select(c => c.Clone()).ToList()
                    : new List<JsonElement> { photo };
                foreach (var unit in cartons)
                {
                    var id = unit.GetProperty("carton").GetString()!;
                    var problem = unit.GetProperty("problem").GetString()!;
                    var (caught, detail) = DockEval50Scoring.ScoreOne(unit, expectedPo, expectedPart, expectedLot, reads);
                    var slot = perCarton ? unit.GetProperty("slot").GetString()! + "," : string.Empty;
                    rows.Add(string.Join(",", image, id, slot + problem, caught ? "yes" : "no",
                        "\"" + detail.Replace("\"", "\"\"") + "\""));
                    var slot2 = byType.TryGetValue(problem, out var prior) ? prior : (Caught: 0, Total: 0);
                    byType[problem] = (slot2.Caught + (caught ? 1 : 0), slot2.Total + 1);
                    units++;
                }
            }

            result[name] = new RunScore(name, byType, units, rows);
        }

        return result;
    }

    private static void WriteCsv(string path, string header, List<string> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine(header);
        foreach (var row in rows)
        {
            csv.AppendLine(row);
        }

        File.WriteAllText(path, csv.ToString());
    }

    private static RunTimings LoadTimings(string path, string what)
    {
        Assert.True(File.Exists(path), $"Timings missing for {what}: {path}. Run the live reads first.");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var reads = new Dictionary<string, (long Ms, int? In, int? Out)>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.GetProperty("reads").EnumerateObject())
        {
            reads[prop.Name] = (
                prop.Value.GetProperty("ms").GetInt64(),
                prop.Value.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number ? pt.GetInt32() : null,
                prop.Value.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : null);
        }

        return new RunTimings(doc.RootElement.GetProperty("model").GetString()!, reads);
    }

    private static void AppendTier(StringBuilder report, string title, Dictionary<string, RunScore> tier)
    {
        report.AppendLine($"## {title}");
        report.AppendLine();
        var types = tier.Values.SelectMany(r => r.ByType.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal).ToList();
        report.AppendLine("| problem | spark-orig | spark-rerun | baseten |");
        report.AppendLine("|---|---|---|---|");
        foreach (var type in types)
        {
            string Cell(string run) => tier.TryGetValue(run, out var r) && r.ByType.TryGetValue(type, out var v)
                ? $"{v.Caught}/{v.Total}" : "n/a";
            report.AppendLine($"| {type} | {Cell("spark-orig")} | {Cell("spark-rerun")} | {Cell("baseten")} |");
        }

        string Total(string run) => tier.TryGetValue(run, out var r)
            ? $"{r.ByType.Values.Sum(v => v.Caught)}/{r.Units}" : "n/a";
        report.AppendLine($"| OVERALL | {Total("spark-orig")} | {Total("spark-rerun")} | {Total("baseten")} |");
        report.AppendLine();
        if (tier.TryGetValue("spark-orig", out var orig) && tier.TryGetValue("spark-rerun", out var rerun))
        {
            var a = orig.ByType.Values.Sum(v => v.Caught);
            var b = rerun.ByType.Values.Sum(v => v.Caught);
            report.AppendLine(a == b
                ? $"Spark rerun matches the original ({a}/{orig.Units})."
                : $"NOTE: Spark rerun differs from the original ({a} vs {b} of {orig.Units}); both reported above.");
            report.AppendLine();
        }
    }

    private static void AppendTiming(
        StringBuilder report, string tier, RunTimings spark, RunTimings baseten, IConfiguration config)
    {
        report.AppendLine($"## {tier} tier: time, tokens, cost per photo");
        report.AppendLine();
        report.AppendLine("| run | photos | avg ms/photo | avg in-tok | avg out-tok | $/photo |");
        report.AppendLine("|---|---|---|---|---|---|");
        report.AppendLine($"| spark-rerun ({spark.Model}) | {spark.Reads.Count} | {AvgMs(spark)} | {AvgTok(spark, inn: true)} | {AvgTok(spark, inn: false)} | {CostPerPhoto(spark, config, "MetaApi")} |");
        report.AppendLine($"| baseten ({baseten.Model}) | {baseten.Reads.Count} | {AvgMs(baseten)} | {AvgTok(baseten, inn: true)} | {AvgTok(baseten, inn: false)} | {CostPerPhoto(baseten, config, "Baseten")} |");
        report.AppendLine();
    }

    private static string AvgMs(RunTimings timings) =>
        timings.Reads.Count == 0 ? "n/a" : ((long)timings.Reads.Values.Average(v => v.Ms)).ToString("N0", CultureInfo.InvariantCulture);

    private static string AvgTok(RunTimings timings, bool inn)
    {
        var toks = timings.Reads.Values.Select(v => inn ? v.In : v.Out).Where(t => t.HasValue).Select(t => t!.Value).ToList();
        return toks.Count == 0 ? "n/a" : ((long)toks.Average()).ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string CostPerPhoto(RunTimings timings, IConfiguration config, string section)
    {
        if (!double.TryParse(config[section + ":PricePer1kIn"], NumberStyles.Float, CultureInfo.InvariantCulture, out var priceIn)
            || !double.TryParse(config[section + ":PricePer1kOut"], NumberStyles.Float, CultureInfo.InvariantCulture, out var priceOut))
        {
            return "rates not set";
        }

        var costs = new List<double>();
        foreach (var (_, inn, outt) in timings.Reads.Values)
        {
            if (inn.HasValue && outt.HasValue)
            {
                costs.Add(inn.Value / 1000.0 * priceIn + outt.Value / 1000.0 * priceOut);
            }
        }

        return costs.Count == 0 ? "no usage" : "$" + costs.Average().ToString("0.0000", CultureInfo.InvariantCulture);
    }
}

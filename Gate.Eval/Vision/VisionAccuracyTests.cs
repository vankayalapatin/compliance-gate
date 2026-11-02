// Per-carton accuracy scoring of cached Spark reads against ground truth.
// Offline: replays the committed golden set (finals plus reads plus build
// record) with zero live calls, verifies final hashes against the record,
// and rewrites assets/accuracy.csv before asserting the demo targets.
using System.Text;
using System.Text.Json;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class VisionAccuracyTests
{
    [Fact]
    public void CachedReadsScoreAgainstGroundTruth()
    {
        var root = VisionFiles.RepoRoot();
        var dock = Path.Combine(root, "assets", "dock");
        var cache = new VisionReadCache(Path.Combine(root, "assets", "read-cache"));
        using var specs = VisionFiles.LoadJson(VisionFiles.ImageSpecsPath());
        using var cartons = VisionFiles.LoadJson(VisionFiles.CartonsPath());
        var recordFile = Path.Combine(dock, "build-record.json");
        Assert.True(File.Exists(recordFile), "Golden build record is missing. Run the golden build once.");
        using var record = VisionFiles.LoadJson(recordFile);
        var goldenPo = record.RootElement.GetProperty("poNumber").GetString()!;
        var goldenLots = record.RootElement.GetProperty("lots").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
        var goldenImages = record.RootElement.GetProperty("images").EnumerateArray()
            .ToDictionary(element => element.GetProperty("id").GetString()!, element => element, StringComparer.Ordinal);
        var images = specs.RootElement.GetProperty("images").EnumerateArray().ToList();
        var rows = cartons.RootElement.GetProperty("cartons").EnumerateArray().ToList();

        var readsByImage = new Dictionary<string, List<JsonElement>>(StringComparer.Ordinal);
        foreach (var spec in images)
        {
            var id = spec.GetProperty("id").GetString()!;
            var file = Path.Combine(dock, id + ".jpg");
            Assert.True(File.Exists(file), $"Final view {id}.jpg is missing. Run the golden build once.");
            var sha = VisionFiles.Sha256OfFile(file);
            Assert.True(goldenImages.TryGetValue(id, out var recorded)
                && string.Equals(recorded.GetProperty("sha256").GetString(), sha, StringComparison.OrdinalIgnoreCase),
                $"Final view {id}.jpg does not match the golden build record; rebuild or restore it.");
            var entry = cache.TryRead(sha, VisionReadCache.CartonReadV1);
            Assert.NotNull(entry);
            readsByImage[id] = ParseReads(entry!.Text, id);
        }

        var palletIds = images
            .Where(spec => spec.GetProperty("kind").GetString() == "pallet")
            .Select(spec => spec.GetProperty("id").GetString()!)
            .ToList();
        var evaluator = new DockEvaluator();
        var csv = new StringBuilder();
        csv.AppendLine("carton_id,expected_mark,read_mark,mark_match,expected_verdict,actual_verdict,verdict_match,rule_expected,rule_actual,cite_match,images_seen_in,notes");
        var verdictOk = 0;
        var citeOk = 0;
        var fullyRead = 0;
        foreach (var row in rows)
        {
            var id = row.GetProperty("id").GetString()!;
            var expectedMark = row.GetProperty("markPresent").GetBoolean();
            var expectedDamage = IsDamaged(row);
            var hidden = IsHidden(row);
            var expectedVisible = new HashSet<string>(row.GetProperty("visibleIn").EnumerateArray()
                .Select(element => element.GetString()!), StringComparer.Ordinal);

            var found = new List<(string Image, JsonElement Read)>();
            foreach (var imageId in palletIds)
            {
                foreach (var read in readsByImage[imageId])
                {
                    if (string.Equals(read.GetProperty("carton_id").GetString(), id, StringComparison.Ordinal))
                    {
                        found.Add((imageId, read));
                    }
                }
            }

            var seenIn = found.Select(pair => pair.Image).Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList();
            string readMark;
            bool markMatch;
            if (hidden)
            {
                readMark = found.Count == 0 ? "unread" : "over-read";
                markMatch = found.Count == 0;
            }
            else
            {
                var marks = found.Select(pair => pair.Read.GetProperty("mark_present").GetBoolean()).ToList();
                readMark = marks.Count == 0 ? "unread" : marks.All(m => m) ? "true" : marks.All(m => !m) ? "false" : "conflict";
                markMatch = new HashSet<string>(seenIn, StringComparer.Ordinal).SetEquals(expectedVisible)
                    && string.Equals(readMark, expectedMark ? "true" : "false", StringComparison.Ordinal);
            }

            var cartonReads = found.Select(pair => ToCartonRead(id, pair.Image, pair.Read)).ToList();
            var verdicts = evaluator.EvaluateCarton(id, cartonReads, goldenPo, goldenLots);
            var expected = ExpectedKinds(expectedMark, expectedDamage, hidden);
            var actual = verdicts.ToDictionary(v => v.RuleId, v => v.Kind, StringComparer.Ordinal);
            var verdictMatch = expected.All(pair =>
                actual.TryGetValue(pair.Key, out var kind) && kind == pair.Value);
            var ruleExpected = LeadRule(expected);
            var ruleActual = LeadRule(actual);
            var citeMatch = string.Equals(ruleExpected, ruleActual, StringComparison.Ordinal);
            if (verdictMatch)
            {
                verdictOk++;
            }

            if (citeMatch)
            {
                citeOk++;
            }

            var fieldsOk = !hidden && new HashSet<string>(seenIn, StringComparer.Ordinal).SetEquals(expectedVisible)
                && found.All(pair => FieldsMatch(pair.Read, row, goldenPo, goldenLots[id]));
            if (fieldsOk)
            {
                fullyRead++;
            }

            var notes = hidden
                ? "hidden by design; reconciliation review"
                : string.Equals(id, "CTN-07", StringComparison.Ordinal)
                    ? "missing mark pre-reshot; IMG-04 flips to true"
                    : expectedDamage ? "damage routes to human review" : string.Empty;
            csv.AppendLine(string.Join(",",
                id, expectedMark ? "true" : "false", readMark, markMatch ? "yes" : "no",
                Describe(expected), Describe(actual), verdictMatch ? "yes" : "no",
                ruleExpected, ruleActual, citeMatch ? "yes" : "no",
                seenIn.Count == 0 ? "(none)" : string.Join("|", seenIn), notes));
        }

        var flipReads = readsByImage["IMG-04"];
        var flip = flipReads.Count == 1
            && string.Equals(flipReads[0].GetProperty("carton_id").GetString(), "CTN-07", StringComparison.Ordinal)
            && flipReads[0].GetProperty("mark_present").GetBoolean();
        csv.AppendLine($"SUMMARY,verdict {verdictOk}/12,cite {citeOk}/12,fully-read {fullyRead}/12 (CTN-11 excepted by design),IMG-04 flip {(flip ? "true" : "false")},,,,,,,");
        File.WriteAllText(Path.Combine(root, "assets", "accuracy.csv"), csv.ToString());

        Assert.Equal(12, verdictOk);
        Assert.Equal(12, citeOk);
        Assert.Equal(11, fullyRead);
        Assert.True(flip);
    }

    private static List<JsonElement> ParseReads(string text, string imageId)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = trimmed.Split('\n').ToList();
            lines.RemoveAt(0);
            if (lines.Count > 0 && lines[^1].Trim().StartsWith("```", StringComparison.Ordinal))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            trimmed = string.Join('\n', lines);
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
        }
        catch (JsonException ex)
        {
            Assert.Fail($"Cached read for {imageId} is not a JSON array: {ex.Message}. Text: {(trimmed.Length <= 300 ? trimmed : trimmed[..300] + "...")}");
            return [];
        }
    }

    private static CartonRead ToCartonRead(string cartonId, string imageId, JsonElement read) =>
        new(
            cartonId,
            read.GetProperty("part").GetString()!,
            read.TryGetProperty("po_number", out var po) ? po.GetString() : null,
            read.TryGetProperty("lot_code", out var lot) ? lot.GetString() : null,
            read.TryGetProperty("units_per_carton", out var units) && units.ValueKind == JsonValueKind.Number ? units.GetInt32() : 0,
            read.GetProperty("mark_present").GetBoolean(),
            read.TryGetProperty("mark_legible", out var legible) && legible.ValueKind == JsonValueKind.True,
            read.TryGetProperty("un_number", out var un) ? un.GetString() : null,
            Class9Required: true,
            read.TryGetProperty("class9_visible", out var class9) && class9.ValueKind == JsonValueKind.True,
            read.GetProperty("damage_visible").GetBoolean(),
            read.TryGetProperty("damage_note", out var note) ? note.GetString() : null,
            imageId,
            read.TryGetProperty("read_confidence", out var confidence) && confidence.ValueKind == JsonValueKind.Number
                ? (decimal)confidence.GetDouble() : 0m);

    private static Dictionary<string, VerdictKind> ExpectedKinds(bool expectedMark, bool expectedDamage, bool hidden)
    {
        var mark = hidden ? VerdictKind.HumanReview
            : expectedMark ? VerdictKind.Pass : VerdictKind.Hold;
        return new Dictionary<string, VerdictKind>(StringComparer.Ordinal)
        {
            ["BAT-LI-03"] = mark,
            ["BAT-LI-04"] = VerdictKind.Pass,
            ["BAT-LI-05"] = expectedDamage ? VerdictKind.HumanReview : VerdictKind.Pass
        };
    }

    private static string LeadRule(IReadOnlyDictionary<string, VerdictKind> kinds)
    {
        foreach (var rule in new[] { "BAT-LI-03", "BAT-LI-04", "BAT-LI-05" })
        {
            if (kinds.TryGetValue(rule, out var kind) && kind is VerdictKind.Block or VerdictKind.Hold)
            {
                return rule;
            }
        }

        foreach (var rule in new[] { "BAT-LI-03", "BAT-LI-04", "BAT-LI-05" })
        {
            if (kinds.TryGetValue(rule, out var kind) && kind == VerdictKind.HumanReview)
            {
                return rule;
            }
        }

        return "BAT-LI-03";
    }

    private static string Describe(IReadOnlyDictionary<string, VerdictKind> kinds) =>
        string.Join("|", new[] { "BAT-LI-03", "BAT-LI-04", "BAT-LI-05" }
            .Select(rule => $"{rule}:{(kinds.TryGetValue(rule, out var kind) ? kind : VerdictKind.Pass)}"));

    private static bool FieldsMatch(JsonElement read, JsonElement row, string expectedPo, string expectedLot) =>
        string.Equals(read.GetProperty("part").GetString(), row.GetProperty("part").GetString(), StringComparison.Ordinal)
        && read.TryGetProperty("po_number", out var po) && string.Equals(po.GetString(), expectedPo, StringComparison.Ordinal)
        && read.TryGetProperty("lot_code", out var lot) && string.Equals(lot.GetString(), expectedLot, StringComparison.Ordinal)
        && read.TryGetProperty("units_per_carton", out var units) && units.ValueKind == JsonValueKind.Number
        && units.GetInt32() == row.GetProperty("units").GetInt32();

    private static bool IsHidden(JsonElement row) =>
        row.GetProperty("damage").GetString()!
            .Contains("hidden", StringComparison.OrdinalIgnoreCase);

    private static bool IsDamaged(JsonElement row)
    {
        var damage = row.GetProperty("damage").GetString()!;
        return !string.Equals(damage, "none", StringComparison.OrdinalIgnoreCase) && !IsHidden(row);
    }
}

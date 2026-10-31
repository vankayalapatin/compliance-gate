// Vision-owned model-agnostic scoring for the 50-photo eval. Scores any
// cache dir (Spark today, Glimmer later) against the answer key. Logic only;
// every carton id, PO, part, and lot comes from the files.
using System.Text.Json;

namespace Gate.Eval;

internal sealed record DockEval50Score(string Image, string Carton, string Problem, bool Caught, string Detail);

internal static class DockEval50Scoring
{
    public static List<JsonElement> ParseReads(string text)
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

        using var doc = JsonDocument.Parse(trimmed);
        return doc.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
    }

    public static (bool Caught, string Detail) ScoreOne(
        JsonElement photo, string expectedPo, string expectedPart, string expectedLot,
        List<JsonElement> reads)
    {
        var carton = photo.GetProperty("carton").GetString()!;
        var problem = photo.GetProperty("problem").GetString()!;
        var expected = photo.GetProperty("expected");
        var shouldRead = expected.GetProperty("should_read").GetBoolean();

        var match = reads.FirstOrDefault(r =>
            r.TryGetProperty("carton_id", out var id) && id.ValueKind == JsonValueKind.String
            && string.Equals(id.GetString(), carton, StringComparison.Ordinal));
        var found = match.ValueKind != JsonValueKind.Undefined;

        if (!shouldRead)
        {
            return found
                ? (false, $"hidden but model read {carton} (over-read, {reads.Count} reads)")
                : (true, reads.Count == 0 ? "hidden: []" : $"hidden: {reads.Count} other reads, none for {carton}");
        }

        if (!found)
        {
            return (false, $"no read for {carton} ({reads.Count} reads)");
        }

        if (!photo.TryGetProperty("label", out var label) || label.ValueKind != JsonValueKind.Object)
        {
            return (false, "answer key has should_read=true but no label");
        }

        var labelPart = label.GetProperty("part").GetString()!;
        var labelPo = label.GetProperty("po_number").GetString()!;
        var labelLot = label.GetProperty("lot_code").GetString()!;
        var labelUnits = label.GetProperty("units").GetInt32();
        var labelUn = label.GetProperty("un_number").GetString()!;
        var labelMark = label.GetProperty("mark_present").GetBoolean();
        var expectedDamage = expected.GetProperty("damage_visible").GetBoolean();

        string? Str(string key) =>
            match.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? Bool(string key) =>
            match.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.ValueKind == JsonValueKind.True : null;
        int? Int(string key) =>
            match.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        double? Num(string key) =>
            match.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        var readPart = Str("part");
        var readPo = Str("po_number");
        var readLot = Str("lot_code");
        var readUnits = Int("units_per_carton");
        var readUn = Str("un_number");
        var readMark = Bool("mark_present");
        var readDamage = Bool("damage_visible");
        var confidence = Num("read_confidence") ?? 1.0;

        if (string.Equals(problem, "smudged_lot", StringComparison.Ordinal))
        {
            var othersOk = string.Equals(readPart, labelPart, StringComparison.Ordinal)
                && string.Equals(readPo, labelPo, StringComparison.Ordinal)
                && readUnits == labelUnits
                && string.Equals(readUn, labelUn, StringComparison.Ordinal)
                && readMark == labelMark
                && readDamage == expectedDamage;
            // Honesty is judged against this carton's own label lot: lots are
            // unique per carton, so copying a neighbour's lot still mismatches
            // downstream and only the true lot (guessed unseen) is dishonest.
            // expectedLot stays for signature stability (eval50: equals label).
            var lotHonest = readLot is null || !string.Equals(readLot, labelLot, StringComparison.Ordinal) || confidence < 0.5;
            var caught = othersOk && lotHonest;
            return (caught, caught
                ? $"smudge honest: lot={(readLot ?? "null")} conf={confidence:0.00}"
                : $"smudge miss: lot={(readLot ?? "null")} conf={confidence:0.00} othersOk={othersOk}");
        }

        var mismatches = new List<string>();
        if (!string.Equals(readPart, labelPart, StringComparison.Ordinal))
        {
            mismatches.Add($"part {readPart ?? "null"}!={labelPart}");
        }

        if (!string.Equals(readPo, labelPo, StringComparison.Ordinal))
        {
            mismatches.Add($"po {readPo ?? "null"}!={labelPo}");
        }

        if (!string.Equals(readLot, labelLot, StringComparison.Ordinal))
        {
            mismatches.Add($"lot {readLot ?? "null"}!={labelLot}");
        }

        if (readUnits != labelUnits)
        {
            mismatches.Add($"units {readUnits?.ToString() ?? "null"}!={labelUnits}");
        }

        if (!string.Equals(readUn, labelUn, StringComparison.Ordinal))
        {
            mismatches.Add($"un {readUn ?? "null"}!={labelUn}");
        }

        if (readMark != labelMark)
        {
            mismatches.Add($"mark {readMark?.ToString() ?? "null"}!={labelMark}");
        }

        if (readDamage != expectedDamage)
        {
            mismatches.Add($"damage {readDamage?.ToString() ?? "null"}!={expectedDamage}");
        }

        if (mismatches.Count > 0)
        {
            return (false, string.Join("; ", mismatches));
        }

        // Transcription matches the label pixels. For wrong-value problems the
        // catch is the mismatch against the expected shipment value.
        if (string.Equals(problem, "wrong_po", StringComparison.Ordinal))
        {
            return string.Equals(labelPo, expectedPo, StringComparison.Ordinal)
                ? (false, "answer key has no PO mismatch")
                : (true, $"wrong PO transcribed: {readPo}!=expected");
        }

        if (string.Equals(problem, "wrong_part", StringComparison.Ordinal))
        {
            return string.Equals(labelPart, expectedPart, StringComparison.Ordinal)
                ? (false, "answer key has no part mismatch")
                : (true, $"wrong part transcribed: {readPart}!=expected");
        }

        return (true, problem switch
        {
            "clean" => "clean fully transcribed",
            "missing_mark" => "missing mark read false",
            "crushed" => "damage read true",
            "subtle_dent" => "subtle dent read true",
            _ => "transcribed"
        });
    }
}

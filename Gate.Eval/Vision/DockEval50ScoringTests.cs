// Scoring unit checks with small fake data (no demo numbers): every
// problem type plus its failure path, and read parsing edges.
using System.Text.Json;

namespace Gate.Eval;

public sealed class DockEval50ScoringTests
{
    private static JsonElement Photo(string problem, bool shouldRead, bool mark, bool damage, bool withLabel = true)
    {
        var label = withLabel
            ? ""","label":{"carton_id":"CART-UNIT-1","part":"PART-U1","po_number":"PO-UNIT-1","lot_code":"LOT-UNIT-1","units":7,"un_number":"UN0000","mark_present":MARK,"class9":true,"smudge_lot":false,"x":0.25,"y":0.3,"w":0.5,"h":0.4}"""
                .Replace("MARK", mark ? "true" : "false")
            : "";
        var json = $$"""
            {"carton":"CART-UNIT-1","problem":"{{problem}}","expected":{"should_read":{{(shouldRead ? "true" : "false")}},"damage_visible":{{(damage ? "true" : "false")}}} {{label}}}
            """;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static List<JsonElement> Reads(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private static string ReadJson(
        string carton = "CART-UNIT-1", string part = "PART-U1", string po = "PO-UNIT-1",
        string? lot = "LOT-UNIT-1", int units = 7, string un = "UN0000",
        bool mark = true, bool damage = false, double conf = 0.99)
    {
        var lotJson = lot is null ? "null" : $"\"{lot}\"";
        return $$"""[{"carton_id":"{{carton}}","part":"{{part}}","po_number":"{{po}}","lot_code":{{lotJson}},"units_per_carton":{{units}},"mark_present":{{(mark ? "true" : "false")}},"mark_legible":{{(mark ? "true" : "false")}},"un_number":"{{un}}","class9_visible":true,"damage_visible":{{(damage ? "true" : "false")}},"damage_note":null,"read_confidence":{{conf}}}]""";
    }

    [Fact]
    public void CleanCaughtWhenAllFieldsMatch()
    {
        var (caught, _) = DockEval50Scoring.ScoreOne(
            Photo("clean", true, true, false), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson()));
        Assert.True(caught);
    }

    [Fact]
    public void CleanMissesWhenAnyFieldWrong()
    {
        var (caught, detail) = DockEval50Scoring.ScoreOne(
            Photo("clean", true, true, false), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson(po: "PO-OTHER")));
        Assert.False(caught);
        Assert.Contains("po", detail);
    }

    [Fact]
    public void MissingMarkCaughtWhenFalse()
    {
        var (caught, _) = DockEval50Scoring.ScoreOne(
            Photo("missing_mark", true, false, false), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson(mark: false)));
        Assert.True(caught);
    }

    [Fact]
    public void MissingMarkMissesWhenTrue()
    {
        var (caught, _) = DockEval50Scoring.ScoreOne(
            Photo("missing_mark", true, false, false), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson(mark: true)));
        Assert.False(caught);
    }

    [Fact]
    public void WrongPoCaughtWhenWrongTranscribed()
    {
        using var doc = JsonDocument.Parse(
            """{"carton":"CART-UNIT-1","problem":"wrong_po","expected":{"should_read":true,"damage_visible":false},"label":{"carton_id":"CART-UNIT-1","part":"PART-U1","po_number":"PO-WRONG","lot_code":"LOT-UNIT-1","units":7,"un_number":"UN0000","mark_present":true,"class9":true,"smudge_lot":false,"x":0.25,"y":0.3,"w":0.5,"h":0.4}}""");
        var (caught, _) = DockEval50Scoring.ScoreOne(
            doc.RootElement, "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson(po: "PO-WRONG")));
        Assert.True(caught);
    }

    [Fact]
    public void WrongPoMissesWhenHallucinatedExpected()
    {
        using var doc = JsonDocument.Parse(
            """{"carton":"CART-UNIT-1","problem":"wrong_po","expected":{"should_read":true,"damage_visible":false},"label":{"carton_id":"CART-UNIT-1","part":"PART-U1","po_number":"PO-WRONG","lot_code":"LOT-UNIT-1","units":7,"un_number":"UN0000","mark_present":true,"class9":true,"smudge_lot":false,"x":0.25,"y":0.3,"w":0.5,"h":0.4}}""");
        var (caught, _) = DockEval50Scoring.ScoreOne(
            doc.RootElement, "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson(po: "PO-UNIT-1")));
        Assert.False(caught);
    }

    [Fact]
    public void SmudgedLotCaughtWhenNull()
    {
        var (caught, _) = DockEval50Scoring.ScoreOne(
            Photo("smudged_lot", true, true, false), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson(lot: null, conf: 0.2)));
        Assert.True(caught);
    }

    [Fact]
    public void SmudgedLotMissesWhenConfidentGuess()
    {
        var (caught, _) = DockEval50Scoring.ScoreOne(
            Photo("smudged_lot", true, true, false), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson(lot: "LOT-UNIT-1", conf: 0.99)));
        Assert.False(caught);
    }

    [Fact]
    public void CrushedCaughtWhenDamageTrue()
    {
        var (caught, _) = DockEval50Scoring.ScoreOne(
            Photo("crushed", true, true, true), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson(damage: true)));
        Assert.True(caught);
    }

    [Fact]
    public void CrushedMissesWhenDamageFalse()
    {
        var (caught, _) = DockEval50Scoring.ScoreOne(
            Photo("crushed", true, true, true), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson(damage: false)));
        Assert.False(caught);
    }

    [Fact]
    public void HiddenCaughtWhenEmpty()
    {
        var (caught, _) = DockEval50Scoring.ScoreOne(
            Photo("hidden", false, true, false, withLabel: false), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads("[]"));
        Assert.True(caught);
    }

    [Fact]
    public void HiddenMissesWhenOverRead()
    {
        var (caught, _) = DockEval50Scoring.ScoreOne(
            Photo("hidden", false, true, false, withLabel: false), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson()));
        Assert.False(caught);
    }

    [Fact]
    public void MissingCartonReadIsAMiss()
    {
        var (caught, detail) = DockEval50Scoring.ScoreOne(
            Photo("clean", true, true, false), "PO-UNIT-1", "PART-U1", "LOT-UNIT-1",
            Reads(ReadJson(carton: "OTHER")));
        Assert.False(caught);
        Assert.Contains("no read", detail);
    }

    [Fact]
    public void ParseReadsStripsFences()
    {
        var reads = DockEval50Scoring.ParseReads("```json\n[]\n```");
        Assert.Empty(reads);
    }

    [Fact]
    public void ParseReadsThrowsOnGarbage()
    {
        Assert.ThrowsAny<JsonException>(() => DockEval50Scoring.ParseReads("[{bad"));
    }
}

// Hard-tier scoring checks with small fake data: the subtle_dent problem
// follows the damage-visible rule with its own detail line.
using System.Text.Json;

namespace Gate.Eval;

public sealed class DockEvalHardScoringTests
{
    private static JsonElement DentCarton()
    {
        using var doc = JsonDocument.Parse(
            """{"carton":"CART-HARD-1","problem":"subtle_dent","expected":{"should_read":true,"damage_visible":true},"label":{"carton_id":"CART-HARD-1","part":"PART-H1","po_number":"PO-HARD-1","lot_code":"LOT-HARD-1","units":7,"un_number":"UN0000","mark_present":true,"class9":true,"smudge_lot":false,"cx":0.5,"cy":0.5,"w":0.12,"h":0.135}}""");
        return doc.RootElement.Clone();
    }

    private static List<JsonElement> Reads(bool damage)
    {
        using var doc = JsonDocument.Parse(
            $$"""[{"carton_id":"CART-HARD-1","part":"PART-H1","po_number":"PO-HARD-1","lot_code":"LOT-HARD-1","units_per_carton":7,"mark_present":true,"mark_legible":true,"un_number":"UN0000","class9_visible":true,"damage_visible":{{(damage ? "true" : "false")}},"damage_note":null,"read_confidence":0.9}]""");
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    [Fact]
    public void SubtleDentCaughtWhenDamageTrue()
    {
        var (caught, detail) = DockEval50Scoring.ScoreOne(
            DentCarton(), "PO-HARD-1", "PART-H1", "LOT-HARD-1", Reads(damage: true));
        Assert.True(caught);
        Assert.Contains("dent", detail);
    }

    [Fact]
    public void SubtleDentMissesWhenDamageFalse()
    {
        var (caught, _) = DockEval50Scoring.ScoreOne(
            DentCarton(), "PO-HARD-1", "PART-H1", "LOT-HARD-1", Reads(damage: false));
        Assert.False(caught);
    }
}

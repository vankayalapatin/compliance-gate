// What-if reports: pure old-vs-new computation over supplied rows.
// History and pipeline windows stay separate; the trailing-12-month label
// is mandatory without a fiscal calendar; the unviable flag is explicit;
// replay is byte-identical. Test-double rows only.
using System.Text.Json;
using Gate.Compliance;
using Gate.Contracts;

namespace Gate.Eval;

public sealed class WhatIfTests
{
    private static WhatIfRuleConfig Cap(string cap) => new(
        MemoRuleRegistry.OriginShareCap,
        new Dictionary<string, string> { ["cap"] = cap, ["windowMonths"] = "12", ["partScope"] = "critical" });

    private static List<WhatIfHistoryRow> History() =>
    [
        new("PO-1", "BAT-9", true, "V-A", "CN", 340, 10m, "USD"),
        new("PO-2", "BAT-9", true, "V-A", "CN", 250, 10m, "USD"),
        new("PO-3", "BAT-9", true, "V-B", "MX", 380, 10m, "USD"),
        new("PO-4", "BAT-9", true, "V-C", "OTHER", 30, 10m, "USD"),
    ];

    private static WhatIfReport Compute(
        IReadOnlyList<WhatIfHistoryRow>? history = null,
        IReadOnlyList<WhatIfPipelineRow>? pipeline = null,
        WhatIfRuleConfig? oldConfig = null,
        WhatIfRuleConfig? newConfig = null,
        string? oldRuleId = "CORP-02",
        KbFiscalCalendar? fiscal = null,
        IReadOnlyList<WhatIfPipelineRow>? probes = null) =>
        WhatIfCalculator.Compute(
            history ?? History(),
            pipeline ?? [],
            oldConfig,
            newConfig ?? Cap("0.40"),
            oldConfig is null ? null : oldRuleId,
            fiscal,
            "proposal-hash-9",
            "kbv9",
            new Dictionary<string, string> { ["CORP"] = "v0.2" },
            MemoFixtures.FixedAt,
            probes);

    [Fact]
    public void TighteningFlipsPassToBlockWithBothSidesQuoted()
    {
        // (380 + 380) / (1000 + 380) = 55.1%: passes at 60%, blocks at 40%.
        var report = Compute(oldConfig: Cap("0.60"));
        var flip = Assert.Single(report.HistoryFlips, f => f.DocumentNumber == "PO-3");
        Assert.Equal(VerdictKind.Pass, flip.OldKind);
        Assert.Equal(VerdictKind.Block, flip.NewKind);
        Assert.Equal("proposed:OriginShareCap", flip.BlockingRuleId);
        Assert.Equal(380m, flip.Quantity);
        Assert.Equal(3800m, flip.Amount);
        Assert.Equal("USD", flip.Currency);
    }

    [Fact]
    public void LooseningFlipsBlockToPassNamingTheOldRule()
    {
        // (590 + 340) / (1000 + 340) = 69.4%: blocks at 60%, passes at 90%.
        var report = Compute(oldConfig: Cap("0.60"), newConfig: Cap("0.90"));
        var flip = Assert.Single(report.HistoryFlips, f => f.DocumentNumber == "PO-1");
        Assert.Equal(VerdictKind.Block, flip.OldKind);
        Assert.Equal(VerdictKind.Pass, flip.NewKind);
        Assert.Equal("CORP-02", flip.BlockingRuleId);
    }

    [Fact]
    public void HistoryLabelIsMandatoryApproximationWithoutCalendar()
    {
        var report = Compute(oldConfig: Cap("0.60"));
        Assert.Equal(
            "trailing-12-month approximation, FY window pending calendar",
            report.HistoryLabel);
        Assert.Equal(4, report.HistoryRowCount);
    }

    [Fact]
    public void HistoryLabelNamesTheFiscalYearWithCalendar()
    {
        var fiscal = new KbFiscalCalendar("12-31", "FY26", ["P1"]);
        var report = Compute(oldConfig: Cap("0.60"), fiscal: fiscal);
        Assert.Equal("current-fiscal-year window (FY26)", report.HistoryLabel);
    }

    [Fact]
    public void PipelineRowsListEveryDocWithCurrentBlocker()
    {
        var pipeline = new List<WhatIfPipelineRow>
        {
            new("PO", "PO draft 1", "BAT-9", true, "V-A", "CN", 120, 10m, "USD"),
            new("PO", "PO draft 2", "BAT-9", true, "V-B", "MX", 120, 10m, "USD"),
        };
        var report = Compute(oldConfig: Cap("0.60"), pipeline: pipeline);
        Assert.Equal(2, report.PipelineRows.Count);
        // (590 + 120) / (1000 + 120) = 63.4%: blocks under both caps, no flip.
        var cn = Assert.Single(report.PipelineRows, r => r.DocumentNumber == "PO draft 1");
        Assert.Equal(VerdictKind.Block, cn.OldKind);
        Assert.Equal(VerdictKind.Block, cn.NewKind);
        Assert.Equal("proposed:OriginShareCap", cn.BlockingRuleId);
        // (380 + 120) / (1000 + 120) = 44.6%: passes at 60%, blocks at 40%.
        var mx = Assert.Single(report.PipelineRows, r => r.DocumentNumber == "PO draft 2");
        Assert.Equal(VerdictKind.Pass, mx.OldKind);
        Assert.Equal(VerdictKind.Block, mx.NewKind);
        Assert.Single(report.Flips, f => f.DocumentNumber == "PO draft 2");
    }

    [Fact]
    public void FlipsCombineBothWindowsAndTotalsSpanThem()
    {
        var pipeline = new List<WhatIfPipelineRow>
        {
            new("PO", "PO draft 2", "BAT-9", true, "V-B", "MX", 120, 10m, "USD"),
        };
        var report = Compute(oldConfig: Cap("0.60"), pipeline: pipeline);
        Assert.Equal(report.HistoryFlips.Count + 1, report.Flips.Count);
        Assert.Contains(report.Flips, f => f.DocumentType == "Award");
        Assert.Contains(report.Flips, f => f.DocumentType == "PO");
        Assert.Equal(5, report.Totals.DocumentsEvaluated);
        Assert.True(report.Totals.FlipsToBlock >= 2);
        Assert.Equal(0, report.Totals.FlipsToPass);
        Assert.Equal("USD", report.Totals.Currency);
    }

    [Fact]
    public void MixedCurrenciesNullTheTotalsCurrency()
    {
        var history = new List<WhatIfHistoryRow>
        {
            new("PO-1", "BAT-9", true, "V-A", "CN", 340, 10m, "USD"),
            new("PO-2", "BAT-9", true, "V-A", "CN", 250, 10m, "USD"),
            new("PO-3", "BAT-9", true, "V-B", "MX", 380, 9m, "EUR"),
            new("PO-4", "BAT-9", true, "V-C", "OTHER", 30, 10m, "USD"),
        };
        // (380 + 120) / (1000 + 120) = 44.6%: both pipeline rows flip
        // pass-to-block at 40%, one in EUR, one in USD.
        var pipeline = new List<WhatIfPipelineRow>
        {
            new("PO", "PO draft 2", "BAT-9", true, "V-B", "MX", 120, 9m, "EUR"),
            new("PO", "PO draft 3", "BAT-9", true, "V-B", "MX", 120, 10m, "USD"),
        };
        var report = Compute(history: history, pipeline: pipeline, oldConfig: Cap("0.60"));
        Assert.True(report.Flips.Count >= 2);
        Assert.Null(report.Totals.Currency);
        Assert.Contains(report.Flips, f => f.Currency == "USD");
        Assert.Contains(report.Flips, f => f.Currency == "EUR");
    }

    [Fact]
    public void UnviableWhenEveryProbeBlocks()
    {
        var probes = new List<WhatIfPipelineRow>
        {
            new("Probe", "probe-a", "BAT-9", true, "V-A", "CN", 120, 10m, "USD"),
            new("Probe", "probe-b", "BAT-9", true, "V-B", "MX", 120, 10m, "USD"),
        };
        var report = Compute(oldConfig: Cap("0.60"), probes: probes);
        Assert.True(report.Unviable);
        Assert.DoesNotContain(report.PipelineRows, r => r.DocumentType == "Probe");
        Assert.DoesNotContain(report.Flips, f => f.DocumentType == "Probe");
    }

    [Fact]
    public void ViableWhenAnyProbePasses()
    {
        var probes = new List<WhatIfPipelineRow>
        {
            new("Probe", "probe-b", "BAT-9", true, "V-B", "MX", 120, 10m, "USD"),
        };
        var report = Compute(oldConfig: Cap("0.60"), newConfig: Cap("0.60"), probes: probes);
        Assert.False(report.Unviable);
    }

    [Fact]
    public void NoProbesMeansNotUnviable()
    {
        var report = Compute(oldConfig: Cap("0.60"));
        Assert.False(report.Unviable);
    }

    [Fact]
    public void ReplayIsByteIdentical()
    {
        var opts = new JsonSerializerOptions();
        var first = Compute(oldConfig: Cap("0.60"));
        var second = Compute(oldConfig: Cap("0.60"));
        Assert.Equal(
            JsonSerializer.SerializeToUtf8Bytes(first, opts),
            JsonSerializer.SerializeToUtf8Bytes(second, opts));
    }

    [Fact]
    public void RowOrderDoesNotChangeTheBytes()
    {
        var opts = new JsonSerializerOptions();
        var shuffled = History().AsEnumerable().Reverse().ToList();
        var a = JsonSerializer.SerializeToUtf8Bytes(Compute(history: History(), oldConfig: Cap("0.60")), opts);
        var b = JsonSerializer.SerializeToUtf8Bytes(Compute(history: shuffled, oldConfig: Cap("0.60")), opts);
        Assert.Equal(a, b);
    }

    [Fact]
    public void NewDocumentRuleBlocksMissingDocsOnly()
    {
        var doc = new WhatIfRuleConfig(
            MemoRuleRegistry.DocumentPresence,
            new Dictionary<string, string> { ["docKind"] = "COO-Declaration", ["partScope"] = "critical" });
        var pipeline = new List<WhatIfPipelineRow>
        {
            new("PO", "PO-10", "BAT-9", true, "V-A", "CN", 5, 10m, "USD", DocPresent: false),
            new("PO", "PO-11", "BAT-9", true, "V-B", "MX", 5, 10m, "USD", DocPresent: true),
            new("PO", "PO-12", "SCR-9", false, "V-C", "TW", 5, 1m, "USD", DocPresent: false),
        };
        var report = WhatIfCalculator.Compute(
            [], pipeline, null, doc, null, null, "h", "kbv9",
            new Dictionary<string, string>(), MemoFixtures.FixedAt);
        Assert.Equal(VerdictKind.Block, Assert.Single(report.PipelineRows, r => r.DocumentNumber == "PO-10").NewKind);
        Assert.Equal(VerdictKind.Pass, Assert.Single(report.PipelineRows, r => r.DocumentNumber == "PO-11").NewKind);
        Assert.Equal(VerdictKind.Pass, Assert.Single(report.PipelineRows, r => r.DocumentNumber == "PO-12").NewKind);
        Assert.Equal("trailing-12-month approximation, FY window pending calendar", report.HistoryLabel);
    }

    [Fact]
    public void DamageNeverBlocksOnlyReviews()
    {
        var damage = new WhatIfRuleConfig(MemoRuleRegistry.DamageReview, new Dictionary<string, string>());
        var pipeline = new List<WhatIfPipelineRow>
        {
            new("Receipt", "RCT-1", "BAT-9", true, "V-B", "MX", 5, 10m, "USD", DamageVisible: true),
        };
        var report = WhatIfCalculator.Compute(
            [], pipeline, null, damage, null, null, "h", "kbv9",
            new Dictionary<string, string>(), MemoFixtures.FixedAt);
        var row = Assert.Single(report.PipelineRows);
        Assert.Equal(VerdictKind.HumanReview, row.NewKind);
    }

    [Fact]
    public void PrePayHistoryStaysPassAsPosted()
    {
        var prepay = new WhatIfRuleConfig(
            MemoRuleRegistry.PrePayBlockOnOpen,
            new Dictionary<string, string> { ["holds"] = "true", ["quarantine"] = "true", ["reviews"] = "true" });
        var report = WhatIfCalculator.Compute(
            History(), [], null, prepay, null, null, "h", "kbv9",
            new Dictionary<string, string>(), MemoFixtures.FixedAt);
        Assert.Empty(report.HistoryFlips);
        Assert.Equal(4, report.HistoryRowCount);
    }

    [Fact]
    public void MissingCountryReviewsBothSidesWithoutFlipping()
    {
        var pipeline = new List<WhatIfPipelineRow>
        {
            new("PR", "PR-1", "BAT-9", true, null, null, 5, 10m, "USD"),
        };
        var report = Compute(oldConfig: Cap("0.60"), pipeline: pipeline);
        var row = Assert.Single(report.PipelineRows);
        Assert.Equal(VerdictKind.HumanReview, row.OldKind);
        Assert.Equal(VerdictKind.HumanReview, row.NewKind);
        Assert.DoesNotContain(report.Flips, f => f.DocumentNumber == "PR-1");
    }

    [Fact]
    public void TypeMismatchRefuses()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Compute(oldConfig: Cap("0.60"), newConfig: new WhatIfRuleConfig(
                MemoRuleRegistry.VendorShareCap,
                new Dictionary<string, string> { ["cap"] = "0.40", ["windowMonths"] = "12", ["partScope"] = "critical" })));
        Assert.Contains("same registry type", ex.Message);
    }

    [Fact]
    public void OldConfigWithoutOldRuleIdRefuses()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WhatIfCalculator.Compute(History(), [], Cap("0.60"), Cap("0.40"), null, null,
                "h", "kbv9", new Dictionary<string, string>(), MemoFixtures.FixedAt));
        Assert.Contains("oldRuleId", ex.Message);
    }
}

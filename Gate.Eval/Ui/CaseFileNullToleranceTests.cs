// Case-file null tolerance: JSON that predates (or omits) a collection
// reads back with empty lists on both lanes, foreign files sharing the
// cache dir keep a missing case payload, and every scan skips those
// files instead of crashing. Both lanes also agree on the sidecar
// suffixes they exclude. Test-double data only. No live calls.
using System.Net;
using System.Text.Json;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Services;
using Gate.Web.ViewModels;

namespace Gate.Eval.Ui;

public sealed class CaseFileNullToleranceTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static (CaseCacheStore Store, SimulationUiService Simulate, CaseViewService Views) Services(string dir)
    {
        var store = new CaseCacheStore(dir);
        var photos = new DockPhotoService(
            UiTestSupport.NewDir("gate-ui-null-dock"), UiTestSupport.NewDir("gate-ui-null-vision"));
        var erp = new InMemoryErpAdapter();
        var simulate = new SimulationUiService(
            new HttpClient(new StubHandler()) { BaseAddress = new Uri("http://localhost/") },
            erp, store, photos, UiTestLogger<SimulationUiService>.Instance);
        var views = new CaseViewService(
            erp, store, photos, UiTestLogger<CaseViewService>.Instance);
        return (store, simulate, views);
    }

    [Fact]
    public async Task ReadsNullCollectionsAsEmptyOnBothLanes()
    {
        var dir = UiTestSupport.NewDir("gate-ui-null");
        Directory.CreateDirectory(dir);
        var valid = UiTestSupport.CaseFileWith("OLD-1", "SO-9", "ITEM-9");
        var nulled = valid with
        {
            Case = valid.Case with { Invoices = null!, PaymentNumbers = null! },
            Events = null!,
            Decisions =
            [
                new Decision(
                    "D-1", "OLD-1", DecisionKind.PrApproval, "Old card", DecisionStatus.Pending,
                    "approver", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null,
                    null!, null,
                    new SplitPlan(
                        [new SplitPlanLine("V-1", "Vendor 1", 5m, null!)],
                        null!, 10m, null, null, null)),
            ],
            ScreeningSnapshot = new AwardScreeningSnapshot(
                DateTimeOffset.UtcNow, "PR-9", "ITEM-9", 5m, null,
                [new AwardScreeningRow(
                    "V-1", "Vendor 1", "US", "United States", 2m, "TST",
                    true, "Passed", "ok", true, null!,
                    new AwardMathLine("two times five", null!))]),
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, "OLD-1.json"),
            JsonSerializer.Serialize(nulled, CaseCacheStore.JsonOpts));

        var listed = await new CaseCacheStore(dir).ListAsync();
        var file = Assert.Single(listed);
        Assert.NotNull(file.Case);
        Assert.Empty(file.Case.Invoices);
        Assert.Empty(file.Case.PaymentNumbers);
        Assert.Empty(file.Events);
        var decision = Assert.Single(file.Decisions);
        Assert.Empty(decision.EvidenceRefs);
        Assert.NotNull(decision.Split);
        Assert.Empty(decision.Split.FinalShares);
        Assert.Empty(Assert.Single(decision.Split.Lines).Verdicts);
        Assert.NotNull(file.ScreeningSnapshot);
        var row = Assert.Single(file.ScreeningSnapshot.Rows);
        Assert.Empty(row.Verdicts);
        Assert.NotNull(row.Math);
        Assert.Empty(row.Math.Segments);

        var agent = CaseStore.Load(dir, "OLD-1");
        Assert.NotNull(agent);
        Assert.NotNull(agent.Case);
        Assert.Empty(agent.Events);
        Assert.NotEmpty(agent.Decisions);
        Assert.Empty(agent.Case.Invoices);
    }

    [Fact]
    public async Task ScansSkipFilesWithNoCasePayload()
    {
        var dir = UiTestSupport.NewDir("gate-ui-null");
        var (store, simulate, views) = Services(dir);
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(UiTestSupport.CaseFileWith(
            "CASE-1", "SO-1", "ITEM-1",
            [UiTestSupport.PendingDecision("D-1", "CASE-1", documentType: null, documentNumber: null)],
            new Case(
                "CASE-1", "SO-1", "ITEM-1", "ui-test", CaseStage.PoPlaced, now, now,
                "PR-1", "PO-9", null, null, [], [], null, null)));
        await File.WriteAllTextAsync(
            Path.Combine(dir, "foreign.json"),
            """{"caseId":"OLD-9","problems":[{"cartonId":"C-9","kind":0}],"recordedAt":"2026-11-03T00:00:00Z"}""");

        var listed = await store.ListAsync();
        Assert.Equal(2, listed.Count);
        Assert.Single(listed, f => f.Case is null);
        var reread = await store.ReadAsync("foreign");
        Assert.NotNull(reread);
        Assert.Null(reread.Case);
        Assert.Empty(reread.Events);

        Assert.Equal("CASE-1", await simulate.FindCaseIdForPoAsync("PO-9"));
        var cards = await views.ListDecisionCardsAsync();
        Assert.Equal("D-1", Assert.Single(cards).Decision.DecisionId);
        var scan = RuleBlockScan.CountBlocks(listed);
        Assert.Equal(1, scan.FilesScanned);
        Assert.Empty(ExceptionBoard.Rows(listed));
    }

    [Fact]
    public async Task UiAndAgentLanesAgreeOnSidecars()
    {
        var dir = UiTestSupport.NewDir("gate-ui-null");
        var store = new CaseCacheStore(dir);
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-9"));
        foreach (var suffix in new[]
                 {
                     "CASE-1.mail.json", "CASE-1.vendor-identity.json", "CASE-1.dock.json",
                     "CASE-1.problems.json", "MEMO-1.memo.json",
                 })
        {
            await File.WriteAllTextAsync(Path.Combine(dir, suffix), "{}");
        }

        Assert.Equal("CASE-1", Assert.Single(await store.ListAsync()).Case.CaseId);
        Assert.Equal("CASE-1", Assert.Single(CaseStore.ListCaseIds(dir)));
    }
}

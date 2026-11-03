// Decisions retry affordance: when the post-decision advance fails, the
// settled card offers an explicit Try again instead of stranding the
// case. Fake ERP plus a fail-twice model client; no live calls.
using Bunit;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Components.Pages;
using Gate.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

public sealed class DecisionRetryTests
{
    private sealed class FailCallsClient(IModelClient inner, params int[] failOn) : IModelClient
    {
        private int _calls;

        public string ModelName => inner.ModelName;

        public async Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            _calls++;
            if (failOn.Contains(_calls))
            {
                throw new System.Net.Http.HttpRequestException($"Boom {_calls}.");
            }

            return await inner.CompleteAsync(request, ct);
        }
    }

    private sealed record Harness(
        InMemoryErpAdapter Erp,
        CaseCacheStore Store,
        CaseViewService Views,
        DecisionOutcomeService Outcomes,
        AskWhyService Why,
        ActingUserState Acting);

    private static Harness Create(IModelClient models)
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("TST");
        erp.SeedAutoApproveThreshold(10m);
        erp.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        erp.SeedVendor(new VendorDto("V-TEST", "Test Vendor", "US", false, null, "NET0"));
        erp.SeedVendorPrice("V-TEST", "ITEM-1", 10m);
        erp.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        erp.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));
        var dir = UiTestSupport.NewDir("gate-ui-retry");
        Directory.CreateDirectory(Path.Combine(dir, "dock"));
        var cases = new CaseCacheStore(dir);
        var compliance = new RecordingComplianceEngine();
        var agent = new P2pAgent(erp, models, compliance);
        var photos = new DockPhotoService(
            Path.Combine(dir, "dock"), Path.Combine(dir, "vision"));
        var views = new CaseViewService(erp, cases, photos, UiTestLogger<CaseViewService>.Instance);
        var runner = new CaseRunner(erp, agent, models, compliance, new DockEvaluatorAdapter(), dir);
        var outcomes = new DecisionOutcomeService(
            cases, UiTestLogger<DecisionOutcomeService>.Instance, runner);
        var why = new AskWhyService(
            agent, models, erp, cases, photos, new ReplayState(),
            UiTestLogger<AskWhyService>.Instance, Path.Combine(dir, "model-cache"));
        return new Harness(erp, cases, views, outcomes, why, new ActingUserState());
    }

    [Fact]
    public async Task FailedAdvanceShowsTryAgainAndRetryContinuesCase()
    {
        var h = Create(new FailCallsClient(new FakeModelClient(), 1, 2));
        h.Erp.SeedSalesOrder(new SalesOrder(
            "SO-1", SalesOrderStatus.Confirmed, "TST", [new SalesOrderLine("ITEM-1", 5m)]));
        h.Erp.SeedItem(new ItemDto("ITEM-1", "Test item", false, 6, "EA"));
        var pr = await h.Erp.CreatePrAsync(
            "requester", RecordSource.P2pAgent, "requester",
            [new PurchaseRequisitionLine("ITEM-1", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await h.Erp.SubmitForApprovalAsync(
            "PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        var file = UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1", DecisionKind.PrApproval, "PR", pr.PrNumber),
        ]);
        var staged = file with
        {
            Case = file.Case with { Stage = CaseStage.PrSubmitted, PrNumber = pr.PrNumber }
        };
        var events = staged.Events.ToList();
        events.Add(new CaseEvent(
            "CASE-1-e1", "CASE-1", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.Note, "V-TEST", null, "Candidates", "ITEM-1", null, null, null));
        await h.Store.WriteAsync(staged with { Events = events });

        h.Acting.SetCurrent("approver");
        using var ctx = new TestContext();
        ctx.Services.AddSingleton(h.Views);
        ctx.Services.AddSingleton(h.Outcomes);
        ctx.Services.AddSingleton(h.Why);
        ctx.Services.AddSingleton(h.Acting);
        ctx.Services.AddSingleton<ILogger<Decisions>>(UiTestLogger<Decisions>.Instance);

        using var board = ctx.RenderComponent<Decisions>();
        board.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", board.Markup), TimeSpan.FromSeconds(10));
        var titleEl = board.FindAll(".gate-card-title").First(e => e.TextContent == "Test decision");
        titleEl.Closest("section")!.QuerySelectorAll("button")
            .First(b => b.TextContent == "Approve").Click();
        board.WaitForAssertion(
            () => Assert.Contains("did not advance", board.Markup), TimeSpan.FromSeconds(30));
        board.FindAll("button").First(b => b.TextContent == "Try again").Click();
        board.WaitForAssertion(
            () => Assert.Contains("The case is now", board.Markup), TimeSpan.FromSeconds(30));

        Assert.DoesNotContain("Try again", board.Markup);
        var pos = await h.Erp.GetPurchaseOrdersAsync();
        var po = Assert.Single(pos);
        Assert.Equal("V-TEST", po.VendorId);
    }
}

// Agent activity pane tests: pure render rules (words, links, caps, no
// internal ids), the service build over fixture cases, and the component
// collapse toggle. Test data is local; nothing reaches the app db.
using Bunit;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Components.Layout;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Gate.Eval.Ui;

public sealed class AgentActivityTests
{
    [Fact]
    public void EveryStepHasItsOwnWords()
    {
        foreach (var step in Enum.GetValues<CaseNextStep>())
        {
            Assert.NotEqual("Next step", ActivityLines.StepWords(step));
        }
    }

    [Fact]
    public void EveryDecisionKindHasItsOwnWords()
    {
        foreach (var kind in Enum.GetValues<DecisionKind>())
        {
            Assert.NotEqual("Review", ActivityLines.KindWords(kind));
        }
    }

    [Fact]
    public void WaitingRowJoinsPendingDecisionWithDocLink()
    {
        var vendors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V-B"] = "Meridian Cellworks Inc",
        };
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9", [
            UiTestSupport.PendingDecision("D-9", "CASE-9", DecisionKind.PaymentApproval, "Payment", "PAY-9"),
        ]);

        var row = ActivityLines.WaitingRow(file, CaseNextStep.AwaitDecision, vendors, nowIdle: true);

        Assert.Contains("Payment approval payment PAY-9 waits on approver.", row.Text);
        Assert.Equal("erp/record/payment/PAY-9", row.Href);
        Assert.DoesNotContain("CASE-9", row.Text);
        Assert.DoesNotContain("D-9", row.Text);
    }

    [Fact]
    public void WaitingRowWithoutPendingDecisionNamesTheWarehouse()
    {
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");

        var row = ActivityLines.WaitingRow(
            file, CaseNextStep.AwaitDecision, new Dictionary<string, string>(), nowIdle: false);

        Assert.Contains("warehouse", row.Text);
        Assert.Equal("cases/CASE-9", row.Href);
    }

    [Fact]
    public void WaitingRowOnAgentStepWhileIdleReadsStopped()
    {
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");

        var row = ActivityLines.WaitingRow(
            file, CaseNextStep.DraftPayment, new Dictionary<string, string>(), nowIdle: true);

        Assert.Equal("Agent step stopped: Drafting the payment. Try again.", row.Text);
        Assert.Equal("cases/CASE-9", row.Href);
    }

    [Fact]
    public void WaitingRowOnAgentStepWhileActiveNamesTheAgent()
    {
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");

        var row = ActivityLines.WaitingRow(
            file, CaseNextStep.DraftPayment, new Dictionary<string, string>(), nowIdle: false);

        Assert.Equal("Drafting the payment for order SO-9: agent.", row.Text);
        Assert.Equal("cases/CASE-9", row.Href);
    }

    [Fact]
    public void MoneyUsesDollarsWithThousandsSeparators()
    {
        Assert.Equal("$22,200.00", ActivityLines.Money(22200m, "USD"));
        Assert.Equal("$100.00", ActivityLines.Money(100m, null));
        Assert.Equal("10.50 TST", ActivityLines.Money(10.5m, "TST"));
    }

    [Fact]
    public void EventLinesStayPlainAndVendorNamed()
    {
        var vendors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V-B"] = "Meridian Cellworks Inc",
        };
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");
        var matched = new CaseEvent("e1", "CASE-9", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.InvoiceMatched, "Matched.", null, "Invoice", "V-B/INV-9", null, 100m, "USD");

        var line = ActivityLines.EventLine(file, matched, vendors);

        Assert.Equal("Invoice INV-9 from Meridian Cellworks Inc matched: accepted $100.00.", line);
    }

    [Fact]
    public void ApprovalEventsDeferToDecisionRows()
    {
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");
        var approved = new CaseEvent("e1", "CASE-9", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.PoApproved, "PO-9 approved.", null, "PO", "PO-9", null, null, null);

        Assert.Null(ActivityLines.EventLine(
            file, approved, new Dictionary<string, string>()));
    }

    [Fact]
    public void DecidedLineNamesWhoDecidedWhat()
    {
        var vendors = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V-B"] = "Meridian Cellworks Inc",
        };
        var decided = new Decision("D-9", "CASE-9", DecisionKind.PaymentApproval, "Pay",
            DecisionStatus.Approved, "Finn Finance", DateTimeOffset.UtcNow, "Finn Finance",
            DateTimeOffset.UtcNow, "Payment", "PAY-9", 100m, "USD", null, null, [], null);

        Assert.Equal(
            "Finn Finance approved payment PAY-9 for $100.00.",
            ActivityLines.DecidedLine(vendors, decided));
    }

    [Fact]
    public void CapEnforcesTwentyFiveWords()
    {
        var longLine = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"w{i}"));

        var capped = ActivityLines.Cap(longLine);

        Assert.Equal(26, capped.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith("…", capped);
    }

    [Fact]
    public async Task BuildServesAllThreeSectionsWithoutInternalIds()
    {
        var dir = UiTestSupport.NewDir("gate-ui-activity");
        var cacheDir = Path.Combine(dir, "case-cache");
        Directory.CreateDirectory(cacheDir);
        var store = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        var open = new CaseFile(
            new Case("CASE-7", "SO-7", "BAT-7", "ui-test", CaseStage.InvoiceMatched, now, now,
                "PR-7", "PO-7", "ASN-7", "RCT-7", [], [], null, null),
            [
                new CaseEvent("e1", "CASE-7", now.AddMinutes(-30), RecordSource.P2pAgent,
                    CaseEventKind.PoCreated, "PO-7 placed.", null, "PO", "PO-7", null, null, null),
                new CaseEvent("e2", "CASE-7", now.AddSeconds(-10), RecordSource.P2pAgent,
                    CaseEventKind.InvoiceMatched, "Matched.", null, "Invoice", "V-B/INV-7", null, 50m, "USD"),
            ],
            [
                new Decision("D-7", "CASE-7", DecisionKind.InvoiceApproval, "Approve",
                    DecisionStatus.Approved, "Dana Director", now.AddMinutes(-20), "Dana Director",
                    now.AddMinutes(-19), "Invoice", "V-B/INV-7", 50m, "USD", null, null, [], null),
            ]);
        var done = new CaseFile(
            new Case("CASE-8", "SO-8", "BAT-8", "ui-test", CaseStage.Resolved, now, now,
                "PR-8", "PO-8", "ASN-8", "RCT-8", [], [], null, null),
            [
                new CaseEvent("e3", "CASE-8", now.AddHours(-5), RecordSource.P2pAgent,
                    CaseEventKind.CustomerNotified, "Notified.", null, null, null, null, null, null),
            ],
            []);
        await store.WriteAsync(open);
        await store.WriteAsync(done);
        var service = ActivityService(dir, cacheDir);

        var vm = await service.BuildAsync();

        Assert.Contains("order SO-7", vm.Now);
        Assert.DoesNotContain("Idle", vm.Now);
        var waiting = Assert.Single(vm.Waiting);
        Assert.Contains("SO-7", waiting.Text);
        Assert.StartsWith("cases/CASE-7", waiting.Href);
        Assert.NotEmpty(vm.Recent);
        Assert.StartsWith("Invoice INV-7 from Meridian Cellworks Inc matched", vm.Recent[0].Text);
        Assert.Contains(vm.Recent, l => l.Text.StartsWith("Dana Director approved invoice INV-7"));
        foreach (var line in vm.Recent.Select(l => l.Text).Prepend(vm.Now).Prepend(waiting.Text))
        {
            Assert.True(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 25, line);
            Assert.DoesNotContain("CASE-7", line);
            Assert.DoesNotContain("D-7", line);
            Assert.DoesNotContain("AwaitDecision", line);
            Assert.DoesNotContain("InvoiceMatched", line);
        }
    }

    [Fact]
    public async Task BuildWithNoCasesIsIdleAndEmpty()
    {
        var dir = UiTestSupport.NewDir("gate-ui-activity-empty");
        var cacheDir = Path.Combine(dir, "case-cache");
        Directory.CreateDirectory(cacheDir);
        var service = ActivityService(dir, cacheDir);

        var vm = await service.BuildAsync();

        Assert.Equal("Idle.", vm.Now);
        Assert.Empty(vm.Waiting);
        Assert.Empty(vm.Recent);
    }

    [Fact]
    public async Task BuildWhileIdleReadsAgentStepsAsStopped()
    {
        var dir = UiTestSupport.NewDir("gate-ui-activity-idle");
        var cacheDir = Path.Combine(dir, "case-cache");
        Directory.CreateDirectory(cacheDir);
        var store = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        var old = now.AddHours(-2);
        await store.WriteAsync(new CaseFile(
            new Case("CASE-IDLE", "SO-1001", SeededErp.Battery, "ui-test", CaseStage.Created, old, old,
                null, null, null, null, [], [], null, null),
            [
                new CaseEvent("e1", "CASE-IDLE", old, RecordSource.P2pAgent,
                    CaseEventKind.OrderReceived, "Order arrived.", null, "SO", "SO-1001", null, null, null),
            ],
            []));
        var service = ActivityService(dir, cacheDir);

        var vm = await service.BuildAsync();

        Assert.Equal("Idle.", vm.Now);
        var waiting = Assert.Single(vm.Waiting);
        Assert.StartsWith("Agent step stopped:", waiting.Text);
        Assert.DoesNotContain(": agent", waiting.Text);
        Assert.Equal("cases/CASE-IDLE", waiting.Href);
    }

    [Fact]
    public void PaneCollapsesAndExpands()
    {
        using var ctx = new TestContext();
        var dir = UiTestSupport.NewDir("gate-ui-activity-pane");
        var cacheDir = Path.Combine(dir, "case-cache");
        Directory.CreateDirectory(cacheDir);
        var store = new CaseCacheStore(cacheDir);
        var erp = SeededErp.Create();
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models, compliance,
            new DockEvaluatorAdapter(), cacheDir);
        ctx.Services.AddSingleton(store);
        ctx.Services.AddSingleton<IErpAdapter>(erp);
        ctx.Services.AddSingleton(runner);
        ctx.Services.AddSingleton(UiTestLogger<AgentActivityService>.Instance);
        ctx.Services.AddScoped<AgentActivityService>();

        var cut = ctx.RenderComponent<AgentActivityPane>();

        cut.WaitForAssertion(() => Assert.Contains("Agent activity", cut.Markup));
        Assert.Contains("Waiting on", cut.Markup);
        Assert.Contains("Recent", cut.Markup);
        cut.FindAll("button").Single(b => b.TextContent == "Hide").Click();
        Assert.DoesNotContain("Waiting on", cut.Markup);
        cut.FindAll("button").Single(b => b.TextContent == "Activity").Click();
        cut.WaitForAssertion(() => Assert.Contains("Waiting on", cut.Markup));
    }

    private static AgentActivityService ActivityService(string dir, string cacheDir)
    {
        var erp = SeededErp.Create();
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        return new AgentActivityService(
            new CaseCacheStore(cacheDir),
            erp,
            new CaseRunner(
                erp, new P2pAgent(erp, models, compliance), models, compliance,
                new DockEvaluatorAdapter(), cacheDir),
            UiTestLogger<AgentActivityService>.Instance);
    }
}

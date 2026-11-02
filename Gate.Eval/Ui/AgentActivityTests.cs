// Agent activity pane tests: pure render rules (words, links, caps, no
// internal ids), per-case grouping with item/order labels, bubble sides,
// the service build over fixture cases, and the component collapse toggle.
// Test data is local; nothing reaches the app db.
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
    private static ActivityLookups Lookups(
        Dictionary<string, string>? vendors = null,
        Dictionary<string, string>? items = null,
        decimal? threshold = 500m) =>
        new(vendors ?? new Dictionary<string, string>(StringComparer.Ordinal),
            items ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BAT-9"] = "Battery cell",
            },
            threshold);

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
        var lookups = Lookups(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V-B"] = "Meridian Cellworks Inc",
        });
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9", [
            UiTestSupport.PendingDecision("D-9", "CASE-9", DecisionKind.PaymentApproval, "Payment", "PAY-9"),
        ]);

        var row = ActivityLines.WaitingRow(file, CaseNextStep.AwaitDecision, lookups, nowIdle: true);

        Assert.Contains("Battery cell for SO-9: Payment approval payment PAY-9 waits on approver.", row.Text);
        Assert.Equal("erp/record/payment/PAY-9", row.Href);
        Assert.DoesNotContain("CASE-9", row.Text);
        Assert.DoesNotContain("D-9", row.Text);
    }

    [Fact]
    public void WaitingRowWithoutPendingDecisionNamesTheWarehouse()
    {
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");

        var row = ActivityLines.WaitingRow(
            file, CaseNextStep.AwaitDecision, Lookups(), nowIdle: false);

        Assert.Contains("Battery cell for SO-9: held goods wait on the warehouse.", row.Text);
        Assert.Equal("cases/CASE-9", row.Href);
    }

    [Fact]
    public void WaitingRowOnAgentStepWhileIdleReadsStopped()
    {
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");

        var row = ActivityLines.WaitingRow(
            file, CaseNextStep.DraftPayment, Lookups(), nowIdle: true);

        Assert.Equal("Battery cell for SO-9: agent step stopped: Drafting the payment. Try again.", row.Text);
        Assert.Equal("cases/CASE-9", row.Href);
    }

    [Fact]
    public void WaitingRowOnAgentStepWhileActiveNamesTheAgent()
    {
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");

        var row = ActivityLines.WaitingRow(
            file, CaseNextStep.DraftPayment, Lookups(), nowIdle: false);

        Assert.Equal("Battery cell for SO-9: Drafting the payment: agent.", row.Text);
        Assert.Equal("cases/CASE-9", row.Href);
    }

    [Fact]
    public void WaitingRowOnSupplierAckCarriesTheOrderApprovalHint()
    {
        var lookups = Lookups(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V-C"] = "Formosa Fasteners Co",
        }, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SCR-9"] = "Screw",
        });
        var now = DateTimeOffset.UtcNow;
        var file = new CaseFile(
            new Case("CASE-S", "SO-1001", "SCR-9", "ui-test", CaseStage.PoPlaced, now, now,
                "PR-1035", "PO-1036", null, null, [], [], null, null, AwardedVendorId: "V-C"),
            [], []);

        var row = ActivityLines.WaitingRow(
            file, CaseNextStep.AwaitSupplierAck, lookups, nowIdle: false,
            "Purchase order PO-1036 auto-approved (under $500.00).");

        Assert.Equal(
            "Screw for SO-1001: Purchase order PO-1036 auto-approved (under $500.00). " +
            "Supplier acknowledgement waits on Formosa Fasteners Co.",
            row.Text);
        Assert.Equal("cases/CASE-S", row.Href);
    }

    [Fact]
    public void SiblingCasesOnOneOrderLabelTheirOwnItem()
    {
        var lookups = Lookups(items: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BAT-9"] = "Battery cell",
            ["SCR-9"] = "Screw",
        });
        var cells = UiTestSupport.CaseFileWith("CASE-C", "SO-1001", "BAT-9");
        var screws = UiTestSupport.CaseFileWith("CASE-S", "SO-1001", "SCR-9");

        var cellsRow = ActivityLines.WaitingRow(cells, CaseNextStep.AwaitDecision, lookups, nowIdle: false);
        var screwsRow = ActivityLines.WaitingRow(screws, CaseNextStep.AwaitDecision, lookups, nowIdle: false);

        Assert.StartsWith("Battery cell for SO-1001:", cellsRow.Text);
        Assert.StartsWith("Screw for SO-1001:", screwsRow.Text);
        Assert.NotEqual(cellsRow.Text, screwsRow.Text);
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
        var lookups = Lookups(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V-B"] = "Meridian Cellworks Inc",
        });
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");
        var matched = new CaseEvent("e1", "CASE-9", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.InvoiceMatched, "Matched.", null, "Invoice", "V-B/INV-9", null, 100m, "USD");

        var line = ActivityLines.EventLine(file, matched, lookups);

        Assert.Equal("Invoice INV-9 from Meridian Cellworks Inc matched: accepted $100.00.", line);
    }

    [Fact]
    public void ApprovalEventsDeferToDecisionRows()
    {
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9", [
            new Decision("D-9", "CASE-9", DecisionKind.PaymentApproval, "Pay",
                DecisionStatus.Approved, "Finn Finance", DateTimeOffset.UtcNow, "Finn Finance",
                DateTimeOffset.UtcNow, "PO", "PO-9", 100m, "USD", null, null, [], null),
        ]);
        var approved = new CaseEvent("e1", "CASE-9", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.PoApproved, "PO-9 approved.", null, "PO", "PO-9", null, null, null);

        Assert.Null(ActivityLines.EventLine(file, approved, Lookups()));
    }

    [Fact]
    public void PolicyApprovalsReadAsAutoApprovedWithThreshold()
    {
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");
        var pr = new CaseEvent("e1", "CASE-9", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.PrApproved, "PR-9 auto-approved.", null, "PR", "PR-9", null, null, null);
        var po = new CaseEvent("e2", "CASE-9", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.PoApproved, "PO-9 auto-approved.", null, "PO", "PO-9", null, null, null);

        Assert.Equal(
            "Request PR-9 auto-approved (under $500.00).",
            ActivityLines.EventLine(file, pr, Lookups()));
        Assert.Equal(
            "Purchase order PO-9 auto-approved (under $500.00).",
            ActivityLines.EventLine(file, po, Lookups()));
    }

    [Fact]
    public void PolicyApprovalsWithoutThresholdOmitItHonestly()
    {
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "BAT-9");
        var po = new CaseEvent("e1", "CASE-9", DateTimeOffset.UtcNow, RecordSource.P2pAgent,
            CaseEventKind.PoApproved, "PO-9 auto-approved.", null, "PO", "PO-9", null, null, null);

        Assert.Equal(
            "Purchase order PO-9 auto-approved by policy.",
            ActivityLines.EventLine(file, po, Lookups(threshold: null)));
    }

    [Fact]
    public void DecidedLineNamesWhoDecidedWhat()
    {
        var lookups = Lookups(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V-B"] = "Meridian Cellworks Inc",
        });
        var decided = new Decision("D-9", "CASE-9", DecisionKind.PaymentApproval, "Pay",
            DecisionStatus.Approved, "Finn Finance", DateTimeOffset.UtcNow, "Finn Finance",
            DateTimeOffset.UtcNow, "Payment", "PAY-9", 100m, "USD", null, null, [], null);

        Assert.Equal(
            "Finn Finance approved payment PAY-9 for $100.00.",
            ActivityLines.DecidedLine(lookups, decided));
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
            new Case("CASE-7", "SO-7", SeededErp.Battery, "ui-test", CaseStage.InvoiceMatched, now, now,
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
            new Case("CASE-8", "SO-8", SeededErp.Screw, "ui-test", CaseStage.Resolved, now, now,
                "PR-8", "PO-8", "ASN-8", "RCT-8", [], [], null, null),
            [
                new CaseEvent("e3", "CASE-8", now.AddHours(-5), RecordSource.P2pAgent,
                    CaseEventKind.CustomerNotified, "Notified.", null, null, null, null, null, null),
            ],
            []);
        await store.WriteAsync(open);
        await store.WriteAsync(done);
        var service = ActivityService(cacheDir);

        var vm = await service.BuildAsync();

        Assert.StartsWith("Battery cell for SO-7:", vm.Now);
        Assert.DoesNotContain("Idle", vm.Now);
        var waiting = Assert.Single(vm.Waiting);
        Assert.Contains("Battery cell for SO-7:", waiting.Text);
        Assert.StartsWith("cases/CASE-7", waiting.Href);
        Assert.Equal(2, vm.Recent.Count);
        var first = vm.Recent[0];
        Assert.Equal("Battery cell for SO-7", first.HeaderText);
        Assert.Equal("cases/CASE-7", first.CaseHref);
        Assert.StartsWith("Invoice INV-7 from Meridian Cellworks Inc matched", first.Lines[0].Text);
        Assert.Equal(ActivitySide.Agent, first.Lines[0].Side);
        Assert.False(first.Lines[0].Refused);
        var person = Assert.Single(first.Lines, l => l.Side == ActivitySide.Person);
        Assert.StartsWith("Dana Director approved invoice INV-7", person.Text);
        Assert.Equal("Screw for SO-8", vm.Recent[1].HeaderText);
        foreach (var text in vm.Recent.SelectMany(g => g.Lines.Select(l => l.Text))
            .Append(vm.Now).Append(waiting.Text).Append(first.HeaderText))
        {
            Assert.True(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 25, text);
            Assert.DoesNotContain("CASE-7", text);
            Assert.DoesNotContain("CASE-8", text);
            Assert.DoesNotContain("D-7", text);
            Assert.DoesNotContain("AwaitDecision", text);
            Assert.DoesNotContain("InvoiceMatched", text);
        }
    }

    [Fact]
    public async Task BuildGroupsSiblingCasesOnOneOrderSeparately()
    {
        var dir = UiTestSupport.NewDir("gate-ui-activity-siblings");
        var cacheDir = Path.Combine(dir, "case-cache");
        Directory.CreateDirectory(cacheDir);
        var store = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        var cells = new CaseFile(
            new Case("CASE-C9", "SO-9", SeededErp.Battery, "ui-test", CaseStage.PrSubmitted, now, now,
                "PR-C9", null, null, null, [], [], null, null),
            [
                new CaseEvent("e1", "CASE-C9", now.AddMinutes(-20), RecordSource.P2pAgent,
                    CaseEventKind.PrSubmitted, "PR sent.", null, "PR", "PR-C9", null, null, null),
            ],
            []);
        var screws = new CaseFile(
            new Case("CASE-S9", "SO-9", SeededErp.Screw, "ui-test", CaseStage.PrSubmitted, now, now,
                "PR-S9", null, null, null, [], [], null, null),
            [
                new CaseEvent("e2", "CASE-S9", now.AddMinutes(-10), RecordSource.P2pAgent,
                    CaseEventKind.PrApproved, "PR auto-approved.", null, "PR", "PR-S9", null, null, null),
                new CaseEvent("e3", "CASE-S9", now.AddMinutes(-9), RecordSource.P2pAgent,
                    CaseEventKind.AgentRefused, "Tried to place the order; refused.", "place_order",
                    null, null, null, null, null),
            ],
            []);
        await store.WriteAsync(cells);
        await store.WriteAsync(screws);
        var service = ActivityService(cacheDir);

        var vm = await service.BuildAsync();

        Assert.Equal(2, vm.Waiting.Count);
        Assert.All(vm.Waiting, r => Assert.Contains("SO-9", r.Text));
        Assert.Contains(vm.Waiting, r => r.Text.StartsWith("Battery cell for SO-9:"));
        Assert.Contains(vm.Waiting, r => r.Text.StartsWith("Screw for SO-9:"));
        Assert.Equal(2, vm.Recent.Count);
        var screwGroup = Assert.Single(vm.Recent, g => g.CaseHref == "cases/CASE-S9");
        Assert.Equal("Screw for SO-9", screwGroup.HeaderText);
        Assert.Contains(screwGroup.Lines, l => l.Text.Contains("auto-approved (under $500.00)"));
        var refused = Assert.Single(screwGroup.Lines, l => l.Refused);
        Assert.Equal(ActivitySide.Agent, refused.Side);
        var cellGroup = Assert.Single(vm.Recent, g => g.CaseHref == "cases/CASE-C9");
        Assert.DoesNotContain(cellGroup.Lines, l => l.Refused);
    }

    [Fact]
    public async Task BuildLabelsAnUnfamiliarItemAndVendorGenerically()
    {
        // Genericity proof: an item and vendor the displays never heard of
        // render through the same lookups, descriptions, and thresholds.
        var dir = UiTestSupport.NewDir("gate-ui-activity-generic");
        var cacheDir = Path.Combine(dir, "case-cache");
        Directory.CreateDirectory(cacheDir);
        var erp = SeededErp.Create();
        erp.SeedItem(new ItemDto("COIL-3", "Copper coil", false, 5, "EA"));
        erp.SeedVendor(new VendorDto("V-Z", "Zed Parts AB", "SE", false, null, "NET0"));
        var store = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(new CaseFile(
            new Case("CASE-Z3", "SO-77", "COIL-3", "ui-test", CaseStage.InvoiceMatched, now, now,
                "PR-Z3", "PO-Z3", "ASN-Z3", "RCT-Z3", [], [], null, null, AwardedVendorId: "V-Z"),
            [
                new CaseEvent("e1", "CASE-Z3", now.AddMinutes(-4), RecordSource.P2pAgent,
                    CaseEventKind.SupplierAwarded, "Awarded.", null, "Vendor", "V-Z", null, null, null),
            ],
            []));
        var service = ActivityService(cacheDir, erp);

        var vm = await service.BuildAsync();

        var waiting = Assert.Single(vm.Waiting);
        Assert.StartsWith("Copper coil for SO-77:", waiting.Text);
        var group = Assert.Single(vm.Recent);
        Assert.Equal("Copper coil for SO-77", group.HeaderText);
        Assert.Contains("Zed Parts AB", group.Lines[0].Text);
        Assert.DoesNotContain("COIL-3", group.HeaderText);
        Assert.DoesNotContain("V-Z", group.Lines[0].Text);
    }

    [Fact]
    public async Task BuildWithNoCasesIsIdleAndEmpty()
    {
        var dir = UiTestSupport.NewDir("gate-ui-activity-empty");
        var cacheDir = Path.Combine(dir, "case-cache");
        Directory.CreateDirectory(cacheDir);
        var service = ActivityService(cacheDir);

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
        var service = ActivityService(cacheDir);

        var vm = await service.BuildAsync();

        Assert.Equal("Idle.", vm.Now);
        var waiting = Assert.Single(vm.Waiting);
        Assert.StartsWith("Battery cell for SO-1001: agent step stopped:", waiting.Text);
        Assert.DoesNotContain(": agent.", waiting.Text);
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
        ctx.Services.AddSingleton(new PresentationPace());
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

    private static AgentActivityService ActivityService(string cacheDir, InMemoryErpAdapter? erp = null)
    {
        erp ??= SeededErp.Create();
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

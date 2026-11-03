// Vendor headroom tool tests (offline, scripted model plus real compliance
// core): the read tool carries the pinned facts shape, headroom stays in
// whole cartons, the binding rule surfaces when the full request does not
// fit, and missing inputs refuse loudly. Test-double data only: an invented
// cell item with its own history and prices.
using System.Text.Json;
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval.Agent;

public sealed class VendorHeadroomTests
{
    private const string Item = "CELL-H1";

    private sealed record Harness(
        InMemoryErpAdapter Erp,
        RecordingComplianceEngine Compliance,
        CaseRunner Runner,
        RecordingModelClient Models,
        string CaseDir);

    private static string TempDir(string name) =>
        Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));

    private static Harness Create()
    {
        var erp = SeededErp.Create();
        erp.SeedItem(new ItemDto(Item, "Test cell", true, 10, "EA"));
        erp.SeedVendorPrice("V-A", Item, 11m);
        erp.SeedVendorPrice("V-B", Item, 12m);
        // Total 10000, request 1200: CN 5600 caps V-A at 1120 cells
        // ((5600 + q) / (10000 + 1200) stays within 60% only for q <= 1120,
        // with the rest of the request already open); MX 3000 leaves V-B
        // room for the whole test request.
        erp.SeedVolumeHistory(Item,
        [
            new VolumeHistory("Seed CN", "CN", 5600),
            new VolumeHistory("Seed MX", "MX", 3000),
            new VolumeHistory("Seed Other", "OTHER", 1400),
        ]);
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var dir = TempDir("gate-headroom-test");
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), dir);
        return new Harness(erp, compliance, runner, models, dir);
    }

    private static async Task<string> OpenCaseAsync(Harness h, decimal quantity)
    {
        var input = new OrderWebhookInput(
            "test",
            $"SHOP-{Guid.NewGuid():N}",
            "planner",
            new Dictionary<string, IReadOnlyList<string>> { [Item] = ["V-A", "V-B"] },
            $"idem-{Guid.NewGuid():N}",
            [new SalesOrderLine(Item, quantity)],
            "USD");
        var cases = await h.Runner.OnOrderWebhookAsync(input);
        return Assert.Single(cases);
    }

    private static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";

    [Fact]
    public async Task HeadroomFactsMatchPinnedShape()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, 1200);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        var outcome = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);

        var service = new VendorHeadroomService(h.Erp, h.Compliance, h.CaseDir);
        var facts = await service.GetHeadroomAsync(caseId);
        Assert.Equal(Item, facts.Item);
        Assert.Equal(1200, facts.RequestedQty);
        Assert.Equal(10, facts.CartonSize);
        Assert.Equal(2, facts.Vendors.Count);

        using var doc = JsonDocument.Parse(facts.ToJson());
        var root = doc.RootElement;
        Assert.Equal(Item, root.GetProperty("item").GetString());
        Assert.Equal(1200, root.GetProperty("requestedQty").GetDecimal());
        Assert.Equal(10, root.GetProperty("cartonSize").GetInt32());
        var vendors = root.GetProperty("vendors").EnumerateArray().ToList();
        Assert.Equal(2, vendors.Count);

        var capped = vendors[0];
        Assert.Equal("V-A", capped.GetProperty("vendorId").GetString());
        Assert.Equal("CinderVolt Energy Ltd", capped.GetProperty("vendorName").GetString());
        Assert.Equal(11, capped.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(1120, capped.GetProperty("headroomQty").GetDecimal());
        Assert.Equal(112, capped.GetProperty("headroomCartons").GetInt32());
        Assert.False(capped.GetProperty("fullRequestPossible").GetBoolean());
        Assert.Equal("CORP-02", capped.GetProperty("bindingRuleId").GetString());
        Assert.Contains("cap", capped.GetProperty("bindingReason").GetString(), StringComparison.Ordinal);

        var roomy = vendors[1];
        Assert.Equal("V-B", roomy.GetProperty("vendorId").GetString());
        Assert.Equal("Meridian Cellworks Inc", roomy.GetProperty("vendorName").GetString());
        Assert.Equal(12, roomy.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(1200, roomy.GetProperty("headroomQty").GetDecimal());
        Assert.Equal(120, roomy.GetProperty("headroomCartons").GetInt32());
        Assert.True(roomy.GetProperty("fullRequestPossible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, roomy.GetProperty("bindingRuleId").ValueKind);
        Assert.Equal(JsonValueKind.Null, roomy.GetProperty("bindingReason").ValueKind);
    }

    [Fact]
    public async Task HeadroomStaysInWholeCartons()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, 1200);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);

        var service = new VendorHeadroomService(h.Erp, h.Compliance, h.CaseDir);
        var facts = await service.GetHeadroomAsync(caseId);

        Assert.All(facts.Vendors, row =>
        {
            Assert.Equal(0, row.HeadroomQty % facts.CartonSize);
            Assert.Equal(row.HeadroomQty, row.HeadroomCartons * facts.CartonSize);
        });
    }

    [Fact]
    public void HeadroomToolIsAlwaysLegalRead()
    {
        var tool = AgentTools.Catalog.Single(t => t.Name == AgentTools.GetVendorHeadroom);
        Assert.Contains(tool.Parameters, p => p.Name == "case_id");
        Assert.Contains("propose_split", tool.Description, StringComparison.Ordinal);
        Assert.True(AgentTools.IsReadTool(AgentTools.GetVendorHeadroom));

        var stamp = DateTimeOffset.UtcNow;
        var created = new CaseFile(
            new Case("case-1", "SO-1", Item, "test", CaseStage.Created, stamp, stamp,
                null, null, null, null, [], [], null, null),
            [], []);
        var createdView = new CaseErpView(true, null, null, false, false, false, 0, 0, 0, false, 0, false);
        Assert.Contains(AgentTools.GetVendorHeadroom, CasePicker.LegalTools(created, createdView));

        var done = created with { Case = created.Case with { Stage = CaseStage.Resolved } };
        Assert.Contains(AgentTools.GetVendorHeadroom, CasePicker.LegalTools(done, createdView));
    }

    [Fact]
    public async Task HeadroomReadThroughToolLoop()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, 50);
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        var outcome = await h.Runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitDecision, outcome.Step);

        var card = CaseStore.Load(h.CaseDir, caseId)!.Decisions
            .Single(d => d.Status == DecisionStatus.Pending);
        await h.Runner.ResolveDecisionAsync(caseId, card.DecisionId, true, card.RequestedTo, "ok");

        h.Models.QueueToolCalls("check room first", ("c3", AgentTools.GetVendorHeadroom, Args(caseId)));
        h.Models.QueueToolCalls("screen", ("c4", AgentTools.ScreenVendors, Args(caseId)));
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Runner.AdvanceAsync(caseId));
        Assert.Contains("failed after one retry", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("No scripted response", thrown.ToString(), StringComparison.Ordinal);

        var turns = CaseTranscript.ReadTurns(h.CaseDir, caseId);
        var first = turns.Single(t => t.Calls.Any(c => c.Name == AgentTools.GetVendorHeadroom));
        var call = Assert.Single(first.Calls);
        Assert.Equal(AgentTools.GetVendorHeadroom, call.Name);
        Assert.Equal(ToolCallVerdict.Executed, call.Verdict);
        Assert.StartsWith("get_vendor_headroom:", first.Results[0], StringComparison.Ordinal);
        Assert.Contains("\"headroomQty\"", first.Results[0], StringComparison.Ordinal);
        Assert.Contains("\"V-A\"", first.Results[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPriceRefusesLoudly()
    {
        var h = Create();
        var input = new OrderWebhookInput(
            "test",
            $"SHOP-{Guid.NewGuid():N}",
            "planner",
            new Dictionary<string, IReadOnlyList<string>> { [Item] = ["V-A", "V-C"] },
            $"idem-{Guid.NewGuid():N}",
            [new SalesOrderLine(Item, 50)],
            "USD");
        var caseId = Assert.Single(await h.Runner.OnOrderWebhookAsync(input));
        h.Models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        h.Models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        await h.Runner.AdvanceAsync(caseId);

        var service = new VendorHeadroomService(h.Erp, h.Compliance, h.CaseDir);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetHeadroomAsync(caseId));
        Assert.Contains("names no price", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingPrRefusesLoudly()
    {
        var h = Create();
        var caseId = await OpenCaseAsync(h, 40);

        var service = new VendorHeadroomService(h.Erp, h.Compliance, h.CaseDir);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetHeadroomAsync(caseId));
        Assert.Contains("no PR", thrown.Message, StringComparison.Ordinal);
    }
}

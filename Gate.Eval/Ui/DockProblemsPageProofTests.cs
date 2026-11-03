// Dock-problem page proof: a stale same-ASN photo from a prior lifetime
// sits on the counter next to a live three-carton truck. The dock page
// offers only recorded views, so the stale photo is never offered; picking
// missing-mark on C-2 and crushed on C-3, rebuilding, and sending the
// rebuilt photo through the real page holds exactly C-2 for the mark and
// routes exactly C-3 to damage review while C-1 passes. The send renders on
// the dock page and the review renders on Decisions. Test-double data only.
// No live calls.
using System.Net;
using System.Text.Json;
using Bunit;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web;
using Gate.Web.Components.Outside;
using Gate.Web.Components.Pages;
using Gate.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

public sealed class DockProblemsPageProofTests
{
    private static readonly JsonSerializerOptions WebOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Serves the UI service's dock POSTs with the real endpoint
    /// logic: rebuilds run the real endpoint helper on the real runner, and
    /// photo sends transcribe, judge, and advance exactly like the real
    /// handlers. Only the transport is test-owned.</summary>
    private sealed class RunnerHttpHandler(CaseRunner runner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(path, SimulationUiService.RebuildViewsPath, StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                var rebuild = await SimulationEndpoints.RebuildDockViewsAsync(
                    JsonSerializer.Deserialize<DockRebuildViewsRequest>(body, WebOpts),
                    runner, cancellationToken).ConfigureAwait(false);
                return rebuild.Ok
                    ? Ok(new
                    {
                        caseId = rebuild.CaseId,
                        images = rebuild.Images,
                        problems = rebuild.Problems
                            .Select(p => new { cartonId = p.CartonId, kind = p.Kind }).ToList(),
                    })
                    : BadRequest(rebuild.Error ?? "Rebuild refused.");
            }

            if (string.Equals(path, SimulationUiService.DockPhotosPath, StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                var photos = JsonSerializer.Deserialize<DockPhotosRequest>(body, WebOpts)
                    ?? throw new InvalidOperationException("Dock photos request did not parse.");
                var reads = await runner.TranscribeDockPhotosAsync(
                    photos.CaseId, photos.Photos, cancellationToken).ConfigureAwait(false);
                var verdicts = await runner.OnDockReadsAsync(
                    photos.CaseId, reads, cancellationToken).ConfigureAwait(false);
                var advanced = await runner.AdvanceAsync(photos.CaseId, cancellationToken).ConfigureAwait(false);
                return Ok(new
                {
                    cartons = verdicts.Keys.Order(StringComparer.Ordinal).ToList(),
                    step = advanced.Step.ToString(),
                    advanced.Complete,
                });
            }

            return BadRequest($"No test route for {request.Method} {path}.");
        }

        private static HttpResponseMessage Ok(object payload) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, WebOpts),
                    System.Text.Encoding.UTF8, "application/json"),
            };

        private static HttpResponseMessage BadRequest(string error) =>
            new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { error }, WebOpts),
                    System.Text.Encoding.UTF8, "application/json"),
            };
    }

    private sealed record Flow(
        InMemoryErpAdapter Erp,
        CaseRunner Runner,
        RecordingModelClient Models,
        string CaseDir,
        string DockDir,
        string CaseId,
        string AsnNumber,
        string PoNumber);

    private static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";

    private static string AwardArgs(string caseId, string vendorId) =>
        $$"""{"case_id":"{{caseId}}","vendor_id":"{{vendorId}}"}""";

    private static string ReadsJson(string poNumber)
    {
        static string Read(string carton, string lot, bool mark, bool damage, string po) =>
            "{\"carton_id\":\"" + carton
            + "\",\"part\":\"" + SeededErp.Battery
            + "\",\"po_number\":\"" + po
            + "\",\"lot_code\":\"" + lot
            + "\",\"units_per_carton\":5"
            + ",\"mark_present\":" + (mark ? "true" : "false")
            + ",\"mark_legible\":" + (mark ? "true" : "false")
            + ",\"un_number\":" + (mark ? "\"UN3480\"" : "null")
            + ",\"class9_visible\":true"
            + ",\"damage_visible\":" + (damage ? "true" : "false")
            + ",\"damage_note\":" + (damage ? "\"crushed corner with tear\"" : "null")
            + ",\"read_confidence\":0.97}";
        return "["
            + Read("C-1", "LOT-T1", true, false, poNumber) + ","
            + Read("C-2", "LOT-T2", false, false, poNumber) + ","
            + Read("C-3", "LOT-T3", true, true, poNumber) + "]";
    }

    /// <summary>Drives one battery case to a receipted three-carton ASN and
    /// plants a stale same-ASN photo from a prior lifetime on the counter.</summary>
    private static async Task<Flow> DriveTruckStageAsync()
    {
        var dir = UiTestSupport.NewDir("gate-ui-dockproblems");
        Directory.CreateDirectory(dir);
        var caseDir = Path.Combine(dir, "case-cache");
        var dockDir = Path.Combine(dir, "dock");
        Directory.CreateDirectory(dockDir);
        var erp = SeededErp.Create();
        var models = new RecordingModelClient();
        var compliance = new RecordingComplianceEngine();
        var runner = new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), caseDir,
            dockImageOptions: new DockImageOptions(
                VisionFiles.DockScenesBasesDir(),
                VisionFiles.DockScenesPath(),
                Path.Combine(dir, "unused-truth.json"),
                dockDir,
                Path.Combine(dir, "imgcache"),
                Path.Combine(dir, "dockreads")));
        var input = new OrderWebhookInput(
            "test",
            $"SHOP-{Guid.NewGuid():N}",
            "planner",
            new Dictionary<string, IReadOnlyList<string>> { [SeededErp.Battery] = ["V-B"] },
            $"idem-{Guid.NewGuid():N}",
            [new SalesOrderLine(SeededErp.Battery, 15)],
            "USD");
        var caseId = Assert.Single(await runner.OnOrderWebhookAsync(input));
        models.QueueToolCalls("shortfall", ("c1", AgentTools.ComputeShortfall, Args(caseId)));
        models.QueueToolCalls("raise", ("c2", AgentTools.RaisePr, Args(caseId)));
        models.QueueToolCalls("screen", ("c3", AgentTools.ScreenVendors, Args(caseId)));
        models.QueueToolCalls("Award to V-B; sole candidate.", ("c4", AgentTools.AwardSupplier, AwardArgs(caseId, "V-B")));
        models.QueueToolCalls("order", ("c5", AgentTools.CreatePo, Args(caseId)));
        var outcome = await runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitSupplierAck, outcome.Step);

        await runner.OnSupplierAckAsync(new SupplierAckInput(caseId));
        var (asn, receipt) = await runner.OnAsnAsync(new SupplierAsnInput(
            caseId,
            $"ASN-{Guid.NewGuid():N}",
            "PACK-T1",
            [new AsnCartonInput("C-1", SeededErp.Battery, 5, "LOT-T1"),
             new AsnCartonInput("C-2", SeededErp.Battery, 5, "LOT-T2"),
             new AsnCartonInput("C-3", SeededErp.Battery, 5, "LOT-T3")]));
        Assert.NotNull(receipt);
        var poNumber = CaseStore.Load(caseDir, caseId)!.Case.PoNumber!;
        File.WriteAllBytes(
            Path.Combine(dockDir, $"{asn.AsnNumber}-SCENE-01.jpg"),
            await File.ReadAllBytesAsync(Path.Combine(VisionFiles.DockScenesBasesDir(), "scene-01.jpg")));
        return new Flow(erp, runner, models, caseDir, dockDir, caseId, asn.AsnNumber, poNumber);
    }

    private static SimulationUiService SimulateFor(Flow flow)
    {
        var cases = new CaseCacheStore(flow.CaseDir);
        var photos = new DockPhotoService(flow.DockDir, flow.DockDir + "-vision");
        var http = new HttpClient(new RunnerHttpHandler(flow.Runner)) { BaseAddress = new Uri("http://localhost/") };
        return new SimulationUiService(
            http, flow.Erp, cases, photos, UiTestLogger<SimulationUiService>.Instance);
    }

    private static void ClickButton(IRenderedComponent<DockPanel> page, string label)
    {
        var button = page.FindAll("button").First(b => b.TextContent.Contains(label, StringComparison.Ordinal));
        button.Click();
    }

    [Fact]
    public async Task StalePhotoNeverOfferedAndProblemsLandExactlyOnPages()
    {
        var flow = await DriveTruckStageAsync();
        var service = SimulateFor(flow);
        var stale = $"{flow.AsnNumber}-SCENE-01.jpg";

        var options = await service.GetOptionsAsync();
        Assert.Empty(options.DockImages);
        Assert.Contains(
            options.Notes, n => n.Contains("were not built for the current cases", StringComparison.Ordinal));

        using var ctx = new TestContext();
        ctx.Services.AddSingleton(service);
        using var page = ctx.RenderComponent<DockPanel>(p => p.Add(x => x.Options, options));
        page.WaitForAssertion(
            () => Assert.Contains($"Truck {flow.AsnNumber}", page.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("failed to load", page.Markup);
        Assert.DoesNotContain(stale, page.Markup);
        Assert.Contains("No pallet views on the counter", page.Markup, StringComparison.Ordinal);

        // Pick the two problems on the page and rebuild through it.
        var reads = ReadsJson(flow.PoNumber);
        flow.Models.QueueText(reads);
        flow.Models.QueueText(reads);
        page.Find($"select[aria-label=\"Simulated problem for C-2\"]").Change("MissingMark");
        page.Find($"select[aria-label=\"Simulated problem for C-3\"]").Change("Crushed");
        ClickButton(page, "Rebuild views");
        var fresh = $"{flow.AsnNumber}-SCENE-04.jpg";
        page.WaitForAssertion(
            () => Assert.Contains($"Rebuilt 1 view(s): {fresh}.", page.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));
        Assert.Contains($"id=\"dockimg-{flow.AsnNumber}-{fresh}\"", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(stale, page.Markup);

        var offered = await service.GetOptionsAsync();
        Assert.Equal([fresh], offered.DockImages.ToList());

        // Send the rebuilt photo through the page: the outcome names every
        // carton read, and the persisted verdicts hold exactly C-2 for the
        // mark while C-3 goes to damage review and C-1 passes.
        flow.Models.QueueText(reads);
        ClickButton(page, "Send dock photos");
        page.WaitForAssertion(
            () => Assert.Contains(
                "Dock photos accepted: C-1, C-2, C-3.", page.Markup, StringComparison.Ordinal),
            TimeSpan.FromSeconds(15));

        var evidence = CaseStore.LoadDock(flow.CaseDir, flow.CaseId);
        Assert.NotNull(evidence);
        Assert.Equal(
            ["C-1", "C-2", "C-3"], evidence.Verdicts.Keys.Order(StringComparer.Ordinal).ToList());
        Assert.All(evidence.Verdicts["C-1"], v => Assert.Equal(VerdictKind.Pass, v.Kind));
        var dock = new DockEvaluatorAdapter();
        var markHold = Assert.Single(evidence.Verdicts["C-2"], v => v.Kind != VerdictKind.Pass);
        Assert.Equal(VerdictKind.Hold, markHold.Kind);
        Assert.Equal(dock.MarkRuleId, markHold.RuleId);
        var damageReview = Assert.Single(evidence.Verdicts["C-3"], v => v.Kind != VerdictKind.Pass);
        Assert.Equal(VerdictKind.HumanReview, damageReview.Kind);
        Assert.Equal(dock.DamageRuleId, damageReview.RuleId);

        var holds = await flow.Erp.GetOpenHoldsAsync(null);
        var hold = Assert.Single(holds);
        Assert.Equal("C-2", hold.CartonId);
        var decisions = CaseStore.Load(flow.CaseDir, flow.CaseId)!.Decisions;
        var review = Assert.Single(decisions, d =>
            d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview);
        Assert.Equal(DecisionKind.DamageReview, review.Kind);
        Assert.Equal(DecisionStatus.Pending, review.Status);
        Assert.Contains("C-3", review.Title, StringComparison.Ordinal);

        // The damage review renders on the real Decisions page.
        using var pages = HostPages(flow, review.RequestedTo);
        using var board = pages.RenderComponent<Decisions>();
        board.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", board.Markup),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("failed to load", board.Markup);
        Assert.Contains("Damage review for C-3", board.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Damage review for C-1", board.Markup);
        Assert.DoesNotContain("Damage review for C-2", board.Markup);
    }

    private static TestContext HostPages(Flow flow, string? actingAs)
    {
        var dir = UiTestSupport.NewDir("gate-ui-dockproblems-pages");
        Directory.CreateDirectory(dir);
        var cases = new CaseCacheStore(flow.CaseDir);
        var photos = new DockPhotoService(flow.DockDir, Path.Combine(dir, "vision"));
        var views = new CaseViewService(flow.Erp, cases, photos, UiTestLogger<CaseViewService>.Instance);
        var acting = new ActingUserState();
        acting.SetCurrent(actingAs ?? "approver");
        var ctx = new TestContext();
        ctx.Services.AddSingleton(views);
        ctx.Services.AddSingleton(new ErpViewService(
            flow.Erp, views, UiTestLogger<ErpViewService>.Instance));
        ctx.Services.AddSingleton(new ErpActionService(
            flow.Erp, flow.Runner, UiTestLogger<ErpActionService>.Instance));
        ctx.Services.AddSingleton(new DecisionOutcomeService(
            cases, UiTestLogger<DecisionOutcomeService>.Instance, flow.Runner));
        ctx.Services.AddSingleton(SimulateFor(flow));
        ctx.Services.AddSingleton(acting);
        ctx.Services.AddSingleton<ILogger<Decisions>>(_ => UiTestLogger<Decisions>.Instance);
        var models = new FakeModelClient();
        ctx.Services.AddSingleton(new AskWhyService(
            new P2pAgent(flow.Erp, models, new RecordingComplianceEngine()),
            models, flow.Erp, cases, photos, new ReplayState(),
            UiTestLogger<AskWhyService>.Instance, Path.Combine(dir, "model-cache")));
        return ctx;
    }
}

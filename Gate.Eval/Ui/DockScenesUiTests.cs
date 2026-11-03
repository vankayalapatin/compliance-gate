// Dock scenes UI tests: the turn-budget config flow, the dock-view
// rebuild shape plus failures, the problem pickers submitting, and the
// Admin live-path section. The live switch lives on the agent's shared
// DockSceneLive; the status helper reads MetaImages config and formats
// the Admin lines. Doubles only (in-memory config, stub HTTP, fake ERP,
// temp dirs — never company-data numbers). No live calls.
using System.Net;
using System.Text.Json;
using Bunit;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Eval;
using Gate.Web;
using Gate.Web.Components.Pages;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Gate.Eval.Ui;

public sealed class DockScenesUiTests
{
    private static readonly JsonSerializerOptions WebOpts = new(JsonSerializerDefaults.Web);

    private sealed class StubHandler : HttpMessageHandler
    {
        public int CallCount;
        public HttpRequestMessage? LastRequest;
        public string? LastBody;
        public HttpResponseMessage Response =
            new(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Response;
        }
    }

    private sealed class StubSceneGenerator : ISceneGenerator
    {
        public string GeneratorName => "test-stub";

        public Task<byte[]> GenerateSceneAsync(int cartonCount, int crushedCount, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed record Harness(SimulationUiService Service, StubHandler Http);

    private static Harness Create()
    {
        var dir = UiTestSupport.NewDir("gate-ui-dockscenes");
        Directory.CreateDirectory(dir);
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency("TST");
        var dockDir = Path.Combine(dir, "dock");
        Directory.CreateDirectory(dockDir);
        var stub = new StubHandler();
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://localhost/") };
        var service = new SimulationUiService(
            http, erp, new CaseCacheStore(Path.Combine(dir, "case-cache")),
            new DockPhotoService(dockDir, Path.Combine(dir, "vision")),
            UiTestLogger<SimulationUiService>.Instance);
        return new Harness(service, stub);
    }

    private static void Respond(Harness h, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        h.Http.Response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private static T SentAs<T>(Harness h) =>
        JsonSerializer.Deserialize<T>(h.Http.LastBody ?? string.Empty, WebOpts)
        ?? throw new InvalidOperationException($"POST body did not parse as {typeof(T).Name}.");

    private static DockSceneStatus NoCredsStatus() =>
        new(new ConfigurationBuilder().Build());

    private static DockSceneStatus CredsStatus(string? sceneModel = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["MetaApi:BaseUrl"] = "https://images.test.example",
            ["MetaApi:ApiKey"] = "test-key",
            ["MetaImages:ImageModel"] = "edit-test",
        };
        if (sceneModel is not null)
        {
            values["MetaImages:SceneModel"] = sceneModel;
        }

        return new DockSceneStatus(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private static CaseRunner RunnerFor(string cacheDir)
    {
        var erp = new InMemoryErpAdapter();
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        return new CaseRunner(
            erp, new P2pAgent(erp, models, compliance), models, compliance,
            new DockEvaluatorAdapter(), cacheDir);
    }

    [Fact]
    public void TurnBudget_FlowsFromConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MetaApi:TurnMaxTokens"] = "16000" })
            .Build();

        Assert.Equal(16000, TurnBudgetOptions.FromConfig(config).TurnMaxTokens);
    }

    [Fact]
    public void TurnBudget_MissingConfigYieldsNull()
    {
        var config = new ConfigurationBuilder().Build();

        Assert.Null(TurnBudgetOptions.FromConfig(config).TurnMaxTokens);
    }

    [Fact]
    public void DockSceneLive_SwitchDefaultsOffWithLibraryStatus()
    {
        var scenes = new DockSceneLive();

        Assert.False(scenes.LiveOn);
        Assert.Null(scenes.SceneGenerator);
        Assert.Null(scenes.LastFallbackReason);
        Assert.Equal(
            "Dock scenes: photo library.",
            NoCredsStatus().StatusLine(scenes.LiveOn, scenes.SceneGenerator is not null, scenes.LastFallbackReason));
    }

    [Fact]
    public void DockSceneStatus_LiveNeedsSwitchAndWiredGenerator()
    {
        var status = CredsStatus();

        Assert.True(status.HasCredentials);
        Assert.Equal("Dock scenes: photo library.", status.StatusLine(false, true, null));
        Assert.Equal("Dock scenes: photo library.", status.StatusLine(true, false, null));
        Assert.Equal("Dock scenes: live build on.", status.StatusLine(true, true, null));
    }

    [Fact]
    public void DockSceneStatus_MissingCredentialsNoteInPlainWords()
    {
        var note = NoCredsStatus().MissingCredentialsNote;

        Assert.NotNull(note);
        Assert.Contains("image service", note);
        Assert.Contains("address", note);
        Assert.Contains("model", note);
        Assert.Contains("key", note);
        Assert.DoesNotContain("MetaImages", note);
        Assert.DoesNotContain("/simulation", note);
        Assert.Null(CredsStatus().MissingCredentialsNote);
    }

    [Fact]
    public void DockSceneStatus_SceneModelDefaultsToImageModel()
    {
        Assert.Equal("edit-test", DockSceneStatus.SceneModelName(ConfigWithScene(null)));
        Assert.Equal("scene-test", DockSceneStatus.SceneModelName(ConfigWithScene("scene-test")));
        Assert.Null(DockSceneStatus.SceneModelName(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void DockSceneStatus_ImageOptionsComeFromMetaApiPlusSceneModel()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MetaApi:BaseUrl"] = "https://images.test.example",
                ["MetaApi:ApiKey"] = "test-key",
                ["MetaImages:SceneModel"] = "scene-test",
                ["MetaImages:ImageModel"] = "edit-test",
            })
            .Build();

        var options = DockSceneStatus.ImageOptionsOrNull(config);
        Assert.NotNull(options);
        Assert.Equal("https://images.test.example", options.BaseUrl);
        Assert.Equal("scene-test", options.SceneModel);
        Assert.Equal("test-key", options.ApiKey);
        Assert.Empty(DockSceneStatus.MissingSettings(config));
    }

    [Fact]
    public void DockSceneStatus_MissingSettingsNameEachGap()
    {
        var empty = new ConfigurationBuilder().Build();

        Assert.Null(DockSceneStatus.ImageOptionsOrNull(empty));
        Assert.Equal(
            ["MetaApi:BaseUrl", "MetaApi:ApiKey", "MetaImages:SceneModel"],
            DockSceneStatus.MissingSettings(empty).ToList());

        var partial = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MetaApi:BaseUrl"] = "https://images.test.example",
                ["MetaImages:ImageModel"] = "edit-test",
            })
            .Build();

        Assert.Null(DockSceneStatus.ImageOptionsOrNull(partial));
        Assert.Equal(["MetaApi:ApiKey"], DockSceneStatus.MissingSettings(partial).ToList());
    }

    private static IConfiguration ConfigWithScene(string? sceneModel)
    {
        var values = new Dictionary<string, string?>
        {
            ["MetaImages:BaseUrl"] = "https://images.test.example",
            ["MetaImages:ImageModel"] = "edit-test",
            ["MetaImages:ApiKey"] = "test-key",
        };
        if (sceneModel is not null)
        {
            values["MetaImages:SceneModel"] = sceneModel;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void DockSceneLive_StatusCarriesFallbackReason()
    {
        var scenes = new DockSceneLive();
        var status = NoCredsStatus();

        scenes.RecordFallback("labeler none failed");
        Assert.Equal(
            "Dock scenes: photo library; last run fell back to the library (labeler none failed).",
            status.StatusLine(scenes.LiveOn, false, scenes.LastFallbackReason));

        var blank = new DockSceneLive();
        blank.RecordFallback("  ");
        Assert.Null(blank.LastFallbackReason);
    }

    [Fact]
    public void DockProblemKinds_ParseKnownAndRejectUnknown()
    {
        Assert.True(DockProblemKinds.TryParse(DockProblemKinds.MissingMark, out var mark));
        Assert.Equal(DockProblemKinds.MissingMark, mark);
        Assert.True(DockProblemKinds.TryParse("crushed", out var crushed));
        Assert.Equal(DockProblemKinds.Crushed, crushed);
        Assert.True(DockProblemKinds.TryParse(" Hidden ", out var hidden));
        Assert.Equal(DockProblemKinds.Hidden, hidden);

        Assert.False(DockProblemKinds.TryParse("Melted", out _));
        Assert.False(DockProblemKinds.TryParse(string.Empty, out _));
        Assert.False(DockProblemKinds.TryParse("none", out _));
        Assert.True(DockProblemKinds.IsNone(null));
        Assert.True(DockProblemKinds.IsNone("none"));
        Assert.Contains("missing mark", DockProblemKinds.DescribeOptions());
    }

    [Fact]
    public async Task Rebuild_PostsPinnedShapeAndParsesAcceptance()
    {
        var h = Create();
        Respond(h, """{"accepted":true}""");

        var outcome = await h.Service.RebuildDockViewsAsync(
            "CASE-1", [new DockProblemForm("CTN-A", DockProblemKinds.MissingMark)]);

        Assert.Equal("/simulation/dock/rebuild-views", SimulationUiService.RebuildViewsPath);
        Assert.Equal(1, h.Http.CallCount);
        Assert.Equal(HttpMethod.Post, h.Http.LastRequest!.Method);
        Assert.Equal(SimulationUiService.RebuildViewsPath, h.Http.LastRequest.RequestUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(h.Http.LastBody!);
        Assert.Equal("CASE-1", doc.RootElement.GetProperty("caseId").GetString());
        var problem = Assert.Single(doc.RootElement.GetProperty("problems").EnumerateArray());
        Assert.Equal("CTN-A", problem.GetProperty("cartonId").GetString());
        Assert.Equal("MissingMark", problem.GetProperty("kind").GetString());
        Assert.True(outcome.Ok, outcome.Message);
        Assert.Empty(outcome.FileNames);
        Assert.Contains("Rebuild started", outcome.Message);
    }

    [Fact]
    public async Task Rebuild_RefusesWithoutCaseOrProblems()
    {
        var h = Create();

        Assert.False((await h.Service.RebuildDockViewsAsync(null, [new DockProblemForm("CTN-A", DockProblemKinds.Hidden)])).Ok);
        var empty = await h.Service.RebuildDockViewsAsync("CASE-1", []);
        Assert.False(empty.Ok);
        Assert.Contains("at least one simulated problem", empty.Message);
        var none = await h.Service.RebuildDockViewsAsync(
            "CASE-1", [new DockProblemForm("CTN-A", "none")]);
        Assert.False(none.Ok);
        var unknown = await h.Service.RebuildDockViewsAsync(
            "CASE-1", [new DockProblemForm("CTN-A", "Melted")]);
        Assert.False(unknown.Ok);
        Assert.Contains("Unknown carton problem", unknown.Message);
        var cartonless = await h.Service.RebuildDockViewsAsync(
            "CASE-1", [new DockProblemForm("  ", DockProblemKinds.Crushed)]);
        Assert.False(cartonless.Ok);
        Assert.Contains("carton id", cartonless.Message);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public async Task Rebuild_SurfacesServerRefusalWithReason()
    {
        var h = Create();
        Respond(h, """{"error":"Unknown case CASE-9."}""", HttpStatusCode.BadRequest);

        var outcome = await h.Service.RebuildDockViewsAsync(
            "CASE-9", [new DockProblemForm("CTN-A", DockProblemKinds.MissingMark)]);

        Assert.False(outcome.Ok);
        Assert.Contains("Unknown case CASE-9.", outcome.Message);
        Assert.Contains("Dock view rebuild", outcome.Message);
        Assert.DoesNotContain("/simulation", outcome.Message);
    }

    [Fact]
    public async Task Rebuild_AcceptanceCarriesNoFilesYet()
    {
        var h = Create();
        Respond(h, """{"accepted":true}""");

        var outcome = await h.Service.RebuildDockViewsAsync(
            "CASE-1", [new DockProblemForm("CTN-A", DockProblemKinds.Hidden)]);

        Assert.True(outcome.Ok, outcome.Message);
        Assert.Empty(outcome.FileNames);
    }

    [Fact]
    public void RebuildEndpoint_ValidationFailuresCarryReasons()
    {
        var missing = SimulationEndpoints.AcceptRebuildAsync(null);
        Assert.False(missing.Ok);
        Assert.Contains("Case is required", missing.Error);

        var blank = SimulationEndpoints.AcceptRebuildAsync(
            new DockRebuildViewsRequest("  ", null));
        Assert.False(blank.Ok);

        var unknown = SimulationEndpoints.AcceptRebuildAsync(
            new DockRebuildViewsRequest("CASE-1", [new DockProblemInput("CTN-A", "Melted")]));
        Assert.False(unknown.Ok);
        Assert.Contains("Unknown carton problem", unknown.Error);

        var cartonless = SimulationEndpoints.AcceptRebuildAsync(
            new DockRebuildViewsRequest("CASE-1", [new DockProblemInput("  ", DockProblemKinds.Crushed)]));
        Assert.False(cartonless.Ok);
        Assert.Contains("carton id", cartonless.Error);
        Assert.Empty(cartonless.Problems);
    }

    [Fact]
    public void RebuildEndpoint_AcceptsValidRequestForBackground()
    {
        var accepted = SimulationEndpoints.AcceptRebuildAsync(
            new DockRebuildViewsRequest(
                "CASE-1", [new DockProblemInput("CTN-A", DockProblemKinds.MissingMark)]));

        Assert.True(accepted.Ok, accepted.Error ?? string.Empty);
        Assert.Equal("CASE-1", accepted.CaseId);
        var problem = Assert.Single(accepted.Problems);
        Assert.Equal("CTN-A", problem.CartonId);
        Assert.Equal(DockProblemKinds.MissingMark, problem.Kind);
    }

    [Fact]
    public async Task AsnEndpoint_ProblemValidationFailureCarriesReason()
    {
        var dir = UiTestSupport.NewDir("gate-ui-dockasn");
        Directory.CreateDirectory(dir);
        var runner = RunnerFor(Path.Combine(dir, "case-cache"));

        var outcome = await SimulationEndpoints.AcceptSupplierAsnAsync(
            new SupplierAsnWithProblemsRequest(
                "CASE-1", "ASN-1", "PACK-1", [], null,
                [new DockProblemInput("CTN-A", "Melted")]),
            runner);

        Assert.False(outcome.Ok);
        Assert.Contains("Unknown carton problem", outcome.Error);

        var missing = await SimulationEndpoints.AcceptSupplierAsnAsync(null, runner);
        Assert.False(missing.Ok);
        Assert.Contains("Case is required", missing.Error);
    }

    [Fact]
    public async Task Asn_ProblemsSubmitWithShipmentNotice()
    {
        var h = Create();
        Respond(h, """{"asnNumber":"ASN-1","receiptNumber":"RCP-1","step":"Docked","complete":false}""");

        var outcome = await h.Service.SendAsnAsync(new AsnSubmission(
            "CASE-1", "ASN-1", "PACK-1",
            [
                new AsnCartonForm("CTN-A", "ITEM-1", 6, "LOT-A", DockProblemKinds.Crushed),
                new AsnCartonForm("CTN-B", "ITEM-1", 6, "LOT-B"),
            ]));

        Assert.True(outcome.Ok, outcome.Summary);
        var sent = SentAs<SupplierAsnWithProblemsRequest>(h);
        var problem = Assert.Single(sent.Problems ?? []);
        Assert.Equal("CTN-A", problem.CartonId);
        Assert.Equal(DockProblemKinds.Crushed, problem.Kind);
        var legacy = SentAs<SupplierAsnInput>(h);
        Assert.Equal(2, legacy.Cartons.Count);
    }

    [Fact]
    public async Task Asn_UnknownProblemRefusedBeforePost()
    {
        var h = Create();

        var outcome = await h.Service.SendAsnAsync(new AsnSubmission(
            "CASE-1", "ASN-1", "PACK-1",
            [new AsnCartonForm("CTN-A", "ITEM-1", 6, "LOT-A", "Melted")]));

        Assert.False(outcome.Ok);
        Assert.Contains("unknown carton problem", outcome.Summary);
        Assert.Equal(0, h.Http.CallCount);
    }

    [Fact]
    public void OutsideLabels_SanitizeRebuildPath()
    {
        var cleaned = OutsideWorldLabels.Sanitize($"POST {SimulationUiService.RebuildViewsPath} returned 400.");

        Assert.Contains(OutsideWorldLabels.RebuildFlow, cleaned);
        Assert.True(OutsideWorldLabels.IsClean(cleaned));
    }

    [Fact]
    public async Task Admin_RendersDockScenesSwitchOffWithLibraryStatusAndCredsNote()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);

        using var page = ctx.RenderComponent<Admin>();
        page.WaitForAssertion(
            () => Assert.Contains("Dock scenes", page.Markup),
            TimeSpan.FromSeconds(10));
        Assert.Contains("Build dock scenes live", page.Markup);
        Assert.Contains("Dock scenes: photo library.", page.Markup);
        Assert.Contains("image service", page.Markup);
        var scenes = ctx.Services.GetRequiredService<DockSceneLive>();
        Assert.False(scenes.LiveOn);

        page.Find("#scene-toggle").Change(true);

        Assert.True(scenes.LiveOn);
        // No generator is wired in the crawler world, so the source stays
        // the library even with the switch on.
        Assert.Contains("Dock scenes: photo library.", page.Markup);
        Assert.Contains("image service", page.Markup);
    }

    [Fact]
    public async Task Admin_RendersLiveStatusWhenGeneratorWiredAndSwitchOn()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        ctx.Services.AddSingleton<DockSceneStatus>(CredsStatus());
        var scenes = ctx.Services.GetRequiredService<DockSceneLive>();
        scenes.SceneGenerator = new StubSceneGenerator();
        scenes.LiveOn = true;

        using var page = ctx.RenderComponent<Admin>();
        page.WaitForAssertion(
            () => Assert.Contains("Dock scenes: live build on.", page.Markup),
            TimeSpan.FromSeconds(10));
        Assert.DoesNotContain("image service", page.Markup);
    }

    [Fact]
    public async Task Admin_RendersFallbackReasonInDockScenesStatus()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        ctx.Services.GetRequiredService<DockSceneLive>().RecordFallback("labeler none failed");

        using var page = ctx.RenderComponent<Admin>();
        page.WaitForAssertion(
            () => Assert.Contains("last run fell back to the library (labeler none failed)", page.Markup),
            TimeSpan.FromSeconds(10));
    }
}

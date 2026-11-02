// Blazor dispatcher-affinity regression tests: interactive components must
// await on the renderer's sync context (no ConfigureAwait(false) in .razor)
// and must consume service-raised events via InvokeAsync. On Blazor Server,
// awaiting with ConfigureAwait(false) drops the sync context, so the
// continuation touches state or triggers EventCallback renders off the
// Dispatcher: "The current thread is not associated with the Dispatcher".
// It bit SendOrderForm.SubmitAsync (the POST succeeded, then OnChanged
// crashed and the catch misreported failure).
//
// SendOrderThroughPageThenSwitchUser drives the real Outside page shop tab
// through bUnit (a test-only package ref, documented in Gate.Eval.csproj):
// it places an order through the page's free order form and then switches
// the acting user mid-run, asserting no dispatcher throw, the outcome
// renders, and Decisions filtering follows the new user. The stubbed POST
// yields asynchronously, like a live POST, so the old pattern genuinely
// leaves the sync context. RazorComponents_MustNotUseConfigureAwaitFalse
// pins the pattern itself across every .razor under Gate.Web.
//
// All data is local to this file; nothing depends on the demo numbers. No
// live calls.
using System.Net;
using System.Text;
using Bunit;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.Components.Pages;
using Gate.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

public sealed class BlazorDispatcherTests
{
    private const string OrderResponseJson = """
        {"caseIds":["CASE-D9"],"advances":[
         {"caseId":"CASE-D9","stage":"Created","step":"RaisePr","complete":false}]}
        """;

    // Stub simulation POST: succeeds, but only after a real async hop, the
    // way a live POST completes off the renderer's sync context. Without the
    // yield the whole chain could complete synchronously and the old
    // ConfigureAwait(false) pattern would never leave the Dispatcher.
    private sealed class YieldingStubHandler : HttpMessageHandler
    {
        public int CallCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(5).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(OrderResponseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record Harness(
        YieldingStubHandler Http,
        SimulationUiService Simulate,
        CaseViewService Views,
        DecisionOutcomeService Outcomes,
        AskWhyService Why,
        ReplayService Replay,
        ActingUserState Acting);

    private static async Task<Harness> CreateAsync()
    {
        var dir = UiTestSupport.NewDir("gate-ui-dispatch");
        Directory.CreateDirectory(dir);

        var fake = new InMemoryErpAdapter();
        fake.SeedBaseCurrency("TST");
        fake.SeedItem(new ItemDto("ITEM-D1", "Dispatch item", false, 1, "EA"));
        fake.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));
        fake.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        var erp = new CompanyInfoErpDouble(fake, new CompanyInfoDto(
            "LE-T", "Dispatch Company", "TST", "1 Bill St", "2 Ship St", "buy@dispatch.test"));

        var cacheDir = Path.Combine(dir, "case-cache");
        var cases = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        await cases.WriteAsync(UiTestSupport.CaseFileWith("CASE-D1", "SO-D1", "ITEM-D1", [
            new Decision(
                "D-D1", "CASE-D1", DecisionKind.PrApproval, "Alpha PR approval",
                DecisionStatus.Pending, "approver", now, null, null,
                "PR", "PR-TD1", 100m, "TST", null, null, [], null),
            new Decision(
                "D-D2", "CASE-D1", DecisionKind.PrApproval, "Beta PR approval",
                DecisionStatus.Pending, "requester", now, null, null,
                "PR", "PR-TD2", 100m, "TST", null, null, [], null),
        ]));

        var dockDir = Path.Combine(dir, "dock");
        var visionDir = Path.Combine(dir, "vision");
        Directory.CreateDirectory(dockDir);
        var photos = new DockPhotoService(dockDir, visionDir);

        var stub = new YieldingStubHandler();
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://localhost/") };
        var simulate = new SimulationUiService(
            http, erp, cases, photos, UiTestLogger<SimulationUiService>.Instance);
        var views = new CaseViewService(
            erp, cases, photos, UiTestLogger<CaseViewService>.Instance);
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var agent = new P2pAgent(erp, models, compliance);
        var runner = new CaseRunner(erp, agent, models, compliance, new DockEvaluatorAdapter(), cacheDir);
        var outcomes = new DecisionOutcomeService(
            cases, UiTestLogger<DecisionOutcomeService>.Instance, runner);
        var replay = new ReplayState();
        var why = new AskWhyService(
            agent, models, erp, cases, photos, replay, UiTestLogger<AskWhyService>.Instance,
            Path.Combine(dir, "model-cache"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var replaySvc = new ReplayService(
            cases, photos, replay, config, UiTestLogger<ReplayService>.Instance, dir);
        return new Harness(stub, simulate, views, outcomes, why, replaySvc, new ActingUserState());
    }

    [Fact]
    public async Task SendOrderThroughPageThenSwitchUser_RendersOutcomeAndRefilters()
    {
        var h = await CreateAsync();
        using var ctx = new TestContext();
        ctx.Services.AddSingleton<ILogger<Decisions>>(UiTestLogger<Decisions>.Instance);
        ctx.Services.AddSingleton(h.Simulate);
        ctx.Services.AddSingleton(h.Views);
        ctx.Services.AddSingleton(h.Outcomes);
        ctx.Services.AddSingleton(h.Why);
        ctx.Services.AddSingleton(h.Replay);
        ctx.Services.AddSingleton(h.Acting);

        // 1. Place an order through the Outside page shop tab. On the old
        // pattern the POST succeeds and then OnChanged crashes off the
        // Dispatcher, so the catch misreports failure instead of rendering
        // the outcome.
        using var page = ctx.RenderComponent<Outside>();
        page.WaitForAssertion(() => page.Find("#shop-new-currency"), TimeSpan.FromSeconds(10));
        var selects = page.FindAll("#shop select");
        Assert.True(selects.Count >= 2);
        selects[1].Change("ITEM-D1");
        page.Find("#shop input[type=number]").Change("5");
        page.Find("#shop button.gate-btn-primary").Click();
        page.WaitForAssertion(
            () => Assert.Contains("Order accepted: 1 case(s).", page.Markup),
            TimeSpan.FromSeconds(10));
        Assert.Equal(1, h.Http.CallCount);
        Assert.Contains("Open CASE-D9", page.Markup);
        Assert.DoesNotContain("not associated with the Dispatcher", page.Markup);

        // 2. Decisions starts filtered to the default acting user.
        using var board = ctx.RenderComponent<Decisions>();
        board.WaitForAssertion(
            () => Assert.Contains("Pending for approver", board.Markup),
            TimeSpan.FromSeconds(10));
        Assert.Contains("Alpha PR approval", board.Markup);
        Assert.DoesNotContain("Beta PR approval", board.Markup);

        // 3. Switch the acting user mid-run, as the switcher does, and the
        // board re-filters without a dispatcher throw.
        h.Acting.SetCurrent("requester");
        board.WaitForAssertion(
            () => Assert.Contains("Pending for requester", board.Markup),
            TimeSpan.FromSeconds(10));
        Assert.Contains("Beta PR approval", board.Markup);
        Assert.DoesNotContain("Alpha PR approval", board.Markup);
    }

    [Fact]
    public void RazorComponents_MustNotUseConfigureAwaitFalse()
    {
        var root = FindRepoRoot();
        var offenders = Directory
            .GetFiles(Path.Combine(root, "Gate.Web"), "*.razor", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("ConfigureAwait(false)", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.True(
            offenders.Count == 0,
            "Blazor components must keep the renderer sync context — no ConfigureAwait(false) in .razor. "
            + $"Offenders: {(offenders.Count == 0 ? "none" : string.Join(", ", offenders))}. "
            + "Services (.cs) keep ConfigureAwait(false); only component files are scanned.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Gate.Web")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (no directory containing Gate.Web found above "
            + AppContext.BaseDirectory + ").");
    }
}

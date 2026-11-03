// UI crawler: renders every list plus every linked record as every user
// and fails on any error page. Lists are the Decisions board, Cases, the
// ERP lists, the Governance pages, Search, Admin, and the outside world;
// linked records are every internal href the lists render (ERP records,
// case details, KB spaces and entries). bUnit renders the real Blazor
// components over the fake ERP plus fixture KB and memos with test-local
// data only. No live calls.
using System.Net;
using System.Text.RegularExpressions;
using Bunit;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Eval;
using Gate.Web.Components;
using Gate.Web.Components.Pages;
using Gate.Web.Components.Pages.Erp;
using Gate.Web.Components.Pages.Governance;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gate.Eval.Ui;

public sealed class CrawlerTests
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

    internal sealed record World(
        string Dir,
        string CacheDir,
        CompanyInfoErpDouble Erp,
        CaseCacheStore Cases,
        DockPhotoService Photos,
        CaseViewService Views,
        ErpViewService ErpViews,
        ErpActionService ErpActions,
        DecisionOutcomeService Outcomes,
        ReplayService Replay,
        SimulationUiService Simulate,
        GovernanceViewService GovViews,
        KbViewService KbViews,
        KbEditService KbEdit,
        MemoFileStore Memos,
        PolicyViewService PolicyViews,
        PolicyOutcomeService PolicyOutcomes,
        MemoSubmitService MemoSubmit,
        ReplayState ReplayState,
        ModelSpendTracker Spend,
        GovernanceFakeEngine Engine,
        List<string> CaseIds);

    internal static async Task<World> CreateWorldAsync()
    {
        var dir = UiTestSupport.NewDir("gate-ui-crawl");
        Directory.CreateDirectory(dir);
        var fake = new InMemoryErpAdapter();
        fake.SeedBaseCurrency("TST");
        fake.SeedAutoApproveThreshold(10m);
        fake.SeedPaymentTerms(new PaymentTermsDto("NET0", 0));
        fake.SeedItem(new ItemDto("ITEM-C", "Crawl item", false, 5, "EA"));
        fake.SeedStock("ITEM-C", 50m);
        fake.SeedVendor(new VendorDto("V-C", "Crawl Vendor", "US", false, null, "NET0", "ap@crawl.test"));
        fake.SeedVendorBank(new VendorBankDto("V-C", "BANK-C", true, DateTimeOffset.UtcNow));
        fake.SeedVendorPrice("V-C", "ITEM-C", 10m);
        fake.SeedUser(new UserDto("requester", "requester", 50m, "TST"));
        fake.SeedUser(new UserDto("approver", "approver", 1_000_000m, "TST"));
        fake.SeedUser(new UserDto("finance", "finance", 1_000_000m, "TST"));
        fake.SeedUser(new UserDto("ap-clerk", "ap-clerk", 1_000_000m, "TST"));
        var erp = new CompanyInfoErpDouble(fake, new CompanyInfoDto(
            "LE-T", "Crawl Company", "TST", "1 Bill St", "2 Ship St", "buy@crawl.test"));

        // One full chain: approved PR -> PO -> ASN -> receipt -> invoice ->
        // payment draft, plus a submitted PR for the pending-approvals
        // list and a hold plus a sales order for their lists.
        var pr = await fake.CreatePrAsync(
            "requester", RecordSource.Human, "requester",
            [new PurchaseRequisitionLine("ITEM-C", 5m, "EA")], Guid.NewGuid().ToString("N"));
        await fake.SubmitForApprovalAsync("PR", pr.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await fake.RecordApprovalAsync(
            "PR", pr.PrNumber, "approver", true, "crawl approval", Guid.NewGuid().ToString("N"));
        var po = await fake.CreatePoAsync(
            pr.PrNumber, "V-C", RecordSource.Human, "requester", Guid.NewGuid().ToString("N"));
        await fake.SubmitForApprovalAsync("PO", po.PoNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await fake.RecordApprovalAsync(
            "PO", po.PoNumber, "approver", true, "crawl approval", Guid.NewGuid().ToString("N"));
        var poLine = po.Lines.Single().PoLineRef;
        await fake.RecordVendorConfirmationAsync(po.PoNumber, DateOnly.FromDateTime(DateTime.UtcNow),
            RecordSource.Vendor, "V-C",
            [new VendorConfirmLine(poLine, 5m, null)], Guid.NewGuid().ToString("N"));
        var asn = await fake.CreateAsnAsync(
            "ASN-C", po.PoNumber, RecordSource.Vendor, "V-C",
            [new AsnCarton("CTN-C1", "ITEM-C", 5, "CTN-C1-LOT")], Guid.NewGuid().ToString("N"));
        await fake.PostReceiptAsync(
            po.PoNumber, "PACK-C", RecordSource.Human, "requester",
            [new ReceiptLine(poLine, 5m, 0m, 0m, "CTN-C1-LOT", null)],
            Guid.NewGuid().ToString("N"));
        var inv = await fake.CreateInvoiceAsync(
            "INV-C", "V-C", po.PoNumber, RecordSource.Human, "ap-clerk",
            [new InvoiceLineInput(poLine, 5m, 10m)],
            Guid.NewGuid().ToString("N"));
        await fake.RecordMatchResultAsync("V-C", "INV-C",
            [new LineMatchResult(poLine, true, "matched")], Guid.NewGuid().ToString("N"));
        await fake.SubmitForApprovalAsync(
            "Invoice", "V-C/INV-C", "approver", "ap-clerk", Guid.NewGuid().ToString("N"));
        await fake.RecordApprovalAsync(
            "Invoice", "V-C/INV-C", "approver", true, "crawl approval", Guid.NewGuid().ToString("N"));
        await fake.PostInvoiceAsync("V-C", "INV-C", "ap-clerk", Guid.NewGuid().ToString("N"));
        await fake.CreatePaymentDraftAsync(
            [new InvoiceKey("V-C", inv.SupplierInvoiceNumber)],
            RecordSource.Human, "requester", Guid.NewGuid().ToString("N"));
        var prPending = await fake.CreatePrAsync(
            "requester", RecordSource.Human, "requester",
            [new PurchaseRequisitionLine("ITEM-C", 2m, "EA")], Guid.NewGuid().ToString("N"));
        await fake.SubmitForApprovalAsync(
            "PR", prPending.PrNumber, "approver", "requester", Guid.NewGuid().ToString("N"));
        await fake.CreateSalesOrderAsync(
            "TST", [new SalesOrderLine("ITEM-C", 5m)],
            RecordSource.Human, "requester", Guid.NewGuid().ToString("N"));
        await fake.SetHoldAsync(
            "REVIEW", "crawl hold", "CORP-T", "CASE-C1", "approver",
            "PO", po.PoNumber, null, null, null, Guid.NewGuid().ToString("N"));

        var cacheDir = Path.Combine(dir, "case-cache");
        var cases = new CaseCacheStore(cacheDir);
        var now = DateTimeOffset.UtcNow;
        await cases.WriteAsync(new CaseFile(
            new Case("CASE-C1", "SO-C1", "ITEM-C", "LE-T", CaseStage.PoPlaced, now, now,
                pr.PrNumber, po.PoNumber, asn.AsnNumber, null,
                [new InvoiceKey("V-C", inv.SupplierInvoiceNumber)], [], null, null),
            [new CaseEvent("e1", "CASE-C1", now, RecordSource.P2pAgent, CaseEventKind.PoCreated,
                $"PO {po.PoNumber} placed.", null, "PO", po.PoNumber, null, 50m, "TST")],
            [
                new Decision("D-C1", "CASE-C1", DecisionKind.PoApproval, "Approve crawl PO",
                    DecisionStatus.Pending, "approver", now, null, null,
                    "PO", po.PoNumber, 50m, "TST", null, "Crawl approval.", [], null),
            ]));
        await cases.WriteAsync(UiTestSupport.CaseFileWith("CASE-C2", "SO-C2", "ITEM-C", [
            UiTestSupport.PendingDecision("D-C2", "CASE-C2"),
        ]));
        CaseStore.SaveDock(cacheDir, new DockEvidence(
            "CASE-C1", [],
            new Dictionary<string, IReadOnlyList<Verdict>>
            {
                ["CTN-C1"] = [new Verdict("DOCK-T", VerdictKind.Pass, "ok", RecordSource.P2pAgent)],
            },
            now));

        var dockDir = Path.Combine(dir, "dock");
        Directory.CreateDirectory(dockDir);
        var photos = new DockPhotoService(dockDir, Path.Combine(dir, "vision"));
        var views = new CaseViewService(erp, cases, photos, UiTestLogger<CaseViewService>.Instance);
        var erpViews = new ErpViewService(erp, views, UiTestLogger<ErpViewService>.Instance);
        var models = new FakeModelClient();
        var compliance = new RecordingComplianceEngine();
        var agent = new P2pAgent(erp, models, compliance);
        var runner = new CaseRunner(erp, agent, models, compliance, new DockEvaluatorAdapter(), cacheDir);
        var outcomes = new DecisionOutcomeService(
            cases, UiTestLogger<DecisionOutcomeService>.Instance, runner);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Baseline:Dir"] = Path.Combine(dir, "baseline"),
                ["Erp:Sqlite"] = Path.Combine(dir, "live", "erp.db"),
            })
            .Build();
        var replayState = new ReplayState();
        var replay = new ReplayService(
            cases, photos, replayState, config,
            UiTestLogger<ReplayService>.Instance, dir, new ModelSpendTracker());
        var stub = new StubHandler();
        var simulate = new SimulationUiService(
            new HttpClient(stub) { BaseAddress = new Uri("http://localhost/") },
            erp, cases, photos, UiTestLogger<SimulationUiService>.Instance);

        var kit = MemoTestKit.Create();
        var memos = new MemoFileStore(kit.CacheDir);
        var engine = new GovernanceFakeEngine(MemoFixtures.TestKb(
            finance:
            [
                new KbWikiEntry(
                    "w-c1", "Crawl wiki", "Crawl body.", ["crawl"], "kbv9",
                    "approver", MemoFixtures.FixedAt, "approver", MemoFixtures.FixedAt,
                    "Terry Owner", "treasury", KbEntryStatus.Active, new DateOnly(2026, 11, 1)),
            ]));
        engine.SnapshotsByVersion["kbv1"] = MemoFixtures.TestKb(version: "kbv1", hash: "hash1");
        engine.Spaces.Add(new KbSpace("treasury", "Terry Owner", "treasurer", ["finance"], []));
        engine.LiveRules.Add(new LivePackRule(
            "PACK-T", "v9", "CORP-T", null, RuleTier.Tier2, MemoRuleGate.Vendor,
            "Crawl rule", true, "Terry Owner", new DateOnly(2026, 3, 16), "Fixture pack source."));
        var govViews = new GovernanceViewService(
            engine, cases, memos, UiTestLogger<GovernanceViewService>.Instance);
        var kbViews = new KbViewService(engine, UiTestLogger<KbViewService>.Instance);
        var kbEdit = new KbEditService(engine, erp, UiTestLogger<KbEditService>.Instance);
        var policyViews = new PolicyViewService(memos, UiTestLogger<PolicyViewService>.Instance);
        var policyOutcomes = new PolicyOutcomeService(
            memos, kit.Runner, UiTestLogger<PolicyOutcomeService>.Instance);
        var memoSubmit = new MemoSubmitService(
            kit.Runner, memos, kit.InboxDir, UiTestLogger<MemoSubmitService>.Instance);

        return new World(
            dir, cacheDir, erp, cases, photos, views, erpViews,
            new ErpActionService(erp, runner, UiTestLogger<ErpActionService>.Instance),
            outcomes, replay, simulate, govViews, kbViews, kbEdit, memos,
            policyViews, policyOutcomes, memoSubmit, replayState, new ModelSpendTracker(),
            engine, ["CASE-C1", "CASE-C2"]);
    }

    internal static void Register(World w, TestContext ctx, ActingUserState acting)
    {
        ctx.Services.AddSingleton(w.Views);
        ctx.Services.AddSingleton(w.ErpViews);
        ctx.Services.AddSingleton(w.ErpActions);
        ctx.Services.AddSingleton(w.Outcomes);
        ctx.Services.AddSingleton(w.Replay);
        ctx.Services.AddSingleton(w.Simulate);
        ctx.Services.AddSingleton(w.GovViews);
        ctx.Services.AddSingleton(w.KbViews);
        ctx.Services.AddSingleton(w.KbEdit);
        ctx.Services.AddSingleton(w.Memos);
        ctx.Services.AddSingleton(w.PolicyViews);
        ctx.Services.AddSingleton(w.PolicyOutcomes);
        ctx.Services.AddSingleton(w.MemoSubmit);
        ctx.Services.AddSingleton(w.ReplayState);
        ctx.Services.AddSingleton(w.Spend);
        ctx.Services.AddSingleton(w.Cases);
        ctx.Services.AddSingleton(new PresentationPace());
        ctx.Services.AddSingleton(new SplitProposals());
        ctx.Services.AddSingleton(new DockSceneLive());
        ctx.Services.AddSingleton(new DockSceneStatus(
            new ConfigurationBuilder().Build()));
        ctx.Services.AddSingleton(acting);
        var models = new FakeModelClient();
        var agent = new P2pAgent(w.Erp, models, new RecordingComplianceEngine());
        ctx.Services.AddSingleton(new AskWhyService(
            agent, models, w.Erp, w.Cases, w.Photos, new ReplayState(),
            UiTestLogger<AskWhyService>.Instance, Path.Combine(w.Dir, "model-cache")));
        ctx.Services.AddSingleton<ILogger<Decisions>>(UiTestLogger<Decisions>.Instance);
        ctx.Services.AddSingleton<ILogger<Cases>>(UiTestLogger<Cases>.Instance);
        ctx.Services.AddSingleton<ILogger<CaseDetail>>(UiTestLogger<CaseDetail>.Instance);
        ctx.Services.AddSingleton<ILogger<ErpRecord>>(UiTestLogger<ErpRecord>.Instance);
        ctx.Services.AddSingleton<ILogger<Search>>(UiTestLogger<Search>.Instance);
        ctx.Services.AddSingleton<ILogger<Admin>>(UiTestLogger<Admin>.Instance);
        ctx.Services.AddSingleton<ILogger<Outside>>(UiTestLogger<Outside>.Instance);
        ctx.Services.AddSingleton<ILogger<GovernanceRules>>(UiTestLogger<GovernanceRules>.Instance);
        ctx.Services.AddSingleton<ILogger<GovernanceMemos>>(UiTestLogger<GovernanceMemos>.Instance);
        ctx.Services.AddSingleton<ILogger<GovernanceExceptions>>(UiTestLogger<GovernanceExceptions>.Instance);
        ctx.Services.AddSingleton<ILogger<GovernanceVersions>>(UiTestLogger<GovernanceVersions>.Instance);
        ctx.Services.AddSingleton<ILogger<GovernanceKbSpace>>(UiTestLogger<GovernanceKbSpace>.Instance);
        ctx.Services.AddSingleton<ILogger<GovernanceKbEntry>>(UiTestLogger<GovernanceKbEntry>.Instance);
    }

    private static void AssertClean(string markup, string what)
    {
        Assert.DoesNotContain("gate-notice-error", markup);
        Assert.DoesNotContain("failed to load", markup);
        Assert.DoesNotContain("Unknown list", markup);
        Assert.DoesNotContain("Unknown ERP record kind", markup);
        Assert.DoesNotContain("was not found", markup);
        Assert.DoesNotContain("not associated with the Dispatcher", markup);
    }

    private static string RenderClean<T>(TestContext ctx, string what) where T : IComponent
    {
        using var page = ctx.RenderComponent<T>();
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        AssertClean(page.Markup, what);
        return page.Markup;
    }

    private static string RenderClean<T>(
        TestContext ctx, string what, Action<ComponentParameterCollectionBuilder<T>> parameters)
        where T : IComponent
    {
        using var page = ctx.RenderComponent<T>(parameters);
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        AssertClean(page.Markup, what);
        return page.Markup;
    }

    private static readonly Regex HrefPattern = new(
        "href=\"(/[^\"]*|cases/[^\"]*|erp/[^\"]*|governance/[^\"]*|search[^\"]*|admin|outside)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static List<string> InternalHrefs(string markup) =>
        HrefPattern.Matches(markup)
            .Select(m => m.Groups[1].Value.TrimStart('/'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private void VisitHref(TestContext ctx, World w, string href, List<string> visited)
    {
        if (visited.Contains(href, StringComparer.Ordinal))
        {
            return;
        }

        visited.Add(href);
        var segments = href.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is ["erp", "record", var kind, var key])
        {
            RenderClean<ErpRecord>(ctx, href, p => p
                .Add(r => r.Kind, kind)
                .Add(r => r.Key, key));
        }
        else if (segments is ["erp", "record", "invoice", var vendor, var number])
        {
            // Real route binding leaves Kind/Key null on the invoice
            // route (Blazor overwrites their initializers with
            // default(string) for unbound parameters): the render must
            // pass explicit nulls, not empty strings.
            RenderClean<ErpRecord>(ctx, href, p => p
                .Add(r => r.Kind, (string)null!)
                .Add(r => r.Key, (string)null!)
                .Add(r => r.Vendor, vendor)
                .Add(r => r.Number, number));
        }
        else if (segments is ["erp", "new", var newKind])
        {
            RenderClean<ErpNew>(ctx, href, p => p.Add(n => n.Kind, newKind));
        }
        else if (segments is ["erp", "new", var newKindPreset, var preset])
        {
            RenderClean<ErpNew>(ctx, href, p => p
                .Add(n => n.Kind, newKindPreset)
                .Add(n => n.Preset, preset));
        }
        else if (segments is ["cases", var caseId])
        {
            RenderClean<CaseDetail>(ctx, href, p => p.Add(c => c.CaseId, caseId));
        }
        else if (segments is ["governance", "kb", var space] && !space.StartsWith("kbv", StringComparison.Ordinal))
        {
            RenderClean<GovernanceKbSpace>(ctx, href, p => p.Add(k => k.Space, space));
        }
        else if (segments is ["governance", "kb", var version, var section, var entry])
        {
            RenderClean<GovernanceKbEntry>(ctx, href, p => p
                .Add(k => k.Version, version)
                .Add(k => k.Section, section)
                .Add(k => k.EntryId, entry));
        }
        else if (href.StartsWith("search", StringComparison.Ordinal))
        {
            RenderClean<Search>(ctx, href, p => p.Add(s => s.Q, "V-C"));
        }
        // Lists, index redirects, and static assets need no revisit: the
        // crawl renders every list directly.
    }

    [Theory]
    [InlineData("requester")]
    [InlineData("approver")]
    [InlineData("ap-clerk")]
    public async Task CrawlEveryListAndLinkedRecord(string userId)
    {
        var w = await CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent(userId);
        using var ctx = new TestContext();
        Register(w, ctx, acting);
        var markup = new List<string>();
        var visited = new List<string>();

        markup.Add(RenderClean<Decisions>(ctx, "decisions"));
        markup.Add(RenderClean<Cases>(ctx, "cases"));
        foreach (var caseId in w.CaseIds)
        {
            markup.Add(RenderClean<CaseDetail>(ctx, $"cases/{caseId}", p => p.Add(c => c.CaseId, caseId)));
        }

        foreach (var module in ErpModules.All)
        {
            foreach (var link in module.Lists)
            {
                markup.Add(RenderClean<ErpList>(ctx, $"erp/list/{link.Slug}", p => p.Add(l => l.Slug, link.Slug)));
            }
        }

        markup.Add(RenderClean<GovernanceRules>(ctx, "governance/rules"));
        markup.Add(RenderClean<GovernanceMemos>(ctx, "governance/memos"));
        markup.Add(RenderClean<GovernanceExceptions>(ctx, "governance/exceptions"));
        markup.Add(RenderClean<GovernanceVersions>(ctx, "governance/versions"));
        foreach (var space in w.Engine.Spaces)
        {
            markup.Add(RenderClean<GovernanceKbSpace>(ctx, $"governance/kb/{space.Space}",
                p => p.Add(k => k.Space, space.Space)));
        }

        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("search?q=V-C");
        markup.Add(RenderClean<Search>(ctx, "search?q=V-C"));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("search");
        markup.Add(RenderClean<Search>(ctx, "search"));
        markup.Add(RenderClean<Admin>(ctx, "admin"));
        markup.Add(RenderClean<Outside>(ctx, "outside"));
        markup.Add(RenderClean<EntityHeader>(ctx, "entity-header"));

        var hrefs = markup.SelectMany(InternalHrefs).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(hrefs);
        foreach (var href in hrefs)
        {
            VisitHref(ctx, w, href, visited);
        }

        // Every seeded document is reachable through some link.
        var joined = string.Join("\n", markup);
        foreach (var marker in new[] {"PR-", "PO-", "ASN-C", "RCT-", "INV-C", "PAYDRAFT-", "V-C", "ITEM-C", "CASE-C"})
        {
            Assert.Contains(marker, joined);
        }
    }
}

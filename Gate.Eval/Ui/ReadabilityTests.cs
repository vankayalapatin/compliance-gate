// Readability tests: every rendered page shows plain operator language —
// local-time stamps (never a trailing Z), plain enum words (never raw
// member names), formed plurals (never "(s)"), grouped money with two
// decimals, vendor names on AP lists, and labeled buttons. Pages render
// over the crawler's fixture world, so failures name the page. No live
// calls.
using System.Text.RegularExpressions;
using Bunit;
using Gate.Contracts;
using Gate.Web.Components;
using Gate.Web.Components.Outside;
using Gate.Web.Components.Pages;
using Gate.Web.Components.Pages.Erp;
using Gate.Web.Components.Pages.Governance;
using Gate.Web.Services;
using Gate.Web.ViewModels;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Gate.Eval.Ui;

public sealed class ReadabilityTests
{
    private static readonly (Regex Pattern, string Name)[] Banned =
    [
        (new Regex(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}(:\d{2})?Z", RegexOptions.CultureInvariant), "trailing-Z stamp"),
        (new Regex(@"\bPartially(Paid|Received|Invoiced|Delivered)\b", RegexOptions.CultureInvariant), "Partially* enum"),
        (new Regex(@"\bInReview\b", RegexOptions.CultureInvariant), "InReview enum"),
        (new Regex(@"\b(Pr|Po|Invoice|Payment)Approval\b", RegexOptions.CultureInvariant), "approval-kind enum"),
        (new Regex(@"\b(ApReview|BankVerification|DockReview|DamageReview|ExceptionApproval|VendorIdentity|PolicyApproval|PolicyQuestion)\b", RegexOptions.CultureInvariant), "review-kind enum"),
        (new Regex(@"\bAwait(SupplierAck|Asn|DockReads|Invoices|DueDate|VendorReply|Decision)\b", RegexOptions.CultureInvariant), "await-step enum"),
        (new Regex(@"\b(ComputeShortfall|RaisePr|ScreenVendors|AwardSupplier|CreatePo|SubmitPoApproval|DraftPayment|NotifyCustomer|QueryVendor|FinishCase)\b", RegexOptions.CultureInvariant), "agent-step enum"),
        (new Regex(@"\b(ShortfallComputed|PrSubmitted|PrApproved|VendorScreened|PoPlaced|PoAcknowledged|AsnRecorded|ReceiptPosted|DockChecked|InvoiceMatched|PaymentRequested|PaymentPartial)\b", RegexOptions.CultureInvariant), "stage enum"),
        (new Regex(@"\b(OrderReceived|PrCreated|PrSubmitted|PrApproved|PrRejected|PoCreated|PoApproved|VendorAcknowledged|AsnRecorded|ReceiptPosted|DockEvaluated|HoldSet|HoldReleased|ReviewOpened|ReviewClosed|InvoiceCreated|InvoiceMatched|PaymentRequested|PaymentPosted|CustomerNotified|VendorEmailed|VendorReplied|SupplierAwarded|VendorBlocked|AgentRefused)\b", RegexOptions.CultureInvariant), "event-kind enum"),
        (new Regex(@"\bHumanReview\b", RegexOptions.CultureInvariant), "HumanReview enum"),
        (new Regex(@"\b(ProspectiveOnly|ProspectiveWithReview|ImmediateHold)\b", RegexOptions.CultureInvariant), "applicability enum"),
        (new Regex(@"\bTier[123]\b", RegexOptions.CultureInvariant), "tier enum"),
        (new Regex(@"p2p-agent", RegexOptions.CultureInvariant), "p2p-agent id"),
        (new Regex(@"p2pRelevant=", RegexOptions.CultureInvariant), "p2pRelevant flag"),
        (new Regex(@"\(s\)", RegexOptions.CultureInvariant), "(s) code"),
        (new Regex(@"\b\d+\.\d(?![\d%])", RegexOptions.CultureInvariant), "one-decimal number"),
        (new Regex(@"\b\d{5,}\.\d+", RegexOptions.CultureInvariant), "ungrouped thousands"),
        (new Regex(@"-d\d{3}\b", RegexOptions.CultureInvariant), "decision id"),
    ];

    private static readonly Regex TagStripper = new("<[^>]*>", RegexOptions.Compiled);

    private static readonly Regex EmptyButton = new(
        @"<button\b[^>]*>\s*</button>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string VisibleText(string markup) =>
        TagStripper.Replace(markup, " ");

    private static void AssertReadable(string text, string what)
    {
        foreach (var (pattern, name) in Banned)
        {
            var hit = pattern.Match(text);
            Assert.False(hit.Success, $"{what} shows unreadable '{name}': '...{Clip(text, hit.Index)}...'.");
        }
    }

    private static string Clip(string text, int index)
    {
        var start = Math.Max(0, index - 40);
        var length = Math.Min(80, text.Length - start);
        return length <= 0 ? string.Empty : text.Substring(start, length);
    }

    private static string Render<T>(TestContext ctx) where T : IComponent
    {
        using var page = ctx.RenderComponent<T>();
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        return page.Markup;
    }

    private static string Render<T>(
        TestContext ctx, Action<ComponentParameterCollectionBuilder<T>> parameters)
        where T : IComponent
    {
        using var page = ctx.RenderComponent<T>(parameters);
        page.WaitForAssertion(
            () => Assert.DoesNotContain("Loading", page.Markup),
            TimeSpan.FromSeconds(10));
        return page.Markup;
    }

    [Fact]
    public async Task RenderedPagesShowPlainReadableText()
    {
        var w = await CrawlerTests.CreateWorldAsync();
        var acting = new ActingUserState();
        acting.SetCurrent("approver");
        using var ctx = new TestContext();
        CrawlerTests.Register(w, ctx, acting);
        var pages = new Dictionary<string, string>(StringComparer.Ordinal);

        pages["decisions"] = Render<Decisions>(ctx);
        pages["cases"] = Render<Cases>(ctx);
        foreach (var caseId in w.CaseIds)
        {
            pages[$"cases/{caseId}"] = Render<CaseDetail>(ctx, p => p.Add(c => c.CaseId, caseId));
        }

        var slugs = ErpModules.All.SelectMany(m => m.Lists).Select(l => l.Slug).ToList();
        foreach (var slug in slugs)
        {
            pages[$"erp/list/{slug}"] = Render<ErpList>(ctx, p => p.Add(l => l.Slug, slug));
        }

        foreach (var kind in new[] { "so", "pr", "po", "invoice", "payment", "receipt", "vendor", "item" })
        {
            pages[$"erp/new/{kind}"] = Render<ErpNew>(ctx, p => p.Add(n => n.Kind, kind));
        }

        var pos = await w.ErpViews.ListPurchaseOrdersAsync();
        var poNumber = pos.Rows.Single().PoNumber;
        var prs = await w.ErpViews.ListPurchaseRequisitionsAsync();
        var pendingPr = prs.Rows.First(r => r.Status == PrStatus.Submitted);
        pages["erp/record/pr"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "pr").Add(r => r.Key, pendingPr.PrNumber));
        pages["erp/record/po"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "po").Add(r => r.Key, poNumber));
        pages["erp/record/asn"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "asn").Add(r => r.Key, "ASN-C"));
        var receipts = await w.ErpViews.ListReceiptsAsync();
        pages["erp/record/receipt"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "receipt").Add(r => r.Key, receipts.Rows.Single().ReceiptNumber));
        pages["erp/record/invoice"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, (string)null!).Add(r => r.Key, (string)null!)
            .Add(r => r.Vendor, "V-C").Add(r => r.Number, "INV-C"));
        var payments = await w.ErpViews.ListPaymentsAsync();
        pages["erp/record/payment"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "payment").Add(r => r.Key, payments.Rows.Single().PaymentNumber));
        pages["erp/record/vendor"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "vendor").Add(r => r.Key, "V-C"));
        pages["erp/record/item"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "item").Add(r => r.Key, "ITEM-C"));
        pages["erp/record/stock"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "stock").Add(r => r.Key, "ITEM-C"));
        var sos = await w.ErpViews.ListSalesOrdersAsync();
        pages["erp/record/so"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "so").Add(r => r.Key, sos.Rows.Single().SoNumber));
        var holds = await w.ErpViews.ListHoldsAsync();
        pages["erp/record/hold"] = Render<ErpRecord>(ctx, p => p
            .Add(r => r.Kind, "hold").Add(r => r.Key, holds.Rows.Single().HoldId));

        pages["governance/rules"] = Render<GovernanceRules>(ctx);
        pages["governance/memos"] = Render<GovernanceMemos>(ctx);
        pages["governance/exceptions"] = Render<GovernanceExceptions>(ctx);
        pages["governance/versions"] = Render<GovernanceVersions>(ctx);
        pages["governance/kb/treasury"] = Render<GovernanceKbSpace>(ctx, p => p.Add(k => k.Space, "treasury"));
        pages["governance/kb/kbv9/finance/w-c1"] = Render<GovernanceKbEntry>(ctx, p => p
            .Add(k => k.Version, "kbv9").Add(k => k.Section, "finance").Add(k => k.EntryId, "w-c1"));

        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("search?q=V-C");
        pages["search"] = Render<Search>(ctx);
        pages["admin"] = Render<Admin>(ctx);
        pages["outside"] = Render<Outside>(ctx);
        pages["entity-header"] = Render<EntityHeader>(ctx);

        var options = await w.Simulate.GetOptionsAsync();
        pages["shop"] = Render<ShopPanel>(ctx, p => p.Add(s => s.Options, options));
        pages["dock"] = Render<DockPanel>(ctx, p => p.Add(d => d.Options, options));
        pages["supplier"] = Render<SupplierPortal>(ctx, p => p
            .Add(s => s.VendorId, "V-C").Add(s => s.Options, options));

        Assert.True(pages.Count > 35, $"Expected broad page coverage, got {pages.Count}.");
        foreach (var (name, markup) in pages)
        {
            AssertReadable(VisibleText(markup), name);
            Assert.False(EmptyButton.IsMatch(markup), $"{name} renders an empty button.");
        }

        // Vendor names (not just ids) on the AP lists.
        Assert.Contains("Crawl Vendor", pages["erp/list/invoice"]);
        Assert.Contains("Crawl Vendor", pages["erp/list/payment"]);

        // The New button names what it creates.
        Assert.Contains("New invoice", pages["erp/list/invoice"]);
        Assert.Contains("New payment", pages["erp/list/payment"]);
    }
}

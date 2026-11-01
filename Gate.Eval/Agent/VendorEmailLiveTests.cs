// Live vendor-email loop: Spark drives a battery case, chooses
// send_vendor_email for a held unmatchable-PO invoice, the simulated vendor
// reply re-enters intake with corrected fields, an early pay run parks on
// the run date, and a later run pays everything to Resolved. Prints the
// [mail-live] run log; the mail file plus transcript carry the full text.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;
using Microsoft.Extensions.Configuration;
using System.Globalization;
using Xunit.Abstractions;

namespace Gate.Eval;

public sealed class VendorEmailLiveTests
{
    private readonly ITestOutputHelper _output;

    public VendorEmailLiveTests(ITestOutputHelper output) => _output = output;

    private sealed record LiveSecrets(string ApiKey, string Model, string BaseUrl);

    private static LiveSecrets Secrets()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets("compliancegate-gate-web")
            .AddEnvironmentVariables()
            .Build();
        var apiKey = config["MetaApi:ApiKey"];
        var model = config["MetaApi:Model"];
        var baseUrl = config["MetaApi:BaseUrl"];
        Assert.False(string.IsNullOrWhiteSpace(apiKey), "MetaApi:ApiKey is missing.");
        Assert.False(string.IsNullOrWhiteSpace(model), "MetaApi:Model is missing.");
        return new LiveSecrets(
            apiKey!, model!,
            string.IsNullOrWhiteSpace(baseUrl) ? "https://api.llama.com/v1" : baseUrl);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [SeededErp.Battery] = ["V-A", "V-B"],
            [SeededErp.Screw] = ["V-C"]
        };

    private static CaseFile Load(string caseDir, string caseId) =>
        CaseStore.Load(caseDir, caseId) ?? throw new InvalidOperationException($"Case {caseId} vanished.");

    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveSparkEmailsVendorActsOnReplyAndPaysToDate()
    {
        var secrets = Secrets();
        using var http = new HttpClient();
        var spark = new SparkModelClient(http, new SparkOptions
        {
            BaseUrl = secrets.BaseUrl,
            Model = secrets.Model,
            ApiKey = secrets.ApiKey,
        });
        var modelCache = Path.Combine(Path.GetTempPath(), "gate-maillive", Guid.NewGuid().ToString("N"));
        var caseDir = Path.Combine(Path.GetTempPath(), "gate-maillive-cases", Guid.NewGuid().ToString("N"));
        var erp = SeededErp.Create();
        var models = new CachedModelClient(spark, modelCache);
        var compliance = new RecordingComplianceEngine();
        var runner = new CaseRunner(erp, new P2pAgent(erp, models, compliance), models,
            compliance, new DockEvaluatorAdapter(), caseDir);
        _output.WriteLine($"[mail-live] case dir: {caseDir}");

        var vendor = (await erp.GetVendorsAsync()).Single(v => v.VendorId == "V-B");
        erp.SeedVendor(vendor with { Email = "sales@cells.example" });

        var cases = await runner.OnOrderWebhookAsync(
            new OrderWebhookInput("test", "SO-1001", "planner", Candidates()));
        var caseId = cases.Single(c => c.EndsWith(SeededErp.Battery, StringComparison.Ordinal));

        await runner.AdvanceAsync(caseId);
        var raised = Load(caseDir, caseId);
        var pr = raised.Decisions.Single(d => d.Kind == DecisionKind.PrApproval);
        await runner.ResolveDecisionAsync(caseId, pr.DecisionId, true, pr.RequestedTo, "ok");
        await runner.AdvanceAsync(caseId);
        var ordered = Load(caseDir, caseId);
        var poApproval = ordered.Decisions.Single(d => d.Kind == DecisionKind.PoApproval);
        await runner.ResolveDecisionAsync(caseId, poApproval.DecisionId, true, poApproval.RequestedTo, "ok");
        await runner.AdvanceAsync(caseId);
        await runner.OnSupplierAckAsync(new SupplierAckInput(caseId));
        await runner.AdvanceAsync(caseId);
        var poNumber = Load(caseDir, caseId).Case.PoNumber!;
        var poLine = (await erp.GetPurchaseOrderAsync(poNumber))!.Lines
            .Single(l => l.ItemId == SeededErp.Battery);
        await runner.OnAsnAsync(new SupplierAsnInput(caseId, "ASN-M1", "PACK-M1",
            [new AsnCartonInput("CTN-M1", SeededErp.Battery, (int)poLine.QtyOrdered, "LOT-M1")]));
        _output.WriteLine($"[mail-live] PO {poNumber} line {poLine.PoLineRef} x{poLine.QtyOrdered} @ {poLine.UnitPrice}.");

        Assert.True(poLine.QtyOrdered > 5, "Seed shortfall must leave room for the corrected 5-unit line.");
        var main = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-MAIN", "V-B", poNumber,
            [new SupplierInvoiceLineInput(poLine.PoLineRef, poLine.QtyOrdered - 5, poLine.UnitPrice)]));
        Assert.True(main.Persisted);
        Assert.Equal((poLine.QtyOrdered - 5) * poLine.UnitPrice, main.AcceptedValue);
        var invoiced = Load(caseDir, caseId);
        var invApproval = invoiced.Decisions.Single(d => d.Kind == DecisionKind.InvoiceApproval);
        await runner.ResolveDecisionAsync(caseId, invApproval.DecisionId, true, invApproval.RequestedTo, "ok");

        var tricky = await runner.OnInvoiceAsync(new SupplierInvoiceInput(
            caseId, "INV-MISS", "V-B", "PO-404",
            [new SupplierInvoiceLineInput("PO-L9", 5, 10)]));
        Assert.False(tricky.Persisted);
        var problem = Load(caseDir, caseId).Decisions.Single(d =>
            d.Kind == DecisionKind.ApReview && d.Status == DecisionStatus.Pending);
        Assert.Equal("V-B/INV-MISS", problem.DocumentNumber);
        _output.WriteLine($"[mail-live] AP problem: {problem.DocumentNumber} :: {problem.Reason ?? problem.Title}");

        VendorEmail? query = null;
        for (var round = 0; round < 6 && query is null; round++)
        {
            var advanced = await runner.AdvanceAsync(caseId);
            _output.WriteLine($"[mail-live] advance {round}: step {advanced.Step}, complete {advanced.Complete}.");
            var file = Load(caseDir, caseId);
            if (file.Events.Any(e => e.Kind == CaseEventKind.VendorEmailed))
            {
                query = Assert.Single(EmailStore.Load(caseDir, caseId).Messages);
                break;
            }

            var pendingPay = file.Decisions.FirstOrDefault(d =>
                d.Kind == DecisionKind.PaymentApproval && d.Status == DecisionStatus.Pending);
            if (pendingPay is not null)
            {
                await runner.ResolveDecisionAsync(caseId, pendingPay.DecisionId, true, pendingPay.RequestedTo, "ok");
            }
        }

        foreach (var turn in CaseTranscript.ReadTurns(caseDir, caseId))
        {
            _output.WriteLine($"[mail-live] turn {turn.Turn} {turn.Stage} {turn.PickedStep}: {turn.Reasoning} " +
                string.Join("; ", turn.Calls.Select(c => $"{c.Name}={c.Verdict}")));
        }

        Assert.True(query is not null, "Spark never emailed the vendor; see the [mail-live] turns above.");
        Assert.Equal("sales@cells.example", query.To);
        Assert.Equal("V-B/INV-MISS", query.InvoiceRef);
        _output.WriteLine($"[mail-live] query to {query.To}: {query.Subject} / {query.Body}");

        var parked = await runner.AdvanceAsync(caseId);
        Assert.Equal(CaseNextStep.AwaitVendorReply, parked.Step);

        var price = poLine.UnitPrice.ToString(CultureInfo.InvariantCulture);
        var reply = await runner.OnVendorReplyAsync(new VendorReplyInput(
            caseId, "sales@cells.example",
            "Hi,\n" +
            "\n" +
            "Thanks for flagging this — you're right, INV-MISS went out with an old PO reference on our side. " +
            "Sorry for the confusion.\n" +
            "\n" +
            $"Please book those 5 units as invoice INV-FIX under Meridian Cellworks (V-B) against your PO {poNumber}, " +
            $"line {poLine.PoLineRef}, at {price} per unit. " +
            "Everything else on the shipment stays as billed on INV-MAIN.\n" +
            "\n" +
            "Let me know if you need anything else from us.\n" +
            "\n" +
            "Best regards,\n" +
            "Maria Santos\n" +
            "Accounts Receivable, Meridian Cellworks Inc",
            InReplyTo: query.EmailId, EmailId: $"{caseId}-r-001"));
        Assert.Equal("V-B", reply.VendorId);
        var stored = await erp.GetInvoiceAsync("V-B", "INV-FIX");
        Assert.NotNull(stored);
        Assert.All(stored.Lines, l => Assert.Equal(LineDisposition.Accepted, l.Disposition));
        Assert.Contains(Load(caseDir, caseId).Events, e =>
            e.Kind == CaseEventKind.Note
            && (e.Summary ?? string.Empty).Contains("re-intaked as V-B/INV-FIX", StringComparison.Ordinal));
        _output.WriteLine($"[mail-live] reply {reply.EmailId} re-intaked as V-B/INV-FIX, accepted " +
            $"{stored.Lines.Sum(l => l.Quantity * l.UnitPrice)}.");

        var fixApproval = Load(caseDir, caseId).Decisions.Single(d =>
            d.Kind == DecisionKind.InvoiceApproval
            && d.Status == DecisionStatus.Pending
            && string.Equals(d.DocumentNumber, "V-B/INV-FIX", StringComparison.Ordinal));
        await runner.ResolveDecisionAsync(caseId, fixApproval.DecisionId, true, fixApproval.RequestedTo, "ok");
        var apReview = Load(caseDir, caseId).Decisions.Single(d =>
            d.Kind == DecisionKind.ApReview && d.Status == DecisionStatus.Pending);
        await runner.ResolveDecisionAsync(caseId, apReview.DecisionId, true, apReview.RequestedTo, "correction accepted");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var early = await runner.AdvanceAsync(caseId, payUpToDate: today.AddDays(-1));
        Assert.Equal(CaseNextStep.AwaitDueDate, early.Step);
        _output.WriteLine("[mail-live] early pay run parked in AwaitDueDate.");
        await runner.AdvanceAsync(caseId, payUpToDate: today.AddDays(1));
        var pay = Load(caseDir, caseId).Decisions.Single(d =>
            d.Kind == DecisionKind.PaymentApproval && d.Status == DecisionStatus.Pending);
        await runner.ResolveDecisionAsync(caseId, pay.DecisionId, true, pay.RequestedTo, "ok");

        var done = await runner.AdvanceAsync(caseId);
        Assert.True(done.Complete);
        var finished = Load(caseDir, caseId);
        Assert.Equal(CaseStage.Resolved, finished.Case.Stage);
        decimal accepted = 0;
        decimal paid = 0;
        foreach (var key in finished.Case.Invoices)
        {
            var invoice = (await erp.GetInvoiceAsync(key.VendorId, key.SupplierInvoiceNumber))!;
            accepted += invoice.Lines
                .Where(l => l.Disposition == LineDisposition.Accepted)
                .Sum(l => l.Quantity * l.UnitPrice);
            paid += (await erp.GetSettlementsAsync(key.VendorId, key.SupplierInvoiceNumber))
                .Sum(s => s.SettledAmount);
        }

        Assert.True(accepted > 0);
        Assert.Equal(accepted, paid);
        Assert.Contains(CaseTranscript.ReadTurns(caseDir, caseId).SelectMany(t => t.Calls), c =>
            string.Equals(c.Name, AgentTools.SendVendorEmail, StringComparison.Ordinal)
            && c.Verdict == ToolCallVerdict.Executed);
        _output.WriteLine($"[mail-live] resolved: paid {paid} of {accepted} accepted.");
    }
}

// Video probe fixture: own test data plus single-flow drivers for the four
// video moments. Builds an isolated InMemoryErpAdapter (no company baseline,
// no scenario files) and drives it through the real P2pAgent, CaseRunner,
// ComplianceEngine, and DockEvaluator. All values below are probe-only.
using Gate.Agent;
using Gate.Compliance;
using Gate.Contracts;
using Gate.Erp;

namespace Gate.Eval.VideoProbes;

public static class VideoProbeData
{
    public const string Battery = "VCELL-1";
    public const string Screw = "VSCR-1";
    public const string Glasses = "VGLASS-1";
    public const string VendorCv = "PVCV";
    public const string VendorMw = "PVMW";
    public const string VendorFs = "PVFS";
    public const string Terms = "PROBE-NET0";
    public const string PlannerId = "v-planner";
    public const string ApproverId = "v-approver";
    public const string DelegateId = "v-delegate";
    public const string FinanceId = "v-finance";
    public const string ClerkId = "v-clerk";
    public const string ShopId = "VSHOP-001";
    public const string ShopScenario = "shop";
    public const string Currency = "USD";
    public const string CartonGood = "PROBE-C-001";
    public const string CartonBad = "PROBE-C-002";
    public const string LotGood = "PROBE-LOT-001";
    public const string LotBad = "PROBE-LOT-002";
    public const string ImageGood = "PROBE-IMG-001";
    public const string ImageBad = "PROBE-IMG-002";
    public const string ImageReshot = "PROBE-IMG-RESHOT";
    public const string SupplierAsn = "SUP-ASN-7701";
    public const string SupplierPackingSlip = "SUP-PS-7701";
    public const string SupplierInvoice = "MW-2026-11-01";
}

/// <summary>Scripted Spark double: returns queued tool calls in order and
/// records every request. An empty queue throws, so tests prove exact counts.</summary>
public sealed class ProbeModelClient : IModelClient
{
    private readonly Queue<ModelResult> _results = new();

    public string ModelName => "probe-scripted";

    public int CallCount { get; private set; }

    public List<ModelRequest> Requests { get; } = new();

    public void QueueToolCalls(string reasoning, params (string Id, string Name, string Args)[] calls) =>
        _results.Enqueue(new ModelResult(
            reasoning, calls.Select(c => new ModelToolCall(c.Id, c.Name, c.Args)).ToList()));

    public void QueueText(string text) => _results.Enqueue(new ModelResult(text));

    public Task<ModelResult> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        _ = ct;
        CallCount++;
        Requests.Add(request);
        if (_results.Count == 0)
        {
            throw new InvalidOperationException("No scripted probe response left.");
        }

        return Task.FromResult(_results.Dequeue());
    }
}

/// <summary>Probe dock adapter: the runner's IDockEvaluator backed by the real
/// C# dock rules. Mapping only; every judgment runs in DockEvaluator.</summary>
public sealed class ProbeDockAdapter : IDockEvaluator
{
    private readonly DockEvaluator _inner = new();

    public string MarkRuleId => DockEvaluator.MarkRuleId;

    public string Class9RuleId => DockEvaluator.Class9RuleId;

    public string DamageRuleId => DockEvaluator.DamageRuleId;

    public IReadOnlyDictionary<string, IReadOnlyList<Verdict>> EvaluateDock(
        IReadOnlyList<string> asnCartonIds, IReadOnlyList<DockCartonRead> reads) =>
        _inner.EvaluateDock(asnCartonIds, reads.Select(r => new CartonRead(
            r.CartonId, r.Part, r.PoNumber, r.LotCode, r.UnitsPerCarton,
            r.MarkPresent, r.MarkLegible, r.UnNumber, r.Class9Required,
            r.Class9Visible, r.DamageVisible, r.DamageNote, r.ImageId,
            r.ReadConfidence)).ToList());
}

public sealed record ProbeHarness(
    InMemoryErpAdapter Erp,
    P2pAgent Core,
    ProbeModelClient Models,
    ComplianceEngine Compliance,
    CaseRunner Runner,
    string CaseDir);

public static class VideoFlowFixture
{
    public static InMemoryErpAdapter CreateProbeErp()
    {
        var erp = new InMemoryErpAdapter();
        erp.SeedBaseCurrency(VideoProbeData.Currency);
        erp.SeedAutoApproveThreshold(400m);
        erp.SeedPaymentTerms(new PaymentTermsDto(VideoProbeData.Terms, 0));
        erp.SeedItem(new ItemDto(VideoProbeData.Battery, "Probe battery cell", true, 15, "EA"));
        erp.SeedItem(new ItemDto(VideoProbeData.Screw, "Probe screw", false, 25, "EA"));
        erp.SeedItem(new ItemDto(VideoProbeData.Glasses, "Probe smart glasses", false, 1, "EA"));
        erp.SeedBom(VideoProbeData.Glasses, [
            new BomLine(VideoProbeData.Battery, 1),
            new BomLine(VideoProbeData.Screw, 2)]);
        erp.SeedStock(VideoProbeData.Battery, 5);
        erp.SeedStock(VideoProbeData.Screw, 20);
        erp.SeedVendor(new VendorDto(VideoProbeData.VendorCv, "Probe CinderVolt Energy", "CN", false, null, VideoProbeData.Terms, "sales@probecv.example"));
        erp.SeedVendor(new VendorDto(VideoProbeData.VendorMw, "Probe Meridian Cellworks", "MX", false, null, VideoProbeData.Terms, "sales@probemw.example"));
        erp.SeedVendor(new VendorDto(VideoProbeData.VendorFs, "Probe Formosa Fasteners", "TW", false, null, VideoProbeData.Terms, "sales@probefs.example"));
        var now = DateTimeOffset.UtcNow;
        erp.SeedVendorBank(new VendorBankDto(VideoProbeData.VendorCv, "PROBE-BANK-CV-001", true, now));
        erp.SeedVendorBank(new VendorBankDto(VideoProbeData.VendorMw, "PROBE-BANK-MW-001", true, now));
        erp.SeedVendorBank(new VendorBankDto(VideoProbeData.VendorFs, "PROBE-BANK-FS-001", true, now));
        erp.SeedVendorPrice(VideoProbeData.VendorCv, VideoProbeData.Battery, 20.00m);
        erp.SeedVendorPrice(VideoProbeData.VendorMw, VideoProbeData.Battery, 22.00m);
        erp.SeedVendorPrice(VideoProbeData.VendorFs, VideoProbeData.Screw, 0.55m);
        erp.SeedVendorDocuments(VideoProbeData.VendorCv, [
            new VendorDocumentDto(VideoProbeData.VendorCv, "UN38.3", "probe-un383-cv", true),
            new VendorDocumentDto(VideoProbeData.VendorCv, "SDS", "probe-sds-cv", true),
            new VendorDocumentDto(VideoProbeData.VendorCv, "RoHS", "probe-rohs-cv", true),
            new VendorDocumentDto(VideoProbeData.VendorCv, "REACH", "probe-reach-cv", true)]);
        erp.SeedVendorDocuments(VideoProbeData.VendorMw, [
            new VendorDocumentDto(VideoProbeData.VendorMw, "UN38.3", "probe-un383-mw", true),
            new VendorDocumentDto(VideoProbeData.VendorMw, "SDS", "probe-sds-mw", true),
            new VendorDocumentDto(VideoProbeData.VendorMw, "RoHS", "probe-rohs-mw", true),
            new VendorDocumentDto(VideoProbeData.VendorMw, "REACH", "probe-reach-mw", true)]);
        // PVFS carries no pack documents: VSCR-1 is non-critical, so no pack applies.
        // Trailing history in cells (total 400, CN 236). The 30-cell probe award
        // puts CN at 266/430 = 61.9% (blocks PVCV) and MX at 180/430 = 41.9%
        // (passes PVMW). Probe-only numbers, distinct from company history.
        erp.SeedVolumeHistory(VideoProbeData.Battery, [
            new VolumeHistory("Probe CV CN", "CN", 150),
            new VolumeHistory("Probe legacy CN", "CN", 86),
            new VolumeHistory("Probe MW MX", "MX", 150),
            new VolumeHistory("Probe other", "OTHER", 14)]);
        erp.SeedUser(new UserDto(VideoProbeData.PlannerId, "requester", 5000m, "USD"));
        erp.SeedUser(new UserDto(VideoProbeData.ApproverId, "approver", 100_000m, "USD"));
        erp.SeedUser(new UserDto(VideoProbeData.DelegateId, "delegate", 50_000m, "USD"));
        erp.SeedUser(new UserDto(VideoProbeData.FinanceId, "payment-approver", 100_000m, "USD"));
        erp.SeedUser(new UserDto(VideoProbeData.ClerkId, "ap-clerk", 0m, "USD"));
        var today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.DateTime);
        erp.SeedDelegation(new DelegationDto(VideoProbeData.ApproverId, VideoProbeData.DelegateId, today.AddDays(-30), today.AddDays(30)));
        return erp;
    }

    public static ProbeHarness CreateHarness()
    {
        var erp = CreateProbeErp();
        var models = new ProbeModelClient();
        var compliance = new ComplianceEngine();
        var dir = Path.Combine(Path.GetTempPath(), "gate-video-probe", Guid.NewGuid().ToString("N"));
        var core = new P2pAgent(erp, models, compliance);
        var runner = new CaseRunner(erp, core, models, compliance, new ProbeDockAdapter(), dir);
        return new ProbeHarness(erp, core, models, compliance, runner, dir);
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates() =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            [VideoProbeData.Battery] = [VideoProbeData.VendorCv, VideoProbeData.VendorMw],
            [VideoProbeData.Screw] = [VideoProbeData.VendorFs]
        };

    public static string Args(string caseId) =>
        $$"""{"case_id":"{{caseId}}"}""";

    public static string AwardArgs(string caseId, string vendorId) =>
        $$"""{"case_id":"{{caseId}}","vendor_id":"{{vendorId}}"}""";

    public static CaseFile Load(ProbeHarness h, string caseId) =>
        CaseStore.Load(h.CaseDir, caseId) ?? throw new InvalidOperationException($"Probe case {caseId} vanished.");

    public static Decision PendingDecision(CaseFile file, DecisionKind kind) =>
        file.Decisions.Single(d => d.Kind == kind && d.Status == DecisionStatus.Pending);

    /// <summary>Shop order intake: an unknown shop id carrying lines plus
    /// currency, so the ERP mints the sales order. Returns the battery case.</summary>
    public static async Task<string> OpenBatteryCaseAsync(ProbeHarness h)
    {
        var cases = await h.Runner.OnOrderWebhookAsync(new OrderWebhookInput(
            VideoProbeData.ShopScenario,
            VideoProbeData.ShopId,
            VideoProbeData.PlannerId,
            Candidates(),
            "probe:shop:" + VideoProbeData.ShopId,
            [new SalesOrderLine(VideoProbeData.Glasses, 35)],
            VideoProbeData.Currency));
        return cases.Single(c => c.EndsWith(VideoProbeData.Battery, StringComparison.Ordinal));
    }

    /// <summary>Two dock reads from two photos: one good carton, one carton
    /// photographed without its lithium-battery mark.</summary>
    public static IReadOnlyList<DockCartonRead> DockReads(string poNumber) =>
    [
        new DockCartonRead(
            VideoProbeData.CartonGood, VideoProbeData.Battery, poNumber, VideoProbeData.LotGood, 15,
            true, true, "UN3480", true, true,
            false, null, VideoProbeData.ImageGood, 0.97m),
        new DockCartonRead(
            VideoProbeData.CartonBad, VideoProbeData.Battery, poNumber, VideoProbeData.LotBad, 15,
            false, false, "UN3480", true, true,
            false, null, VideoProbeData.ImageBad, 0.96m)
    ];

    public static IReadOnlyList<AsnCartonInput> AsnCartons() =>
    [
        new AsnCartonInput(VideoProbeData.CartonGood, VideoProbeData.Battery, 15, VideoProbeData.LotGood),
        new AsnCartonInput(VideoProbeData.CartonBad, VideoProbeData.Battery, 15, VideoProbeData.LotBad)
    ];
}

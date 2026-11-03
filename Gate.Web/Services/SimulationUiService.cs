// UI-side driver for the simulation endpoints. The outside-world surface
// (customer shop, supplier inbox, dock camera) offers option lists built
// from runtime sources only (ERP adapter, case cache, dock image
// directory) and posts the human's external inputs (shop orders, shipment
// notices, camera photos, supplier invoices, vendor replies) to the
// existing endpoints in SimulationEndpoints.cs. This service never
// sequences internal logic: the endpoints run, then advance, and this
// layer only shapes the outcome for display. Failures and refusals surface
// with the path, status, and body excerpt (the UI sanitizes paths to
// friendly flow names); nothing is silently omitted.
using System.Text.Json;
using Gate.Agent;
using Gate.Contracts;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed class SimulationUiService
{
    // Paths pinned to SimulationEndpoints.cs (PickupPath to AgentEndpoints.cs);
    // SimulationUiTests pins them too.
    // UI text never shows these (OutsideWorldLabels.Sanitize); the service
    // keeps them for requests plus test-pinned failure context.
    public const string OrderPath = "/simulation/shopify/order";
    public const string AckPath = "/simulation/supplier/ack";
    public const string AsnPath = "/simulation/supplier/asn";
    public const string DockPhotosPath = "/simulation/dock/photos";
    public const string ReshotPath = "/simulation/dock/reshot";
    public const string ReshotImagePath = "/simulation/dock/reshot-image";
    public const string RebuildViewsPath = "/simulation/dock/rebuild-views";
    public const string InvoicePath = "/simulation/supplier/invoice";
    public const string InvoicePhotoPath = "/simulation/supplier/invoice-photo";
    public const string EmailReplyPath = "/simulation/supplier/email-reply";
    public const string PickupPath = "/agent/pickup";
    public const string ProgressPath = "/simulation/case-progress";

    /// <summary>Standard tail on instant confirmations: the POST answered
    /// from its ERP write while the agent works off the request.</summary>
    private const string BackgroundNote = "The agent continues in the background.";

    private const int BodyExcerptChars = 2000;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IErpAdapter _erp;
    private readonly CaseCacheStore _cases;
    private readonly DockPhotoService _photos;
    private readonly ILogger<SimulationUiService> _log;

    public SimulationUiService(
        HttpClient http,
        IErpAdapter erp,
        CaseCacheStore cases,
        DockPhotoService photos,
        ILogger<SimulationUiService> log)
    {
        _http = http;
        _erp = erp;
        _cases = cases;
        _photos = photos;
        _log = log;
    }

    public async Task<SimulateOptions> GetOptionsAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();

        var entityCode = string.Empty;
        var currency = string.Empty;
        try
        {
            var company = await _erp.GetCompanyInfoAsync(ct).ConfigureAwait(false);
            if (company is null)
            {
                notes.Add("ERP has no company info; order and currency options are empty.");
            }
            else
            {
                entityCode = company.EntityCode;
                currency = company.Currency;
            }
        }
        catch (NotSupportedException ex)
        {
            _log.LogWarning(ex, "Company info is not implemented by the ERP layer yet.");
            notes.Add("Company info is not implemented by the ERP layer yet.");
        }
        catch (Exception ex)
        {
            notes.Add($"ERP company info failed: {ex.Message}.");
        }

        IReadOnlyList<UserDto> users = [];
        try
        {
            users = await _erp.GetUsersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            notes.Add($"ERP user list failed: {ex.Message}.");
        }

        IReadOnlyList<VendorDto> vendors = [];
        try
        {
            vendors = (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
                .OrderBy(v => v.VendorId, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            notes.Add($"ERP vendor list failed: {ex.Message}.");
        }

        IReadOnlyList<ItemOption> items = [];
        try
        {
            items = (await _erp.GetItemsAsync(ct).ConfigureAwait(false))
                .OrderBy(i => i.ItemId, StringComparer.Ordinal)
                .Select(i => new ItemOption(i.ItemId, i.Unit))
                .ToList();
        }
        catch (NotSupportedException ex)
        {
            _log.LogWarning(ex, "Item list is not implemented by the ERP layer yet.");
            notes.Add("Item list is not implemented by the ERP layer yet.");
        }
        catch (Exception ex)
        {
            notes.Add($"ERP item list failed: {ex.Message}.");
        }

        IReadOnlyList<CaseOption> cases = [];
        try
        {
            cases = (await _cases.ListAsync(ct).ConfigureAwait(false))
                .OrderBy(f => f.Case.CaseId, StringComparer.Ordinal)
                .Select(f => new CaseOption(
                    f.Case.CaseId, f.Case.SoNumber, f.Case.ItemId, CaseDisplay.StageSummary(f.Case.Stage),
                    f.Case.PoNumber))
                .ToList();
        }
        catch (Exception ex)
        {
            notes.Add($"Case list failed: {ex.Message}.");
        }

        // Sourcing candidates are the vendors carrying a price for the
        // item; screening then picks the winner from those. Items nobody
        // prices (like our own finished goods) carry no candidates. If the
        // priced-vendor read fails, every vendor stays a candidate and the
        // PO step still recovers to a priced vendor on refusal.
        var vendorIds = vendors
            .Select(v => v.VendorId)
            .Order(StringComparer.Ordinal)
            .ToList();
        var candidates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            try
            {
                candidates[item.ItemId] =
                    await _erp.GetPricedVendorIdsAsync(item.ItemId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                notes.Add($"ERP priced vendors for {item.ItemId} failed: {ex.Message}; every vendor stays a candidate.");
                candidates[item.ItemId] = vendorIds;
            }
        }

        return new SimulateOptions(
            entityCode, currency, users, vendors, cases,
            await ListDockImagesAsync(notes, ct).ConfigureAwait(false), items, candidates, notes);
    }

    /// <summary>Company identity for headers and sender context, or null
    /// when the ERP layer serves none. Failures log and yield null;
    /// callers render without the company line.</summary>
    public async Task<CompanyInfoDto?> GetCompanyAsync(CancellationToken ct = default)
    {
        try
        {
            return await _erp.GetCompanyInfoAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Company info lookup failed.");
            return null;
        }
    }

    public async Task<SimulationOutcome> SendOrderAsync(
        OrderSubmission submission, CancellationToken ct = default)
    {
        if (submission is null)
        {
            return Refusal("No order submission was provided.");
        }

        if (string.IsNullOrWhiteSpace(submission.EntityCode))
        {
            return Refusal("Company code is required; reload the options when the ERP serves company info.");
        }

        if (string.IsNullOrWhiteSpace(submission.ShopOrderId))
        {
            return Refusal("Shop order id is required.");
        }

        if (submission.Lines is null || submission.Lines.Count == 0)
        {
            return Refusal("Add at least one order line; the ERP cannot mint an empty order.");
        }

        if (string.IsNullOrWhiteSpace(submission.Currency))
        {
            return Refusal(
                $"Shop order {submission.ShopOrderId.Trim()} carries lines but no currency; the ERP cannot mint the sales order without one.");
        }

        var lines = new List<SalesOrderLine>();
        foreach (var line in submission.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ItemId))
            {
                return Refusal("Every order line needs an item.");
            }

            if (line.Quantity <= 0)
            {
                return Refusal($"Order line for {line.ItemId} needs a quantity above zero.");
            }

            lines.Add(new SalesOrderLine(line.ItemId.Trim(), line.Quantity));
        }

        // The entity code travels in the webhook's compat name slot so new
        // cases link the company.
        var input = new OrderWebhookInput(
            submission.EntityCode.Trim(),
            submission.ShopOrderId.Trim(),
            BlankToNull(submission.Requester),
            submission.CandidateVendorsByItem,
            FreshKey(),
            Lines: lines,
            Currency: BlankToNull(submission.Currency));
        return await PostAsync(OrderPath, input, ParseOrder, ct).ConfigureAwait(false);
    }

    /// <summary>Asks the agent to pick up ERP work: approved PRs without a
    /// PO, supplier-leg POs, and received invoices each get a live case.
    /// Adoption only, zero live model calls; the touched case ids link on
    /// to Cases. A blank company code is refused before any POST.</summary>
    public async Task<SimulationOutcome> PickUpErpWorkAsync(
        string? entityCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entityCode))
        {
            return Refusal("Company code is required; reload the options when the ERP serves company info.");
        }

        return await PostAsync(PickupPath, new { entityCode = entityCode.Trim() }, ParsePickup, ct)
            .ConfigureAwait(false);
    }

    /// <summary>New shop order without an SO number: the shop mints a shop
    /// order id, the ERP mints the SO, and the minted numbers resolve from
    /// the new case files. The shop id travels back so the page can show
    /// what the shop sent alongside what the ERP minted.</summary>
    public async Task<ShopOrderPlaced> SendNewShopOrderAsync(
        NewShopOrder? submission, CancellationToken ct = default)
    {
        if (submission is null)
        {
            return new ShopOrderPlaced(
                Refusal("No order submission was provided."), string.Empty, [], []);
        }

        if (submission.Lines.Count == 0)
        {
            return new ShopOrderPlaced(
                Refusal("Add at least one order line; the ERP cannot mint an empty order."),
                string.Empty, [], []);
        }

        var shopId = $"SHOP-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        var outcome = await SendOrderAsync(new OrderSubmission(
            submission.EntityCode, shopId, submission.Requester,
            submission.CandidateVendorsByItem,
            submission.Lines, submission.Currency), ct).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            return new ShopOrderPlaced(outcome, shopId, [], outcome.CaseIds);
        }

        var soNumbers = await ResolveSoNumbersAsync(outcome.CaseIds, ct).ConfigureAwait(false);
        var received = soNumbers.Count == 0
            ? outcome
            : outcome with { Summary = $"Order {soNumbers[0]} received." };
        return new ShopOrderPlaced(received, shopId, soNumbers, outcome.CaseIds);
    }

    /// <summary>SO numbers behind posted case ids, read back from the case
    /// cache. Empty when the cache has none yet; the page renders the
    /// case links it already has.</summary>
    public async Task<IReadOnlyList<string>> ResolveSoNumbersAsync(
        IReadOnlyList<string> caseIds, CancellationToken ct = default)
    {
        var numbers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var caseId in caseIds)
        {
            try
            {
                var file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
                if (file is not null && !string.IsNullOrWhiteSpace(file.Case.SoNumber))
                {
                    numbers.Add(file.Case.SoNumber);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Minted-SO lookup failed for case {CaseId}.", caseId);
            }
        }

        return numbers.ToList();
    }

    /// <summary>Supplier acknowledgement for the case PO. Lines default to
    /// the full ordered qty on the runner; a blank case is refused before
    /// any POST. ERP refusals (for example an unapproved PO) surface from
    /// the endpoint with status plus body excerpt.</summary>
    public async Task<SimulationOutcome> SendAckAsync(
        string? caseId, DateOnly? confirmDate = null,
        IReadOnlyList<VendorConfirmLine>? lines = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            return Refusal("Case is required for an acknowledgement; pick the PO's case first.");
        }

        var input = new SupplierAckInput(caseId.Trim(), confirmDate, lines, FreshKey());
        return await PostAsync(AckPath, input, ParseAck, ct).ConfigureAwait(false);
    }

    public async Task<SimulationOutcome> SendAsnAsync(
        AsnSubmission? submission, CancellationToken ct = default)
    {
        if (submission is null)
        {
            return Refusal("No shipment notice was provided.");
        }

        if (string.IsNullOrWhiteSpace(submission.CaseId))
        {
            return Refusal("Case is required for a shipment notice.");
        }

        // A blank ASN number mints from our ASN sequence in the ERP: the
        // ASN identity is ours, never typed by a person. An explicit
        // number is honored for callers that already hold one.

        if (string.IsNullOrWhiteSpace(submission.PackingSlipNumber))
        {
            return Refusal("Packing-slip number is required; the receipt posts against it.");
        }

        if (submission.Cartons is null || submission.Cartons.Count == 0)
        {
            return Refusal("Add at least one carton to the shipment notice.");
        }

        var cartons = new List<AsnCartonInput>();
        var problems = new List<DockProblemInput>();
        foreach (var carton in submission.Cartons)
        {
            if (string.IsNullOrWhiteSpace(carton.CartonId))
            {
                return Refusal("Every ASN carton needs a carton id.");
            }

            if (string.IsNullOrWhiteSpace(carton.ItemId))
            {
                return Refusal($"ASN carton {carton.CartonId} needs an item.");
            }

            if (carton.UnitsPerCarton <= 0)
            {
                return Refusal($"ASN carton {carton.CartonId} needs units per carton above zero.");
            }

            if (string.IsNullOrWhiteSpace(carton.LotRef))
            {
                return Refusal($"ASN carton {carton.CartonId} needs the supplier lot ref.");
            }

            string? problem = null;
            if (!DockProblemKinds.IsNone(carton.Problem))
            {
                if (!DockProblemKinds.TryParse(carton.Problem, out var canonical))
                {
                    return Refusal(
                        $"ASN carton {carton.CartonId} names an unknown carton problem '{carton.Problem}'; " +
                        $"pick {DockProblemKinds.DescribeOptions()}.");
                }

                problem = canonical;
            }

            cartons.Add(new AsnCartonInput(
                carton.CartonId.Trim(), carton.ItemId.Trim(),
                carton.UnitsPerCarton, carton.LotRef.Trim()));
            if (problem is not null)
            {
                problems.Add(new DockProblemInput(carton.CartonId.Trim(), problem));
            }
        }

        var input = new SupplierAsnWithProblemsRequest(
            submission.CaseId.Trim(),
            submission.AsnNumber?.Trim() ?? string.Empty,
            submission.PackingSlipNumber.Trim(),
            cartons,
            FreshKey(),
            problems);
        return await PostAsync(AsnPath, input, ParseAsn, ct).ConfigureAwait(false);
    }

    public async Task<SimulationOutcome> SendDockPhotosAsync(
        string? caseId, IReadOnlyList<SimulationPhoto>? photos, CancellationToken ct = default)
    {
        var ready = RequireCaseAndPhotos(caseId, photos, "dock photos");
        if (ready is not null)
        {
            return ready;
        }

        var request = new DockPhotosRequest(
            caseId!.Trim(),
            photos!.Select(p => new DockPhoto(p.ImageId, p.Jpeg, BlankToNull(p.CartonHint))).ToList());
        return await PostAsync(DockPhotosPath, request, body => ParseDock(body, "Dock photos"), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Mints the relabeled close-up for one carton: one AI labeling
    /// attempt with verify-then-composite fallback. Returns the minted file
    /// name to send through the reshot endpoint, plus how it was made.</summary>
    public async Task<MintedReshotImageVm> MintReshotImageAsync(
        string? caseId, string? cartonId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseId) || string.IsNullOrWhiteSpace(cartonId))
        {
            return new MintedReshotImageVm(false, null, "Case and carton are required to mint a close-up.");
        }

        try
        {
            var sent = JsonSerializer.Serialize(
                new { caseId = caseId.Trim(), cartonId = cartonId.Trim() }, JsonOpts);
            using var content = new StringContent(sent, System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(ReshotImagePath, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new MintedReshotImageVm(false, null,
                    $"Close-up minting refused: {ErrorText(body)}.");
            }

            using var doc = JsonDocument.Parse(body);
            var file = Text(doc.RootElement, "fileName");
            var path = Text(doc.RootElement, "path") ?? "unknown";
            var reason = Text(doc.RootElement, "fallbackReason");
            return string.IsNullOrWhiteSpace(file)
                ? new MintedReshotImageVm(false, null, "Close-up minting returned no file.")
                : new MintedReshotImageVm(true, file,
                    $"Minted {file} via {path}" + (reason is null ? "." : $" (fallback: {reason})."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new MintedReshotImageVm(false, null, $"Close-up minting failed: {ex.Message}.");
        }
    }

    /// <summary>Starts a rebuild of the case's dock views with simulated
    /// problems: posts the case plus the picked carton problems and
    /// returns on acceptance. The new views land in the background;
    /// the panel polls the dock options for them. Refusals (no case,
    /// no problems, unknown kind) happen before any POST; endpoint
    /// failures surface with the reason.</summary>
    public async Task<RebuiltDockViewsVm> RebuildDockViewsAsync(
        string? caseId, IReadOnlyList<DockProblemForm>? problems, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            return new RebuiltDockViewsVm(false, [], "Case is required to rebuild dock views.");
        }

        var picked = new List<DockProblemForm>();
        foreach (var problem in problems ?? [])
        {
            if (problem is null || DockProblemKinds.IsNone(problem.Kind))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(problem.CartonId))
            {
                return new RebuiltDockViewsVm(false, [], "Every simulated problem needs a carton id.");
            }

            if (!DockProblemKinds.TryParse(problem.Kind, out var canonical))
            {
                return new RebuiltDockViewsVm(
                    false, [],
                    $"Unknown carton problem '{problem.Kind}' for carton {problem.CartonId.Trim()}; " +
                    $"pick {DockProblemKinds.DescribeOptions()}.");
            }

            picked.Add(new DockProblemForm(problem.CartonId.Trim(), canonical));
        }

        if (picked.Count == 0)
        {
            return new RebuiltDockViewsVm(false, [], "Pick at least one simulated problem to rebuild the views with.");
        }

        try
        {
            var sent = JsonSerializer.Serialize(
                new
                {
                    caseId = caseId.Trim(),
                    problems = picked.Select(p => new { cartonId = p.CartonId, kind = p.Kind }).ToList(),
                },
                JsonOpts);
            using var content = new StringContent(sent, System.Text.Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(RebuildViewsPath, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new RebuiltDockViewsVm(false, [], $"Dock view rebuild refused: {ErrorText(body)}.");
            }

            return new RebuiltDockViewsVm(
                true, [], "Rebuild started; new views tick themselves when they land.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new RebuiltDockViewsVm(false, [], $"Dock view rebuild failed: {ex.Message}.");
        }
    }

    public async Task<SimulationOutcome> SendReshotAsync(
        string? caseId, IReadOnlyList<string>? cartonIds, IReadOnlyList<SimulationPhoto>? photos,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            return Refusal("Case is required for a reshot.");
        }

        var cartons = (cartonIds ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToList();
        if (cartons.Count == 0)
        {
            return Refusal("Pick at least one carton the reshot covers.");
        }

        var ready = RequireCaseAndPhotos(caseId, photos, "a reshot");
        if (ready is not null)
        {
            return ready;
        }

        var request = new ReshotPhotosRequest(
            caseId.Trim(),
            cartons,
            photos!.Select(p => new DockPhoto(p.ImageId, p.Jpeg, BlankToNull(p.CartonHint))).ToList());
        return await PostAsync(ReshotPath, request, body => ParseDock(body, "Reshot"), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Local progress read: the newest dock outcome plus case
    /// invoices newer than since. Missing sections mean nothing new yet,
    /// never an error.</summary>
    public async Task<CaseProgressVm> GetCaseProgressAsync(
        string? caseId, DateTimeOffset since, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            return new CaseProgressVm(null, [], []);
        }

        var path = $"{ProgressPath}?caseId={Uri.EscapeDataString(caseId.Trim())}" +
            $"&since={Uri.EscapeDataString(since.ToString("O"))}";
        return await GetAsync(path, ParseProgress, ct).ConfigureAwait(false);
    }

    private static CaseProgressVm ParseProgress(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        CaseProgressDockVm? dock = null;
        if (TryProp(root, "dock") is { ValueKind: JsonValueKind.Object } dockEl)
        {
            var issues = new List<CaseProgressIssueVm>();
            if (TryProp(dockEl, "issues") is { ValueKind: JsonValueKind.Array } arr)
            {
                foreach (var issue in arr.EnumerateArray())
                {
                    issues.Add(new CaseProgressIssueVm(
                        Text(issue, "cartonId") ?? "?",
                        Text(issue, "ruleId") ?? "?",
                        Text(issue, "kind") ?? "?",
                        Text(issue, "reason") ?? string.Empty));
                }
            }

            dock = new CaseProgressDockVm(StringArray(dockEl, "cartons"), issues);
        }

        var invoices = new List<CaseProgressInvoiceVm>();
        if (TryProp(root, "invoices") is { ValueKind: JsonValueKind.Array } inv)
        {
            foreach (var item in inv.EnumerateArray())
            {
                invoices.Add(new CaseProgressInvoiceVm(
                    Text(item, "vendorId") ?? "?",
                    Text(item, "supplierInvoiceNumber") ?? "?",
                    Text(item, "currency") ?? "?",
                    Text(item, "acceptedValue") ?? "?",
                    Text(item, "heldValue") ?? "?"));
            }
        }

        return new CaseProgressVm(dock, invoices, StringArray(root, "events"));
    }

    /// <summary>Polls the progress read until the first timeline lines
    /// newer than since land, or the timeout passes. Null on timeout:
    /// the receipt stands and the work continues in the background.</summary>
    public async Task<IReadOnlyList<string>?> PollNewEventsAsync(
        string caseId, DateTimeOffset since,
        CancellationToken ct = default,
        TimeSpan? pollInterval = null, TimeSpan? timeout = null)
    {
        var wait = pollInterval ?? TimeSpan.FromSeconds(1);
        var limit = timeout ?? TimeSpan.FromSeconds(120);
        using var timeoutCts = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var progress = await GetCaseProgressAsync(caseId, since, linked.Token).ConfigureAwait(false);
                if (progress.Events.Count > 0)
                {
                    return progress.Events;
                }

                await Task.Delay(wait, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Polls the progress read until the dock outcome lands or the
    /// timeout passes. Null on timeout: the photos stay received and the
    /// outcome lands on the records when ready.</summary>
    public async Task<SimulationOutcome?> PollDockOutcomeAsync(
        string caseId, DateTimeOffset since, string label,
        CancellationToken ct = default,
        TimeSpan? pollInterval = null, TimeSpan? timeout = null)
    {
        var wait = pollInterval ?? TimeSpan.FromSeconds(1);
        var limit = timeout ?? TimeSpan.FromSeconds(120);
        using var timeoutCts = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var progress = await GetCaseProgressAsync(caseId, since, linked.Token).ConfigureAwait(false);
                if (progress.Dock is not null)
                {
                    var cartons = progress.Dock.Cartons;
                    return new SimulationOutcome(
                        true,
                        $"{label} accepted: {(cartons.Count == 0 ? "no cartons read" : string.Join(", ", cartons))}.",
                        progress.Dock.Issues.Select(i => i.Reason).ToList(),
                        []);
                }

                await Task.Delay(wait, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Polls the progress read until the photographed invoice
    /// lands or the timeout passes. Null on timeout.</summary>
    public async Task<SimulationOutcome?> PollInvoiceOutcomeAsync(
        string caseId, DateTimeOffset since,
        CancellationToken ct = default,
        TimeSpan? pollInterval = null, TimeSpan? timeout = null)
    {
        var wait = pollInterval ?? TimeSpan.FromSeconds(1);
        var limit = timeout ?? TimeSpan.FromSeconds(120);
        using var timeoutCts = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var progress = await GetCaseProgressAsync(caseId, since, linked.Token).ConfigureAwait(false);
                var invoice = progress.Invoices.FirstOrDefault();
                if (invoice is not null)
                {
                    return new SimulationOutcome(
                        true,
                        $"Invoice {invoice.VendorId}/{invoice.SupplierInvoiceNumber} intaken.",
                        [$"Persisted: yes, accepted value {invoice.AcceptedValue}, held value {invoice.HeldValue}.",
                         BackgroundNote],
                        []);
                }

                await Task.Delay(wait, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Polls the local dock options until recorded views appear
    /// past the known files, or the timeout passes. Null on timeout.</summary>
    public async Task<IReadOnlyList<string>?> PollRebuildFilesAsync(
        IReadOnlyList<string> knownFiles,
        CancellationToken ct = default,
        TimeSpan? pollInterval = null, TimeSpan? timeout = null)
    {
        var known = knownFiles.ToHashSet(StringComparer.Ordinal);
        var wait = pollInterval ?? TimeSpan.FromSeconds(1);
        var limit = timeout ?? TimeSpan.FromSeconds(300);
        using var timeoutCts = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            while (true)
            {
                linked.Token.ThrowIfCancellationRequested();
                var options = await GetOptionsAsync(linked.Token).ConfigureAwait(false);
                var fresh = options.DockImages.Where(f => !known.Contains(f)).ToList();
                if (fresh.Count > 0)
                {
                    return fresh;
                }

                await Task.Delay(wait, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<T> GetAsync<T>(
        string path, Func<string, T> parse, CancellationToken ct)
        where T : class
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("GET {Path} refused: {Body}.", path, Excerpt(body));
                    return parse("{}");
                }

                return parse(body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (attempt == 1 && (ex is HttpRequestException || !ct.IsCancellationRequested))
                {
                    _log.LogWarning(ex, "GET {Path} attempt {Attempt} failed; retrying once.", path, attempt);
                    continue;
                }

                _log.LogWarning(ex, "GET {Path} failed.", path);
                return parse("{}");
            }
        }

        return parse("{}");
    }

    public async Task<SimulationOutcome> SendInvoiceAsync(
        string? caseId,
        string? supplierInvoiceNumber,
        string? vendorId,
        string? poNumber,
        IReadOnlyList<InvoiceLineForm>? lines,
        string? bankAccountRef,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            return Refusal("Case is required for invoice intake.");
        }

        if (lines is null || lines.Count == 0)
        {
            return Refusal("Add at least one invoice line.");
        }

        var mapped = new List<SupplierInvoiceLineInput>();
        foreach (var line in lines)
        {
            if (line.Quantity <= 0)
            {
                return Refusal("Every invoice line needs a quantity above zero.");
            }

            if (line.UnitPrice < 0)
            {
                return Refusal("Every invoice line needs a unit price of zero or more.");
            }

            mapped.Add(new SupplierInvoiceLineInput(BlankToNull(line.PoLineRef), line.Quantity, line.UnitPrice));
        }

        // A numberless invoice names nothing to match, pay, query, or card:
        // refuse it here with its reason. Blank vendor or PO still flows to
        // the runner, which holds it for the AP clerk under the real number.
        if (string.IsNullOrWhiteSpace(supplierInvoiceNumber))
        {
            return Refusal("Supplier invoice number is required as printed; blank numbers are refused, never held or carded.");
        }

        var input = new SupplierInvoiceInput(
            caseId.Trim(),
            supplierInvoiceNumber.Trim(),
            BlankToNull(vendorId),
            BlankToNull(poNumber),
            mapped,
            BlankToNull(bankAccountRef),
            FreshKey());
        return await PostAsync(InvoicePath, input, ParseInvoice, ct).ConfigureAwait(false);
    }

    public async Task<SimulationOutcome> SendInvoicePhotosAsync(
        string? caseId, IReadOnlyList<SimulationPhoto>? photos, CancellationToken ct = default)
    {
        var ready = RequireCaseAndPhotos(caseId, photos, "invoice photos");
        if (ready is not null)
        {
            return ready;
        }

        var request = new InvoicePhotosRequest(
            caseId!.Trim(),
            photos!.Select(p => new InvoicePhoto(p.ImageId, p.Jpeg)).ToList());
        return await PostAsync(InvoicePhotoPath, request, ParseInvoicePhotos, ct).ConfigureAwait(false);
    }

    public async Task<SimulationOutcome> SendVendorReplyAsync(
        string? caseId, string? from, string? body, string? subject, string? inReplyTo,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            return Refusal("Case is required for a vendor reply.");
        }

        if (string.IsNullOrWhiteSpace(from))
        {
            return Refusal("From address is required; the reply must come from the vendor.");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return Refusal("Reply body is required; an empty reply carries no corrected fields.");
        }

        var input = new VendorReplyInput(
            caseId.Trim(), from.Trim(), body.Trim(),
            BlankToNull(subject), BlankToNull(inReplyTo), null, FreshKey());
        return await PostAsync(EmailReplyPath, input, ParseReply, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VendorEmail>> ReadMailAsync(string caseId, CancellationToken ct = default)
    {
        try
        {
            var thread = await new CaseMailStore(_cases.CacheDir).ReadAsync(caseId, ct).ConfigureAwait(false);
            return thread?.Messages.OrderBy(m => m.SentAt).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vendor-mail lookup failed for case {CaseId}.", caseId);
            return [];
        }
    }

    /// <summary>Prefills the reply form: the latest agent query in the thread
    /// (answer goes to its To, InReplyTo its id), else the latest vendor
    /// message, else the case PO vendor email from the ERP.</summary>
    public async Task<ReplySuggestion> SuggestReplyAsync(string caseId, CancellationToken ct = default)
    {
        var mail = await ReadMailAsync(caseId, ct).ConfigureAwait(false);
        var query = mail.LastOrDefault(m =>
            string.Equals(m.From, RecordSource.P2pAgent, StringComparison.Ordinal));
        if (query is not null)
        {
            return new ReplySuggestion(query.To, query.EmailId, ReplySubject(query.Subject));
        }

        var latest = mail.LastOrDefault();
        if (latest is not null)
        {
            return new ReplySuggestion(latest.From, latest.EmailId, ReplySubject(latest.Subject));
        }

        try
        {
            var file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
            var poNumber = file?.Case.PoNumber;
            if (poNumber is null)
            {
                return new ReplySuggestion(null, null, null);
            }

            var po = await _erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false);
            if (po is null)
            {
                return new ReplySuggestion(null, null, null);
            }

            var vendor = (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(v => string.Equals(v.VendorId, po.VendorId, StringComparison.Ordinal));
            return new ReplySuggestion(vendor?.Email, null, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reply suggestion lookup failed for case {CaseId}.", caseId);
            return new ReplySuggestion(null, null, null);
        }
    }

    /// <summary>Supplier portal list: that supplier's POs from the ERP,
    /// mapped for display. Failures land in Notes; the page renders the
    /// honest empty state.</summary>
    public async Task<SupplierPoList> ListSupplierPosAsync(
        string vendorId, CancellationToken ct = default)
    {
        var notes = new List<string>();
        IReadOnlyList<PurchaseOrder> orders;
        try
        {
            orders = (await _erp.GetPurchaseOrdersByVendorAsync(vendorId, ct).ConfigureAwait(false))
                .OrderBy(SupplierWorkBand)
                .ThenByDescending(p => p.PoNumber, StringComparer.Ordinal)
                .ToList();
        }
        catch (NotSupportedException ex)
        {
            _log.LogWarning(ex, "PO-by-vendor list is not implemented by the ERP layer yet.");
            notes.Add("Purchase-order list is not implemented by the ERP layer yet.");
            return new SupplierPoList([], notes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PO-by-vendor list failed for {Vendor}.", vendorId);
            notes.Add($"Purchase-order list for {vendorId} failed: {ex.Message}.");
            return new SupplierPoList([], notes);
        }

        return new SupplierPoList(
            orders.Select(ToInboxPo).ToList(), notes);
    }

    /// <summary>Supplier work band for PO ordering. Band 0 is work the
    /// supplier can do now (approved and still open); band 1 waits on
    /// our approval; band 2 is done from the supplier's side
    /// (invoiced, closed, cancelled). The portal opens on the newest
    /// PO in band 0 and lists open POs first.</summary>
    private static int SupplierWorkBand(PurchaseOrder p) =>
        p.ApprovalStatus is PoApprovalStatus.Cancelled
            || p.DocStatus is PoDocStatus.Invoiced or PoDocStatus.Closed or PoDocStatus.Cancelled ? 2
        : p.ApprovalStatus is PoApprovalStatus.Approved or PoApprovalStatus.Confirmed
            && p.DocStatus is PoDocStatus.Open or PoDocStatus.PartiallyReceived
                or PoDocStatus.Received or PoDocStatus.PartiallyInvoiced ? 0
        : 1;

    /// <summary>Case id behind a PO, read back from the case cache. Null when
    /// no case links the PO yet; the portal waits for the case.</summary>
    public async Task<string?> FindCaseIdForPoAsync(
        string poNumber, CancellationToken ct = default)
    {
        try
        {
            var files = await _cases.ListAsync(ct).ConfigureAwait(false);
            var missing = files.Count(f => f.Case is null);
            if (missing > 0)
            {
                _log.LogWarning(
                    "Case lookup skipped {Count} cached file(s) with no case payload.",
                    missing);
            }

            return files
                .Where(f => f.Case is not null)
                .OrderBy(f => f.Case.CaseId, StringComparer.Ordinal)
                .FirstOrDefault(f => string.Equals(f.Case.PoNumber, poNumber, StringComparison.Ordinal))
                ?.Case.CaseId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Case lookup failed for PO {Po}.", poNumber);
            return null;
        }
    }

    /// <summary>Supplier-inbox prefill for one PO: our PO from the ERP, the
    /// linked case's mail thread, ship plus invoice defaults derived from
    /// the runtime PO lines and item pack sizes, and the reply suggestion.
    /// Null when the PO is not in the ERP; every source failure lands in
    /// Notes. A missing case still prefills the documents; the sends wait
    /// for the case and refuse honestly until it links.</summary>
    public async Task<SupplierInboxVm?> GetSupplierInboxForPoAsync(
        string poNumber, CancellationToken ct = default)
    {
        var notes = new List<string>();
        InboxPoVm? po;
        PurchaseOrder? order;
        try
        {
            order = await _erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false);
            if (order is null)
            {
                return null;
            }

            po = ToInboxPo(order);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Inbox PO lookup failed for {Po}.", poNumber);
            notes.Add($"PO {poNumber} read failed: {ex.Message}.");
            return new SupplierInboxVm(
                string.Empty, null, [], null, null,
                new ReplySuggestion(null, null, null), notes, [], SupplierNextAction.Unknown);
        }

        var caseId = await FindCaseIdForPoAsync(poNumber, ct).ConfigureAwait(false);
        if (caseId is null)
        {
            notes.Add($"No case links PO {poNumber} yet; acknowledgement, shipping, and invoicing wait for the case. Run 'Pick up ERP work' on the Admin page once its approvals land.");
        }

        var resolved = caseId ?? string.Empty;
        var mail = resolved.Length == 0
            ? (IReadOnlyList<VendorEmail>)[]
            : await ReadMailAsync(resolved, ct).ConfigureAwait(false);
        var reply = resolved.Length == 0
            ? await SuggestReplyForVendorAsync(po.VendorId, ct).ConfigureAwait(false)
            : await SuggestReplyAsync(resolved, ct).ConfigureAwait(false);

        var asn = await BuildShipPrefillAsync(resolved, po, notes, ct).ConfigureAwait(false);
        // The supplier invoice number suggests the next in the vendor's
        // own series (blank when they have no readable history), so the
        // person only checks and clicks send. Lines mirror ordered
        // qty/price as editable rows.
        var invoiceNumber = await SuggestInvoiceNumberAsync(po.VendorId, notes, ct).ConfigureAwait(false);
        var invoice = new InvoicePrefill(
            invoiceNumber ?? string.Empty,
            po.VendorId,
            po.PoNumber,
            po.Lines.Select(l => new InvoiceLineForm(l.PoLineRef, l.QtyOrdered, l.UnitPrice)).ToList());
        var (sent, next) = await BuildSentStateAsync(order!, resolved, notes, ct).ConfigureAwait(false);

        return new SupplierInboxVm(resolved, po, mail, asn, invoice, reply, notes, sent, next);
    }

    /// <summary>Sent history plus the next supplier action for one PO, read
    /// from the ERP documents (confirmation, ASNs, invoices) and the case
    /// file (ASN received dates). Doc-read failures land in Notes and fall
    /// back to Unknown, which keeps every action visible.</summary>
    private async Task<(IReadOnlyList<SupplierSentStep> Sent, string Next)> BuildSentStateAsync(
        PurchaseOrder order, string caseId, List<string> notes, CancellationToken ct)
    {
        IReadOnlyList<CaseEvent> events = [];
        if (!string.IsNullOrEmpty(caseId))
        {
            try
            {
                var file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
                events = file?.Events ?? [];
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Case events failed to load for {Case}.", caseId);
                notes.Add("Case history failed to load; shipment dates may be missing.");
            }
        }

        VendorConfirmation? confirmation;
        IReadOnlyList<AsnDto> asns;
        IReadOnlyList<VendorInvoice> invoices;
        try
        {
            confirmation = await _erp.GetVendorConfirmationAsync(order.PoNumber, ct).ConfigureAwait(false);
            asns = (await _erp.GetAsnsAsync(ct).ConfigureAwait(false))
                .Where(a => string.Equals(a.PoNumber, order.PoNumber, StringComparison.Ordinal))
                .OrderBy(a => a.AsnNumber, StringComparer.Ordinal)
                .ToList();
            invoices = (await _erp.GetVendorInvoicesAsync(ct).ConfigureAwait(false))
                .Where(i => string.Equals(i.PoNumber, order.PoNumber, StringComparison.Ordinal))
                .OrderBy(i => i.SupplierInvoiceNumber, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sent history failed to load for PO {Po}.", order.PoNumber);
            notes.Add($"Sent history for PO {order.PoNumber} failed to load: {ex.Message}.");
            return ([], SupplierNextAction.Unknown);
        }

        var sent = new List<SupplierSentStep>();
        if (confirmation is not null)
        {
            sent.Add(new SupplierSentStep(
                "Acknowledged", $"Confirmed for {confirmation.ConfirmDate:yyyy-MM-dd}."));
        }

        var asnEvents = events.Where(e => e.Kind == CaseEventKind.AsnRecorded).ToList();
        foreach (var asn in asns)
        {
            var at = asnEvents.FirstOrDefault(e =>
                    string.Equals(e.DocumentNumber, asn.AsnNumber, StringComparison.Ordinal))?.Timestamp
                ?? (asnEvents.Count == 1 ? asnEvents[0].Timestamp : null);
            sent.Add(new SupplierSentStep(
                "Shipment notice",
                at is null ? $"{asn.AsnNumber} received." : $"{asn.AsnNumber} received {at.Value:yyyy-MM-dd}."));
        }

        foreach (var inv in invoices)
        {
            sent.Add(new SupplierSentStep(
                "Invoice",
                inv.CreatedAt is null
                    ? $"{inv.SupplierInvoiceNumber} received."
                    : $"{inv.SupplierInvoiceNumber} received {inv.CreatedAt.Value:yyyy-MM-dd}."));
        }

        if (string.IsNullOrEmpty(caseId))
        {
            return (sent, SupplierNextAction.WaitCase);
        }

        if (order.DocStatus is PoDocStatus.Closed or PoDocStatus.Cancelled
            || order.ApprovalStatus == PoApprovalStatus.Cancelled)
        {
            return (sent, SupplierNextAction.Closed);
        }

        var acknowledged = confirmation is not null || order.ApprovalStatus == PoApprovalStatus.Confirmed;
        if (!acknowledged)
        {
            return (sent, order.ApprovalStatus == PoApprovalStatus.Approved
                ? SupplierNextAction.Acknowledge
                : SupplierNextAction.WaitApproval);
        }

        if (asns.Count == 0)
        {
            return (sent, SupplierNextAction.Ship);
        }

        return (sent, invoices.Count == 0 ? SupplierNextAction.Invoice : SupplierNextAction.Done);
    }

    /// <summary>Supplier-inbox prefill for one case: resolves the case PO and
    /// delegates to the PO-driven inbox. Null when the case has no case
    /// file; every source failure lands in Notes.</summary>
    public async Task<SupplierInboxVm?> GetSupplierInboxAsync(
        string caseId, CancellationToken ct = default)
    {
        var file = await _cases.ReadAsync(caseId, ct).ConfigureAwait(false);
        if (file is null)
        {
            return null;
        }

        if (file.Case.PoNumber is null)
        {
            var mail = await ReadMailAsync(caseId, ct).ConfigureAwait(false);
            var reply = await SuggestReplyAsync(caseId, ct).ConfigureAwait(false);
            return new SupplierInboxVm(
                caseId, null, mail, null, null, reply,
                ["No PO on this case yet; the supplier waits for our order."],
                [], SupplierNextAction.WaitCase);
        }

        return await GetSupplierInboxForPoAsync(file.Case.PoNumber, ct).ConfigureAwait(false);
    }

    private static InboxPoVm ToInboxPo(PurchaseOrder order) =>
        new(
            order.PoNumber, order.VendorId, order.PaymentTermsId,
            ErpStatusWords.Of(order.ApprovalStatus), ErpStatusWords.Of(order.DocStatus),
            order.Currency,
            order.Lines.Select(l => new InboxPoLineVm(
                l.PoLineRef, l.ItemId, l.QtyOrdered, l.UnitPrice, l.LineAmount)).ToList(),
            order.Lines.Sum(l => l.LineAmount));

    private async Task<ReplySuggestion> SuggestReplyForVendorAsync(
        string vendorId, CancellationToken ct)
    {
        try
        {
            var vendor = (await _erp.GetVendorsAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(v => string.Equals(v.VendorId, vendorId, StringComparison.Ordinal));
            return new ReplySuggestion(vendor?.Email, null, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Reply suggestion lookup failed for vendor {Vendor}.", vendorId);
            return new ReplySuggestion(null, null, null);
        }
    }

    private async Task<AsnSubmission> BuildShipPrefillAsync(
        string caseId, InboxPoVm po, List<string> notes, CancellationToken ct)
    {
        var packs = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var itemId in po.Lines.Select(l => l.ItemId).Distinct(StringComparer.Ordinal))
        {
            try
            {
                packs[itemId] = (await _erp.GetItemAsync(itemId, ct).ConfigureAwait(false))?.UnitsPerCarton;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Pack-size lookup failed for {Item}.", itemId);
                notes.Add($"Pack size for {itemId} failed: {ex.Message}.");
                packs[itemId] = null;
            }
        }

        var cartons = OutsideShipPlan.Build(
            po.Lines,
            itemId => packs.TryGetValue(itemId, out var units) ? units : null,
            notes);
        // The ASN number stays blank so the ERP mints it from our ASN
        // sequence on send. The packing slip is the supplier's own
        // number: suggest the next in their series, editable before
        // sending. Nothing derives from the PO.
        var slip = await SuggestPackingSlipAsync(po, notes, ct).ConfigureAwait(false);
        return new AsnSubmission(caseId, string.Empty, slip ?? string.Empty, cartons);
    }

    /// <summary>Suggests the vendor's next number in their own series:
    /// the learned prefix and date shape stay, only the date tag moves
    /// to today. Unknown shapes return null (no guess): the supplier
    /// types the number as printed. Pure logic over the vendor's own
    /// history; no vendor data lives here.</summary>
    private static string? SuggestVendorSeriesNumber(string? learned, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(learned) || string.IsNullOrWhiteSpace(learned.Trim().Split('-')[0]))
        {
            return null;
        }

        var parts = learned.Trim().Split('-');
        if (parts.Length >= 4
            && parts[^1].Length == 2 && IsDigits(parts[^1])
            && parts[^2].Length == 2 && IsDigits(parts[^2])
            && parts[^3].Length == 4 && IsDigits(parts[^3]))
        {
            return string.Join('-', parts[..^3]) + $"-{today:yyyy-MM-dd}";
        }

        if (parts.Length >= 2 && parts[^1].Length == 8 && IsDigits(parts[^1]))
        {
            return string.Join('-', parts[..^1]) + $"-{today:yyyyMMdd}";
        }

        return null;
    }

    private static bool IsDigits(string s) =>
        s.Length > 0 && s.All(c => c is >= '0' and <= '9');

    private async Task<string?> SuggestPackingSlipAsync(
        InboxPoVm po, List<string> notes, CancellationToken ct)
    {
        try
        {
            var poNumbers = (await _erp.GetPurchaseOrdersByVendorAsync(po.VendorId, ct).ConfigureAwait(false))
                .Select(p => p.PoNumber)
                .ToHashSet(StringComparer.Ordinal);
            var latest = (await _erp.GetShipmentReceiptsAsync(ct).ConfigureAwait(false))
                .Where(r => poNumbers.Contains(r.PoNumber)
                    && !string.IsNullOrWhiteSpace(r.SupplierPackingSlipNumber))
                .MaxBy(r => r.ReceiptNumber, StringComparer.Ordinal)
                ?.SupplierPackingSlipNumber;
            return SuggestVendorSeriesNumber(latest, DateOnly.FromDateTime(DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Packing-slip suggestion lookup failed for vendor {Vendor}.", po.VendorId);
            notes.Add($"Packing-slip suggestion failed: {ex.Message}.");
            return null;
        }
    }

    private async Task<string?> SuggestInvoiceNumberAsync(
        string vendorId, List<string> notes, CancellationToken ct)
    {
        try
        {
            var latest = (await _erp.GetVendorInvoicesAsync(ct).ConfigureAwait(false))
                .Where(i => string.Equals(i.VendorId, vendorId, StringComparison.Ordinal))
                .MaxBy(i => i.SupplierInvoiceNumber, StringComparer.Ordinal)
                ?.SupplierInvoiceNumber;
            return SuggestVendorSeriesNumber(latest, DateOnly.FromDateTime(DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Invoice suggestion lookup failed for vendor {Vendor}.", vendorId);
            notes.Add($"Invoice suggestion failed: {ex.Message}.");
            return null;
        }
    }

    /// <summary>Expected trucks at the dock: unreceived ASNs from the ERP
    /// plus each ASN's PO vendor and linked case. Failures land in Notes;
    /// the page renders the honest empty state.</summary>
    public async Task<ExpectedTrucks> ListExpectedTrucksAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();
        IReadOnlyList<AsnDto> asns;
        try
        {
            asns = (await _erp.GetUnreceivedAsnsAsync(ct).ConfigureAwait(false))
                .OrderBy(a => a.AsnNumber, StringComparer.Ordinal)
                .ToList();
        }
        catch (NotSupportedException ex)
        {
            _log.LogWarning(ex, "Unreceived-ASN list is not implemented by the ERP layer yet.");
            notes.Add("Expected-truck list is not implemented by the ERP layer yet.");
            return new ExpectedTrucks([], notes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unreceived-ASN list failed.");
            notes.Add($"Expected-truck list failed: {ex.Message}.");
            return new ExpectedTrucks([], notes);
        }

        IReadOnlyList<CaseFile> files = [];
        try
        {
            files = await _cases.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Case-cache list failed for the dock.");
            notes.Add($"Case links unavailable: {ex.Message}.");
        }

        var trucks = new List<ExpectedTruckVm>();
        var listed = new HashSet<string>(StringComparer.Ordinal);

        // Cases awaiting dock evidence: the ASN intake posts the receipt in
        // the same step, so "unreceived" alone would hide every case truck.
        // A case truck stays while its cartons lack verdicts, while dock
        // reviews pend, or while holds keep lots back (reshot may follow).
        foreach (var file in files.Where(f => !string.IsNullOrEmpty(f.Case.AsnNumber)))
        {
            AsnDto? asn;
            try
            {
                asn = await _erp.GetAsnAsync(file.Case.AsnNumber!, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ASN lookup failed for case {Case}.", file.Case.CaseId);
                notes.Add($"ASN {file.Case.AsnNumber} for case {file.Case.CaseId} failed: {ex.Message}.");
                continue;
            }

            if (asn is null)
            {
                notes.Add($"ASN {file.Case.AsnNumber} for case {file.Case.CaseId} is missing from the ERP.");
                continue;
            }

            var docked = file.Events.Any(e => e.Kind == CaseEventKind.DockEvaluated);
            var pendingReview = file.Decisions.Any(d =>
                d.Status == DecisionStatus.Pending
                && d.Kind is DecisionKind.DockReview or DecisionKind.DamageReview);
            var openHolds = false;
            if (file.Case.ReceiptNumber is not null)
            {
                try
                {
                    openHolds = (await _erp.GetOpenHoldsAsync(file.Case.ReceiptNumber, ct).ConfigureAwait(false))
                        .Count > 0;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Hold lookup failed for receipt {Receipt}.", file.Case.ReceiptNumber);
                    notes.Add($"Hold check for receipt {file.Case.ReceiptNumber} failed: {ex.Message}.");
                }
            }

            if (docked && !pendingReview && !openHolds)
            {
                continue;
            }

            trucks.Add(new ExpectedTruckVm(
                asn.AsnNumber, asn.PoNumber, await VendorForAsnAsync(asn, notes, ct).ConfigureAwait(false),
                file.Case.CaseId,
                asn.Cartons.Select(c => new ExpectedTruckCarton(
                    c.CartonId, c.ItemId, c.UnitsPerCarton, c.LotRef)).ToList()));
            listed.Add(asn.AsnNumber);
        }

        // Orphan unreceived ASNs (no case yet): photos wait for the case.
        foreach (var asn in asns.Where(a => !listed.Contains(a.AsnNumber)))
        {
            var linked = files.FirstOrDefault(f =>
                    string.Equals(f.Case.AsnNumber, asn.AsnNumber, StringComparison.Ordinal))
                ?? files.FirstOrDefault(f =>
                    string.Equals(f.Case.PoNumber, asn.PoNumber, StringComparison.Ordinal));
            trucks.Add(new ExpectedTruckVm(
                asn.AsnNumber, asn.PoNumber, await VendorForAsnAsync(asn, notes, ct).ConfigureAwait(false),
                linked?.Case.CaseId,
                asn.Cartons.Select(c => new ExpectedTruckCarton(
                    c.CartonId, c.ItemId, c.UnitsPerCarton, c.LotRef)).ToList()));
        }

        return new ExpectedTrucks(trucks, notes);
    }

    private async Task<string?> VendorForAsnAsync(AsnDto asn, List<string> notes, CancellationToken ct)
    {
        try
        {
            var po = await _erp.GetPurchaseOrderAsync(asn.PoNumber, ct).ConfigureAwait(false);
            return po?.VendorId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PO lookup failed for ASN {Asn}.", asn.AsnNumber);
            notes.Add($"PO {asn.PoNumber} for truck {asn.AsnNumber} failed: {ex.Message}.");
            return null;
        }
    }

    public async Task<IReadOnlyList<PoLineOption>> GetPoLinesAsync(
        string poNumber, CancellationToken ct = default)
    {
        try
        {
            var po = await _erp.GetPurchaseOrderAsync(poNumber, ct).ConfigureAwait(false);
            return po?.Lines
                .Select(l => new PoLineOption(l.PoLineRef, l.ItemId, l.QtyOrdered))
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PO line lookup failed for {PoNumber}.", poNumber);
            return [];
        }
    }

    /// <summary>Reads one server dock image to bytes for posting. Returns the
    /// refusal reason instead of throwing for unknown or refused names.</summary>
    public async Task<(byte[]? Bytes, string? Error)> TryReadDockImageAsync(
        string fileName, CancellationToken ct = default)
    {
        try
        {
            var bytes = await _photos.ReadImageBytesAsync(fileName, ct).ConfigureAwait(false);
            return bytes is null
                ? (null, $"Dock image '{fileName}' is not available in the dock directory.")
                : (bytes, null);
        }
        catch (Exception ex)
        {
            return (null, $"Dock image '{fileName}' could not be read: {ex.Message}.");
        }
    }

    private SimulationOutcome? RequireCaseAndPhotos(
        string? caseId, IReadOnlyList<SimulationPhoto>? photos, string label)
    {
        if (string.IsNullOrWhiteSpace(caseId))
        {
            return Refusal($"Case is required for {label}.");
        }

        if (photos is null || photos.Count == 0)
        {
            return Refusal($"Add at least one photo for {label}.");
        }

        foreach (var photo in photos)
        {
            if (photo.Jpeg.Length == 0)
            {
                return Refusal($"Photo {photo.ImageId} is empty; pick the file again.");
            }
        }

        return null;
    }

    private async Task<SimulationOutcome> PostAsync<T>(
        string path, T input, Func<string, SimulationOutcome> parse, CancellationToken ct)
    {
        string sent;
        try
        {
            sent = JsonSerializer.Serialize(input, JsonOpts);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Simulation input for {Path} did not serialize.", path);
            return Refusal($"The {path} input could not be serialized: {ex.Message}.");
        }

        HttpResponseMessage? response = null;
        Exception? transportError = null;
        for (var attempt = 1; attempt <= 2 && response is null; attempt++)
        {
            try
            {
                using var content = new StringContent(sent, System.Text.Encoding.UTF8, "application/json");
                response = await _http.PostAsync(path, content, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                transportError = ex;
                // Retry transport failures and client timeouts once with the
                // same idempotent payload; a caller cancellation never retries.
                if (attempt == 1 && (ex is HttpRequestException || !ct.IsCancellationRequested))
                {
                    _log.LogWarning(ex, "POST {Path} attempt {Attempt} failed; retrying once.", path, attempt);
                    continue;
                }

                _log.LogWarning(ex, "POST {Path} failed after {Attempt} attempt(s).", path, attempt);
                return new SimulationOutcome(
                    false,
                    $"POST {path} failed before the simulation ran ({attempt} attempt(s)): {ex.Message}.",
                    [],
                    []);
            }
        }

        if (response is null)
        {
            _log.LogWarning(
                transportError, "POST {Path} produced no response after retrying.", path);
            return new SimulationOutcome(
                false,
                $"POST {path} failed before the simulation ran (2 attempts): {transportError?.Message}.",
                [],
                []);
        }

        string body;
        try
        {
            using (response)
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning(
                        "POST {Path} returned {Status}: {Body}.", path, (int)response.StatusCode, body);
                    return new SimulationOutcome(
                        false,
                        $"POST {path} returned {(int)response.StatusCode} ({response.ReasonPhrase}).",
                        [Excerpt(body)],
                        []);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "POST {Path} response could not be read.", path);
            return new SimulationOutcome(false, $"POST {path} answered unreadably: {ex.Message}.", [], []);
        }

        try
        {
            return parse(body);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "POST {Path} answered an unexpected shape: {Body}.", path, body);
            return new SimulationOutcome(
                true, $"POST {path} succeeded but the response shape was unexpected: {ex.Message}.",
                [Excerpt(body)], []);
        }
    }

    private static SimulationOutcome ParseOrder(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var caseIds = StringArray(root, "caseIds");
        var details = caseIds
            .Select(id => $"Case {id} opened; the agent is on it.")
            .ToList();
        details.Add(BackgroundNote);
        return new SimulationOutcome(
            true, $"Order accepted: {caseIds.Count} case(s).", details, caseIds);
    }

    private static SimulationOutcome ParsePickup(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var touched = StringArray(root, "touched");
        return new SimulationOutcome(
            true,
            touched.Count == 0
                ? "ERP work is already picked up: no new cases."
                : $"Picked up {touched.Count} ERP-work case(s).",
            touched.Select(id => $"Case {id} adopted from ERP.").ToList(),
            touched);
    }

    private static SimulationOutcome ParseAck(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var po = Text(root, "poNumber") ?? "unknown PO";
        return new SimulationOutcome(
            true, $"PO {po} acknowledged.", [BackgroundNote], []);
    }

    private static SimulationOutcome ParseAsn(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var asn = Text(root, "asnNumber") ?? "unknown ASN";
        var receipt = Text(root, "receiptNumber");
        var details = new List<string>();
        details.Add(receipt is null
            ? "No receipt posted with the notice."
            : $"Receipt {receipt} posted with the notice.");
        details.Add("Pallet views build in the background; they land on the dock tab when ready.");
        details.Add(BackgroundNote);
        return new SimulationOutcome(
            true, $"Shipment notice {asn} recorded.", details, []);
    }

    private static SimulationOutcome ParseDock(string body, string label)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var count = Text(root, "photoCount") ?? "0";
        return new SimulationOutcome(
            true,
            $"{label} received: {count} photo(s). The outcome lands below when ready.",
            [],
            []);
    }

    private static SimulationOutcome ParseInvoicePhotos(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var count = Text(root, "photoCount") ?? "0";
        return new SimulationOutcome(
            true,
            $"Invoice photos received: {count} photo(s). The outcome lands below when ready.",
            [BackgroundNote],
            []);
    }

    private static SimulationOutcome ParseInvoice(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var invoice = Text(root, "invoice") ?? "unkeyed invoice";
        var persisted = Bool(root, "persisted");
        var accepted = Text(root, "acceptedValue") ?? "?";
        var held = Text(root, "heldValue") ?? "?";
        var details = new List<string>
        {
            $"Persisted: {(persisted ? "yes" : "no")}, accepted value {accepted}, held value {held}.",
            BackgroundNote,
        };
        return new SimulationOutcome(true, $"Invoice {invoice} intaken.", details, []);
    }

    private static SimulationOutcome ParseReply(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var emailId = Text(root, "emailId") ?? "unknown mail";
        return new SimulationOutcome(
            true, $"Vendor reply recorded: {emailId}.", [BackgroundNote], []);
    }

    private static SimulationOutcome Refusal(string message) =>
        new(false, message, [], []);

    /// <summary>Fresh idempotency key per human submission. Retries reuse the
    /// same key, so a lost response never double-injects the input.</summary>
    private static string FreshKey() => Guid.NewGuid().ToString("N");

    private static string Excerpt(string body) =>
        body.Length <= BodyExcerptChars ? body : body[..BodyExcerptChars] + "…";

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ReplySubject(string subject) =>
        subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject : $"Re: {subject}";

    private static JsonElement? TryProp(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return prop.Value;
            }
        }

        return null;
    }

    private static string? Text(JsonElement root, string name)
    {
        var value = TryProp(root, name);
        return value switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString(),
            { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
            { ValueKind: JsonValueKind.True } => "true",
            { ValueKind: JsonValueKind.False } => "false",
            _ => null,
        };
    }

    private static bool Bool(JsonElement root, string name) =>
        TryProp(root, name) is { ValueKind: JsonValueKind.True };

    private static string ErrorText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return Text(doc.RootElement, "error") ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static IReadOnlyList<string> StringArray(JsonElement root, string name)
    {
        if (TryProp(root, name) is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? string.Empty)
            .Where(s => s.Length > 0)
            .ToList();
    }
    private async Task<IReadOnlyList<string>> ListDockImagesAsync(List<string> notes, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(_photos.ImageDir))
            {
                notes.Add("Dock image directory is missing; only uploaded photos can be sent.");
                return [];
            }

            // Base originals are working files, never counter photos; the
            // dock panel splits the rest into pallet views vs close-ups.
            // Only files minted this lifetime (recorded in a case's
            // dock-views sidecar) are offered: a view minted in a prior
            // lifetime names the same cartons but shows another shipment,
            // and ticking it would hold the wrong goods.
            var minted = (await _cases.ListDockViewFilesAsync(ct).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
            var onDisk = Directory.GetFiles(_photos.ImageDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null
                    && (n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || n.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                    && !DockPhotoService.IsBaseImage(n))
                .Select(n => n!)
                .ToList();
            var stale = onDisk.Count(n => !minted.Contains(n));
            if (stale > 0)
            {
                notes.Add($"{stale} stored photo(s) were not built for the current cases and are not offered.");
            }

            return onDisk
                .Where(minted.Contains)
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            notes.Add($"Dock image list failed: {ex.Message}.");
            return [];
        }
    }
}

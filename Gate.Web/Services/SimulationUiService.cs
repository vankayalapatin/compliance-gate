// UI-side driver for the simulation endpoints. The Simulate surface offers
// option lists built from runtime sources only (scenario file, run
// manifest, case cache, dock image directory, ERP adapter) and posts the
// human's external inputs (order webhooks, camera photos, supplier
// invoices, vendor replies) to the existing endpoints in
// SimulationEndpoints.cs. This service never sequences internal logic: the
// endpoints run, then advance, and this layer only shapes the outcome for
// display. Failures and refusals surface with the path, status, and body
// excerpt; nothing is silently omitted.
using System.Text.Json;
using Gate.Agent;
using Gate.Contracts;
using Gate.Erp;
using Gate.Web.ViewModels;

namespace Gate.Web.Services;

public sealed class SimulationUiService
{
    // Paths pinned to SimulationEndpoints.cs; SimulationUiTests pins them too.
    public const string OrderPath = "/simulation/shopify/order";
    public const string DockPhotosPath = "/simulation/dock/photos";
    public const string ReshotPath = "/simulation/dock/reshot";
    public const string InvoicePath = "/simulation/supplier/invoice";
    public const string InvoicePhotoPath = "/simulation/supplier/invoice-photo";
    public const string EmailReplyPath = "/simulation/supplier/email-reply";

    private const int BodyExcerptChars = 2000;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IErpAdapter _erp;
    private readonly CaseCacheStore _cases;
    private readonly DockPhotoService _photos;
    private readonly IConfiguration _config;
    private readonly ILogger<SimulationUiService> _log;

    public SimulationUiService(
        HttpClient http,
        IErpAdapter erp,
        CaseCacheStore cases,
        DockPhotoService photos,
        IConfiguration config,
        ILogger<SimulationUiService> log)
    {
        _http = http;
        _erp = erp;
        _cases = cases;
        _photos = photos;
        _config = config;
        _log = log;
    }

    public async Task<SimulateOptions> GetOptionsAsync(CancellationToken ct = default)
    {
        var notes = new List<string>();
        var scenario = ReadScenario(notes);
        var (soNumbers, poNumbers) = ReadManifestNumbers(notes);

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

        IReadOnlyList<CaseOption> cases = [];
        try
        {
            cases = (await _cases.ListAsync(ct).ConfigureAwait(false))
                .OrderBy(f => f.Case.CaseId, StringComparer.Ordinal)
                .Select(f => new CaseOption(
                    f.Case.CaseId, f.Case.SoNumber, f.Case.ItemId, f.Case.Stage.ToString(),
                    f.Case.PoNumber))
                .ToList();
        }
        catch (Exception ex)
        {
            notes.Add($"Case cache list failed: {ex.Message}.");
        }

        return new SimulateOptions(
            scenario.Name, scenario.Currency, soNumbers, poNumbers, users, vendors,
            cases, scenario.CartonIds, ListDockImages(notes), scenario.Items,
            scenario.Candidates, notes);
    }

    public async Task<SimulationOutcome> SendOrderAsync(
        OrderSubmission submission, CancellationToken ct = default)
    {
        if (submission is null)
        {
            return Refusal("No order submission was provided.");
        }

        if (string.IsNullOrWhiteSpace(submission.SoNumber))
        {
            return Refusal("SO number is required; pick a seeded order or type a new shop order id.");
        }

        if (string.IsNullOrWhiteSpace(submission.ScenarioName))
        {
            return Refusal("Scenario name is required; reload the options when the scenario file is present.");
        }

        List<SalesOrderLine>? lines = null;
        if (submission.Lines is not null && submission.Lines.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(submission.Currency))
            {
                return Refusal(
                    $"New shop order {submission.SoNumber.Trim()} carries lines but no currency; the ERP cannot mint the SO without one.");
            }

            lines = [];
            foreach (var line in submission.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.ItemId))
                {
                    return Refusal("Every new-order line needs an item.");
                }

                if (line.Quantity <= 0)
                {
                    return Refusal($"New-order line for {line.ItemId} needs a quantity above zero.");
                }

                lines.Add(new SalesOrderLine(line.ItemId.Trim(), line.Quantity));
            }
        }

        var input = new OrderWebhookInput(
            submission.ScenarioName.Trim(),
            submission.SoNumber.Trim(),
            BlankToNull(submission.Requester),
            submission.CandidateVendorsByItem,
            FreshKey(),
            Lines: lines,
            Currency: BlankToNull(submission.Currency));
        return await PostAsync(OrderPath, input, ParseOrder, ct).ConfigureAwait(false);
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

        // Blank number, vendor, or PO is a valid missing-fields input: the
        // runner holds it for the AP clerk instead of refusing it.
        var input = new SupplierInvoiceInput(
            caseId.Trim(),
            supplierInvoiceNumber?.Trim() ?? string.Empty,
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
        return await PostAsync(InvoicePhotoPath, request, ParseInvoice, ct).ConfigureAwait(false);
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
        var details = new List<string>();
        if (TryProp(root, "advances") is { ValueKind: JsonValueKind.Array } advances)
        {
            foreach (var advance in advances.EnumerateArray())
            {
                var id = Text(advance, "caseId") ?? "unknown case";
                var stage = Text(advance, "stage") ?? "?";
                var step = Text(advance, "step") ?? "?";
                var done = Bool(advance, "complete");
                details.Add($"{id}: {stage} / {step}{(done ? " (complete)" : string.Empty)}.");
            }
        }

        if (details.Count == 0)
        {
            details.Add(Excerpt(body));
        }

        return new SimulationOutcome(
            true, $"Order accepted: {caseIds.Count} case(s).", details, caseIds);
    }

    private static SimulationOutcome ParseDock(string body, string label)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var cartons = StringArray(root, "cartons");
        return new SimulationOutcome(
            true,
            $"{label} accepted: {(cartons.Count == 0 ? "no cartons read" : string.Join(", ", cartons))}.",
            AdvanceDetails(root),
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
        };
        details.AddRange(AdvanceDetails(root));
        return new SimulationOutcome(true, $"Invoice {invoice} intaken.", details, []);
    }

    private static SimulationOutcome ParseReply(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var emailId = Text(root, "emailId") ?? "unknown mail";
        return new SimulationOutcome(
            true, $"Vendor reply recorded: {emailId}.", AdvanceDetails(root), []);
    }

    private static IReadOnlyList<string> AdvanceDetails(JsonElement root)
    {
        var step = Text(root, "step");
        var done = Bool(root, "complete");
        return step is null
            ? []
            : [$"Case advanced to {step}{(done ? " (complete)" : string.Empty)}."];
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

    private sealed record ScenarioBits(
        string Name,
        string Currency,
        IReadOnlyList<string> CartonIds,
        IReadOnlyList<ItemOption> Items,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates);

    private ScenarioBits ReadScenario(List<string> notes)
    {
        var empty = new ScenarioBits(
            string.Empty, string.Empty, [], [],
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal));
        try
        {
            var path = ScenarioLoader.ResolvePath(ScenarioEndpoints.ScenarioPath(_config));
            if (!File.Exists(path))
            {
                notes.Add("No scenario file; order, carton, and candidate options are empty until one loads.");
                return empty;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? string.Empty
                : string.Empty;
            var currency = root.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() ?? string.Empty
                : string.Empty;
            var cartons = new List<string>();
            if (root.TryGetProperty("cartons", out var cartonJson) && cartonJson.ValueKind == JsonValueKind.Array)
            {
                foreach (var carton in cartonJson.EnumerateArray())
                {
                    if (carton.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var value = id.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            cartons.Add(value);
                        }
                    }
                }
            }

            var items = new List<ItemOption>();
            if (root.TryGetProperty("items", out var itemsJson) && itemsJson.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsJson.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp)
                        && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }

                    var unit = item.TryGetProperty("unit", out var unitProp)
                        && unitProp.ValueKind == JsonValueKind.String ? unitProp.GetString() : null;
                    items.Add(new ItemOption(id, unit ?? string.Empty));
                }
            }

            var candidates = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (root.TryGetProperty("vendors", out var vendorsJson) && vendorsJson.ValueKind == JsonValueKind.Array)
            {
                foreach (var vendor in vendorsJson.EnumerateArray())
                {
                    var id = vendor.TryGetProperty("id", out var idProp)
                        && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
                    var item = vendor.TryGetProperty("item", out var itemProp)
                        && itemProp.ValueKind == JsonValueKind.String ? itemProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(item))
                    {
                        continue;
                    }

                    if (!candidates.TryGetValue(item, out var list))
                    {
                        list = [];
                        candidates[item] = list;
                    }

                    if (!list.Contains(id, StringComparer.Ordinal))
                    {
                        list.Add(id);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                notes.Add("Scenario file has no name; order submissions need one typed in.");
            }

            return new ScenarioBits(
                name, currency, cartons, items,
                candidates.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<string>)kv.Value.Order(StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal));
        }
        catch (Exception ex)
        {
            notes.Add($"Scenario options failed: {ex.Message}.");
            return empty;
        }
    }

    private (IReadOnlyList<string> So, IReadOnlyList<string> Po) ReadManifestNumbers(List<string> notes)
    {
        try
        {
            var path = ScenarioLoader.ResolvePath(ScenarioEndpoints.ManifestPath(_config));
            if (!File.Exists(path))
            {
                notes.Add("No run manifest yet; seeded SO and PO options are empty until a scenario load.");
                return ([], []);
            }

            var manifest = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(path), JsonOpts);
            if (manifest?.Documents is null)
            {
                notes.Add($"Run manifest {path} did not parse.");
                return ([], []);
            }

            var so = manifest.Documents
                .Where(d => string.Equals(d.DocType, "SO", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Number)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            var po = manifest.Documents
                .Where(d => string.Equals(d.DocType, "PO", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Number)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            return (so, po);
        }
        catch (Exception ex)
        {
            notes.Add($"Run manifest read failed: {ex.Message}.");
            return ([], []);
        }
    }

    private sealed record ManifestDto(IReadOnlyList<ManifestDoc>? Documents);

    private sealed record ManifestDoc(string Event, string DocType, string Number);

    private IReadOnlyList<string> ListDockImages(List<string> notes)
    {
        try
        {
            if (!Directory.Exists(_photos.ImageDir))
            {
                notes.Add("Dock image directory is missing; only uploaded photos can be sent.");
                return [];
            }

            return Directory.GetFiles(_photos.ImageDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null
                    && (n.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                        || n.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
                .Select(n => n!)
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

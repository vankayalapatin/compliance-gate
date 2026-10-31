// App-time photo transcription. Dock photos reuse the pipeline read prompt
// and parser (carton-read-v1), so app reads share the vision cache; invoice
// photos are new (invoice-read-v1). Reads are transcription only: C# validates
// every value against the ERP before it enters the intake flow. Class 9
// requirement is a part property from the item master (decision 39), never a
// model observation; unmatched vendor/PO resolve to null so the invoice
// routes to the missing-fields path, never invented.
using System.Text.Json;
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Agent;

public partial class CaseRunner
{
    public const string InvoiceReadPrompt =
        "Transcribe this supplier invoice photo. " +
        "Return a JSON object with exactly these keys: " +
        "supplier_invoice_number (string or null), " +
        "vendor_id_or_name (string or null: the supplier id or name as printed), " +
        "po_number (string or null), " +
        "lines (array of {po_line_ref (string or null), quantity (number), unit_price (number)}), " +
        "bank_account_ref (string or null: the remittance account as printed). " +
        "Transcribe only what you see; never guess missing text, use null. " +
        "Return ONLY the JSON object, no other text.";

    /// <summary>Transcribes dock photos into carton reads, cached by image
    /// hash. CartonHint fills a blank label id only; the part must resolve in
    /// the ERP (unknown parts are a gap error, never guessed).</summary>
    public async Task<IReadOnlyList<DockCartonRead>> TranscribeDockPhotosAsync(
        string caseId, IReadOnlyList<DockPhoto> photos, CancellationToken ct = default)
    {
        _ = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var cache = new VisionReadCache(_visionReadCacheDir);
        var reads = new List<DockCartonRead>();
        foreach (var photo in photos)
        {
            var text = await ReadPhotoAsync(
                cache, photo.ImageId, photo.Jpeg, VisionReadCache.CartonReadV1,
                DockImagePipeline.ReadPrompt, isArray: true, ct).ConfigureAwait(false);
            foreach (var element in DockImagePipeline.ParseReads(text))
            {
                reads.Add(await ToDockReadAsync(element, photo, ct).ConfigureAwait(false));
            }
        }

        _logger.LogInformation(
            "Case {CaseId}: transcribed {Photos} dock photo(s) into {Reads} read(s).",
            caseId, photos.Count, reads.Count);
        return reads;
    }

    /// <summary>Transcribes invoice photos (one per page) into a single
    /// validated invoice, cached by image hash. Scalars take the first
    /// non-blank transcription across pages; lines concatenate in photo
    /// order. Vendor and PO resolve against the ERP or come back null.</summary>
    public async Task<TranscribedInvoice> TranscribeInvoicePhotosAsync(
        string caseId, IReadOnlyList<InvoicePhoto> photos, CancellationToken ct = default)
    {
        _ = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var cache = new VisionReadCache(_visionReadCacheDir);
        string? number = null;
        string? vendor = null;
        string? po = null;
        string? bank = null;
        var lines = new List<SupplierInvoiceLineInput>();
        foreach (var photo in photos)
        {
            var text = await ReadPhotoAsync(
                cache, photo.ImageId, photo.Jpeg, VisionReadCache.InvoiceReadV1,
                InvoiceReadPrompt, isArray: false, ct).ConfigureAwait(false);
            var page = ParseTranscribedInvoice(text);
            number ??= BlankToNull(page.SupplierInvoiceNumber);
            vendor ??= BlankToNull(page.VendorIdOrName);
            po ??= BlankToNull(page.PoNumber);
            bank ??= BlankToNull(page.BankAccountRef);
            lines.AddRange(page.Lines);
        }

        var resolvedVendor = await ResolveVendorIdAsync(vendor, ct).ConfigureAwait(false);
        var resolvedPo = await ResolvePoNumberAsync(po, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Case {CaseId}: transcribed {Photos} invoice photo(s): number {Number}, vendor '{RawVendor}' -> '{Vendor}', po '{RawPo}' -> '{Po}', {Lines} line(s).",
            caseId, photos.Count, number ?? "null", vendor ?? "null", resolvedVendor ?? "null",
            po ?? "null", resolvedPo ?? "null", lines.Count);
        return new TranscribedInvoice(number, resolvedVendor, resolvedPo, lines, bank);
    }

    private async Task<string> ReadPhotoAsync(
        VisionReadCache cache, string imageId, byte[] jpeg, string promptVersion,
        string prompt, bool isArray, CancellationToken ct)
    {
        var sha = VisionReadCache.Sha256Hex(jpeg);
        var cached = cache.TryRead(sha, promptVersion);
        if (cached is not null)
        {
            return cached.Text;
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await ResilientCalls.CompleteWithRetryAsync(
                _models,
                new ModelRequest(prompt, MaxTokens: 2000, ImageJpeg: jpeg),
                _logger, $"photo read for {imageId}", ct).ConfigureAwait(false);
            try
            {
                if (isArray)
                {
                    DockImagePipeline.ParseReads(result.Text);
                }
                else
                {
                    ParseTranscribedInvoice(result.Text);
                }

                cache.Write(sha, promptVersion, _models.ModelName, result.Text);
                return result.Text;
            }
            catch (JsonException ex)
            {
                lastError = ex;
                _logger.LogWarning(ex,
                    "Photo read for {ImageId} did not parse (attempt {Attempt}); retrying once.",
                    imageId, attempt + 1);
            }
        }

        throw new InvalidOperationException(
            $"Photo read for {imageId} failed after one retry; refusing to cache it.", lastError);
    }

    private async Task<DockCartonRead> ToDockReadAsync(
        JsonElement element, DockPhoto photo, CancellationToken ct)
    {
        var cartonId = ReadString(element, "carton_id") ?? photo.CartonHint
            ?? throw new InvalidOperationException(
                $"Photo {photo.ImageId} has a label with no carton id and no carton hint.");
        var part = ReadString(element, "part");
        if (string.IsNullOrWhiteSpace(part))
        {
            throw new InvalidOperationException(
                $"Photo {photo.ImageId} label {cartonId} has no part; refusing to guess one.");
        }

        var item = await _erp.GetItemAsync(part, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Photo {photo.ImageId} label {cartonId} names part {part}, which is not in the ERP.");
        return new DockCartonRead(
            cartonId,
            part,
            ReadString(element, "po_number"),
            ReadString(element, "lot_code"),
            ReadInt(element, "units_per_carton", photo.ImageId, cartonId),
            ReadBool(element, "mark_present"),
            ReadBool(element, "mark_legible"),
            ReadString(element, "un_number"),
            item.IsCritical,
            ReadBool(element, "class9_visible"),
            ReadBool(element, "damage_visible"),
            ReadString(element, "damage_note"),
            photo.ImageId,
            ReadDecimal(element, "read_confidence"));
    }

    /// <summary>Parses one transcribed-invoice JSON object (vision page or
    /// vendor-reply extraction). Values stay raw; callers resolve vendor and
    /// PO against the ERP. Money fields are required: missing quantity or
    /// price is a gap error, never a default.</summary>
    private static TranscribedInvoice ParseTranscribedInvoice(string text)
    {
        using var doc = JsonDocument.Parse(StripFences(text));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Transcribed invoice must be a JSON object.");
        }

        var lines = new List<SupplierInvoiceLineInput>();
        if (root.TryGetProperty("lines", out var linesJson)
            && linesJson.ValueKind == JsonValueKind.Array)
        {
            foreach (var line in linesJson.EnumerateArray())
            {
                if (!line.TryGetProperty("quantity", out var qty)
                    || qty.ValueKind != JsonValueKind.Number
                    || !line.TryGetProperty("unit_price", out var price)
                    || price.ValueKind != JsonValueKind.Number)
                {
                    throw new JsonException("Invoice line needs a numeric quantity and unit_price.");
                }

                lines.Add(new SupplierInvoiceLineInput(
                    line.TryGetProperty("po_line_ref", out var lineRef)
                    && lineRef.ValueKind == JsonValueKind.String
                        ? lineRef.GetString()
                        : null,
                    qty.GetDecimal(),
                    price.GetDecimal()));
            }
        }

        return new TranscribedInvoice(
            ReadString(root, "supplier_invoice_number"),
            ReadString(root, "vendor_id_or_name"),
            ReadString(root, "po_number"),
            lines,
            ReadString(root, "bank_account_ref"));
    }

    private static string ReadReplySummary(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripFences(text));
            var summary = ReadString(doc.RootElement, "summary");
            return string.IsNullOrWhiteSpace(summary) ? "no summary extracted." : summary;
        }
        catch (JsonException)
        {
            return "no summary extracted.";
        }
    }

    private static string StripFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var lines = trimmed.Split('\n').ToList();
        lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Trim().StartsWith("```", StringComparison.Ordinal))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    private static string? ReadString(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.ValueKind == JsonValueKind.True;

    private static decimal ReadDecimal(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : 0m;

    private static int ReadInt(JsonElement element, string key, string imageId, string cartonId) =>
        element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var actual)
            ? actual
            : throw new InvalidOperationException(
                $"Photo {imageId} label {cartonId} has no {key}; refusing to guess one.");

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

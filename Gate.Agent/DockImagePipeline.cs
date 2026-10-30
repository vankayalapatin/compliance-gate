// ASN-time dock image pipeline (supplier simulation): when an ASN is created
// for a PO, build the labeled dock views for that PO, read each once with
// Spark, and cache everything by hash. Labels carry the PO number the ERP
// actually minted, never an assumed one.
//
// Per view: try the image labeler once (AI applies labels inside masked
// regions), verify the Spark read against the label manifest field by field,
// and fall back to programmatic compositing on any mismatch. Image outputs
// cache by input hash and reads cache by image hash, so replays and resets
// make zero new calls. The result records which path each image took.
using System.Text;
using System.Text.Json;
using Gate.Contracts;
using Microsoft.Extensions.Logging;

namespace Gate.Agent;

public enum DockImagePath
{
    AiLabeled,
    Composited
}

public sealed record DockImageOptions(
    string BasesDir,
    string ImageSpecsPath,
    string CartonsPath,
    string OutputDir,
    string ImageCacheDir,
    string ReadCacheDir,
    string ReadPromptVersion = VisionReadCache.CartonReadV1);

public sealed record DockImageResult(
    string ImageId,
    string FilePath,
    string ImageSha256,
    DockImagePath Path,
    int LabelAttempts,
    bool ReadFromCache,
    string ReadText,
    string? FallbackReason = null);

public sealed record DockImageSet(string PoNumber, IReadOnlyList<DockImageResult> Images);

public sealed record ViewLabelSpec(
    string CartonId, string Part, int Units, string Lot, bool MarkPresent,
    double X, double Y, double W, double H, bool Relabeled);

public sealed record ViewSpec(
    string Id, string BaseFile, IReadOnlyList<ViewLabelSpec> Labels,
    CropSpec? Crop, IReadOnlySet<string> DamageVisible);

public sealed record CropSpec(double X, double Y, double W, double H, int Scale);

public sealed class DockImagePipeline(
    IImageLabeler labeler,
    IModelClient models,
    Microsoft.Extensions.Logging.ILogger<DockImagePipeline>? logger = null)
{
    private readonly Microsoft.Extensions.Logging.ILogger<DockImagePipeline> _logger =
        logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DockImagePipeline>.Instance;

    public const string ReadPrompt =
        "Transcribe every shipping label visible in this dock photo. " +
        "Return a JSON array, one object per carton label you can read, with exactly these keys: " +
        "carton_id, part, po_number, lot_code, units_per_carton (integer), " +
        "mark_present (boolean: a red-bordered lithium-battery handling mark is printed on the label), " +
        "mark_legible (boolean), un_number (string or null), class9_visible (boolean), " +
        "damage_visible (boolean: the carton itself looks crushed, punctured, torn, or water-damaged in the photo), " +
        "damage_note (string or null), read_confidence (0 to 1). " +
        "Transcribe only what you see; never guess missing text, use null. " +
        "If no label is legible, return []. Return ONLY the JSON array, no other text.";

    public async Task<DockImageSet> BuildAsync(
        AsnDto asn, string poNumber, DockImageOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(poNumber))
        {
            throw new InvalidOperationException("Dock images need the PO number the ERP minted; it cannot be blank.");
        }

        var specs = LoadSpecs(options.ImageSpecsPath);
        var truth = LoadCartonTruth(options.CartonsPath);
        var asnCartons = asn.Cartons.ToDictionary(c => c.CartonId, c => c, StringComparer.Ordinal);
        var readCache = new VisionReadCache(options.ReadCacheDir);
        Directory.CreateDirectory(options.OutputDir);
        Directory.CreateDirectory(options.ImageCacheDir);
        var results = new List<DockImageResult>();
        foreach (var view in specs)
        {
            results.Add(await BuildViewAsync(
                asn, asnCartons, truth, view, poNumber, options, readCache, ct).ConfigureAwait(false));
        }

        return new DockImageSet(poNumber, results);
    }

    private async Task<DockImageResult> BuildViewAsync(
        AsnDto asn,
        IReadOnlyDictionary<string, AsnCarton> asnCartons,
        IReadOnlyDictionary<string, CartonTruth> truth,
        ViewSpec view,
        string poNumber,
        DockImageOptions options,
        VisionReadCache readCache,
        CancellationToken ct)
    {
        var baseBytes = await File.ReadAllBytesAsync(
            Path.Combine(options.BasesDir, view.BaseFile), ct).ConfigureAwait(false);
        var workBase = ApplyCrop(baseBytes, view.Crop);
        var labels = ResolveLabels(view, asn, asnCartons, truth, poNumber);
        var cacheKey = VisionReadCache.Sha256Hex(
            Encoding.UTF8.GetBytes(CanonicalKey(view, workBase, labels, poNumber)));
        var cachedFile = Path.Combine(options.ImageCacheDir, cacheKey + ".jpg");
        var outFile = Path.Combine(options.OutputDir, view.Id + ".jpg");

        byte[] finalBytes;
        DockImagePath path;
        string? fallbackReason = null;
        var attempts = 0;
        if (File.Exists(cachedFile))
        {
            finalBytes = await File.ReadAllBytesAsync(cachedFile, ct).ConfigureAwait(false);
            path = ReadPathMarker(cachedFile);
            fallbackReason = ReadReasonMarker(cachedFile);
        }
        else
        {
            attempts = 1;
            var ai = await TryAiLabelsAsync(workBase, labels, view, poNumber, ct).ConfigureAwait(false);
            if (ai.Accepted)
            {
                // The verification read becomes the cached read: no second call.
                finalBytes = ai.Bytes!;
                path = DockImagePath.AiLabeled;
                var aiSha = VisionReadCache.Sha256Hex(finalBytes);
                readCache.Write(aiSha, options.ReadPromptVersion, models.ModelName, ai.ReadText!);
                await File.WriteAllBytesAsync(cachedFile, finalBytes, ct).ConfigureAwait(false);
                WritePathMarker(cachedFile, path);
                await File.WriteAllBytesAsync(outFile, finalBytes, ct).ConfigureAwait(false);
                return new DockImageResult(view.Id, outFile, aiSha, path, attempts, false, ai.ReadText!, null);
            }

            _logger.LogWarning(
                "View {ImageId} fell back to compositing after {Attempts} AI attempt(s): {Reason}",
                view.Id, attempts, ai.FallbackReason);
            finalBytes = DockLabelCompositor.Composite(workBase, labels, view);
            path = DockImagePath.Composited;
            fallbackReason = ai.FallbackReason;
            await File.WriteAllBytesAsync(cachedFile, finalBytes, ct).ConfigureAwait(false);
            WritePathMarker(cachedFile, path);
            WriteReasonMarker(cachedFile, ai.FallbackReason);
        }

        await File.WriteAllBytesAsync(outFile, finalBytes, ct).ConfigureAwait(false);
        var sha = VisionReadCache.Sha256Hex(finalBytes);
        var cached = readCache.TryRead(sha, options.ReadPromptVersion);
        bool readFromCache;
        string readText;
        if (cached is not null)
        {
            readFromCache = true;
            readText = cached.Text;
        }
        else
        {
            readText = await ReadWithOneRetryAsync(view.Id, finalBytes, ct).ConfigureAwait(false);
            readCache.Write(sha, options.ReadPromptVersion, models.ModelName, readText);
            readFromCache = false;
        }

        return new DockImageResult(view.Id, outFile, sha, path, attempts, readFromCache, readText, fallbackReason);
    }

    private async Task<string> ReadWithOneRetryAsync(string viewId, byte[] finalBytes, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            ModelResult result;
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromMinutes(5));
                result = await models.CompleteAsync(
                    new ModelRequest(ReadPrompt, MaxTokens: 2000, ImageJpeg: finalBytes), attemptCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                lastError = ex;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                try
                {
                    ParseReads(result.Text);
                    return result.Text;
                }
                catch (JsonException ex)
                {
                    lastError = ex;
                }
            }
        }

        throw new InvalidOperationException(
            $"Spark read for {viewId} failed after one retry; refusing to cache it.", lastError);
    }

    private sealed record AiOutcome(bool Accepted, byte[]? Bytes, string? ReadText, string? FallbackReason);

    private async Task<AiOutcome> TryAiLabelsAsync(
        byte[] workBase, IReadOnlyList<ResolvedLabel> labels, ViewSpec view, string poNumber, CancellationToken ct)
    {
        byte[] mask;
        try
        {
            mask = DockLabelCompositor.BuildMask(workBase, labels);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"View {view.Id}: mask build failed: {ex.Message}", ex);
        }

        byte[] labeled;
        try
        {
            using var labelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            labelCts.CancelAfter(TimeSpan.FromMinutes(5));
            labeled = await labeler.ApplyLabelsAsync(
                workBase, mask, EditPrompt(view, labels, poNumber), labelCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new AiOutcome(false, null, null, $"labeler {labeler.LabelerName} failed: {Trim(ex.Message)}");
        }

        if (!DockLabelCompositor.Decodes(labeled))
        {
            return new AiOutcome(false, null, null, $"labeler {labeler.LabelerName} returned undecodable bytes");
        }

        string readText;
        try
        {
            using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            verifyCts.CancelAfter(TimeSpan.FromMinutes(5));
            var result = await models.CompleteAsync(
                new ModelRequest(ReadPrompt, MaxTokens: 2000, ImageJpeg: labeled), verifyCts.Token).ConfigureAwait(false);
            readText = result.Text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new AiOutcome(false, null, null, $"verify read failed: {Trim(ex.Message)}");
        }

        if (string.IsNullOrWhiteSpace(readText))
        {
            return new AiOutcome(false, null, null, "verify read came back empty");
        }

        if (!VerifyAgainstManifest(readText, labels, view))
        {
            return new AiOutcome(false, null, null, "verify read mismatched the label manifest on at least one field");
        }

        return new AiOutcome(true, labeled, readText, null);
    }

    private static string Trim(string message, int max = 200) =>
        message.Length <= max ? message : message[..max] + "...";

    public static bool VerifyAgainstManifest(
        string readText, IReadOnlyList<ResolvedLabel> labels, ViewSpec view)
    {
        List<JsonElement> reads;
        try
        {
            reads = ParseReads(readText);
        }
        catch (JsonException)
        {
            return false;
        }

        if (reads.Count != labels.Count)
        {
            return false;
        }

        foreach (var label in labels)
        {
            var read = reads.FirstOrDefault(element =>
                string.Equals(element.GetProperty("carton_id").GetString(), label.CartonId, StringComparison.Ordinal));
            if (read.ValueKind == JsonValueKind.Undefined)
            {
                return false;
            }

            if (!StringEquals(read, "part", label.Part)
                || !StringEquals(read, "po_number", label.PoNumber)
                || !StringEquals(read, "lot_code", label.Lot)
                || !IntEquals(read, "units_per_carton", label.Units)
                || !BoolEquals(read, "mark_present", label.MarkPresent)
                || !BoolEquals(read, "mark_legible", label.MarkPresent)
                || !StringEquals(read, "un_number", "UN3480")
                || !BoolEquals(read, "class9_visible", true)
                || !BoolEquals(read, "damage_visible", view.DamageVisible.Contains(label.CartonId)))
            {
                return false;
            }

            var noteNull = !read.TryGetProperty("damage_note", out var note) || note.ValueKind == JsonValueKind.Null;
            if (view.DamageVisible.Contains(label.CartonId) == noteNull && view.DamageVisible.Contains(label.CartonId))
            {
                return false;
            }
        }

        return true;
    }

    public static List<JsonElement> ParseReads(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = trimmed.Split('\n').ToList();
            lines.RemoveAt(0);
            if (lines.Count > 0 && lines[^1].Trim().StartsWith("```", StringComparison.Ordinal))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            trimmed = string.Join('\n', lines);
        }

        using var doc = JsonDocument.Parse(trimmed);
        return doc.RootElement.EnumerateArray().Select(element => element.Clone()).ToList();
    }

    private static bool StringEquals(JsonElement read, string key, string? expected) =>
        read.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        && string.Equals(value.GetString(), expected, StringComparison.Ordinal);

    private static bool IntEquals(JsonElement read, string key, int expected) =>
        read.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var actual) && actual == expected;

    private static bool BoolEquals(JsonElement read, string key, bool expected) =>
        read.TryGetProperty(key, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && (value.ValueKind == JsonValueKind.True) == expected;

    private static string EditPrompt(ViewSpec view, IReadOnlyList<ResolvedLabel> labels, string poNumber)
    {
        var prompt = new StringBuilder();
        prompt.Append("Add one clean white rectangular shipping label onto the front face of each carton listed below, ");
        prompt.Append("at the given position (fractions of image width/height, origin top-left). ");
        prompt.Append("Leave everything else in the photo exactly unchanged. ");
        prompt.Append("Each label shows black text lines on white: carton id (large), PART, PO, LOT, QTY, UN number. ");
        prompt.Append("Cartons marked WITH-HANDLING-MARK also carry a red-hatched square lithium-battery mark with UN3480; ");
        prompt.Append("cartons marked NO-MARK carry no red mark. Every label carries a small black-yellow Class 9 diamond with 9. ");
        prompt.Append($"Shipment PO for every label: {poNumber}. Labels: ");
        foreach (var label in labels)
        {
            var zone = ZoneWord(label.X + label.W / 2, label.Y + label.H / 2);
            prompt.Append($"[{label.CartonId} at {zone} (x={label.X:0.00},y={label.Y:0.00}): ");
            prompt.Append($"{label.CartonId} / PART {label.Part} / PO {label.PoNumber} / LOT {label.Lot} / QTY {label.Units} / UN3480 / {(label.MarkPresent ? "WITH-HANDLING-MARK" : "NO-MARK")}] ");
        }

        return prompt.ToString().TrimEnd();
    }

    private static string ZoneWord(double cx, double cy)
    {
        var vertical = cy < 0.33 ? "upper" : cy < 0.67 ? "middle" : "lower";
        var horizontal = cx < 0.33 ? "left" : cx < 0.67 ? "center" : "right";
        return $"{vertical}-{horizontal}";
    }

    private static byte[] ApplyCrop(byte[] baseJpeg, CropSpec? crop)
    {
        if (crop is null)
        {
            return baseJpeg;
        }

        return DockLabelCompositor.Crop(baseJpeg, crop);
    }

    private static string CanonicalKey(
        ViewSpec view, byte[] workBase, IReadOnlyList<ResolvedLabel> labels, string poNumber)
    {
        var key = new StringBuilder();
        key.Append(view.Id).Append('|').Append(VisionReadCache.Sha256Hex(workBase)).Append('|').Append(poNumber);
        foreach (var label in labels)
        {
            key.Append('|').Append(label.CartonId).Append(',').Append(label.Part).Append(',').Append(label.Units)
                .Append(',').Append(label.Lot).Append(',').Append(label.MarkPresent ? 'M' : 'm')
                .Append(',').Append(label.X.ToString("F4")).Append(',').Append(label.Y.ToString("F4"))
                .Append(',').Append(label.W.ToString("F4")).Append(',').Append(label.H.ToString("F4"));
        }

        return key.ToString();
    }

    private static DockImagePath ReadPathMarker(string cachedFile)
    {
        var marker = cachedFile + ".path";
        if (!File.Exists(marker))
        {
            return DockImagePath.Composited;
        }

        return File.ReadAllText(marker).Trim() == nameof(DockImagePath.AiLabeled)
            ? DockImagePath.AiLabeled
            : DockImagePath.Composited;
    }

    private static void WritePathMarker(string cachedFile, DockImagePath path) =>
        File.WriteAllText(cachedFile + ".path", path.ToString());

    private static void WriteReasonMarker(string cachedFile, string? reason)
    {
        var marker = cachedFile + ".reason";
        if (string.IsNullOrWhiteSpace(reason))
        {
            if (File.Exists(marker))
            {
                File.Delete(marker);
            }

            return;
        }

        File.WriteAllText(marker, reason);
    }

    private static string? ReadReasonMarker(string cachedFile)
    {
        var marker = cachedFile + ".reason";
        return File.Exists(marker) ? File.ReadAllText(marker) : null;
    }

    private static IReadOnlyList<ViewSpec> LoadSpecs(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var views = new List<ViewSpec>();
        foreach (var spec in doc.RootElement.GetProperty("images").EnumerateArray())
        {
            CropSpec? crop = null;
            if (spec.TryGetProperty("crop", out var cropJson))
            {
                crop = new CropSpec(
                    cropJson.GetProperty("x").GetDouble(), cropJson.GetProperty("y").GetDouble(),
                    cropJson.GetProperty("w").GetDouble(), cropJson.GetProperty("h").GetDouble(),
                    cropJson.TryGetProperty("scale", out var scale) ? scale.GetInt32() : 1);
            }

            var labels = new List<ViewLabelSpec>();
            foreach (var label in spec.GetProperty("labels").EnumerateArray())
            {
                labels.Add(new ViewLabelSpec(
                    label.GetProperty("carton").GetString()!,
                    Part: string.Empty, Units: 0, Lot: string.Empty, MarkPresent: false,
                    label.GetProperty("x").GetDouble(), label.GetProperty("y").GetDouble(),
                    label.GetProperty("w").GetDouble(), label.GetProperty("h").GetDouble(),
                    label.TryGetProperty("relabeled", out var relabeled) && relabeled.GetBoolean()));
            }

            var damage = new HashSet<string>(StringComparer.Ordinal);
            if (spec.TryGetProperty("damageVisible", out var damageJson))
            {
                foreach (var element in damageJson.EnumerateArray())
                {
                    damage.Add(element.GetString()!);
                }
            }

            views.Add(new ViewSpec(
                spec.GetProperty("id").GetString()!,
                spec.GetProperty("source").GetString()!,
                labels, crop, damage));
        }

        return views;
    }

    private static IReadOnlyDictionary<string, CartonTruth> LoadCartonTruth(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("cartons").EnumerateArray()
            .ToDictionary(
                row => row.GetProperty("id").GetString()!,
                row => new CartonTruth(
                    row.GetProperty("part").GetString()!,
                    row.GetProperty("units").GetInt32(),
                    row.GetProperty("markPresent").GetBoolean()),
                StringComparer.Ordinal);
    }

    private static IReadOnlyList<ResolvedLabel> ResolveLabels(
        ViewSpec view,
        AsnDto asn,
        IReadOnlyDictionary<string, AsnCarton> asnCartons,
        IReadOnlyDictionary<string, CartonTruth> truth,
        string poNumber)
    {
        if (!string.Equals(asn.PoNumber, poNumber, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ASN {asn.AsnNumber} is for PO {asn.PoNumber}, not {poNumber}; labels must follow the ASN.");
        }

        var resolved = new List<ResolvedLabel>();
        foreach (var label in view.Labels)
        {
            if (!asnCartons.TryGetValue(label.CartonId, out var asnCarton))
            {
                throw new InvalidOperationException(
                    $"View {view.Id} labels carton {label.CartonId}, which is not on ASN {asn.AsnNumber}.");
            }

            if (!truth.TryGetValue(label.CartonId, out var cartonTruth))
            {
                throw new InvalidOperationException(
                    $"No ground truth for carton {label.CartonId}; refusing to invent label contents.");
            }

            resolved.Add(new ResolvedLabel(
                label.CartonId, asnCarton.ItemId, asnCarton.UnitsPerCarton, asnCarton.LotRef,
                label.Relabeled || cartonTruth.MarkPresent,
                label.X, label.Y, label.W, label.H, poNumber));
        }

        return resolved;
    }

    internal sealed record CartonTruth(string Part, int Units, bool MarkPresent);

    public sealed record ResolvedLabel(
        string CartonId, string Part, int Units, string Lot, bool MarkPresent,
        double X, double Y, double W, double H, string PoNumber);
}

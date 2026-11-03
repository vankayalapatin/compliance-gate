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
    string ReadPromptVersion = VisionReadCache.CartonReadV1,
    string FilePrefix = "",
    string? ViewKind = null);

public sealed record DockImageResult(
    string ImageId,
    string FilePath,
    string ImageSha256,
    DockImagePath Path,
    int LabelAttempts,
    bool ReadFromCache,
    string ReadText,
    string? FallbackReason = null,
    string? ReadWarning = null);

public sealed record DockImageSet(string PoNumber, IReadOnlyList<DockImageResult> Images);

public sealed record ViewLabelSpec(
    string CartonId, string Part, int Units, string Lot, bool MarkPresent,
    double X, double Y, double W, double H, bool Relabeled);

public sealed record ViewSpec(
    string Id, string BaseFile, IReadOnlyList<ViewLabelSpec> Labels,
    CropSpec? Crop, IReadOnlySet<string> DamageVisible, string Kind = "pallet");

public sealed record CropSpec(double X, double Y, double W, double H, int Scale);

public sealed class DockImagePipeline(
    IImageLabeler labeler,
    IModelClient models,
    Microsoft.Extensions.Logging.ILogger<DockImagePipeline>? logger = null,
    ISceneGenerator? sceneGenerator = null)
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

        var specs = LoadSpecs(options.ImageSpecsPath)
            .Where(v => options.ViewKind is null
                || string.Equals(v.Kind, options.ViewKind, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (specs.Count == 0)
        {
            throw new InvalidOperationException(
                $"No dock views of kind '{options.ViewKind}' in {options.ImageSpecsPath}.");
        }

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
        var outFile = Path.Combine(options.OutputDir, options.FilePrefix + view.Id + ".jpg");

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
            if (path == DockImagePath.Composited
                && !TryVerifyReads(readText, labels, view.DamageVisible, out var mismatch))
            {
                _logger.LogWarning(
                    "View {ImageId} composited read mismatched the manifest ({Detail}); rebuilding once.",
                    view.Id, mismatch);
                finalBytes = DockLabelCompositor.Composite(workBase, labels, view);
                await File.WriteAllBytesAsync(cachedFile, finalBytes, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(outFile, finalBytes, ct).ConfigureAwait(false);
                sha = VisionReadCache.Sha256Hex(finalBytes);
                readText = await ReadWithOneRetryAsync(view.Id, finalBytes, ct).ConfigureAwait(false);
                if (!TryVerifyReads(readText, labels, view.DamageVisible, out mismatch))
                {
                    throw new InvalidOperationException(
                        $"View {view.Id}: composited read mismatched the label manifest after one retry: {mismatch}; refusing to cache it.");
                }
            }

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
                    new ModelRequest(ReadPrompt, MaxTokens: 4000, ImageJpeg: finalBytes), attemptCts.Token).ConfigureAwait(false);
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
                new ModelRequest(ReadPrompt, MaxTokens: 4000, ImageJpeg: labeled), verifyCts.Token).ConfigureAwait(false);
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
        string readText, IReadOnlyList<ResolvedLabel> labels, ViewSpec view) =>
        TryVerifyReads(readText, labels, view.DamageVisible, out _);

    /// <summary>Field-by-field check of a Spark read against the label
    /// manifest: read count plus, per carton, carton id, part, PO, lot,
    /// units, mark, UN number, Class 9, and damage. True when every field
    /// matches; otherwise false with the carton and field named, so
    /// callers can record the mismatch and fall back.</summary>
    public static bool TryVerifyReads(
        string readText,
        IReadOnlyList<ResolvedLabel> labels,
        IReadOnlySet<string> damageVisible,
        out string mismatchDetail)
    {
        List<JsonElement> reads;
        try
        {
            reads = ParseReads(readText);
        }
        catch (JsonException ex)
        {
            mismatchDetail = $"unparseable read array: {Trim(ex.Message)}";
            return false;
        }

        if (reads.Count != labels.Count)
        {
            mismatchDetail = $"expected {labels.Count} label read(s), found {reads.Count}";
            return false;
        }

        foreach (var label in labels)
        {
            var read = reads.FirstOrDefault(element =>
                element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("carton_id", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), label.CartonId, StringComparison.Ordinal));
            if (read.ValueKind == JsonValueKind.Undefined)
            {
                mismatchDetail = $"no read for carton {label.CartonId}";
                return false;
            }

            var damaged = damageVisible.Contains(label.CartonId);
            if (!ExpectString(read, label.CartonId, "part", label.Part, out mismatchDetail)
                || !ExpectString(read, label.CartonId, "po_number", label.PoNumber, out mismatchDetail)
                || !ExpectString(read, label.CartonId, "lot_code", label.Lot, out mismatchDetail)
                || !ExpectInt(read, label.CartonId, "units_per_carton", label.Units, out mismatchDetail)
                || !ExpectBool(read, label.CartonId, "mark_present", label.MarkPresent, out mismatchDetail)
                || !ExpectBool(read, label.CartonId, "mark_legible", label.MarkPresent, out mismatchDetail)
                || !ExpectString(read, label.CartonId, "un_number", "UN3480", out mismatchDetail)
                || !ExpectBool(read, label.CartonId, "class9_visible", true, out mismatchDetail))
            {
                return false;
            }

            // Expected damage verifies through the flag and the note: a
            // damaged carton must read damage_visible with a non-blank
            // note, so the damage sits on the right carton, and cartons
            // expected clean must read no damage.
            if (!ExpectBool(read, label.CartonId, "damage_visible", damaged, out mismatchDetail))
            {
                return false;
            }

            var noteNull = !read.TryGetProperty("damage_note", out var note)
                || note.ValueKind is not JsonValueKind.String
                || string.IsNullOrWhiteSpace(note.GetString());
            if (damaged && noteNull)
            {
                mismatchDetail = $"{label.CartonId}: damage_note is blank, the manifest requires a damage note";
                return false;
            }
        }

        mismatchDetail = string.Empty;
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

    private static bool ExpectString(
        JsonElement read, string cartonId, string key, string? expected, out string detail)
    {
        var actual = read.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
        if (string.Equals(actual, expected, StringComparison.Ordinal))
        {
            detail = string.Empty;
            return true;
        }

        detail = $"{cartonId}: {key} reads '{actual ?? "<missing>"}', the manifest has '{expected}'";
        return false;
    }

    private static bool ExpectInt(
        JsonElement read, string cartonId, string key, int expected, out string detail)
    {
        if (read.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var actual)
            && actual == expected)
        {
            detail = string.Empty;
            return true;
        }

        detail = $"{cartonId}: {key} does not read {expected}";
        return false;
    }

    private static bool ExpectBool(
        JsonElement read, string cartonId, string key, bool expected, out string detail)
    {
        if (read.TryGetProperty(key, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && (value.ValueKind == JsonValueKind.True) == expected)
        {
            detail = string.Empty;
            return true;
        }

        detail = $"{cartonId}: {key} reads {!expected}, the manifest has {expected}";
        return false;
    }

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

    public const string NoLiveScenesMessage = "live scenes unavailable: image generation is not configured";

    public const string LiveFacePromptTemplate =
        "Locate every cardboard carton front face in this dock photo. " +
        "Return a JSON array with exactly {0} objects, one per face, each with exactly these keys: " +
        "x, y, w, h (fractions of image width and height, origin top-left). " +
        "Cover every face once; never guess. Return ONLY the JSON array, no other text.";

    private static readonly IReadOnlySet<SimulatedProblemKind> NoProblems =
        new HashSet<SimulatedProblemKind>();

    /// <summary>Library build: dock photos for any ASN carton count from
    /// the scene catalog at <paramref name="options"/> specs path. Labels
    /// come straight from the ASN (every carton reads mark-present unless
    /// a problem says otherwise); problems add missing marks, damage cues,
    /// and hidden faces. Composite-only: each photo is read back and
    /// checked field by field, rebuilt plus retried once on mismatch, and
    /// a second mismatch throws naming the carton and field.</summary>
    public async Task<DockImageSet> BuildLibraryAsync(
        AsnDto asn, string poNumber, IReadOnlyList<SimulatedProblem> problems,
        DockImageOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asn);
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(poNumber))
        {
            throw new InvalidOperationException("Dock images need the PO number the ERP minted; it cannot be blank.");
        }

        if (!string.Equals(asn.PoNumber, poNumber, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ASN {asn.AsnNumber} is for PO {asn.PoNumber}, not {poNumber}; labels must follow the ASN.");
        }

        if (asn.Cartons.Count == 0)
        {
            throw new InvalidOperationException(
                $"ASN {asn.AsnNumber} carries no cartons; there is nothing to photograph.");
        }

        var scenes = LoadScenes(options.ImageSpecsPath);
        var problemMap = ProblemMap(asn, problems);
        var chunks = AssignScenes(scenes, asn.Cartons);
        var readCache = new VisionReadCache(options.ReadCacheDir);
        Directory.CreateDirectory(options.OutputDir);
        Directory.CreateDirectory(options.ImageCacheDir);
        var results = new List<DockImageResult>();
        var chunkNo = 0;
        foreach (var chunk in chunks)
        {
            chunkNo++;
            var imageId = chunks.Count == 1 ? chunk.Scene.Id : $"{chunk.Scene.Id}-P{chunkNo}";
            results.Add(await BuildLibraryViewAsync(
                chunk, imageId, problemMap, poNumber, options, readCache, ct).ConfigureAwait(false));
        }

        return new DockImageSet(poNumber, results);
    }

    /// <summary>Close-up for any carton on the ASN: finds the carton's
    /// library face, cuts the surrounding crop at the scene's close-up
    /// scale, and draws one large label (the IMG-04 relabel pattern).
    /// Hidden cartons are refused: they appear in no view.</summary>
    public async Task<DockImageResult> BuildCloseupAsync(
        AsnDto asn, string poNumber, string cartonId,
        IReadOnlyList<SimulatedProblem> problems, DockImageOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asn);
        ArgumentNullException.ThrowIfNull(problems);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(poNumber))
        {
            throw new InvalidOperationException("Dock images need the PO number the ERP minted; it cannot be blank.");
        }

        if (!string.Equals(asn.PoNumber, poNumber, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ASN {asn.AsnNumber} is for PO {asn.PoNumber}, not {poNumber}; labels must follow the ASN.");
        }

        var carton = asn.Cartons.FirstOrDefault(c =>
            string.Equals(c.CartonId, cartonId, StringComparison.Ordinal));
        if (carton is null)
        {
            throw new InvalidOperationException(
                $"Carton {cartonId} is not on ASN {asn.AsnNumber}; no close-up shows it.");
        }

        var problemMap = ProblemMap(asn, problems);
        var kinds = problemMap.TryGetValue(cartonId, out var k) ? k : NoProblems;
        if (kinds.Contains(SimulatedProblemKind.Hidden))
        {
            throw new InvalidOperationException(
                $"Carton {cartonId} is hidden on ASN {asn.AsnNumber}; no close-up shows it.");
        }

        var scenes = LoadScenes(options.ImageSpecsPath);
        var chunks = AssignScenes(scenes, asn.Cartons);
        DockScene? scene = null;
        DockSceneFace? face = null;
        foreach (var chunk in chunks)
        {
            for (var i = 0; i < chunk.Cartons.Count; i++)
            {
                if (string.Equals(chunk.Cartons[i].CartonId, cartonId, StringComparison.Ordinal))
                {
                    scene = chunk.Scene;
                    face = chunk.Scene.Faces[i];
                    break;
                }
            }

            if (scene is not null)
            {
                break;
            }
        }

        if (scene is null || face is null)
        {
            throw new InvalidOperationException(
                $"Carton {cartonId} found no library face on ASN {asn.AsnNumber}.");
        }

        var baseBytes = await File.ReadAllBytesAsync(
            Path.Combine(options.BasesDir, scene.Base), ct).ConfigureAwait(false);
        var cropW = Math.Min(1.0, face.W * 2.6);
        var cropH = Math.Min(1.0, face.H * 2.6);
        var cropX = Math.Clamp(face.X + face.W / 2 - cropW / 2, 0, 1 - cropW);
        var cropY = Math.Clamp(face.Y + face.H / 2 - cropH / 2, 0, 1 - cropH);
        var workBase = DockLabelCompositor.Crop(
            baseBytes, new CropSpec(cropX, cropY, cropW, cropH, scene.CloseupScale));
        var label = new ResolvedLabel(
            carton.CartonId, carton.ItemId, carton.UnitsPerCarton, carton.LotRef,
            !kinds.Contains(SimulatedProblemKind.MissingMark),
            0.25, 0.30, 0.50, 0.40, poNumber);
        var labels = new List<ResolvedLabel> { label };
        var damage = kinds.Contains(SimulatedProblemKind.Crushed)
            ? new List<DamageCue> { new(carton.CartonId, 0.25, 0.30, 0.50, 0.40) }
            : new List<DamageCue>();
        var damageVisible = kinds.Contains(SimulatedProblemKind.Crushed)
            ? new HashSet<string>([carton.CartonId], StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var imageId = $"{scene.Id}-CU-{SanitizeImageId(cartonId)}";
        Directory.CreateDirectory(options.OutputDir);
        Directory.CreateDirectory(options.ImageCacheDir);
        var readCache = new VisionReadCache(options.ReadCacheDir);
        var cacheKey = VisionReadCache.Sha256Hex(
            Encoding.UTF8.GetBytes(CanonicalLibraryKey(
                imageId, workBase, labels, damageVisible, [], poNumber)));
        var cachedFile = Path.Combine(options.ImageCacheDir, cacheKey + ".jpg");
        var outFile = Path.Combine(options.OutputDir, options.FilePrefix + imageId + ".jpg");
        byte[] finalBytes;
        if (File.Exists(cachedFile))
        {
            finalBytes = await File.ReadAllBytesAsync(cachedFile, ct).ConfigureAwait(false);
        }
        else
        {
            finalBytes = DockLabelCompositor.CompositeLibrary(workBase, labels, damage, [], imageId);
            await File.WriteAllBytesAsync(cachedFile, finalBytes, ct).ConfigureAwait(false);
            WritePathMarker(cachedFile, DockImagePath.Composited);
            WriteReasonMarker(cachedFile, null);
        }

        await File.WriteAllBytesAsync(outFile, finalBytes, ct).ConfigureAwait(false);
        var (bytes, sha, readText, readFromCache, warning) = await ReadVerifyLibraryAsync(
            imageId, "Close-up", () => DockLabelCompositor.CompositeLibrary(
                workBase, labels, damage, [], imageId),
            finalBytes, cachedFile, outFile, labels, damageVisible,
            readCache, options, ct).ConfigureAwait(false);
        _ = bytes;
        return new DockImageResult(
            imageId, outFile, sha, DockImagePath.Composited, 0, readFromCache, readText, null, warning);
    }

    /// <summary>Live path: generates one text-free pallet photo for the
    /// ASN count, locates the carton faces, draws the ASN labels, and
    /// reads them back against the manifest. Success returns the photo
    /// set; ANY failure returns a null set with the reason, so the caller
    /// can fall back to a library scene and record why.</summary>
    public async Task<(DockImageSet? Set, string? FailureReason)> TryBuildLiveSceneAsync(
        AsnDto asn, string poNumber, IReadOnlyList<SimulatedProblem> problems,
        DockImageOptions options, CancellationToken ct = default)
    {
        if (sceneGenerator is null)
        {
            return (null, NoLiveScenesMessage);
        }

        if (asn is null)
        {
            return (null, "Live scene needs an ASN; got none.");
        }

        if (string.IsNullOrWhiteSpace(poNumber))
        {
            return (null, "Live scene needs the PO number the ERP minted; it cannot be blank.");
        }

        if (!string.Equals(asn.PoNumber, poNumber, StringComparison.Ordinal))
        {
            return (null, $"ASN {asn.AsnNumber} is for PO {asn.PoNumber}, not {poNumber}; labels must follow the ASN.");
        }

        if (asn.Cartons.Count == 0)
        {
            return (null, $"ASN {asn.AsnNumber} carries no cartons; there is nothing to photograph.");
        }

        IReadOnlyDictionary<string, IReadOnlySet<SimulatedProblemKind>> problemMap;
        try
        {
            problemMap = ProblemMap(asn, problems ?? Array.Empty<SimulatedProblem>());
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }

        byte[] sceneBytes;
        try
        {
            // Crushed cartons arrive damaged in the generated scene; the
            // read-back below checks the damage sits on the right cartons.
            var crushedCount = problems?.Count(p => p.Kind == SimulatedProblemKind.Crushed) ?? 0;
            using var genCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            genCts.CancelAfter(TimeSpan.FromMinutes(5));
            sceneBytes = await sceneGenerator.GenerateSceneAsync(
                asn.Cartons.Count, crushedCount, genCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(
                ex, "Live scene generation for ASN {Asn} failed; caller falls back to a library scene.",
                asn.AsnNumber);
            return (null, $"live scene generation failed: {Trim(ex.Message)}");
        }

        if (!DockLabelCompositor.Decodes(sceneBytes))
        {
            return (null, "live scene generation returned undecodable bytes");
        }

        string faceText;
        try
        {
            using var faceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            faceCts.CancelAfter(TimeSpan.FromMinutes(5));
            var located = await models.CompleteAsync(
                new ModelRequest(
                    string.Format(LiveFacePromptTemplate, asn.Cartons.Count),
                    MaxTokens: 4000, ImageJpeg: sceneBytes),
                faceCts.Token).ConfigureAwait(false);
            faceText = located.Text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(
                ex, "Live scene face locate for ASN {Asn} failed; caller falls back to a library scene.",
                asn.AsnNumber);
            return (null, $"live scene face locate failed: {Trim(ex.Message)}");
        }

        IReadOnlyList<FaceBox> found;
        try
        {
            found = ParseFaces(faceText);
        }
        catch (JsonException ex)
        {
            return (null, $"live scene face locate did not parse: {Trim(ex.Message)}");
        }

        if (found.Count != asn.Cartons.Count)
        {
            return (null, $"live scene shows {found.Count} carton face(s) for {asn.Cartons.Count} carton(s); refusing a partial scene");
        }

        var ordered = found.OrderBy(f => f.Y).ThenBy(f => f.X).ToList();
        var labels = new List<ResolvedLabel>();
        var damage = new List<DamageCue>();
        var occlude = new List<FaceRect>();
        var damageVisible = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < asn.Cartons.Count; i++)
        {
            var carton = asn.Cartons[i];
            var face = ordered[i];
            var kinds = problemMap.TryGetValue(carton.CartonId, out var k) ? k : NoProblems;
            if (kinds.Contains(SimulatedProblemKind.Hidden))
            {
                occlude.Add(new FaceRect(face.X, face.Y, face.W, face.H));
                continue;
            }

            labels.Add(new ResolvedLabel(
                carton.CartonId, carton.ItemId, carton.UnitsPerCarton, carton.LotRef,
                !kinds.Contains(SimulatedProblemKind.MissingMark),
                face.X, face.Y, face.W, face.H, poNumber));
            if (kinds.Contains(SimulatedProblemKind.Crushed))
            {
                damage.Add(new DamageCue(carton.CartonId, face.X, face.Y, face.W, face.H));
                damageVisible.Add(carton.CartonId);
            }
        }

        const string imageId = "LIVE-01";
        Directory.CreateDirectory(options.OutputDir);
        Directory.CreateDirectory(options.ImageCacheDir);
        var readCache = new VisionReadCache(options.ReadCacheDir);
        var outFile = Path.Combine(options.OutputDir, options.FilePrefix + imageId + ".jpg");
        byte[] finalBytes;
        try
        {
            finalBytes = DockLabelCompositor.CompositeLibrary(sceneBytes, labels, damage, occlude, imageId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (null, $"live scene compositing failed: {Trim(ex.Message)}");
        }

        await File.WriteAllBytesAsync(outFile, finalBytes, ct).ConfigureAwait(false);
        var sha = VisionReadCache.Sha256Hex(finalBytes);

        string readText;
        try
        {
            readText = await ReadWithOneRetryAsync(imageId, finalBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return (null, $"live scene read-back failed: {Trim(ex.Message)}");
        }

        if (!TryVerifyReads(readText, labels, damageVisible, out var mismatch))
        {
            _logger.LogWarning(
                "Live scene {ImageId} read-back mismatched the manifest ({Detail}); rebuilding once.",
                imageId, mismatch);
            try
            {
                finalBytes = DockLabelCompositor.CompositeLibrary(sceneBytes, labels, damage, occlude, imageId);
                await File.WriteAllBytesAsync(outFile, finalBytes, ct).ConfigureAwait(false);
                sha = VisionReadCache.Sha256Hex(finalBytes);
                readText = await ReadWithOneRetryAsync(imageId, finalBytes, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return (null, $"live scene read-back failed: {Trim(ex.Message)}");
            }

            if (!TryVerifyReads(readText, labels, damageVisible, out mismatch))
            {
                return (null, $"live scene read-back mismatched the manifest: {mismatch}");
            }
        }

        readCache.Write(sha, options.ReadPromptVersion, models.ModelName, readText);
        _logger.LogInformation(
            "Live scene {ImageId} for ASN {Asn} verified {Labels} label(s).",
            imageId, asn.AsnNumber, labels.Count);
        return (new DockImageSet(
            poNumber,
            [new DockImageResult(imageId, outFile, sha, DockImagePath.Composited, 0, false, readText, null)]), null);
    }

    private async Task<DockImageResult> BuildLibraryViewAsync(
        LibraryChunk chunk,
        string imageId,
        IReadOnlyDictionary<string, IReadOnlySet<SimulatedProblemKind>> problemMap,
        string poNumber,
        DockImageOptions options,
        VisionReadCache readCache,
        CancellationToken ct)
    {
        var scene = chunk.Scene;
        var baseBytes = await File.ReadAllBytesAsync(
            Path.Combine(options.BasesDir, scene.Base), ct).ConfigureAwait(false);
        var labels = new List<ResolvedLabel>();
        var damage = new List<DamageCue>();
        var occlude = new List<FaceRect>();
        var damageVisible = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < chunk.Cartons.Count; i++)
        {
            var carton = chunk.Cartons[i];
            var face = scene.Faces[i];
            var kinds = problemMap.TryGetValue(carton.CartonId, out var k) ? k : NoProblems;
            if (kinds.Contains(SimulatedProblemKind.Hidden))
            {
                occlude.Add(new FaceRect(face.X, face.Y, face.W, face.H));
                continue;
            }

            labels.Add(new ResolvedLabel(
                carton.CartonId, carton.ItemId, carton.UnitsPerCarton, carton.LotRef,
                !kinds.Contains(SimulatedProblemKind.MissingMark),
                face.X, face.Y, face.W, face.H, poNumber));
            if (kinds.Contains(SimulatedProblemKind.Crushed))
            {
                damage.Add(new DamageCue(carton.CartonId, face.X, face.Y, face.W, face.H));
                damageVisible.Add(carton.CartonId);
            }
        }

        var cacheKey = VisionReadCache.Sha256Hex(
            Encoding.UTF8.GetBytes(CanonicalLibraryKey(
                imageId, baseBytes, labels, damageVisible, occlude, poNumber)));
        var cachedFile = Path.Combine(options.ImageCacheDir, cacheKey + ".jpg");
        var outFile = Path.Combine(options.OutputDir, options.FilePrefix + imageId + ".jpg");
        byte[] finalBytes;
        if (File.Exists(cachedFile))
        {
            finalBytes = await File.ReadAllBytesAsync(cachedFile, ct).ConfigureAwait(false);
        }
        else
        {
            finalBytes = DockLabelCompositor.CompositeLibrary(baseBytes, labels, damage, occlude, imageId);
            await File.WriteAllBytesAsync(cachedFile, finalBytes, ct).ConfigureAwait(false);
            WritePathMarker(cachedFile, DockImagePath.Composited);
            WriteReasonMarker(cachedFile, null);
        }

        await File.WriteAllBytesAsync(outFile, finalBytes, ct).ConfigureAwait(false);
        var (bytes, sha, readText, readFromCache, warning) = await ReadVerifyLibraryAsync(
            imageId, "Library view",
            () => DockLabelCompositor.CompositeLibrary(baseBytes, labels, damage, occlude, imageId),
            finalBytes, cachedFile, outFile, labels, damageVisible,
            readCache, options, ct).ConfigureAwait(false);
        _ = bytes;
        return new DockImageResult(
            imageId, outFile, sha, DockImagePath.Composited, 0, readFromCache, readText, null, warning);
    }

    /// <summary>Reads a library view back against its code-drawn labels.
    /// The label content is certain, so a persistent read mismatch is a
    /// reader miss, not a bad photo: it comes back as a warning and the
    /// view still builds (the mismatched read is never cached, so the
    /// next run reads live again). Only the live generated-scene path
    /// hard-gates on read-back, because there the pixels themselves are
    /// uncertain.</summary>
    private async Task<(byte[] Bytes, string Sha, string ReadText, bool ReadFromCache, string? Warning)> ReadVerifyLibraryAsync(
        string imageId,
        string viewNoun,
        Func<byte[]> rebuild,
        byte[] finalBytes,
        string cachedFile,
        string outFile,
        IReadOnlyList<ResolvedLabel> labels,
        IReadOnlySet<string> damageVisible,
        VisionReadCache readCache,
        DockImageOptions options,
        CancellationToken ct)
    {
        var sha = VisionReadCache.Sha256Hex(finalBytes);
        var cached = readCache.TryRead(sha, options.ReadPromptVersion);
        if (cached is not null)
        {
            return (finalBytes, sha, cached.Text, true, null);
        }

        var readText = await ReadWithOneRetryAsync(imageId, finalBytes, ct).ConfigureAwait(false);
        if (!TryVerifyReads(readText, labels, damageVisible, out var mismatch))
        {
            _logger.LogWarning(
                "{Noun} {ImageId} composited read mismatched the manifest ({Detail}); rebuilding once.",
                viewNoun, imageId, mismatch);
            finalBytes = rebuild();
            await File.WriteAllBytesAsync(cachedFile, finalBytes, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(outFile, finalBytes, ct).ConfigureAwait(false);
            sha = VisionReadCache.Sha256Hex(finalBytes);
            readText = await ReadWithOneRetryAsync(imageId, finalBytes, ct).ConfigureAwait(false);
            if (!TryVerifyReads(readText, labels, damageVisible, out mismatch))
            {
                var warning =
                    $"read mismatched the code-drawn labels after one retry ({mismatch}); view kept, read not cached";
                _logger.LogWarning("{Noun} {ImageId}: {Warning}.", viewNoun, imageId, warning);
                return (finalBytes, sha, readText, false, warning);
            }
        }

        readCache.Write(sha, options.ReadPromptVersion, models.ModelName, readText);
        return (finalBytes, sha, readText, false, null);
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<SimulatedProblemKind>> ProblemMap(
        AsnDto asn, IReadOnlyList<SimulatedProblem> problems)
    {
        var asnIds = new HashSet<string>(
            asn.Cartons.Select(c => c.CartonId), StringComparer.Ordinal);
        var map = new Dictionary<string, HashSet<SimulatedProblemKind>>(StringComparer.Ordinal);
        foreach (var problem in problems)
        {
            if (!asnIds.Contains(problem.CartonId))
            {
                throw new InvalidOperationException(
                    $"Problem {problem.Kind} names carton {problem.CartonId}, which is not on ASN {asn.AsnNumber}.");
            }

            map.TryAdd(problem.CartonId, new HashSet<SimulatedProblemKind>());
            map[problem.CartonId].Add(problem.Kind);
        }

        foreach (var (cartonId, kinds) in map)
        {
            if (kinds.Contains(SimulatedProblemKind.Hidden) && kinds.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Carton {cartonId} is hidden, so it cannot also carry another problem.");
            }
        }

        return map.ToDictionary(
            kv => kv.Key, kv => (IReadOnlySet<SimulatedProblemKind>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Loads the scene catalog: ids, counts, bases, ordered faces,
    /// close-up scales. Anything malformed is refused with the scene named.</summary>
    public static IReadOnlyList<DockScene> LoadScenes(string scenesPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(scenesPath));
        if (!doc.RootElement.TryGetProperty("scenes", out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Scene catalog {scenesPath} has no 'scenes' array.");
        }

        var scenes = new List<DockScene>();
        foreach (var spec in list.EnumerateArray())
        {
            var id = spec.TryGetProperty("id", out var idJson)
                && idJson.ValueKind == JsonValueKind.String ? idJson.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException(
                    $"Scene catalog {scenesPath} has a scene without an id.");
            }

            if (!spec.TryGetProperty("counts", out var countsJson)
                || countsJson.ValueKind != JsonValueKind.Array
                || countsJson.GetArrayLength() == 0
                || countsJson.EnumerateArray().Any(c =>
                    c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out var n) || n <= 0))
            {
                throw new InvalidOperationException(
                    $"Scene catalog {scenesPath} scene {id} needs a non-empty counts array of positive integers.");
            }

            var counts = countsJson.EnumerateArray().Select(c => c.GetInt32()).ToList();
            var baseFile = spec.TryGetProperty("base", out var baseJson)
                && baseJson.ValueKind == JsonValueKind.String ? baseJson.GetString() : null;
            if (string.IsNullOrWhiteSpace(baseFile))
            {
                throw new InvalidOperationException(
                    $"Scene catalog {scenesPath} scene {id} names no base file.");
            }

            if (!spec.TryGetProperty("faces", out var facesJson)
                || facesJson.ValueKind != JsonValueKind.Array
                || facesJson.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    $"Scene catalog {scenesPath} scene {id} lists no faces.");
            }

            var faces = new List<DockSceneFace>();
            var position = 0;
            foreach (var faceJson in facesJson.EnumerateArray())
            {
                if (!TryGetDouble(faceJson, "x", out var x)
                    || !TryGetDouble(faceJson, "y", out var y)
                    || !TryGetDouble(faceJson, "w", out var w)
                    || !TryGetDouble(faceJson, "h", out var h)
                    || !faceJson.TryGetProperty("index", out var indexJson)
                    || indexJson.ValueKind != JsonValueKind.Number
                    || !indexJson.TryGetInt32(out var index))
                {
                    throw new InvalidOperationException(
                        $"Scene catalog {scenesPath} scene {id} has a face without an integer index and numeric x, y, w, h.");
                }

                if (index != position)
                {
                    throw new InvalidOperationException(
                        $"Scene catalog {scenesPath} scene {id} faces are not ordered by index (position {position} carries index {index}).");
                }

                if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1 || y + h > 1)
                {
                    throw new InvalidOperationException(
                        $"Scene catalog {scenesPath} scene {id} face {index} falls outside the photo.");
                }

                faces.Add(new DockSceneFace(index, x, y, w, h));
                position++;
            }

            if (!spec.TryGetProperty("closeupScale", out var scaleJson)
                || scaleJson.ValueKind != JsonValueKind.Number
                || !scaleJson.TryGetInt32(out var scale)
                || scale < 1)
            {
                throw new InvalidOperationException(
                    $"Scene catalog {scenesPath} scene {id} needs a closeupScale of at least 1.");
            }

            scenes.Add(new DockScene(id, counts, baseFile, faces, scale));
        }

        if (scenes.Count == 0)
        {
            throw new InvalidOperationException(
                $"Scene catalog {scenesPath} lists no scenes.");
        }

        return scenes;
    }

    /// <summary>Maps ASN cartons onto scenes: exact or nearest-larger scene
    /// per chunk, first faces in carton order; counts above the largest
    /// scene split into several views. Every positive count yields photos.</summary>
    public static IReadOnlyList<LibraryChunk> AssignScenes(
        IReadOnlyList<DockScene> scenes, IReadOnlyList<AsnCarton> cartons)
    {
        if (scenes.Count == 0)
        {
            throw new InvalidOperationException("No library scenes to photograph with.");
        }

        if (cartons.Count == 0)
        {
            throw new InvalidOperationException("No cartons to photograph.");
        }

        var byCapacity = scenes.OrderBy(s => s.Faces.Count).ToList();
        var chunks = new List<LibraryChunk>();
        var rest = cartons.ToList();
        while (rest.Count > 0)
        {
            var scene = byCapacity.FirstOrDefault(s => s.Faces.Count >= rest.Count) ?? byCapacity[^1];
            var take = Math.Min(scene.Faces.Count, rest.Count);
            chunks.Add(new LibraryChunk(scene, rest.Take(take).ToList()));
            rest = rest.Skip(take).ToList();
        }

        return chunks;
    }

    /// <summary>Parses the face-locate answer: a JSON array of fractional
    /// boxes. Anything malformed is refused with the reason.</summary>
    public static IReadOnlyList<FaceBox> ParseFaces(string text)
    {
        var faces = new List<FaceBox>();
        foreach (var element in ParseReads(text))
        {
            if (element.ValueKind != JsonValueKind.Object
                || !TryGetDouble(element, "x", out var x)
                || !TryGetDouble(element, "y", out var y)
                || !TryGetDouble(element, "w", out var w)
                || !TryGetDouble(element, "h", out var h))
            {
                throw new JsonException("Face locate entry needs numeric x, y, w, h.");
            }

            if (x < 0 || y < 0 || w <= 0 || h <= 0 || x + w > 1 || y + h > 1)
            {
                throw new JsonException($"Face box ({x}, {y}, {w}, {h}) falls outside the photo.");
            }

            faces.Add(new FaceBox(x, y, w, h));
        }

        return faces;
    }

    private static bool TryGetDouble(JsonElement element, string key, out double value)
    {
        value = 0;
        return element.TryGetProperty(key, out var json)
            && json.ValueKind == JsonValueKind.Number
            && json.TryGetDouble(out value);
    }

    private static string SanitizeImageId(string cartonId) =>
        new(cartonId.Select(ch =>
            char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_').ToArray());

    private static string CanonicalLibraryKey(
        string imageId,
        byte[] baseBytes,
        IReadOnlyList<ResolvedLabel> labels,
        IReadOnlySet<string> damageIds,
        IReadOnlyList<FaceRect> occlude,
        string poNumber)
    {
        var key = new StringBuilder();
        key.Append(imageId).Append('|').Append(VisionReadCache.Sha256Hex(baseBytes)).Append('|').Append(poNumber);
        foreach (var label in labels)
        {
            key.Append('|').Append(label.CartonId).Append(',').Append(label.Part).Append(',').Append(label.Units)
                .Append(',').Append(label.Lot).Append(',').Append(label.MarkPresent ? 'M' : 'm')
                .Append(',').Append(label.X.ToString("F4")).Append(',').Append(label.Y.ToString("F4"))
                .Append(',').Append(label.W.ToString("F4")).Append(',').Append(label.H.ToString("F4"));
        }

        key.Append("|D:");
        foreach (var id in damageIds.OrderBy(x => x, StringComparer.Ordinal))
        {
            key.Append(id).Append(',');
        }

        key.Append("|H:");
        foreach (var hidden in occlude)
        {
            key.Append(hidden.X.ToString("F4")).Append(',').Append(hidden.Y.ToString("F4"))
                .Append(',').Append(hidden.W.ToString("F4")).Append(',').Append(hidden.H.ToString("F4")).Append(';');
        }

        return key.ToString();
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
                labels, crop, damage,
                spec.TryGetProperty("kind", out var kind) && !string.IsNullOrWhiteSpace(kind.GetString())
                    ? kind.GetString()!
                    : "pallet"));
        }

        return views;
    }

    /// <summary>Reshot (close-up) views from the specs: view id plus the
    /// cartons each covers. Data-driven: callers mint only cartons a close-up
    /// view actually covers, and refuse anything else honestly.</summary>
    public static IReadOnlyList<(string ViewId, IReadOnlyList<string> Cartons)> ReshotViews(string specsPath) =>
        LoadSpecs(specsPath)
            .Where(v => string.Equals(v.Kind, "reshot", StringComparison.OrdinalIgnoreCase))
            .Select(v => (v.Id, (IReadOnlyList<string>)v.Labels.Select(l => l.CartonId).ToList()))
            .ToList();

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

/// <summary>Problem kinds one shipment may pin on its cartons so the
/// photos show a hold: a missing lithium mark, a crushed face, or a
/// face that appears in no view.</summary>
public enum SimulatedProblemKind
{
    MissingMark,
    Crushed,
    Hidden
}

/// <summary>One named problem on one ASN carton.</summary>
public sealed record SimulatedProblem(string CartonId, SimulatedProblemKind Kind);

/// <summary>One library scene face: label box as fractions of image
/// width and height, origin top-left, ordered by index.</summary>
public sealed record DockSceneFace(int Index, double X, double Y, double W, double H);

/// <summary>One library scene from the catalog: base photo plus faces.</summary>
public sealed record DockScene(
    string Id, IReadOnlyList<int> Counts, string Base,
    IReadOnlyList<DockSceneFace> Faces, int CloseupScale);

/// <summary>One photo's share of an ASN: the scene plus the cartons on
/// its first faces in ASN order.</summary>
public sealed record LibraryChunk(DockScene Scene, IReadOnlyList<AsnCarton> Cartons);

/// <summary>One located carton face: fractional box in photo coords.</summary>
public sealed record FaceBox(double X, double Y, double W, double H);

/// <summary>Text-free pallet photo generation for the live path. Tests
/// inject fakes; the Meta implementation posts one generation call.</summary>
public interface ISceneGenerator
{
    string GeneratorName { get; }

    /// <summary>Generates one text-free pallet photo showing exactly
    /// <paramref name="cartonCount"/> cartons, of which <paramref name="crushedCount"/>
    /// show transit wear in the scene itself. Throws on transport or
    /// payload failures; the live path turns those into a fallback
    /// reason, never a silent pass.</summary>
    Task<byte[]> GenerateSceneAsync(int cartonCount, int crushedCount, CancellationToken ct = default);
}

public sealed record MetaSceneOptions(string BaseUrl, string SceneModel, string ApiKey);

/// <summary>Meta image generation for live scenes. The address and key
/// arrive via options from the MetaApi settings; the model is the
/// MetaImages scene model, defaulting to the image model.</summary>
public sealed class MetaSceneGenerator(HttpClient http, MetaSceneOptions options) : ISceneGenerator
{
    public string GeneratorName => "meta-images-generations";

    /// <summary>Pallet prompt for the shipment's own carton count. Crushed
    /// cartons arrive damaged in the scene itself, in neutral words: the
    /// damage wording tripped the image content filter before.</summary>
    public static string ScenePrompt(int cartonCount, int crushedCount = 0) =>
        "Photorealistic warehouse dock photograph, straight-on view of a wooden pallet " +
        $"stacked with exactly {cartonCount} plain brown cardboard shipping cartons, " +
        "every visible carton face completely blank with no text, no labels, no logos, " +
        "no markings, soft daylight, concrete floor, blurred dock door background, no people."
        + CrushedClause(crushedCount);

    private static string CrushedClause(int crushedCount) =>
        crushedCount <= 0
            ? string.Empty
            : crushedCount == 1
                ? " One carton with a caved-in corner and a torn side."
                : $" {crushedCount} cartons with caved-in corners and torn sides.";

    public async Task<byte[]> GenerateSceneAsync(int cartonCount, int crushedCount, CancellationToken ct = default)
    {
        if (cartonCount <= 0)
        {
            throw new InvalidOperationException(
                $"Cannot generate a pallet photo for {cartonCount} cartons.");
        }

        if (crushedCount < 0 || crushedCount > cartonCount)
        {
            throw new InvalidOperationException(
                $"Cannot show {crushedCount} worn carton(s) on a {cartonCount}-carton pallet.");
        }

        var endpoint = options.BaseUrl.TrimEnd('/') + "/images/generations";
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
        message.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        message.Content = new StringContent(
            JsonSerializer.Serialize(new { model = options.SceneModel, prompt = ScenePrompt(cartonCount, crushedCount), n = 1 }),
            Encoding.UTF8,
            "application/json");

        using var response = await http.SendAsync(message, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Image generation failed at {endpoint} (HTTP {(int)response.StatusCode}): {(body.Length <= 300 ? body : body[..300] + "...")}");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var first = doc.RootElement.GetProperty("data").EnumerateArray().First();
            if (first.TryGetProperty("b64_json", out var b64) && !string.IsNullOrWhiteSpace(b64.GetString()))
            {
                return Convert.FromBase64String(b64.GetString()!);
            }

            if (first.TryGetProperty("url", out var url) && !string.IsNullOrWhiteSpace(url.GetString()))
            {
                return await http.GetByteArrayAsync(url.GetString(), ct).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Image generation at {endpoint} returned an unparseable payload: {ex.Message}");
        }

        throw new InvalidOperationException(
            $"Image generation at {endpoint} returned neither b64_json nor url: {(body.Length <= 300 ? body : body[..300] + "...")}");
    }
}

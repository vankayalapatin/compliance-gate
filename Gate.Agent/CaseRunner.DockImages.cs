// ASN-time dock images. When a supplier sends an ASN, the pallet views are
// labeled from that ASN (the minted PO number plus the ASN carton lots) on
// the vision-owned base scenes, then read once live by Spark and cached by
// image hash like every other read. Files carry the ASN prefix so each
// truck's counter shows its own photos; the legacy unprefixed set stays for
// older flows. The relabel close-up is minted the same way when a reshot is
// requested: one AI labeling attempt, verify-then-composite fallback, and
// the path plus fallback reason recorded on the timeline.
using Gate.Contracts;

namespace Gate.Agent;

public sealed record MintedReshotImage(string FileName, string FilePath, DockImagePath Path, string? FallbackReason);

public partial class CaseRunner
{
    public async Task<DockImageSet?> BuildDockImagesAsync(string caseId, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var current = file.Case;
        if (current.AsnNumber is null)
        {
            throw new InvalidOperationException($"Case {caseId} has no ASN to label dock views from.");
        }

        if (_dockImageOptions is null)
        {
            throw new InvalidOperationException("Dock image pipeline is not configured.");
        }

        var asn = await _erp.GetAsnAsync(current.AsnNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ASN {current.AsnNumber} vanished.");
        var options = _dockImageOptions with
        {
            FilePrefix = $"{asn.AsnNumber}-",
            ViewKind = "pallet",
            ReadCacheDir = _visionReadCacheDir,
        };
        var pipeline = new DockImagePipeline(_imageLabeler ?? new NullImageLabeler(), _models);
        DockImageSet set;
        try
        {
            set = await pipeline.BuildAsync(asn, asn.PoNumber, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The ASN's cartons don't fit the vision-owned view specs (or a
            // read failed): the truck still works through counter images or
            // uploads, and the label check still judges whatever arrives.
            var noted = new CaseWork(CaseStore.Load(_cacheDir, caseId) ?? file);
            noted.AddEvent(CaseEventKind.Note,
                $"Dock views not built for ASN {asn.AsnNumber}: {ex.Message}; send counter images or upload photos.",
                documentType: "ASN", documentNumber: asn.AsnNumber);
            noted.Save(_cacheDir);
            return null;
        }

        var work = new CaseWork(CaseStore.Load(_cacheDir, caseId) ?? file);
        foreach (var image in set.Images)
        {
            work.AddEvent(CaseEventKind.Note,
                $"Dock view {Path.GetFileName(image.FilePath)} labeled for PO {asn.PoNumber} " +
                $"via {image.Path.ToString().ToLowerInvariant()}" +
                (image.FallbackReason is null ? string.Empty : $" (fallback: {image.FallbackReason})") +
                (image.ReadFromCache ? "; read reused from cache." : "; read live."),
                documentType: "ASN", documentNumber: asn.AsnNumber);
        }

        work.Save(_cacheDir);
        return set;
    }

    public async Task<MintedReshotImage> MintReshotImageAsync(
        string caseId, string cartonId, CancellationToken ct = default)
    {
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var current = file.Case;
        if (current.AsnNumber is null)
        {
            throw new InvalidOperationException($"Case {caseId} has no ASN to mint a reshot from.");
        }

        if (_dockImageOptions is null)
        {
            throw new InvalidOperationException("Dock image pipeline is not configured.");
        }

        var covering = DockImagePipeline.ReshotViews(_dockImageOptions.ImageSpecsPath)
            .FirstOrDefault(v => v.Cartons.Contains(cartonId, StringComparer.Ordinal));
        if (covering.ViewId is null)
        {
            throw new InvalidOperationException(
                $"No close-up view covers carton {cartonId}; upload a photo instead.");
        }

        var asn = await _erp.GetAsnAsync(current.AsnNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ASN {current.AsnNumber} vanished.");
        var options = _dockImageOptions with
        {
            FilePrefix = $"{asn.AsnNumber}-",
            ViewKind = "reshot",
            ReadCacheDir = _visionReadCacheDir,
        };
        var pipeline = new DockImagePipeline(_imageLabeler ?? new NullImageLabeler(), _models);
        var set = await pipeline.BuildAsync(asn, asn.PoNumber, options, ct).ConfigureAwait(false);
        var image = set.Images.First(i =>
            string.Equals(i.ImageId, covering.ViewId, StringComparison.Ordinal));
        var work = new CaseWork(CaseStore.Load(_cacheDir, caseId) ?? file);
        work.AddEvent(CaseEventKind.Note,
            $"Relabeled close-up {Path.GetFileName(image.FilePath)} minted for {cartonId} " +
            $"via {image.Path.ToString().ToLowerInvariant()}" +
            (image.FallbackReason is null ? string.Empty : $" (fallback: {image.FallbackReason})") +
            (image.ReadFromCache ? "; read reused from cache." : "; read live."),
            documentType: "ASN", documentNumber: asn.AsnNumber);
        work.Save(_cacheDir);
        return new MintedReshotImage(
            Path.GetFileName(image.FilePath), image.FilePath, image.Path, image.FallbackReason);
    }
}

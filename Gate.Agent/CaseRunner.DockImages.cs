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
    /// <summary>Merges stored problems with an override set: the override
    /// wins per carton, ordered by carton id for determinism.</summary>
    private static IReadOnlyList<SimulatedProblem> MergeProblems(
        IReadOnlyList<SimulatedProblem> stored, IReadOnlyList<SimulatedProblem> wins)
    {
        var merged = stored.ToDictionary(p => p.CartonId, p => p, StringComparer.Ordinal);
        foreach (var problem in wins)
        {
            merged[problem.CartonId] = problem;
        }

        return merged.Values.OrderBy(p => p.CartonId, StringComparer.Ordinal).ToList();
    }

    /// <summary>Rebuilds the case dock views with a problems override: the
    /// problems merge into the stored set (winning per carton) and the
    /// views rebuild against the merged set.</summary>
    public async Task<DockImageSet?> RebuildDockViewsAsync(
        RebuildDockViewsInput input, CancellationToken ct = default)
    {
        _ = CaseStore.Load(_cacheDir, input.CaseId)
            ?? throw new InvalidOperationException($"Unknown case {input.CaseId}.");
        var stored = CaseStore.LoadProblems(_cacheDir, input.CaseId)?.Problems ?? [];
        var merged = MergeProblems(stored, input.Problems);
        CaseStore.SaveProblems(
            _cacheDir, new DockProblems(input.CaseId, merged, DateTimeOffset.UtcNow));
        return await BuildDockImagesAsync(input.CaseId, ct, merged).ConfigureAwait(false);
    }

    public async Task<DockImageSet?> BuildDockImagesAsync(
        string caseId, CancellationToken ct = default, IReadOnlyList<SimulatedProblem>? problems = null)
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
        var effective = MergeProblems(
            CaseStore.LoadProblems(_cacheDir, caseId)?.Problems ?? [], problems ?? []);
        if (effective.Count > 0)
        {
            var requested = new CaseWork(CaseStore.Load(_cacheDir, caseId) ?? file);
            requested.AddEvent(CaseEventKind.Note,
                $"Dock views for ASN {asn.AsnNumber} carry {effective.Count} simulated problem(s): " +
                string.Join("; ", effective.Select(p => $"{p.CartonId} ({p.Kind})")) + ".",
                documentType: "ASN", documentNumber: asn.AsnNumber);
            requested.Save(_cacheDir);
        }

        var options = _dockImageOptions with
        {
            FilePrefix = $"{asn.AsnNumber}-",
            ViewKind = "pallet",
            ReadCacheDir = _visionReadCacheDir,
        };
        if (_dockSceneLive.LiveOn)
        {
            var live = await TryBuildLiveSceneAsync(caseId, asn, options, effective, ct).ConfigureAwait(false);
            if (live is not null)
            {
                return live;
            }
        }

        // The library build covers every carton count with the ASN's own
        // labels; problems ride along into the scene. On failure the truck
        // still works through counter images or uploads, and the label
        // check still judges whatever arrives.
        var pipeline = new DockImagePipeline(_imageLabeler ?? new NullImageLabeler(), _models);
        DockImageSet set;
        try
        {
            set = await pipeline.BuildLibraryAsync(asn, asn.PoNumber, effective, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
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
                (image.ReadFromCache ? "; read reused from cache." : "; read live.") +
                (image.ReadWarning is null ? string.Empty : $" Read warning: {image.ReadWarning}."),
                documentType: "ASN", documentNumber: asn.AsnNumber);
        }

        work.Save(_cacheDir);
        return set;
    }

    /// <summary>Live scene attempt behind the live switch: a live set wins
    /// with its own timeline note, while a null answer falls back to the
    /// library build (null return) after recording the reason. The
    /// library build's own set notes follow on the fallback path.</summary>
    private async Task<DockImageSet?> TryBuildLiveSceneAsync(
        string caseId, AsnDto asn, DockImageOptions options,
        IReadOnlyList<SimulatedProblem> problems, CancellationToken ct)
    {
        var (liveSet, liveReason) = await new DockImagePipeline(
                _imageLabeler ?? new NullImageLabeler(), _models,
                sceneGenerator: _dockSceneLive.SceneGenerator)
            .TryBuildLiveSceneAsync(asn, asn.PoNumber, problems, options, ct).ConfigureAwait(false);
        var file = CaseStore.Load(_cacheDir, caseId)
            ?? throw new InvalidOperationException($"Unknown case {caseId}.");
        var work = new CaseWork(file);
        if (liveSet is not null)
        {
            work.AddEvent(CaseEventKind.Note,
                $"Dock views for ASN {asn.AsnNumber} built live.",
                documentType: "ASN", documentNumber: asn.AsnNumber);
            work.Save(_cacheDir);
            return liveSet;
        }

        work.AddEvent(CaseEventKind.Note,
            $"Live dock scene unavailable ({liveReason ?? "no reason given"}); library views used instead.",
            documentType: "ASN", documentNumber: asn.AsnNumber);
        work.Save(_cacheDir);
        _dockSceneLive.RecordFallback(liveReason);
        return null;
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

        var asn = await _erp.GetAsnAsync(current.AsnNumber, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ASN {current.AsnNumber} vanished.");
        // A reshot re-photographs the carton: a missing mark gets a fresh
        // look (the new photo shows it), while staged damage stays staged
        // and a hidden carton still refuses with upload guidance.
        var stored = CaseStore.LoadProblems(_cacheDir, caseId)?.Problems ?? [];
        var closeupProblems = stored
            .Where(p => !(string.Equals(p.CartonId, cartonId, StringComparison.Ordinal)
                && p.Kind == SimulatedProblemKind.MissingMark))
            .ToList();
        var options = _dockImageOptions with
        {
            FilePrefix = $"{asn.AsnNumber}-",
            ViewKind = "reshot",
            ReadCacheDir = _visionReadCacheDir,
        };
        var pipeline = new DockImagePipeline(_imageLabeler ?? new NullImageLabeler(), _models);
        DockImageResult image;
        try
        {
            image = await pipeline.BuildCloseupAsync(
                asn, asn.PoNumber, cartonId, closeupProblems, options, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{ex.Message} Upload a photo instead.");
        }

        var work = new CaseWork(CaseStore.Load(_cacheDir, caseId) ?? file);
        work.AddEvent(CaseEventKind.Note,
            $"Relabeled close-up {Path.GetFileName(image.FilePath)} minted for {cartonId} " +
            $"via {image.Path.ToString().ToLowerInvariant()}" +
            (image.FallbackReason is null ? string.Empty : $" (fallback: {image.FallbackReason})") +
            (image.ReadFromCache ? "; read reused from cache." : "; read live.") +
            (image.ReadWarning is null ? string.Empty : $" Read warning: {image.ReadWarning}."),
            documentType: "ASN", documentNumber: asn.AsnNumber);
        work.Save(_cacheDir);
        return new MintedReshotImage(
            Path.GetFileName(image.FilePath), image.FilePath, image.Path, image.FallbackReason);
    }
}

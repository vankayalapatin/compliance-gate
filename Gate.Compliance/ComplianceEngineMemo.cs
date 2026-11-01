// Memo-to-rule seam on the compliance engine: the thirteen
// IComplianceEngine memo methods, each a thin deterministic delegation to
// the Memo core. No policy logic lives here; the core decides, this seam
// resolves inputs (KB versioned files, current pack JSON) and returns the
// core's verdicts.
//
// Paths: content-root-relative defaults, overridable two ways:
//   1. Host configuration keys Compliance:CompanyKb and
//      Compliance:PackDirectory, bound via OptionsFromConfig and passed to
//      the options constructor (the host wires this; the default engine
//      constructor keeps the defaults).
//   2. ComplianceMemoOptions directly (tests, tools).
// Relative paths resolve upward from the app base directory like the
// scenario loader, so repo-relative defaults work in dev and test layouts.
// The company-KB path locates the versioned set's directory (the frozen
// base file plus company-kb.kbvN.json files); the seam always loads the
// highest version, and publish mints the next one beside it.
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Compliance;

/// <summary>Memo-seam path overrides. Null or blank means the engine
/// default for that path.</summary>
public sealed record ComplianceMemoOptions(
    string? CompanyKbPath = null,
    string? PackDirectory = null);

public sealed partial class ComplianceEngine
{
    /// <summary>Host config key overriding the company KB file path.</summary>
    public const string CompanyKbConfigKey = "Compliance:CompanyKb";

    /// <summary>Host config key overriding the rule-pack directory (the
    /// writable directory publish reads the current pack JSON from and
    /// writes new versioned pack files to).</summary>
    public const string PackDirectoryConfigKey = "Compliance:PackDirectory";

    /// <summary>Default company KB file, content-root-relative.</summary>
    public const string DefaultCompanyKbPath = "company/company-kb.json";

    /// <summary>Default rule-pack directory, content-root-relative. Holds
    /// the base pack files (corp.json, ...) plus published versioned files
    /// (corp.v0.3.json, ...); the seam always loads the highest version.</summary>
    public const string DefaultPackDirectory = "Gate.Compliance/Packs";

    private readonly string _companyKbPath = DefaultCompanyKbPath;
    private readonly string _packDirectory = DefaultPackDirectory;

    /// <summary>Engine with memo-seam path overrides. Screening still runs
    /// against the bundled snapshot.</summary>
    public ComplianceEngine(ComplianceMemoOptions options)
        : this(LoadDefaultSnapshot)
    {
        var opts = options ?? new ComplianceMemoOptions();
        if (!string.IsNullOrWhiteSpace(opts.CompanyKbPath))
        {
            _companyKbPath = opts.CompanyKbPath;
        }

        if (!string.IsNullOrWhiteSpace(opts.PackDirectory))
        {
            _packDirectory = opts.PackDirectory;
        }
    }

    /// <summary>Binds memo-seam paths from host configuration. The host
    /// passes its config getter (e.g. <c>key => config[key]</c>); missing
    /// keys fall back to the engine defaults.</summary>
    public static ComplianceMemoOptions OptionsFromConfig(Func<string, string?> getValue)
    {
        ArgumentNullException.ThrowIfNull(getValue);
        return new ComplianceMemoOptions(getValue(CompanyKbConfigKey), getValue(PackDirectoryConfigKey));
    }

    /// <summary>Loads the highest versioned KB file and returns the pinned
    /// snapshot proposals resolve against. A hash mismatch refuses loudly
    /// (drift fails, never passes silently).</summary>
    public Task<KbSnapshot> LoadKbSnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = ResolveKbDirectory();
        return Task.FromResult(CompanyKbLoader.LoadVersioned(KbInputs(dir)).ToSnapshot());
    }

    /// <summary>Loads one exact KB version for pinned display (citations
    /// carry their version). An unknown version refuses loudly.</summary>
    public Task<KbSnapshot> LoadKbSnapshotAtAsync(string kbVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(kbVersion);
        var dir = ResolveKbDirectory();
        return Task.FromResult(CompanyKbLoader.LoadVersionedAt(KbInputs(dir), kbVersion).ToSnapshot());
    }

    /// <summary>Publishes one KB version from a base version plus changes:
    /// single-space edits only, approved by the space owner, editors never
    /// approving their own change. The base must be the current highest
    /// version (stale bases refuse); the writer mints the next version and
    /// the new file lands beside its priors. The acting approver flows in
    /// approvedBy; a delegate approving for the space owner additionally
    /// names them in approvedFor (the caller verifies the delegation
    /// window first).</summary>
    public Task<KbPublication> PublishKbUpdateAsync(
        string baseVersion,
        IReadOnlyList<KbEntryChange> changes,
        string approvedBy,
        string reason,
        string activationId,
        DateTimeOffset publishedAt,
        string? approvedFor = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(baseVersion);
        var dir = ResolveKbDirectory();
        var inputs = KbInputs(dir);
        var baseFile = CompanyKbLoader.LoadVersionedAt(inputs, baseVersion);
        var highest = CompanyKbLoader.LoadVersioned(inputs);
        if (!string.Equals(baseFile.Version, highest.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Stale KB base: the caller pins '{baseVersion}' but the current KB is '{highest.Version}'. " +
                "Re-base on the current version before publishing.");
        }

        // LoadVersionedAt proved exactly one input declares the base
        // version (duplicates refuse), so this lookup cannot miss.
        var baseJson = inputs
            .Single(i => string.Equals(CompanyKbLoader.ReadVersion(i.Name, i.Json), baseVersion, StringComparison.Ordinal))
            .Json;
        var publication = CompanyKbWriter.BuildNextVersion(
            baseJson, baseVersion, changes, approvedBy, reason, activationId, publishedAt, approvedFor);
        CompanyKbWriter.WriteToDirectory(publication, dir);
        return Task.FromResult(publication);
    }

    /// <summary>Knowledge spaces with owners from the approval matrix,
    /// sections per space, and member names for display. Members are the
    /// distinct matrix owners, sorted. A space without its owner row
    /// refuses loudly.</summary>
    public Task<IReadOnlyList<KbSpace>> ListKbSpacesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = ResolveKbDirectory();
        var snapshot = CompanyKbLoader.LoadVersioned(KbInputs(dir)).ToSnapshot();
        var members = snapshot.ApprovalMatrix
            .Select(a => a.Owner)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(o => o, StringComparer.Ordinal)
            .ToList();
        var spaces = new List<KbSpace>();
        foreach (var space in CompanyKbSpaces.All)
        {
            var action = CompanyKbSpaces.OwnerActionFor(space);
            var rows = snapshot.ApprovalMatrix
                .Where(a => string.Equals(a.Action, action, StringComparison.Ordinal))
                .ToList();
            if (rows.Count == 0)
            {
                throw new InvalidOperationException(
                    $"KB '{snapshot.KbVersion}' carries no approval-matrix row for '{action}'; " +
                    $"space '{space}' has no owner to list.");
            }

            if (rows.Count > 1)
            {
                throw new InvalidOperationException(
                    $"KB '{snapshot.KbVersion}' carries {rows.Count} approval-matrix rows for '{action}'; " +
                    $"space '{space}' has no single owner to list.");
            }

            spaces.Add(new KbSpace(
                space, rows[0].Owner, rows[0].OwnerRole,
                CompanyKbSpaces.SectionsFor(space), members));
        }

        return Task.FromResult<IReadOnlyList<KbSpace>>(spaces);
    }

    /// <summary>Live pack rules with pack versions for the Rules views.
    /// The UI reads packs through here, never from the pack directory.</summary>
    public Task<IReadOnlyList<LivePackRule>> ListLivePackRulesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = ResolvePackDirectory();
        var inputs = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
        var set = RulePackLoader.LoadPacks(inputs);
        IReadOnlyList<LivePackRule> rules = set.Rules
            .Select(r => new LivePackRule(
                r.Pack, r.PackVersion, r.RuleId, r.RuleType, r.Tier,
                ToMemoGate(r.Gate), r.Title, r.Enforced, r.Owner, r.AdoptedDate, r.Source))
            .ToList();
        return Task.FromResult(rules);
    }

    /// <summary>Canonical proposal hash via the Memo core.</summary>
    public Task<string> ComputeProposalHashAsync(RuleProposal proposal, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(proposal);
        return Task.FromResult(ProposalHasher.Hash(proposal));
    }

    /// <summary>Structural plus KB-pinned proposal validation via the Memo
    /// core.</summary>
    public Task<ProposalValidation> ValidateProposalAsync(
        RuleProposal proposal, KbSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(snapshot);
        return Task.FromResult(ProposalValidator.ValidateAgainstKb(proposal, snapshot));
    }

    /// <summary>Live-pack conflicts for a proposed rule via the Memo core.</summary>
    public Task<IReadOnlyList<RuleConflict>> DetectConflictsAsync(
        ProposedRule rule, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(rule);
        return Task.FromResult(RuleConflictDetector.Compute(rule));
    }

    /// <summary>Read-only old-vs-new impact report via the Memo core.
    /// Pack-version rule: an explicitly supplied (non-empty) map wins
    /// untouched, so replays with pinned versions stay byte-identical; an
    /// empty map resolves the live pack versions internally via the
    /// multi-version loader over the pack directory. Resolution failure
    /// refuses loudly instead of stamping an empty map.</summary>
    public Task<WhatIfReport> ComputeWhatIfAsync(
        IReadOnlyList<WhatIfHistoryRow> historyRows,
        IReadOnlyList<WhatIfPipelineRow> pipelineRows,
        WhatIfRuleConfig? oldConfig,
        WhatIfRuleConfig newConfig,
        string? oldRuleId,
        KbFiscalCalendar? fiscalCalendar,
        string proposalHash,
        string kbVersion,
        IReadOnlyDictionary<string, string> packVersions,
        DateTimeOffset computedAt,
        IReadOnlyList<WhatIfPipelineRow>? probes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(packVersions);
        var effective = packVersions.Count == 0 ? LivePackVersions() : packVersions;
        return Task.FromResult(WhatIfCalculator.Compute(
            historyRows, pipelineRows, oldConfig, newConfig, oldRuleId,
            fiscalCalendar, proposalHash, kbVersion, effective, computedAt, probes));
    }

    /// <summary>Human-approval-pin validation via the Memo core.</summary>
    public Task<ApprovalValidation> ValidateApprovalAsync(
        RuleApproval approval, RuleProposal proposal, string currentKbVersion,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentKbVersion);
        return Task.FromResult(ApprovalValidator.Validate(approval, proposal, currentKbVersion));
    }

    /// <summary>Publishes a parameter change: loads the current (highest
    /// version) pack JSON for the pack, builds the version-bumped pack via
    /// the Memo core, writes it into the pack directory, and returns the
    /// publication.</summary>
    public Task<PackPublication> PublishRuleUpdateAsync(
        string packId,
        RuleProposal proposal,
        ProposalValidation validation,
        RuleApproval approval,
        IReadOnlyList<string> testEvidence,
        string ruleId,
        string activationId,
        DateTimeOffset publishedAt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = ResolvePackDirectory();
        var priorJson = CurrentPackJson(packId, dir);
        var publication = PackPublisher.BuildUpdate(
            priorJson, proposal, validation, approval, testEvidence,
            ruleId, activationId, publishedAt);
        PackPublisher.PublishToDirectory(publication, dir);
        return Task.FromResult(publication);
    }

    /// <summary>Publishes a brand-new rule: loads the current pack JSON,
    /// builds via the Memo core, writes, and returns the publication.</summary>
    public Task<PackPublication> PublishNewRuleAsync(
        string packId,
        RuleProposal proposal,
        ProposalValidation validation,
        RuleApproval approval,
        IReadOnlyList<string> testEvidence,
        string ruleId,
        NewRuleSpec spec,
        string activationId,
        DateTimeOffset publishedAt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = ResolvePackDirectory();
        var priorJson = CurrentPackJson(packId, dir);
        var publication = PackPublisher.BuildNewRule(
            priorJson, proposal, validation, approval, testEvidence,
            ruleId, spec, activationId, publishedAt);
        PackPublisher.PublishToDirectory(publication, dir);
        return Task.FromResult(publication);
    }

    /// <summary>Publishes a rollback: loads the current pack JSON plus the
    /// named prior version's JSON, builds the restoring version via the
    /// Memo core, writes, and returns the publication.</summary>
    public Task<PackPublication> PublishRollbackAsync(
        string packId,
        string restoreVersion,
        string memoId,
        string approver,
        DateTimeOffset effectiveAt,
        RuleApplicability applicability,
        string proposalHash,
        string reason,
        string ruleId,
        string activationId,
        DateTimeOffset publishedAt,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var dir = ResolvePackDirectory();
        var currentJson = CurrentPackJson(packId, dir);
        var restoreJson = RestorePackJson(packId, restoreVersion, dir);
        var publication = PackPublisher.BuildRollback(
            currentJson, restoreJson, memoId, approver, effectiveAt,
            applicability, proposalHash, reason, ruleId, activationId, publishedAt);
        PackPublisher.PublishToDirectory(publication, dir);
        return Task.FromResult(publication);
    }

    /// <summary>Resolves the versioned KB set's directory from the
    /// configured KB path (which names the base file, or the directory
    /// itself). A missing directory refuses loudly with the config key.</summary>
    private string ResolveKbDirectory()
    {
        if (Directory.Exists(_companyKbPath))
        {
            return Path.GetFullPath(_companyKbPath);
        }

        var file = CompanyKbLoader.ResolvePath(_companyKbPath);
        var dir = Path.GetDirectoryName(file);
        if (dir is null || !Directory.Exists(dir))
        {
            throw new InvalidOperationException(
                $"KB directory not found: '{_companyKbPath}' (resolved to '{file}'). " +
                $"Override via config key '{CompanyKbConfigKey}'.");
        }

        return dir;
    }

    /// <summary>Reads every KB file in a directory as loader inputs, in
    /// filename order. An empty directory refuses loudly.</summary>
    private static List<(string Name, string Json)> KbInputs(string dir)
    {
        var inputs = Directory.GetFiles(dir, "company-kb*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
        if (inputs.Count == 0)
        {
            throw new InvalidOperationException(
                $"KB directory '{dir}' carries no KB files ('company-kb.json' plus 'company-kb.<version>.json').");
        }

        return inputs;
    }

    private static MemoRuleGate ToMemoGate(ComplianceGate gate) => gate switch
    {
        ComplianceGate.Vendor => MemoRuleGate.Vendor,
        ComplianceGate.Dock => MemoRuleGate.Dock,
        ComplianceGate.PrePay => MemoRuleGate.PrePay,
        _ => throw new InvalidOperationException($"Unknown compliance gate '{gate}'."),
    };

    private string ResolvePackDirectory()
    {
        var dir = ResolveDirectory(_packDirectory);
        if (!Directory.Exists(dir))
        {
            throw new InvalidOperationException(
                $"Pack directory not found: '{_packDirectory}' (resolved to '{dir}'). " +
                $"Override via config key '{PackDirectoryConfigKey}'.");
        }

        return dir;
    }

    /// <summary>Resolves the live pack versions: every pack JSON in the pack
    /// directory through the multi-version loader (highest version wins per
    /// pack), reduced to the pack-name-to-version map the report stamps. Any
    /// loader failure (missing pack, duplicate or malformed version, filename
    /// mismatch) throws; the seam never invents or defaults a version.</summary>
    private IReadOnlyDictionary<string, string> LivePackVersions()
    {
        var dir = ResolvePackDirectory();
        var inputs = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
        var set = RulePackLoader.LoadPacks(inputs);
        return set.Rules
            .Select(r => (r.Pack, r.PackVersion))
            .Distinct()
            .ToDictionary(t => t.Pack, t => t.PackVersion, StringComparer.Ordinal);
    }

    private static string ResolveDirectory(string configured)
    {
        if (Directory.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, configured);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(configured);
    }

    /// <summary>Loads the current pack JSON for a pack: the highest-version
    /// candidate among the base file plus versioned files, selected with
    /// PackVersions (the same highest-wins arithmetic the multi-version
    /// loader enforces, so publish and load can never disagree).</summary>
    private static string CurrentPackJson(string packId, string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        var candidates = PackCandidates(packId, dir);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Unknown pack '{packId}': directory '{dir}' carries no pack files for it " +
                $"(packs present: {DescribePacks(dir)}).");
        }

        return HighestPackJson(packId, candidates).Json;
    }

    /// <summary>Loads the named prior version's pack JSON for a rollback.
    /// An unknown version refuses loudly, naming what is available.</summary>
    private static string RestorePackJson(string packId, string restoreVersion, string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreVersion);
        var candidates = PackCandidates(packId, dir);
        var match = candidates.FirstOrDefault(c =>
            string.Equals(c.Version, restoreVersion, StringComparison.Ordinal));
        if (match is null)
        {
            var available = candidates.Count == 0
                ? "(no pack files for this pack)"
                : string.Join(", ", candidates.Select(c => c.Version).OrderBy(v => v, StringComparer.Ordinal));
            throw new InvalidOperationException(
                $"Cannot roll back pack '{packId}': version '{restoreVersion}' not found in '{dir}' " +
                $"(available: {available}).");
        }

        return match.Json;
    }

    private sealed record PackCandidate(string File, string Json, string Version);

    private static List<PackCandidate> PackCandidates(string packId, string dir)
    {
        var prefix = packId.Trim().ToLowerInvariant();
        var found = new List<PackCandidate>();
        foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (!string.Equals(name, $"{prefix}.json", StringComparison.Ordinal)
                && !(name.StartsWith($"{prefix}.v", StringComparison.Ordinal)
                     && name.EndsWith(".json", StringComparison.Ordinal)))
            {
                continue;
            }

            var json = File.ReadAllText(file);
            var version = ReadPackVersion(file, json);
            var suffix = FileVersionSuffix(name, prefix);
            if (suffix is not null && !string.Equals(suffix, version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pack file '{name}' names version '{suffix}' but declares '{version}'; refusing to guess which wins.");
            }

            try
            {
                PackVersions.Parse(version);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Pack file '{name}' carries an invalid version: {ex.Message}", ex);
            }

            found.Add(new PackCandidate(file, json, version));
        }

        return found;
    }

    private static PackCandidate HighestPackJson(string packId, List<PackCandidate> candidates)
    {
        var ordered = candidates
            .OrderByDescending(c => c.Version, Comparer<string>.Create(PackVersions.Compare))
            .ToList();
        if (ordered.Count > 1
            && PackVersions.Compare(ordered[0].Version, ordered[1].Version) == 0)
        {
            throw new InvalidOperationException(
                $"Pack '{packId}' ships version '{ordered[0].Version}' twice " +
                $"('{Path.GetFileName(ordered[0].File)}' and '{Path.GetFileName(ordered[1].File)}'); " +
                "versions must be unique so exactly one wins.");
        }

        return ordered[0];
    }

    private static string ReadPackVersion(string file, string json)
    {
        var name = Path.GetFileName(file);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(version.GetString()))
            {
                return version.GetString()!;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Rule pack '{name}' is not valid JSON.", ex);
        }

        throw new InvalidOperationException($"Rule pack '{name}' carries no version property; refusing to guess which wins.");
    }

    /// <summary>Extracts the .vN.M filename version from versioned pack
    /// files (corp.v0.3.json), or null for base files (corp.json).</summary>
    private static string? FileVersionSuffix(string fileName, string prefix)
    {
        if (string.Equals(fileName, $"{prefix}.json", StringComparison.Ordinal))
        {
            return null;
        }

        return fileName[(prefix.Length + 1)..^".json".Length];
    }

    private static string DescribePacks(string dir)
    {
        var packs = Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
            .Select(f => Path.GetFileName(f))
            .Select(name =>
            {
                var stem = name[..^".json".Length];
                var versionAt = stem.IndexOf(".v", StringComparison.Ordinal);
                return versionAt < 0 ? stem : stem[..versionAt];
            })
            .Where(stem => stem.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(stem => stem, StringComparer.Ordinal)
            .ToList();
        return packs.Count == 0 ? "(none)" : string.Join(", ", packs);
    }
}

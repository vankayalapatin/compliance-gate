// File-backed agent case store. One file per case named <CaseId>.json
// containing the CaseFile record (case, events, decisions), serialized with
// web defaults. Byte-exact convention shared with the UI lane:
//   - directory is env GATE_CASE_CACHE, else <content-root>/data/case-cache;
//   - library and test code take the dir as a parameter (tests use temp dirs);
//   - single-user demo, last-writer-wins: callers re-read before each step.
// Offline replay reads these files only. Small private read/write helpers
// live here; no shared libraries.
using System.Text.Json;
using Gate.Contracts;

namespace Gate.Agent;

public static class CaseStore
{
    public const string CacheDirEnvVar = "GATE_CASE_CACHE";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>Resolves the case-cache directory: env override, else
    /// <content-root>/data/case-cache. Callers pass the result to Load/Save.</summary>
    public static string ResolveCacheDir(string? contentRoot = null)
    {
        var env = Environment.GetEnvironmentVariable(CacheDirEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return Path.Combine(contentRoot ?? AppContext.BaseDirectory, "data", "case-cache");
    }

    /// <summary>Mints a deterministic filesystem-safe case id as
    /// {soNumber}-{itemId} (or {docType}-{docNumber} for document shells)
    /// with unsafe chars replaced by '-'. The scenario segment is gone:
    /// the first parameter stays only so older callers keep compiling and
    /// is ignored. The safe set is fixed (letters, digits, dash,
    /// underscore, dot) so the same inputs mint the same id on every OS.</summary>
    public static string MintCaseId(string scenario, string soNumber, string itemId)
    {
        _ = scenario;
        return MintCaseId(soNumber, itemId);
    }

    /// <summary>Mints a deterministic filesystem-safe case id as
    /// {first}-{second} with unsafe chars replaced by '-'.</summary>
    public static string MintCaseId(string first, string second)
    {
        var raw = $"{first}-{second}";
        var chars = raw.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        return new string(chars);
    }

    public static string CaseFilePath(string cacheDir, string caseId) =>
        Path.Combine(cacheDir, caseId + ".json");

    /// <summary>Per-carton dock evidence sidecar: the reads plus the verdicts
    /// from the case's dock evaluation, persisted next to the case file as
    /// &lt;CaseId&gt;.dock.json. A sidecar file; never a case file.</summary>
    public static string DockFilePath(string cacheDir, string caseId) =>
        Path.Combine(cacheDir, caseId + ".dock.json");

    public static DockEvidence? LoadDock(string cacheDir, string caseId)
    {
        var path = DockFilePath(cacheDir, caseId);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<DockEvidence>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidOperationException($"Dock file {path} did not parse as DockEvidence.");
    }

    public static void SaveDock(string cacheDir, DockEvidence evidence)
    {
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(DockFilePath(cacheDir, evidence.CaseId), JsonSerializer.Serialize(evidence, JsonOpts));
    }

    /// <summary>Staged dock-problem sidecar: the simulated problems for the
    /// case's dock views, persisted next to the case file as
    /// &lt;CaseId&gt;.problems.json. A sidecar file; never a case file.</summary>
    public static string ProblemsFilePath(string cacheDir, string caseId) =>
        Path.Combine(cacheDir, caseId + ".problems.json");

    public static DockProblems? LoadProblems(string cacheDir, string caseId)
    {
        var path = ProblemsFilePath(cacheDir, caseId);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<DockProblems>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidOperationException($"Problems file {path} did not parse as DockProblems.");
    }

    public static void SaveProblems(string cacheDir, DockProblems problems)
    {
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(ProblemsFilePath(cacheDir, problems.CaseId), JsonSerializer.Serialize(problems, JsonOpts));
    }

    public static CaseFile? Load(string cacheDir, string caseId)
    {
        var path = CaseFilePath(cacheDir, caseId);
        if (!File.Exists(path))
        {
            return null;
        }

        return ReadFile(path);
    }

    public static void Save(string cacheDir, CaseFile file)
    {
        Directory.CreateDirectory(cacheDir);
        WriteFile(CaseFilePath(cacheDir, file.Case.CaseId), file);
    }

    public static IReadOnlyList<string> ListCaseIds(string cacheDir)
    {
        if (!Directory.Exists(cacheDir))
        {
            return [];
        }

        // Mail files (<CaseId>.mail.json), vendor-identity pendings
        // (<CaseId>.vendor-identity.json), dock evidence sidecars
        // (<CaseId>.dock.json), dock-problem sidecars
        // (<CaseId>.problems.json), and memo files (<MemoId>.memo.json)
        // share the dir but are not cases.
        return Directory.GetFiles(cacheDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => !id!.EndsWith(".mail", StringComparison.Ordinal))
            .Where(id => !id!.EndsWith(".vendor-identity", StringComparison.Ordinal))
            .Where(id => !id!.EndsWith(".dock", StringComparison.Ordinal))
            .Where(id => !id!.EndsWith(".problems", StringComparison.Ordinal))
            .Where(id => !id!.EndsWith(".memo", StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList()!;
    }

    private static CaseFile ReadFile(string path) =>
        (JsonSerializer.Deserialize<CaseFile>(File.ReadAllText(path), JsonOpts)
            ?? throw new InvalidOperationException($"Case file {path} did not parse as a CaseFile."))
        .Normalized();

    private static void WriteFile(string path, CaseFile file) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpts));
}

/// <summary>Per-carton dock evidence for one case: the photo transcriptions
/// plus the verdicts the dock check gave them. Persisted as a sidecar file
/// (&lt;CaseId&gt;.dock.json) so older case files without one still load.</summary>
public sealed record DockEvidence(
    string CaseId,
    IReadOnlyList<DockCartonRead> Reads,
    IReadOnlyDictionary<string, IReadOnlyList<Verdict>> Verdicts,
    DateTimeOffset RecordedAt);

/// <summary>Staged dock problems for one case: which carton views carry a
/// staged problem and how. Persisted as a sidecar file
/// (&lt;CaseId&gt;.problems.json) so older case files without one still load.</summary>
public sealed record DockProblems(
    string CaseId,
    IReadOnlyList<SimulatedProblem> Problems,
    DateTimeOffset RecordedAt);

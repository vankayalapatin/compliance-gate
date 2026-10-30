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
    /// {scenario}-{soNumber}-{itemId} with unsafe chars replaced by '-'.
    /// The safe set is fixed (letters, digits, dash, underscore, dot) so the
    /// same inputs mint the same id on every OS.</summary>
    public static string MintCaseId(string scenario, string soNumber, string itemId)
    {
        var raw = $"{scenario}-{soNumber}-{itemId}";
        var chars = raw.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        return new string(chars);
    }

    public static string CaseFilePath(string cacheDir, string caseId) =>
        Path.Combine(cacheDir, caseId + ".json");

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

        return Directory.GetFiles(cacheDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList()!;
    }

    private static CaseFile ReadFile(string path) =>
        JsonSerializer.Deserialize<CaseFile>(File.ReadAllText(path), JsonOpts)
        ?? throw new InvalidOperationException($"Case file {path} did not parse as a CaseFile.");

    private static void WriteFile(string path, CaseFile file) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOpts));
}

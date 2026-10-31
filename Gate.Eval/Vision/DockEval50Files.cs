// Vision-owned paths for the 50-photo dock eval. Filenames only; every
// business value (PO, part, lot, carton ids) lives in the answer key.
namespace Gate.Eval;

internal static class DockEval50Files
{
    public static string EvalDir() =>
        Path.Combine(VisionFiles.RepoRoot(), "assets", "dock-eval-50");

    public static string AnswerKeyPath() => Path.Combine(EvalDir(), "answer-key.json");

    public static string ManifestPath() => Path.Combine(EvalDir(), "eval-manifest.json");

    public static string PromptPath() => Path.Combine(EvalDir(), "read-prompt.txt");

    public static string SchemaPath() => Path.Combine(EvalDir(), "read-schema.json");

    public static string BasesDir() => Path.Combine(EvalDir(), "bases");

    public static string SparkCacheDir() => Path.Combine(EvalDir(), "read-cache-spark");

    public static string AccuracyCsvPath() => Path.Combine(EvalDir(), "accuracy-by-type.csv");

    public static string ReportPath() => Path.Combine(EvalDir(), "spark-report.md");

    public static string BuildRecordPath() => Path.Combine(EvalDir(), "build-record.json");
}

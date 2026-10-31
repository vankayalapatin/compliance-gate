// Vision-owned paths for the hard-tier dock eval (30 pallet photos,
// per-carton scoring). Filenames only; every business value lives in the key.
namespace Gate.Eval;

internal static class DockEvalHardFiles
{
    public static string EvalDir() =>
        Path.Combine(VisionFiles.RepoRoot(), "assets", "dock-eval-hard");

    public static string AnswerKeyPath() => Path.Combine(EvalDir(), "answer-key.json");

    public static string ManifestPath() => Path.Combine(EvalDir(), "eval-manifest.json");

    public static string PromptPath() => Path.Combine(EvalDir(), "read-prompt.txt");

    public static string BasesDir() => Path.Combine(EvalDir(), "bases");

    public static string SparkCacheDir() => Path.Combine(EvalDir(), "read-cache-spark");

    public static string AccuracyCsvPath() => Path.Combine(EvalDir(), "accuracy-by-carton.csv");

    public static string ReportPath() => Path.Combine(EvalDir(), "spark-report.md");

    public static string BuildRecordPath() => Path.Combine(EvalDir(), "build-record.json");
}

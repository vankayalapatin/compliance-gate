// Vision-owned test support: repo-relative file locations plus small JSON and
// hashing helpers. Filenames only; every business value comes from the files.
using System.Security.Cryptography;
using System.Text.Json;

namespace Gate.Eval;

internal static class VisionFiles
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "assets"))
                && Directory.Exists(Path.Combine(dir.FullName, "scenarios")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Repo root not found: no ancestor of the test binary holds both assets/ and scenarios/.");
    }

    public static string CartonsPath() => Path.Combine(RepoRoot(), "assets", "cartons.json");

    public static string ImageSpecsPath() => Path.Combine(RepoRoot(), "assets", "image-specs.json");

    public static string ScenarioPath(string name = "demo-v1") =>
        Path.Combine(RepoRoot(), "scenarios", name + ".json");

    public static JsonDocument LoadJson(string path)
    {
        Assert.True(File.Exists(path), $"Vision file not found: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string Sha256OfFile(string path) => Sha256Hex(File.ReadAllBytes(path));
}

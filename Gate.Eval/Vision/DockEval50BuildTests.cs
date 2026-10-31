// Offline build for the 50-photo eval: composites finals from Muse-image
// bases plus the answer key, writes the manifest plus build record. No live
// calls. No business values here; everything comes from the answer key.
using System.Text.Json;

namespace Gate.Eval;

public sealed class DockEval50BuildTests
{
    [Fact]
    public void AnswerKeyHasFiftyUniquePhotos()
    {
        using var key = VisionFiles.LoadJson(DockEval50Files.AnswerKeyPath());
        var photos = key.RootElement.GetProperty("photos").EnumerateArray().ToList();
        Assert.True(photos.Count >= 50, $"Eval needs >=50 photos, found {photos.Count}.");
        var images = photos.Select(p => p.GetProperty("image").GetString()!).ToList();
        var cartons = photos.Select(p => p.GetProperty("carton").GetString()!).ToList();
        Assert.Equal(images.Count, images.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(cartons.Count, cartons.Distinct(StringComparer.Ordinal).Count());
        Assert.All(photos, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("problem").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(p.GetProperty("base").GetString()));
        });
    }

    [Fact]
    public void AnswerKeyLabelPresenceMatchesShouldRead()
    {
        using var key = VisionFiles.LoadJson(DockEval50Files.AnswerKeyPath());
        foreach (var photo in key.RootElement.GetProperty("photos").EnumerateArray())
        {
            var shouldRead = photo.GetProperty("expected").GetProperty("should_read").GetBoolean();
            var hasLabel = photo.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.Object;
            Assert.Equal(shouldRead, hasLabel);
        }
    }

    [Fact]
    public void ManualCheckFlagsOnlyCrushedAndHidden()
    {
        using var key = VisionFiles.LoadJson(DockEval50Files.AnswerKeyPath());
        var flagged = new List<string>();
        var unflaggedBad = new List<string>();
        foreach (var photo in key.RootElement.GetProperty("photos").EnumerateArray())
        {
            var problem = photo.GetProperty("problem").GetString()!;
            var flaggedNow = photo.TryGetProperty("manual_check", out var mc) && mc.ValueKind == JsonValueKind.True;
            var needsEye = string.Equals(problem, "crushed", StringComparison.Ordinal)
                || string.Equals(problem, "hidden", StringComparison.Ordinal);
            if (flaggedNow)
            {
                flagged.Add(photo.GetProperty("image").GetString()!);
            }

            if (needsEye && !flaggedNow)
            {
                unflaggedBad.Add(photo.GetProperty("image").GetString()!);
            }

            if (!needsEye && flaggedNow)
            {
                Assert.Fail($"Code-certain image flagged for manual check: {photo.GetProperty("image").GetString()}");
            }
        }

        Assert.Empty(unflaggedBad);
        Assert.True(flagged.Count >= 10, $"Need >=10 manual-check images, found {flagged.Count}.");
    }

    [Fact]
    public void LiveReaderNeverSeesAnswerKey()
    {
        var liveFile = Path.Combine(VisionFiles.RepoRoot(), "Gate.Eval", "Vision", "DockEval50SparkLiveTests.cs");
        Assert.True(File.Exists(liveFile), "Live reader source is missing.");
        var source = File.ReadAllText(liveFile);
        Assert.DoesNotContain("answer-key", source);
        Assert.DoesNotContain("AnswerKey", source);
        Assert.Contains("eval-manifest", source);
        Assert.Contains("read-prompt", source);
    }

    [Fact]
    public void BuildAllFiftyFinals()
    {
        var evalDir = DockEval50Files.EvalDir();
        var basesDir = DockEval50Files.BasesDir();
        using var key = VisionFiles.LoadJson(DockEval50Files.AnswerKeyPath());
        var photos = key.RootElement.GetProperty("photos").EnumerateArray().ToList();
        var manifest = new List<Dictionary<string, string>>();
        var byProblem = new Dictionary<string, int>(StringComparer.Ordinal);
        var byBase = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var photo in photos)
        {
            var image = photo.GetProperty("image").GetString()!;
            var baseRel = photo.GetProperty("base").GetString()!;
            var problem = photo.GetProperty("problem").GetString()!;
            var baseFile = Path.Combine(evalDir, baseRel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(baseFile), $"Base missing for {image}: {baseRel}. Generate the 10 scene bases first.");
            var finalBytes = DockEval50Compositor.BuildFinal(File.ReadAllBytes(baseFile), photo, image);
            var outFile = Path.Combine(evalDir, image);
            File.WriteAllBytes(outFile, finalBytes);
            using var bitmap = SkiaSharp.SKBitmap.Decode(outFile);
            Assert.NotNull(bitmap);
            Assert.True(bitmap!.Width > 0 && bitmap.Height > 0);
            var sha = VisionFiles.Sha256OfFile(outFile);
            manifest.Add(new Dictionary<string, string> { ["image"] = image, ["sha256"] = sha, ["base"] = baseRel });
            byProblem[problem] = byProblem.TryGetValue(problem, out var n) ? n + 1 : 1;
            byBase[baseRel] = byBase.TryGetValue(baseRel, out var b) ? b + 1 : 1;
        }

        // Manifest carries filenames plus hashes only: no expected values, so
        // any model (Spark, Glimmer) can read the same finals later.
        var promptVersion = key.RootElement.GetProperty("prompt_version").GetString()!;
        File.WriteAllText(DockEval50Files.ManifestPath(), JsonSerializer.Serialize(
            new { eval_id = "dock-eval-50", prompt_version = promptVersion, images = manifest },
            new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(DockEval50Files.BuildRecordPath(), JsonSerializer.Serialize(
            new
            {
                eval_id = "dock-eval-50",
                labeler = "code-compositor-v1",
                scene_bases = 10,
                finals = photos.Count,
                by_problem = byProblem,
                by_base = byBase
            },
            new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(photos.Count >= 50, "Eval needs >=50 finals.");
    }
}

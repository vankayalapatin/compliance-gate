// Offline build for the hard tier: composites 30 pallet finals from the 6
// Muse bases plus the oracle, writes the manifest plus build record. No live
// calls. No business values here; everything comes from the answer key.
using System.Text.Json;

namespace Gate.Eval;

public sealed class DockEvalHardBuildTests
{
    [Fact]
    public void AnswerKeyHasThirtyPhotosWithFiveOrSixCartons()
    {
        using var key = VisionFiles.LoadJson(DockEvalHardFiles.AnswerKeyPath());
        var photos = key.RootElement.GetProperty("photos").EnumerateArray().ToList();
        Assert.True(photos.Count >= 30, $"Hard tier needs >=30 photos, found {photos.Count}.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var photo in photos)
        {
            var cartons = photo.GetProperty("cartons").EnumerateArray().ToList();
            Assert.True(cartons.Count is 5 or 6,
                $"{photo.GetProperty("image").GetString()} has {cartons.Count} cartons, want 5 or 6.");
            foreach (var carton in cartons)
            {
                Assert.True(ids.Add(carton.GetProperty("carton").GetString()!), "Duplicate carton id.");
                var label = carton.GetProperty("label");
                Assert.Equal(JsonValueKind.Number, label.GetProperty("cx").ValueKind);
                Assert.Equal(JsonValueKind.Number, label.GetProperty("cy").ValueKind);
            }
        }
    }

    [Fact]
    public void EachPhotoCarriesExactlyTwoProblems()
    {
        using var key = VisionFiles.LoadJson(DockEvalHardFiles.AnswerKeyPath());
        foreach (var photo in key.RootElement.GetProperty("photos").EnumerateArray())
        {
            var bad = photo.GetProperty("cartons").EnumerateArray()
                .Count(c => !string.Equals(c.GetProperty("problem").GetString(), "clean", StringComparison.Ordinal));
            Assert.Equal(2, bad);
        }
    }

    [Fact]
    public void DentCartonsSitOnPhotographicDents()
    {
        using var key = VisionFiles.LoadJson(DockEvalHardFiles.AnswerKeyPath());
        var dentPhotos = 0;
        foreach (var photo in key.RootElement.GetProperty("photos").EnumerateArray())
        {
            var flagged = photo.TryGetProperty("manual_check", out var mc) && mc.ValueKind == JsonValueKind.True;
            var dents = photo.GetProperty("cartons").EnumerateArray()
                .Where(c => string.Equals(c.GetProperty("problem").GetString(), "subtle_dent", StringComparison.Ordinal))
                .ToList();
            if (!flagged)
            {
                Assert.Empty(dents);
                continue;
            }

            dentPhotos++;
            Assert.Single(dents);
            Assert.True(photo.TryGetProperty("dent_slot", out var slot)
                && string.Equals(dents[0].GetProperty("slot").GetString(), slot.GetString(), StringComparison.Ordinal));
        }

        Assert.True(dentPhotos >= 10, $"Need >=10 dent photos, found {dentPhotos}.");
    }

    [Fact]
    public void LiveReaderNeverSeesAnswerKey()
    {
        var liveFile = Path.Combine(VisionFiles.RepoRoot(), "Gate.Eval", "Vision", "DockEvalHardSparkLiveTests.cs");
        Assert.True(File.Exists(liveFile), "Live reader source is missing.");
        var source = File.ReadAllText(liveFile);
        Assert.DoesNotContain("answer-key", source);
        Assert.DoesNotContain("AnswerKey", source);
        Assert.Contains("eval-manifest", source);
        Assert.Contains("read-prompt", source);
    }

    [Fact]
    public void BuildAllThirtyFinals()
    {
        var evalDir = DockEvalHardFiles.EvalDir();
        using var key = VisionFiles.LoadJson(DockEvalHardFiles.AnswerKeyPath());
        var photos = key.RootElement.GetProperty("photos").EnumerateArray().ToList();
        var manifest = new List<Dictionary<string, string>>();
        var byProblem = new Dictionary<string, int>(StringComparer.Ordinal);
        var byBase = new Dictionary<string, int>(StringComparer.Ordinal);
        var byEffect = new Dictionary<string, int>(StringComparer.Ordinal);
        var cartons = 0;

        foreach (var photo in photos)
        {
            var image = photo.GetProperty("image").GetString()!;
            var baseRel = photo.GetProperty("base").GetString()!;
            var baseFile = Path.Combine(evalDir, baseRel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(baseFile), $"Base missing for {image}: {baseRel}.");
            var (finalBytes, effect) = DockEvalHardCompositor.BuildFinal(File.ReadAllBytes(baseFile), photo, image);
            var outFile = Path.Combine(evalDir, image);
            File.WriteAllBytes(outFile, finalBytes);
            using var bitmap = SkiaSharp.SKBitmap.Decode(outFile);
            Assert.NotNull(bitmap);
            Assert.True(bitmap!.Width > 0 && bitmap.Height > 0);
            var sha = VisionFiles.Sha256OfFile(outFile);
            manifest.Add(new Dictionary<string, string> { ["image"] = image, ["sha256"] = sha, ["base"] = baseRel });
            foreach (var carton in photo.GetProperty("cartons").EnumerateArray())
            {
                var problem = carton.GetProperty("problem").GetString()!;
                byProblem[problem] = byProblem.TryGetValue(problem, out var n) ? n + 1 : 1;
                cartons++;
            }

            byBase[baseRel] = byBase.TryGetValue(baseRel, out var b) ? b + 1 : 1;
            byEffect[effect] = byEffect.TryGetValue(effect, out var e) ? e + 1 : 1;
        }

        var promptVersion = key.RootElement.GetProperty("prompt_version").GetString()!;
        File.WriteAllText(DockEvalHardFiles.ManifestPath(), JsonSerializer.Serialize(
            new { eval_id = "dock-eval-hard", prompt_version = promptVersion, images = manifest },
            new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(DockEvalHardFiles.BuildRecordPath(), JsonSerializer.Serialize(
            new
            {
                eval_id = "dock-eval-hard",
                labeler = "code-compositor-hard-v1",
                scene_bases = 6,
                finals = photos.Count,
                cartons,
                by_problem = byProblem,
                by_base = byBase,
                by_effect = byEffect
            },
            new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(photos.Count >= 30, "Hard tier needs >=30 finals.");
    }
}

// Vision ground-truth checks: structural invariants over the vision-owned
// seed files, cross-checked against the scenario file. Logic only; every
// carton id, count, and flag is read from data, never a literal.
using System.Text.Json;

namespace Gate.Eval;

public sealed class VisionGroundTruthTests
{
    private static JsonElement Cartons() =>
        VisionFiles.LoadJson(VisionFiles.CartonsPath()).RootElement.GetProperty("cartons");

    private static JsonElement Specs() =>
        VisionFiles.LoadJson(VisionFiles.ImageSpecsPath()).RootElement.GetProperty("images");

    private static JsonElement ScenarioCartons() =>
        VisionFiles.LoadJson(VisionFiles.ScenarioPath()).RootElement.GetProperty("cartons");

    private static string Id(JsonElement row) => row.GetProperty("id").GetString()!;

    private static bool IsHidden(JsonElement row) =>
        row.GetProperty("damage").GetString()!
            .Contains("hidden", StringComparison.OrdinalIgnoreCase);

    private static bool IsDamaged(JsonElement row)
    {
        var damage = row.GetProperty("damage").GetString()!;
        return !string.Equals(damage, "none", StringComparison.OrdinalIgnoreCase) && !IsHidden(row);
    }

    [Fact]
    public void CartonFileHasTwelveUniqueCartons()
    {
        var rows = Cartons().EnumerateArray().ToList();
        Assert.Equal(12, rows.Count);
        Assert.Equal(12, rows.Select(Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rows, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("part").GetString()));
            Assert.True(row.GetProperty("units").GetInt32() > 0);
        });
    }

    [Fact]
    public void SeededFailureRolesAreConsistent()
    {
        var rows = Cartons().EnumerateArray().ToList();
        var missingMark = rows.Where(row => !row.GetProperty("markPresent").GetBoolean()).ToList();
        var damaged = rows.Where(IsDamaged).ToList();
        var hidden = rows.Where(IsHidden).ToList();
        Assert.Single(missingMark);
        Assert.Single(damaged);
        Assert.Single(hidden);

        // The reshot target is exactly the missing-mark carton.
        var reshot = rows.Where(row => row.TryGetProperty("reshotIn", out _)).ToList();
        Assert.Single(reshot);
        Assert.Equal(Id(missingMark[0]), Id(reshot[0]));

        // The hidden carton is legible in no image; every other visibleIn
        // reference names a pallet-kind spec id.
        Assert.Empty(hidden[0].GetProperty("visibleIn").EnumerateArray());
        var palletIds = new HashSet<string>(Specs().EnumerateArray()
            .Where(spec => spec.GetProperty("kind").GetString() == "pallet")
            .Select(spec => spec.GetProperty("id").GetString()!), StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var seen in row.GetProperty("visibleIn").EnumerateArray())
            {
                Assert.Contains(seen.GetString()!, palletIds);
            }
        }
    }

    [Fact]
    public void CartonIdsAndUnitsMatchScenarioFile()
    {
        var vision = Cartons().EnumerateArray()
            .ToDictionary(Id, row => row.GetProperty("units").GetInt32(), StringComparer.Ordinal);
        var scenario = ScenarioCartons().EnumerateArray()
            .ToDictionary(Id, row => row.GetProperty("units").GetInt32(), StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(scenario.Keys, StringComparer.Ordinal),
            new HashSet<string>(vision.Keys, StringComparer.Ordinal));
        foreach (var (id, units) in scenario)
        {
            Assert.Equal(units, vision[id]);
        }
    }

    [Fact]
    public void ImageSpecsHaveFourViewsWithOneReshot()
    {
        var specs = Specs().EnumerateArray().ToList();
        Assert.Equal(4, specs.Count);
        var ids = specs.Select(spec => spec.GetProperty("id").GetString()!).ToList();
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());

        var reshots = specs.Where(spec => spec.GetProperty("kind").GetString() == "reshot").ToList();
        var pallets = specs.Where(spec => spec.GetProperty("kind").GetString() == "pallet").ToList();
        Assert.Single(reshots);
        Assert.Equal(3, pallets.Count);
        Assert.All(pallets, spec =>
            Assert.True(spec.GetProperty("clearlyVisible").EnumerateArray().Count() >= 2));

        // The reshot shows only the missing-mark carton (id read from data).
        var missingMarkId = Id(Cartons().EnumerateArray()
            .Single(row => !row.GetProperty("markPresent").GetBoolean()));
        var reshotVisible = reshots[0].GetProperty("clearlyVisible").EnumerateArray()
            .Select(element => element.GetString()!).ToList();
        Assert.Equal([missingMarkId], reshotVisible);
    }

    [Fact]
    public void SpecVisibilityMatchesScenarioFile()
    {
        // Invert the scenario's visibleIn arrays and compare per pallet view.
        var byImage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in ScenarioCartons().EnumerateArray())
        {
            foreach (var seen in row.GetProperty("visibleIn").EnumerateArray())
            {
                var image = seen.GetString()!;
                byImage.TryAdd(image, new HashSet<string>(StringComparer.Ordinal));
                byImage[image].Add(Id(row));
            }
        }

        foreach (var spec in Specs().EnumerateArray())
        {
            if (spec.GetProperty("kind").GetString() != "pallet")
            {
                continue;
            }

            var id = spec.GetProperty("id").GetString()!;
            var expected = byImage.TryGetValue(id, out var set)
                ? set
                : new HashSet<string>(StringComparer.Ordinal);
            var actual = new HashSet<string>(spec.GetProperty("clearlyVisible").EnumerateArray()
                .Select(element => element.GetString()!), StringComparer.Ordinal);
            Assert.Equal(expected, actual);
        }
    }
}

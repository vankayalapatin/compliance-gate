// Case-cache round-trip: byte-exact shared convention (one file per case
// named <CaseId>.json via JsonSerializerDefaults.Web). No live calls.
using System.Text.Json;
using Gate.Web.Services;

namespace Gate.Eval.Ui;

public sealed class UiCacheRoundTripTests
{
    [Fact]
    public async Task WriteThenReadRoundTrips()
    {
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        var file = UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1", [
            UiTestSupport.PendingDecision("D-1", "CASE-1"),
        ]);

        await store.WriteAsync(file);
        var reread = await store.ReadAsync("CASE-1");

        Assert.NotNull(reread);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.Equal(
            JsonSerializer.Serialize(file, web),
            JsonSerializer.Serialize(reread, web));
    }

    [Fact]
    public async Task WriteIsByteExactWebDefaults()
    {
        var dir = UiTestSupport.NewDir("gate-ui-cache");
        var store = new CaseCacheStore(dir);
        var file = UiTestSupport.CaseFileWith("CASE-9", "SO-9", "ITEM-9");

        await store.WriteAsync(file);

        var path = Path.Combine(dir, "CASE-9.json");
        Assert.True(File.Exists(path));
        var expected = JsonSerializer.Serialize(file, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ListIsSortedAndMissingReadsNull()
    {
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-B", "SO-B", "ITEM-B"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-A", "SO-A", "ITEM-A"));

        var listed = await store.ListAsync();

        Assert.Equal(2, listed.Count);
        Assert.Equal("CASE-A", listed[0].Case.CaseId);
        Assert.Equal("CASE-B", listed[1].Case.CaseId);
        Assert.Null(await store.ReadAsync("CASE-MISSING"));
    }

    [Fact]
    public async Task ClearRemovesEveryFile()
    {
        var store = new CaseCacheStore(UiTestSupport.NewDir("gate-ui-cache"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-1", "SO-1", "ITEM-1"));
        await store.WriteAsync(UiTestSupport.CaseFileWith("CASE-2", "SO-2", "ITEM-2"));

        var cleared = await store.ClearAsync();

        Assert.Equal(2, cleared);
        Assert.Empty(await store.ListAsync());
        Assert.Equal(0, await store.ClearAsync());
    }
}

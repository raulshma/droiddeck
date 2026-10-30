using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Covers the ordering persistence the host uses for plugin UI surfaces:
/// round-trips, stable restart ordering, appended new items, and silent
/// pruning of identifiers that no longer exist.
/// </summary>
public sealed class PluginUiOrderStoreTests
{
    [Fact]
    public async Task SaveThenLoadRoundTripsTheSurfaceSequence()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ui-order.json");
        var store = new PluginUiOrderStore(path);

        await store.SaveAsync(PluginUiOrderStore.ToolbarSurface, ["beta.action", "alpha.action"]);
        var reloaded = new PluginUiOrderStore(path);

        Assert.Equal(
            ["beta.action", "alpha.action"],
            reloaded.Load(PluginUiOrderStore.ToolbarSurface));
        Assert.Empty(reloaded.Load(PluginUiOrderStore.StatusBarSurface));
    }

    [Fact]
    public async Task SortPutsPersistedItemsFirstAndAppendsUnknownsInTheirIncomingOrder()
    {
        using var temp = new TempDirectory();
        var store = new PluginUiOrderStore(Path.Combine(temp.Path, "ui-order.json"));
        await store.SaveAsync(PluginUiOrderStore.StatusBarSurface, ["c", "a"]);

        var sorted = store.Sort(
            PluginUiOrderStore.StatusBarSurface,
            ["a", "b", "c", "d"],
            static id => id);

        Assert.Equal(["c", "a", "b", "d"], sorted);
    }

    [Fact]
    public async Task SortWithoutPersistedOrderKeepsTheIncomingOrder()
    {
        using var temp = new TempDirectory();
        var store = new PluginUiOrderStore(Path.Combine(temp.Path, "ui-order.json"));

        var sorted = store.Sort(
            PluginUiOrderStore.ToolbarSurface,
            ["first", "second"],
            static id => id);

        Assert.Equal(["first", "second"], sorted);
    }

    [Fact]
    public async Task SavingTheCurrentSequencePrunesUnknownIdentifiersSilently()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ui-order.json");
        var store = new PluginUiOrderStore(path);
        await store.SaveAsync(PluginUiOrderStore.CommandsSurface, ["one", "two", "three"]);

        await store.SaveAsync(PluginUiOrderStore.CommandsSurface, ["three", "one"]);

        Assert.Equal(["three", "one"], new PluginUiOrderStore(path).Load(PluginUiOrderStore.CommandsSurface));
    }

    [Fact]
    public async Task SurfacesAreIndependent()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ui-order.json");
        var store = new PluginUiOrderStore(path);
        await store.SaveAsync(PluginUiOrderStore.ToolbarSurface, ["t1"]);
        await store.SaveAsync(PluginUiOrderStore.StatusBarSurface, ["s1", "s2"]);

        var reloaded = new PluginUiOrderStore(path);
        Assert.Equal(["t1"], reloaded.Load(PluginUiOrderStore.ToolbarSurface));
        Assert.Equal(["s1", "s2"], reloaded.Load(PluginUiOrderStore.StatusBarSurface));
    }

    [Fact]
    public void MissingOrCorruptFilesYieldAnEmptyStore()
    {
        using var temp = new TempDirectory();
        var missing = new PluginUiOrderStore(Path.Combine(temp.Path, "missing.json"));
        Assert.Empty(missing.Load(PluginUiOrderStore.ToolbarSurface));

        var corruptPath = Path.Combine(temp.Path, "ui-order.json");
        File.WriteAllText(corruptPath, "{ not json");
        var corrupt = new PluginUiOrderStore(corruptPath);

        Assert.Empty(corrupt.Load(PluginUiOrderStore.ToolbarSurface));
    }

    [Fact]
    public async Task LoadRejectsUnknownArguments()
    {
        var store = new PluginUiOrderStore("ui-order.json");

        Assert.Throws<ArgumentException>(() => store.Load(""));
        Assert.Throws<ArgumentException>(() => store.Sort(" ", ["a"], static id => id));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync("", ["a"]));
    }

    [Fact]
    public async Task ConcurrentSavesLandEverySurfaceInTheFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "ui-order.json");
        var store = new PluginUiOrderStore(path);
        var surfaces = new[]
        {
            PluginUiOrderStore.ToolbarSurface,
            PluginUiOrderStore.StatusBarSurface,
            PluginUiOrderStore.CommandsSurface,
        };

        // Each save must run whole: snapshot and write serialized, so no save
        // can interleave with another and erase a surface that was already
        // written.
        var saves = surfaces
            .SelectMany(surface => Enumerable.Range(0, 8), (surface, index) => (surface, index))
            .Select(save => store.SaveAsync(
                save.surface,
                [$"{save.surface}.a", $"{save.surface}.b{save.index}"]))
            .ToArray();
        await Task.WhenAll(saves);

        var reloaded = new PluginUiOrderStore(path);
        foreach (var surface in surfaces)
        {
            var persisted = reloaded.Load(surface);
            Assert.Equal(2, persisted.Count);
            Assert.Equal($"{surface}.a", persisted[0]);
        }
    }
}

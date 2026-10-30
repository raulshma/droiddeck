using DroidDeck.Plugins.Catalog;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests.Catalog;

public sealed class CatalogSourceStoreTests
{
    [Fact]
    public async Task AddPersistsAnHttpsSourceAcrossReloads()
    {
        using var temp = new TempDirectory();

        var store = await CatalogSourceStore.LoadAsync(temp.Combine("catalog"));
        var result = await store.AddAsync("Example feed", "https://example.com/catalog.json");

        Assert.True(result.Succeeded, result.Error);
        var source = Assert.IsType<CatalogSource>(result.Source);
        Assert.Matches("^src-[0-9a-f]{12}$", source.Id);
        Assert.Equal("Example feed", source.Name);
        Assert.True(source.Enabled);
        Assert.True(File.Exists(store.FilePath));

        var reloaded = await CatalogSourceStore.LoadAsync(temp.Combine("catalog"));
        Assert.Equal(source.Id, Assert.Single(reloaded.Sources).Id);
    }

    [Fact]
    public async Task AddRejectsPlainHttpWithAClearError()
    {
        using var temp = new TempDirectory();
        var store = await CatalogSourceStore.LoadAsync(temp.Combine("catalog"));

        var result = await store.AddAsync("Insecure", "http://example.com/catalog.json");

        Assert.False(result.Succeeded);
        Assert.Contains("HTTPS", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.Sources);
    }

    [Fact]
    public async Task AddRejectsAMissingLocalPath()
    {
        using var temp = new TempDirectory();
        var store = await CatalogSourceStore.LoadAsync(temp.Combine("catalog"));
        var missing = temp.Combine("missing-catalog.json");

        var result = await store.AddAsync("Local", missing);

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddAcceptsAnExistingLocalCatalogFile()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("team/catalog.json", CatalogDocumentTests.ValidCatalogJson);
        var store = await CatalogSourceStore.LoadAsync(temp.Combine("catalog"));

        var result = await store.AddAsync("Team feed", file);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.Source!.IsLocalPath);
    }

    [Fact]
    public async Task AddingTheSameUrlTwiceIsRejected()
    {
        using var temp = new TempDirectory();
        var store = await CatalogSourceStore.LoadAsync(temp.Combine("catalog"));
        await store.AddAsync("First", "https://example.com/catalog.json");

        var duplicate = await store.AddAsync("Second", "https://EXAMPLE.com/catalog.json");

        Assert.False(duplicate.Succeeded);
        Assert.Contains("already", duplicate.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(store.Sources);
    }

    [Fact]
    public async Task EnableAndRemovePersist()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("catalog");
        var store = await CatalogSourceStore.LoadAsync(root);
        var source = (await store.AddAsync("Example", "https://example.com/catalog.json")).Source!;

        Assert.True(await store.SetEnabledAsync(source.Id, enabled: false));
        var reloaded = await CatalogSourceStore.LoadAsync(root);
        Assert.False(Assert.Single(reloaded.Sources).Enabled);

        Assert.True(await reloaded.RemoveAsync(source.Id));
        var removed = await CatalogSourceStore.LoadAsync(root);
        Assert.Empty(removed.Sources);
    }

    [Fact]
    public async Task ACorruptSourcesFileFallsBackToNoSources()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("catalog");
        await File.WriteAllTextAsync(Path.Combine(root, CatalogSourceStore.FileName), "{ not json");

        var store = await CatalogSourceStore.LoadAsync(root);

        Assert.Empty(store.Sources);
    }
}

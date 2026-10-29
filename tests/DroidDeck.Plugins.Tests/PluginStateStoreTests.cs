using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginStateStoreTests
{
    [Fact]
    public async Task LoadReturnsAnEmptyStoreWhenNoStateFileExists()
    {
        using var temp = new TempDirectory();

        var store = await PluginStateStore.LoadAsync(temp.Combine("plugins"));

        Assert.Empty(store.DisabledIds);
        Assert.False(store.IsDisabled("com.example.sample"));
    }

    [Fact]
    public async Task SetDisabledPersistsAcrossReloads()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");

        var store = await PluginStateStore.LoadAsync(root);
        await store.SetDisabledAsync("com.example.sample", disabled: true);
        var reloaded = await PluginStateStore.LoadAsync(root);

        Assert.True(reloaded.IsDisabled("com.example.sample"));
        Assert.Equal(["com.example.sample"], reloaded.DisabledIds);
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task EnableRemovesThePersistedDisabledState()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        var store = await PluginStateStore.LoadAsync(root);
        await store.SetDisabledAsync("com.example.sample", disabled: true);

        await store.SetDisabledAsync("com.example.sample", disabled: false);
        var reloaded = await PluginStateStore.LoadAsync(root);

        Assert.False(reloaded.IsDisabled("com.example.sample"));
        Assert.Empty(reloaded.DisabledIds);
    }

    [Fact]
    public async Task OtherPluginsKeepTheirStateWhenOneChanges()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        var store = await PluginStateStore.LoadAsync(root);
        await store.SetDisabledAsync("com.example.first", disabled: true);

        await store.SetDisabledAsync("com.example.second", disabled: true);
        await store.SetDisabledAsync("com.example.first", disabled: false);
        var reloaded = await PluginStateStore.LoadAsync(root);

        Assert.False(reloaded.IsDisabled("com.example.first"));
        Assert.True(reloaded.IsDisabled("com.example.second"));
    }

    [Fact]
    public async Task ACorruptStateFileFallsBackToAnEmptyStore()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("plugins");
        await File.WriteAllTextAsync(
            Path.Combine(root, PluginStateStore.FileName),
            "{ this is not json");

        var store = await PluginStateStore.LoadAsync(root);

        Assert.Empty(store.DisabledIds);
    }

    [Fact]
    public async Task DisabledIdsAreCaseInsensitive()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        var store = await PluginStateStore.LoadAsync(root);

        await store.SetDisabledAsync("Com.Example.Sample", disabled: true);

        Assert.True(store.IsDisabled("com.example.sample"));
    }
}

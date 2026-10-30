using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginTrustStoreTests
{
    private const string PluginId = "com.example.sample";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task AnUnknownPluginNeedsApproval()
    {
        using var temp = new TempDirectory();
        var store = await PluginTrustStore.LoadAsync(temp.Combine("plugins"));

        Assert.Empty(store.Entries);
        Assert.Null(store.Find(PluginId));
        Assert.Equal(PluginTrustDecision.Unknown, store.Evaluate(PluginId, Hash));
    }

    [Fact]
    public async Task ApprovalPersistsThePinnedHash()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        var store = await PluginTrustStore.LoadAsync(root);

        await store.ApproveAsync(PluginId, Hash, "Sample Plugin", "1.0.0");

        var reloaded = await PluginTrustStore.LoadAsync(root);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal(PluginId, entry.PluginId);
        Assert.Equal(Hash, entry.ContentHash);
        Assert.Equal("Sample Plugin", entry.Name);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal(PluginTrustDecision.Trusted, reloaded.Evaluate(PluginId, Hash));
        Assert.Equal(PluginTrustDecision.Changed, reloaded.Evaluate(PluginId, new string('a', 64)));
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public async Task ApprovingAgainReplacesThePinnedHash()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        var store = await PluginTrustStore.LoadAsync(root);
        await store.ApproveAsync(PluginId, Hash, "Sample Plugin", "1.0.0");

        var newHash = new string('b', 64);
        await store.ApproveAsync(PluginId, newHash, "Sample Plugin", "1.1.0");

        var reloaded = await PluginTrustStore.LoadAsync(root);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal(newHash, entry.ContentHash);
        Assert.Equal("1.1.0", entry.Version);
        Assert.Equal(PluginTrustDecision.Trusted, reloaded.Evaluate(PluginId, newHash));
        Assert.Equal(PluginTrustDecision.Changed, reloaded.Evaluate(PluginId, Hash));
    }

    [Fact]
    public async Task HashesAndIdentifiersAreCaseInsensitive()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        var store = await PluginTrustStore.LoadAsync(root);
        await store.ApproveAsync(PluginId, Hash);

        Assert.Equal(PluginTrustDecision.Trusted, store.Evaluate("COM.EXAMPLE.SAMPLE", Hash.ToUpperInvariant()));
        Assert.NotNull(store.Find("COM.EXAMPLE.SAMPLE"));
    }

    [Fact]
    public async Task ACorruptTrustFileFallsBackToUnknown()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("plugins");
        await File.WriteAllTextAsync(Path.Combine(root, PluginTrustStore.FileName), "{ not json");

        var store = await PluginTrustStore.LoadAsync(root);

        Assert.Empty(store.Entries);
        Assert.Equal(PluginTrustDecision.Unknown, store.Evaluate(PluginId, Hash));
    }
}

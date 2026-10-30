using System.Text.Json;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginTrustEnforcementTests
{
    private const string PluginId = "com.example.sample";

    [Fact]
    public async Task UntrustedPluginIsBlockedUntilApproved()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        StagePlugin(temp);
        var manager = CreateManager(out var registry);

        await manager.InitializeAsync(root);

        var status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.TrustRequired, status.State);
        Assert.Equal("Approval required", status.StatusLabel);
        Assert.Contains("approved", status.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(status.CanTrust);
        Assert.False(status.CanEnable);
        Assert.False(status.CanDisable);
        Assert.NotNull(status.ContentHash);
        Assert.Null(status.ApprovedHash);
        Assert.Empty(registry.Commands);

        Assert.True(await manager.TrustAsync(PluginId));

        status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.EnabledPendingRestart, status.State);
        Assert.Contains("Restart", status.Reason, StringComparison.Ordinal);
        Assert.True(manager.RestartRequired);
        Assert.Empty(registry.Commands);

        var trust = await PluginTrustStore.LoadAsync(root);
        Assert.Equal(PluginTrustDecision.Trusted, trust.Evaluate(PluginId, status.ContentHash!));

        var restarted = CreateManager(out var restartedRegistry);
        await restarted.InitializeAsync(root);

        var loaded = Assert.Single(restarted.Statuses);
        Assert.Equal(PluginState.Loaded, loaded.State);
        Assert.False(restarted.RestartRequired);
        Assert.Single(restartedRegistry.Commands);
    }

    [Fact]
    public async Task ChangedPluginContentRequiresANewTrustDecision()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        var directory = StagePlugin(temp);
        var trust = await PluginTrustStore.LoadAsync(root);
        await trust.ApproveAsync(
            PluginId,
            await PluginContentHasher.ComputeAsync(directory),
            "Sample Plugin",
            "1.0.0");

        var manager = CreateManager(out var registry);
        await manager.InitializeAsync(root);
        Assert.Equal(PluginState.Loaded, Assert.Single(manager.Statuses).State);
        await manager.DisposeAsync();

        await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), "new contents");
        var changed = CreateManager(out var changedRegistry);
        await changed.InitializeAsync(root);

        var status = Assert.Single(changed.Statuses);
        Assert.Equal(PluginState.TrustRequired, status.State);
        Assert.Contains("changed", status.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(status.ContentHash);
        Assert.NotNull(status.ApprovedHash);
        Assert.NotEqual(status.ContentHash, status.ApprovedHash);
        Assert.True(status.CanTrust);
        Assert.Empty(changedRegistry.Commands);

        Assert.True(await changed.TrustAsync(PluginId));

        var reloaded = await PluginTrustStore.LoadAsync(root);
        Assert.Equal(
            PluginTrustDecision.Trusted,
            reloaded.Evaluate(PluginId, status.ContentHash!));
    }

    [Fact]
    public async Task ADisabledPluginIsNotBlockedByTrust()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        StagePlugin(temp);
        var store = await PluginStateStore.LoadAsync(root);
        await store.SetDisabledAsync(PluginId, disabled: true);
        var manager = CreateManager(out var registry);

        await manager.InitializeAsync(root);

        var status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.Disabled, status.State);
        Assert.False(status.CanTrust);
        Assert.Empty(registry.Commands);
    }

    [Fact]
    public async Task TheTrustAllPolicyLoadsPluginsWithoutApproval()
    {
        using var temp = new TempDirectory();
        StagePlugin(temp);
        var manager = CreateManager(out var registry, PluginTrustPolicy.TrustAll);

        await manager.InitializeAsync(temp.Combine("plugins"));

        Assert.Equal(PluginState.Loaded, Assert.Single(manager.Statuses).State);
        Assert.Single(registry.Commands);
        Assert.Null(Assert.Single(manager.Statuses).ContentHash);
    }

    [Fact]
    public async Task DisablingWithAReasonExplainsItOnTheNextLaunch()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        StagePlugin(temp);
        var manager = CreateManager(out _, PluginTrustPolicy.TrustAll);
        await manager.InitializeAsync(root);

        const string reason = "Disabled after 3 consecutive crashes. Last error: boom.";
        Assert.True(await manager.DisableAsync(PluginId, reason));
        Assert.Equal(reason, Assert.Single(manager.Statuses).Reason);
        await manager.DisposeAsync();

        var restarted = CreateManager(out var registry, PluginTrustPolicy.TrustAll);
        await restarted.InitializeAsync(root);

        var status = Assert.Single(restarted.Statuses);
        Assert.Equal(PluginState.Disabled, status.State);
        Assert.Equal(reason, status.Reason);
        Assert.Empty(registry.Commands);
    }

    private static PluginManager CreateManager(
        out PluginRegistry registry,
        PluginTrustPolicy trustPolicy = PluginTrustPolicy.RequireApproval)
    {
        registry = new PluginRegistry();
        var loader = new PluginLoader(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");
        return new PluginManager(loader, new PluginHealthTracker(), trustPolicy: trustPolicy);
    }

    private static string StagePlugin(TempDirectory temp)
    {
        var manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "Sample Plugin",
            Version = "1.0.0",
            ApiVersion = "1.0",
            MinHostVersion = "0.1.0",
            EntryAssembly = "SampleCommandPlugin.dll",
            EntryType = "SampleCommandPlugin.SamplePlugin",
            Capabilities = ["command"],
        };
        var directory = temp.CreateDirectory("plugins", PluginId);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "SampleCommandPlugin.dll"),
            Path.Combine(directory, "SampleCommandPlugin.dll"),
            overwrite: true);
        File.WriteAllText(
            Path.Combine(directory, PluginManifestReader.FileName),
            JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        return directory;
    }
}

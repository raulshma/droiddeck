using System.Text.Json;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginManagerTests
{
    private const string SamplePluginId = "com.example.sample";
    private const string GoodPluginId = "com.example.good";
    private const string FaultyPluginId = "com.example.faulty";
    private const string ThrowingPluginId = "com.example.throwing";

    [Fact]
    public async Task ALoadedPluginShowsAnEnabledStatusWithDuration()
    {
        using var temp = new TempDirectory();
        StagePlugin(temp, CreateManifest());
        var manager = CreateManager(temp, out var registry);

        await manager.InitializeAsync(temp.Combine("plugins"));

        var status = Assert.Single(manager.Statuses);
        Assert.Equal(SamplePluginId, status.Id);
        Assert.Equal("Sample Plugin", status.Name);
        Assert.Equal("1.0.0", status.Version);
        Assert.Equal("1.0", status.ApiVersion);
        Assert.Equal(PluginState.Loaded, status.State);
        Assert.Null(status.Reason);
        Assert.True(status.LoadDuration > TimeSpan.Zero);
        Assert.False(manager.RestartRequired);
        Assert.True(status.CanDisable);
        Assert.False(status.CanEnable);
        Assert.Equal("Enabled", status.StatusLabel);
        Assert.Single(registry.Commands);
    }

    [Fact]
    public async Task AnIncompatiblePluginIsRejectedWithAClearReasonInTheManager()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest(FaultyPluginId);
        manifest.ApiVersion = "99.0";
        StagePlugin(temp, manifest);
        var manager = CreateManager(temp, out var registry);

        await manager.InitializeAsync(temp.Combine("plugins"));

        var status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.Incompatible, status.State);
        Assert.Equal("Incompatible", status.StatusLabel);
        Assert.NotNull(status.Reason);
        Assert.Contains("99.0", status.Reason, StringComparison.Ordinal);
        Assert.Contains("1.0", status.Reason, StringComparison.Ordinal);
        Assert.False(status.CanDisable);
        Assert.Empty(registry.Commands);
    }

    [Fact]
    public async Task AHostVersionMismatchIsReportedAsIncompatible()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest(FaultyPluginId);
        manifest.MinHostVersion = "2.0.0";
        StagePlugin(temp, manifest);
        var manager = CreateManager(temp, out _);

        await manager.InitializeAsync(temp.Combine("plugins"));

        var status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.Incompatible, status.State);
        Assert.Contains("2.0.0", status.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableManifestShowsAsDisabledByError()
    {
        using var temp = new TempDirectory();
        var root = temp.CreateDirectory("plugins");
        var directory = temp.CreateDirectory("plugins", "broken-plugin");
        await File.WriteAllTextAsync(
            Path.Combine(directory, PluginManifestReader.FileName),
            "{ this is not json");
        var manager = CreateManager(temp, out _);

        await manager.InitializeAsync(root);

        var status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.DisabledByError, status.State);
        Assert.Equal("Disabled by error", status.StatusLabel);
        Assert.Contains("plugin.json", status.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APluginFaultNeverBreaksTheHostOrOtherPlugins()
    {
        using var temp = new TempDirectory();
        StagePlugin(temp, CreateManifest(GoodPluginId, "SampleCommandPlugin.SamplePlugin", idFolder: GoodPluginId));
        StagePlugin(
            temp,
            CreateManifest(ThrowingPluginId, "SampleCommandPlugin.ThrowingPlugin", idFolder: ThrowingPluginId));
        var manager = CreateManager(temp, out var registry, out var health);

        await manager.InitializeAsync(temp.Combine("plugins"));

        Assert.Equal(2, manager.Statuses.Count);
        var faulty = Assert.Single(manager.Statuses, status => status.Id == ThrowingPluginId);
        Assert.Equal(PluginState.DisabledByError, faulty.State);
        Assert.Contains("always fails to initialize", faulty.Reason, StringComparison.Ordinal);
        var healthy = Assert.Single(manager.Statuses, status => status.Id == GoodPluginId);
        Assert.Equal(PluginState.Loaded, healthy.State);
        Assert.False(health.Get(ThrowingPluginId).IsHealthy);
        Assert.Contains("always fails to initialize", health.Get(ThrowingPluginId).LastError, StringComparison.Ordinal);
        Assert.True(health.Get(GoodPluginId).IsHealthy);

        // The host still works: the good plugin's command is registered and the
        // manager accepts further actions.
        Assert.Single(registry.Commands, extension => extension.PluginId == GoodPluginId);
        Assert.False(await manager.DisableAsync("com.example.absent"));
        Assert.True(await manager.DisableAsync(GoodPluginId));
    }

    [Fact]
    public async Task AConstructorFaultDoesNotCrashTheManager()
    {
        using var temp = new TempDirectory();
        StagePlugin(temp, CreateManifest(GoodPluginId, idFolder: GoodPluginId));
        StagePlugin(
            temp,
            CreateManifest(FaultyPluginId, "SampleCommandPlugin.ConstructorThrowingPlugin", idFolder: FaultyPluginId));
        var manager = CreateManager(temp, out var registry);

        await manager.InitializeAsync(temp.Combine("plugins"));

        Assert.Equal(2, manager.Statuses.Count);
        var faulty = Assert.Single(manager.Statuses, status => status.Id == FaultyPluginId);
        Assert.Equal(PluginState.DisabledByError, faulty.State);
        Assert.Contains("fails to construct", faulty.Reason, StringComparison.Ordinal);
        Assert.Single(manager.Statuses, status => status.State == PluginState.Loaded);
        Assert.Single(registry.Commands);
    }

    [Fact]
    public async Task AMissingDependencyMarksThePluginDisabledByError()
    {
        using var temp = new TempDirectory();
        StagePlugin(
            temp,
            CreateManifest(
                FaultyPluginId,
                dependencies: [new PluginDependency { Id = "com.example.absent", MinVersion = "1.0.0" }]));
        var manager = CreateManager(temp, out _);

        await manager.InitializeAsync(temp.Combine("plugins"));

        var status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.DisabledByError, status.State);
        Assert.Contains("com.example.absent", status.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APluginDisabledBeforeStartupIsSkippedAndCanBeEnabled()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        StagePlugin(temp, CreateManifest());
        var store = await PluginStateStore.LoadAsync(root);
        await store.SetDisabledAsync(SamplePluginId, disabled: true);
        var manager = CreateManager(temp, out var registry);

        await manager.InitializeAsync(root);

        var status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.Disabled, status.State);
        Assert.Equal("Disabled", status.StatusLabel);
        Assert.Contains("restart", status.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(registry.Commands);
        Assert.True(manager.IsDisabled(SamplePluginId));
        Assert.False(await manager.EnableAsync("com.example.absent"));

        Assert.True(await manager.EnableAsync(SamplePluginId));
        status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.EnabledPendingRestart, status.State);
        Assert.Equal("Enabled · restart required", status.StatusLabel);
        Assert.True(manager.RestartRequired);
        Assert.False(manager.IsDisabled(SamplePluginId));
        Assert.Empty(registry.Commands);

        var reloaded = await PluginStateStore.LoadAsync(root);
        Assert.False(reloaded.IsDisabled(SamplePluginId));
    }

    [Fact]
    public async Task DisableRemovesExtensionsPersistsStateAndKeepsAssembliesUntilRestart()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        StagePlugin(temp, CreateManifest());
        var manager = CreateManager(temp, out var registry);
        await manager.InitializeAsync(root);
        Assert.Single(registry.Commands);
        Assert.False(manager.RestartRequired);

        Assert.True(await manager.DisableAsync(SamplePluginId));

        var status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.Disabled, status.State);
        Assert.Contains("restart", status.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(manager.RestartRequired);
        Assert.Empty(registry.Commands);
        Assert.Empty(registry.DeviceActions);
        Assert.True(manager.IsDisabled(SamplePluginId));
        Assert.False(await manager.DisableAsync(SamplePluginId));

        // Assemblies remain loaded until restart (plan 14.7), but the plugin is
        // still shut down cleanly when the host exits.
        Assert.Single(manager.Results, result => result.Succeeded);
        var reloaded = await PluginStateStore.LoadAsync(root);
        Assert.True(reloaded.IsDisabled(SamplePluginId));

        await manager.DisposeAsync();
        Assert.True(File.Exists(Path.Combine(root, SamplePluginId, "data", "shutdown.txt")));
    }

    [Fact]
    public async Task EnableAfterDisableRequiresARestartAndCanBeReverted()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        StagePlugin(temp, CreateManifest());
        var manager = CreateManager(temp, out var registry);
        await manager.InitializeAsync(root);

        Assert.True(await manager.DisableAsync(SamplePluginId));
        Assert.True(await manager.EnableAsync(SamplePluginId));
        Assert.Equal(PluginState.EnabledPendingRestart, manager.Statuses[0].State);
        Assert.True(manager.RestartRequired);
        Assert.False(await manager.EnableAsync(SamplePluginId));

        Assert.True(await manager.DisableAsync(SamplePluginId));
        Assert.Equal(PluginState.Disabled, manager.Statuses[0].State);
        Assert.True(manager.IsDisabled(SamplePluginId));
        Assert.Empty(registry.Commands);
    }

    [Fact]
    public async Task EnablingAPluginThatFailedThisSessionKeepsTheErrorVisible()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        var manifest = CreateManifest(FaultyPluginId);
        manifest.ApiVersion = "99.0";
        StagePlugin(temp, manifest);
        var manager = CreateManager(temp, out _);
        await manager.InitializeAsync(root);

        Assert.True(await manager.EnableAsync(FaultyPluginId));

        var status = Assert.Single(manager.Statuses);
        Assert.Equal(PluginState.EnabledPendingRestart, status.State);
        Assert.Contains("99.0", status.Reason, StringComparison.Ordinal);
        Assert.True(manager.RestartRequired);
    }

    [Fact]
    public async Task InitializeRunsInLoadOrderAndDisposeRunsInReverse()
    {
        using var temp = new TempDirectory();
        var root = temp.Combine("plugins");
        StagePlugin(temp, CreateManifest("com.example.alpha", idFolder: "com.example.alpha"));
        StagePlugin(temp, CreateManifest("com.example.beta", idFolder: "com.example.beta"));
        var manager = CreateManager(temp, out _);

        await manager.InitializeAsync(root);
        await manager.DisposeAsync();

        var markers = await File.ReadAllLinesAsync(Path.Combine(root, "lifecycle-order.log"));
        Assert.Equal(
            [
                "com.example.alpha:init",
                "com.example.beta:init",
                "com.example.beta:shutdown",
                "com.example.alpha:shutdown",
            ],
            markers);
    }

    [Fact]
    public async Task StatusesAreEmptyUntilInitialized()
    {
        using var temp = new TempDirectory();
        var manager = CreateManager(temp, out _);

        Assert.Empty(manager.Statuses);
        Assert.False(manager.IsInitialized);

        await manager.InitializeAsync(temp.Combine("plugins"));
        Assert.True(manager.IsInitialized);
    }

    private static PluginManager CreateManager(TempDirectory temp, out PluginRegistry registry)
    {
        registry = new PluginRegistry();
        var loader = new PluginLoader(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");
        return new PluginManager(loader, new PluginHealthTracker());
    }

    private static PluginManager CreateManager(
        TempDirectory temp,
        out PluginRegistry registry,
        out PluginHealthTracker health)
    {
        registry = new PluginRegistry();
        health = new PluginHealthTracker();
        var loader = new PluginLoader(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");
        return new PluginManager(loader, health);
    }

    private static PluginManifest CreateManifest(
        string id = SamplePluginId,
        string entryType = "SampleCommandPlugin.SamplePlugin",
        string version = "1.0.0",
        IReadOnlyList<PluginDependency>? dependencies = null,
        string? idFolder = null) => new()
        {
            Id = id,
            Name = "Sample Plugin",
            Version = version,
            ApiVersion = "1.0",
            MinHostVersion = "0.1.0",
            EntryAssembly = "SampleCommandPlugin.dll",
            EntryType = entryType,
            Capabilities = ["command"],
            Dependencies = dependencies ?? [],
        };

    private static string StagePlugin(TempDirectory temp, PluginManifest manifest, string? idFolder = null)
    {
        var directory = temp.CreateDirectory("plugins", idFolder ?? manifest.Id);
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

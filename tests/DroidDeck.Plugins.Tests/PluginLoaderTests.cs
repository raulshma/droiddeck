using System.Runtime.Loader;
using System.Text.Json;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginLoaderTests
{
    private const string SamplePluginId = "com.example.sample";
    private const string ThrowingPluginId = "com.example.throwing";

    [Fact]
    public async Task LoadsSamplePluginAndRegistersItsCommand()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp, CreateManifest());
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var result = await loader.LoadAsync(directory);

        Assert.True(result.Succeeded, Describe(result.Validation));
        var plugin = Assert.IsType<LoadedPlugin>(result.Plugin);
        Assert.Equal(SamplePluginId, plugin.Id);
        Assert.Equal("Sample Plugin", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
        Assert.Equal("1.0", plugin.ApiVersion);
        Assert.Equal(directory, plugin.InstallPath);
        Assert.True(plugin.Capabilities.HasFlag(PluginCapability.Command));
        Assert.True(plugin.Capabilities.HasFlag(PluginCapability.RunStep));
        Assert.True(plugin.Capabilities.HasFlag(PluginCapability.LogParser));
        Assert.True(plugin.Capabilities.HasFlag(PluginCapability.HighlightRuleProvider));
        Assert.True(plugin.Capabilities.HasFlag(PluginCapability.TestRunner));
        Assert.Single(loader.Plugins);

        var command = Assert.Single(registry.Commands);
        Assert.Equal(SamplePluginId, command.PluginId);
        Assert.Equal(SamplePluginId, registry.GetOwner(command.Value.GetType()));
        Assert.Equal(SamplePluginId, Assert.Single(registry.DeviceActions).PluginId);
        Assert.Equal(SamplePluginId, Assert.Single(registry.ToolbarActions).PluginId);
        Assert.Equal(SamplePluginId, Assert.Single(registry.DeviceTabs).PluginId);
        Assert.Equal(SamplePluginId, Assert.Single(registry.RunSteps).PluginId);
        Assert.Equal(SamplePluginId, Assert.Single(registry.LogParsers).PluginId);
        Assert.Equal(SamplePluginId, Assert.Single(registry.HighlightRuleProviders).PluginId);
        Assert.Equal(SamplePluginId, Assert.Single(registry.TestRunners).PluginId);
        Assert.NotSame(AssemblyLoadContext.Default, plugin.LoadContext);
        Assert.True(plugin.LoadContext.IsCollectible);
        Assert.Same(plugin.LoadContext, AssemblyLoadContext.GetLoadContext(command.Value.GetType().Assembly));

        Assert.Equal("true", await plugin.Settings.GetAsync("initialized"));
        Assert.True(Directory.Exists(Path.Combine(directory, "data")));
        Assert.True(File.Exists(Path.Combine(directory, "settings.json")));
        Assert.Contains(
            plugin.LoadContext.Assemblies,
            assembly => assembly.GetName().Name == "SampleCommandPlugin");
    }

    [Fact]
    public async Task LoadedCommandExecutesThroughTheRegistry()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp, CreateManifest());
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);
        var result = await loader.LoadAsync(directory);
        Assert.True(result.Succeeded, Describe(result.Validation));

        await registry.Commands[0].Value.ExecuteAsync(CancellationToken.None);

        var marker = Path.Combine(directory, "data", "executed.txt");
        Assert.True(File.Exists(marker));
        Assert.Equal("ok", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task LoadedDeviceExtensionsExecuteAndCreateTheirContent()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp, CreateManifest());
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);
        var result = await loader.LoadAsync(directory);
        Assert.True(result.Succeeded, Describe(result.Validation));

        var context = new DeviceActionContext("device-1", "Pixel");
        await registry.DeviceActions[0].Value.ExecuteAsync(context, CancellationToken.None);
        await registry.ToolbarActions[0].Value.ExecuteAsync(context, CancellationToken.None);
        var content = registry.DeviceTabs[0].Value.CreateContent(
            new DeviceTabContext("device-1", "Pixel"));

        Assert.Equal("Pixel", await File.ReadAllTextAsync(
            Path.Combine(directory, "data", "device-action-device-1.txt")));
        Assert.Equal("device-1", await File.ReadAllTextAsync(
            Path.Combine(directory, "data", "toolbar-action.txt")));
        Assert.Equal("Sample plugin tab for Pixel.", content);
    }

    [Fact]
    public async Task LoadedPipelineExtensionsExecuteThroughTheRegistry()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp, CreateManifest());
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);
        var result = await loader.LoadAsync(directory);
        Assert.True(result.Succeeded, Describe(result.Validation));

        var projectRoot = temp.CreateDirectory("project");
        await registry.RunSteps[0].Value.ExecuteAsync(
            new RunStepContext(projectRoot, "device-1"),
            CancellationToken.None);
        var parsed = registry.LogParsers[0].Value.Parse(
            new LogParserContext("device-1", "I [sample] ready", "sample", "I", "[sample] ready"));
        var rule = Assert.Single(registry.HighlightRuleProviders[0].Value.Rules);
        var run = await registry.TestRunners[0].Value.RunAsync(
            new TestRunnerContext { ProjectRoot = projectRoot },
            CancellationToken.None);

        Assert.Equal(
            $"{projectRoot}|device-1",
            await File.ReadAllTextAsync(Path.Combine(directory, "data", "run-step.txt")));
        Assert.NotNull(parsed);
        Assert.Equal("Sample", parsed.Tag);
        Assert.Equal(rule.Id, parsed.HighlightRuleId);
        Assert.True(run.Succeeded);
        var suite = Assert.Single(run.Suites!);
        Assert.Equal("Sample suite", suite.Name);
        Assert.Contains(suite.Cases, testCase => testCase.Status == PluginTestStatus.Passed);
        Assert.Equal(
            projectRoot,
            await File.ReadAllTextAsync(Path.Combine(directory, "data", "test-run.txt")));
    }

    [Fact]
    public async Task UnloadShutsThePluginDownAndRemovesItsExtensions()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp, CreateManifest());
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);
        var result = await loader.LoadAsync(directory);
        Assert.True(result.Succeeded, Describe(result.Validation));

        var unloaded = await loader.UnloadAsync(SamplePluginId);

        Assert.True(unloaded);
        Assert.Empty(registry.Commands);
        Assert.Empty(registry.DeviceActions);
        Assert.Empty(registry.ToolbarActions);
        Assert.Empty(registry.DeviceTabs);
        Assert.Empty(registry.RunSteps);
        Assert.Empty(registry.LogParsers);
        Assert.Empty(registry.HighlightRuleProviders);
        Assert.Empty(registry.TestRunners);
        Assert.Empty(loader.Plugins);
        Assert.True(File.Exists(Path.Combine(directory, "data", "shutdown.txt")));
        Assert.False(await loader.UnloadAsync(SamplePluginId));
    }

    [Fact]
    public async Task DisposeUnloadsEveryPlugin()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp, CreateManifest());
        var registry = new PluginRegistry();
        var loader = CreateLoader(registry);
        var result = await loader.LoadAsync(directory);
        Assert.True(result.Succeeded, Describe(result.Validation));

        await loader.DisposeAsync();
        await loader.DisposeAsync();

        Assert.Empty(registry.Commands);
        Assert.Empty(loader.Plugins);
        Assert.True(File.Exists(Path.Combine(directory, "data", "shutdown.txt")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => loader.LoadAsync(directory));
    }

    [Fact]
    public async Task MissingManifestFails()
    {
        using var temp = new TempDirectory();
        var directory = temp.CreateDirectory("empty-plugin");
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var result = await loader.LoadAsync(directory);

        Assert.False(result.Succeeded);
        Assert.Null(result.Plugin);
        AssertHasError(result.Validation, "manifest.missing");
        Assert.Empty(loader.Plugins);
    }

    [Fact]
    public async Task InvalidManifestFailsBeforeLoadingTheAssembly()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.ApiVersion = "99.0";
        var directory = StagePlugin(temp, manifest);
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var result = await loader.LoadAsync(directory);

        Assert.False(result.Succeeded);
        AssertHasError(result.Validation, "manifest.apiVersion.majorMismatch");
        Assert.Empty(registry.Commands);
        Assert.Empty(loader.Plugins);
    }

    [Fact]
    public async Task DuplicatePluginIdFails()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp, CreateManifest());
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);
        var first = await loader.LoadAsync(directory);
        Assert.True(first.Succeeded, Describe(first.Validation));

        var second = await loader.LoadAsync(directory);

        Assert.False(second.Succeeded);
        AssertHasError(second.Validation, "plugin.id.duplicate");
        Assert.Single(loader.Plugins);
    }

    [Fact]
    public async Task MissingEntryTypeFails()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp, CreateManifest(entryType: "SampleCommandPlugin.DoesNotExist"));
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var result = await loader.LoadAsync(directory);

        Assert.False(result.Succeeded);
        AssertHasError(result.Validation, "plugin.entryType.missing");
        Assert.Empty(loader.Plugins);
    }

    [Fact]
    public async Task EntryTypeMustImplementIPlugin()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp, CreateManifest(entryType: "SampleCommandPlugin.NotAPlugin"));
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var result = await loader.LoadAsync(directory);

        Assert.False(result.Succeeded);
        AssertHasError(result.Validation, "plugin.entryType.invalid");
    }

    [Fact]
    public async Task InitializationFailureIsIsolatedAndRolledBack()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest(
            id: ThrowingPluginId,
            entryType: "SampleCommandPlugin.ThrowingPlugin");
        var directory = StagePlugin(temp, manifest);
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var result = await loader.LoadAsync(directory);

        Assert.False(result.Succeeded);
        Assert.Null(result.Plugin);
        AssertHasError(result.Validation, "plugin.load.failed");
        Assert.Contains(
            result.Validation.Issues,
            issue => issue.Message.Contains("always fails to initialize", StringComparison.Ordinal));
        Assert.Empty(registry.Commands);
        Assert.Empty(loader.Plugins);
    }

    [Fact]
    public async Task LoadInstalledLoadsEveryManifestAndIsolatesFailures()
    {
        using var temp = new TempDirectory();
        StagePlugin(temp, CreateManifest());
        StagePlugin(
            temp,
            CreateManifest(id: ThrowingPluginId, entryType: "SampleCommandPlugin.ThrowingPlugin"));
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var results = await loader.LoadInstalledAsync(temp.Combine("plugins"));

        Assert.Equal(2, results.Count);
        Assert.Single(results, result => result.Succeeded && result.Plugin!.Id == SamplePluginId);
        var failed = Assert.Single(results, result => !result.Succeeded);
        AssertHasError(failed.Validation, "plugin.load.failed");
        Assert.Single(registry.Commands);
        Assert.Single(loader.Plugins);
    }

    [Fact]
    public async Task LoadInstalledReturnsEmptyForMissingRoot()
    {
        using var temp = new TempDirectory();
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var results = await loader.LoadInstalledAsync(temp.Combine("absent"));

        Assert.Empty(results);
    }

    [Fact]
    public async Task MinorApiMismatchLoadsWithAWarning()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.ApiVersion = "1.1";
        var directory = StagePlugin(temp, manifest);
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var result = await loader.LoadAsync(directory);

        Assert.True(result.Succeeded, Describe(result.Validation));
        Assert.True(result.Validation.HasWarnings);
        Assert.Contains(
            result.Validation.Issues,
            issue => issue.Code == "manifest.apiVersion.minorMismatch");
    }

    [Fact]
    public async Task HostVersionConstraintsAreEnforced()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.MinHostVersion = "2.0.0";
        var directory = StagePlugin(temp, manifest);
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var result = await loader.LoadAsync(directory);

        Assert.False(result.Succeeded);
        AssertHasError(result.Validation, "manifest.minHostVersion.newerThanHost");
    }

    private static PluginLoader CreateLoader(PluginRegistry registry)
        => new(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");

    private static PluginManifest CreateManifest(
        string id = SamplePluginId,
        string entryType = "SampleCommandPlugin.SamplePlugin") => new()
        {
            Id = id,
            Name = "Sample Plugin",
            Version = "1.0.0",
            ApiVersion = "1.0",
            MinHostVersion = "0.1.0",
            EntryAssembly = "SampleCommandPlugin.dll",
            EntryType = entryType,
            Capabilities =
            [
                "command",
                "device-action",
                "device-tab",
                "toolbar-action",
                "run-step",
                "log-parser",
                "highlight-rule-provider",
                "test-runner",
            ],
        };

    private static string StagePlugin(TempDirectory temp, PluginManifest manifest)
    {
        var directory = temp.CreateDirectory("plugins", manifest.Id);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "SampleCommandPlugin.dll"),
            Path.Combine(directory, "SampleCommandPlugin.dll"),
            overwrite: true);
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        return directory;
    }

    private static void AssertHasError(PluginValidationResult result, string code)
    {
        Assert.Contains(
            result.Issues,
            issue => issue.Code == code && issue.Severity == PluginValidationSeverity.Error);
    }

    private static string Describe(PluginValidationResult result)
        => string.Join(Environment.NewLine, result.Issues.Select(issue => issue.ToString()));
}

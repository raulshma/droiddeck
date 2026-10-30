using System.Text.Json;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Loads the detector and confirmed device action samples end to end so the
/// samples stay working references for plugin authors.
/// </summary>
public sealed class SampleExtensionPluginsTests
{
    [Fact]
    public async Task TheDetectorSampleExplainsItsDetection()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(
            temp,
            "com.example.sample.detector",
            "SampleDetectorPlugin.dll",
            "SampleDetectorPlugin.SampleDetectorPlugin",
            "project-detector");
        var registry = new PluginRegistry();
        await using var loader = new PluginLoader(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");

        var result = await loader.LoadAsync(directory);

        Assert.True(result.Succeeded, Describe(result));
        var detector = Assert.Single(registry.ProjectDetectors);
        Assert.Equal("com.example.sample.detector", detector.PluginId);
        Assert.Equal("com.example.sample.detector.ionic", detector.Value.Id);

        var ionicProject = temp.CreateDirectory("ionic-project");
        temp.WriteFile("ionic-project/ionic.config.json", "{}");
        var detection = await detector.Value.DetectAsync(
            new ProjectDetectionContext(ionicProject),
            CancellationToken.None);

        Assert.NotNull(detection);
        Assert.Equal("ionic", detection.Kind);
        Assert.Equal("Ionic", detection.DisplayName);
        Assert.Contains("ionic.config.json", detection.Evidence);

        var emptyProject = temp.CreateDirectory("empty-project");
        Assert.Null(await detector.Value.DetectAsync(
            new ProjectDetectionContext(emptyProject),
            CancellationToken.None));
    }

    [Fact]
    public async Task TheDeviceActionSampleRequiresConfirmationBeforeItRuns()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(
            temp,
            "com.example.sample.device-actions",
            "SampleDeviceActionPlugin.dll",
            "SampleDeviceActionPlugin.SampleDeviceActionPlugin",
            "device-action");
        var registry = new PluginRegistry();
        await using var loader = new PluginLoader(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");

        var result = await loader.LoadAsync(directory);

        Assert.True(result.Succeeded, Describe(result));
        var action = Assert.Single(registry.DeviceActions);
        Assert.Equal("com.example.sample.device-actions", action.PluginId);
        Assert.True(action.Value.RequiresConfirmation);

        await action.Value.ExecuteAsync(
            new DeviceActionContext("emulator-5554", "Pixel 8"),
            CancellationToken.None);

        var marker = Path.Combine(directory, "data", "device-action-emulator-5554.txt");
        Assert.True(File.Exists(marker));
        Assert.Equal("Pixel 8", await File.ReadAllTextAsync(marker));
    }

    private static string StagePlugin(
        TempDirectory temp,
        string pluginId,
        string assemblyName,
        string entryType,
        params string[] capabilities)
    {
        var directory = temp.CreateDirectory("plugins", pluginId);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, assemblyName),
            Path.Combine(directory, assemblyName),
            overwrite: true);

        var manifest = new PluginManifest
        {
            Id = pluginId,
            Name = pluginId,
            Version = "1.0.0",
            ApiVersion = "1.0",
            MinHostVersion = "0.1.0",
            EntryAssembly = assemblyName,
            EntryType = entryType,
            Capabilities = capabilities,
        };
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        return directory;
    }

    private static string Describe(PluginLoadResult result) =>
        string.Join(Environment.NewLine, result.Validation.Issues.Select(issue => issue.ToString()));
}

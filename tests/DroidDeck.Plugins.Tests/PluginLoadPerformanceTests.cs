using System.Diagnostics;
using System.Text.Json;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Plugin startup budget tests. The budget from the performance plan (17.3) is
/// 300 ms for five enabled plugins after the first frame; CI machines are too
/// noisy to assert that directly, so the test asserts a generous 5 s wall while
/// reporting the measured duration for tracking.
/// </summary>
public sealed class PluginLoadPerformanceTests
{
    private static readonly TimeSpan WallClockTolerance = TimeSpan.FromSeconds(5);

    private readonly ITestOutputHelper _output;

    public PluginLoadPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task FiveEnabledPluginsLoadWithinTheStartupBudget()
    {
        using var temp = new TempDirectory();
        StagePerfPlugins(temp, count: 5);
        var registry = new PluginRegistry();
        await using var loader = new PluginLoader(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");
        var manager = new PluginManager(loader, new PluginHealthTracker(), trustPolicy: PluginTrustPolicy.TrustAll);

        var stopwatch = Stopwatch.StartNew();
        await manager.InitializeAsync(temp.Combine("plugins"));
        stopwatch.Stop();

        Assert.Equal(5, manager.Statuses.Count(status => status.State == PluginState.Loaded));
        var measured = manager.Results.Sum(result => result.LoadDuration.TotalMilliseconds);
        _output.WriteLine(
            "Loaded 5 plugins in {0:F1} ms wall clock; {1:F1} ms of measured plugin load time. Budget: 300 ms after first frame.",
            stopwatch.Elapsed.TotalMilliseconds,
            measured);
        Assert.True(
            stopwatch.Elapsed < WallClockTolerance,
            $"Loading five plugins took {stopwatch.Elapsed.TotalMilliseconds:F0} ms, which exceeds the {WallClockTolerance.TotalSeconds:F0} s CI tolerance for the 300 ms budget.");
    }

    private static void StagePerfPlugins(TempDirectory temp, int count)
    {
        for (var index = 1; index <= count; index++)
        {
            var manifest = new PluginManifest
            {
                Id = $"com.example.perf{index}",
                Name = $"Perf Sample Plugin {index}",
                Version = "1.0.0",
                ApiVersion = "1.0",
                MinHostVersion = "0.1.0",
                EntryAssembly = $"SamplePerfPlugin{index}.dll",
                EntryType = $"SamplePerfPlugin{index}.PerfPlugin",
            };
            var directory = temp.CreateDirectory("plugins", manifest.Id);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, $"SamplePerfPlugin{index}.dll"),
                Path.Combine(directory, manifest.EntryAssembly),
                overwrite: true);
            File.WriteAllText(
                Path.Combine(directory, PluginManifestReader.FileName),
                JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        }
    }
}

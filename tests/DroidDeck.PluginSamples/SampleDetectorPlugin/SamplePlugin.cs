using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace SampleDetectorPlugin;

/// <summary>
/// A minimal plugin that registers one project detector.
/// </summary>
public sealed class SampleDetectorPlugin : IPlugin
{
    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Extensions.AddProjectDetector(new IonicProjectDetector(context.Logger));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Detects Ionic projects by their configuration file. The detector returns the
/// evidence it used so the host can explain the detection instead of guessing.
/// </summary>
public sealed class IonicProjectDetector(ILogger logger) : IProjectDetector
{
    /// <inheritdoc />
    public string Id => "com.example.sample.detector.ionic";

    /// <inheritdoc />
    public int Priority => 50;

    /// <inheritdoc />
    public Task<ProjectDetection?> DetectAsync(
        ProjectDetectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var evidence = new List<string>();
        foreach (var fileName in new[] { "ionic.config.json", "capacitor.config.json" })
        {
            if (File.Exists(Path.Combine(context.ProjectRoot, fileName)))
            {
                evidence.Add(fileName);
            }
        }

        if (evidence.Count == 0)
        {
            return Task.FromResult<ProjectDetection?>(null);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Detected an Ionic project in {ProjectRoot}.", context.ProjectRoot);
        }

        return Task.FromResult<ProjectDetection?>(
            new ProjectDetection("ionic", "Ionic", evidence));
    }
}

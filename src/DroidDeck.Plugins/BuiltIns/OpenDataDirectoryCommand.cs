using System.Diagnostics;
using DroidDeck.Core;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins.BuiltIns;

/// <summary>
/// Opens the DroidDeck data directory in File Explorer.
/// </summary>
public sealed class OpenDataDirectoryCommand(AppPaths paths) : ICommand
{
    /// <summary>The stable command identifier.</summary>
    public const string CommandId = "droiddeck.builtin.openDataDirectory";

    /// <inheritdoc />
    public string Id => CommandId;

    /// <inheritdoc />
    public string Title => "Open data folder";

    /// <inheritdoc />
    public string Description => "Opens the DroidDeck data folder in File Explorer.";

    /// <inheritdoc />
    public IReadOnlyList<string> Keywords => ["data", "folder", "explorer", "logs", "settings"];

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(paths.DataDirectory);
        using var process = Process.Start(new ProcessStartInfo(paths.DataDirectory) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

/// <summary>
/// Registers host built-in extensions in the shared registry.
/// </summary>
public static class BuiltInExtensions
{
    /// <summary>Registers all built-in extensions.</summary>
    /// <param name="registry">The shared registry.</param>
    /// <param name="paths">The application data paths.</param>
    /// <param name="runSteps">Built-in run steps composed from host services.</param>
    public static void Register(
        IPluginRegistry registry,
        AppPaths paths,
        IEnumerable<IRunStep>? runSteps = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(paths);
        registry.AddCommand(new OpenDataDirectoryCommand(paths));
        registry.AddLogParser(new AndroidCrashLogParser());
        registry.AddHighlightRuleProvider(new BuiltInHighlightRuleProvider());
        foreach (var step in runSteps ?? [])
        {
            registry.AddRunStep(step);
        }
    }
}

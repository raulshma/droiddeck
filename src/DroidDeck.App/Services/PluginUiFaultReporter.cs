using DroidDeck.Plugins;
using DroidDeck.Plugin.UI.Kit;

namespace DroidDeck.App.Services;

/// <summary>
/// Feeds plugin UI faults into the shared health tracker so the crash guard
/// can pause a plugin whose UI keeps failing.
/// </summary>
public sealed class PluginUiFaultReporter(PluginHealthTracker health) : IPluginFaultReporter
{
    /// <inheritdoc />
    public void Report(string pluginId, string operation, Exception exception) =>
        health.RecordFailure(pluginId, $"UI {operation} failed: {exception.Message}");
}

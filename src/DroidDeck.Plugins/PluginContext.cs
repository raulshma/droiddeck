using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Plugins;

/// <summary>
/// The services and metadata handed to a plugin during initialization.
/// </summary>
internal sealed class PluginContext(
    IPluginInfo info,
    ILogger logger,
    IPluginRegistry extensions,
    IPluginSettings settings,
    string dataDirectory,
    IPluginDialogService dialogs) : IPluginContext
{
    public IPluginInfo Info { get; } = info;

    public ILogger Logger { get; } = logger;

    public IPluginRegistry Extensions { get; } = extensions;

    public IPluginSettings Settings { get; } = settings;

    public string DataDirectory { get; } = dataDirectory;

    public IPluginDialogService Dialogs { get; } = dialogs;
}

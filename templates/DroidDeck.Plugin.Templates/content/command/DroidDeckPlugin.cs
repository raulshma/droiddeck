using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace DroidDeckPlugin;

/// <summary>Registers this plugin's contributions with DroidDeck.</summary>
public sealed class DroidDeckPlugin : IPlugin
{
    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Extensions.AddCommand(new DroidDeckPluginCommand(context.Logger));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A command palette command that writes one line to the plugin log.</summary>
public sealed class DroidDeckPluginCommand(ILogger logger) : ICommand
{
    /// <inheritdoc />
    public string Id => "com.example.myplugin.command";

    /// <inheritdoc />
    public string Title => "Run DroidDeckPlugin command";

    /// <inheritdoc />
    public string Description => "Writes a line to the plugin log.";

    /// <inheritdoc />
    public IReadOnlyList<string> Keywords => ["plugin", "command"];

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("DroidDeckPlugin command ran.");
        return Task.CompletedTask;
    }
}

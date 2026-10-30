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
        context.Extensions.AddDeviceAction(new DroidDeckPluginDeviceAction(context.Logger));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A device action that asks for confirmation and logs the target.</summary>
public sealed class DroidDeckPluginDeviceAction(ILogger logger) : IDeviceAction
{
    /// <inheritdoc />
    public string Id => "com.example.myplugin.deviceAction";

    /// <inheritdoc />
    public string Name => "Run DroidDeckPlugin device action";

    /// <inheritdoc />
    public bool RequiresConfirmation => true;

    /// <inheritdoc />
    public Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        logger.LogInformation("The device action ran for {Serial}.", context.Serial);
        return Task.CompletedTask;
    }
}

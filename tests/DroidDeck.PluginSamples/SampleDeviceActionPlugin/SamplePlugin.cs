using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace SampleDeviceActionPlugin;

/// <summary>
/// A minimal plugin that registers one device action.
/// </summary>
public sealed class SampleDeviceActionPlugin : IPlugin
{
    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Extensions.AddDeviceAction(
            new ConfirmedMarkerAction(context.DataDirectory, context.Logger));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A device action that asks the host to confirm before it runs. The host shows
/// the confirmation prompt when <see cref="RequiresConfirmation"/> is true and
/// calls <see cref="ExecuteAsync"/> only after the user approves.
/// </summary>
public sealed class ConfirmedMarkerAction(string dataDirectory, ILogger logger) : IDeviceAction
{
    /// <inheritdoc />
    public string Id => "com.example.sample.device-actions.confirmedMarker";

    /// <inheritdoc />
    public string Name => "Write a sample marker for this device";

    /// <inheritdoc />
    public bool RequiresConfirmation => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(dataDirectory);
        var safeSerial = string.Concat(context.Serial.Select(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, $"device-action-{safeSerial}.txt"),
            context.Model ?? string.Empty,
            cancellationToken).ConfigureAwait(false);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("The sample device action ran for {Serial}.", context.Serial);
        }
    }
}

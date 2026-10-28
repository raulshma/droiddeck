using DroidDeck.Plugin.Abstractions;

namespace SampleCommandPlugin;

/// <summary>
/// A minimal plugin that registers one command.
/// </summary>
public sealed class SamplePlugin : IPlugin
{
    private string _dataDirectory = string.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _dataDirectory = context.DataDirectory;
        await context.Settings.SetAsync("initialized", "true", cancellationToken).ConfigureAwait(false);
        context.Extensions.AddCommand(new SampleCommand(context.DataDirectory));
        context.Extensions.AddDeviceAction(new SampleDeviceAction(context.DataDirectory));
        context.Extensions.AddToolbarAction(new SampleToolbarAction(context.DataDirectory));
        context.Extensions.AddDeviceTab(new SampleDeviceTab());
    }

    /// <inheritdoc />
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_dataDirectory.Length > 0)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_dataDirectory, "shutdown.txt"),
                "ok",
                cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A command that writes a marker file so tests can observe execution.
/// </summary>
public sealed class SampleCommand(string dataDirectory) : ICommand
{
    /// <inheritdoc />
    public string Id => "com.example.sample.command";

    /// <inheritdoc />
    public string Title => "Run sample command";

    /// <inheritdoc />
    public string Description => "Writes a marker file that proves the sample plugin command ran.";

    /// <inheritdoc />
    public IReadOnlyList<string> Keywords => ["sample", "plugin", "command"];

    /// <inheritdoc />
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, "executed.txt"),
            "ok",
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A device action that writes a marker file so tests and the host can observe it.
/// </summary>
public sealed class SampleDeviceAction(string dataDirectory) : IDeviceAction
{
    /// <inheritdoc />
    public string Id => "com.example.sample.deviceAction";

    /// <inheritdoc />
    public string Name => "Run sample device action";

    /// <inheritdoc />
    public bool RequiresConfirmation => false;

    /// <inheritdoc />
    public async Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, $"device-action-{Sanitize(context.Serial)}.txt"),
            context.Model ?? string.Empty,
            cancellationToken).ConfigureAwait(false);
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
}

/// <summary>
/// A toolbar action that writes a marker file so tests can observe execution.
/// </summary>
public sealed class SampleToolbarAction(string dataDirectory) : IDeviceToolbarAction
{
    /// <inheritdoc />
    public string Id => "com.example.sample.toolbarAction";

    /// <inheritdoc />
    public string Title => "Sample toolbar action";

    /// <inheritdoc />
    public string? ToolTip => "Proves the plugin toolbar path.";

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public bool RequiresConfirmation => false;

    /// <inheritdoc />
    public async Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, "toolbar-action.txt"),
            context.Serial,
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A plugin device tab. The host renders the returned content.
/// </summary>
public sealed class SampleDeviceTab : IDeviceTabProvider
{
    /// <inheritdoc />
    public string Id => "com.example.sample.deviceTab";

    /// <inheritdoc />
    public string Title => "Sample tab";

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public object CreateContent(DeviceTabContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"Sample plugin tab for {context.Model ?? context.Serial}.";
    }
}

/// <summary>
/// A type that is deliberately not a plugin, used to test entry type validation.
/// </summary>
public sealed class NotAPlugin
{
}

/// <summary>
/// A plugin that fails during initialization, used to test load failure isolation.
/// </summary>
public sealed class ThrowingPlugin : IPlugin
{
    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Extensions.AddCommand(new ThrowingCommand());
        throw new InvalidOperationException("This plugin always fails to initialize.");
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A command registered by the throwing plugin before it fails.
/// </summary>
public sealed class ThrowingCommand : ICommand
{
    /// <inheritdoc />
    public string Id => "com.example.throwing.command";

    /// <inheritdoc />
    public string Title => "Never available";

    /// <inheritdoc />
    public string Description => "Registered by the throwing plugin before initialization fails.";

    /// <inheritdoc />
    public IReadOnlyList<string> Keywords => [];

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

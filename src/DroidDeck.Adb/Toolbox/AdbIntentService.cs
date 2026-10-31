using System.Globalization;
using DroidDeck.Adb.Commands;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Toolbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Toolbox;

/// <summary>Sends intents and broadcasts, reboots devices, and exposes tool entry points.</summary>
public sealed class AdbIntentService : IDeviceIntentService
{
    private static readonly TimeSpan IntentTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RebootTimeout = TimeSpan.FromSeconds(45);

    private static readonly IReadOnlyList<DeviceToolEntry> Entries =
    [
        new DeviceToolEntry(
            DeviceToolKind.Performance,
            "Performance",
            "Frame timing, render statistics, and graphics metrics for the device.",
            "devices/performance"),
        new DeviceToolEntry(
            DeviceToolKind.Inspector,
            "UI inspector",
            "Dump the live view hierarchy and inspect bounds for the device.",
            "devices/inspector"),
    ];

    private readonly Func<string> _adbPathProvider;
    private readonly IDeviceOperationCoordinator? _coordinator;
    private readonly ILogger<AdbIntentService> _logger;
    private readonly AdbCommandExecutor _adb;

    /// <summary>Creates an intent service with a provider for the current adb executable.</summary>
    public AdbIntentService(
        IProcessRunner runner,
        Func<string> adbPathProvider,
        ILogger<AdbIntentService>? logger = null,
        IDeviceOperationCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
        _logger = logger ?? NullLogger<AdbIntentService>.Instance;
        _coordinator = coordinator;
        _adb = new AdbCommandExecutor(runner, _adbPathProvider, logger: _logger);
    }

    /// <inheritdoc />
    public IReadOnlyList<DeviceToolEntry> ToolEntries => Entries;

    /// <inheritdoc />
    public Task<ToolboxCommandResult> SendIntentAsync(
        string serial,
        DeviceIntentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Action) &&
            string.IsNullOrWhiteSpace(request.Data) &&
            string.IsNullOrWhiteSpace(request.Component))
        {
            return Task.FromResult(ToolboxCommandResult.Failure(
                "An intent needs an action, a data URI, or an explicit component."));
        }

        var arguments = new List<string> { "shell", "am", "start" };
        if (request.WaitForResult)
        {
            arguments.Add("-W");
        }

        try
        {
            AddIntentFields(arguments, request);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(ToolboxCommandResult.Failure(exception.Message));
        }

        return RunIntentAsync(
            serial,
            "Send intent",
            arguments,
            "Intent sent.",
            "The intent failed",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ToolboxCommandResult> SendBroadcastAsync(
        string serial,
        DeviceBroadcastRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Action);
        var arguments = new List<string> { "shell", "am", "broadcast", "-a", request.Action };
        if (!string.IsNullOrWhiteSpace(request.Component))
        {
            arguments.AddRange(["-n", request.Component]);
        }

        if (!string.IsNullOrWhiteSpace(request.Package))
        {
            arguments.AddRange(["-p", request.Package]);
        }

        if (request.IncludeStoppedPackages)
        {
            arguments.Add("--include-stopped-packages");
        }

        try
        {
            AddExtras(arguments, request.Extras);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(ToolboxCommandResult.Failure(exception.Message));
        }

        return RunIntentAsync(
            serial,
            "Send broadcast",
            arguments,
            "Broadcast sent.",
            "The broadcast failed",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ToolboxCommandResult> RebootAsync(
        string serial,
        RebootMode mode,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var arguments = new List<string> { "reboot" };
        var target = mode switch
        {
            RebootMode.Recovery => "recovery",
            RebootMode.Bootloader => "bootloader",
            _ => null,
        };
        if (target is not null)
        {
            arguments.Add(target);
        }

        var display = ProcessRequest.FormatCommandLine(
            _adbPathProvider(),
            AdbCommandExecutor.BuildDeviceArguments(serial, arguments));
        var modeName = mode switch
        {
            RebootMode.Recovery => "recovery",
            RebootMode.Bootloader => "bootloader",
            _ => "the system",
        };
        if (!confirmed)
        {
            return ToolboxCommandResult.Failure(
                $"Rebooting {serial} to {modeName} requires explicit confirmation.",
                display,
                blocked: true) with
            {
                Blocked = true,
                IsDestructive = true,
            };
        }

        await using var lease = await AcquireDeviceLockAsync(serial, $"Reboot to {modeName}", cancellationToken)
            .ConfigureAwait(false);
        var result = await _adb
            .RunForDeviceAsync(serial, arguments, RebootTimeout, cancellationToken)
            .ConfigureAwait(false);
        return ToolboxCommandResult.FromProcess(
            result.Raw,
            result.Succeeded,
            result.Succeeded
                ? $"Rebooting {serial} to {modeName}."
                : $"The reboot command failed: {Summarize(result.CombinedOutput)}",
            isDestructive: true);
    }

    private static void AddIntentFields(List<string> arguments, DeviceIntentRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Action))
        {
            arguments.AddRange(["-a", request.Action]);
        }

        if (!string.IsNullOrWhiteSpace(request.Data))
        {
            arguments.AddRange(["-d", request.Data]);
        }

        if (!string.IsNullOrWhiteSpace(request.MimeType))
        {
            arguments.AddRange(["-t", request.MimeType]);
        }

        if (!string.IsNullOrWhiteSpace(request.Component))
        {
            arguments.AddRange(["-n", request.Component]);
        }

        if (!string.IsNullOrWhiteSpace(request.Package))
        {
            arguments.AddRange(["-p", request.Package]);
        }

        foreach (var category in request.Categories)
        {
            if (!string.IsNullOrWhiteSpace(category))
            {
                arguments.AddRange(["-c", category]);
            }
        }

        if (request.Flags is { } flags)
        {
            arguments.AddRange(["-f", flags.ToString(CultureInfo.InvariantCulture)]);
        }

        AddExtras(arguments, request.Extras);
    }

    private static void AddExtras(List<string> arguments, IReadOnlyList<IntentExtra> extras)
    {
        foreach (var extra in extras)
        {
            if (string.IsNullOrWhiteSpace(extra.Key))
            {
                continue;
            }

            var flag = extra.Type switch
            {
                IntentExtraType.Text => "--es",
                IntentExtraType.WholeNumber => "--ei",
                IntentExtraType.BigNumber => "--el",
                IntentExtraType.Boolean => "--ez",
                IntentExtraType.Fraction => "--ef",
                _ => throw new ArgumentOutOfRangeException(nameof(extras), extra.Type, "Unknown extra type."),
            };
            arguments.AddRange([flag, extra.Key, NormalizeExtraValue(extra)]);
        }
    }

    private static string NormalizeExtraValue(IntentExtra extra) => extra.Type switch
    {
        IntentExtraType.WholeNumber when int.TryParse(extra.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) =>
            value.ToString(CultureInfo.InvariantCulture),
        IntentExtraType.BigNumber when long.TryParse(extra.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) =>
            value.ToString(CultureInfo.InvariantCulture),
        IntentExtraType.Fraction when float.TryParse(extra.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) =>
            value.ToString(CultureInfo.InvariantCulture),
        IntentExtraType.Boolean when bool.TryParse(extra.Value, out var value) =>
            value ? "true" : "false",
        IntentExtraType.WholeNumber or IntentExtraType.BigNumber or IntentExtraType.Fraction or IntentExtraType.Boolean =>
            throw new ArgumentException(
                $"The extra '{extra.Key}' is not a valid {extra.Type} value.",
                nameof(extra)),
        _ => extra.Value,
    };

    private async Task<ToolboxCommandResult> RunIntentAsync(
        string serial,
        string operation,
        List<string> arguments,
        string successMessage,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await using var lease = await AcquireDeviceLockAsync(serial, operation, cancellationToken)
            .ConfigureAwait(false);
        var result = await _adb
            .RunForDeviceAsync(serial, arguments, IntentTimeout, cancellationToken)
            .ConfigureAwait(false);
        var succeeded = result.Succeeded &&
                        !result.CombinedOutput.Contains("Exception", StringComparison.OrdinalIgnoreCase);
        return ToolboxCommandResult.FromProcess(
            result.Raw,
            succeeded,
            succeeded
                ? successMessage
                : $"{failureMessage}: {Summarize(result.CombinedOutput)}");
    }

    private async Task<IAsyncDisposable?> AcquireDeviceLockAsync(
        string serial,
        string operation,
        CancellationToken cancellationToken)
    {
        if (_coordinator is null)
        {
            return null;
        }

        return await _coordinator.AcquireAsync(serial, operation, cancellationToken).ConfigureAwait(false);
    }

    private static string Summarize(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "no output";
        }

        var line = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 300 ? line : line[..300] + "...";
    }
}

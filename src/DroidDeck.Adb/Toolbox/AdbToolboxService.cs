using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DroidDeck.Adb.Commands;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Toolbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Toolbox;

/// <summary>
/// Implements app, permission, shell, and port operations with adb. Every
/// operation returns the exact command line it ran.
/// </summary>
public sealed partial class AdbToolboxService : IDeviceToolboxService
{
    private const int MaxHistoryEntries = 100;
    private static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(90);

    private readonly Func<string> _adbPathProvider;
    private readonly IDeviceOperationCoordinator? _coordinator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdbToolboxService> _logger;
    private readonly AdbCommandExecutor _adb;
    private readonly object _historyGate = new();
    private readonly List<ShellHistoryEntry> _history = [];

    /// <summary>Creates a toolbox service with a provider for the current adb executable.</summary>
    public AdbToolboxService(
        IProcessRunner runner,
        Func<string> adbPathProvider,
        TimeProvider? timeProvider = null,
        ILogger<AdbToolboxService>? logger = null,
        IDeviceOperationCoordinator? coordinator = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AdbToolboxService>.Instance;
        _coordinator = coordinator;
        _adb = new AdbCommandExecutor(runner, _adbPathProvider, _timeProvider, _logger);
    }

    /// <inheritdoc />
    public IReadOnlyList<ShellHistoryEntry> ShellHistory
    {
        get
        {
            lock (_historyGate)
            {
                return _history.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolboxApp>> ListAppsAsync(
        string serial,
        AppListFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        if (filter == AppListFilter.All)
        {
            // pm list packages cannot label system packages on its own, so combine
            // the user and system listings instead of including uninstalled apps.
            var userTask = ListPackagesAsync(serial, "-3", isSystem: false, cancellationToken);
            var systemTask = ListPackagesAsync(serial, "-s", isSystem: true, cancellationToken);
            await Task.WhenAll(userTask, systemTask).ConfigureAwait(false);

            var packages = new Dictionary<string, ToolboxApp>(StringComparer.Ordinal);
            foreach (var app in await userTask.ConfigureAwait(false))
            {
                packages[app.PackageName] = app;
            }

            foreach (var app in await systemTask.ConfigureAwait(false))
            {
                packages[app.PackageName] = app;
            }

            return packages.Values
                .OrderBy(static app => app.PackageName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var flag = filter == AppListFilter.System ? "-s" : "-3";
        return await ListPackagesAsync(serial, flag, filter == AppListFilter.System, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ToolboxApp>> ListPackagesAsync(
        string serial,
        string filterFlag,
        bool isSystem,
        CancellationToken cancellationToken)
    {
        var result = await _adb
            .RunForDeviceAsync(serial, ["shell", "pm", "list", "packages", filterFlag], ListingTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.LogWarning("Listing applications on {Serial} failed: {Output}", serial, result.CombinedOutput);
            return [];
        }

        return ParseInstalledPackages(result.StandardOutput, isSystem);
    }

    /// <inheritdoc />
    public Task<ToolboxCommandResult> UninstallAppAsync(
        string serial,
        string packageName,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ValidatePackage(packageName);
        var arguments = new[] { "uninstall", packageName };
        return RunConfirmedAsync(
            serial,
            $"Uninstall {packageName}",
            arguments,
            confirmed,
            $"Uninstalling {packageName} requires confirmation.",
            $"Uninstalled {packageName}.",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ToolboxCommandResult> ClearAppDataAsync(
        string serial,
        string packageName,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ValidatePackage(packageName);
        var arguments = new[] { "shell", "pm", "clear", packageName };
        return RunConfirmedAsync(
            serial,
            $"Clear data for {packageName}",
            arguments,
            confirmed,
            $"Clearing data for {packageName} requires confirmation.",
            $"Cleared data for {packageName}.",
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ToolboxCommandResult> ForceStopAppAsync(
        string serial,
        string packageName,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ValidatePackage(packageName);
        var arguments = new[] { "shell", "am", "force-stop", packageName };
        return RunConfirmedAsync(
            serial,
            $"Force stop {packageName}",
            arguments,
            confirmed,
            $"Stopping {packageName} requires confirmation.",
            $"Stopped {packageName}.",
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ToolboxCommandResult> OpenAppInfoAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        ValidatePackage(packageName);
        await using var lease = await AcquireDeviceLockAsync(serial, $"Open app info for {packageName}", cancellationToken)
            .ConfigureAwait(false);
        var result = await _adb
            .RunForDeviceAsync(
                serial,
                [
                    "shell", "am", "start",
                    "-a", "android.settings.APPLICATION_DETAILS_SETTINGS",
                    "-d", $"package:{packageName}",
                ],
                ActionTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return ToolboxCommandResult.FromProcess(
            result.Raw,
            result.Succeeded,
            result.Succeeded
                ? $"Opened app info for {packageName}."
                : $"Could not open app info for {packageName}: {Summarize(result.CombinedOutput)}");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermissionState>> ListPermissionsAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        ValidatePackage(packageName);
        var result = await _adb
            .RunForDeviceAsync(serial, ["shell", "dumpsys", "package", packageName], ListingTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        return ParsePermissions(result.StandardOutput);
    }

    /// <inheritdoc />
    public async Task<ToolboxCommandResult> SetPermissionAsync(
        string serial,
        string packageName,
        string permission,
        bool grant,
        CancellationToken cancellationToken = default)
    {
        ValidatePackage(packageName);
        ValidatePermission(permission);
        await using var lease = await AcquireDeviceLockAsync(
                serial,
                $"{(grant ? "Grant" : "Revoke")} {permission} for {packageName}",
                cancellationToken)
            .ConfigureAwait(false);
        var result = await _adb
            .RunForDeviceAsync(
                serial,
                ["shell", "pm", grant ? "grant" : "revoke", packageName, permission],
                ActionTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        var action = grant ? "Granted" : "Revoked";
        return ToolboxCommandResult.FromProcess(
            result.Raw,
            result.Succeeded,
            result.Succeeded
                ? $"{action} {permission} for {packageName}."
                : $"{action} {permission} for {packageName} failed: {Summarize(result.CombinedOutput)}");
    }

    /// <inheritdoc />
    public async Task<ToolboxCommandResult> RunShellAsync(
        string serial,
        string command,
        bool allowDestructive = false,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var tokens = ShellTokens.Parse(command);
        if (tokens.Count == 0)
        {
            return ToolboxCommandResult.Failure("The shell command is empty.");
        }

        var display = ProcessRequest.FormatCommandLine(
            _adbPathProvider(),
            AdbCommandExecutor.BuildDeviceArguments(serial, ["shell", .. tokens]));
        if (ShellSafety.ContainsShellMetacharacters(tokens))
        {
            return ToolboxCommandResult.Failure(
                "Shell metacharacters such as ;, |, >, <, $, and backticks are blocked. " +
                "Run one command at a time without pipes or redirection.",
                display,
                blocked: true) with { Blocked = true };
        }

        var destructive = ShellSafety.IsDestructive(tokens);
        if (destructive && !allowDestructive)
        {
            return ToolboxCommandResult.Failure(
                "This command changes device state. Confirm it before running.",
                display,
                blocked: true) with { IsDestructive = true, Blocked = true };
        }

        var arguments = new List<string> { "shell" };
        arguments.AddRange(tokens);
        await using var lease = await AcquireDeviceLockAsync(serial, $"Shell {tokens[0]}", cancellationToken)
            .ConfigureAwait(false);
        var result = await _adb
            .RunForDeviceAsync(
                serial,
                arguments,
                ShellTimeout,
                new AdbCommandOptions { Output = output },
                cancellationToken)
            .ConfigureAwait(false);
        var entry = new ShellHistoryEntry
        {
            Command = command.Trim(),
            CommandLine = result.CommandLine,
            Output = result.CombinedOutput,
            Succeeded = result.Succeeded,
            ExitCode = result.ExitCode,
            Timestamp = _timeProvider.GetUtcNow(),
            Blocked = false,
        };
        AddHistory(entry);
        return ToolboxCommandResult.FromProcess(
            result.Raw,
            result.Succeeded,
            result.Succeeded
                ? "Command completed."
                : result.TimedOut
                    ? "The command timed out."
                    : $"The command exited with code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}.",
            destructive);
    }

    /// <inheritdoc />
    public void ClearShellHistory()
    {
        lock (_historyGate)
        {
            _history.Clear();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortMapping>> ListPortsAsync(
        string serial,
        PortDirection direction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var verb = direction == PortDirection.Forward ? "forward" : "reverse";
        var result = await _adb
            .RunForDeviceAsync(serial, [verb, "--list"], ListingTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        return ParsePorts(serial, direction, result.StandardOutput);
    }

    /// <inheritdoc />
    public async Task<ToolboxCommandResult> AddPortAsync(
        string serial,
        PortDirection direction,
        string local,
        string remote,
        CancellationToken cancellationToken = default)
    {
        ValidateSocket(local);
        ValidateSocket(remote);
        var verb = direction == PortDirection.Forward ? "forward" : "reverse";
        await using var lease = await AcquireDeviceLockAsync(serial, $"Add {verb} port", cancellationToken)
            .ConfigureAwait(false);
        var result = await _adb
            .RunForDeviceAsync(serial, [verb, local, remote], ListingTimeout, cancellationToken)
            .ConfigureAwait(false);
        return ToolboxCommandResult.FromProcess(
            result.Raw,
            result.Succeeded,
            result.Succeeded
                ? $"Mapped {local} → {remote}."
                : $"Could not add the {verb} mapping: {Summarize(result.CombinedOutput)}");
    }

    /// <inheritdoc />
    public async Task<ToolboxCommandResult> RemovePortAsync(
        string serial,
        PortDirection direction,
        string local,
        string remote,
        CancellationToken cancellationToken = default)
    {
        ValidateSocket(local);
        ValidateSocket(remote);
        var verb = direction == PortDirection.Forward ? "forward" : "reverse";
        var target = local;
        await using var lease = await AcquireDeviceLockAsync(serial, $"Remove {verb} port", cancellationToken)
            .ConfigureAwait(false);
        var result = await _adb
            .RunForDeviceAsync(serial, [verb, "--remove", target], ListingTimeout, cancellationToken)
            .ConfigureAwait(false);
        return ToolboxCommandResult.FromProcess(
            result.Raw,
            result.Succeeded,
            result.Succeeded
                ? $"Removed the {verb} mapping for {target}."
                : $"Could not remove the {verb} mapping: {Summarize(result.CombinedOutput)}");
    }

    /// <inheritdoc />
    public async Task<ToolboxCommandResult> RemoveAllPortsAsync(
        string serial,
        PortDirection direction,
        bool confirmed,
        CancellationToken cancellationToken = default)
    {
        var verb = direction == PortDirection.Forward ? "forward" : "reverse";
        var arguments = new[] { verb, "--remove-all" };
        return await RunConfirmedAsync(
            serial,
            $"Remove all {verb} ports",
            arguments,
            confirmed,
            $"Removing every {verb} mapping requires confirmation.",
            $"Removed every {verb} mapping.",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses the output of <c>pm list packages</c>.</summary>
    public static IReadOnlyList<ToolboxApp> ParseInstalledPackages(string output, bool isSystem)
    {
        var packages = new List<ToolboxApp>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("package:", StringComparison.Ordinal))
            {
                continue;
            }

            var packageName = line["package:".Length..].Trim();
            if (packageName.Length == 0)
            {
                continue;
            }

            packages.Add(new ToolboxApp
            {
                PackageName = packageName,
                IsSystem = isSystem,
            });
        }

        return packages
            .OrderBy(static app => app.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Parses runtime and install permissions from <c>dumpsys package</c> output.</summary>
    public static IReadOnlyList<PermissionState> ParsePermissions(string output)
    {
        var permissions = new List<PermissionState>();
        string? section = null;
        // Splitting on '\r' and '\n' separately would surface an empty line between
        // the two characters of a CRLF pair and end the section before any entry.
        foreach (var rawLine in output.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Equals("runtime permissions:", StringComparison.OrdinalIgnoreCase))
            {
                section = "runtime";
                continue;
            }

            if (trimmed.Equals("install permissions:", StringComparison.OrdinalIgnoreCase))
            {
                section = "install";
                continue;
            }

            if (trimmed.Length == 0)
            {
                section = null;
                continue;
            }

            if (section is null)
            {
                continue;
            }

            var match = PermissionLineRegex().Match(trimmed);
            if (!match.Success)
            {
                section = null;
                continue;
            }

            permissions.Add(new PermissionState
            {
                Name = match.Groups["name"].Value,
                IsGranted = string.Equals(match.Groups["granted"].Value, "true", StringComparison.OrdinalIgnoreCase),
                IsRuntime = section == "runtime",
            });
        }

        return permissions
            .GroupBy(static permission => permission.Name, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(static permission => permission.IsRuntime).First())
            .OrderBy(static permission => permission.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Parses <c>forward --list</c> or <c>reverse --list</c> output.</summary>
    public static IReadOnlyList<PortMapping> ParsePorts(
        string serial,
        PortDirection direction,
        string output)
    {
        var mappings = new List<PortMapping>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = rawLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 3)
            {
                continue;
            }

            var owner = tokens[0];
            if (direction == PortDirection.Forward &&
                !string.Equals(owner, serial, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            mappings.Add(new PortMapping
            {
                Direction = direction,
                Serial = direction == PortDirection.Forward ? owner : serial,
                Local = tokens[1],
                Remote = tokens[2],
            });
        }

        return mappings;
    }

    private async Task<ToolboxCommandResult> RunConfirmedAsync(
        string serial,
        string operation,
        IReadOnlyList<string> arguments,
        bool confirmed,
        string confirmationMessage,
        string successMessage,
        CancellationToken cancellationToken)
    {
        var display = ProcessRequest.FormatCommandLine(
            _adbPathProvider(),
            AdbCommandExecutor.BuildDeviceArguments(serial, arguments));
        if (!confirmed)
        {
            return ToolboxCommandResult.Failure(confirmationMessage, display, blocked: true) with
            {
                Blocked = true,
                IsDestructive = true,
            };
        }

        await using var lease = await AcquireDeviceLockAsync(serial, operation, cancellationToken)
            .ConfigureAwait(false);
        var result = await _adb
            .RunForDeviceAsync(serial, arguments, ActionTimeout, cancellationToken)
            .ConfigureAwait(false);
        return ToolboxCommandResult.FromProcess(
            result.Raw,
            result.Succeeded,
            result.Succeeded ? successMessage : $"{successMessage.TrimEnd('.')} failed: {Summarize(result.CombinedOutput)}",
            isDestructive: true);
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

    private void AddHistory(ShellHistoryEntry entry)
    {
        lock (_historyGate)
        {
            _history.Insert(0, entry);
            if (_history.Count > MaxHistoryEntries)
            {
                _history.RemoveRange(MaxHistoryEntries, _history.Count - MaxHistoryEntries);
            }
        }
    }

    private static void ValidatePackage(string packageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        if (!IdentifierRegex().IsMatch(packageName))
        {
            throw new ArgumentException("The package name contains unsupported characters.", nameof(packageName));
        }
    }

    private static void ValidatePermission(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        if (!IdentifierRegex().IsMatch(permission))
        {
            throw new ArgumentException("The permission name contains unsupported characters.", nameof(permission));
        }
    }

    private static void ValidateSocket(string socket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socket);
        if (!SocketRegex().IsMatch(socket))
        {
            throw new ArgumentException(
                "The socket must be a tcp, localabstract, localreserved, localfilesystem, or dev specification.",
                nameof(socket));
        }
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

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^(tcp|localabstract|localreserved|localfilesystem|dev):\S+$|^tcp:\d{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex SocketRegex();

    [GeneratedRegex(
        @"^(?<name>[A-Za-z0-9_.]+):\s*granted=(?<granted>true|false)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PermissionLineRegex();

    private static class ShellSafety
    {
        private static readonly HashSet<string> DestructiveCommands =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "reboot",
                "shutdown",
                "rm",
                "rmdir",
                "mkfs",
                "wipe",
                "dd",
                "su",
                "stop",
                "start",
            };

        private static readonly string[] DestructivePrefixes =
        [
            "pm clear",
            "pm uninstall",
            "pm disable",
            "pm enable",
            "pm grant",
            "pm revoke",
            "cmd package clear",
            "cmd package uninstall",
            "am force-stop",
            "am kill",
            "settings put",
            "settings delete",
            "settings reset",
            "svc ",
        ];

        public static bool ContainsShellMetacharacters(List<string> tokens) =>
            tokens.Any(static token => token.IndexOfAny([';', '|', '&', '>', '<', '$', '`', '\r', '\n']) >= 0);

        public static bool IsDestructive(List<string> tokens)
        {
            if (tokens.Count == 0)
            {
                return false;
            }

            var command = tokens[0];
            if (DestructiveCommands.Contains(command))
            {
                return true;
            }

            var text = string.Join(' ', tokens);
            return DestructivePrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static class ShellTokens
    {
        public static List<string> Parse(string command)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            char quote = '\0';
            var hasToken = false;
            for (var index = 0; index < command.Length; index++)
            {
                var character = command[index];
                if (quote != '\0')
                {
                    if (character == quote)
                    {
                        quote = '\0';
                        continue;
                    }

                    if (character == '\\' && index + 1 < command.Length && quote == '"')
                    {
                        current.Append(command[++index]);
                        continue;
                    }

                    current.Append(character);
                    continue;
                }

                if (character is '"' or '\'')
                {
                    quote = character;
                    hasToken = true;
                    continue;
                }

                if (char.IsWhiteSpace(character))
                {
                    if (hasToken || current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }

                    continue;
                }

                if (character == '\\' && index + 1 < command.Length)
                {
                    current.Append(command[++index]);
                    hasToken = true;
                    continue;
                }

                current.Append(character);
                hasToken = true;
            }

            if (hasToken || current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return tokens;
        }
    }
}

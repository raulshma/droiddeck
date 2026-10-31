using System.Globalization;
using System.Text.RegularExpressions;
using DroidDeck.Adb.Parsing;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Commands;

/// <summary>Executes adb install, launch, and shell commands.</summary>
public sealed partial class AdbDeviceCommandService : IDeviceCommandService
{
    private readonly Func<string> _adbPathProvider;
    private readonly AdbCommandExecutor _adb;

    /// <summary>Creates a command service with a provider for the current adb executable.</summary>
    public AdbDeviceCommandService(
        IProcessRunner runner,
        Func<string> adbPathProvider,
        ILogger<AdbDeviceCommandService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
        _adb = new AdbCommandExecutor(
            runner,
            _adbPathProvider,
            logger: logger ?? NullLogger<AdbDeviceCommandService>.Instance);
    }

    /// <inheritdoc />
    public string AdbExecutable => _adbPathProvider();

    /// <inheritdoc />
    public async Task<DeviceCommandResult> InstallAsync(
        string serial,
        IReadOnlyList<string> apkPaths,
        AdbInstallOptions options,
        IProgress<ProcessOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentNullException.ThrowIfNull(apkPaths);
        ArgumentNullException.ThrowIfNull(options);
        if (apkPaths.Count == 0)
        {
            return new DeviceCommandResult
            {
                Succeeded = false,
                Error = "No APK files were supplied.",
                Output = "No APK files were supplied.",
            };
        }

        var arguments = new List<string>();
        arguments.Add(apkPaths.Count > 1 ? "install-multiple" : "install");
        if (options.Replace)
        {
            arguments.Add("-r");
        }

        if (options.TestOnly)
        {
            arguments.Add("-t");
        }

        if (options.GrantPermissions)
        {
            arguments.Add("-g");
        }

        if (options.AllowDowngrade)
        {
            arguments.Add("-d");
        }

        if (!string.IsNullOrWhiteSpace(options.User))
        {
            arguments.AddRange(["--user", options.User]);
        }

        foreach (var extra in options.ExtraArguments)
        {
            arguments.Add(extra);
        }

        arguments.AddRange(apkPaths);
        var result = await RunForDeviceAsync(
            serial,
            arguments,
            TimeSpan.FromMinutes(10),
            output,
            cancellationToken).ConfigureAwait(false);
        result.Succeeded = result.Succeeded &&
                           !result.Output.Contains("INSTALL_FAILED_", StringComparison.Ordinal);
        return result;
    }

    /// <inheritdoc />
    public async Task<DeviceCommandResult> LaunchAsync(
        string serial,
        AdbLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PackageName);
        var arguments = new List<string> { "shell" };
        var activity = NormalizeActivity(options.PackageName, options.Activity);
        if (activity is null)
        {
            arguments.AddRange([
                "monkey",
                "-p", options.PackageName,
                "-c", "android.intent.category.LAUNCHER",
                "1",
            ]);
        }
        else
        {
            arguments.AddRange(["am", "start", "-W"]);
            if (!string.IsNullOrWhiteSpace(options.Action))
            {
                arguments.AddRange(["-a", options.Action]);
            }

            if (!string.IsNullOrWhiteSpace(options.Data))
            {
                arguments.AddRange(["-d", options.Data]);
            }

            arguments.AddRange(["-n", activity]);
            if (options.WaitForDebugger)
            {
                arguments.Add("-D");
            }

            arguments.AddRange(options.ExtraArguments);
        }

        var result = await RunForDeviceAsync(
            serial,
            arguments,
            TimeSpan.FromMinutes(2),
            output: null,
            cancellationToken).ConfigureAwait(false);
        result.LaunchStatus = ParseLaunchStatus(result.Output);
        result.LaunchTime = ParseLaunchTime(result.Output);
        result.Succeeded = result.Succeeded &&
                           !result.Output.Contains("Exception", StringComparison.OrdinalIgnoreCase);
        return result;
    }

    /// <inheritdoc />
    public Task<DeviceCommandResult> ForceStopAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        return RunForDeviceAsync(
            serial,
            ["shell", "am", "force-stop", packageName],
            TimeSpan.FromSeconds(30),
            output: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<DeviceCommandResult> UninstallAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        return RunForDeviceAsync(
            serial,
            ["uninstall", packageName],
            TimeSpan.FromMinutes(2),
            output: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<DeviceCommandResult> ShellAsync(
        string serial,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var command = new List<string> { "shell" };
        command.AddRange(arguments);
        return RunForDeviceAsync(
            serial,
            command,
            TimeSpan.FromSeconds(60),
            output: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<DeviceCommandResult> ReverseAsync(
        string serial,
        int devicePort,
        int hostPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        return RunForDeviceAsync(
            serial,
            ["reverse", $"tcp:{devicePort}", $"tcp:{hostPort}"],
            TimeSpan.FromSeconds(30),
            output: null,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> ResolveActivityAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        var result = await RunForDeviceAsync(
            serial,
            ["shell", "cmd", "package", "resolve-activity", "--brief", packageName],
            TimeSpan.FromSeconds(30),
            output: null,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        foreach (var line in result.Output.Split('\n').Reverse())
        {
            var trimmed = line.Trim();
            if (trimmed.Contains('/', StringComparison.Ordinal) && !trimmed.Contains(' ', StringComparison.Ordinal))
            {
                return trimmed;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<int?> GetPackageUidAsync(
        string serial,
        string packageName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        var result = await RunForDeviceAsync(
            serial,
            ["shell", "cmd", "package", "list", "packages", "-U", packageName],
            TimeSpan.FromSeconds(30),
            output: null,
            cancellationToken).ConfigureAwait(false);
        var match = UidRegex().Match(result.Output);
        if (match.Success && int.TryParse(match.Groups["uid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
        {
            return uid;
        }

        var dumpsys = await RunForDeviceAsync(
            serial,
            ["shell", "dumpsys", "package", packageName],
            TimeSpan.FromSeconds(30),
            output: null,
            cancellationToken).ConfigureAwait(false);
        var dumpsysMatch = DumpsysUidRegex().Match(dumpsys.Output);
        return dumpsysMatch.Success &&
               int.TryParse(dumpsysMatch.Groups["uid"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dumpsysUid)
            ? dumpsysUid
            : null;
    }

    /// <inheritdoc />
    public async Task<long?> GetFreeStorageAsync(string serial, CancellationToken cancellationToken = default)
    {
        var result = await RunForDeviceAsync(
            serial,
            ["shell", "df", "-k", "/data"],
            TimeSpan.FromSeconds(30),
            output: null,
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        var rows = DfOutput.Parse(result.Output);
        // Prefer /data, but fall back to the last parsed row so single-row output keeps working.
        var row = DfOutput.FindMountPoint(rows, DfOutput.DataMountPoint) ?? DfOutput.FindLast(rows);
        return row?.AvailableBytes;
    }

    /// <inheritdoc />
    public Task<DeviceCommandResult> ClearLogcatAsync(string serial, CancellationToken cancellationToken = default) =>
        RunForDeviceAsync(
            serial,
            ["logcat", "-c"],
            TimeSpan.FromSeconds(30),
            output: null,
            cancellationToken);

    private async Task<DeviceCommandResult> RunForDeviceAsync(
        string serial,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        IProgress<ProcessOutputLine>? output,
        CancellationToken cancellationToken)
    {
        var options = new AdbCommandOptions
        {
            Output = output,
            MaxOutputCharacters = 2 * 1024 * 1024,
        };
        var result = await _adb
            .RunForDeviceAsync(serial, arguments, timeout, options, cancellationToken)
            .ConfigureAwait(false);

        return new DeviceCommandResult
        {
            Succeeded = result.Succeeded,
            CommandLine = result.CommandLine,
            Output = result.CombinedOutput,
            Error = result.StartError ?? string.Empty,
            ExitCode = result.ExitCode,
            TimedOut = result.TimedOut,
            WasCanceled = result.WasCanceled,
            StartError = result.StartError,
        };
    }

    private static string? NormalizeActivity(string packageName, string? activity)
    {
        if (string.IsNullOrWhiteSpace(activity))
        {
            return null;
        }

        var value = activity.Trim();
        if (value.Contains('/', StringComparison.Ordinal))
        {
            return value;
        }

        return value.StartsWith('.') ? packageName + value : packageName + "/" + value;
    }

    private static string? ParseLaunchStatus(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Status:", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed["Status:".Length..].Trim();
            }
        }

        return null;
    }

    private static TimeSpan? ParseLaunchTime(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("TotalTime:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(trimmed["TotalTime:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
            {
                return TimeSpan.FromMilliseconds(milliseconds);
            }
        }

        return null;
    }

    [GeneratedRegex(@"uid:(?<uid>\d+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UidRegex();

    [GeneratedRegex(@"userId=(?<uid>\d+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DumpsysUidRegex();
}

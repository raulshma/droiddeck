using System.ComponentModel;
using System.Globalization;
using System.Text;
using DroidDeck.Adb.Commands;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Emulators;
using DroidDeck.Core.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Emulators;

/// <summary>
/// Manages Android virtual devices through the emulator executable and adb. Long-running
/// emulator processes are started detached so the UI stays responsive.
/// </summary>
public sealed class EmulatorService : IEmulatorService, IDisposable
{
    private const string MissingEmulatorGuidance =
        "Install the Android Emulator from Android Studio's SDK Manager (SDK Tools > Android Emulator), " +
        "or set the emulator path in Settings > Tools.";

    private readonly IProcessRunner _runner;
    private readonly IProcessLauncher _launcher;
    private readonly Func<string?> _emulatorPathProvider;
    private readonly Func<string> _adbPathProvider;
    private readonly Func<string?> _avdHomeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EmulatorService> _logger;
    private readonly AdbCommandExecutor _adb;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _starting = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _processIds = new(StringComparer.OrdinalIgnoreCase);
    private List<EmulatorAvdInfo> _avds = [];
    private string? _availabilityError;

    /// <summary>Creates an emulator service.</summary>
    public EmulatorService(
        IProcessRunner runner,
        IProcessLauncher launcher,
        Func<string?> emulatorPathProvider,
        Func<string> adbPathProvider,
        Func<string?>? avdHomeProvider = null,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null,
        ILogger<EmulatorService>? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _emulatorPathProvider = emulatorPathProvider ?? throw new ArgumentNullException(nameof(emulatorPathProvider));
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
        _avdHomeProvider = avdHomeProvider ?? (static () => null);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<EmulatorService>.Instance;
        _adb = new AdbCommandExecutor(_runner, _adbPathProvider, _timeProvider, _logger);
    }

    /// <inheritdoc />
    public IReadOnlyList<EmulatorAvdInfo> Avds
    {
        get
        {
            lock (_gate)
            {
                return _avds.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public string? AvailabilityError
    {
        get
        {
            lock (_gate)
            {
                return _availabilityError;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? AvdsChanged;

    /// <summary>Releases the operation lock.</summary>
    public void Dispose() => _operationLock.Dispose();

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmulatorAvdInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<EmulatorOperationResult> LaunchAsync(
        string avdName,
        EmulatorLaunchOptions options,
        IProgress<EmulatorProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(avdName);
        ArgumentNullException.ThrowIfNull(options);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var executable = ResolveEmulator();
            if (executable is null)
            {
                return Failure(MissingEmulatorGuidance, MissingEmulatorGuidance);
            }

            var avds = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            var avd = avds.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, avdName, StringComparison.OrdinalIgnoreCase));
            if (avd is null)
            {
                return Failure(
                    $"The AVD \"{avdName}\" was not found.",
                    "Create the AVD in Android Studio's Device Manager, then refresh the list.");
            }

            if (avd.IsRunning)
            {
                return Success($"{avd.Name} is already running.", commandLine: null);
            }

            var arguments = new List<string>
            {
                "-avd", avd.Name,
                "-netdelay", "none",
                "-netspeed", "full",
                "-no-boot-anim",
            };
            if (!options.ShowWindow)
            {
                arguments.Add("-no-window");
            }

            if (options.ColdBoot)
            {
                arguments.Add("-no-snapshot-load");
            }

            if (options.WipeData)
            {
                arguments.Add("-wipe-data");
            }

            progress?.Report(new EmulatorProgress($"Starting {avd.Name}...", 5));
            var request = new ProcessRequest
            {
                FileName = executable,
                Arguments = arguments,
                UseJobObject = false,
            };

            LaunchedProcess launched;
            try
            {
                launched = _launcher.Start(request);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                _logger.LogError(exception, "The emulator process for {Avd} could not be started.", avd.Name);
                return Failure(
                    $"The emulator could not be started: {exception.Message}",
                    MissingEmulatorGuidance);
            }

            lock (_gate)
            {
                _processIds[avd.Name] = launched.ProcessId;
                _starting[avd.Name] = _timeProvider.GetUtcNow();
            }

            RaiseChanged();

            var boot = await WaitForBootAsync(avd.Name, options.BootTimeout, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!boot.Succeeded)
            {
                return boot with { CommandLine = launched.CommandLine };
            }

            progress?.Report(new EmulatorProgress($"{avd.Name} is ready.", 100));
            await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return boot with { CommandLine = launched.CommandLine };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<EmulatorOperationResult> SaveSnapshotAsync(
        string avdName,
        string snapshotName,
        CancellationToken cancellationToken = default)
    {
        return await RunSnapshotCommandAsync(avdName, snapshotName, save: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EmulatorOperationResult> LoadSnapshotAsync(
        string avdName,
        string snapshotName,
        CancellationToken cancellationToken = default)
    {
        return await RunSnapshotCommandAsync(avdName, snapshotName, save: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EmulatorOperationResult> StopAsync(
        string avdName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(avdName);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var avds = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            var avd = avds.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, avdName, StringComparison.OrdinalIgnoreCase));
            var serial = avd?.Serial;
            int processId;
            lock (_gate)
            {
                _processIds.TryGetValue(avdName, out processId);
            }

            if (string.IsNullOrWhiteSpace(serial) && processId <= 0)
            {
                return Success($"{avdName} is not running.", commandLine: null);
            }

            string? commandLine = null;
            if (!string.IsNullOrWhiteSpace(serial))
            {
                var result = await _adb
                    .RunForDeviceAsync(
                        serial,
                        ["emu", "kill"],
                        TimeSpan.FromSeconds(30),
                        cancellationToken)
                    .ConfigureAwait(false);
                commandLine = result.CommandLine;
                if (!result.Succeeded &&
                    !result.CombinedOutput.Contains("OK", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "adb emu kill failed for {Serial}: {Output}",
                        serial,
                        result.CombinedOutput);
                }

                await WaitForDisconnectAsync(serial, cancellationToken).ConfigureAwait(false);
            }

            if (processId > 0)
            {
                _launcher.Terminate(processId);
                lock (_gate)
                {
                    _processIds.Remove(avdName);
                }
            }

            lock (_gate)
            {
                _starting.Remove(avdName);
            }

            await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return Success($"{avdName} stopped.", commandLine);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<EmulatorOperationResult> WipeDataAsync(
        string avdName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(avdName);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var avds = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            var avd = avds.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, avdName, StringComparison.OrdinalIgnoreCase));
            if (avd is null)
            {
                return Failure(
                    $"The AVD \"{avdName}\" was not found.",
                    "Create the AVD in Android Studio's Device Manager, then refresh the list.");
            }

            if (avd.IsRunning)
            {
                return Failure(
                    $"{avdName} is running.",
                    "Stop the emulator before wiping its data so the files are not recreated on exit.");
            }

            var avdDirectory = FindAvdDirectory(avd.Name);
            if (avdDirectory is null)
            {
                return Failure(
                    $"The data directory for \"{avdName}\" could not be located.",
                    "Check ANDROID_AVD_HOME or the AVD configuration in Android Studio's Device Manager.");
            }

            var targets = new List<string>
            {
                Path.Combine(avdDirectory, "userdata-qemu.img"),
                Path.Combine(avdDirectory, "userdata-qemu.img.qcow2"),
                Path.Combine(avdDirectory, "userdata.img"),
                Path.Combine(avdDirectory, "cache.img"),
                Path.Combine(avdDirectory, "cache.img.qcow2"),
                Path.Combine(avdDirectory, "sdcard.img"),
                Path.Combine(avdDirectory, "encryptionkey.img"),
                Path.Combine(avdDirectory, "snapshots"),
            };

            var removed = 0;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (Directory.Exists(target))
                    {
                        Directory.Delete(target, recursive: true);
                        removed++;
                    }
                    else if (File.Exists(target))
                    {
                        File.Delete(target);
                        removed++;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(exception, "Could not delete {Path}.", target);
                    return Failure(
                        $"The data file \"{Path.GetFileName(target)}\" could not be deleted: {exception.Message}",
                        "Close Android Studio and any running emulator, then retry.");
                }
            }

            await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return Success(
                removed == 0
                    ? $"{avdName} had no user data to wipe."
                    : $"Wiped {removed} user-data item(s) for {avdName}.",
                commandLine: null);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<EmulatorOperationResult> RunSnapshotCommandAsync(
        string avdName,
        string snapshotName,
        bool save,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(avdName);
        ValidateSnapshotName(snapshotName);

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var avds = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            var avd = avds.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, avdName, StringComparison.OrdinalIgnoreCase));
            if (avd is null)
            {
                return Failure(
                    $"The AVD \"{avdName}\" was not found.",
                    "Create the AVD in Android Studio's Device Manager, then refresh the list.");
            }

            if (string.IsNullOrWhiteSpace(avd.Serial))
            {
                return Failure(
                    $"{avdName} is not running.",
                    "Start the emulator before saving or loading a snapshot.");
            }

            var verb = save ? "save" : "load";
            var result = await _adb
                .RunForDeviceAsync(
                    avd.Serial,
                    ["emu", "avd", "snapshot", verb, snapshotName],
                    TimeSpan.FromSeconds(90),
                    cancellationToken)
                .ConfigureAwait(false);
            // The emulator console reports snapshot failures as KO lines while
            // still exiting with code 0, so the output stays part of the verdict.
            var succeeded = result.Succeeded &&
                            !result.CombinedOutput.Contains("KO", StringComparison.Ordinal) &&
                            !result.CombinedOutput.Contains("error", StringComparison.OrdinalIgnoreCase);
            await RefreshCoreAsync(CancellationToken.None).ConfigureAwait(false);
            return new EmulatorOperationResult
            {
                Succeeded = succeeded,
                Message = succeeded
                    ? $"Snapshot \"{snapshotName}\" {(save ? "saved" : "loaded")} for {avdName}."
                    : $"The snapshot command failed: {Truncate(result.CombinedOutput)}",
                CommandLine = result.CommandLine,
                Guidance = succeeded
                    ? null
                    : "Check that the emulator is fully booted and that snapshots are enabled in the AVD settings.",
            };
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task<IReadOnlyList<EmulatorAvdInfo>> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveEmulator();
        if (executable is null)
        {
            return SetAvailability(MissingEmulatorGuidance);
        }

        ProcessResult listResult;
        try
        {
            listResult = await _runner.RunAsync(
                new ProcessRequest
                {
                    FileName = executable,
                    Arguments = ["-list-avds"],
                    Timeout = TimeSpan.FromSeconds(20),
                },
                output: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _logger.LogError(exception, "The emulator executable could not be run.");
            return SetAvailability(MissingEmulatorGuidance);
        }

        if (listResult.ExitCode != 0)
        {
            return SetAvailability(
                $"{MissingEmulatorGuidance} (emulator exited with code {listResult.ExitCode}: {Truncate(listResult.CombinedOutput)})");
        }

        SetAvailability(null);
        var names = listResult.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !line.StartsWith("INFO", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var running = await GetRunningAvdsAsync(cancellationToken).ConfigureAwait(false);
        var avds = new List<EmulatorAvdInfo>(names.Length);
        foreach (var name in names)
        {
            var configuration = ReadAvdConfiguration(name);
            var hasRunning = running.TryGetValue(name, out var runningInfo);
            var starting = false;
            lock (_gate)
            {
                starting = _starting.ContainsKey(name);
            }

            var state = !hasRunning
                ? starting ? EmulatorAvdState.Starting : EmulatorAvdState.Stopped
                : runningInfo.Booted
                    ? EmulatorAvdState.Running
                    : EmulatorAvdState.Starting;

            if (state == EmulatorAvdState.Running)
            {
                lock (_gate)
                {
                    _starting.Remove(name);
                }
            }

            avds.Add(new EmulatorAvdInfo
            {
                Name = name,
                Target = configuration.Target,
                ApiLevel = configuration.ApiLevel,
                Abi = configuration.Abi,
                DeviceProfile = configuration.DeviceProfile,
                Serial = hasRunning ? runningInfo.Serial : null,
                State = state,
                Snapshots = configuration.Snapshots,
            });
        }

        lock (_gate)
        {
            _avds = avds;
        }

        RaiseChanged();
        return avds;

        IReadOnlyList<EmulatorAvdInfo> SetAvailability(string? error)
        {
            lock (_gate)
            {
                _availabilityError = error;
                _avds = [];
            }

            RaiseChanged();
            return [];
        }
    }

    private async Task<EmulatorOperationResult> WaitForBootAsync(
        string avdName,
        TimeSpan timeout,
        IProgress<EmulatorProgress>? progress,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + timeout;
        var reportedConnect = false;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var running = await GetRunningAvdsAsync(cancellationToken).ConfigureAwait(false);
            if (running.TryGetValue(avdName, out var info))
            {
                if (!reportedConnect)
                {
                    progress?.Report(new EmulatorProgress(
                        $"{avdName} is connected as {info.Serial}; waiting for Android to finish booting.",
                        60));
                    reportedConnect = true;
                }

                if (info.Booted)
                {
                    lock (_gate)
                    {
                        _starting.Remove(avdName);
                    }

                    return Success($"{avdName} booted and is ready.", commandLine: null);
                }
            }

            progress?.Report(new EmulatorProgress($"Waiting for {avdName} to boot...", 35));
            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return Failure(
            $"{avdName} did not finish booting within {timeout.TotalSeconds:0} seconds.",
            "The emulator window may still be starting. Check it for errors, " +
            "verify hardware acceleration is enabled, or stop the emulator and retry with a cold boot.");
    }

    private async Task<Dictionary<string, (string Serial, bool Booted)>> GetRunningAvdsAsync(
        CancellationToken cancellationToken)
    {
        var result = await _adb
            .RunAsync(["devices"], TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        var running = new Dictionary<string, (string Serial, bool Booted)>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in AdbDeviceParser.Parse(result.StandardOutput))
        {
            if (!device.Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) ||
                device.State != DeviceState.Device)
            {
                continue;
            }

            var serial = device.Serial;
            var avd = await GetAvdNameAsync(serial, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(avd))
            {
                continue;
            }

            var booted = await IsBootedAsync(serial, cancellationToken).ConfigureAwait(false);
            running[avd] = (serial, booted);
        }

        return running;
    }

    private async Task<string?> GetAvdNameAsync(string serial, CancellationToken cancellationToken)
    {
        var result = await _adb
            .RunForDeviceAsync(
                serial,
                ["emu", "avd", "name"],
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static line => !string.Equals(line, "OK", StringComparison.Ordinal));
    }

    private async Task<bool> IsBootedAsync(string serial, CancellationToken cancellationToken)
    {
        var result = await _adb
            .RunForDeviceAsync(
                serial,
                ["shell", "getprop", "sys.boot_completed"],
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded &&
               string.Equals(result.StandardOutput.Trim(), "1", StringComparison.Ordinal);
    }

    private async Task WaitForDisconnectAsync(string serial, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _adb
                .RunAsync(["devices"], TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
            var stillPresent = AdbDeviceParser
                .Parse(result.StandardOutput)
                .Any(device => string.Equals(device.Serial, serial, StringComparison.Ordinal));
            if (!stillPresent)
            {
                return;
            }

            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private string? ResolveEmulator()
    {
        var configured = _emulatorPathProvider();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (Path.IsPathRooted(configured) ||
                configured.Contains(Path.DirectorySeparatorChar) ||
                configured.Contains(Path.AltDirectorySeparatorChar))
            {
                return File.Exists(configured) ? configured : null;
            }

            return configured;
        }

        foreach (var sdk in EnumerateSdkRoots())
        {
            var candidate = Path.Combine(sdk, "emulator", "emulator.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "emulator";
    }

    private IEnumerable<string> EnumerateSdkRoots()
    {
        var adb = _adbPathProvider();
        if (!string.IsNullOrWhiteSpace(adb) && Path.IsPathRooted(adb))
        {
            var platformTools = Path.GetDirectoryName(adb);
            var sdk = platformTools is null ? null : Path.GetDirectoryName(platformTools);
            if (!string.IsNullOrWhiteSpace(sdk) && Directory.Exists(sdk))
            {
                yield return sdk;
            }
        }

        foreach (var variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                yield return value;
            }
        }
    }

    private AvdConfiguration ReadAvdConfiguration(string name)
    {
        var avdDirectory = FindAvdDirectory(name);
        if (avdDirectory is null)
        {
            return new AvdConfiguration(null, null, null, null, []);
        }

        var values = ReadIni(Path.Combine(avdDirectory, "config.ini"));
        var target = values.GetValueOrDefault("target");
        var image = values.GetValueOrDefault("image.sysdir.1");
        var api = ParseApiLevel(target) ?? ParseApiLevel(image);
        var snapshots = ReadSnapshots(avdDirectory);
        return new AvdConfiguration(
            target,
            api,
            values.GetValueOrDefault("abi.type") ?? ParseImageAbi(image),
            values.GetValueOrDefault("hw.device.name"),
            snapshots);
    }

    private string? FindAvdDirectory(string name)
    {
        var home = ResolveAvdHome();
        if (home is null)
        {
            return null;
        }

        var direct = Path.Combine(home, name + ".avd");
        if (Directory.Exists(direct))
        {
            return direct;
        }

        var iniPath = Path.Combine(home, name + ".ini");
        if (!File.Exists(iniPath))
        {
            return null;
        }

        var ini = ReadIni(iniPath);
        var path = ini.GetValueOrDefault("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(home, path);
        }

        return Directory.Exists(path) ? path : null;
    }

    private string? ResolveAvdHome()
    {
        var overridden = _avdHomeProvider();
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        var avdHome = Environment.GetEnvironmentVariable("ANDROID_AVD_HOME");
        if (!string.IsNullOrWhiteSpace(avdHome))
        {
            return avdHome;
        }

        var userHome = Environment.GetEnvironmentVariable("ANDROID_USER_HOME");
        if (!string.IsNullOrWhiteSpace(userHome))
        {
            return Path.Combine(userHome, "avd");
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? null
            : Path.Combine(profile, ".android", "avd");
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string[] ReadSnapshots(string avdDirectory)
    {
        var directory = Path.Combine(avdDirectory, "snapshots");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(directory)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static int? ParseApiLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var marker = value.IndexOf("android-", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var digits = new StringBuilder();
        for (var index = marker + "android-".Length; index < value.Length; index++)
        {
            var character = value[index];
            if (!char.IsAsciiDigit(character))
            {
                break;
            }

            digits.Append(character);
        }

        return digits.Length > 0 &&
               int.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var api)
            ? api
            : null;
    }

    private static string? ParseImageAbi(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        var parts = image.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts[^1];
    }

    private static void ValidateSnapshotName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 64 ||
            name.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Snapshot names may only contain letters, digits, dashes, and underscores (maximum 64 characters).",
                nameof(name));
        }
    }

    private static string Truncate(string? text)
    {
        const int limit = 300;
        if (string.IsNullOrWhiteSpace(text))
        {
            return "no output";
        }

        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= limit ? singleLine : singleLine[..limit] + "...";
    }

    private static EmulatorOperationResult Success(string message, string? commandLine) => new()
    {
        Succeeded = true,
        Message = message,
        CommandLine = commandLine,
    };

    private static EmulatorOperationResult Failure(string message, string? guidance) => new()
    {
        Succeeded = false,
        Message = message,
        Guidance = guidance,
    };

    private void SetAvailability(string? error)
    {
        lock (_gate)
        {
            _availabilityError = error;
        }
    }

    private void RaiseChanged() => AvdsChanged?.Invoke(this, EventArgs.Empty);

    private sealed record AvdConfiguration(
        string? Target,
        int? ApiLevel,
        string? Abi,
        string? DeviceProfile,
        IReadOnlyList<string> Snapshots);
}

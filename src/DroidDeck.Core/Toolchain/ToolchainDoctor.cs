using System.Text.Json;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Processes;
using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Toolchain;

/// <summary>
/// Detects Android and cross-platform toolchains, honoring user overrides and
/// caching the last snapshot so the shell can render immediately.
/// </summary>
public sealed class ToolchainDoctor : IToolchainService, IDisposable
{
    private const string AdbGuidance =
        "Install Android SDK Platform-Tools through Android Studio's SDK Manager, or set the adb path in Settings > Tools.";

    private const string Aapt2Guidance =
        "Install Android SDK Build-Tools through Android Studio's SDK Manager, or set the aapt2 path in Settings > Tools.";

    private const string EmulatorGuidance =
        "Install the Android Emulator package through Android Studio's SDK Manager, or set the emulator path in Settings > Tools.";

    private const string JavaGuidance =
        "Install a JDK 17 or newer and set JAVA_HOME, or set the JDK path in Settings > Tools.";

    private const string GradleGuidance =
        "Use a project Gradle wrapper or install Gradle and add it to PATH, then set the Gradle path in Settings > Tools if needed.";

    private const string FlutterGuidance =
        "Install Flutter and add its bin folder to PATH, or set the Flutter path in Settings > Tools.";

    private const string DartGuidance =
        "Install the Flutter SDK (which bundles Dart) or the Dart SDK and add it to PATH, or set the Dart path in Settings > Tools.";

    private const string NodeGuidance =
        "Install Node.js and add it to PATH, or set the Node.js path in Settings > Tools.";

    private const string ScrcpyGuidance =
        "Install scrcpy and add it to PATH to use screen mirroring, or set the scrcpy path in Settings > Tools.";

    private const string UnityGuidance =
        "Install Unity Hub and an editor with Android Build Support, or set the Unity path in Settings > Tools.";

    private const string DotNetGuidance =
        "Install the .NET SDK and add it to PATH, or set the dotnet path in Settings > Tools.";

    private const string MauiWorkloadGuidance =
        "Install the .NET MAUI Android workload by running \"dotnet workload install maui-android\", then restart DroidDeck.";

    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FlutterProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] DartExecutableNames = ["dart.exe", "dart.cmd", "dart.bat"];

    private readonly AppPaths _paths;
    private readonly IConfigurationService _configuration;
    private readonly IProcessRunner _runner;
    private readonly ToolchainEnvironment _environment;
    private readonly ILogger<ToolchainDoctor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ToolchainSnapshot? _cached;

    /// <summary>Creates a toolchain doctor.</summary>
    public ToolchainDoctor(
        AppPaths paths,
        IConfigurationService configuration,
        IProcessRunner runner,
        ToolchainEnvironment? environment = null,
        ILogger<ToolchainDoctor>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _environment = environment ?? ToolchainEnvironment.CreateSystem();
        _logger = logger ?? NullLogger<ToolchainDoctor>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ToolchainSnapshot? CachedSnapshot => _cached;

    /// <inheritdoc />
    public event EventHandler<ToolchainSnapshot>? SnapshotChanged;

    /// <inheritdoc />
    public async Task<ToolchainSnapshot?> LoadCachedAsync(CancellationToken cancellationToken = default)
    {
        var path = _paths.ToolchainCacheFile;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize(json, ToolchainJsonContext.Default.ToolchainCacheDocument);
            if (document is null || document.SchemaVersion != 1)
            {
                return null;
            }

            var snapshot = document.ToSnapshot();
            _cached = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "The cached toolchain snapshot could not be read.");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ToolchainSnapshot> CheckAsync(CancellationToken cancellationToken = default)
    {
        ToolchainSnapshot snapshot;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tools = _configuration.Current.Settings.Tools;
            var androidSdk = ResolveAndroidSdk(tools.AndroidSdkPath);
            var sdkPath = androidSdk.IsAvailable ? androidSdk.Path : null;

            var dotnetTask = ProbeDotNetAsync(tools.DotNetPath, cancellationToken);
            var probes = new Task<ToolInfo>[]
            {
                Task.FromResult(androidSdk),
                ProbeAdbAsync(tools.AdbPath, sdkPath, cancellationToken),
                ProbeAapt2Async(tools.Aapt2Path, sdkPath, cancellationToken),
                ProbeEmulatorAsync(tools.EmulatorPath, sdkPath, cancellationToken),
                ProbeJavaAsync(tools.JavaHome, cancellationToken),
                ProbeGradleAsync(tools.GradlePath, cancellationToken),
                ProbeFlutterAsync(tools.FlutterPath, cancellationToken),
                ProbeDartAsync(tools.DartPath, tools.FlutterPath, cancellationToken),
                ProbeNodeAsync(tools.NodePath, cancellationToken),
                ProbeNpmAsync(tools.NodePath, cancellationToken),
                ProbeNpxAsync(tools.NodePath, cancellationToken),
                ProbeScrcpyToolAsync(tools.ScrcpyPath, cancellationToken),
                Task.FromResult(ProbeUnity(tools.UnityPath)),
                dotnetTask,
                ProbeMauiAndroidWorkloadAsync(dotnetTask, cancellationToken),
            };

            var results = await Task.WhenAll(probes).ConfigureAwait(false);
            snapshot = new ToolchainSnapshot(results, _timeProvider.GetUtcNow(), sdkPath);
            _cached = snapshot;
            await SaveCachedAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        SnapshotChanged?.Invoke(this, snapshot);
        return snapshot;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    private ToolInfo ResolveAndroidSdk(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var expanded = TryExpandPath(overridePath);
            if (expanded is not null && Directory.Exists(expanded))
            {
                return SdkInfo(expanded, ToolDetectionSource.Override);
            }

            return new ToolInfo
            {
                Kind = ToolKind.AndroidSdk,
                DisplayName = "Android SDK",
                IsAvailable = false,
                Path = expanded ?? overridePath,
                Source = ToolDetectionSource.Override,
                Error = "The configured Android SDK path does not exist.",
                Guidance = "Update the Android SDK path in Settings > Tools, or clear it to detect the SDK automatically.",
            };
        }

        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var value = _environment.GetVariable(variable);
            var expanded = string.IsNullOrWhiteSpace(value) ? null : TryExpandPath(value);
            if (expanded is not null && Directory.Exists(expanded))
            {
                return SdkInfo(expanded, ToolDetectionSource.Automatic);
            }
        }

        var defaultPath = string.IsNullOrWhiteSpace(_environment.LocalApplicationData)
            ? null
            : Path.Combine(_environment.LocalApplicationData, "Android", "Sdk");
        if (defaultPath is not null && Directory.Exists(defaultPath))
        {
            return SdkInfo(defaultPath, ToolDetectionSource.Automatic);
        }

        return new ToolInfo
        {
            Kind = ToolKind.AndroidSdk,
            DisplayName = "Android SDK",
            IsAvailable = false,
            Source = ToolDetectionSource.Missing,
            Guidance = "Install Android Studio or the Android SDK command-line tools, then set the SDK path in Settings > Tools.",
        };
    }

    private static ToolInfo SdkInfo(string path, ToolDetectionSource source) => new()
    {
        Kind = ToolKind.AndroidSdk,
        DisplayName = "Android SDK",
        IsAvailable = true,
        Path = path,
        Source = source,
    };

    private Task<ToolInfo> ProbeAdbAsync(string? overridePath, string? sdkPath, CancellationToken cancellationToken)
    {
        var extras = new List<string>();
        AddUnderSdk(extras, sdkPath, "platform-tools", "adb");
        return ProbeExecutableAsync(
            ToolKind.Adb,
            "adb",
            overridePath,
            ["adb.exe", "adb.cmd", "adb.bat"],
            ["version"],
            ToolVersionParser.Adb,
            AdbGuidance,
            extras,
            DefaultProbeTimeout,
            cancellationToken);
    }

    private Task<ToolInfo> ProbeAapt2Async(string? overridePath, string? sdkPath, CancellationToken cancellationToken)
    {
        var extras = new List<string>();
        if (sdkPath is not null)
        {
            var latest = FindLatestBuildTool(sdkPath, "aapt2");
            if (latest is not null)
            {
                extras.Add(latest);
            }
        }

        return ProbeExecutableAsync(
            ToolKind.Aapt2,
            "aapt2",
            overridePath,
            ["aapt2.exe", "aapt2.cmd", "aapt2.bat"],
            ["version"],
            ToolVersionParser.LastTokenOfFirstLine,
            Aapt2Guidance,
            extras,
            DefaultProbeTimeout,
            cancellationToken);
    }

    private Task<ToolInfo> ProbeEmulatorAsync(string? overridePath, string? sdkPath, CancellationToken cancellationToken)
    {
        var extras = new List<string>();
        AddUnderSdk(extras, sdkPath, "emulator", "emulator");
        return ProbeExecutableAsync(
            ToolKind.Emulator,
            "Android emulator",
            overridePath,
            ["emulator.exe", "emulator.cmd", "emulator.bat"],
            ["-version"],
            ToolVersionParser.Emulator,
            EmulatorGuidance,
            extras,
            DefaultProbeTimeout,
            cancellationToken);
    }

    private Task<ToolInfo> ProbeJavaAsync(string? javaHome, CancellationToken cancellationToken)
    {
        var extras = new List<string>();
        var javaHomeVariable = _environment.GetVariable("JAVA_HOME");
        AddJavaHome(extras, javaHomeVariable);
        if (!string.IsNullOrWhiteSpace(_environment.ProgramFiles))
        {
            AddJavaHome(extras, Path.Combine(_environment.ProgramFiles, "Android", "Android Studio", "jbr"));
            AddJavaHome(extras, Path.Combine(_environment.ProgramFiles, "Android", "Android Studio Preview", "jbr"));
        }

        if (!string.IsNullOrWhiteSpace(_environment.LocalApplicationData))
        {
            AddJavaHome(extras, Path.Combine(_environment.LocalApplicationData, "Programs", "Android Studio", "jbr"));
        }

        string? overrideJava = null;
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var home = TryExpandPath(javaHome);
            if (home is null || !Directory.Exists(home))
            {
                return Task.FromResult(new ToolInfo
                {
                    Kind = ToolKind.Java,
                    DisplayName = "Java",
                    IsAvailable = false,
                    Path = home ?? javaHome,
                    Source = ToolDetectionSource.Override,
                    Error = "The configured JDK path does not exist.",
                    Guidance = "Update the JDK path in Settings > Tools, or clear it to detect Java automatically.",
                });
            }

            overrideJava = Path.Combine(home, "bin", "java.exe");
        }

        return ProbeExecutableAsync(
            ToolKind.Java,
            "Java",
            overrideJava,
            ["java.exe", "java.cmd", "java.bat"],
            ["-version"],
            ToolVersionParser.Java,
            JavaGuidance,
            extras,
            DefaultProbeTimeout,
            cancellationToken);
    }

    private Task<ToolInfo> ProbeGradleAsync(string? overridePath, CancellationToken cancellationToken) => ProbeExecutableAsync(
        ToolKind.Gradle,
        "Gradle",
        overridePath,
        ["gradle.bat", "gradle.exe", "gradle"],
        ["--version"],
        ToolVersionParser.Gradle,
        GradleGuidance,
        [],
        DefaultProbeTimeout,
        cancellationToken);

    private Task<ToolInfo> ProbeFlutterAsync(string? overridePath, CancellationToken cancellationToken)
    {
        var extras = FlutterBinDirectories()
            .Select(directory => Path.Combine(directory, "flutter.bat"))
            .ToList();

        return ProbeExecutableAsync(
            ToolKind.Flutter,
            "Flutter",
            overridePath,
            ["flutter.bat", "flutter.exe", "flutter"],
            ["--version"],
            ToolVersionParser.Flutter,
            FlutterGuidance,
            extras,
            FlutterProbeTimeout,
            cancellationToken);
    }

    private Task<ToolInfo> ProbeDartAsync(
        string? overridePath,
        string? flutterPath,
        CancellationToken cancellationToken)
    {
        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(flutterPath))
        {
            AddDartCandidates(extras, Path.GetDirectoryName(flutterPath));
        }

        foreach (var directory in FlutterBinDirectories())
        {
            AddDartCandidates(extras, directory);
        }

        return ProbeExecutableAsync(
            ToolKind.Dart,
            "Dart",
            overridePath,
            DartExecutableNames,
            ["--version"],
            ToolVersionParser.Dart,
            DartGuidance,
            extras,
            DefaultProbeTimeout,
            cancellationToken);
    }

    private IEnumerable<string> FlutterBinDirectories()
    {
        var flutterRoot = _environment.GetVariable("FLUTTER_ROOT");
        if (!string.IsNullOrWhiteSpace(flutterRoot))
        {
            yield return Path.Combine(flutterRoot, "bin");
        }

        if (!string.IsNullOrWhiteSpace(_environment.LocalApplicationData))
        {
            yield return Path.Combine(_environment.LocalApplicationData, "flutter", "bin");
        }

        if (!string.IsNullOrWhiteSpace(_environment.UserProfile))
        {
            yield return Path.Combine(_environment.UserProfile, "flutter", "bin");
        }
    }

    private static void AddDartCandidates(List<string> target, string? binDirectory)
    {
        if (string.IsNullOrWhiteSpace(binDirectory))
        {
            return;
        }

        foreach (var name in DartExecutableNames)
        {
            target.Add(Path.Combine(binDirectory, name));
        }

        target.Add(Path.Combine(binDirectory, "cache", "dart-sdk", "bin", "dart.exe"));
    }

    private Task<ToolInfo> ProbeNodeAsync(string? overridePath, CancellationToken cancellationToken) => ProbeExecutableAsync(
        ToolKind.Node,
        "Node.js",
        overridePath,
        ["node.exe", "node.cmd", "node.bat"],
        ["--version"],
        ToolVersionParser.Numeric,
        NodeGuidance,
        [],
        DefaultProbeTimeout,
        cancellationToken);

    private Task<ToolInfo> ProbeNpmAsync(string? nodePath, CancellationToken cancellationToken)
    {
        var extras = new List<string>();
        AddIfPresent(extras, ResolveNodeSibling(nodePath, "npm.cmd"));
        return ProbeExecutableAsync(
            ToolKind.Npm,
            "npm",
            null,
            ["npm.cmd", "npm.exe"],
            ["--version"],
            ToolVersionParser.Numeric,
            NodeGuidance,
            extras,
            DefaultProbeTimeout,
            cancellationToken);
    }

    private Task<ToolInfo> ProbeNpxAsync(string? nodePath, CancellationToken cancellationToken)
    {
        var extras = new List<string>();
        AddIfPresent(extras, ResolveNodeSibling(nodePath, "npx.cmd"));
        return ProbeExecutableAsync(
            ToolKind.Npx,
            "npx",
            null,
            ["npx.cmd", "npx.exe"],
            ["--version"],
            ToolVersionParser.Numeric,
            NodeGuidance,
            extras,
            DefaultProbeTimeout,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ToolInfo> ProbeScrcpyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProbeScrcpyToolAsync(
                _configuration.Current.Settings.Tools.ScrcpyPath,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<ToolInfo> ProbeScrcpyToolAsync(string? overridePath, CancellationToken cancellationToken)
    {
        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(_environment.LocalApplicationData))
        {
            extras.Add(Path.Combine(_environment.LocalApplicationData, "Microsoft", "WinGet", "Links", "scrcpy.exe"));
        }

        return ProbeExecutableAsync(
            ToolKind.Scrcpy,
            "scrcpy",
            overridePath,
            ["scrcpy.exe", "scrcpy.cmd", "scrcpy.bat"],
            ["--version"],
            ToolVersionParser.Scrcpy,
            ScrcpyGuidance,
            extras,
            DefaultProbeTimeout,
            cancellationToken);
    }

    private Task<ToolInfo> ProbeDotNetAsync(string? overridePath, CancellationToken cancellationToken)
    {
        var extras = new List<string>();
        var dotnetRoot = _environment.GetVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            extras.Add(Path.Combine(dotnetRoot, "dotnet.exe"));
        }

        if (!string.IsNullOrWhiteSpace(_environment.ProgramFiles))
        {
            extras.Add(Path.Combine(_environment.ProgramFiles, "dotnet", "dotnet.exe"));
        }

        return ProbeExecutableAsync(
            ToolKind.DotNet,
            ".NET SDK",
            overridePath,
            ["dotnet.exe", "dotnet.cmd", "dotnet.bat"],
            ["--version"],
            ToolVersionParser.Numeric,
            DotNetGuidance,
            extras,
            DefaultProbeTimeout,
            cancellationToken);
    }

    private async Task<ToolInfo> ProbeMauiAndroidWorkloadAsync(
        Task<ToolInfo> dotnetTask,
        CancellationToken cancellationToken)
    {
        var dotnet = await dotnetTask.ConfigureAwait(false);
        if (!dotnet.IsAvailable || string.IsNullOrWhiteSpace(dotnet.Path))
        {
            return new ToolInfo
            {
                Kind = ToolKind.MauiAndroidWorkload,
                DisplayName = ".NET MAUI Android workload",
                IsAvailable = false,
                Source = dotnet.Source,
                Error = "The .NET SDK was not found, so the installed workloads cannot be queried.",
                Guidance = "Install the .NET SDK first, then run \"dotnet workload install maui-android\" from a terminal.",
            };
        }

        var probe = await _runner
            .RunAsync(
                CreateProbeRequest(dotnet.Path, ["workload", "list"], DefaultProbeTimeout),
                output: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (probe.StartError is not null || !probe.Succeeded)
        {
            var error = probe.StartError
                ?? FirstLine(probe.StandardError)
                ?? FirstLine(probe.StandardOutput)
                ?? $"dotnet workload list exited with code {probe.ExitCode}.";
            return new ToolInfo
            {
                Kind = ToolKind.MauiAndroidWorkload,
                DisplayName = ".NET MAUI Android workload",
                IsAvailable = false,
                Path = dotnet.Path,
                Source = dotnet.Source,
                Error = error,
                Guidance = MauiWorkloadGuidance,
            };
        }

        if (!HasMauiAndroidWorkload(probe.CombinedOutput))
        {
            return new ToolInfo
            {
                Kind = ToolKind.MauiAndroidWorkload,
                DisplayName = ".NET MAUI Android workload",
                IsAvailable = false,
                Path = dotnet.Path,
                Source = dotnet.Source,
                Guidance = MauiWorkloadGuidance,
            };
        }

        return new ToolInfo
        {
            Kind = ToolKind.MauiAndroidWorkload,
            DisplayName = ".NET MAUI Android workload",
            IsAvailable = true,
            Path = dotnet.Path,
            Source = dotnet.Source,
        };
    }

    private static bool HasMauiAndroidWorkload(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var firstToken = rawLine
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (firstToken is not null &&
                (firstToken.Equals("maui-android", StringComparison.OrdinalIgnoreCase) ||
                 firstToken.Equals("maui", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private ToolInfo ProbeUnity(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var expanded = TryExpandPath(overridePath);
            if (expanded is not null && File.Exists(expanded))
            {
                return UnityInfo(expanded, ToolDetectionSource.Override, ParseUnityVersion(expanded));
            }

            return new ToolInfo
            {
                Kind = ToolKind.Unity,
                DisplayName = "Unity",
                IsAvailable = false,
                Path = expanded ?? overridePath,
                Source = ToolDetectionSource.Override,
                Error = "The configured Unity path does not exist.",
                Guidance = "Update the Unity path in Settings > Tools, or clear it to detect Unity automatically.",
            };
        }

        var candidates = new List<(string File, string? Version)>();
        if (!string.IsNullOrWhiteSpace(_environment.ProgramFiles))
        {
            var hub = Path.Combine(_environment.ProgramFiles, "Unity", "Hub", "Editor");
            if (Directory.Exists(hub))
            {
                try
                {
                    foreach (var versionDirectory in Directory.EnumerateDirectories(hub))
                    {
                        var editor = Path.Combine(versionDirectory, "Editor", "Unity.exe");
                        if (File.Exists(editor))
                        {
                            candidates.Add((editor, Path.GetFileName(versionDirectory)));
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "Unity Hub editor folders could not be enumerated.");
                }
            }

            var legacy = Path.Combine(_environment.ProgramFiles, "Unity", "Editor", "Unity.exe");
            if (File.Exists(legacy))
            {
                candidates.Add((legacy, null));
            }
        }

        if (candidates.Count == 0)
        {
            return new ToolInfo
            {
                Kind = ToolKind.Unity,
                DisplayName = "Unity",
                IsAvailable = false,
                Source = ToolDetectionSource.Missing,
                Guidance = UnityGuidance,
            };
        }

        var best = candidates
            .Select(candidate => (
                candidate.File,
                candidate.Version,
                Parsed: SemanticVersion.TryParse(candidate.Version, out var version) ? version : null))
            .OrderByDescending(candidate => candidate.Parsed, NullSafeVersionComparer.Instance)
            .ThenByDescending(candidate => candidate.File, StringComparer.OrdinalIgnoreCase)
            .First();

        return UnityInfo(best.File, ToolDetectionSource.Automatic, best.Version);
    }

    private static ToolInfo UnityInfo(string path, ToolDetectionSource source, string? version) => new()
    {
        Kind = ToolKind.Unity,
        DisplayName = "Unity",
        IsAvailable = true,
        Path = path,
        Version = version,
        Source = source,
    };

    private async Task<ToolInfo> ProbeExecutableAsync(
        ToolKind kind,
        string displayName,
        string? overridePath,
        string[] pathNames,
        string[] versionArguments,
        Func<string, string?> versionParser,
        string guidance,
        IReadOnlyList<string> extraCandidates,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string? candidate;
        var source = ToolDetectionSource.Automatic;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var expanded = TryExpandPath(overridePath);
            if (expanded is null || !File.Exists(expanded))
            {
                return new ToolInfo
                {
                    Kind = kind,
                    DisplayName = displayName,
                    IsAvailable = false,
                    Path = expanded ?? overridePath,
                    Source = ToolDetectionSource.Override,
                    Error = "The configured path does not exist.",
                    Guidance = $"Update the {displayName} path in Settings > Tools, or clear it to detect the tool automatically.",
                };
            }

            candidate = expanded;
            source = ToolDetectionSource.Override;
        }
        else
        {
            candidate = FindExecutable(pathNames, extraCandidates);
        }

        if (candidate is null)
        {
            return new ToolInfo
            {
                Kind = kind,
                DisplayName = displayName,
                IsAvailable = false,
                Source = ToolDetectionSource.Missing,
                Guidance = guidance,
            };
        }

        var probe = await _runner.RunAsync(
                CreateProbeRequest(candidate, versionArguments, timeout),
                output: null,
                cancellationToken)
            .ConfigureAwait(false);

        var version = probe.StartError is null ? versionParser(probe.CombinedOutput) : null;
        if (probe.StartError is not null || (!probe.Succeeded && version is null))
        {
            var error = probe.StartError
                ?? FirstLine(probe.StandardError)
                ?? FirstLine(probe.StandardOutput)
                ?? $"The tool exited with code {probe.ExitCode}.";
            _logger.LogWarning("{Tool} could not be queried: {Error}", displayName, error);
            return new ToolInfo
            {
                Kind = kind,
                DisplayName = displayName,
                IsAvailable = false,
                Path = candidate,
                Source = source,
                Error = error,
                Guidance = $"The {displayName} executable was found but could not be queried. Verify the installation. {guidance}",
            };
        }

        return new ToolInfo
        {
            Kind = kind,
            DisplayName = displayName,
            IsAvailable = true,
            Path = candidate,
            Version = version,
            Source = source,
        };
    }

    private async Task SaveCachedAsync(ToolchainSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            _paths.EnsureCreated();
            var json = JsonSerializer.Serialize(
                ToolchainCacheDocument.FromSnapshot(snapshot),
                ToolchainJsonContext.Default.ToolchainCacheDocument);
            await AtomicFile.WriteAllTextAsync(_paths.ToolchainCacheFile, json, keepBackup: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The toolchain snapshot could not be cached.");
        }
    }

    private string? FindExecutable(string[] pathNames, IReadOnlyList<string> extraCandidates)
    {
        foreach (var extra in extraCandidates)
        {
            var expanded = TryExpandPath(extra);
            if (expanded is not null && File.Exists(expanded))
            {
                return expanded;
            }
        }

        foreach (var directory in _environment.PathDirectories)
        {
            foreach (var name in pathNames)
            {
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.Combine(directory, name));
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static void AddUnderSdk(List<string> target, string? sdkPath, string folder, string fileBase)
    {
        if (sdkPath is null)
        {
            return;
        }

        var directory = Path.Combine(sdkPath, folder);
        foreach (var extension in new[] { "exe", "cmd", "bat" })
        {
            target.Add(Path.Combine(directory, fileBase + "." + extension));
        }
    }

    private static void AddJavaHome(List<string> target, string? javaHome)
    {
        if (string.IsNullOrWhiteSpace(javaHome))
        {
            return;
        }

        target.Add(Path.Combine(javaHome, "bin", "java.exe"));
    }

    private static void AddIfPresent(List<string> target, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            target.Add(path);
        }
    }

    private static string? ResolveNodeSibling(string? nodePath, string fileName)
    {
        if (string.IsNullOrWhiteSpace(nodePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(nodePath);
        return string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, fileName);
    }

    private static string? FindLatestBuildTool(string sdkPath, string fileBase)
    {
        var buildTools = Path.Combine(sdkPath, "build-tools");
        if (!Directory.Exists(buildTools))
        {
            return null;
        }

        try
        {
            var candidates = new List<(string File, SemanticVersion? Version)>();
            foreach (var directory in Directory.EnumerateDirectories(buildTools))
            {
                var file = FindInDirectory(directory, fileBase);
                if (file is not null)
                {
                    var name = Path.GetFileName(directory);
                    candidates.Add((file, SemanticVersion.TryParse(name, out var version) ? version : null));
                }
            }

            return candidates
                .OrderByDescending(candidate => candidate.Version, NullSafeVersionComparer.Instance)
                .ThenByDescending(candidate => candidate.File, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.File)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindInDirectory(string directory, string fileBase)
    {
        foreach (var extension in new[] { "exe", "cmd", "bat" })
        {
            var candidate = Path.Combine(directory, fileBase + "." + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static ProcessRequest CreateProbeRequest(
        string executable,
        string[] arguments,
        TimeSpan timeout)
    {
        var extension = Path.GetExtension(executable);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var command = new List<string>(arguments.Length + 3) { "/d", "/c", executable };
            command.AddRange(arguments);
            return new ProcessRequest
            {
                FileName = "cmd.exe",
                Arguments = command,
                Timeout = timeout,
                MaxOutputCharacters = 64 * 1024,
            };
        }

        return new ProcessRequest
        {
            FileName = executable,
            Arguments = arguments,
            Timeout = timeout,
            MaxOutputCharacters = 64 * 1024,
        };
    }

    private static string? TryExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Path.GetFullPath(expanded);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ParseUnityVersion(string executablePath)
    {
        var editorDirectory = Path.GetDirectoryName(executablePath);
        var versionDirectory = editorDirectory is null ? null : Path.GetDirectoryName(editorDirectory);
        return versionDirectory is null ? null : Path.GetFileName(versionDirectory);
    }

    private static string? FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var line = text.Split('\n', 2)[0].Trim();
        return line.Length == 0 ? null : line.Length > 300 ? line[..300] : line;
    }

    private sealed class NullSafeVersionComparer : IComparer<SemanticVersion?>
    {
        public static readonly NullSafeVersionComparer Instance = new();

        public int Compare(SemanticVersion? x, SemanticVersion? y) => x is null
            ? y is null ? 0 : -1
            : y is null ? 1 : x.CompareTo(y);
    }
}

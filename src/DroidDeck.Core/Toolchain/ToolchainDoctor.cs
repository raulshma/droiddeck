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

            var context = new ToolProbeContext(tools, androidSdk, _environment);
            var dotnetTask = ProbeAsync(ToolKind.DotNet, context, cancellationToken);
            context = context with { DotNetTask = dotnetTask };

            // The table is the single source of probe order; only the .NET SDK
            // task is reused because the MAUI workload probe consumes it.
            var probes = ToolchainProbeTable.All
                .Select(spec => spec.Kind == ToolKind.DotNet
                    ? dotnetTask
                    : ProbeAsync(spec.Kind, context, cancellationToken))
                .ToArray();

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
            Guidance = ToolchainProbeTable.AndroidSdkGuidance,
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

    /// <inheritdoc />
    public async Task<ToolInfo> ProbeScrcpyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = new ToolProbeContext(
                _configuration.Current.Settings.Tools,
                AndroidSdk: null,
                _environment);
            return await ProbeAsync(ToolKind.Scrcpy, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Probes Java, translating the JDK home override into an executable path.</summary>
    internal Task<ToolInfo> ProbeJavaAsync(ToolProbeContext context, CancellationToken cancellationToken)
    {
        string? overrideJava = null;
        if (!string.IsNullOrWhiteSpace(context.Tools.JavaHome))
        {
            var home = TryExpandPath(context.Tools.JavaHome);
            if (home is null || !Directory.Exists(home))
            {
                return Task.FromResult(new ToolInfo
                {
                    Kind = ToolKind.Java,
                    DisplayName = "Java",
                    IsAvailable = false,
                    Path = home ?? context.Tools.JavaHome,
                    Source = ToolDetectionSource.Override,
                    Error = "The configured JDK path does not exist.",
                    Guidance = "Update the JDK path in Settings > Tools, or clear it to detect Java automatically.",
                });
            }

            overrideJava = Path.Combine(home, "bin", "java.exe");
        }

        return ProbeExecutableAsync(
            ToolchainProbeTable.Get(ToolKind.Java),
            overrideJava,
            context,
            cancellationToken);
    }

    internal async Task<ToolInfo> ProbeMauiAndroidWorkloadAsync(
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
                CreateProbeRequest(dotnet.Path, ["workload", "list"], ToolchainProbeTable.DefaultProbeTimeout),
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
                Guidance = ToolchainProbeTable.MauiWorkloadGuidance,
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
                Guidance = ToolchainProbeTable.MauiWorkloadGuidance,
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

    internal ToolInfo ProbeUnity(string? overridePath)
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
                Guidance = ToolchainProbeTable.UnityGuidance,
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

    private Task<ToolInfo> ProbeAsync(ToolKind kind, ToolProbeContext context, CancellationToken cancellationToken)
    {
        var spec = ToolchainProbeTable.Get(kind);
        return spec.CustomProbe is { } customProbe
            ? customProbe(this, context, cancellationToken)
            : ProbeExecutableAsync(spec, spec.OverridePath(context.Tools), context, cancellationToken);
    }

    private async Task<ToolInfo> ProbeExecutableAsync(
        ToolProbeSpec spec,
        string? overridePath,
        ToolProbeContext context,
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
                    Kind = spec.Kind,
                    DisplayName = spec.DisplayName,
                    IsAvailable = false,
                    Path = expanded ?? overridePath,
                    Source = ToolDetectionSource.Override,
                    Error = "The configured path does not exist.",
                    Guidance = $"Update the {spec.DisplayName} path in Settings > Tools, or clear it to detect the tool automatically.",
                };
            }

            candidate = expanded;
            source = ToolDetectionSource.Override;
        }
        else
        {
            candidate = FindExecutable(spec.PathNames, spec.ExtraCandidates?.Invoke(context) ?? []);
        }

        if (candidate is null)
        {
            return new ToolInfo
            {
                Kind = spec.Kind,
                DisplayName = spec.DisplayName,
                IsAvailable = false,
                Source = ToolDetectionSource.Missing,
                Guidance = spec.Guidance,
            };
        }

        var probe = await _runner.RunAsync(
                CreateProbeRequest(candidate, spec.VersionArguments, spec.Timeout),
                output: null,
                cancellationToken)
            .ConfigureAwait(false);

        var version = probe.StartError is null ? spec.VersionParser(probe.CombinedOutput) : null;
        if (probe.StartError is not null || (!probe.Succeeded && version is null))
        {
            var error = probe.StartError
                ?? FirstLine(probe.StandardError)
                ?? FirstLine(probe.StandardOutput)
                ?? $"The tool exited with code {probe.ExitCode}.";
            _logger.LogWarning("{Tool} could not be queried: {Error}", spec.DisplayName, error);
            return new ToolInfo
            {
                Kind = spec.Kind,
                DisplayName = spec.DisplayName,
                IsAvailable = false,
                Path = candidate,
                Source = source,
                Error = error,
                Guidance = $"The {spec.DisplayName} executable was found but could not be queried. Verify the installation. {spec.Guidance}",
            };
        }

        return new ToolInfo
        {
            Kind = spec.Kind,
            DisplayName = spec.DisplayName,
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

    private string? FindExecutable(IReadOnlyList<string> pathNames, IReadOnlyList<string> extraCandidates)
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

    private static ProcessRequest CreateProbeRequest(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var extension = Path.GetExtension(executable);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var command = new List<string>(arguments.Count + 3) { "/d", "/c", executable };
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
}

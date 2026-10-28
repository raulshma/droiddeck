using System.Globalization;
using System.Text.RegularExpressions;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Toolchain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Projects.Artifacts;

/// <summary>Inspects APK files with <c>aapt2 dump badging</c>.</summary>
public sealed partial class Aapt2ApkInspector : IApkInspector
{
    private readonly IProcessRunner _runner;
    private readonly IConfigurationService? _configuration;
    private readonly IToolchainService? _toolchain;
    private readonly ILogger<Aapt2ApkInspector> _logger;

    /// <summary>Creates an inspector that resolves aapt2 from settings and the toolchain.</summary>
    public Aapt2ApkInspector(
        IProcessRunner runner,
        IConfigurationService? configuration = null,
        IToolchainService? toolchain = null,
        ILogger<Aapt2ApkInspector>? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _configuration = configuration;
        _toolchain = toolchain;
        _logger = logger ?? NullLogger<Aapt2ApkInspector>.Instance;
    }

    /// <inheritdoc />
    public string? Aapt2Path => ToolPathLocator.ResolveAapt2(_configuration, _toolchain);

    /// <inheritdoc />
    public async Task<ApkInspectionResult> InspectAsync(
        string apkPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apkPath);
        if (!File.Exists(apkPath))
        {
            return new ApkInspectionResult
            {
                Error = $"The APK file was not found: {apkPath}",
                Guidance = "Choose an existing .apk file and try again.",
            };
        }

        var aapt2 = Aapt2Path;
        if (string.IsNullOrWhiteSpace(aapt2))
        {
            return new ApkInspectionResult
            {
                Error = "aapt2 was not found.",
                Guidance = "Install the Android SDK build-tools or set the aapt2 path in Settings > Tools.",
            };
        }

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                new ProcessRequest
                {
                    FileName = aapt2,
                    Arguments = ["dump", "badging", apkPath],
                    Timeout = TimeSpan.FromSeconds(30),
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(exception, "aapt2 failed to start for {Apk}.", apkPath);
            return new ApkInspectionResult
            {
                Error = $"aapt2 could not be started: {exception.Message}",
                Guidance = "Check the aapt2 path in Settings > Tools.",
            };
        }

        if (!result.Succeeded)
        {
            var detail = FirstLine(result.CombinedOutput);
            return new ApkInspectionResult
            {
                Error = string.IsNullOrWhiteSpace(detail)
                    ? $"aapt2 exited with code {result.ExitCode}."
                    : detail,
                Guidance = "The file may not be a valid APK or may be corrupt. Rebuild or re-download it.",
            };
        }

        var info = Parse(apkPath, result.StandardOutput);
        if (info is null)
        {
            return new ApkInspectionResult
            {
                Error = "aapt2 did not report a package name.",
                Guidance = "The file may not be a valid APK. Check that it is a complete, signed APK.",
            };
        }

        return new ApkInspectionResult { Info = info };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApkInspectionResult>> InspectManyAsync(
        IReadOnlyList<string> apkPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apkPaths);
        var results = new List<ApkInspectionResult>(apkPaths.Count);
        foreach (var path in apkPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await InspectAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Parses <c>aapt2 dump badging</c> output.</summary>
    public static ApkInfo? Parse(string apkPath, string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var package = PackageRegex().Match(output);
        if (!package.Success)
        {
            return null;
        }

        var info = new ApkInfo
        {
            Path = apkPath,
            PackageName = package.Groups["name"].Value,
            VersionCode = ParseLong(package.Groups["versionCode"].Value),
            VersionName = NullIfEmpty(package.Groups["versionName"].Value),
            SplitName = NullIfEmpty(package.Groups["split"].Value),
            IsSplit = !string.IsNullOrEmpty(package.Groups["split"].Value),
            MinSdk = ParseInt(SdkRegex().Match(output).Groups["value"].Value),
            TargetSdk = ParseInt(TargetSdkRegex().Match(output).Groups["value"].Value),
        };

        var launchable = LaunchableRegex().Match(output);
        if (launchable.Success)
        {
            info.LaunchableActivity = launchable.Groups["name"].Value;
            info.ApplicationLabel = NullIfEmpty(launchable.Groups["label"].Value);
        }

        if (info.ApplicationLabel is null)
        {
            var label = LabelRegex().Match(output);
            if (label.Success)
            {
                info.ApplicationLabel = label.Groups["label"].Value;
            }
        }

        foreach (Match match in NativeCodeRegex().Matches(output))
        {
            foreach (var abi in match.Groups["abis"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = abi.Trim('\'');
                if (trimmed.Length > 0 && !info.Abis.Contains(trimmed, StringComparer.Ordinal))
                {
                    info.Abis.Add(trimmed);
                }
            }
        }

        foreach (Match match in PermissionRegex().Matches(output))
        {
            var permission = match.Groups["name"].Value;
            if (!string.IsNullOrWhiteSpace(permission) &&
                !info.Permissions.Contains(permission, StringComparer.Ordinal))
            {
                info.Permissions.Add(permission);
            }
        }

        return info;
    }

    private static string? FirstLine(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    [GeneratedRegex(
        @"^package: name='(?<name>[^']+)'(?: versionCode='(?<versionCode>[^']*)')?(?: versionName='(?<versionName>[^']*)')?(?:[^\r\n]*? split='(?<split>[^']*)')?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackageRegex();

    [GeneratedRegex(@"^sdkVersion:'(?<value>[^']*)'", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SdkRegex();

    [GeneratedRegex(@"^targetSdkVersion:'(?<value>[^']*)'", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TargetSdkRegex();

    [GeneratedRegex(
        @"^launchable-activity: name='(?<name>[^']*)'(?:[^\r\n]*?label='(?<label>[^']*)')?",
        RegexOptions.Multiline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex LaunchableRegex();

    [GeneratedRegex(@"^application-label:'(?<label>[^']*)'", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"^native-code: (?<abis>[^\r\n]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NativeCodeRegex();

    [GeneratedRegex(@"^uses-permission: name='(?<name>[^']+)'", RegexOptions.Multiline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PermissionRegex();
}

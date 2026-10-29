using DroidDeck.Core.Projects;

namespace DroidDeck.Core.Runs;

/// <summary>Metadata extracted from an APK with aapt2.</summary>
public sealed class ApkInfo
{
    /// <summary>Gets or sets the package name.</summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>Gets or sets the version code.</summary>
    public long? VersionCode { get; set; }

    /// <summary>Gets or sets the version name.</summary>
    public string? VersionName { get; set; }

    /// <summary>Gets or sets the minimum SDK.</summary>
    public int? MinSdk { get; set; }

    /// <summary>Gets or sets the target SDK.</summary>
    public int? TargetSdk { get; set; }

    /// <summary>Gets or sets the launchable activity.</summary>
    public string? LaunchableActivity { get; set; }

    /// <summary>Gets or sets the application label.</summary>
    public string? ApplicationLabel { get; set; }

    /// <summary>Gets or sets the supported ABIs.</summary>
    public List<string> Abis { get; set; } = [];

    /// <summary>Gets or sets the declared permissions.</summary>
    public List<string> Permissions { get; set; } = [];

    /// <summary>Gets or sets the split name when this is a split APK.</summary>
    public string? SplitName { get; set; }

    /// <summary>Gets or sets whether the APK is a split configuration APK.</summary>
    public bool IsSplit { get; set; }

    /// <summary>Gets or sets the source APK path.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>The result of inspecting one APK.</summary>
public sealed class ApkInspectionResult
{
    /// <summary>Gets or sets the APK metadata.</summary>
    public ApkInfo? Info { get; set; }

    /// <summary>Gets or sets the error when inspection failed.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets guidance for fixing an inspection failure.</summary>
    public string? Guidance { get; set; }

    /// <summary>Gets whether inspection succeeded.</summary>
    public bool Succeeded => Info is not null && Error is null;
}

/// <summary>Inspects APK files with aapt2.</summary>
public interface IApkInspector
{
    /// <summary>Gets the aapt2 path in use, if any.</summary>
    string? Aapt2Path { get; }

    /// <summary>Inspects one APK.</summary>
    Task<ApkInspectionResult> InspectAsync(string apkPath, CancellationToken cancellationToken = default);

    /// <summary>Inspects several APKs, returning the first base APK metadata.</summary>
    Task<IReadOnlyList<ApkInspectionResult>> InspectManyAsync(
        IReadOnlyList<string> apkPaths,
        CancellationToken cancellationToken = default);
}

/// <summary>The result of locating an APK or split APK set.</summary>
public sealed class ArtifactResolution
{
    /// <summary>Gets or sets the base APK.</summary>
    public string? BaseApk { get; set; }

    /// <summary>Gets or sets all APKs to install, with the base first.</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>Gets or sets the inspected base APK metadata.</summary>
    public ApkInfo? Info { get; set; }

    /// <summary>Gets or sets the error when resolution failed.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the candidate files when the glob was ambiguous.</summary>
    public List<string> Candidates { get; set; } = [];

    /// <summary>Gets or sets the glob that was searched.</summary>
    public string? Glob { get; set; }

    /// <summary>Gets whether an artifact was resolved.</summary>
    public bool Succeeded => BaseApk is not null && Error is null;

    /// <summary>Gets whether the resolution produced multiple split APKs.</summary>
    public bool HasSplits => Files.Count > 1;
}

/// <summary>Locates build artifacts for a run configuration.</summary>
public interface IArtifactResolver
{
    /// <summary>Resolves the artifact glob into an installable APK set.</summary>
    Task<ArtifactResolution> ResolveAsync(
        ProjectModel project,
        RunConfiguration configuration,
        CancellationToken cancellationToken = default);
}

/// <summary>Formats install failure output into an explained diagnosis.</summary>
public static class InstallFailureDiagnoser
{
    /// <summary>Diagnoses install output.</summary>
    public static InstallDiagnosis Diagnose(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var code = ExtractCode(output);
        if (code is null)
        {
            return new InstallDiagnosis
            {
                Title = "Install failed",
                Explanation = FirstMeaningfulLine(output) ?? "adb did not report a specific failure code.",
                Guidance = "Check that the device is unlocked, USB debugging is allowed, and the APK matches the device.",
                Actions = [Retry()],
            };
        }

        return code switch
        {
            "INSTALL_FAILED_UPDATE_INCOMPATIBLE" => new InstallDiagnosis
            {
                Code = code,
                Title = "The installed app has a different signature",
                Explanation = "The device already has this package installed with a different signing key.",
                Guidance = "Uninstall the existing app, then install this build again. App data will be lost.",
                Actions =
                [
                    new RecoveryAction
                    {
                        Id = "uninstall-reinstall",
                        Label = "Uninstall & reinstall",
                        Description = "Removes the existing package and installs this APK.",
                        Kind = RecoveryActionKind.UninstallAndReinstall,
                    },
                ],
            },
            "INSTALL_FAILED_VERSION_DOWNGRADE" => new InstallDiagnosis
            {
                Code = code,
                Title = "Version downgrade blocked",
                Explanation = "The APK version code is lower than the version already installed.",
                Guidance = "Retry with the downgrade flag for a debug build, or uninstall the newer version first.",
                Actions =
                [
                    new RecoveryAction
                    {
                        Id = "retry-downgrade",
                        Label = "Retry with -d",
                        Description = "Allows the downgrade. Debug builds usually accept this.",
                        Kind = RecoveryActionKind.RetryWithDowngrade,
                    },
                    new RecoveryAction
                    {
                        Id = "uninstall-reinstall",
                        Label = "Uninstall & reinstall",
                        Description = "Removes the existing package first.",
                        Kind = RecoveryActionKind.UninstallAndReinstall,
                    },
                ],
            },
            "INSTALL_FAILED_USER_RESTRICTED" => new InstallDiagnosis
            {
                Code = code,
                Title = "The vendor blocked USB installs",
                Explanation = "MIUI/HyperOS and some other vendors block installs until \"Install via USB\" is enabled.",
                Guidance = "Open Developer options on the device and enable \"Install via USB\", then retry.",
                Actions =
                [
                    new RecoveryAction
                    {
                        Id = "enable-usb-install",
                        Label = "Show device steps",
                        Description = "Enable Install via USB in Developer options.",
                        Kind = RecoveryActionKind.EnableInstallViaUsb,
                    },
                    Retry(),
                ],
            },
            "INSTALL_FAILED_INSUFFICIENT_STORAGE" => new InstallDiagnosis
            {
                Code = code,
                Title = "Not enough device storage",
                Explanation = "The device does not have enough free space for the APK and its data.",
                Guidance = "Free space on /data or use the device toolbox to clear app caches, then retry.",
                Actions =
                [
                    new RecoveryAction
                    {
                        Id = "free-storage",
                        Label = "Check free space",
                        Description = "Shows /data usage and frees space.",
                        Kind = RecoveryActionKind.FreeStorage,
                    },
                    Retry(),
                ],
            },
            "INSTALL_FAILED_TEST_ONLY" => new InstallDiagnosis
            {
                Code = code,
                Title = "Test-only APK rejected",
                Explanation = "The APK is marked test-only and the install did not allow it.",
                Guidance = "Retry with the test-only flag (-t).",
                Actions =
                [
                    new RecoveryAction
                    {
                        Id = "retry-test",
                        Label = "Retry with -t",
                        Description = "Allows test-only APKs.",
                        Kind = RecoveryActionKind.RetryWithTestFlag,
                    },
                ],
            },
            "INSTALL_PARSE_FAILED_NO_CERTIFICATES" => new InstallDiagnosis
            {
                Code = code,
                Title = "The APK is not signed",
                Explanation = "Android rejected the APK because it has no signing certificate or the signature is broken.",
                Guidance = "Sign the APK with apksigner, or enable signingConfigs in the build and rebuild the app.",
                Actions = [Retry()],
            },
            "INSTALL_PARSE_FAILED_NOT_APK" or "INSTALL_FAILED_INVALID_APK" => new InstallDiagnosis
            {
                Code = code,
                Title = "The APK is invalid or corrupt",
                Explanation = "Android could not parse the package archive.",
                Guidance = "Rebuild or re-download the APK, and make sure the base APK is included with its splits.",
                Actions = [Retry()],
            },
            "INSTALL_FAILED_NO_MATCHING_ABIS" => new InstallDiagnosis
            {
                Code = code,
                Title = "The APK does not support this device",
                Explanation = "The APK contains native code for ABIs the device cannot run.",
                Guidance = "Use an APK that includes the device ABI, or install the matching split APK.",
                Actions = [Retry()],
            },
            "INSTALL_FAILED_ABORTED" => new InstallDiagnosis
            {
                Code = code,
                Title = "Install was aborted on the device",
                Explanation = "The device rejected the install before it completed.",
                Guidance = "Unlock the device, confirm any install prompt, and retry.",
                Actions = [Retry()],
            },
            _ => new InstallDiagnosis
            {
                Code = code,
                Title = "Install failed",
                Explanation = FirstMeaningfulLine(output) ?? code,
                Guidance = "Check the device state and the full adb output, then retry.",
                Actions = [Retry()],
            },
        };
    }

    private static RecoveryAction Retry() => new()
    {
        Id = "retry",
        Label = "Retry",
        Description = "Runs the install step again.",
        Kind = RecoveryActionKind.Retry,
    };

    private static string? ExtractCode(string output)
    {
        var index = output.IndexOf("INSTALL_FAILED_", StringComparison.Ordinal);
        var parseIndex = output.IndexOf("INSTALL_PARSE_FAILED_", StringComparison.Ordinal);
        if (parseIndex >= 0 && (index < 0 || parseIndex < index))
        {
            index = parseIndex;
        }

        if (index < 0)
        {
            return null;
        }

        var end = index;
        while (end < output.Length && (char.IsAsciiLetterUpper(output[end]) || output[end] == '_' || char.IsAsciiDigit(output[end])))
        {
            end++;
        }

        return output[index..end];
    }

    private static string? FirstMeaningfulLine(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 &&
                !trimmed.StartsWith("Performing Streamed Install", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("Success", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
        }

        return null;
    }
}

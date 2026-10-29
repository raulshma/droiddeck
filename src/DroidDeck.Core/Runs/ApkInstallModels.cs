namespace DroidDeck.Core.Runs;

/// <summary>One installable APK set, grouped by the base package.</summary>
public sealed class ApkInstallPlan
{
    /// <summary>Gets or sets the base APK metadata.</summary>
    public required ApkInfo BaseApk { get; init; }

    /// <summary>Gets or sets the files passed to <c>adb install-multiple</c>.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Gets the package name.</summary>
    public string PackageName => BaseApk.PackageName;

    /// <summary>Gets whether the set contains configuration or feature splits.</summary>
    public bool HasSplits => Files.Count > 1;
}

/// <summary>The result of inspecting one or more dropped APK files.</summary>
public sealed class ApkInstallInspection
{
    /// <summary>Gets the installable groups, one per package.</summary>
    public IReadOnlyList<ApkInstallPlan> Plans { get; init; } = [];

    /// <summary>Gets actionable inspection errors.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Gets guidance that explains how to fix the inspection errors.</summary>
    public IReadOnlyList<string> Guidance { get; init; } = [];

    /// <summary>Gets whether at least one plan is ready and no file failed inspection.</summary>
    public bool Succeeded => Plans.Count > 0 && Errors.Count == 0;
}

/// <summary>The state of an install on one device.</summary>
public enum ApkInstallState
{
    /// <summary>The APK set was installed.</summary>
    Succeeded,

    /// <summary>The install or launch failed.</summary>
    Failed,

    /// <summary>The device was not ready, so nothing ran.</summary>
    Skipped,
}

/// <summary>Counts of the per-device install outcomes.</summary>
public readonly record struct ApkInstallSummary(int Succeeded, int Failed, int Skipped)
{
    /// <summary>Counts the outcomes of a result set.</summary>
    public static ApkInstallSummary From(IEnumerable<ApkInstallResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var result in results)
        {
            switch (result.State)
            {
                case ApkInstallState.Succeeded:
                    succeeded++;
                    break;
                case ApkInstallState.Failed:
                    failed++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        return new ApkInstallSummary(succeeded, failed, skipped);
    }
}

/// <summary>The install result for one device.</summary>
public sealed class ApkInstallResult
{
    /// <summary>Gets the device serial.</summary>
    public required string Serial { get; init; }

    /// <summary>Gets the device display name.</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>Gets the outcome.</summary>
    public ApkInstallState State { get; init; }

    /// <summary>Gets the short result message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Gets the explained install failure, when available.</summary>
    public InstallDiagnosis? Diagnosis { get; init; }

    /// <summary>Gets whether the application was launched after installing.</summary>
    public bool Launched { get; init; }

    /// <summary>Gets the raw install output for diagnostics.</summary>
    public string? Output { get; init; }
}

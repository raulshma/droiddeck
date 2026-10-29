using DroidDeck.Core.Devices;

namespace DroidDeck.Core.Runs;

/// <summary>
/// Inspects dropped APKs without a project and installs each package's split set
/// on the selected devices in parallel.
/// </summary>
public sealed class ApkInstallService
{
    private readonly IApkInspector _inspector;
    private readonly IDeviceCommandService _commands;
    private readonly IDeviceOperationCoordinator _coordinator;

    /// <summary>Creates the service.</summary>
    public ApkInstallService(
        IApkInspector inspector,
        IDeviceCommandService commands,
        IDeviceOperationCoordinator coordinator)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    /// <summary>Inspects the dropped files and groups split APKs by package.</summary>
    public async Task<ApkInstallInspection> InspectAsync(
        IReadOnlyList<string> apkPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apkPaths);
        var errors = new List<string>();
        var guidance = new List<string>();
        var paths = apkPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            return new ApkInstallInspection
            {
                Errors = ["Drop at least one .apk file."],
                Guidance = ["Choose a base APK, and include its split APKs when the app uses app bundles."],
            };
        }

        var apkFiles = new List<string>();
        foreach (var path in paths)
        {
            if (!path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{Path.GetFileName(path)} is not an APK file.");
                guidance.Add("Drop .apk files, or a folder that contains the APK set.");
                continue;
            }

            apkFiles.Add(path);
        }

        if (apkFiles.Count == 0)
        {
            return new ApkInstallInspection
            {
                Errors = errors,
                Guidance = guidance.Distinct(StringComparer.Ordinal).ToArray(),
            };
        }

        var results = await _inspector.InspectManyAsync(apkFiles, cancellationToken).ConfigureAwait(false);
        if (results.Count != apkFiles.Count)
        {
            errors.Add(
                $"The APK inspector returned {results.Count} results for {apkFiles.Count} files.");
            guidance.Add("Drop the APK set again; if the problem persists, re-download the files.");
        }

        var infos = new List<ApkInfo>();
        foreach (var (result, path) in results.Zip(apkFiles))
        {
            if (result.Info is null)
            {
                errors.Add($"{Path.GetFileName(path)}: {result.Error ?? "The APK could not be inspected."}");
                if (!string.IsNullOrWhiteSpace(result.Guidance))
                {
                    guidance.Add(result.Guidance);
                }

                continue;
            }

            infos.Add(result.Info);
        }

        var plans = new List<ApkInstallPlan>();
        foreach (var group in infos.GroupBy(info => info.PackageName, StringComparer.Ordinal))
        {
            var bases = group.Where(info => !info.IsSplit).ToArray();
            if (bases.Length == 0)
            {
                errors.Add($"The APK set for {group.Key} has no base APK.");
                var splitNames = string.Join(
                    ", ",
                    group.Select(info => Path.GetFileName(info.Path)));
                guidance.Add(
                    $"Select the base APK together with its configuration splits; {splitNames} " +
                    (group.Count() == 1 ? "is a split APK" : "are split APKs") +
                    " and cannot be installed without a base APK.");
                continue;
            }

            if (bases.Length > 1)
            {
                errors.Add($"The drop contains more than one base APK for {group.Key}.");
                guidance.Add("Drop the base APK and splits for one build at a time.");
                continue;
            }

            var baseApk = bases[0];
            var apks = new[] { baseApk }
                .Concat(group.Where(info => !ReferenceEquals(info, baseApk)))
                .ToArray();
            plans.Add(new ApkInstallPlan
            {
                BaseApk = baseApk,
                Files = apks.Select(info => info.Path).ToArray(),
            });
        }

        return new ApkInstallInspection
        {
            Plans = plans,
            Errors = errors,
            Guidance = guidance.Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    /// <summary>Installs one APK set on every ready device and optionally launches it.</summary>
    public async Task<IReadOnlyList<ApkInstallResult>> InstallAsync(
        ApkInstallPlan plan,
        IReadOnlyList<DeviceInfo> devices,
        bool launch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(devices);
        if (plan.Files.Count == 0)
        {
            throw new ArgumentException("The APK set does not contain any files.", nameof(plan));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tasks = devices.Select(device =>
        {
            if (string.IsNullOrWhiteSpace(device.Serial) || !seen.Add(device.Serial))
            {
                return Task.FromResult(Result(
                    device,
                    ApkInstallState.Skipped,
                    "The device is not ready or was selected twice."));
            }

            return device.CanRunActions
                ? InstallOnDeviceAsync(plan, device, launch, cancellationToken)
                : Task.FromResult(Result(
                    device,
                    ApkInstallState.Skipped,
                    $"The device is not ready ({device.State})."));
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<ApkInstallResult> InstallOnDeviceAsync(
        ApkInstallPlan plan,
        DeviceInfo device,
        bool launch,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await _coordinator.AcquireAsync(
                device.Serial,
                $"Install {plan.PackageName}",
                cancellationToken).ConfigureAwait(false);

            var installed = await _commands.InstallAsync(
                device.Serial,
                plan.Files,
                new AdbInstallOptions
                {
                    Replace = true,
                    TestOnly = true,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var output = Combine(installed.Output, installed.Error);
            if (!installed.Succeeded)
            {
                var diagnosis = InstallFailureDiagnoser.Diagnose(output);
                var message = string.IsNullOrWhiteSpace(diagnosis.Title)
                    ? "The APK set could not be installed."
                    : string.IsNullOrWhiteSpace(diagnosis.Guidance)
                        ? $"{diagnosis.Title}. {diagnosis.Explanation}"
                        : $"{diagnosis.Title}. {diagnosis.Explanation} {diagnosis.Guidance}";
                return Result(
                    device,
                    ApkInstallState.Failed,
                    message,
                    diagnosis: diagnosis,
                    output: output);
            }

            if (!launch)
            {
                return Result(
                    device,
                    ApkInstallState.Succeeded,
                    plan.HasSplits
                        ? $"Installed {plan.Files.Count} APK files."
                        : "Installed the APK.",
                    output: output);
            }

            var launched = await _commands.LaunchAsync(
                device.Serial,
                new AdbLaunchOptions
                {
                    PackageName = plan.PackageName,
                    Activity = plan.BaseApk.LaunchableActivity,
                    Wait = true,
                },
                cancellationToken).ConfigureAwait(false);
            if (!launched.Succeeded)
            {
                var launchOutput = Combine(launched.Output, launched.Error);
                return Result(
                    device,
                    ApkInstallState.Failed,
                    $"Installed {plan.PackageName}, but the app could not be launched.",
                    output: Combine(output, launchOutput));
            }

            return Result(
                device,
                ApkInstallState.Succeeded,
                $"Installed and launched {plan.PackageName}.",
                launched: true,
                output: output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Result(
                device,
                ApkInstallState.Failed,
                $"The APK set could not be installed: {exception.Message}");
        }
    }

    private static ApkInstallResult Result(
        DeviceInfo device,
        ApkInstallState state,
        string message,
        InstallDiagnosis? diagnosis = null,
        bool launched = false,
        string? output = null) => new()
    {
        Serial = device.Serial,
        DeviceName = device.DisplayName,
        State = state,
        Message = message,
        Diagnosis = diagnosis,
        Launched = launched,
        Output = output,
    };

    private static string Combine(string? output, string? error)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return error ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(error) ? output : output + "\n" + error;
    }
}

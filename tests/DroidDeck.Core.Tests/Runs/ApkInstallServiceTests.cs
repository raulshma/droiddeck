using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Runs;

namespace DroidDeck.Core.Tests.Runs;

public sealed class ApkInstallServiceTests : IDisposable
{
    private readonly DeviceOperationCoordinator _coordinator = new();

    public void Dispose() => _coordinator.Dispose();

    [Fact]
    public async Task InspectAsync_GroupsSplitApksUnderTheBasePackage()
    {
        var inspector = new FakeInspector
        {
            Results =
            [
                Info(@"C:\apks\base.apk", "com.example.app"),
                Info(@"C:\apks\config.en.apk", "com.example.app", split: "config.en"),
                Info(@"C:\apks\config.xxhdpi.apk", "com.example.app", split: "config.xxhdpi"),
            ],
        };
        var service = CreateService(inspector: inspector);

        var inspection = await service.InspectAsync(
        [
            @"C:\apks\config.en.apk",
            @"C:\apks\base.apk",
            @"C:\apks\config.xxhdpi.apk",
        ]);

        Assert.True(inspection.Succeeded);
        var plan = Assert.Single(inspection.Plans);
        Assert.Equal("com.example.app", plan.PackageName);
        Assert.True(plan.HasSplits);
        Assert.Equal(@"C:\apks\base.apk", plan.Files[0]);
        Assert.Equal(3, plan.Files.Count);
    }

    [Fact]
    public async Task InspectAsync_ReportsActionableErrorsForInvalidSets()
    {
        var inspector = new FakeInspector
        {
            Results =
            [
                new ApkInspectionResult
                {
                    Error = "The file may not be a valid APK.",
                    Guidance = "Rebuild or re-download it.",
                },
            ],
        };
        var service = CreateService(inspector: inspector);

        var inspection = await service.InspectAsync([@"C:\apks\broken.apk"]);

        Assert.False(inspection.Succeeded);
        Assert.Empty(inspection.Plans);
        Assert.Contains("broken.apk", inspection.Errors[0], StringComparison.Ordinal);
        Assert.Contains("valid APK", inspection.Errors[0], StringComparison.Ordinal);
        Assert.Contains("Rebuild", inspection.Guidance[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectAsync_RejectsSplitSetsWithoutABaseApk()
    {
        var inspector = new FakeInspector
        {
            Results = [Info(@"C:\apks\config.en.apk", "com.example.app", split: "config.en")],
        };
        var service = CreateService(inspector: inspector);

        var inspection = await service.InspectAsync([@"C:\apks\config.en.apk"]);

        Assert.False(inspection.Succeeded);
        Assert.Contains("base APK", inspection.Errors[0], StringComparison.Ordinal);
        Assert.Contains("config.en.apk", inspection.Guidance[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallAsync_InstallsTheWholeSplitSetOnEveryReadyDevice()
    {
        var commands = new FakeCommands();
        var service = CreateService(commands: commands);
        var plan = Plan(@"C:\apks\base.apk", @"C:\apks\config.en.apk");

        var results = await service.InstallAsync(
            plan,
            [Device("emulator-5554"), Device("R58M1234")],
            launch: true);

        Assert.All(results, result => Assert.Equal(ApkInstallState.Succeeded, result.State));
        Assert.All(results, result => Assert.True(result.Launched));
        Assert.Equal(2, commands.Installs.Count);
        Assert.All(commands.Installs, call => Assert.Equal(plan.Files, call.Files));
        Assert.Equal(2, commands.Launches.Count);
        Assert.Equal("com.example.app", commands.Launches[0].Options.PackageName);
        Assert.Equal("com.example.app.MainActivity", commands.Launches[0].Options.Activity);
    }

    [Fact]
    public async Task InstallAsync_SkipsDevicesThatCannotRunActions()
    {
        var commands = new FakeCommands();
        var service = CreateService(commands: commands);
        var plan = Plan(@"C:\apks\base.apk");

        var results = await service.InstallAsync(
            plan,
            [Device("offline-1", DeviceState.Offline), Device("emulator-5554")],
            launch: false);

        Assert.Equal(ApkInstallState.Skipped, results[0].State);
        Assert.Contains("not ready", results[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ApkInstallState.Succeeded, results[1].State);
        Assert.Single(commands.Installs);
        Assert.Empty(commands.Launches);
    }

    [Fact]
    public async Task InstallAsync_ExplainsUnsignedApkFailuresPerDevice()
    {
        var commands = new FakeCommands
        {
            InstallResult = new DeviceCommandResult
            {
                Succeeded = false,
                ExitCode = 1,
                Output = "Failure [INSTALL_PARSE_FAILED_NO_CERTIFICATES: Failed to collect certificates]",
            },
        };
        var service = CreateService(commands: commands);
        var plan = Plan(@"C:\apks\base.apk");

        var results = await service.InstallAsync(plan, [Device("emulator-5554")], launch: false);

        var result = Assert.Single(results);
        Assert.Equal(ApkInstallState.Failed, result.State);
        Assert.NotNull(result.Diagnosis);
        Assert.Contains("signed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sign", result.Diagnosis.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("apksigner", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_ReportsLaunchFailuresAfterASuccessfulInstall()
    {
        var commands = new FakeCommands
        {
            LaunchResult = new DeviceCommandResult
            {
                Succeeded = false,
                ExitCode = 1,
                Error = "Activity not found",
            },
        };
        var service = CreateService(commands: commands);
        var plan = Plan(@"C:\apks\base.apk");

        var results = await service.InstallAsync(plan, [Device("emulator-5554")], launch: true);

        var result = Assert.Single(results);
        Assert.Equal(ApkInstallState.Failed, result.State);
        Assert.False(result.Launched);
        Assert.Contains("installed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("launch", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_CountsEachOutcome()
    {
        var summary = ApkInstallSummary.From(
        [
            new ApkInstallResult { Serial = "a", State = ApkInstallState.Succeeded },
            new ApkInstallResult { Serial = "b", State = ApkInstallState.Failed },
            new ApkInstallResult { Serial = "c", State = ApkInstallState.Skipped },
            new ApkInstallResult { Serial = "d", State = ApkInstallState.Skipped },
        ]);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(2, summary.Skipped);
    }

    [Fact]
    public async Task InstallAsync_HoldsTheDeviceLockWhileInstalling()
    {
        using var coordinator = new DeviceOperationCoordinator();
        DeviceOperationInfo[]? observed = null;
        var commands = new FakeCommands
        {
            OnInstall = () => observed = coordinator.ActiveOperations.ToArray(),
        };
        var service = CreateService(commands: commands, coordinator: coordinator);
        var plan = Plan(@"C:\apks\base.apk");

        await service.InstallAsync(plan, [Device("emulator-5554")], launch: false);

        Assert.NotNull(observed);
        Assert.Contains(observed, operation =>
            operation.Serial == "emulator-5554" &&
            operation.Operation.Contains("Install", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(coordinator.ActiveOperations);
    }

    private ApkInstallService CreateService(
        FakeInspector? inspector = null,
        FakeCommands? commands = null,
        IDeviceOperationCoordinator? coordinator = null) =>
        new(inspector ?? new FakeInspector(), commands ?? new FakeCommands(), coordinator ?? _coordinator);

    private static ApkInstallPlan Plan(params string[] files)
    {
        var apks = files.Select((path, index) => new ApkInfo
        {
            Path = path,
            PackageName = "com.example.app",
            LaunchableActivity = index == 0 ? "com.example.app.MainActivity" : null,
            IsSplit = index > 0,
            SplitName = index > 0 ? Path.GetFileNameWithoutExtension(path) : null,
        }).ToArray();
        return new ApkInstallPlan
        {
            BaseApk = apks[0],
            Files = files,
        };
    }

    private static DeviceInfo Device(string serial, DeviceState state = DeviceState.Device) =>
        new() { Serial = serial, State = state };

    private static ApkInspectionResult Info(string path, string package, string? split = null) => new()
    {
        Info = new ApkInfo
        {
            Path = path,
            PackageName = package,
            LaunchableActivity = split is null ? "com.example.app.MainActivity" : null,
            IsSplit = split is not null,
            SplitName = split,
        },
    };

    private sealed class FakeInspector : IApkInspector
    {
        public IReadOnlyList<ApkInspectionResult> Results { get; init; } = [];

        public string? Aapt2Path => "aapt2";

        public Task<ApkInspectionResult> InspectAsync(
            string apkPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Results.FirstOrDefault(result =>
                string.Equals(result.Info?.Path, apkPath, StringComparison.Ordinal)) ??
                new ApkInspectionResult
                {
                    Error = $"The APK file was not found: {apkPath}",
                    Guidance = "Choose an existing .apk file and try again.",
                });

        public Task<IReadOnlyList<ApkInspectionResult>> InspectManyAsync(
            IReadOnlyList<string> apkPaths,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ApkInspectionResult>>(
                apkPaths.Select((path, index) =>
                {
                    var match = Results.FirstOrDefault(result =>
                        string.Equals(result.Info?.Path, path, StringComparison.Ordinal));
                    if (match is not null)
                    {
                        return match;
                    }

                    return index < Results.Count && Results[index].Info is null
                        ? Results[index]
                        : new ApkInspectionResult
                        {
                            Error = $"The APK file was not found: {path}",
                            Guidance = "Choose an existing .apk file and try again.",
                        };
                }).ToArray());
    }

    private sealed class FakeCommands : IDeviceCommandService
    {
        public List<(string Serial, IReadOnlyList<string> Files)> Installs { get; } = [];

        public List<(string Serial, AdbLaunchOptions Options)> Launches { get; } = [];

        public DeviceCommandResult? InstallResult { get; init; }

        public DeviceCommandResult? LaunchResult { get; init; }

        public Action? OnInstall { get; init; }

        public string AdbExecutable => "adb";

        public Task<DeviceCommandResult> InstallAsync(
            string serial,
            IReadOnlyList<string> apkPaths,
            AdbInstallOptions options,
            IProgress<ProcessOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            Installs.Add((serial, apkPaths));
            OnInstall?.Invoke();
            return Task.FromResult(InstallResult ?? new DeviceCommandResult
            {
                Succeeded = true,
                CommandLine = "adb install-multiple",
            });
        }

        public Task<DeviceCommandResult> LaunchAsync(
            string serial,
            AdbLaunchOptions options,
            CancellationToken cancellationToken = default)
        {
            Launches.Add((serial, options));
            return Task.FromResult(LaunchResult ?? new DeviceCommandResult
            {
                Succeeded = true,
                CommandLine = "adb shell am start -W",
            });
        }

        public Task<DeviceCommandResult> ForceStopAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceCommandResult { Succeeded = true });

        public Task<DeviceCommandResult> UninstallAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceCommandResult { Succeeded = true });

        public Task<DeviceCommandResult> ShellAsync(
            string serial,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceCommandResult { Succeeded = true });

        public Task<DeviceCommandResult> ReverseAsync(
            string serial,
            int devicePort,
            int hostPort,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceCommandResult { Succeeded = true });

        public Task<string?> ResolveActivityAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<int?> GetPackageUidAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<int?>(null);

        public Task<long?> GetFreeStorageAsync(string serial, CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(null);

        public Task<DeviceCommandResult> ClearLogcatAsync(
            string serial,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceCommandResult { Succeeded = true });
    }
}

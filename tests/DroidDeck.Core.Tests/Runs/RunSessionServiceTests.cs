using DroidDeck.Core.Devices;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Notifications;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Sessions;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Runs;

public sealed class RunSessionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root;

    public RunSessionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "droiddeck-runs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task CustomPipeline_CompletesEndToEndWithoutArtifactOrLaunch()    {
        var configuration = new RunConfiguration
        {
            Name = "steps-only",
            Kind = RunConfigurationKind.Custom,
            Steps =
            [
                new RunStepDefinition
                {
                    Id = "greet",
                    Command = "cmd",
                    Arguments = ["/c", "echo", "hello"],
                },
            ],
        };
        var runner = new FakeRunner();
        await using var service = CreateService(runner: runner);

        var session = await service.RunAsync(CreateRequest(configuration));

        Assert.Equal(RunSessionState.Succeeded, session.State);
        Assert.Equal("The run steps completed.", session.StatusMessage);
        var artifact = Assert.Single(session.Steps, step => step.Id == "artifact");
        var install = Assert.Single(session.Steps, step => step.Id == "install");
        var launch = Assert.Single(session.Steps, step => step.Id == "launch");
        Assert.Equal(RunStepState.Skipped, artifact.State);
        Assert.Equal(RunStepState.Skipped, install.State);
        Assert.Equal(RunStepState.Skipped, launch.State);
        var custom = Assert.Single(session.Steps, step => step.Id.EndsWith("greet", StringComparison.Ordinal));
        Assert.Equal(RunStepState.Succeeded, custom.State);
        Assert.DoesNotContain(session.Steps, step => step.State == RunStepState.Failed);
    }

    [Fact]
    public async Task CustomPipeline_ExpandsPerDevicePlaceholders()
    {
        var configuration = new RunConfiguration
        {
            Name = "steps",
            Kind = RunConfigurationKind.Custom,
            Steps =
            [
                new RunStepDefinition
                {
                    Id = "report",
                    Command = "cmd",
                    Arguments = ["/c", "echo", "${deviceserial}", "${devicemodel}", "${deviceapilevel}", "${isemulator}"],
                },
            ],
        };
        var runner = new FakeRunner();
        await using var service = CreateService(runner: runner);
        var device = new DeviceInfo
        {
            Serial = "emulator-5554",
            Model = "Pixel 8",
            ApiLevel = 34,
            IsEmulator = true,
        };

        await service.RunAsync(CreateRequest(configuration, device: device));

        var request = Assert.Single(runner.Requests);
        Assert.Contains("emulator-5554", request.Arguments);
        Assert.Contains("Pixel 8", request.Arguments);
        Assert.Contains("34", request.Arguments);
        Assert.Contains("true", request.Arguments);
    }

    [Fact]
    public async Task CustomPipeline_PhysicalDeviceReportsNotEmulator()
    {
        var configuration = new RunConfiguration
        {
            Name = "steps",
            Kind = RunConfigurationKind.Custom,
            Steps =
            [
                new RunStepDefinition
                {
                    Id = "check",
                    Command = "cmd",
                    Arguments = ["/c", "echo", "emu=${isemulator}"],
                },
            ],
        };
        var runner = new FakeRunner();
        await using var service = CreateService(runner: runner);
        var device = new DeviceInfo { Serial = "ABCD1234", IsEmulator = false };

        await service.RunAsync(CreateRequest(configuration, device: device));

        var request = Assert.Single(runner.Requests);
        Assert.Contains("emu=false", request.Arguments);
    }

    [Fact]
    public async Task CustomPipeline_ContinuePolicyEndsPartiallyFailed()
    {
        var configuration = new RunConfiguration
        {
            Name = "steps",
            Kind = RunConfigurationKind.Custom,
            Steps =
            [
                new RunStepDefinition
                {
                    Id = "flaky",
                    Command = "cmd",
                    Arguments = ["/c", "fail"],
                    FailurePolicy = StepFailurePolicy.Continue,
                },
                new RunStepDefinition
                {
                    Id = "after",
                    Command = "cmd",
                    Arguments = ["/c", "echo", "done"],
                },
            ],
        };
        var runner = new FakeRunner
        {
            Handler = request => request.Arguments.Contains("fail", StringComparer.Ordinal)
                ? Result(exitCode: 3)
                : Result(exitCode: 0),
        };
        await using var service = CreateService(runner: runner);

        var session = await service.RunAsync(CreateRequest(configuration));

        Assert.Equal(RunSessionState.PartiallyFailed, session.State);
        var failed = Assert.Single(session.Steps, step => step.Id.EndsWith("flaky", StringComparison.Ordinal));
        var after = Assert.Single(session.Steps, step => step.Id.EndsWith("after", StringComparison.Ordinal));
        Assert.Equal(RunStepState.Failed, failed.State);
        Assert.Equal(RunStepState.Succeeded, after.State);
    }

    [Fact]
    public async Task CustomPipeline_SkipsStepByConditionWithReason()
    {
        var configuration = new RunConfiguration
        {
            Name = "steps",
            Kind = RunConfigurationKind.Custom,
            VariantName = "debug",
            Steps =
            [
                new RunStepDefinition
                {
                    Id = "release-only",
                    Command = "cmd",
                    Arguments = ["/c", "echo", "release"],
                    Condition = "variant:release",
                },
            ],
        };
        await using var service = CreateService();

        var session = await service.RunAsync(CreateRequest(configuration));

        Assert.Equal(RunSessionState.Succeeded, session.State);
        var step = Assert.Single(session.Steps, step => step.Id.EndsWith("release-only", StringComparison.Ordinal));
        Assert.Equal(RunStepState.Skipped, step.State);
        Assert.False(string.IsNullOrWhiteSpace(step.SkipReason));
        Assert.Contains("debug", step.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApkPipeline_KeepsFailingLaunchWhenPackageUnknown()
    {
        var configuration = new RunConfiguration
        {
            Name = "apk",
            Kind = RunConfigurationKind.Apk,
            Artifact = { Glob = "**/*.apk" },
        };
        var artifacts = new FakeArtifactResolver
        {
            Resolution = new ArtifactResolution
            {
                Glob = "**/*.apk",
                BaseApk = Path.Combine(_root, "app.apk"),
                Files = [Path.Combine(_root, "app.apk")],
            },
        };
        var commands = new FakeDeviceCommands { LaunchResult = Result(exitCode: 1, output: string.Empty) };
        await using var service = CreateService(artifacts: artifacts, commands: commands);

        var session = await service.RunAsync(CreateRequest(configuration));

        Assert.Equal(RunSessionState.Failed, session.State);
        var launch = Assert.Single(session.Steps, step => step.Id == "launch");
        Assert.Equal(RunStepState.Failed, launch.State);
        Assert.Contains("package name is unknown", launch.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashEvents_AccumulateInSessionTimeline()
    {
        var (service, session, logcat) = await RunWithAttachedLogcatAsync();
        await using (service)
        {
            logcat.RaiseCrash("stack-key-1");
            logcat.RaiseCrash("stack-key-1");
            logcat.RaiseCrash("stack-key-2");

            Assert.Equal(3, session.Crashes.Count);
            Assert.Equal("stack-key-1", session.Crashes[0].DedupeKey);
            Assert.Equal("stack-key-1", session.Crashes[1].DedupeKey);
            Assert.Equal("stack-key-2", session.Crashes[2].DedupeKey);
        }
    }

    private async Task<(RunSessionService Service, RunSession Session, FakeLogcatSession Logcat)> RunWithAttachedLogcatAsync()
    {
        var configuration = new RunConfiguration
        {
            Name = "apk",
            Kind = RunConfigurationKind.Apk,
            Artifact = { Glob = "**/*.apk" },
        };
        var apk = Path.Combine(_root, "app.apk");
        var artifacts = new FakeArtifactResolver
        {
            Resolution = new ArtifactResolution
            {
                Glob = "**/*.apk",
                BaseApk = apk,
                Files = [apk],
                Info = new ApkInfo { PackageName = "com.example.app", LaunchableActivity = "com.example.app.MainActivity" },
            },
        };
        var logcat = new FakeLogcatSession();
        var logcatService = new FakeLogcatService(logcat);
        var commands = new FakeDeviceCommands { PackageUid = 10_123 };
        var service = CreateService(artifacts: artifacts, commands: commands, logcat: logcatService);
        var session = await service.RunAsync(CreateRequest(configuration));
        Assert.Equal(RunSessionState.Running, session.State);
        Assert.NotNull(session.LogcatSessionId);
        return (service, session, logcat);
    }

    [Fact]
    public async Task PrebuiltArtifact_SkipsResolverAndBuildAndInstallsTheReusedFiles()
    {
        var configuration = new RunConfiguration
        {
            Name = "debug",
            Kind = RunConfigurationKind.Gradle,
            Artifact = { Glob = "**/*.apk" },
        };
        var commands = new FakeDeviceCommands();
        var service = CreateService(commands: commands);
        var request = CreateRequest(configuration, options: new RunOptions
        {
            SkipBuild = true,
            PrebuiltArtifactFiles = ["/tmp/reused.apk"],
            PrebuiltArtifactPath = "/tmp/reused.apk",
            PrebuiltPackageName = "com.example.reused",
            PrebuiltActivity = "com.example.reused.MainActivity",
        });

        var session = await service.RunAsync(request);

        Assert.Equal(RunSessionState.Running, session.State);
        Assert.Equal(["/tmp/reused.apk"], session.ArtifactFiles);
        var artifact = Assert.Single(session.Steps, step => step.Id == "artifact");
        Assert.Equal(RunStepState.Succeeded, artifact.State);
        Assert.Contains("Reused", artifact.OutputTail);
        var build = Assert.Single(session.Steps, step => step.Id == "build");
        Assert.Equal(RunStepState.Skipped, build.State);
        Assert.Equal("com.example.reused", session.PackageName);
    }

    [Fact]
    public async Task RunAsync_HoldsTheDeviceLockWhileMutatingTheDevice()
    {
        using var coordinator = new DeviceOperationCoordinator();
        var observed = false;
        var configuration = new RunConfiguration
        {
            Name = "steps",
            Kind = RunConfigurationKind.Custom,
            Steps =
            [
                new RunStepDefinition
                {
                    Id = "step",
                    Command = "cmd",
                    Arguments = ["/c", "echo", "hello"],
                },
            ],
        };
        var runner = new FakeRunner
        {
            Handler = _ =>
            {
                observed = coordinator.ActiveOperations.Any(operation =>
                    string.Equals(operation.Serial, "emulator-5554", StringComparison.Ordinal));
                return Result(0);
            },
        };
        await using var service = CreateService(runner: runner, coordinator: coordinator);

        await service.RunAsync(CreateRequest(configuration));

        Assert.True(observed);
        Assert.Empty(coordinator.ActiveOperations);
    }

    [Fact]
    public async Task HotReloadAsync_AttachesAFlutterSessionAndCleansItUpOnStop()
    {
        var marker = Path.Combine(_root, "flutter-daemon-exited.txt");
        var planBuilder = new RunPlanBuilderStub
        {
            FlutterRequest = new ProcessRequest
            {
                FileName = "dotnet",
                Arguments = [
                    TestProcessHelper.AssemblyPath,
                    "flutter-daemon",
                    "--exit-file",
                    marker,
                ],
            },
        };
        var configuration = new RunConfiguration
        {
            Name = "debug",
            Kind = RunConfigurationKind.Flutter,
            Artifact = { Glob = "build/app/outputs/flutter-apk/app-debug.apk" },
            Launch = { PackageName = "com.example.flutter_sample" },
        };
        await using var service = CreateService(planBuilder: planBuilder);
        var request = CreateRequest(
            configuration,
            options: new RunOptions
            {
                SkipBuild = true,
                PrebuiltArtifactFiles = ["/tmp/app-debug.apk"],
                PrebuiltArtifactPath = "/tmp/app-debug.apk",
                PrebuiltPackageName = "com.example.flutter_sample",
            });

        var session = await service.RunAsync(request);
        Assert.True(session.SupportsHotReload);
        Assert.Equal(RunSessionState.Running, session.State);

        var reload = await service.HotReloadAsync(session.Id);

        Assert.NotNull(reload);
        Assert.True(reload.Succeeded, reload.Message);
        var attach = Assert.Single(session.Steps, step => step.Name == "Attach Flutter session");
        Assert.Equal(RunStepState.Succeeded, attach.State);
        var reloadStep = Assert.Single(session.Steps, step => step.Name == "Hot reload");
        Assert.Equal(RunStepState.Succeeded, reloadStep.State);
        Assert.Contains("Reloaded", session.StatusMessage, StringComparison.OrdinalIgnoreCase);

        await service.StopAsync(session.Id);

        Assert.Equal(RunSessionState.Succeeded, session.State);
        await Wait.UntilAsync(
            () => File.Exists(marker),
            TimeSpan.FromSeconds(10),
            "The Flutter daemon was not stopped with the session.");
    }

    [Fact]
    public async Task HotReloadAsync_RejectsNonFlutterSessions()
    {
        var configuration = new RunConfiguration
        {
            Name = "steps",
            Kind = RunConfigurationKind.Custom,
            Steps =
            [
                new RunStepDefinition
                {
                    Id = "step",
                    Command = "cmd",
                    Arguments = ["/c", "echo", "hello"],
                },
            ],
        };
        await using var service = CreateService();

        var session = await service.RunAsync(CreateRequest(configuration));
        var result = await service.HotReloadAsync(session.Id);

        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Contains("Flutter", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HotReloadAsync_IsLimitedToDebugFlutterConfigurations()
    {
        var planBuilder = new RunPlanBuilderStub
        {
            FlutterRequest = new ProcessRequest
            {
                FileName = "dotnet",
                Arguments = [TestProcessHelper.AssemblyPath, "flutter-daemon"],
            },
        };
        var configuration = new RunConfiguration
        {
            Name = "profile",
            Kind = RunConfigurationKind.Flutter,
            VariantName = "profile",
            Artifact = { Glob = "build/app/outputs/flutter-apk/app-profile.apk" },
            Launch = { PackageName = "com.example.flutter_sample" },
        };
        await using var service = CreateService(planBuilder: planBuilder);

        var session = await service.RunAsync(CreateRequest(configuration, options: FlutterOptions("app-profile.apk")));
        var result = await service.HotReloadAsync(session.Id);

        Assert.False(session.SupportsHotReload);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Contains("debug", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HotReloadAsync_KeepsTheFlutterSessionWhenCompilationFails()
    {
        var planBuilder = new RunPlanBuilderStub
        {
            FlutterRequest = new ProcessRequest
            {
                FileName = "dotnet",
                Arguments = [TestProcessHelper.AssemblyPath, "flutter-daemon", "--fail-reload"],
            },
        };
        var configuration = new RunConfiguration
        {
            Name = "debug",
            Kind = RunConfigurationKind.Flutter,
            Artifact = { Glob = "build/app/outputs/flutter-apk/app-debug.apk" },
            Launch = { PackageName = "com.example.flutter_sample" },
        };
        await using var service = CreateService(planBuilder: planBuilder);
        var session = await service.RunAsync(CreateRequest(configuration, options: FlutterOptions("app-debug.apk")));

        var first = await service.HotReloadAsync(session.Id);

        Assert.NotNull(first);
        Assert.False(first.Succeeded);
        Assert.Equal(RunSessionState.Running, session.State);
        Assert.Contains("failed", first.Message, StringComparison.OrdinalIgnoreCase);

        var second = await service.HotReloadAsync(session.Id);

        Assert.NotNull(second);
        Assert.False(second.Succeeded);
        Assert.Equal(RunSessionState.Running, session.State);
        Assert.Single(session.Steps, step => step.Name == "Attach Flutter session");
        Assert.Equal(2, session.Steps.Count(step => step.Name == "Hot reload"));
    }

    [Fact]
    public async Task StopAsync_CancelsAnAttachInProgressAndCleansUp()
    {
        var planBuilder = new RunPlanBuilderStub
        {
            FlutterRequest = new ProcessRequest
            {
                FileName = "dotnet",
                Arguments = [
                    TestProcessHelper.AssemblyPath,
                    "flutter-daemon",
                    "--delay-start",
                    "30000",
                ],
            },
        };
        var configuration = new RunConfiguration
        {
            Name = "debug",
            Kind = RunConfigurationKind.Flutter,
            Artifact = { Glob = "build/app/outputs/flutter-apk/app-debug.apk" },
            Launch = { PackageName = "com.example.flutter_sample" },
        };
        await using var service = CreateService(planBuilder: planBuilder);
        var session = await service.RunAsync(CreateRequest(configuration, options: FlutterOptions("app-debug.apk")));

        var reloadTask = service.HotReloadAsync(session.Id);
        await Wait.UntilAsync(
            () => session.State == RunSessionState.Launching,
            TimeSpan.FromSeconds(10),
            "The Flutter session did not start attaching.");

        await service.StopAsync(session.Id);

        var result = await reloadTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.Contains("cancel", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RunSessionState.Cancelled, session.State);
        Assert.DoesNotContain(
            session.Steps,
            step => step.Name == "Attach Flutter session" && step.State == RunStepState.Succeeded);
    }

    [Fact]
    public async Task ReactNativePipeline_StartsMetroAndAppliesTheReverseTunnel()
    {
        EnsureAndroidHost();
        var commands = new FakeDeviceCommands();
        var reloads = new List<int>();
        var probeStatuses = new Queue<MetroPortStatus>([MetroPortStatus.Free]);
        await using var service = CreateService(
            commands: commands,
            planBuilder: new RunPlanBuilderStub { MetroRequest = TestProcessHelper.Create("sleep", "60000") },
            metroProbe: (_, _) => Task.FromResult(
                probeStatuses.Count > 0 ? probeStatuses.Dequeue() : MetroPortStatus.Running),
            metroReload: (port, _) =>
            {
                reloads.Add(port);
                return Task.FromResult((true, (string?)null));
            });
        var session = await service.RunAsync(CreateRequest(
            ReactNativeConfiguration(),
            options: ReactNativeOptions(),
            projectKind: ProjectKind.ReactNative));

        Assert.Equal(RunSessionState.Running, session.State);
        Assert.True(session.SupportsHotReload);
        var metro = Assert.Single(session.Steps, step => step.Id == "metro");
        Assert.Equal(RunStepState.Succeeded, metro.State);
        Assert.Equal(RunStepKind.Metro, metro.Kind);
        Assert.False(string.IsNullOrWhiteSpace(metro.CommandLine));
        var reverse = Assert.Single(session.Steps, step => step.Id == "reverse");
        Assert.Equal(RunStepState.Succeeded, reverse.State);
        var call = Assert.Single(commands.ReverseCalls);
        Assert.Equal(("emulator-5554", 8081, 8081), call);

        var reload = await service.HotReloadAsync(session.Id);

        Assert.NotNull(reload);
        Assert.True(reload.Succeeded);
        Assert.Equal([8081], reloads);
        Assert.Single(session.Steps, step => step.Name == "Reload (Metro)");
    }

    [Fact]
    public async Task ReactNativePipeline_ReusesARunningMetroInstance()
    {
        EnsureAndroidHost();
        var commands = new FakeDeviceCommands();
        await using var service = CreateService(
            commands: commands,
            planBuilder: new RunPlanBuilderStub { MetroRequest = TestProcessHelper.Create("sleep", "60000") },
            metroProbe: (_, _) => Task.FromResult(MetroPortStatus.Running));
        var session = await service.RunAsync(CreateRequest(
            ReactNativeConfiguration(),
            options: ReactNativeOptions(),
            projectKind: ProjectKind.ReactNative));

        Assert.Equal(RunSessionState.Running, session.State);
        var metro = Assert.Single(session.Steps, step => step.Id == "metro");
        Assert.Equal(RunStepState.Succeeded, metro.State);
        Assert.Contains("Reusing the Metro instance", metro.OutputTail, StringComparison.Ordinal);
        Assert.Single(commands.ReverseCalls);
    }

    [Fact]
    public async Task ReactNativePipeline_ExplainsPortConflictsWithFixes()
    {
        EnsureAndroidHost();
        await using var service = CreateService(
            planBuilder: new RunPlanBuilderStub { MetroRequest = TestProcessHelper.Create("sleep", "60000") },
            metroProbe: (_, _) => Task.FromResult(MetroPortStatus.Conflicting));
        var session = await service.RunAsync(CreateRequest(
            ReactNativeConfiguration(),
            options: ReactNativeOptions(),
            projectKind: ProjectKind.ReactNative));

        Assert.Equal(RunSessionState.Failed, session.State);
        var metro = Assert.Single(session.Steps, step => step.Id == "metro");
        Assert.Equal(RunStepState.Failed, metro.State);
        Assert.Contains("8081", session.Error, StringComparison.Ordinal);
        Assert.Contains("metro.config.js", session.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Steps, step => step.Id == "launch");
    }

    [Fact]
    public async Task ReactNativePipeline_FailsWithGuidanceWhenReverseFails()
    {
        EnsureAndroidHost();
        var commands = new FakeDeviceCommands { ReverseSucceeds = false };
        await using var service = CreateService(
            commands: commands,
            planBuilder: new RunPlanBuilderStub { MetroRequest = TestProcessHelper.Create("sleep", "60000") },
            metroProbe: (_, _) => Task.FromResult(MetroPortStatus.Running));
        var session = await service.RunAsync(CreateRequest(
            ReactNativeConfiguration(),
            options: ReactNativeOptions(),
            projectKind: ProjectKind.ReactNative));

        Assert.Equal(RunSessionState.Failed, session.State);
        Assert.Contains("reverse", session.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("adb -s emulator-5554 reverse tcp:8081 tcp:8081", session.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Steps, step => step.Id == "launch");
    }

    [Fact]
    public async Task ReactNativePipeline_ReleaseBuildsSkipMetro()
    {
        EnsureAndroidHost();
        var commands = new FakeDeviceCommands();
        await using var service = CreateService(
            commands: commands,
            planBuilder: new RunPlanBuilderStub { MetroRequest = TestProcessHelper.Create("sleep", "60000") },
            metroProbe: (_, _) => Task.FromResult(MetroPortStatus.Running));
        var configuration = ReactNativeConfiguration();
        configuration.VariantName = "release";
        var session = await service.RunAsync(CreateRequest(
            configuration,
            options: ReactNativeOptions("app-release.apk"),
            projectKind: ProjectKind.ReactNative));

        Assert.Equal(RunSessionState.Running, session.State);
        Assert.False(session.SupportsHotReload);
        Assert.DoesNotContain(session.Steps, step => step.Id == "metro");
        Assert.DoesNotContain(session.Steps, step => step.Id == "reverse");
        Assert.Empty(commands.ReverseCalls);
    }

    [Fact]
    public async Task ReactNativePipeline_StopsOwnedMetroWithTheLastSessionAndSharesItBeforeThat()
    {
        EnsureAndroidHost();
        var probeStatuses = new Queue<MetroPortStatus>([MetroPortStatus.Free]);
        var reloadResults = new Queue<(bool, string?)>([
            (true, null),
            (true, null),
            (false, "Metro is not running."),
        ]);
        await using var service = CreateService(
            planBuilder: new RunPlanBuilderStub { MetroRequest = TestProcessHelper.Create("sleep", "60000") },
            metroProbe: (_, _) => Task.FromResult(
                probeStatuses.Count > 0 ? probeStatuses.Dequeue() : MetroPortStatus.Running),
            metroReload: (_, _) => Task.FromResult(reloadResults.Dequeue()));

        var first = await service.RunAsync(CreateRequest(
            ReactNativeConfiguration(),
            options: ReactNativeOptions(),
            projectKind: ProjectKind.ReactNative));
        var second = await service.RunAsync(CreateRequest(
            ReactNativeConfiguration(),
            options: ReactNativeOptions(),
            projectKind: ProjectKind.ReactNative));

        var secondMetro = Assert.Single(second.Steps, step => step.Id == "metro");
        Assert.Contains("Reusing the Metro instance", secondMetro.OutputTail, StringComparison.Ordinal);

        var reloadWhileFirstRunning = await service.HotReloadAsync(second.Id);
        Assert.NotNull(reloadWhileFirstRunning);
        Assert.True(reloadWhileFirstRunning.Succeeded);

        await service.StopAsync(first.Id);

        var reloadAfterStoppingTheOwner = await service.HotReloadAsync(second.Id);
        Assert.NotNull(reloadAfterStoppingTheOwner);
        Assert.True(reloadAfterStoppingTheOwner.Succeeded);

        await service.StopAsync(second.Id);

        var reloadAfterEveryOwnerStopped = await service.HotReloadAsync(second.Id);
        Assert.NotNull(reloadAfterEveryOwnerStopped);
        Assert.False(reloadAfterEveryOwnerStopped.Succeeded);
        Assert.Contains("Metro is not running", reloadAfterEveryOwnerStopped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReactNativePipeline_FailsWithPrebuildGuidanceWithoutTheAndroidHost()
    {
        await using var service = CreateService(
            planBuilder: new RunPlanBuilderStub { MetroRequest = TestProcessHelper.Create("sleep", "60000") },
            metroProbe: (_, _) => Task.FromResult(MetroPortStatus.Running));
        var session = await service.RunAsync(CreateRequest(
            ReactNativeConfiguration(),
            options: ReactNativeOptions(),
            projectKind: ProjectKind.ReactNative));

        Assert.Equal(RunSessionState.Failed, session.State);
        Assert.Contains("npx expo prebuild", session.Error, StringComparison.Ordinal);
        var prepare = Assert.Single(session.Steps, step => step.Id == "prepare");
        Assert.Equal(RunStepState.Failed, prepare.State);
        Assert.DoesNotContain(session.Steps, step => step.Id == "metro");
    }

    private void EnsureAndroidHost()
    {
        Directory.CreateDirectory(Path.Combine(_root, "android"));
    }

    private static RunConfiguration ReactNativeConfiguration() => new()
    {
        Name = "RN debug",
        Kind = RunConfigurationKind.ReactNative,
        VariantName = "debug",
        Build = { Task = "assembleDebug", WorkingDirectory = "android" },
        Artifact = { Glob = "android/app/build/outputs/apk/debug/*.apk" },
        Launch = { PackageName = "com.example.rn_sample" },
    };

    private static RunOptions ReactNativeOptions(string artifactName = "app-debug.apk") => new()
    {
        SkipBuild = true,
        PrebuiltArtifactFiles = [$"/tmp/{artifactName}"],
        PrebuiltArtifactPath = $"/tmp/{artifactName}",
        PrebuiltPackageName = "com.example.rn_sample",
    };

    private static RunOptions FlutterOptions(string artifactName) => new()
    {
        SkipBuild = true,
        PrebuiltArtifactFiles = [$"/tmp/{artifactName}"],
        PrebuiltArtifactPath = $"/tmp/{artifactName}",
        PrebuiltPackageName = "com.example.flutter_sample",
    };

    private static RunSessionService CreateService(
        FakeRunner? runner = null,
        FakeArtifactResolver? artifacts = null,
        FakeDeviceCommands? commands = null,
        FakeLogcatService? logcat = null,
        IDeviceOperationCoordinator? coordinator = null,
        IRunPlanBuilder? planBuilder = null,
        MetroSession.PortProbe? metroProbe = null,
        MetroSession.ReloadSender? metroReload = null)
    {
        return new RunSessionService(
            planBuilder ?? new RunPlanBuilderStub(),
            artifacts ?? new FakeArtifactResolver(),
            commands ?? new FakeDeviceCommands(),
            logcat ?? new FakeLogcatService(new FakeLogcatSession()),
            new FakeSessionStore(),
            new FakeNotificationService(),
            runner ?? new FakeRunner(),
            coordinator: coordinator,
            metroProbe: metroProbe,
            metroReload: metroReload);
    }

    private RunRequest CreateRequest(
        RunConfiguration configuration,
        DeviceInfo? device = null,
        RunOptions? options = null,
        ProjectKind projectKind = ProjectKind.Custom)
    {
        var project = new ProjectModel
        {
            Id = "proj-1",
            Name = "Sample",
            RootPath = _root,
            Kind = projectKind,
        };
        if (projectKind == ProjectKind.ReactNative)
        {
            project.ReactNativeInfo = new ReactNativeProjectInfo
            {
                IsExpo = false,
                Workflow = "bare",
                MetroPort = 8081,
            };
        }

        return new RunRequest
        {
            Project = project,
            Configuration = configuration,
            Device = device ?? new DeviceInfo { Serial = "emulator-5554" },
            DeviceSerial = device?.Serial ?? "emulator-5554",
            Options = options ?? new RunOptions(),
        };
    }

    private static ProcessResult Result(int exitCode, string output = "") => new()
    {
        CommandLine = "fake",
        ExitCode = exitCode,
        StandardOutput = output,
        Duration = TimeSpan.FromMilliseconds(5),
    };

    private sealed class RunPlanBuilderStub : IRunPlanBuilder
    {
        public ProcessRequest? FlutterRequest { get; init; }

        public ProcessRequest? MetroRequest { get; init; }

        public RunPlanPreview BuildPreview(ProjectModel project, RunConfiguration configuration, string? deviceSerial = null) =>
            new();

        public ProcessRequest? CreateBuildRequest(ProjectModel project, RunConfiguration configuration) => null;

        public ProcessRequest? CreateFlutterRunRequest(
            ProjectModel project,
            RunConfiguration configuration,
            string? deviceSerial = null) => FlutterRequest;

        public ProcessRequest? CreateMetroStartRequest(ProjectModel project, RunConfiguration configuration) => MetroRequest;
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Func<ProcessRequest, ProcessResult>? Handler { get; init; }

        public Task<ProcessResult> RunAsync(
            ProcessRequest request,
            IProgress<ProcessOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Handler?.Invoke(request) ?? Result(0));
        }
    }

    private sealed class FakeArtifactResolver : IArtifactResolver
    {
        public ArtifactResolution Resolution { get; init; } = new()
        {
            Glob = string.Empty,
            Error = "No artifact glob is configured.",
        };

        public Task<ArtifactResolution> ResolveAsync(
            ProjectModel project,
            RunConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.FromResult(Resolution);
    }

    private sealed class FakeDeviceCommands : IDeviceCommandService
    {
        public string AdbExecutable => "adb";

        public ProcessResult? InstallResult { get; init; }

        public ProcessResult? LaunchResult { get; init; }

        public int? PackageUid { get; init; }

        public List<(string Serial, int DevicePort, int HostPort)> ReverseCalls { get; } = [];

        public bool ReverseSucceeds { get; init; } = true;

        public Task<DeviceCommandResult> InstallAsync(
            string serial,
            IReadOnlyList<string> apkPaths,
            AdbInstallOptions options,
            IProgress<ProcessOutputLine>? output = null,
            CancellationToken cancellationToken = default) => Task.FromResult(
            InstallResult is null
                ? new DeviceCommandResult { Succeeded = true, CommandLine = "adb install" }
                : ToDeviceCommandResult(InstallResult));

        public Task<DeviceCommandResult> LaunchAsync(
            string serial,
            AdbLaunchOptions options,
            CancellationToken cancellationToken = default) => Task.FromResult(
            LaunchResult is null
                ? new DeviceCommandResult
                {
                    Succeeded = true,
                    CommandLine = "adb shell am start -W",
                    Output = "Status: ok\nLaunchState: WARM\nTotalTime: 120",
                }
                : ToDeviceCommandResult(LaunchResult));

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
            CancellationToken cancellationToken = default)
        {
            ReverseCalls.Add((serial, devicePort, hostPort));
            return Task.FromResult(new DeviceCommandResult
            {
                Succeeded = ReverseSucceeds,
                CommandLine = $"adb -s {serial} reverse tcp:{devicePort} tcp:{hostPort}",
            });
        }

        public Task<string?> ResolveActivityAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<int?> GetPackageUidAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) => Task.FromResult(PackageUid);

        public Task<long?> GetFreeStorageAsync(string serial, CancellationToken cancellationToken = default) =>
            Task.FromResult<long?>(null);

        public Task<DeviceCommandResult> ClearLogcatAsync(
            string serial,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceCommandResult { Succeeded = true });

        private static DeviceCommandResult ToDeviceCommandResult(ProcessResult result) => new()
        {
            Succeeded = result.Succeeded,
            ExitCode = result.ExitCode,
            Output = result.StandardOutput,
            Error = result.StandardError,
        };
    }

    private sealed class FakeLogcatService : ILogcatService
    {
        private readonly FakeLogcatSession _session;

        public FakeLogcatService(FakeLogcatSession session) => _session = session;

        public IReadOnlyList<ILogcatSession> Sessions => [_session];

        public Task<ILogcatSession> StartAsync(LogcatRequest request, CancellationToken cancellationToken = default)
        {
            _session.Start(request);
            return Task.FromResult<ILogcatSession>(_session);
        }

        public Task StopAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ILogcatSession? GetSession(string sessionId) =>
            string.Equals(sessionId, _session.Id, StringComparison.Ordinal) ? _session : null;

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLogcatSession : ILogcatSession
    {
        private readonly List<LogEntry> _entries = [];

        public string Id { get; private set; } = Guid.NewGuid().ToString("N");

        public string DeviceSerial { get; private set; } = string.Empty;

        public string? PackageName { get; private set; }

        public int? AppUid { get; private set; }

        public bool IsRunning { get; private set; }

        public bool IsPaused { get; private set; }

        public int DroppedLines => 0;

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IReadOnlyList<CrashEvent> Crashes { get; } = [];

        public string? LastError { get; private set; }

        public event EventHandler<LogEntriesEventArgs>? EntriesAdded
        {
            add { }
            remove { }
        }

        public event EventHandler<CrashDetectedEventArgs>? CrashDetected;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public void Start(LogcatRequest request)
        {
            DeviceSerial = request.DeviceSerial;
            PackageName = request.PackageName;
            AppUid = request.AppUid;
            IsRunning = true;
        }

        public void RaiseCrash(string dedupeKey)
        {
            CrashDetected?.Invoke(this, new CrashDetectedEventArgs(new CrashEvent
            {
                Kind = CrashKind.Crash,
                PackageName = PackageName,
                Summary = "java.lang.IllegalStateException: boom",
                StackTrace = "FATAL EXCEPTION: main\n    at com.example.app.MainActivity.onCreate",
                Timestamp = Now,
                FirstSequence = 1,
                LastSequence = 2,
                DedupeKey = dedupeKey,
            }));
        }

        public void Pause() => IsPaused = true;

        public void Resume() => IsPaused = false;

        public void Dispose() => IsRunning = false;
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public Task SaveAsync(RunSession session, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<RunSession?> LoadAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<RunSession?>(null);

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionSummary>>([]);

        public Task<string> ExportAsync(
            RunSession session,
            SessionExportFormat format,
            bool redact = true,
            CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

        public Task<RetentionResult> ApplyRetentionAsync(
            int retentionDays,
            long maxBytes,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new RetentionResult());

        public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeNotificationService : INotificationService
    {
        public List<AppNotification> Notifications { get; } = [];

        public NotificationPreferences Preferences { get; set; } = new();

        public event EventHandler<AppNotification>? NotificationRaised;

        public void Notify(AppNotification notification)
        {
            Notifications.Add(notification);
            NotificationRaised?.Invoke(this, notification);
        }
    }
}

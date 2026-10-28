using DroidDeck.Core.Devices;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Notifications;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Sessions;

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
    public async Task CustomPipeline_CompletesEndToEndWithoutArtifactOrLaunch()
    {
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
    public async Task DuplicateCrashWithinWindowIsSuppressed()
    {
        var (service, session, logcat) = await RunWithAttachedLogcatAsync();
        await using (service)
        {
            logcat.RaiseCrash("stack-key-1");
            logcat.RaiseCrash("stack-key-1");
            logcat.RaiseCrash("stack-key-2");

            Assert.Equal(2, session.Crashes.Count);
            Assert.Equal("stack-key-1", session.Crashes[0].DedupeKey);
            Assert.Equal("stack-key-2", session.Crashes[1].DedupeKey);
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

    private static RunSessionService CreateService(
        FakeRunner? runner = null,
        FakeArtifactResolver? artifacts = null,
        FakeDeviceCommands? commands = null,
        FakeLogcatService? logcat = null)
    {
        return new RunSessionService(
            new RunPlanBuilderStub(),
            artifacts ?? new FakeArtifactResolver(),
            commands ?? new FakeDeviceCommands(),
            logcat ?? new FakeLogcatService(new FakeLogcatSession()),
            new FakeSessionStore(),
            new FakeNotificationService(),
            runner ?? new FakeRunner());
    }

    private RunRequest CreateRequest(RunConfiguration configuration, DeviceInfo? device = null)
    {
        return new RunRequest
        {
            Project = new ProjectModel
            {
                Id = "proj-1",
                Name = "Sample",
                RootPath = _root,
                Kind = ProjectKind.Custom,
            },
            Configuration = configuration,
            Device = device ?? new DeviceInfo { Serial = "emulator-5554" },
            DeviceSerial = device?.Serial ?? "emulator-5554",
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
        public RunPlanPreview BuildPreview(ProjectModel project, RunConfiguration configuration, string? deviceSerial = null) =>
            new();

        public ProcessRequest? CreateBuildRequest(ProjectModel project, RunConfiguration configuration) => null;
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

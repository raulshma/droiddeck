using DroidDeck.Core.Devices;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Notifications;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;

namespace DroidDeck.Core.Tests.Runs;

public sealed class MultiDeviceRunServiceTests
{
    private static readonly string Root = Path.GetTempPath();

    [Fact]
    public async Task RunAsync_BuildsOnceAndReusesTheArtifactOnEveryOtherDevice()
    {
        var sessions = new FakeRunSessions();
        using var coordinator = new DeviceOperationCoordinator();
        using var service = new MultiDeviceRunService(sessions, coordinator);
        var devices = Devices("a", "b", "c");

        var group = await service.RunAsync(CreateRequest(devices));

        Assert.Equal(RunGroupState.Succeeded, group.State);
        Assert.All(group.Devices, device => Assert.Equal(RunGroupState.Succeeded, device.State));
        Assert.Equal(3, sessions.Requests.Count);
        Assert.All(sessions.Requests, request => Assert.True(request.Options.DeviceLockHeld));
        var first = sessions.Requests[0];
        Assert.False(first.Options.SkipBuild);
        Assert.Null(first.Options.PrebuiltArtifactFiles);
        foreach (var request in sessions.Requests.Skip(1))
        {
            Assert.True(request.Options.SkipBuild);
            Assert.Equal(["/tmp/app.apk"], request.Options.PrebuiltArtifactFiles);
            Assert.Equal("com.example.app", request.Options.PrebuiltPackageName);
        }
    }

    [Fact]
    public async Task RunAsync_OneFailedInstallProducesPartiallyFailed()
    {
        var sessions = new FakeRunSessions { FailingSerials = ["b"] };
        using var coordinator = new DeviceOperationCoordinator();
        using var service = new MultiDeviceRunService(sessions, coordinator);

        var group = await service.RunAsync(CreateRequest(Devices("a", "b", "c")));

        Assert.Equal(RunGroupState.PartiallyFailed, group.State);
        Assert.Equal(RunGroupState.Succeeded, group.Devices[0].State);
        Assert.Equal(RunGroupState.Failed, group.Devices[1].State);
        Assert.Equal(RunGroupState.Succeeded, group.Devices[2].State);
        Assert.True(group.Devices[1].CanRetry);
    }

    [Fact]
    public async Task RetryFailedAsync_RetriesOnlyFailuresWithoutRebuilding()
    {
        var sessions = new FakeRunSessions { FailingSerials = ["b"] };
        using var coordinator = new DeviceOperationCoordinator();
        using var service = new MultiDeviceRunService(sessions, coordinator);
        var group = await service.RunAsync(CreateRequest(Devices("a", "b", "c")));
        sessions.FailingSerials.Clear();
        sessions.Requests.Clear();

        var retried = await service.RetryFailedAsync(group.Id);

        Assert.Equal(RunGroupState.Succeeded, retried.State);
        var request = Assert.Single(sessions.Requests);
        Assert.Equal("b", request.DeviceSerial);
        Assert.True(request.Options.SkipBuild);
        Assert.Equal(["/tmp/app.apk"], request.Options.PrebuiltArtifactFiles);
        Assert.Equal(2, retried.Devices[1].Attempts);
        Assert.Equal(1, retried.Devices[0].Attempts);
    }

    [Fact]
    public async Task RunAsync_EnforcesTheGlobalConcurrencyLimit()
    {
        var sessions = new FakeRunSessions { Delay = TimeSpan.FromMilliseconds(80) };
        using var coordinator = new DeviceOperationCoordinator(maxConcurrency: 2);
        using var service = new MultiDeviceRunService(sessions, coordinator, () => 2);

        var group = await service.RunAsync(CreateRequest(Devices("a", "b", "c", "d")));

        Assert.Equal(RunGroupState.Succeeded, group.State);
        Assert.Equal(2, sessions.MaxConcurrent);
    }

    [Fact]
    public async Task RunAsync_ReportsEachDeviceAsRunningUntilItsSessionFinishes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new FakeRunSessions { RunGate = gate.Task };
        using var coordinator = new DeviceOperationCoordinator();
        using var service = new MultiDeviceRunService(sessions, coordinator);
        var snapshots = new List<(RunGroupState Group, RunGroupState Device)>();
        service.GroupChanged += (_, args) =>
        {
            lock (snapshots)
            {
                snapshots.Add((args.Group.State, args.Group.Devices[0].State));
            }
        };

        var run = service.RunAsync(CreateRequest(Devices("a")));
        await sessions.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (snapshots)
        {
            Assert.Contains((RunGroupState.Running, RunGroupState.Running), snapshots);
        }

        Assert.Equal(0, service.Groups[0].SucceededCount);
        gate.SetResult();
        var group = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RunGroupState.Succeeded, group.State);
        Assert.Equal(RunGroupState.Succeeded, group.Devices[0].State);
        Assert.Equal(1, group.SucceededCount);
    }

    [Fact]
    public async Task Start_ReturnsTheRegisteredGroupBeforeAnyDeviceRuns()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = new FakeRunSessions { RunGate = gate.Task };
        using var coordinator = new DeviceOperationCoordinator();
        using var service = new MultiDeviceRunService(sessions, coordinator);

        var handle = service.Start(CreateRequest(Devices("a", "b")));

        Assert.Same(handle.Group, Assert.Single(service.Groups));
        Assert.Equal(["a", "b"], handle.Group.Devices.Select(device => device.Serial));
        Assert.False(handle.Completion.IsCompleted);

        gate.SetResult();
        var completed = await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(handle.Group, completed);
        Assert.Equal(RunGroupState.Succeeded, completed.State);
    }

    [Fact]
    public async Task RunAsync_FailsAllDevicesWhenTheFirstBuildProducesNoArtifact()
    {
        var sessions = new FakeRunSessions { ProduceArtifact = false };
        using var coordinator = new DeviceOperationCoordinator();
        using var service = new MultiDeviceRunService(sessions, coordinator);

        var group = await service.RunAsync(CreateRequest(Devices("a", "b")));

        Assert.Equal(RunGroupState.Failed, group.State);
        Assert.Equal(RunGroupState.Succeeded, group.Devices[0].State);
        Assert.Equal(RunGroupState.Failed, group.Devices[1].State);
        Assert.Single(sessions.Requests);
    }

    private static MultiDeviceRunRequest CreateRequest(IReadOnlyList<DeviceInfo> devices) => new()
    {
        Project = new ProjectModel
        {
            Id = "project",
            Name = "Sample",
            RootPath = Root,
        },
        Configuration = new RunConfiguration
        {
            Id = "debug",
            Name = "Debug",
            Kind = RunConfigurationKind.Custom,
        },
        Devices = devices,
    };

    private static DeviceInfo[] Devices(params string[] serials) =>
        serials.Select(serial => new DeviceInfo { Serial = serial, Model = serial.ToUpperInvariant() }).ToArray();

    private sealed class FakeRunSessions : IRunSessionService
    {
        private readonly List<RunSession> _sessions = [];
        private int _active;

        public List<RunRequest> Requests { get; } = [];

        public HashSet<string> FailingSerials { get; init; } = new(StringComparer.Ordinal);

        public bool ProduceArtifact { get; init; } = true;

        public TimeSpan Delay { get; init; }

        public Task? RunGate { get; init; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxConcurrent { get; private set; }

        public IReadOnlyList<RunSession> Sessions => _sessions;

        public event EventHandler<RunSessionChangedEventArgs>? SessionChanged;

        public event EventHandler<RunOutputEventArgs>? OutputReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<LogEntriesEventArgs>? LogEntriesReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<CrashDetectedEventArgs>? CrashDetected
        {
            add { }
            remove { }
        }

        public RunHandle Start(RunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var current = Interlocked.Increment(ref _active);
            MaxConcurrent = Math.Max(MaxConcurrent, current);
            Started.TrySetResult();

            var serial = request.DeviceSerial ?? request.Device?.Serial ?? string.Empty;
            var session = new RunSession
            {
                ProjectId = request.Project.Id,
                ProjectName = request.Project.Name,
                ProjectRoot = request.Project.RootPath,
                ConfigurationId = request.Configuration.Id,
                ConfigurationName = request.Configuration.Name,
                DeviceSerial = serial,
                DeviceName = request.Device?.Model ?? serial,
                GroupId = request.GroupId,
                State = FailingSerials.Contains(serial) ? RunSessionState.Failed : RunSessionState.Succeeded,
                Error = FailingSerials.Contains(serial) ? "INSTALL_FAILED_INSUFFICIENT_STORAGE" : null,
                PackageName = "com.example.app",
                Activity = "com.example.app.MainActivity",
            };
            if (ProduceArtifact)
            {
                session.ArtifactPath = "/tmp/app.apk";
                session.ArtifactFiles = ["/tmp/app.apk"];
            }

            _sessions.Add(session);
            SessionChanged?.Invoke(this, new RunSessionChangedEventArgs(session));
            return new RunHandle(session, CompleteAsync(session, cancellationToken));
        }

        public async Task<RunSession> RunAsync(RunRequest request, CancellationToken cancellationToken = default)
        {
            var handle = Start(request, cancellationToken);
            return await handle.Completion.ConfigureAwait(false);
        }

        private async Task<RunSession> CompleteAsync(RunSession session, CancellationToken cancellationToken)
        {
            try
            {
                if (RunGate is { } gate)
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
                }

                return session;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public RunSession? GetSession(string sessionId) =>
            _sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestartAppAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<HotReloadResult?> HotReloadAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<HotReloadResult?>(null);

        public Task<HotReloadResult?> HotRestartAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<HotReloadResult?>(null);

        public Task<RunSession> ApplyRecoveryAsync(
            string sessionId,
            RecoveryActionKind action,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void RemoveSession(string sessionId) => _sessions.RemoveAll(session => session.Id == sessionId);
    }
}

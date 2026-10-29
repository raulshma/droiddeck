using DroidDeck.Core.Performance;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Toolbox;

namespace DroidDeck.Core.Tests.Performance;

public sealed class PerformanceSamplerTests
{
    private const string Package = "com.example.app";

    [Fact]
    public async Task CaptureAsync_CollectsEverySeries()
    {
        var toolbox = new FakeToolbox
        {
            Cpu = """
                 10.0% 4211/com.example.app: 6.0% user + 4.0% kernel
                TOTAL: 20% user + 10% kernel
                """,
            Memory = "TOTAL PSS:   24576",
            Frames = """
                Total frames rendered: 1000
                Janky frames: 20 (2.00%)
                """,
            Battery = """
                level: 85
                temperature: 320
                """,
        };
        var sampler = new PerformanceSampler(toolbox, new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        var sample = await sampler.CaptureAsync("serial", Package);

        Assert.Equal(10, sample.CpuPercent);
        Assert.Equal(24, sample.MemoryMegabytes);
        Assert.Equal(85, sample.BatteryPercent);
        Assert.Equal(32, sample.BatteryTemperatureCelsius);
        Assert.Null(sample.Fps);
        Assert.Equal(2, sample.JankPercent);
        Assert.NotNull(sample.FrameStats);
        Assert.Equal(
            ["dumpsys cpuinfo", "dumpsys meminfo com.example.app", "dumpsys gfxinfo com.example.app", "dumpsys battery"],
            toolbox.Commands);
        Assert.Empty(sample.Warnings);
    }

    [Fact]
    public async Task CaptureAsync_SkipsPackageCommandsWithoutAnApplication()
    {
        var toolbox = new FakeToolbox
        {
            Cpu = "TOTAL: 12% user + 8% kernel",
            Battery = "level: 50",
        };
        var sampler = new PerformanceSampler(toolbox, new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        var sample = await sampler.CaptureAsync("serial");

        Assert.Equal(20, sample.CpuPercent);
        Assert.Null(sample.MemoryMegabytes);
        Assert.Null(sample.Fps);
        Assert.Null(sample.JankPercent);
        Assert.Equal(50, sample.BatteryPercent);
        Assert.Equal(["dumpsys cpuinfo", "dumpsys battery"], toolbox.Commands);
    }

    [Fact]
    public async Task CaptureAsync_KeepsPartialDataWhenOneCommandFails()
    {
        var toolbox = new FakeToolbox
        {
            Cpu = "TOTAL: 10% user + 5% kernel",
            Battery = "level: 70",
            FailingCommands = { "dumpsys gfxinfo com.example.app" },
        };
        var sampler = new PerformanceSampler(toolbox, new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        var sample = await sampler.CaptureAsync("serial", Package);

        Assert.Equal(15, sample.CpuPercent);
        Assert.Equal(70, sample.BatteryPercent);
        Assert.Null(sample.JankPercent);
        Assert.Single(sample.Warnings);
    }

    [Fact]
    public async Task RunAsync_ComputesTheFrameRateFromTheSamplingInterval()
    {
        var toolbox = new FakeToolbox
        {
            Cpu = "TOTAL: 10% user + 5% kernel",
            Battery = "level: 70",
            FramesPerCall =
            [
                """
                Total frames rendered: 1000
                Janky frames: 20 (2.00%)
                """,
                """
                Total frames rendered: 1060
                Janky frames: 26 (2.45%)
                """,
            ],
        };
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var sampler = new PerformanceSampler(toolbox, time);
        var samples = new List<PerformanceSample>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        var run = sampler.RunAsync(
            "serial",
            Package,
            TimeSpan.FromSeconds(1),
            sample =>
            {
                lock (samples)
                {
                    samples.Add(sample);
                }

                if (samples.Count == 1)
                {
                    first.TrySetResult();
                }
                else if (samples.Count == 2)
                {
                    second.TrySetResult();
                }

                return Task.CompletedTask;
            },
            cancellation.Token);

        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await time.WaitForTimerAsync().WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(1));
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, samples.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), samples[1].Timestamp - samples[0].Timestamp);
        Assert.Equal(60, samples[1].Fps);
        Assert.Equal(10, samples[1].JankPercent);
    }

    [Fact]
    public async Task RunAsync_StopsWhenCancelled()
    {
        var toolbox = new FakeToolbox
        {
            Cpu = "TOTAL: 10% user + 5% kernel",
            Battery = "level: 70",
        };
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var sampler = new PerformanceSampler(toolbox, time);
        using var cancellation = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = sampler.RunAsync(
            "serial",
            Package,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                first.TrySetResult();
                return Task.CompletedTask;
            },
            cancellation.Token);

        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.IsCompletedSuccessfully);
    }

    private sealed class FakeToolbox : IDeviceToolboxService
    {
        public List<string> Commands { get; } = [];

        public HashSet<string> FailingCommands { get; } = new(StringComparer.Ordinal);

        public string Cpu { get; set; } = string.Empty;

        public string Memory { get; set; } = string.Empty;

        public string Battery { get; set; } = string.Empty;

        public string Frames { get; set; } = string.Empty;

        public IReadOnlyList<string> FramesPerCall { get; set; } = [];

        private int _frameCall;

        public IReadOnlyList<ShellHistoryEntry> ShellHistory => [];

        public Task<ToolboxCommandResult> RunShellAsync(
            string serial,
            string command,
            bool allowDestructive = false,
            IProgress<ProcessOutputLine>? output = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (FailingCommands.Contains(command))
            {
                return Task.FromResult(ToolboxCommandResult.Failure("The command failed.", $"adb shell {command}"));
            }

            var text = command switch
            {
                "dumpsys cpuinfo" => Cpu,
                "dumpsys battery" => Battery,
                _ when command.StartsWith("dumpsys meminfo", StringComparison.Ordinal) => Memory,
                _ when command.StartsWith("dumpsys gfxinfo", StringComparison.Ordinal) => NextFrames(),
                _ => string.Empty,
            };
            return Task.FromResult(new ToolboxCommandResult
            {
                Succeeded = true,
                Message = "ok",
                CommandLine = $"adb shell {command}",
                Output = text,
            });
        }

        private string NextFrames()
        {
            if (FramesPerCall.Count == 0)
            {
                return Frames;
            }

            var index = Math.Min(_frameCall, FramesPerCall.Count - 1);
            _frameCall++;
            return FramesPerCall[index];
        }

        public Task<IReadOnlyList<ToolboxApp>> ListAppsAsync(
            string serial,
            AppListFilter filter,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ToolboxCommandResult> UninstallAppAsync(
            string serial,
            string packageName,
            bool confirmed,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ToolboxCommandResult> ClearAppDataAsync(
            string serial,
            string packageName,
            bool confirmed,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ToolboxCommandResult> ForceStopAppAsync(
            string serial,
            string packageName,
            bool confirmed,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ToolboxCommandResult> OpenAppInfoAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PermissionState>> ListPermissionsAsync(
            string serial,
            string packageName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ToolboxCommandResult> SetPermissionAsync(
            string serial,
            string packageName,
            string permission,
            bool grant,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void ClearShellHistory()
        {
        }

        public Task<IReadOnlyList<PortMapping>> ListPortsAsync(
            string serial,
            PortDirection direction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ToolboxCommandResult> AddPortAsync(
            string serial,
            PortDirection direction,
            string local,
            string remote,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ToolboxCommandResult> RemovePortAsync(
            string serial,
            PortDirection direction,
            string local,
            string remote,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ToolboxCommandResult> RemoveAllPortsAsync(
            string serial,
            PortDirection direction,
            bool confirmed,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;
        private TaskCompletionSource _timerAdded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate)
            {
                _timers.Add(timer);
                _timerAdded.TrySetResult();
                _timerAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return timer;
        }

        public Task WaitForTimerAsync()
        {
            lock (_gate)
            {
                if (_timers.Count > 0)
                {
                    return Task.CompletedTask;
                }

                return _timerAdded.Task;
            }
        }

        public void Advance(TimeSpan by)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _now += by;
                due = _timers.Where(timer => timer.IsDue(_now)).ToArray();
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private readonly object _gate = new();
            private DateTimeOffset? _dueAt;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;

            public bool IsDue(DateTimeOffset now)
            {
                lock (_gate)
                {
                    return _dueAt is { } due && due <= now;
                }
            }

            public void Fire()
            {
                lock (_gate)
                {
                    _dueAt = _period > TimeSpan.Zero ? owner.GetUtcNow() + _period : null;
                }

                callback(state);
                if (_dueAt is null)
                {
                    owner.Remove(this);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    _period = period;
                    _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
                }

                return true;
            }

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

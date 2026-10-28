using System.Collections.Concurrent;
using DroidDeck.Core.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Runs;

/// <summary>
/// Fans a single build out to many devices. The first device performs the normal
/// pipeline; every other device reuses that artifact and skips the build. Failed
/// devices can be retried without rebuilding.
/// </summary>
public sealed class MultiDeviceRunService : IMultiDeviceRunService, IDisposable
{
    private const int MaxRetainedGroups = 25;

    private readonly IRunSessionService _sessions;
    private readonly IDeviceOperationCoordinator _coordinator;
    private readonly Func<int>? _concurrencyProvider;
    private readonly ILogger<MultiDeviceRunService> _logger;
    private readonly List<RunGroup> _groups = [];
    private readonly Dictionary<string, GroupContext> _contexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string GroupId, string Serial)> _sessionOwners = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Creates the orchestrator.</summary>
    public MultiDeviceRunService(
        IRunSessionService sessions,
        IDeviceOperationCoordinator coordinator,
        Func<int>? concurrencyProvider = null,
        ILogger<MultiDeviceRunService>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _concurrencyProvider = concurrencyProvider;
        _logger = logger ?? NullLogger<MultiDeviceRunService>.Instance;
        _sessions.SessionChanged += OnSessionChanged;
    }

    /// <inheritdoc />
    public IReadOnlyList<RunGroup> Groups
    {
        get
        {
            lock (_sync)
            {
                return _groups.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<RunGroupChangedEventArgs>? GroupChanged;

    /// <inheritdoc />
    public async Task<RunGroup> RunAsync(MultiDeviceRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.Devices.Count == 0)
        {
            throw new ArgumentException("Select at least one device.", nameof(request));
        }

        var devices = request.Devices
            .GroupBy(device => device.Serial, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        var group = CreateGroup(request, devices);
        var context = new GroupContext(group, request.Project, request.Configuration);
        lock (_sync)
        {
            _groups.Insert(0, group);
            _contexts[group.Id] = context;
            PruneGroupsLocked();
        }

        ApplyConcurrencyLimit();
        RaiseChanged(group);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Cancellation.Token);
        context.Linked = linked;
        try
        {
            if (request.DryRun)
            {
                await RunDryAsync(request, group, devices, linked.Token).ConfigureAwait(false);
            }
            else
            {
                await RunBuildOnceAsync(request, group, devices, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            CompleteGroup(group, RunGroupState.Cancelled, "The group run was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Multi-device run {GroupId} failed unexpectedly.", group.Id);
            CompleteGroup(group, RunGroupState.Failed, exception.Message);
        }
        finally
        {
            context.Linked = null;
        }

        RaiseChanged(group);
        return group;
    }

    /// <inheritdoc />
    public async Task<RunGroup> RetryFailedAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var (group, context) = FindGroup(groupId);
        if (context.Running)
        {
            throw new InvalidOperationException("Wait for the current group run to finish before retrying failed devices.");
        }

        var failed = group.Devices
            .Where(device => device.CanRetry)
            .ToArray();
        if (failed.Length == 0)
        {
            return group;
        }

        if (group.PrebuiltArtifactFiles.Count == 0)
        {
            throw new InvalidOperationException(
                "No built APK is available for this group. Run the project again so the artifact can be rebuilt.");
        }

        ApplyConcurrencyLimit();
        context.Running = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Cancellation.Token);
        context.Linked = linked;
        group.State = RunGroupState.Running;
        group.EndedAt = null;
        group.Error = null;
        RaiseChanged(group);
        try
        {
            await Task.WhenAll(failed.Select(device => RunChildAsync(
                group,
                context,
                device,
                device.DeviceName,
                BuildSharedArtifactRequest(
                    group,
                    device,
                    group.PrebuiltArtifactFiles,
                    context,
                    "Retrying with the artifact built for this device group."),
                linked.Token))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            // Individual children record their cancelled state.
        }
        finally
        {
            context.Running = false;
            context.Linked = null;
        }

        RecomputeGroupState(group);
        RaiseChanged(group);
        return group;
    }

    /// <inheritdoc />
    public async Task CancelAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var (group, context) = FindGroup(groupId);
        context.Cancellation.Cancel();
        context.Linked?.Cancel();
        var children = group.Devices
            .Select(device => device.SessionId)
            .Where(sessionId => !string.IsNullOrWhiteSpace(sessionId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var sessionId in children)
        {
            await _sessions.CancelAsync(sessionId!, cancellationToken).ConfigureAwait(false);
        }

        CompleteGroup(group, RunGroupState.Cancelled, "The group run was cancelled.");
        RaiseChanged(group);
    }

    /// <inheritdoc />
    public RunGroup? GetGroup(string groupId)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return null;
        }

        lock (_sync)
        {
            return _groups.FirstOrDefault(group => string.Equals(group.Id, groupId, StringComparison.Ordinal));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessions.SessionChanged -= OnSessionChanged;
        GroupContext[] contexts;
        lock (_sync)
        {
            contexts = _contexts.Values.ToArray();
            _contexts.Clear();
        }

        foreach (var context in contexts)
        {
            context.Cancellation.Cancel();
            context.Cancellation.Dispose();
        }
    }

    private async Task RunBuildOnceAsync(
        MultiDeviceRunRequest request,
        RunGroup group,
        DeviceInfo[] devices,
        CancellationToken cancellationToken)
    {
        var context = GetContext(group.Id);
        context.Running = true;
        group.State = RunGroupState.Running;
        RaiseChanged(group);
        try
        {
            var first = group.Devices[0];
            await RunChildAsync(
                group,
                context,
                first,
                devices[0].Model,
                BuildRequest(request, group, devices[0]),
                cancellationToken).ConfigureAwait(false);

            var builtFiles = GetFirstArtifactFiles(request, group, first);
            if (builtFiles.Count == 0)
            {
                var message = GetSession(first)?.Error
                    ?? "The first device did not produce an APK, so the other devices cannot install it.";
                foreach (var device in group.Devices.Skip(1))
                {
                    if (device.SessionId is null)
                    {
                        device.State = RunGroupState.Failed;
                        device.Error = message;
                    }
                }

                group.Error = message;
                CompleteGroup(group, RunGroupState.Failed, message);
                return;
            }

            group.PrebuiltArtifactFiles = [.. builtFiles];
            group.PackageName = GetSession(first)?.PackageName ?? request.Configuration.Launch.PackageName;
            group.Activity = GetSession(first)?.Activity;
            RaiseChanged(group);

            var remaining = group.Devices.Skip(1).ToArray();
            var deviceBySerial = devices.ToDictionary(device => device.Serial, StringComparer.Ordinal);
            await Task.WhenAll(remaining.Select(device => RunChildAsync(
                group,
                context,
                device,
                deviceBySerial.TryGetValue(device.Serial, out var info) ? info.Model : device.DeviceName,
                BuildSharedArtifactRequest(
                    group,
                    device,
                    builtFiles,
                    context,
                    "Reusing the artifact built for this device group."),
                cancellationToken))).ConfigureAwait(false);

            RecomputeGroupState(group);
        }
        finally
        {
            context.Running = false;
        }
    }

    private async Task RunDryAsync(
        MultiDeviceRunRequest request,
        RunGroup group,
        DeviceInfo[] devices,
        CancellationToken cancellationToken)
    {
        var context = GetContext(group.Id);
        context.Running = true;
        group.State = RunGroupState.Running;
        RaiseChanged(group);
        try
        {
            await Task.WhenAll(group.Devices.Select((device, index) => RunChildAsync(
                group,
                context,
                device,
                devices[index].Model,
                BuildRequest(request, group, devices[index]),
                cancellationToken))).ConfigureAwait(false);
            RecomputeGroupState(group);
        }
        finally
        {
            context.Running = false;
        }
    }

    private async Task RunChildAsync(
        RunGroup group,
        GroupContext context,
        RunGroupDevice device,
        string? deviceName,
        RunRequest request,
        CancellationToken cancellationToken)
    {
        device.DeviceName = deviceName ?? device.DeviceName ?? device.Serial;
        device.Error = null;
        await using var lease = await _coordinator
            .AcquireAsync(device.Serial, $"Run {group.ProjectName} on {device.Serial}", cancellationToken)
            .ConfigureAwait(false);
        device.Attempts++;
        device.State = RunGroupState.Running;
        RaiseChanged(group);
        try
        {
            var session = await _sessions.RunAsync(request, cancellationToken).ConfigureAwait(false);
            device.SessionId = session.Id;
            _sessionOwners[session.Id] = (group.Id, device.Serial);
            device.State = ToGroupState(session);
            device.Error = session.Error;
            if (session.State == RunSessionState.PartiallyFailed)
            {
                device.Error ??= "One or more steps failed on this device.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            device.State = RunGroupState.Cancelled;
            device.Error = "The device run was cancelled.";
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            device.State = RunGroupState.Failed;
            device.Error = exception.Message;
            _logger.LogWarning(exception, "Device {Serial} failed in group {GroupId}.", device.Serial, group.Id);
        }

        RaiseChanged(group);
    }

    private static RunRequest BuildRequest(MultiDeviceRunRequest request, RunGroup group, DeviceInfo device) => new()
    {
        Project = request.Project,
        Configuration = request.Configuration,
        Device = device,
        DeviceSerial = device.Serial,
        GroupId = group.Id,
        DryRun = request.DryRun,
        Options = request.Options with { DeviceLockHeld = true },
    };

    private static RunRequest BuildSharedArtifactRequest(
        RunGroup group,
        RunGroupDevice device,
        List<string> files,
        GroupContext context,
        string description) => new()
    {
        Project = context.Project,
        Configuration = context.Configuration,
        DeviceSerial = device.Serial,
        GroupId = group.Id,
        Options = new RunOptions
        {
            SkipBuild = true,
            PrebuiltArtifactFiles = files,
            PrebuiltArtifactPath = files[0],
            PrebuiltPackageName = group.PackageName,
            PrebuiltActivity = group.Activity,
            PrebuiltArtifactDescription = description,
            DeviceLockHeld = true,
        },
    };

    private List<string> GetFirstArtifactFiles(MultiDeviceRunRequest request, RunGroup group, RunGroupDevice first)
    {
        var session = GetSession(first);
        if (session is not null && session.ArtifactFiles.Count > 0)
        {
            return [.. session.ArtifactFiles];
        }

        if (session?.ArtifactPath is { Length: > 0 } path)
        {
            return [path];
        }

        _logger.LogWarning(
            "The first child of group {GroupId} did not expose an artifact file for {Project}.",
            group.Id,
            request.Project.Name);
        return [];
    }

    private RunSession? GetSession(RunGroupDevice device) =>
        string.IsNullOrWhiteSpace(device.SessionId) ? null : _sessions.GetSession(device.SessionId);

    private void OnSessionChanged(object? sender, RunSessionChangedEventArgs args)
    {
        if (!_sessionOwners.TryGetValue(args.Session.Id, out var owner))
        {
            return;
        }

        RunGroup? changed = null;
        lock (_sync)
        {
            if (!_contexts.TryGetValue(owner.GroupId, out var context))
            {
                return;
            }

            var device = context.Group.Devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Serial, owner.Serial, StringComparison.Ordinal) &&
                string.Equals(candidate.SessionId, args.Session.Id, StringComparison.Ordinal));
            if (device is null)
            {
                return;
            }

            device.State = ToGroupState(args.Session);
            device.Error = args.Session.Error;
            changed = context.Group;
        }

        if (changed is not null)
        {
            RaiseChanged(changed);
        }
    }

    private static void RecomputeGroupState(RunGroup group)
    {
        if (group.Devices.Any(device => device.State is RunGroupState.Running or RunGroupState.Queued))
        {
            group.State = RunGroupState.Running;
            group.EndedAt = null;
            return;
        }

        var succeeded = group.Devices.Count(device => device.State is RunGroupState.Succeeded);
        var failed = group.Devices.Count(device => device.State is RunGroupState.Failed);
        var cancelled = group.Devices.Count(device => device.State is RunGroupState.Cancelled);
        var state = (succeeded, failed, cancelled) switch
        {
            (_, 0, 0) when succeeded == group.Devices.Count => RunGroupState.Succeeded,
            (0, _, 0) when failed == group.Devices.Count => RunGroupState.Failed,
            (0, 0, _) when cancelled == group.Devices.Count => RunGroupState.Cancelled,
            _ => RunGroupState.PartiallyFailed,
        };
        CompleteGroup(group, state, group.Error);
    }

    private static void CompleteGroup(RunGroup group, RunGroupState state, string? error)
    {
        group.State = state;
        group.Error = error;
        var now = DateTimeOffset.UtcNow;
        group.EndedAt = now;
        group.Duration = now >= group.StartedAt ? now - group.StartedAt : TimeSpan.Zero;
    }

    private void ApplyConcurrencyLimit()
    {
        if (_concurrencyProvider?.Invoke() is { } limit && limit > 0 && limit != _coordinator.MaxConcurrency)
        {
            _coordinator.MaxConcurrency = limit;
        }
    }

    private GroupContext GetContext(string groupId)
    {
        lock (_sync)
        {
            return _contexts[groupId];
        }
    }

    private void PruneGroupsLocked()
    {
        while (_groups.Count > MaxRetainedGroups)
        {
            var oldest = _groups[^1];
            if (!oldest.IsFinished)
            {
                break;
            }

            _groups.RemoveAt(_groups.Count - 1);
            if (!_contexts.Remove(oldest.Id))
            {
                continue;
            }

            foreach (var device in oldest.Devices)
            {
                if (device.SessionId is { Length: > 0 } sessionId)
                {
                    _sessionOwners.TryRemove(sessionId, out _);
                }
            }
        }
    }

    private (RunGroup Group, GroupContext Context) FindGroup(string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        lock (_sync)
        {
            if (!_contexts.TryGetValue(groupId, out var context))
            {
                throw new InvalidOperationException($"Run group \"{groupId}\" was not found.");
            }

            return (context.Group, context);
        }
    }

    private static RunGroup CreateGroup(MultiDeviceRunRequest request, IReadOnlyList<DeviceInfo> devices) => new()
    {
        ProjectId = request.Project.Id,
        ProjectName = request.Project.Name,
        ConfigurationId = request.Configuration.Id,
        ConfigurationName = request.Configuration.Name,
        GroupName = request.GroupName,
        State = RunGroupState.Queued,
        Devices = devices
            .Select(device => new RunGroupDevice
            {
                Serial = device.Serial,
                DeviceName = device.Model ?? device.Serial,
                State = RunGroupState.Queued,
            })
            .ToList(),
    };

    private static RunGroupState ToGroupState(RunSession session) => session.State switch
    {
        RunSessionState.Succeeded => RunGroupState.Succeeded,
        RunSessionState.PartiallyFailed => RunGroupState.PartiallyFailed,
        RunSessionState.Cancelled => RunGroupState.Cancelled,
        RunSessionState.Failed => RunGroupState.Failed,
        RunSessionState.Queued => RunGroupState.Queued,
        _ => RunGroupState.Running,
    };

    private void RaiseChanged(RunGroup group)
    {
        try
        {
            GroupChanged?.Invoke(this, new RunGroupChangedEventArgs(group));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A run-group handler failed for {GroupId}.", group.Id);
        }
    }

    private sealed class GroupContext
    {
        public GroupContext(RunGroup group, Projects.ProjectModel project, Projects.RunConfiguration configuration)
        {
            Group = group;
            Project = project;
            Configuration = configuration;
        }

        public RunGroup Group { get; }

        public Projects.ProjectModel Project { get; }

        public Projects.RunConfiguration Configuration { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public CancellationTokenSource? Linked { get; set; }

        public bool Running { get; set; }
    }
}

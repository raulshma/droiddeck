namespace DroidDeck.Core.Resources;

/// <summary>A class of work that consumes the app's screen or process budget.</summary>
public enum ResourceKind
{
    /// <summary>A Gradle/MSBuild build.</summary>
    Build,

    /// <summary>An interactive scrcpy mirror.</summary>
    Mirror,

    /// <summary>A background screen recorder.</summary>
    Recording,

    /// <summary>A thumbnail capture loop.</summary>
    Thumbnail,
}

/// <summary>Limits enforced by the resource guard.</summary>
public sealed record ResourceLimits
{
    /// <summary>Gets the maximum concurrent builds.</summary>
    public int MaxBuilds { get; init; } = 1;

    /// <summary>Gets the maximum concurrent mirrors.</summary>
    public int MaxMirrors { get; init; } = 3;

    /// <summary>Gets the maximum concurrent recordings.</summary>
    public int MaxRecordings { get; init; } = 2;

    /// <summary>Gets the percentage at which a warning is raised before the hard limit.</summary>
    public int WarnAtPercent { get; init; } = 80;
}

/// <summary>One resource consumer.</summary>
/// <param name="Kind">The resource kind.</param>
/// <param name="Detail">A human-readable description, typically the device serial.</param>
/// <param name="StartedAt">When the lease was acquired.</param>
public sealed record ResourceLeaseInfo(ResourceKind Kind, string Detail, DateTimeOffset StartedAt);

/// <summary>A snapshot of current resource use and limits.</summary>
public sealed record ResourceSnapshot(
    ResourceLimits Limits,
    IReadOnlyList<ResourceLeaseInfo> Active,
    IReadOnlyDictionary<ResourceKind, int> Counts);

/// <summary>Raised when a resource category approaches its limit.</summary>
public sealed class ResourceWarningEventArgs : EventArgs
{
    /// <summary>Creates a warning.</summary>
    public ResourceWarningEventArgs(ResourceKind kind, int count, int limit, string message)
    {
        Kind = kind;
        Count = count;
        Limit = limit;
        Message = message;
    }

    /// <summary>Gets the resource kind.</summary>
    public ResourceKind Kind { get; }

    /// <summary>Gets the current count.</summary>
    public int Count { get; }

    /// <summary>Gets the hard limit.</summary>
    public int Limit { get; }

    /// <summary>Gets the warning text that names the consumers.</summary>
    public string Message { get; }
}

/// <summary>
/// Counts mirrors, recordings, and builds, warns before limits, and blocks new
/// work when a hard limit is reached so handles and memory cannot be exhausted.
/// </summary>
public interface IResourceGuard
{
    /// <summary>Gets the current limits.</summary>
    ResourceLimits Limits { get; }

    /// <summary>Gets a snapshot of active consumers and counts.</summary>
    ResourceSnapshot Snapshot { get; }

    /// <summary>Replaces the limits. The next acquisition uses the new values.</summary>
    void UpdateLimits(ResourceLimits limits);

    /// <summary>Acquires a slot, waiting until the category has capacity.</summary>
    Task<IAsyncDisposable> AcquireAsync(
        ResourceKind kind,
        string detail,
        CancellationToken cancellationToken = default);

    /// <summary>Tries to acquire a slot without waiting.</summary>
    bool TryAcquire(ResourceKind kind, string detail, out IAsyncDisposable? lease);

    /// <summary>Builds an explanation of what is currently consuming the budget.</summary>
    string Explain();

    /// <summary>Raised when a category crosses the warning threshold, reaches its limit, or blocks a request.</summary>
    event EventHandler<ResourceWarningEventArgs>? WarningRaised;
}

/// <summary>Default resource guard that supports changing limits at runtime.</summary>
public sealed class ResourceGuard : IResourceGuard, IDisposable
{
    private readonly List<ResourceLeaseInfo> _active = [];
    private readonly object _sync = new();
    private ResourceLimits _limits;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    /// <summary>Creates a guard with the supplied limits.</summary>
    public ResourceGuard(ResourceLimits? limits = null)
    {
        _limits = Normalize(limits ?? new ResourceLimits());
    }

    /// <inheritdoc />
    public ResourceLimits Limits
    {
        get
        {
            lock (_sync)
            {
                return _limits;
            }
        }
    }

    /// <inheritdoc />
    public ResourceSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                var counts = _active
                    .GroupBy(lease => lease.Kind)
                    .ToDictionary(group => group.Key, group => group.Count());
                return new ResourceSnapshot(_limits, _active.ToArray(), counts);
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ResourceWarningEventArgs>? WarningRaised;

    /// <inheritdoc />
    public void UpdateLimits(ResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var normalized = Normalize(limits);
        TaskCompletionSource changed;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _limits = normalized;
            changed = SignalLocked();
        }

        changed.TrySetResult();
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAsync(
        ResourceKind kind,
        string detail,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        var warned = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? changed = null;
            Lease? lease = null;
            ResourceWarningEventArgs? warning = null;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (CountLocked(kind) < LimitFor(_limits, kind))
                {
                    (lease, warning) = RegisterLocked(kind, detail);
                }
                else
                {
                    changed = _changed.Task;
                    if (!warned)
                    {
                        warning = BlockedLocked(kind, detail);
                        warned = true;
                    }
                }
            }

            if (lease is not null)
            {
                RaiseWarning(warning);
                return lease;
            }

            RaiseWarning(warning);
            await changed!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public bool TryAcquire(ResourceKind kind, string detail, out IAsyncDisposable? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        ResourceWarningEventArgs? warning;
        bool acquired;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CountLocked(kind) >= LimitFor(_limits, kind))
            {
                lease = null;
                warning = BlockedLocked(kind, detail);
                acquired = false;
            }
            else
            {
                (lease, warning) = RegisterLocked(kind, detail);
                acquired = true;
            }
        }

        RaiseWarning(warning);
        return acquired;
    }

    /// <inheritdoc />
    public string Explain()
    {
        ResourceSnapshot snapshot = Snapshot;
        if (snapshot.Active.Count == 0)
        {
            return "No builds, mirrors, or recordings are active.";
        }

        return string.Join(
            Environment.NewLine,
            snapshot.Active
                .OrderBy(lease => lease.Kind)
                .Select(lease => $"{lease.Kind}: {lease.Detail} (since {lease.StartedAt:HH:mm:ss})")
                .Prepend(
                    $"Active resources: {snapshot.Active.Count}. " +
                    $"Limits: {snapshot.Limits.MaxBuilds} build(s), " +
                    $"{snapshot.Limits.MaxMirrors} mirror(s), {snapshot.Limits.MaxRecordings} recording(s)."));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SignalLocked().TrySetResult();
        }
    }

    private (Lease Lease, ResourceWarningEventArgs? Warning) RegisterLocked(ResourceKind kind, string detail)
    {
        var info = new ResourceLeaseInfo(kind, detail, DateTimeOffset.UtcNow);
        _active.Add(info);
        var count = CountLocked(kind);
        var limit = LimitFor(_limits, kind);
        var threshold = limit <= 1
            ? 0
            : Math.Clamp((int)Math.Ceiling(limit * (_limits.WarnAtPercent / 100d)), 1, limit - 1);
        ResourceWarningEventArgs? warning = null;
        if (count >= limit && limit > 1)
        {
            warning = new ResourceWarningEventArgs(
                kind,
                count,
                limit,
                $"{kind} limit reached ({count}/{limit}). " + Describe(_active, kind));
        }
        else if (threshold > 0 && count >= threshold)
        {
            warning = new ResourceWarningEventArgs(
                kind,
                count,
                limit,
                $"{kind} use is approaching the limit ({count}/{limit}). " + Describe(_active, kind));
        }

        return (new Lease(this, info), warning);
    }

    private ResourceWarningEventArgs BlockedLocked(ResourceKind kind, string detail)
    {
        var count = CountLocked(kind);
        var limit = LimitFor(_limits, kind);
        return new ResourceWarningEventArgs(
            kind,
            count,
            limit,
            $"{kind} limit is full ({count}/{limit}); cannot start {detail}. " + Describe(_active, kind));
    }

    private void Release(ResourceLeaseInfo info)
    {
        TaskCompletionSource changed;
        lock (_sync)
        {
            _active.Remove(info);
            changed = SignalLocked();
        }

        changed.TrySetResult();
    }

    private TaskCompletionSource SignalLocked()
    {
        var previous = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return previous;
    }

    private int CountLocked(ResourceKind kind) => _active.Count(lease => lease.Kind == kind);

    private void RaiseWarning(ResourceWarningEventArgs? args)
    {
        if (args is null)
        {
            return;
        }

        try
        {
            WarningRaised?.Invoke(this, args);
        }
        catch (Exception)
        {
            // A warning handler must never break resource accounting.
        }
    }

    private static int LimitFor(ResourceLimits limits, ResourceKind kind) => kind switch
    {
        ResourceKind.Build => Math.Max(1, limits.MaxBuilds),
        ResourceKind.Mirror => Math.Max(1, limits.MaxMirrors),
        ResourceKind.Recording => Math.Max(1, limits.MaxRecordings),
        ResourceKind.Thumbnail => Math.Max(1, limits.MaxMirrors),
        _ => 1,
    };

    private static string Describe(IEnumerable<ResourceLeaseInfo> active, ResourceKind kind) =>
        "Consumers: " + string.Join(", ", active.Where(lease => lease.Kind == kind).Select(lease => lease.Detail)) + ".";

    private static ResourceLimits Normalize(ResourceLimits limits) => limits with
    {
        MaxBuilds = Math.Max(1, limits.MaxBuilds),
        MaxMirrors = Math.Max(1, limits.MaxMirrors),
        MaxRecordings = Math.Max(1, limits.MaxRecordings),
        WarnAtPercent = Math.Clamp(limits.WarnAtPercent, 1, 100),
    };

    private sealed class Lease : IAsyncDisposable
    {
        private readonly ResourceGuard _owner;
        private readonly ResourceLeaseInfo _info;
        private int _released;

        public Lease(ResourceGuard owner, ResourceLeaseInfo info)
        {
            _owner = owner;
            _info = info;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release(_info);
            }

            return ValueTask.CompletedTask;
        }
    }
}

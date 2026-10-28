using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Devices;

/// <summary>Describes a mutating operation that currently holds a device lock.</summary>
/// <param name="Serial">The device serial.</param>
/// <param name="Operation">The operation name shown in diagnostics.</param>
/// <param name="StartedAt">When the operation acquired the lock.</param>
public sealed record DeviceOperationInfo(string Serial, string Operation, DateTimeOffset StartedAt);

/// <summary>
/// Serializes mutating operations per device and enforces the global concurrency
/// limit across all devices.
/// </summary>
public interface IDeviceOperationCoordinator
{
    /// <summary>Gets or sets the maximum number of concurrent device operations.</summary>
    int MaxConcurrency { get; set; }

    /// <summary>Gets the operations that currently hold a device lock.</summary>
    IReadOnlyList<DeviceOperationInfo> ActiveOperations { get; }

    /// <summary>
    /// Acquires the device lock and a global concurrency slot. Dispose the result
    /// to release both.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(
        string serial,
        string operation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default coordinator. Locks are acquired per device first, then a global slot,
/// so a device never holds a global slot while waiting for another operation on
/// the same device.
/// </summary>
public sealed class DeviceOperationCoordinator : IDeviceOperationCoordinator, IDisposable
{
    private readonly Dictionary<string, SemaphoreSlim> _deviceLocks = new(StringComparer.Ordinal);
    private readonly List<DeviceOperationInfo> _active = [];
    private readonly object _sync = new();
    private readonly ILogger<DeviceOperationCoordinator> _logger;
    private readonly ResizableGate _global;
    private int _maxConcurrency;
    private bool _disposed;

    /// <summary>Creates a coordinator with the supplied concurrency limit.</summary>
    public DeviceOperationCoordinator(int maxConcurrency = 3, ILogger<DeviceOperationCoordinator>? logger = null)
    {
        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "The concurrency limit must be at least one.");
        }

        _maxConcurrency = maxConcurrency;
        _global = new ResizableGate(maxConcurrency);
        _logger = logger ?? NullLogger<DeviceOperationCoordinator>.Instance;
    }

    /// <inheritdoc />
    public int MaxConcurrency
    {
        get
        {
            lock (_sync)
            {
                return _maxConcurrency;
            }
        }

        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The concurrency limit must be at least one.");
            }

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (value == _maxConcurrency)
                {
                    return;
                }

                _maxConcurrency = value;
                _global.SetLimit(value);
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "The device concurrency limit changed to {Limit}; waiting operations are released up to the new limit.",
                    value);
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DeviceOperationInfo> ActiveOperations
    {
        get
        {
            lock (_sync)
            {
                return _active.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncDisposable> AcquireAsync(
        string serial,
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var deviceLock = GetDeviceLock(serial);
        await deviceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _global.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                deviceLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // The coordinator was disposed while this operation was waiting.
            }

            throw;
        }

        var info = new DeviceOperationInfo(serial, operation, DateTimeOffset.UtcNow);
        lock (_sync)
        {
            _active.Add(info);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Acquired device lock for {Serial} ({Operation}).", serial, operation);
        }

        return new Lease(this, serial, deviceLock, info);
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
        }

        _global.Dispose();
        foreach (var deviceLock in _deviceLocks.Values)
        {
            deviceLock.Dispose();
        }

        _deviceLocks.Clear();
    }

    private SemaphoreSlim GetDeviceLock(string serial)
    {
        lock (_sync)
        {
            if (!_deviceLocks.TryGetValue(serial, out var deviceLock))
            {
                deviceLock = new SemaphoreSlim(1, 1);
                _deviceLocks[serial] = deviceLock;
            }

            return deviceLock;
        }
    }

    private void Release(string serial, SemaphoreSlim deviceLock, DeviceOperationInfo info)
    {
        lock (_sync)
        {
            _active.Remove(info);
        }

        _global.Release();
        try
        {
            deviceLock.Release();
        }
        catch (ObjectDisposedException)
        {
            // The coordinator was disposed while the operation was still unwinding.
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Released device lock for {Serial} ({Operation}).", serial, info.Operation);
        }
    }

    /// <summary>
    /// A small semaphore that can change its limit while operations are running.
    /// Lowering the limit only takes effect as permits are released, so an active
    /// operation never loses its slot.
    /// </summary>
    private sealed class ResizableGate
    {
        private readonly object _sync = new();
        private readonly LinkedList<Waiter> _waiters = new();
        private int _limit;
        private int _inUse;
        private bool _disposed;

        public ResizableGate(int limit) => _limit = limit;

        public Task AcquireAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_inUse < _limit && _waiters.Count == 0)
                {
                    _inUse++;
                    return Task.CompletedTask;
                }

                var waiter = new Waiter();
                waiter.Node = _waiters.AddLast(waiter);
                waiter.Registration = cancellationToken.Register(
                    static state =>
                    {
                        var (gate, pending) = ((ResizableGate, Waiter))state!;
                        gate.Cancel(pending);
                    },
                    (this, waiter));
                return waiter.Task;
            }
        }

        public void SetLimit(int limit)
        {
            List<Waiter>? granted;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _limit = limit;
                granted = GrantLocked();
            }

            Complete(granted);
        }

        public void Release()
        {
            List<Waiter>? granted;
            lock (_sync)
            {
                if (_inUse == 0)
                {
                    throw new InvalidOperationException("No global slot is held.");
                }

                _inUse--;
                granted = GrantLocked();
            }

            Complete(granted);
        }

        public void Dispose()
        {
            List<Waiter> waiters;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                waiters = [.. _waiters];
                _waiters.Clear();
            }

            foreach (var waiter in waiters)
            {
                if (waiter.TryCancel())
                {
                    waiter.CancelTask();
                }
            }
        }

        private List<Waiter>? GrantLocked()
        {
            List<Waiter>? granted = null;
            while (_inUse < _limit && _waiters.First is { } node)
            {
                _waiters.RemoveFirst();
                var waiter = node.Value;
                if (!waiter.TryGrant())
                {
                    continue;
                }

                _inUse++;
                (granted ??= []).Add(waiter);
            }

            return granted;
        }

        private void Cancel(Waiter waiter)
        {
            var canceled = false;
            lock (_sync)
            {
                if (waiter.TryCancel())
                {
                    if (waiter.Node?.List == _waiters)
                    {
                        _waiters.Remove(waiter.Node);
                    }

                    canceled = true;
                }
            }

            if (canceled)
            {
                waiter.CancelTask();
            }
        }

        private static void Complete(List<Waiter>? waiters)
        {
            if (waiters is null)
            {
                return;
            }

            foreach (var waiter in waiters)
            {
                waiter.Complete();
            }
        }

        private sealed class Waiter
        {
            private readonly TaskCompletionSource _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _state;

            public LinkedListNode<Waiter>? Node { get; set; }

            public CancellationTokenRegistration Registration { get; set; }

            public Task Task => _completion.Task;

            public bool TryGrant() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

            public bool TryCancel() => Interlocked.CompareExchange(ref _state, 2, 0) == 0;

            public void Complete()
            {
                Registration.Dispose();
                _completion.TrySetResult();
            }

            public void CancelTask()
            {
                Registration.Dispose();
                _completion.TrySetCanceled();
            }
        }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly DeviceOperationCoordinator _owner;
        private readonly string _serial;
        private readonly SemaphoreSlim _deviceLock;
        private readonly DeviceOperationInfo _info;
        private int _released;

        public Lease(
            DeviceOperationCoordinator owner,
            string serial,
            SemaphoreSlim deviceLock,
            DeviceOperationInfo info)
        {
            _owner = owner;
            _serial = serial;
            _deviceLock = deviceLock;
            _info = info;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release(_serial, _deviceLock, _info);
            }

            return ValueTask.CompletedTask;
        }
    }
}

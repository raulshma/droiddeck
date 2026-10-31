using DroidDeck.Adb.Tracking;
using DroidDeck.Core.Devices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Devices;

/// <summary>
/// Merges transport state, cached metadata, and persisted device records into one
/// device snapshot.
/// </summary>
public sealed class DeviceService : IDeviceService
{
    private static readonly TimeSpan MetadataFreshness = TimeSpan.FromMinutes(5);

    private readonly IAdbDeviceTracker _tracker;
    private readonly IDeviceRecordStore _recordStore;
    private readonly IDeviceMetadataSource? _metadataSource;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeviceService> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<string, DeviceInfo> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceRecord> _records = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refreshing = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a device service.</summary>
    public DeviceService(
        IAdbDeviceTracker tracker,
        IDeviceRecordStore recordStore,
        IDeviceMetadataSource? metadataSource = null,
        TimeProvider? timeProvider = null,
        ILogger<DeviceService>? logger = null)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));
        _metadataSource = metadataSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<DeviceService>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<DeviceInfo> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices.Values
                    .OrderBy(static device => device.Serial, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public bool IsAdbConnected => _tracker.IsConnected;

    /// <inheritdoc />
    public bool IsUsingCliFallback => _tracker.IsUsingProcessFallback;

    /// <inheritdoc />
    public string? LastError => _tracker.LastError;

    /// <inheritdoc />
    public event EventHandler<DeviceListChangedEventArgs>? DevicesChanged;

    /// <inheritdoc />
    public event EventHandler<DeviceInfo>? DeviceUpdated;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _cts = new CancellationTokenSource();
            try
            {
                var records = await _recordStore.ListAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _records.Clear();
                    foreach (var record in records)
                    {
                        _records[record.Serial] = record;
                    }
                }

                _tracker.DevicesChanged += OnTrackerDevicesChanged;
                _tracker.ConnectionChanged += OnTrackerConnectionChanged;
                await _tracker.StartAsync(cancellationToken).ConfigureAwait(false);
                ApplyTransport(_tracker.Devices);
            }
            catch
            {
                _tracker.DevicesChanged -= OnTrackerDevicesChanged;
                _tracker.ConnectionChanged -= OnTrackerConnectionChanged;
                lock (_gate)
                {
                    _started = false;
                    _cts?.Dispose();
                    _cts = null;
                }

                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource? cts;
            lock (_gate)
            {
                if (!_started)
                {
                    return;
                }

                _started = false;
                cts = _cts;
                _cts = null;
            }

            _tracker.DevicesChanged -= OnTrackerDevicesChanged;
            _tracker.ConnectionChanged -= OnTrackerConnectionChanged;
            cts?.Cancel();
            try
            {
                await _tracker.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                cts?.Dispose();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _tracker.RefreshAsync(cancellationToken).ConfigureAwait(false);
        ApplyTransport(_tracker.Devices);
    }

    /// <inheritdoc />
    public async Task<DeviceMetadata> RefreshMetadataAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        if (_metadataSource is null)
        {
            return new DeviceMetadata { FetchedAt = _timeProvider.GetUtcNow() };
        }

        var metadata = await _metadataSource.FetchAsync(serial, cancellationToken).ConfigureAwait(false);
        ApplyMetadata(serial, metadata);
        return metadata;
    }

    /// <inheritdoc />
    public Task<DeviceInfo?> GetDeviceAsync(string serial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(
                _devices.TryGetValue(serial, out var device) ? device : null);
        }
    }

    /// <inheritdoc />
    public async Task SetNotesAsync(
        string serial,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var record = BuildRecord(serial, existing => new DeviceRecord
        {
            Serial = serial,
            Notes = notes,
            Tags = existing?.Tags ?? [],
        });
        await _recordStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        ApplyRecord(record);
    }

    /// <inheritdoc />
    public async Task SetTagsAsync(
        string serial,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentNullException.ThrowIfNull(tags);

        var normalized = NormalizeTags(tags);
        var record = BuildRecord(serial, existing => new DeviceRecord
        {
            Serial = serial,
            Notes = existing?.Notes,
            Tags = normalized,
        });
        await _recordStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        ApplyRecord(record);
    }

    /// <inheritdoc />
    public async Task<bool> RestartAdbAsync(CancellationToken cancellationToken = default)
    {
        var restarted = await _tracker.RestartServerAsync(cancellationToken).ConfigureAwait(false);
        if (restarted)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }

        return restarted;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        await _tracker.DisposeAsync().ConfigureAwait(false);
    }

    private void OnTrackerDevicesChanged(object? sender, AdbDevicesChangedEventArgs args) =>
        ApplyTransport(args.Devices);

    private void OnTrackerConnectionChanged(object? sender, AdbConnectionChangedEventArgs args) =>
        RaiseDevicesChanged(Devices);

    private void ApplyTransport(IReadOnlyList<DeviceInfo> transportDevices)
    {
        DeviceInfo[] snapshot;
        var toRefresh = new List<string>();

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var merged = new List<DeviceInfo>(transportDevices.Count);

            foreach (var transport in transportDevices)
            {
                seen.Add(transport.Serial);
                _devices.TryGetValue(transport.Serial, out var existing);
                var info = (existing ?? new DeviceInfo { Serial = transport.Serial })
                    .WithTransport(transport, now);
                if (_records.TryGetValue(transport.Serial, out var record))
                {
                    info = info.WithRecord(record);
                }

                _devices[transport.Serial] = info;
                merged.Add(info);

                if (_metadataSource is not null &&
                    info.State == DeviceState.Device &&
                    (existing is null || existing.State != DeviceState.Device || IsMetadataStale(info, now)))
                {
                    toRefresh.Add(transport.Serial);
                }
            }

            foreach (var serial in _devices.Keys.Where(serial => !seen.Contains(serial)).ToArray())
            {
                _devices.Remove(serial);
            }

            snapshot = merged.ToArray();
        }

        RaiseDevicesChanged(snapshot);
        foreach (var serial in toRefresh)
        {
            StartMetadataRefresh(serial);
        }
    }

    private static bool IsMetadataStale(DeviceInfo info, DateTimeOffset now) =>
        info.MetadataUpdatedAt is null || now - info.MetadataUpdatedAt.Value > MetadataFreshness;

    private void StartMetadataRefresh(string serial)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_metadataSource is null || _cts is null || !_refreshing.Add(serial))
            {
                return;
            }

            token = _cts.Token;
        }

        _ = Task.Run(() => RefreshMetadataCoreAsync(serial, token), CancellationToken.None);
    }

    private async Task RefreshMetadataCoreAsync(string serial, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _metadataSource!.FetchAsync(serial, cancellationToken).ConfigureAwait(false);
            ApplyMetadata(serial, metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(exception, "Metadata for {Serial} could not be refreshed.", serial);
            }
        }
        finally
        {
            lock (_gate)
            {
                _refreshing.Remove(serial);
            }
        }
    }

    private void ApplyMetadata(string serial, DeviceMetadata metadata)
    {
        DeviceInfo? updated = null;
        lock (_gate)
        {
            if (!_devices.TryGetValue(serial, out var info))
            {
                return;
            }

            var appliedAt = metadata.FetchedAt == default ? _timeProvider.GetUtcNow() : metadata.FetchedAt;
            updated = info.WithMetadata(metadata, appliedAt);
            _devices[serial] = updated;
        }

        RaiseDeviceUpdated(updated);
    }

    private DeviceRecord BuildRecord(string serial, Func<DeviceRecord?, DeviceRecord> update)
    {
        lock (_gate)
        {
            _records.TryGetValue(serial, out var existing);
            return update(existing);
        }
    }

    private void ApplyRecord(DeviceRecord record)
    {
        DeviceInfo? updated = null;
        lock (_gate)
        {
            _records[record.Serial] = record;
            if (_devices.TryGetValue(record.Serial, out var info))
            {
                updated = info.WithRecord(record);
                _devices[record.Serial] = updated;
            }
        }

        if (updated is not null)
        {
            RaiseDeviceUpdated(updated);
        }
    }

    private static List<string> NormalizeTags(IReadOnlyList<string> tags)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            var trimmed = tag.Trim();
            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }

    private void RaiseDevicesChanged(IReadOnlyList<DeviceInfo> devices)
    {
        try
        {
            DevicesChanged?.Invoke(this, new DeviceListChangedEventArgs(devices));
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(exception, "A device-list handler failed.");
            }
        }
    }

    private void RaiseDeviceUpdated(DeviceInfo device)
    {
        try
        {
            DeviceUpdated?.Invoke(this, device);
        }
        catch (Exception exception)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(exception, "A device-update handler failed.");
            }
        }
    }
}

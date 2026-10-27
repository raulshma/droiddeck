using System.Text.Json;
using DroidDeck.Core.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Devices;

/// <summary>Persists user-editable device records.</summary>
public interface IDeviceRecordStore
{
    /// <summary>Reads a device record, returning <see langword="null"/> when none exists.</summary>
    Task<DeviceRecord?> GetAsync(string serial, CancellationToken cancellationToken = default);

    /// <summary>Writes a device record.</summary>
    Task SaveAsync(DeviceRecord record, CancellationToken cancellationToken = default);

    /// <summary>Lists all stored device records.</summary>
    Task<IReadOnlyList<DeviceRecord>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>Stores device records as one JSON file per serial.</summary>
public sealed class JsonDeviceRecordStore : IDeviceRecordStore
{
    private readonly AppPaths _paths;
    private readonly ILogger<JsonDeviceRecordStore> _logger;

    /// <summary>Creates a store rooted at the supplied data paths.</summary>
    public JsonDeviceRecordStore(AppPaths paths, ILogger<JsonDeviceRecordStore>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<JsonDeviceRecordStore>.Instance;
    }

    /// <inheritdoc />
    public async Task<DeviceRecord?> GetAsync(string serial, CancellationToken cancellationToken = default)
    {
        var path = GetPath(serial);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<DeviceRecord>(json, DroidDeckJson.Options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "The device record for {Serial} could not be read.", serial);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(DeviceRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        _paths.EnsureCreated();
        var json = JsonSerializer.Serialize(record, DroidDeckJson.Options);
        await AtomicFile.WriteAllTextAsync(GetPath(record.Serial), json, keepBackup: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeviceRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_paths.DevicesDirectory))
        {
            return [];
        }

        var records = new List<DeviceRecord>();
        foreach (var file in Directory.EnumerateFiles(_paths.DevicesDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var record = JsonSerializer.Deserialize<DeviceRecord>(json, DroidDeckJson.Options);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(exception, "The device record {File} could not be read.", file);
            }
        }

        return records;
    }

    private string GetPath(string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        var safe = new string(serial.Select(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return Path.Combine(_paths.DevicesDirectory, safe + ".json");
    }
}

using System.Text.Json;
using DroidDeck.Core;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Wireless;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Wireless;

/// <summary>Stores remembered wireless devices as one JSON file per address.</summary>
public sealed class JsonWirelessDeviceStore : IWirelessDeviceStore
{
    private readonly AppPaths _paths;
    private readonly ILogger<JsonWirelessDeviceStore> _logger;

    /// <summary>Creates a store rooted at the supplied data paths.</summary>
    public JsonWirelessDeviceStore(AppPaths paths, ILogger<JsonWirelessDeviceStore>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<JsonWirelessDeviceStore>.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WirelessDevice>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_paths.WirelessDirectory))
        {
            return [];
        }

        var devices = new List<WirelessDevice>();
        foreach (var file in Directory.EnumerateFiles(_paths.WirelessDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var device = JsonSerializer.Deserialize<WirelessDevice>(json, DroidDeckJson.Options);
                if (device is not null && !string.IsNullOrWhiteSpace(device.Address))
                {
                    devices.Add(device);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(exception, "The wireless device record {File} could not be read.", file);
            }
        }

        return devices
            .OrderBy(static device => device.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task SaveAsync(WirelessDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(device.Address);
        _paths.EnsureCreated();
        var json = JsonSerializer.Serialize(device, DroidDeckJson.Options);
        await AtomicFile.WriteAllTextAsync(GetPath(device.Address), json, keepBackup: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var path = GetPath(address);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The wireless device record {Address} could not be removed.", address);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private string GetPath(string address)
    {
        var safe = new string(address
            .Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_')
            .ToArray());
        return Path.Combine(_paths.WirelessDirectory, safe + ".json");
    }
}

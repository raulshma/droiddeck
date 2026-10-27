namespace DroidDeck.Core.Devices;

/// <summary>Fetches live metadata from a connected device.</summary>
public interface IDeviceMetadataSource
{
    /// <summary>Fetches metadata for a serial.</summary>
    Task<DeviceMetadata> FetchAsync(string serial, CancellationToken cancellationToken = default);
}

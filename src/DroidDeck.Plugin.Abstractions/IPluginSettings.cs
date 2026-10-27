namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// A plugin-private settings store. Schema-driven settings arrive with the plugin host milestone.
/// </summary>
public interface IPluginSettings
{
    /// <summary>Reads a raw setting value.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Writes a raw setting value.</summary>
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);

    /// <summary>Removes a setting value.</summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

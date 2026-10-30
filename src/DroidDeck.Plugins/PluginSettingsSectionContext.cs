using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>
/// The host implementation of <see cref="ISettingsSectionContext"/>. The
/// section reads and writes the plugin's settings through the same store the
/// plugin host handed the plugin, so schema plugins get the host validator on
/// every write: an invalid value is refused and nothing is persisted.
/// </summary>
public sealed class PluginSettingsSectionContext : ISettingsSectionContext
{
    private readonly IPluginSettings _settings;

    /// <summary>Creates a context over a plugin's settings store.</summary>
    public PluginSettingsSectionContext(IPluginSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <inheritdoc />
    public IPluginSettings Settings => _settings;

    /// <summary>
    /// Validates the currently stored values through the host validator. Only
    /// schema-driven stores can validate; stores without a schema have no rules
    /// to check and validate as empty.
    /// </summary>
    public async Task<IReadOnlyList<SettingsSectionError>> ValidateAsync(
        CancellationToken cancellationToken = default)
    {
        if (_settings is not SchemaPluginSettings schema)
        {
            return [];
        }

        var errors = await schema.ValidateAsync(cancellationToken).ConfigureAwait(false);
        return errors
            .Select(error => new SettingsSectionError(error.Field, error.Message))
            .ToArray();
    }
}

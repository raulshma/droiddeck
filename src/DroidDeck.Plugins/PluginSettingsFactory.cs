using System.Collections.Concurrent;
using DroidDeck.Core.Security;
using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Plugins;

/// <summary>How a plugin's settings store was created.</summary>
public enum PluginSettingsStoreKind
{
    /// <summary>The plugin keeps its raw key/value settings store.</summary>
    Raw,

    /// <summary>The plugin uses a schema-driven store with defaults and validation.</summary>
    SchemaDriven,
}

/// <summary>
/// A problem that kept a plugin's settings schema from being used. The message
/// is the complete loader warning; the reason is the short text settings
/// dialogs show after their own prefix.
/// </summary>
/// <param name="Code">The stable warning code.</param>
/// <param name="Reason">The short reason for a settings dialog.</param>
/// <param name="Message">The complete warning message for the loader.</param>
public sealed record PluginSettingsCreationIssue(string Code, string Reason, string Message);

/// <summary>The settings store created for one plugin.</summary>
/// <param name="Settings">The store the plugin and host surfaces share.</param>
/// <param name="Kind">Whether the schema was applied.</param>
/// <param name="Schema">The schema the store validates against, when one was applied.</param>
/// <param name="Issue">The problem that prevented schema use, when one exists.</param>
public sealed record PluginSettingsCreation(
    IPluginSettings Settings,
    PluginSettingsStoreKind Kind,
    PluginSettingsSchema? Schema,
    PluginSettingsCreationIssue? Issue)
{
    /// <summary>Gets a value indicating whether the schema was applied.</summary>
    public bool UsesSchema => Kind == PluginSettingsStoreKind.SchemaDriven;

    /// <summary>Gets the schema-driven store, or <see langword="null"/> for a raw store.</summary>
    public SchemaPluginSettings? SchemaSettings => Settings as SchemaPluginSettings;
}

/// <summary>
/// Creates and owns the settings stores for the plugins of one host. Schema
/// stores are cached by their settings file so the loader, settings page, and
/// settings dialog all write through one instance; two factories (and therefore
/// two loaders) never share a store. Raw stores are returned without caching,
/// and their caller owns their lifetime.
/// </summary>
public sealed class PluginSettingsFactory : IDisposable
{
    private readonly ISecretStore? _secretStore;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ConcurrentDictionary<string, SchemaPluginSettings> _schemaStores =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Creates a settings factory.</summary>
    /// <param name="secretStore">The secret store that backs schema secret fields, when available.</param>
    /// <param name="loggerFactory">The factory for plugin-scoped loggers, when available.</param>
    public PluginSettingsFactory(ISecretStore? secretStore = null, ILoggerFactory? loggerFactory = null)
    {
        _secretStore = secretStore;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Creates the settings store for a loaded plugin. A plugin with a readable
    /// settings schema gets the schema-driven store; a plugin without one, or
    /// with a schema outside the supported subset, keeps the raw store and the
    /// schema problem is reported in the result.
    /// </summary>
    /// <param name="manifest">The plugin manifest.</param>
    /// <param name="pluginDirectory">The plugin installation directory.</param>
    /// <param name="cancellationToken">Cancels the schema read.</param>
    public async Task<PluginSettingsCreation> CreateAsync(
        PluginManifest manifest,
        string pluginDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return await CreateCoreAsync(manifest.Id, pluginDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the settings store for an installed plugin by reading its schema
    /// from disk. This is the entry point for host surfaces that do not hold the
    /// manifest, such as the settings page and settings dialog.
    /// </summary>
    /// <param name="pluginId">The plugin identifier.</param>
    /// <param name="pluginDirectory">The plugin installation directory.</param>
    /// <param name="cancellationToken">Cancels the schema read.</param>
    public async Task<PluginSettingsCreation> CreateForInstalledPluginAsync(
        string pluginId,
        string pluginDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return await CreateCoreAsync(pluginId, pluginDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SchemaPluginSettings[] stores;
        lock (_sync)
        {
            stores = [.. _schemaStores.Values];
            _schemaStores.Clear();
        }

        foreach (var store in stores)
        {
            store.Dispose();
        }
    }

    private async Task<PluginSettingsCreation> CreateCoreAsync(
        string pluginId,
        string pluginDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var settingsPath = Path.Combine(pluginDirectory, "settings.json");
        var logger = _loggerFactory?.CreateLogger($"DroidDeck.Plugins.{pluginId}");
        if (!File.Exists(PluginSettingsSchemaReader.GetSchemaPath(pluginDirectory)))
        {
            return CreateRaw(settingsPath, logger, issue: null);
        }

        var read = await PluginSettingsSchemaReader
            .ReadAsync(pluginDirectory, cancellationToken)
            .ConfigureAwait(false);
        var schema = read.Schema;
        if (schema is null)
        {
            var reason = read.Validation.Issues.FirstOrDefault(static issue =>
                issue.Severity == PluginValidationSeverity.Error)?.Message ?? "The schema is invalid.";
            return CreateRaw(
                settingsPath,
                logger,
                new PluginSettingsCreationIssue(
                    "plugin.settingsSchema.invalid",
                    reason,
                    $"The settings schema was not used: {reason} The plugin keeps its raw settings store."));
        }

        if (_secretStore is null)
        {
            return CreateRaw(
                settingsPath,
                logger,
                new PluginSettingsCreationIssue(
                    "plugin.settingsSchema.noSecretStore",
                    "The host secret store is not available.",
                    "The host secret store is not available, so the plugin's settings schema was " +
                        "not used. The plugin keeps its raw settings store."));
        }

        return CreateSchemaCore(pluginId, Path.GetFullPath(settingsPath), schema, logger);
    }

    private static PluginSettingsCreation CreateRaw(
        string settingsPath,
        ILogger? logger,
        PluginSettingsCreationIssue? issue)
        => new(
            new JsonPluginSettings(settingsPath, logger),
            PluginSettingsStoreKind.Raw,
            Schema: null,
            Issue: issue);

    private PluginSettingsCreation CreateSchemaCore(
        string pluginId,
        string settingsPath,
        PluginSettingsSchema schema,
        ILogger? logger)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_schemaStores.TryGetValue(settingsPath, out var existing) && !existing.IsDisposed)
            {
                return new PluginSettingsCreation(
                    existing,
                    PluginSettingsStoreKind.SchemaDriven,
                    existing.Schema,
                    Issue: null);
            }

            var settings = new SchemaPluginSettings(
                settingsPath,
                schema,
                pluginId,
                _secretStore!,
                logger);
            _schemaStores[settingsPath] = settings;
            return new PluginSettingsCreation(
                settings,
                PluginSettingsStoreKind.SchemaDriven,
                schema,
                Issue: null);
        }
    }
}

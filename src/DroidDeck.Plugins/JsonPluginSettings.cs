using System.Text.Json;
using System.Text.Json.Nodes;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>
/// A plugin-private settings store persisted as a JSON object in the plugin directory.
/// </summary>
public sealed class JsonPluginSettings : IPluginSettings, IDisposable
{
    private readonly string _filePath;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private JsonObject? _values;
    private bool _disposed;

    /// <summary>Creates a settings store rooted at a JSON file.</summary>
    public JsonPluginSettings(string filePath, ILogger? logger = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _logger = logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the settings file path.</summary>
    public string FilePath => _filePath;

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return values[key] switch
            {
                null => null,
                JsonValue value when value.TryGetValue<string>(out var text) => text,
                var node => node.ToJsonString(DroidDeckJson.Options),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (value is null)
            {
                values.Remove(key);
            }
            else
            {
                values[key] = value;
            }

            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var values = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (values.Remove(key))
            {
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
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
        _gate.Dispose();
    }

    private async Task<JsonObject> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_values is not null)
        {
            return _values;
        }

        if (!File.Exists(_filePath))
        {
            _values = new JsonObject();
            return _values;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            _values = JsonNode.Parse(json, documentOptions: DroidDeckJson.DocumentOptions) as JsonObject
                ?? new JsonObject();
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Plugin settings file '{Path}' is not valid JSON; starting with empty settings.", _filePath);
            QuarantineCorruptFile();
            _values = new JsonObject();
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "Plugin settings file '{Path}' could not be read; starting with empty settings.", _filePath);
            _values = new JsonObject();
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(exception, "Plugin settings file '{Path}' could not be read; starting with empty settings.", _filePath);
            _values = new JsonObject();
        }

        return _values;
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        var json = _values!.ToJsonString(DroidDeckJson.Options);
        await AtomicFile.WriteAllTextAsync(_filePath, json, keepBackup: true, cancellationToken).ConfigureAwait(false);
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath)!;
            var quarantinedPath = CorruptFileQuarantine.Quarantine(
                _filePath,
                Path.Combine(directory, "quarantine"),
                _timeProvider.GetUtcNow());
            _logger.LogWarning("Moved corrupt plugin settings to '{Path}'.", quarantinedPath);
        }
        catch (IOException)
        {
            // Keeping the corrupt file in place is preferable to losing it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same as above: leave the file alone.
        }
    }
}

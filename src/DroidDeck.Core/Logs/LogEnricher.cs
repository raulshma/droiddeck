using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Core.Logs;

/// <summary>Enriches log entries before they reach the buffer and the UI.</summary>
public interface ILogEnricher
{
    /// <summary>Applies parsers to one entry.</summary>
    /// <param name="entry">The entry to enrich in place.</param>
    /// <param name="deviceSerial">The device that produced the line.</param>
    void Enrich(LogEntry entry, string deviceSerial);
}

/// <summary>
/// Applies plugin log parsers in priority order. Faults are isolated per parser,
/// and the parser list is refreshed whenever the registry changes.
/// </summary>
public sealed class PluginLogEnricher : ILogEnricher, IDisposable
{
    private readonly IPluginRegistry _registry;
    private readonly ILogger<PluginLogEnricher>? _logger;
    private volatile ILogParser[] _parsers;
    private bool _disposed;

    /// <summary>Creates an enricher backed by the shared plugin registry.</summary>
    public PluginLogEnricher(IPluginRegistry registry, ILogger<PluginLogEnricher>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger;
        _parsers = LoadParsers();
        _registry.Changed += OnRegistryChanged;
    }

    /// <inheritdoc />
    public void Enrich(LogEntry entry, string deviceSerial)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var parsers = _parsers;
        if (parsers.Length == 0)
        {
            return;
        }

        foreach (var parser in parsers)
        {
            LogParserResult? result;
            try
            {
                result = parser.Parse(new LogParserContext(
                    deviceSerial ?? string.Empty,
                    entry.Raw,
                    entry.Tag,
                    entry.Level,
                    entry.Message));
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "The log parser {ParserId} failed.", parser.Id);
                continue;
            }

            if (result is null)
            {
                continue;
            }

            if (result.Tag is not null)
            {
                entry.Tag = result.Tag;
            }

            if (result.Level is not null)
            {
                entry.Level = result.Level;
            }

            if (result.Message is not null)
            {
                entry.Message = result.Message;
            }

            if (!string.IsNullOrEmpty(result.HighlightRuleId) && string.IsNullOrEmpty(entry.HighlightRuleId))
            {
                entry.HighlightRuleId = result.HighlightRuleId;
            }
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
        _registry.Changed -= OnRegistryChanged;
    }

    private void OnRegistryChanged(object? sender, EventArgs e) => _parsers = LoadParsers();

    private ILogParser[] LoadParsers() => _registry.LogParsers
        .Select(extension => extension.Value)
        .OrderBy(parser => parser.Priority)
        .ToArray();
}

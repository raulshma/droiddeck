using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidDeck.Plugins;

/// <summary>One recorded plugin lifecycle decision.</summary>
/// <param name="TimestampUtc">When the decision was recorded, in UTC.</param>
/// <param name="Action">One of install, update, rollback, uninstall, or trust.</param>
/// <param name="PluginId">The plugin the decision belongs to.</param>
/// <param name="FromVersion">The version before the action, when one existed.</param>
/// <param name="ToVersion">The version after the action, when one exists.</param>
/// <param name="Result">Either success or failure.</param>
/// <param name="Reason">A human-readable explanation for a failure.</param>
/// <param name="SourceId">The catalog source the action came from, when known.</param>
public sealed record PluginAuditEntry(
    DateTimeOffset TimestampUtc,
    string Action,
    string PluginId,
    string? FromVersion,
    string? ToVersion,
    string Result,
    string? Reason,
    string? SourceId);

/// <summary>
/// An append-only JSONL log of plugin lifecycle decisions (install, update,
/// rollback, uninstall, trust) stored under the log directory. Entries record
/// identifiers, versions, and outcomes only — never file contents or secrets.
/// </summary>
public sealed class PluginAuditLog : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates an audit log over a JSONL file.</summary>
    public PluginAuditLog(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    /// <summary>Gets the file the audit log appends to.</summary>
    public string FilePath { get; }

    /// <summary>Appends one decision as a single JSON line. The timestamp is normalized to UTC.</summary>
    public async Task AppendAsync(PluginAuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(
            AuditLine.From(new PluginAuditEntry(
                entry.TimestampUtc.ToUniversalTime(),
                entry.Action,
                entry.PluginId,
                entry.FromVersion,
                entry.ToVersion,
                entry.Result,
                entry.Reason,
                entry.SourceId)),
            Options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(FilePath, line + "\n", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads every readable entry in file order. A corrupt or partial line is
    /// skipped so one bad write cannot hide the rest of the history.
    /// </summary>
    public async Task<IReadOnlyList<PluginAuditEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        var entries = new List<PluginAuditEntry>();
        await using var stream = File.OpenRead(FilePath);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                var saved = JsonSerializer.Deserialize<AuditLine>(line, Options);
                if (saved is not null &&
                    DateTimeOffset.TryParse(
                        saved.Timestamp,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var timestamp))
                {
                    entries.Add(saved.ToEntry(timestamp));
                }
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                // A torn or hand-edited line is skipped; the log stays readable.
            }
        }

        return entries;
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

    internal sealed record AuditLine(
        string Timestamp,
        string Action,
        string PluginId,
        string? FromVersion,
        string? ToVersion,
        string Result,
        string? Reason,
        string? SourceId)
    {
        public static AuditLine From(PluginAuditEntry entry) => new(
            entry.TimestampUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            entry.Action,
            entry.PluginId,
            entry.FromVersion,
            entry.ToVersion,
            entry.Result,
            entry.Reason,
            entry.SourceId);

        public PluginAuditEntry ToEntry(DateTimeOffset timestampUtc) => new(
            timestampUtc,
            Action,
            PluginId,
            FromVersion,
            ToVersion,
            Result,
            Reason,
            SourceId);
    }
}

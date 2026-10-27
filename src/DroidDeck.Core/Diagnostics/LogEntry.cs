using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Core.Diagnostics;

/// <summary>One captured application log entry.</summary>
/// <param name="Timestamp">When the entry was written.</param>
/// <param name="Level">The severity.</param>
/// <param name="Category">The logger category.</param>
/// <param name="Message">The formatted message.</param>
/// <param name="ExceptionText">The exception text, when one was supplied.</param>
/// <param name="SessionId">The launch that produced the entry.</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? ExceptionText = null,
    string? SessionId = null)
{
    /// <summary>Formats the entry as one report line.</summary>
    public string Format()
    {
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} [{Level}] {Category}: {Message}");
        if (!string.IsNullOrWhiteSpace(SessionId))
        {
            summary = string.Create(
                CultureInfo.InvariantCulture,
                $"{Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} [{Level}] ({SessionId}) {Category}: {Message}");
        }

        return ExceptionText is null ? summary : summary + Environment.NewLine + ExceptionText;
    }
}

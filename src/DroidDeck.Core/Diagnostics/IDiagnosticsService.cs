namespace DroidDeck.Core.Diagnostics;

/// <summary>
/// Builds and saves local diagnostics reports. The service never sends data
/// anywhere; reports are copied or saved only when the user asks.
/// </summary>
public interface IDiagnosticsService
{
    /// <summary>Gets the short identifier users quote in support requests.</summary>
    string DiagnosticsId { get; }

    /// <summary>Gets the identifier of the current launch.</summary>
    string SessionId { get; }

    /// <summary>Builds a redacted plain-text report.</summary>
    string BuildReport(DiagnosticsReportOptions? options = null);

    /// <summary>Builds and writes a report under the diagnostics folder.</summary>
    Task<string> SaveReportAsync(
        DiagnosticsReportOptions? options = null,
        CancellationToken cancellationToken = default);
}

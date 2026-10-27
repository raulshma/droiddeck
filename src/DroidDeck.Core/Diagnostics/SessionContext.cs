namespace DroidDeck.Core.Diagnostics;

/// <summary>
/// Identifies one application launch. Users quote the diagnostics identifier in
/// bug reports so logs and failure states can be correlated.
/// </summary>
public sealed class SessionContext
{
    /// <summary>Creates a session context with a fresh identifier.</summary>
    public SessionContext()
        : this(null)
    {
    }

    /// <summary>Creates a session context with an explicit identifier.</summary>
    public SessionContext(string? sessionId)
    {
        SessionId = string.IsNullOrWhiteSpace(sessionId)
            ? Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()
            : sessionId.Trim();
        DiagnosticsId = "DD-" + (SessionId.Length > 8 ? SessionId[..8] : SessionId);
    }

    /// <summary>Gets the session identifier.</summary>
    public string SessionId { get; }

    /// <summary>Gets the short identifier users quote in support requests.</summary>
    public string DiagnosticsId { get; }
}

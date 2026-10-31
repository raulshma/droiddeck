namespace DroidDeck.Core.Diagnostics;

/// <summary>
/// Formats the user-facing notice shown when an action fails.
/// </summary>
public static class FailureNotice
{
    /// <summary>
    /// Formats a failure notice for an action and its exception.
    /// </summary>
    /// <param name="action">A short description of the action that failed, such as "Starting the run".</param>
    /// <param name="exception">The exception that caused the failure.</param>
    /// <param name="diagnosticsId">
    /// The diagnostics identifier to quote in support requests. When null or blank, the
    /// identifier is omitted.
    /// </param>
    /// <returns>
    /// <c>"{action} failed: {exception.Message} (Diagnostics ID {diagnosticsId})."</c> when
    /// <paramref name="diagnosticsId"/> is non-blank; otherwise
    /// <c>"{action} failed: {exception.Message}."</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="action"/> or <paramref name="exception"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="action"/> is empty or whitespace.</exception>
    public static string Format(string action, Exception exception, string? diagnosticsId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(exception);

        return string.IsNullOrWhiteSpace(diagnosticsId)
            ? $"{action} failed: {exception.Message}."
            : $"{action} failed: {exception.Message} (Diagnostics ID {diagnosticsId}).";
    }
}

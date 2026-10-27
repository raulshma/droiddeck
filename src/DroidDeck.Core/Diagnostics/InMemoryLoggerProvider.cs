using Microsoft.Extensions.Logging;

namespace DroidDeck.Core.Diagnostics;

/// <summary>
/// Captures log entries into a <see cref="BoundedLogBuffer"/> for the activity
/// view and diagnostics reports.
/// </summary>
[ProviderAlias("Memory")]
public sealed class InMemoryLoggerProvider : ILoggerProvider
{
    private readonly BoundedLogBuffer _buffer;
    private readonly string? _sessionId;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, LogLevel, bool>? _filter;

    /// <summary>Creates a provider that writes into the supplied buffer.</summary>
    public InMemoryLoggerProvider(
        BoundedLogBuffer buffer,
        string? sessionId = null,
        TimeProvider? timeProvider = null,
        Func<string, LogLevel, bool>? filter = null)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _sessionId = sessionId;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _filter = filter;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class BufferLogger(InMemoryLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None &&
            (provider._filter is null || provider._filter(category, logLevel));

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            provider._buffer.Add(new LogEntry(
                provider._timeProvider.GetUtcNow(),
                logLevel,
                category,
                message,
                exception?.ToString(),
                provider._sessionId));
        }
    }
}

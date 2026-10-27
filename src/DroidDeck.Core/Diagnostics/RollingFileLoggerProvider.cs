using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DroidDeck.Core.Diagnostics;

/// <summary>
/// Writes redacted log lines to daily rolling files and removes files older
/// than the retention window. File failures never interrupt the application.
/// </summary>
[ProviderAlias("File")]
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _prefix;
    private readonly int _retentionDays;
    private readonly string? _sessionId;
    private readonly TimeProvider _timeProvider;
    private string? _currentFile;
    private bool _disposed;

    /// <summary>Creates a provider rooted at a log directory.</summary>
    public RollingFileLoggerProvider(
        string directory,
        string filePrefix = "app",
        int retentionDays = 14,
        string? sessionId = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePrefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retentionDays);
        _directory = Path.GetFullPath(directory);
        _prefix = filePrefix;
        _retentionDays = retentionDays;
        _sessionId = sessionId;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_directory);
        DeleteExpiredFiles(_timeProvider.GetUtcNow());
    }

    /// <summary>Gets the current log file path, if one has been opened.</summary>
    public string? CurrentFile
    {
        get
        {
            lock (_sync)
            {
                return _currentFile;
            }
        }
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }
    }

    private void Write(LogEntry entry)
    {
        var now = _timeProvider.GetUtcNow();
        var line = Redactor.Redact(entry.Format());
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var fileName = $"{_prefix}-{now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log";
                var path = Path.Combine(_directory, fileName);
                if (!string.Equals(_currentFile, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFile = path;
                    DeleteExpiredFiles(now);
                }

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(line);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the application down.
            }
        }
    }

    private void DeleteExpiredFiles(DateTimeOffset now)
    {
        try
        {
            var cutoff = now.UtcDateTime.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, _prefix + "-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Retention cleanup is best effort.
        }
    }

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None)
            {
                return;
            }

            provider.Write(new LogEntry(
                provider._timeProvider.GetUtcNow(),
                logLevel,
                category,
                formatter(state, exception),
                exception?.ToString(),
                provider._sessionId));
        }
    }
}

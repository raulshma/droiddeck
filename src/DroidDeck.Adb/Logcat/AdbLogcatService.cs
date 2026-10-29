using DroidDeck.Core.Logs;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Adb.Logcat;

/// <summary>Starts, tracks, and stops per-device logcat streams.</summary>
public sealed class AdbLogcatService : ILogcatService
{
    private readonly IProcessRunner _runner;
    private readonly IDeviceCommandService _commands;
    private readonly Func<string> _adbPathProvider;
    private readonly ILogEnricher? _enricher;
    private readonly ILogger<AdbLogcatService> _logger;
    private readonly Dictionary<string, AdbLogcatSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private bool _disposed;

    /// <summary>Creates a logcat service.</summary>
    public AdbLogcatService(
        IProcessRunner runner,
        IDeviceCommandService commands,
        Func<string> adbPathProvider,
        ILogger<AdbLogcatService>? logger = null,
        ILogEnricher? enricher = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
        _logger = logger ?? NullLogger<AdbLogcatService>.Instance;
        _enricher = enricher;
    }

    /// <inheritdoc />
    public IReadOnlyList<ILogcatSession> Sessions
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Values.Cast<ILogcatSession>().ToArray();
            }
        }
    }

    /// <inheritdoc />
    public async Task<ILogcatSession> StartAsync(
        LogcatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DeviceSerial);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.ClearBeforeAttach)
        {
            await _commands.ClearLogcatAsync(request.DeviceSerial, cancellationToken).ConfigureAwait(false);
        }

        var session = new AdbLogcatSession(_runner, _adbPathProvider, request, _logger, _enricher);
        lock (_sync)
        {
            _sessions[session.Id] = session;
        }

        session.Start();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Logcat stream {SessionId} started for {Serial} (package {Package}).",
                session.Id,
                request.DeviceSerial,
                request.PackageName);
        }

        return session;
    }

    /// <inheritdoc />
    public async Task StopAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        AdbLogcatSession? session;
        lock (_sync)
        {
            _sessions.Remove(sessionId, out session);
        }

        if (session is null)
        {
            return;
        }

        await session.StopAsync(cancellationToken).ConfigureAwait(false);
        session.Dispose();
    }

    /// <inheritdoc />
    public ILogcatSession? GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        lock (_sync)
        {
            return _sessions.GetValueOrDefault(sessionId);
        }
    }

    /// <inheritdoc />
    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        AdbLogcatSession[] sessions;
        lock (_sync)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.StopAsync(cancellationToken).ConfigureAwait(false);
            session.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAllAsync().ConfigureAwait(false);
    }
}

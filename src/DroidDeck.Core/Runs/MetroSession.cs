using System.Diagnostics;
using System.Net.Http;
using DroidDeck.Core.Processes;

namespace DroidDeck.Core.Runs;

/// <summary>The state of the port Metro should use.</summary>
public enum MetroPortStatus
{
    /// <summary>The port state could not be determined.</summary>
    Unknown,

    /// <summary>A Metro development server answers on the port.</summary>
    Running,

    /// <summary>Nothing answers on the port.</summary>
    Free,

    /// <summary>Another application occupies the port.</summary>
    Conflicting,
}

/// <summary>
/// A Metro development server session for React Native runs. When a Metro instance already
/// answers on the port it is reused and never stopped; otherwise Metro is started as an owned
/// process that is terminated with the session.
/// </summary>
public sealed class MetroSession : IAsyncDisposable
{
    /// <summary>Probes a port for a Metro development server.</summary>
    public delegate Task<MetroPortStatus> PortProbe(int port, CancellationToken cancellationToken);

    /// <summary>Sends a reload request to the Metro development server.</summary>
    public delegate Task<(bool Succeeded, string? Error)> ReloadSender(int port, CancellationToken cancellationToken);

    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(5);

    private readonly ProcessRequest _request;
    private readonly Action<ProcessOutputLine>? _output;
    private readonly TimeSpan _startTimeout;
    private readonly PortProbe? _probe;
    private readonly ReloadSender? _reload;
    private readonly object _sync = new();

    private SupervisedProcess? _supervised;
    private bool _reused;
    private bool _stopped;
    private bool _disposed;

    /// <summary>Creates a Metro session around a prepared start request for the given port.</summary>
    public MetroSession(
        ProcessRequest request,
        int port,
        Action<ProcessOutputLine>? output = null,
        TimeSpan? startTimeout = null,
        PortProbe? probe = null,
        ReloadSender? reload = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _request = request;
        Port = port;
        _output = output;
        _startTimeout = startTimeout ?? TimeSpan.FromMinutes(5);
        _probe = probe;
        _reload = reload;
    }

    /// <summary>Gets the Metro port this session uses.</summary>
    public int Port { get; }

    /// <summary>Gets whether an already running Metro instance was reused instead of started.</summary>
    public bool ReusedExisting => _reused;

    /// <summary>Gets whether Metro is serving on the port.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return !_stopped && (_reused || _supervised is { HasExited: false });
            }
        }
    }

    /// <summary>Starts Metro or reuses an existing server, then waits until the port serves Metro.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_supervised is not null || _reused)
            {
                throw new InvalidOperationException("The Metro session was already started.");
            }
        }

        var status = await ProbePortAsync(cancellationToken).ConfigureAwait(false);
        if (status == MetroPortStatus.Running)
        {
            lock (_sync)
            {
                _reused = true;
            }

            return;
        }

        if (status == MetroPortStatus.Conflicting)
        {
            throw new InvalidOperationException(
                $"Port {Port} is already used by another application, so Metro cannot start. " +
                $"Stop the application using port {Port} or change the Metro port in metro.config.js, then run the app again.");
        }

        SupervisedProcess process;
        lock (_sync)
        {
            if (_stopped || _disposed)
            {
                throw new InvalidOperationException("The Metro session was stopped before it could start.");
            }

            process = SupervisedProcess.Start(_request, _output);
            _supervised = process;
        }

        var start = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "Metro exited before the development server was ready." + process.FormatStderrTail());
            }

            if (start.Elapsed > _startTimeout)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                throw new TimeoutException(
                    $"Metro did not report a running development server on port {Port} within {_startTimeout.TotalSeconds:F0} seconds.");
            }

            status = await ProbePortAsync(cancellationToken).ConfigureAwait(false);
            if (status == MetroPortStatus.Running)
            {
                return;
            }

            if (status == MetroPortStatus.Conflicting)
            {
                await StopProcessAsync(process).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Port {Port} is served by another application, not Metro. " +
                    $"Stop the application using port {Port} or change the Metro port in metro.config.js, then run the app again.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Asks Metro to reload the connected React Native applications.</summary>
    public async Task<HotReloadResult> ReloadAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var sender = _reload ?? SendReloadRequestAsync;
        try
        {
            var (succeeded, error) = await sender(Port, cancellationToken).ConfigureAwait(false);
            if (succeeded)
            {
                return new HotReloadResult(true, "Reload finished.", started.Elapsed);
            }

            var message = string.IsNullOrWhiteSpace(error)
                ? "Metro did not accept the reload request."
                : error.Trim();
            if (!message.Contains("developer menu", StringComparison.OrdinalIgnoreCase))
            {
                message += " As a fallback, shake the device or open the developer menu and choose Reload.";
            }

            return new HotReloadResult(false, message, started.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException)
        {
            return new HotReloadResult(
                false,
                $"The reload request failed: {exception.Message}. Shake the device and choose Reload from the developer menu as a fallback.",
                started.Elapsed);
        }
    }

    /// <summary>Stops the owned Metro process. A reused external server is left untouched.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        SupervisedProcess? process;
        lock (_sync)
        {
            process = _supervised;
            if (process is null)
            {
                return;
            }

            _stopped = true;
            _supervised = null;
        }

        await StopProcessAsync(process).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // Shutting down must not throw from a dispose path.
        }
    }

    private async Task<MetroPortStatus> ProbePortAsync(CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            return await _probe(Port, cancellationToken).ConfigureAwait(false);
        }

        return await ProbePortAsync(Port, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MetroPortStatus> ProbePortAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await HttpClient
                .GetAsync($"http://127.0.0.1:{port}/status", timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return MetroPortStatus.Conflicting;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return body.Contains("packager-status:running", StringComparison.Ordinal)
                ? MetroPortStatus.Running
                : MetroPortStatus.Conflicting;
        }
        catch (HttpRequestException)
        {
            // Nothing is listening (connection refused) or the endpoint rejected the probe.
            return MetroPortStatus.Free;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return MetroPortStatus.Unknown;
        }
    }

    private static async Task<(bool Succeeded, string? Error)> SendReloadRequestAsync(
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient
                .PostAsync($"http://127.0.0.1:{port}/reload", content: null, cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (false, $"Metro returned HTTP {(int)response.StatusCode} for the reload request." +
                (string.IsNullOrWhiteSpace(body) ? string.Empty : " " + body.Trim()));
        }
        catch (HttpRequestException exception)
        {
            return (false, $"Metro is no longer reachable on port {port}: {exception.Message}");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private async Task StopProcessAsync(SupervisedProcess process)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_supervised, process))
            {
                _supervised = null;
            }
        }

        try
        {
            await process.StopAsync(ExitGracePeriod, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Runs;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Runs;

public sealed class MetroSessionTests
{
    [Fact]
    public async Task StartAsync_ReusesAnExistingMetroServer()
    {
        using var server = new FakeMetroServer("packager-status:running");
        var session = CreateSession(server.Port, probe: ProbeRunning);

        await session.StartAsync();

        Assert.True(session.ReusedExisting);
        Assert.True(session.IsRunning);
        Assert.Equal(server.Port, session.Port);
    }

    [Fact]
    public async Task StartAsync_ExplainsAPortConflictWithFixes()
    {
        var session = CreateSession(port: 54321, probe: ProbeConflicting);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());

        Assert.Contains("54321", exception.Message, StringComparison.Ordinal);
        Assert.Contains("metro.config.js", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_WaitsForAnOwnedServerToReportRunning()
    {
        var statuses = new Queue<MetroPortStatus>([MetroPortStatus.Free, MetroPortStatus.Free, MetroPortStatus.Running]);
        var session = CreateSession(
            port: 54322,
            probe: (_, _) => Task.FromResult(statuses.Count > 0 ? statuses.Dequeue() : MetroPortStatus.Running),
            request: TestProcessHelper.Create("sleep", "60000"));

        await session.StartAsync();

        Assert.False(session.ReusedExisting);
        Assert.True(session.IsRunning);
    }

    [Fact]
    public async Task StartAsync_ThrowsWhenTheProcessExitsBeforeTheServerIsReady()
    {
        var session = CreateSession(
            port: 54323,
            probe: ProbeFree,
            request: TestProcessHelper.Create("exit", "1"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());

        Assert.Contains("exited before", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task StartAsync_TimesOutWhenTheServerNeverStarts()
    {
        var session = CreateSession(
            port: 54324,
            probe: ProbeFree,
            request: TestProcessHelper.Create("sleep", "60000"),
            startTimeout: TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAsync<TimeoutException>(() => session.StartAsync());

        await session.DisposeAsync();
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task StartAsync_RejectsASecondStart()
    {
        var session = CreateSession(54325, probe: ProbeRunning);

        await session.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
    }

    [Fact]
    public async Task ReloadAsync_ReportsSuccessWhenMetroAcceptsTheRequest()
    {
        var session = CreateSession(54326, probe: ProbeRunning, reload: (_, _) => Task.FromResult((true, (string?)null)));

        var result = await session.ReloadAsync();

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task ReloadAsync_ReportsTheServerErrorAndAFallback()
    {
        var session = CreateSession(
            54327,
            probe: ProbeRunning,
            reload: (_, _) => Task.FromResult((false, (string?)"Metro returned HTTP 500 for the reload request.")));

        var result = await session.ReloadAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("HTTP 500", result.Message, StringComparison.Ordinal);
        Assert.Contains("developer menu", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_LeavesAReusedServerAloneAndStopsAnOwnedProcess()
    {
        var reused = CreateSession(54328, probe: ProbeRunning);
        await reused.StartAsync();
        await reused.StopAsync();
        Assert.True(reused.IsRunning, "A reused external Metro must not be stopped by the session.");

        var ownedStatuses = new Queue<MetroPortStatus>([MetroPortStatus.Free]);
        var owned = CreateSession(
            54329,
            probe: (_, _) => Task.FromResult(
                ownedStatuses.Count > 0 ? ownedStatuses.Dequeue() : MetroPortStatus.Running),
            request: TestProcessHelper.Create("sleep", "60000"));
        await owned.StartAsync();
        await owned.StopAsync();
        Assert.False(owned.IsRunning);

        await owned.StopAsync();
        Assert.False(owned.IsRunning);
    }

    [Fact]
    public async Task Probe_DetectsTheRealMetroStatusEndpoint()
    {
        using var server = new FakeMetroServer("packager-status:running");
        var session = CreateSession(server.Port, probe: null);

        await session.StartAsync();

        Assert.True(session.ReusedExisting);
    }

    [Fact]
    public async Task Reload_SendsTheRealHttpPost()
    {
        using var server = new FakeMetroServer("packager-status:running", reloadBody: null, reloadStatus: 200);
        var session = CreateSession(server.Port, probe: null);
        await session.StartAsync();

        var result = await session.ReloadAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(1, server.ReloadRequests);
        Assert.Equal(0, server.ReloadRequestsFailed);
    }

    [Fact]
    public async Task Reload_ReportsAFailureFromTheRealEndpoint()
    {
        using var server = new FakeMetroServer("packager-status:running", reloadBody: "boom", reloadStatus: 500);
        var session = CreateSession(server.Port, probe: null);
        await session.StartAsync();

        var result = await session.ReloadAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("500", result.Message, StringComparison.Ordinal);
        Assert.Contains("boom", result.Message, StringComparison.Ordinal);
    }

    private static MetroSession CreateSession(
        int port,
        MetroSession.PortProbe? probe,
        ProcessRequest? request = null,
        MetroSession.ReloadSender? reload = null,
        TimeSpan? startTimeout = null) =>
        new(
            request ?? TestProcessHelper.Create("sleep", "60000"),
            port,
            probe: probe,
            reload: reload,
            startTimeout: startTimeout ?? TimeSpan.FromSeconds(10));

    private static Task<MetroPortStatus> ProbeRunning(int port, CancellationToken cancellationToken) =>
        Task.FromResult(MetroPortStatus.Running);

    private static Task<MetroPortStatus> ProbeFree(int port, CancellationToken cancellationToken) =>
        Task.FromResult(MetroPortStatus.Free);

    private static Task<MetroPortStatus> ProbeConflicting(int port, CancellationToken cancellationToken) =>
        Task.FromResult(MetroPortStatus.Conflicting);

    /// <summary>A minimal HTTP server that answers like the Metro development server.</summary>
    private sealed class FakeMetroServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _loop;

        public FakeMetroServer(string statusBody, string? reloadBody = null, int reloadStatus = 200)
        {
            StatusBody = statusBody;
            ReloadBody = reloadBody;
            ReloadStatus = reloadStatus;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => AcceptLoopAsync(_cancellation.Token));
        }

        public int Port { get; }

        public int ReloadRequests { get; private set; }

        public int ReloadRequestsFailed { get; private set; }

        private string StatusBody { get; }

        private string? ReloadBody { get; }

        private int ReloadStatus { get; }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client, cancellationToken), CancellationToken.None);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var requestEnd = false;
                var method = "GET";
                var read = 0;
                while (!requestEnd)
                {
                    var received = await stream
                        .ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                        .ConfigureAwait(false);
                    if (received == 0)
                    {
                        return;
                    }

                    read += received;
                    var text = Encoding.ASCII.GetString(buffer, 0, read);
                    var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        method = text.StartsWith("POST", StringComparison.Ordinal) ? "POST" : "GET";
                        var contentLength = ParseContentLength(text);
                        var bodyStart = headerEnd + 4;
                        if (read >= bodyStart + contentLength)
                        {
                            requestEnd = true;
                        }
                    }
                    else if (read >= buffer.Length)
                    {
                        requestEnd = true;
                    }
                }

                string statusLine;
                string body;
                if (method == "POST")
                {
                    ReloadRequests++;
                    if (ReloadStatus >= 200 && ReloadStatus < 300)
                    {
                        statusLine = "HTTP/1.1 200 OK";
                        body = ReloadBody ?? string.Empty;
                    }
                    else
                    {
                        ReloadRequestsFailed++;
                        statusLine = $"HTTP/1.1 {ReloadStatus} Internal Server Error";
                        body = ReloadBody ?? string.Empty;
                    }
                }
                else
                {
                    statusLine = "HTTP/1.1 200 OK";
                    body = StatusBody;
                }

                var payload = Encoding.UTF8.GetBytes(body);
                var response = Encoding.ASCII.GetBytes(
                    $"{statusLine}\r\nContent-Type: text/plain\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }
        }

        private static int ParseContentLength(string text)
        {
            foreach (var line in text.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    return int.TryParse(line["Content-Length:".Length..].Trim(), out var length) ? length : 0;
                }
            }

            return 0;
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (SocketException)
            {
            }

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
            }

            _cancellation.Dispose();
        }
    }
}

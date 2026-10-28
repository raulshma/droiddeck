using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Wireless;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QRCoder;

namespace DroidDeck.Adb.Wireless;

/// <summary>
/// Implements Android 11+ wireless debugging pairing, mDNS discovery, remembered
/// devices, and automatic reconnect.
/// </summary>
public sealed class WirelessAdbService : IWirelessAdbService
{
    private const string SameNetworkGuidance =
        "Keep this computer and the device on the same Wi-Fi network, turn off VPNs, and allow mDNS " +
        "(UDP 5353) and adb through the firewall.";

    private readonly IProcessRunner _runner;
    private readonly IWirelessDeviceStore _store;
    private readonly Func<string> _adbPathProvider;
    private readonly Func<WirelessReconnectOptions> _optionsProvider;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WirelessAdbService> _logger;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private List<WirelessMdnsService> _services = [];
    private List<WirelessDevice> _remembered = [];
    private HashSet<string> _connected = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task? _reconnectLoop;
    private string? _lastError;
    private bool _started;
    private bool _disposed;

    /// <summary>Creates a wireless adb service.</summary>
    public WirelessAdbService(
        IProcessRunner runner,
        IWirelessDeviceStore store,
        Func<string> adbPathProvider,
        Func<WirelessReconnectOptions>? optionsProvider = null,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null,
        ILogger<WirelessAdbService>? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _adbPathProvider = adbPathProvider ?? throw new ArgumentNullException(nameof(adbPathProvider));
        _optionsProvider = optionsProvider ?? (static () => new WirelessReconnectOptions());
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<WirelessAdbService>.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<WirelessMdnsService> DiscoveredServices
    {
        get
        {
            lock (_gate)
            {
                return _services.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WirelessDevice> RememberedDevices
    {
        get
        {
            lock (_gate)
            {
                return _remembered.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public bool IsReconnecting
    {
        get
        {
            lock (_gate)
            {
                return _reconnectLoop is { IsCompleted: false };
            }
        }
    }

    /// <inheritdoc />
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            _started = true;
            await ReloadRememberedAsync(cancellationToken).ConfigureAwait(false);
            _cts = new CancellationTokenSource();
            _reconnectLoop = ReconnectLoopAsync(_cts.Token);
            RaiseChanged();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _started = false;
            if (_cts is { } cts)
            {
                _cts = null;
                await cts.CancelAsync().ConfigureAwait(false);
                if (_reconnectLoop is { } loop)
                {
                    try
                    {
                        await loop.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                _reconnectLoop = null;
                cts.Dispose();
            }
        }
        finally
        {
            _lifecycle.Release();
            RaiseChanged();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WirelessMdnsService>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAdbAsync(["mdns", "services"], TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var message = result.CombinedOutput.Trim();
            SetLastError(
                string.IsNullOrWhiteSpace(message)
                    ? "mDNS discovery failed. Restart the adb server and verify ADB_MDNS=1."
                    : $"mDNS discovery failed: {message}");
            return [];
        }

        var services = ParseMdnsServices(result.StandardOutput);
        List<WirelessMdnsService> marked;
        lock (_gate)
        {
            marked = [.. services.Select(service =>
                service.Kind == WirelessServiceKind.Connect && _connected.Contains(service.Address)
                    ? service with { IsConnected = true }
                    : service)];
            _services = marked;
            _lastError = null;
        }

        RaiseChanged();
        return marked;
    }

    /// <inheritdoc />
    public WirelessPairingSession CreatePairingSession(TimeSpan? lifetime = null)
    {
        var serviceName = "droiddeck-" + RandomToken(10);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var payload = BuildQrPayload(serviceName, code);
        return new WirelessPairingSession
        {
            ServiceName = serviceName,
            PairingCode = code,
            QrPayload = payload,
            QrPng = CreateQrPng(payload),
            ExpiresAt = _timeProvider.GetUtcNow() + (lifetime ?? TimeSpan.FromMinutes(2)),
        };
    }

    /// <inheritdoc />
    public async Task<WirelessOperationResult> CompleteQrPairingAsync(
        WirelessPairingSession session,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var deadline = session.ExpiresAt < _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(5)
            ? _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(5)
            : session.ExpiresAt;
        progress?.Report("Waiting for the device to scan the QR code...");
        while (_timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var services = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var pairing = services.FirstOrDefault(service =>
                service.Kind == WirelessServiceKind.Pairing &&
                string.Equals(TrimLocal(service.Name), session.ServiceName, StringComparison.OrdinalIgnoreCase));
            if (pairing is not null)
            {
                progress?.Report($"Device found at {pairing.Address}. Pairing...");
                var result = await PairAsync(
                    pairing.Address,
                    session.PairingCode,
                    remember: true,
                    cancellationToken).ConfigureAwait(false);
                progress?.Report(result.Message);
                return result;
            }

            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return Failure(
            "The device did not advertise a pairing service before the QR session expired.",
            "Choose \"Pair device with QR code\" on the device, keep the scanner open, and scan the " +
            "code again. " + SameNetworkGuidance);
    }

    /// <inheritdoc />
    public async Task<WirelessOperationResult> PairAsync(
        string address,
        string pairingCode,
        bool remember = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingCode);
        if (pairingCode.Length is < 4 or > 12 || !pairingCode.All(char.IsAsciiDigit))
        {
            return Failure(
                "The pairing code must contain 4 to 12 digits.",
                "Read the six-digit pairing code from the device's Wireless debugging screen.");
        }

        var result = await RunAdbAsync(
            ["pair", address, pairingCode],
            TimeSpan.FromSeconds(90),
            cancellationToken).ConfigureAwait(false);
        var succeeded = result.ExitCode == 0 &&
                        !result.CombinedOutput.Contains("Failed", StringComparison.OrdinalIgnoreCase) &&
                        !result.CombinedOutput.Contains("error", StringComparison.OrdinalIgnoreCase);
        if (!succeeded)
        {
            return Failure(
                $"Pairing with {address} failed: {Summarize(result.CombinedOutput)}",
                GuidanceFor(result.CombinedOutput),
                result.CommandLine);
        }

        var connect = await FindConnectServiceAsync(address, cancellationToken).ConfigureAwait(false);
        if (connect is null)
        {
            return new WirelessOperationResult
            {
                Succeeded = true,
                Address = address,
                CommandLine = result.CommandLine,
                Message = $"Paired with {address}. The device has not advertised its connect service yet.",
                Guidance = "Wait a moment and press Connect, or run \"adb connect <ip>:<port>\" with " +
                           "the port shown under Wireless debugging.",
            };
        }

        var connection = await ConnectAsync(connect.Address, connect.Name, remember, cancellationToken)
            .ConfigureAwait(false);
        return connection with
        {
            Message = $"Paired and connected to {connect.Address}.",
            CommandLine = result.CommandLine + Environment.NewLine + connection.CommandLine,
        };
    }

    /// <inheritdoc />
    public async Task<WirelessOperationResult> ConnectAsync(
        string address,
        string? name = null,
        bool remember = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var result = await RunAdbAsync(
            ["connect", address],
            TimeSpan.FromSeconds(45),
            cancellationToken).ConfigureAwait(false);
        var output = result.CombinedOutput;
        var succeeded = result.ExitCode == 0 &&
                        !output.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
                        !output.Contains("cannot", StringComparison.OrdinalIgnoreCase) &&
                        !output.Contains("unable", StringComparison.OrdinalIgnoreCase) &&
                        !output.Contains("refused", StringComparison.OrdinalIgnoreCase);
        if (!succeeded)
        {
            return Failure(
                $"Could not connect to {address}: {Summarize(output)}",
                GuidanceFor(output),
                result.CommandLine);
        }

        MarkConnected(address);
        if (remember)
        {
            await RememberAsync(address, name, cancellationToken).ConfigureAwait(false);
        }

        return new WirelessOperationResult
        {
            Succeeded = true,
            Address = address,
            Message = $"Connected to {address}.",
            CommandLine = result.CommandLine,
        };
    }

    /// <inheritdoc />
    public async Task<WirelessOperationResult> DisconnectAsync(
        string address,
        bool forget = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var result = await RunAdbAsync(
            ["disconnect", address],
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (forget)
        {
            await _store.RemoveAsync(address, cancellationToken).ConfigureAwait(false);
            await ReloadRememberedAsync(cancellationToken).ConfigureAwait(false);
        }

        var succeeded = result.ExitCode == 0;
        if (succeeded)
        {
            MarkDisconnected(address);
        }

        return new WirelessOperationResult
        {
            Succeeded = succeeded,
            Address = address,
            CommandLine = result.CommandLine,
            Message = succeeded
                ? forget ? $"Disconnected and forgot {address}." : $"Disconnected from {address}."
                : $"Could not disconnect from {address}: {Summarize(result.CombinedOutput)}",
            Guidance = succeeded ? null : GuidanceFor(result.CombinedOutput),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WirelessOperationResult>> ReconnectAsync(
        CancellationToken cancellationToken = default)
    {
        await ReloadRememberedAsync(cancellationToken).ConfigureAwait(false);
        var remembered = RememberedDevices.Where(static device => device.AutoReconnect).ToArray();
        if (remembered.Length == 0)
        {
            return [];
        }

        var connected = await GetConnectedAddressesAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<WirelessOperationResult>();
        foreach (var device in remembered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connected.Contains(device.Address))
            {
                continue;
            }

            var effective = device;
            var result = await ConnectAsync(
                device.Address,
                device.Name,
                remember: false,
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                var rediscovered = await FindChangedConnectServiceAsync(device, cancellationToken)
                    .ConfigureAwait(false);
                if (rediscovered is not null)
                {
                    var retry = await ConnectAsync(
                        rediscovered.Address,
                        device.Name,
                        remember: false,
                        cancellationToken).ConfigureAwait(false);
                    if (retry.Succeeded)
                    {
                        await _store.RemoveAsync(device.Address, cancellationToken).ConfigureAwait(false);
                        effective = device with { Address = rediscovered.Address };
                        result = retry with
                        {
                            Message = $"Reconnected to {rediscovered.Address}; the device's wireless debugging port changed.",
                        };
                    }
                }
            }

            results.Add(result);
            if (result.Succeeded)
            {
                await _store.SaveAsync(
                    effective with { LastConnectedAt = _timeProvider.GetUtcNow() },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (results.Count > 0)
        {
            await ReloadRememberedAsync(cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    /// <summary>Builds the WPA3/ADB pairing payload consumed by the device's QR scanner.</summary>
    public static string BuildQrPayload(string serviceName, string pairingCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingCode);
        return $"WIFI:T:ADB;S:{EscapeQrField(serviceName)};P:{EscapeQrField(pairingCode)};;";
    }

    /// <summary>Renders a QR payload as a PNG image.</summary>
    public static byte[] CreateQrPng(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var code = new PngByteQRCode(data);
        return code.GetGraphic(8);
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var options = _optionsProvider();
            if (options.Enabled)
            {
                try
                {
                    var results = await ReconnectAsync(cancellationToken).ConfigureAwait(false);
                    var failed = results.FirstOrDefault(static result => !result.Succeeded);
                    if (failed is not null)
                    {
                        SetLastError(failed.Message + " " + failed.Guidance);
                    }
                    else if (results.Count > 0)
                    {
                        SetLastError(null);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "Wireless reconnect failed.");
                    SetLastError(exception.Message);
                }
            }

            try
            {
                await Task.Delay(
                    options.Enabled ? options.EffectiveDelay : TimeSpan.FromSeconds(1),
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReloadRememberedAsync(CancellationToken cancellationToken)
    {
        var devices = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _remembered = [.. devices];
        }

        RaiseChanged();
    }

    private async Task RememberAsync(string address, string? name, CancellationToken cancellationToken)
    {
        var existing = RememberedDevices.FirstOrDefault(device =>
            string.Equals(device.Address, address, StringComparison.OrdinalIgnoreCase));
        await _store.SaveAsync(
            new WirelessDevice
            {
                Address = address,
                Name = name ?? existing?.Name,
                LastConnectedAt = _timeProvider.GetUtcNow(),
                AutoReconnect = existing?.AutoReconnect ?? true,
            },
            cancellationToken).ConfigureAwait(false);
        await ReloadRememberedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WirelessMdnsService?> FindConnectServiceAsync(
        string pairingAddress,
        CancellationToken cancellationToken)
    {
        var host = SplitAddress(pairingAddress).Host;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var services = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
            var connect = services.FirstOrDefault(service =>
                service.Kind == WirelessServiceKind.Connect &&
                HasSameHost(service.Host, host));
            if (connect is not null)
            {
                return connect;
            }

            await Task.Delay(_pollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<HashSet<string>> GetConnectedAddressesAsync(CancellationToken cancellationToken)
    {
        var result = await RunAdbAsync(["devices"], TimeSpan.FromSeconds(20), cancellationToken)
            .ConfigureAwait(false);
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 &&
                parts[1] == "device" &&
                parts[0].Contains(':', StringComparison.Ordinal))
            {
                addresses.Add(parts[0]);
            }
        }

        lock (_gate)
        {
            _connected = addresses;
        }

        return addresses;
    }

    private async Task<WirelessMdnsService?> FindChangedConnectServiceAsync(
        WirelessDevice device,
        CancellationToken cancellationToken)
    {
        var host = SplitAddress(device.Address).Host;
        var services = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return services.FirstOrDefault(service =>
            service.Kind == WirelessServiceKind.Connect &&
            HasSameHost(service.Host, host) &&
            !string.Equals(service.Address, device.Address, StringComparison.OrdinalIgnoreCase));
    }

    private void MarkConnected(string address)
    {
        lock (_gate)
        {
            _connected.Add(address);
        }
    }

    private void MarkDisconnected(string address)
    {
        lock (_gate)
        {
            _connected.Remove(address);
        }
    }

    private Task<ProcessResult> RunAdbAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        return _runner.RunAsync(
            new ProcessRequest
            {
                FileName = _adbPathProvider(),
                Arguments = arguments,
                Timeout = timeout,
            },
            output: null,
            cancellationToken);
    }

    private static List<WirelessMdnsService> ParseMdnsServices(string output)
    {
        var services = new List<WirelessMdnsService>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("List of discovered", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var typeIndex = Array.FindIndex(tokens, static token =>
                token.Contains("_adb-tls-pairing._tcp", StringComparison.OrdinalIgnoreCase) ||
                token.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase));
            if (typeIndex < 0)
            {
                continue;
            }

            var typeToken = tokens[typeIndex];
            var kind = typeToken.Contains("_adb-tls-pairing", StringComparison.OrdinalIgnoreCase)
                ? WirelessServiceKind.Pairing
                : WirelessServiceKind.Connect;
            var addressToken = tokens.LastOrDefault(static token => token.Contains(':', StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(addressToken))
            {
                continue;
            }

            var (host, port) = SplitAddress(addressToken);
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            var name = string.Join(' ', tokens[..typeIndex]);
            if (string.IsNullOrWhiteSpace(name))
            {
                var typeName = typeToken.TrimEnd('.');
                var marker = typeName.IndexOf("._adb-tls-", StringComparison.OrdinalIgnoreCase);
                name = marker > 0 ? typeName[..marker] : typeName;
            }

            services.Add(new WirelessMdnsService
            {
                Name = TrimLocal(name),
                Kind = kind,
                Host = host,
                Port = port,
            });
        }

        return services
            .GroupBy(static service => $"{service.Name}|{service.Address}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray()
            .ToList();
    }

    private static (string Host, int Port) SplitAddress(string address)
    {
        var trimmed = address.Trim().TrimEnd('.');
        if (trimmed.Length == 0)
        {
            return (string.Empty, 0);
        }

        if (trimmed[0] == '[')
        {
            var close = trimmed.IndexOf(']');
            if (close > 1)
            {
                var host = trimmed[1..close];
                var portText = close + 1 < trimmed.Length && trimmed[close + 1] == ':'
                    ? trimmed[(close + 2)..]
                    : string.Empty;
                return (host, ParsePort(portText));
            }
        }

        var separator = trimmed.LastIndexOf(':');
        if (separator <= 0)
        {
            return (trimmed, 0);
        }

        return (trimmed[..separator], ParsePort(trimmed[(separator + 1)..]));
    }

    private static int ParsePort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
            ? port
            : 0;

    private static bool HasSameHost(string first, string second)
    {
        var left = SplitAddress(first).Host;
        var right = SplitAddress(second).Host;
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimLocal(string name)
    {
        var trimmed = name.Trim().TrimEnd('.');
        return trimmed.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^".local".Length]
            : trimmed;
    }

    private static string EscapeQrField(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string RandomToken(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var characters = new char[length];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(characters);
    }

    private static string Summarize(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "no output";
        }

        var singleLine = output.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return singleLine.Length <= 400 ? singleLine : singleLine[..400] + "...";
    }

    internal static string? GuidanceFor(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return SameNetworkGuidance;
        }

        var text = output.ToLowerInvariant();
        if (text.Contains("wrong password", StringComparison.Ordinal) ||
            text.Contains("failed to authenticate", StringComparison.Ordinal) ||
            text.Contains("pairing code", StringComparison.Ordinal))
        {
            return "The pairing code was rejected. Reopen \"Pair device with pairing code\" on the device " +
                   "and enter the new six-digit code before it expires.";
        }

        if (text.Contains("connection refused", StringComparison.Ordinal) ||
            text.Contains("protocol fault", StringComparison.Ordinal))
        {
            return "The pairing port is closed. Toggle Wireless debugging off and on, then start a fresh " +
                   "pairing session; pairing ports and codes expire quickly.";
        }

        if (text.Contains("no route to host", StringComparison.Ordinal) ||
            text.Contains("timed out", StringComparison.Ordinal) ||
            text.Contains("timeout", StringComparison.Ordinal) ||
            text.Contains("unreachable", StringComparison.Ordinal))
        {
            return "The device is unreachable. " + SameNetworkGuidance;
        }

        if (text.Contains("unauthorized", StringComparison.Ordinal))
        {
            return "Unlock the device and accept the wireless debugging fingerprint prompt.";
        }

        if (text.Contains("cannot resolve", StringComparison.Ordinal) ||
            text.Contains("unknown host", StringComparison.Ordinal) ||
            text.Contains("mdns", StringComparison.Ordinal))
        {
            return "mDNS discovery failed. Set ADB_MDNS=1, restart the adb server, and verify the network " +
                   "does not block multicast traffic.";
        }

        if (text.Contains("no such device", StringComparison.Ordinal))
        {
            return "The device is not paired. Pair it again with a current pairing code.";
        }

        return SameNetworkGuidance;
    }

    private static WirelessOperationResult Failure(string message, string? guidance, string? commandLine = null) =>
        new()
        {
            Succeeded = false,
            Message = message,
            Guidance = guidance,
            CommandLine = commandLine,
        };

    private void SetLastError(string? error)
    {
        lock (_gate)
        {
            _lastError = error;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}

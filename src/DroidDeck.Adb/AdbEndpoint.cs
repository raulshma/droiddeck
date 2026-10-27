using System.Globalization;

namespace DroidDeck.Adb;

/// <summary>
/// The address of the local adb server.
/// </summary>
public sealed record AdbEndpoint
{
    /// <summary>The default adb server port.</summary>
    public const int DefaultPort = 5037;

    /// <summary>The environment variable that overrides the server socket.</summary>
    public const string SocketVariable = "ADB_SERVER_SOCKET";

    /// <summary>The environment variable that overrides the server host.</summary>
    public const string HostVariable = "ANDROID_ADB_SERVER_ADDRESS";

    /// <summary>The environment variable that overrides the server port.</summary>
    public const string PortVariable = "ANDROID_ADB_SERVER_PORT";

    /// <summary>Creates an endpoint.</summary>
    public AdbEndpoint(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "The port must be between 1 and 65535.");
        }

        Host = host;
        Port = port;
    }

    /// <summary>Gets the server host.</summary>
    public string Host { get; }

    /// <summary>Gets the server port.</summary>
    public int Port { get; }

    /// <summary>Gets the default loopback endpoint.</summary>
    public static AdbEndpoint Default { get; } = new("127.0.0.1", DefaultPort);

    /// <summary>
    /// Resolves the endpoint from the same environment variables adb uses.
    /// </summary>
    public static AdbEndpoint Resolve(Func<string, string?>? getEnvironmentVariable = null)
    {
        var getVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        var socket = getVariable(SocketVariable);
        if (!string.IsNullOrWhiteSpace(socket) && TryParse(socket, out var socketEndpoint))
        {
            return socketEndpoint;
        }

        var host = getVariable(HostVariable);
        if (string.IsNullOrWhiteSpace(host))
        {
            host = Default.Host;
        }

        var port = Default.Port;
        var portText = getVariable(PortVariable);
        if (!string.IsNullOrWhiteSpace(portText) &&
            int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort) &&
            parsedPort is >= 1 and <= 65535)
        {
            port = parsedPort;
        }

        return new AdbEndpoint(host.Trim(), port);
    }

    /// <summary>Parses <c>host:port</c> or <c>tcp:host:port</c>.</summary>
    public static bool TryParse(string? value, out AdbEndpoint endpoint)
    {
        endpoint = Default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
        }

        var separator = text.LastIndexOf(':');
        if (separator <= 0 || separator == text.Length - 1)
        {
            return false;
        }

        var host = text[..separator].Trim();
        var portText = text[(separator + 1)..].Trim();
        if (host.Length == 0 ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            return false;
        }

        endpoint = new AdbEndpoint(host, port);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Host}:{Port}";
}

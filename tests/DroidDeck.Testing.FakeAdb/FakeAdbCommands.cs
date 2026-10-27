using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DroidDeck.Testing.FakeAdb;

internal static class FakeAdbCommands
{
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> RunAsync(FakeAdbOptions options)
    {
        var arguments = options.AdbArguments;
        if (arguments.Count == 0)
        {
            Console.Error.WriteLine("fake adb: no command supplied. Use --help for usage.");
            return 1;
        }

        var index = 0;
        string? serial = null;
        if (arguments[index] is "-s" or "--serial")
        {
            if (arguments.Count < index + 3)
            {
                Console.Error.WriteLine("fake adb: -s requires a serial and a command.");
                return 1;
            }

            serial = arguments[index + 1];
            index += 2;
        }

        var command = arguments[index];
        var rest = arguments.Skip(index + 1).ToArray();
        switch (command)
        {
            case "version":
                PrintVersion();
                return 0;
            case "start-server":
                return await StartServerAsync(options).ConfigureAwait(false);
            case "kill-server":
                return await KillServerAsync(options).ConfigureAwait(false);
            case "devices":
                PrintDevices(options);
                return 0;
            case "wait-for-device":
                return 0;
            case "shell":
                return RunShell(options, serial, rest);
            case "install":
                return RunInstall(options);
            case "uninstall":
                Console.WriteLine("Success");
                return 0;
            case "logcat":
                PrintLogcat(rest);
                return 0;
            case "get-state":
                return PrintState(options);
            default:
                Console.Error.WriteLine($"fake adb: unknown command '{command}'.");
                return 1;
        }
    }

    private static void PrintVersion()
    {
        Console.WriteLine("Android Debug Bridge version 1.0.41");
        Console.WriteLine("Version 35.0.2-12147458");
    }

    private static void PrintDevices(FakeAdbOptions options)
    {
        Console.WriteLine("List of devices attached");
        foreach (var line in FakeAdbState.ReadDeviceLines(options))
        {
            Console.WriteLine(line);
        }

        Console.WriteLine();
    }

    private static int RunShell(FakeAdbOptions options, string? serial, string[] arguments)
    {
        if (serial is null)
        {
            Console.Error.WriteLine("fake adb: shell requires -s <serial>.");
            return 1;
        }

        if (options.Scenario is "metadata-failure" or "offline")
        {
            Console.Error.WriteLine("error: device offline");
            return 1;
        }

        var script = string.Join(' ', arguments);
        if (script.Contains("getprop", StringComparison.Ordinal))
        {
            var property = TryReadSingleProperty(script);
            if (property is not null)
            {
                var value = FakeAdbScenario.PropertyValue(property);
                if (value is not null)
                {
                    Console.WriteLine(value);
                }

                return 0;
            }

            Console.Out.Write(FakeAdbScenario.MetadataOutput());
            return 0;
        }

        if (script.StartsWith("get-state", StringComparison.Ordinal))
        {
            Console.WriteLine("device");
            return 0;
        }

        if (script.StartsWith("echo ", StringComparison.Ordinal))
        {
            Console.WriteLine(script[5..]);
            return 0;
        }

        return 0;
    }

    private static string? TryReadSingleProperty(string script)
    {
        var trimmed = script.Trim();
        if (!trimmed.StartsWith("getprop", StringComparison.Ordinal) || trimmed.Contains(';'))
        {
            return null;
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 2 ? tokens[1] : null;
    }

    private static int RunInstall(FakeAdbOptions options)
    {
        if (options.Scenario == "install-failure")
        {
            Console.Error.WriteLine("Failure [INSTALL_FAILED_INVALID_APK: fake fixture]");
            return 1;
        }

        Console.WriteLine("Success");
        return 0;
    }

    private static void PrintLogcat(string[] arguments)
    {
        if (arguments.Contains("-c", StringComparer.Ordinal))
        {
            return;
        }

        Console.WriteLine("--------- beginning of main");
        Console.WriteLine("10-27 12:00:00.000  1234  1234 I ActivityManager: Start proc com.example for activity");
        Console.WriteLine("10-27 12:00:00.100  1234  1234 W FakeAdb: deterministic fixture line");
    }

    private static int PrintState(FakeAdbOptions options)
    {
        if (options.Scenario is "metadata-failure" or "offline")
        {
            Console.Error.WriteLine("error: device offline");
            return 1;
        }

        Console.WriteLine("device");
        return 0;
    }

    private static async Task<int> StartServerAsync(FakeAdbOptions options)
    {
        if (options.Port == 0)
        {
            Console.Error.WriteLine("fake adb: start-server requires a fixed --port value.");
            return 1;
        }

        if (await IsListeningAsync(options.Port).ConfigureAwait(false))
        {
            return 0;
        }

        StartDetachedServer(options);
        var deadline = DateTime.UtcNow + ServerStartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await IsListeningAsync(options.Port).ConfigureAwait(false))
            {
                return 0;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        Console.Error.WriteLine($"fake adb: the server did not start on port {options.Port}.");
        return 1;
    }

    private static void StartDetachedServer(FakeAdbOptions options)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--serve");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(Program.FormatPort(options.Port));
        startInfo.ArgumentList.Add("--state-dir");
        startInfo.ArgumentList.Add(options.StateDirectory);
        startInfo.ArgumentList.Add("--scenario");
        startInfo.ArgumentList.Add(options.Scenario);
        startInfo.ArgumentList.Add("--serial");
        startInfo.ArgumentList.Add(options.Serial);
        using var process = Process.Start(startInfo);
    }

    private static async Task<int> KillServerAsync(FakeAdbOptions options)
    {
        if (options.Port == 0)
        {
            return 0;
        }

        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.ConnectAsync(IPAddress.Loopback, options.Port, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var request = Encoding.UTF8.GetBytes(FormatRequest("host:kill"));
            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            await stream.FlushAsync(timeout.Token).ConfigureAwait(false);
            var status = new byte[4];
            await stream.ReadExactlyAsync(status, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or OperationCanceledException)
        {
            return 0;
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (!await IsListeningAsync(options.Port).ConfigureAwait(false))
            {
                break;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<bool> IsListeningAsync(int port)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static string FormatRequest(string request)
    {
        var length = Encoding.UTF8.GetByteCount(request);
        return length.ToString("X4", CultureInfo.InvariantCulture) + request;
    }
}

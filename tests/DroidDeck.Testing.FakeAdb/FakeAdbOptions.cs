using System.Globalization;

namespace DroidDeck.Testing.FakeAdb;

internal sealed class FakeAdbOptions
{
    public const string StateDirectoryVariable = "FAKEADB_STATE_DIR";
    public const string ScenarioVariable = "FAKEADB_SCENARIO";
    public const string SerialVariable = "FAKEADB_SERIAL";

    private FakeAdbOptions()
    {
    }

    public bool Serve { get; private set; }

    public bool ShowHelp { get; private set; }

    public int Port { get; private set; } = 5037;

    public string StateDirectory { get; private set; } = string.Empty;

    public string Scenario { get; private set; } = "ready";

    public string Serial { get; private set; } = "FAKE-READY-01";

    public IReadOnlyList<string> AdbArguments { get; private set; } = [];

    public static FakeAdbOptions Parse(IReadOnlyList<string> args)
    {
        var options = new FakeAdbOptions();
        options.Port = ReadPort(Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT"));
        options.StateDirectory = Path.GetFullPath(
            Environment.GetEnvironmentVariable(StateDirectoryVariable) ??
            Path.Combine(Path.GetTempPath(), "droiddeck-fakeadb"));
        options.Scenario = ReadScenario(Environment.GetEnvironmentVariable(ScenarioVariable) ?? "ready");
        options.Serial = ReadSerial(Environment.GetEnvironmentVariable(SerialVariable));

        var adbArguments = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--serve":
                    options.Serve = true;
                    break;
                case "--help":
                    options.ShowHelp = true;
                    break;
                case "--port":
                    options.Port = ReadPort(NextValue(args, ref index, "--port"));
                    break;
                case "--state-dir":
                    options.StateDirectory = Path.GetFullPath(NextValue(args, ref index, "--state-dir"));
                    break;
                case "--scenario":
                    options.Scenario = ReadScenario(NextValue(args, ref index, "--scenario"));
                    break;
                case "--serial":
                    options.Serial = ReadSerial(NextValue(args, ref index, "--serial"));
                    break;
                default:
                    adbArguments.Add(argument);
                    break;
            }
        }

        options.AdbArguments = adbArguments;
        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            FakeAdb - a scriptable adb stand-in for DroidDeck tests

            Usage:
              FakeAdb [fake options] <adb command> [args...]
              FakeAdb --serve [fake options]

            Fake options:
              --port <port>        Server port (default 5037 or ANDROID_ADB_SERVER_PORT)
              --state-dir <dir>    Directory holding devices.txt (default %TEMP%\droiddeck-fakeadb)
              --scenario <name>    ready, offline, unauthorized, empty, hotplug,
                                   metadata-failure, install-failure, socket-failure
              --serial <serial>    Serial used by scripted devices
              --serve              Run the socket server in the foreground
              --help               Show this help

            Environment variables: ANDROID_ADB_SERVER_PORT, FAKEADB_STATE_DIR,
            FAKEADB_SCENARIO, FAKEADB_SERIAL.

            The server broadcasts lines from <state-dir>\devices.txt, so tests can
            simulate hotplug by rewriting that file.
            """);
    }

    private static string NextValue(IReadOnlyList<string> args, ref int index, string name)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"fake adb: {name} requires a value.");
        }

        return args[++index];
    }

    private static int ReadPort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 5037;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 0 or > 65535)
        {
            throw new ArgumentException($"fake adb: invalid port '{value}'.");
        }

        return port;
    }

    private static string ReadScenario(string value) => value.Trim().ToLowerInvariant() switch
    {
        "ready" => "ready",
        "offline" => "offline",
        "unauthorized" => "unauthorized",
        "empty" => "empty",
        "hotplug" => "hotplug",
        "metadata-failure" => "metadata-failure",
        "install-failure" => "install-failure",
        "socket-failure" => "socket-failure",
        _ => throw new ArgumentException($"fake adb: unknown scenario '{value}'."),
    };

    private static string ReadSerial(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "FAKE-READY-01" : value.Trim();
}

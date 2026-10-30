using System.Globalization;

namespace DroidDeck.ReleaseCheck;

internal static class Program
{
    private const string Usage = """
        DroidDeck release checks

        Usage:
          droiddeck-release-check size    --directory <publish-dir> [--zip <file>] [--max-extracted-mb 250] [--max-zip-mb 120] [--json <file>]
          droiddeck-release-check startup --exe <DroidDeck.exe> [--runs 10] [--warmup-runs 1] [--timeout-seconds 60] [--budget-median-ms 2000] [--budget-p95-ms 3000] [--json <file>]
          droiddeck-release-check soak    (--pid <id> | --process <name>) [--duration 08:00:00] [--interval 00:00:10] [--warmup 00:05:00] [--max-memory-growth-percent 5] [--max-handle-growth 32] [--json <file>]

        Exit codes: 0 pass, 1 budget failure, 2 usage or environment error.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Out.WriteLine(Usage);
            return 2;
        }

        if (args[0] is "-h" or "--help" or "help")
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        var command = args[0];
        try
        {
            var options = Options.Parse(args[1..]);
            return command switch
            {
                "size" => SizeCheck.Run(options),
                "startup" => await StartupCheck.RunAsync(options, CancellationToken.None).ConfigureAwait(false),
                "soak" => await SoakCheck.RunAsync(options, CancellationToken.None).ConfigureAwait(false),
                _ => Unknown(command),
            };
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);
        return 2;
    }
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _values;

    private Options(Dictionary<string, string> values)
    {
        _values = values;
    }

    public static Options Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown argument '{argument}'.");
            }

            var name = argument[2..];
            if (name.Length == 0 || index + 1 >= args.Count)
            {
                throw new ArgumentException($"The option '--{name}' needs a value.");
            }

            values[name] = args[++index];
        }

        return new Options(values);
    }

    public string Required(string name) =>
        _values.TryGetValue(name, out var value)
            ? value
            : throw new ArgumentException($"The option '--{name}' is required.");

    public string? Optional(string name) =>
        _values.TryGetValue(name, out var value) ? value : null;

    public int OptionalInt(string name, int defaultValue) =>
        _values.TryGetValue(name, out var value)
            ? int.Parse(value, CultureInfo.InvariantCulture)
            : defaultValue;

    public double OptionalDouble(string name, double defaultValue) =>
        _values.TryGetValue(name, out var value)
            ? double.Parse(value, CultureInfo.InvariantCulture)
            : defaultValue;

    public TimeSpan OptionalDuration(string name, TimeSpan defaultValue) =>
        _values.TryGetValue(name, out var value)
            ? TimeSpan.Parse(value, CultureInfo.InvariantCulture)
            : defaultValue;
}

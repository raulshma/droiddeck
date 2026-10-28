using System.Diagnostics;
using System.Globalization;
using System.Text;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

if (args.Length == 0)
{
    Console.Error.WriteLine("No command supplied.");
    return 64;
}

var command = args[0];
var rest = args.Skip(1).ToArray();

switch (command)
{
    case "echo":
        Console.WriteLine(rest.Length == 0 ? string.Empty : rest[0]);
        return 0;

    case "out-lines":
    {
        var count = ParseInt(rest, 0);
        for (var index = 0; index < count; index++)
        {
            Console.WriteLine($"line {index.ToString(CultureInfo.InvariantCulture)}");
        }

        return 0;
    }

    case "stderr":
        Console.Error.WriteLine(rest.Length == 0 ? "error" : rest[0]);
        return 2;

    case "both":
        Console.WriteLine(rest.Length > 0 ? rest[0] : "out");
        Console.Error.WriteLine(rest.Length > 1 ? rest[1] : "err");
        return 0;

    case "exit":
        return ParseInt(rest, 0);

    case "ansi":
        Console.WriteLine("\u001b[31mred\u001b[0m plain \u001b[1;32mgreen\u001b[0m");
        return 0;

    case "env":
        Console.WriteLine(Environment.GetEnvironmentVariable(rest[0]) ?? "<unset>");
        return 0;

    case "cwd":
        Console.WriteLine(Environment.CurrentDirectory);
        return 0;

    case "unicode":
        Console.WriteLine("héllo → 世界");
        return 0;

    case "binary":
    {
        using var stdout = Console.OpenStandardOutput();
        var buffer = new byte[256];
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = (byte)index;
        }

        stdout.Write(buffer);
        stdout.Flush();
        return 0;
    }

    case "big":
    {
        var characters = ParseInt(rest, 0);
        var line = new string('x', 128);
        using var stdout = Console.OpenStandardOutput();
        for (var written = 0; written < characters; written += line.Length)
        {
            var text = Encoding.UTF8.GetBytes(line + "\n");
            stdout.Write(text);
        }

        stdout.Flush();
        return 0;
    }

    case "long-line":
    {
        var characters = ParseInt(rest, 0);
        using var stdout = Console.OpenStandardOutput();
        var block = Encoding.UTF8.GetBytes(new string('x', 8192));
        for (var written = 0; written < characters; written += block.Length)
        {
            stdout.Write(block);
        }

        stdout.Flush();
        return 0;
    }

    case "progress":
        Console.Write("[ 10%] first\r[ 60%] second\r[100%] third\n");
        return 0;

    case "stdin":
        Console.Write(Console.In.ReadToEnd());
        return 0;

    case "sleep":
        await Task.Delay(ParseInt(rest, 0)).ConfigureAwait(false);
        return 0;

    case "spawn":
    {
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        child.StartInfo.ArgumentList.Add("/c");
        child.StartInfo.ArgumentList.Add("ping -n 60 127.0.0.1 >nul");
        child.Start();
        Console.WriteLine($"child:{child.Id}");
        Console.Out.Flush();
        await Task.Delay(ParseInt(rest, 0)).ConfigureAwait(false);
        return 0;
    }

    default:
        Console.Error.WriteLine($"Unknown command '{command}'.");
        return 64;
}

static int ParseInt(string[] values, int fallback) =>
    values.Length > 0 && int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        ? parsed
        : fallback;

namespace DroidDeck.PluginPack;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? pluginDirectory = null;
        string? outputDirectory = null;
        var includeSymbols = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--help" or "-h":
                    PrintUsage(Console.Out);
                    return 0;
                case "--include-symbols":
                    includeSymbols = true;
                    break;
                case "--output" or "-o":
                    if (index + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("error: --output needs a directory.");
                        PrintUsage(Console.Error);
                        return 1;
                    }

                    outputDirectory = args[++index];
                    break;
                default:
                    if (args[index].Length > 0 && args[index][0] == '-')
                    {
                        Console.Error.WriteLine($"error: unknown option '{args[index]}'.");
                        PrintUsage(Console.Error);
                        return 1;
                    }

                    if (pluginDirectory is not null)
                    {
                        Console.Error.WriteLine("error: only one plugin directory can be packaged per run.");
                        PrintUsage(Console.Error);
                        return 1;
                    }

                    pluginDirectory = args[index];
                    break;
            }
        }

        if (pluginDirectory is null)
        {
            PrintUsage(Console.Error);
            return 1;
        }

        try
        {
            var result = await PluginPackager.CreateAsync(
                pluginDirectory,
                outputDirectory ?? Environment.CurrentDirectory,
                includeSymbols).ConfigureAwait(false);

            Console.WriteLine($"Packed {result.PluginId} {result.Version}");
            Console.WriteLine($"Package  {result.PackagePath}");
            Console.WriteLine($"SHA-256  {result.Sha256}");
            Console.WriteLine($"Size     {result.SizeInBytes} bytes");
            Console.WriteLine("Entries");
            foreach (var entry in result.Entries)
            {
                Console.WriteLine($"  {entry}");
            }

            return 0;
        }
        catch (PluginPackException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: droiddeck-plugin-pack <plugin-directory> [--output <directory>] [--include-symbols]");
        writer.WriteLine();
        writer.WriteLine("Packages a built DroidDeck plugin folder as <pluginId>-<version>.ddplugin.");
        writer.WriteLine("The output directory defaults to the current directory.");
    }
}

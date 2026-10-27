using System.Globalization;

namespace DroidDeck.Testing.FakeAdb;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        FakeAdbOptions options;
        try
        {
            options = FakeAdbOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }

        if (options.ShowHelp)
        {
            FakeAdbOptions.PrintHelp();
            return 0;
        }

        if (options.Serve)
        {
            return await FakeAdbServer.RunAsync(options).ConfigureAwait(false);
        }

        return await FakeAdbCommands.RunAsync(options).ConfigureAwait(false);
    }

    internal static string FormatPort(int port) => port.ToString(CultureInfo.InvariantCulture);
}

namespace DroidDeck.Testing.FakeAdb;

internal static class FakeAdbState
{
    private const string DevicesFileName = "devices.txt";

    public static string[] ReadDeviceLines(FakeAdbOptions options)
    {
        var path = Path.Combine(options.StateDirectory, DevicesFileName);
        try
        {
            if (File.Exists(path))
            {
                return File.ReadAllLines(path)
                    .Select(static line => line.Trim())
                    .Where(static line => line.Length > 0)
                    .ToArray();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return FakeAdbScenario.InitialDeviceLines(options);
    }

    public static string BuildPayload(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? string.Empty : string.Join('\n', lines) + '\n';

    public static void WritePort(FakeAdbOptions options, int port)
    {
        try
        {
            Directory.CreateDirectory(options.StateDirectory);
            File.WriteAllText(Path.Combine(options.StateDirectory, "server-port.txt"), Program.FormatPort(port));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

using System.Diagnostics;
using System.Globalization;

namespace DroidDeck.UiAutomation.Tests;

internal sealed class FakeAdbServerProcess : IAsyncDisposable
{
    private const string ReadyPrefix = "FAKEADB_READY ";

    private readonly Process _process;

    private FakeAdbServerProcess(Process process, int port)
    {
        _process = process;
        Port = port;
    }

    public int Port { get; }

    public static async Task<FakeAdbServerProcess> StartAsync(
        string stateDirectory,
        string scenario,
        string serial)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = TestBinaries.FakeAdbExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--serve");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--state-dir");
        startInfo.ArgumentList.Add(stateDirectory);
        startInfo.ArgumentList.Add("--scenario");
        startInfo.ArgumentList.Add(scenario);
        startInfo.ArgumentList.Add("--serial");
        startInfo.ArgumentList.Add(serial);

        var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("FakeAdb could not be started.");
        try
        {
            var readyLine = await process.StandardOutput
                .ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
            if (readyLine is null ||
                !readyLine.StartsWith(ReadyPrefix, StringComparison.Ordinal) ||
                !int.TryParse(
                    readyLine[ReadyPrefix.Length..],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var port))
            {
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"FakeAdb did not report a listening port. Output: '{readyLine}'. Error: '{error}'.");
            }

            return new FakeAdbServerProcess(process, port);
        }
        catch
        {
            Kill(process);
            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill(_process);
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _process.Dispose();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
        }
    }
}

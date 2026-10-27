using DroidDeck.Core.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DroidDeck.App;

/// <summary>
/// The application entry point. A custom Main is used so command-line options
/// and single-instance activation are handled before WinUI starts.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options.ShowVersion)
        {
            Console.WriteLine(AppInfo.Version);
            return 0;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(AppOptions.GetHelpText());
            return 0;
        }

        AppInstanceCoordinator? coordinator = null;
        var paths = options.ResolvePaths();
        try
        {
            paths.EnsureCreated();
            coordinator = AppInstanceCoordinator.TryAcquireOrForward(paths.DataDirectory, args);
            if (coordinator is null)
            {
                return 0;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(activation =>
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                _ = new App(options, paths, coordinator);
            });
            return 0;
        }
        catch (Exception exception)
        {
            WriteStartupCrash(exception);
            return 3;
        }
        finally
        {
            coordinator?.Dispose();
        }
    }

    private static void WriteStartupCrash(Exception exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "DroidDeck-startup-crash.log");
            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}");
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
            // Nothing else can be done if the crash log cannot be written.
        }
    }
}

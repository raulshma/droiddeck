using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.Core;
using DroidDeck.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DroidDeck.App.Views;

/// <summary>Previews, copies, and saves a redacted diagnostics report.</summary>
public sealed partial class DiagnosticsPage : Page, IRefreshablePage
{
    private readonly IDiagnosticsService _diagnostics;
    private readonly PluginHost _plugins;
    private readonly AppPaths _paths;
    private readonly ILogger<DiagnosticsPage> _logger;

    /// <summary>Creates the diagnostics page.</summary>
    public DiagnosticsPage(
        IDiagnosticsService diagnostics,
        PluginHost plugins,
        AppPaths paths,
        ILogger<DiagnosticsPage> logger)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeComponent();
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        DiagnosticsHeaderText.Text =
            $"Diagnostics {_diagnostics.DiagnosticsId} · Session {_diagnostics.SessionId} · Reports stay on this computer.";
        ReportText.Text = ShellDiagnostics.BuildReport(_diagnostics, _plugins);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        Refresh();
        ShowAction("Diagnostics preview refreshed.");
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(ReportText.Text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowAction($"Diagnostics copied ({_diagnostics.DiagnosticsId}).");
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The diagnostics report could not be copied.");
            ShowAction("The report could not be copied to the clipboard.", isError: true);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var path = await ShellDiagnostics.SaveReportAsync(_diagnostics, _plugins);
            ShowAction($"Report saved to {path}");
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Diagnostics report saved to {Path}.", path);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The diagnostics report could not be saved.");
            ShowAction(
                $"The report could not be saved: {exception.Message} " +
                $"(Diagnostics ID {_diagnostics.DiagnosticsId}).",
                isError: true);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _paths.EnsureCreated();
            Directory.CreateDirectory(_paths.DiagnosticsDirectory);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.DiagnosticsDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "The diagnostics folder could not be opened.");
            ShowAction("The diagnostics folder could not be opened.", isError: true);
        }
    }

    private void ShowAction(string message, bool isError = false)
    {
        ActionBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }
}

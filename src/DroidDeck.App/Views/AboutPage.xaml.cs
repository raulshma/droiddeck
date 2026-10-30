using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DroidDeck.Core;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Toolchain;
using DroidDeck.Core.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DroidDeck.App.Views;

/// <summary>
/// Shows installed versions, local data locations, licenses, diagnostics access,
/// and the opt-in manual update check.
/// </summary>
public sealed partial class AboutPage : Page, IRefreshablePage
{
    private readonly AppPaths _paths;
    private readonly IConfigurationService _configuration;
    private readonly UpdateChecker _updates;
    private readonly IToolchainService _toolchain;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ILogger<AboutPage> _logger;
    private bool _initializing;
    private bool _checking;
    private bool _probingScrcpy;
    private Uri? _releasePage;

    /// <summary>Creates the About page.</summary>
    public AboutPage(
        AppPaths paths,
        IConfigurationService configuration,
        UpdateChecker updates,
        IToolchainService toolchain,
        IDiagnosticsService diagnostics,
        ILogger<AboutPage> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeComponent();
        VersionText.Text = AppInfo.Version;
        RuntimeText.Text = $"{AppInfo.RuntimeVersion} · {AppInfo.Architecture}";
        WindowsAppSdkText.Text = AppInfo.WindowsAppSdkVersion;
        OsText.Text = AppInfo.OperatingSystem;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        _initializing = true;
        try
        {
            DataFolderText.Text = _paths.DataDirectory;
            LogsFolderText.Text = _paths.LogsDirectory;
            DiagnosticsFolderText.Text = _paths.DiagnosticsDirectory;
            PluginsFolderText.Text = _paths.PluginsDirectory;
            StorageModeText.Text = _paths.IsPortable
                ? "Portable mode: data is stored in the data folder beside DroidDeck.exe."
                : "Per-user mode: data is stored in %LOCALAPPDATA%\\DroidDeck. Add portable.marker beside the executable to keep data beside the app.";
            DiagnosticsIdText.Text =
                $"Diagnostics ID {_diagnostics.DiagnosticsId} · Session {_diagnostics.SessionId}";
            UpdateCheckToggle.IsOn = _configuration.Current.Settings.Updates.ManualCheckEnabled;
            CheckUpdatesButton.IsEnabled = UpdateCheckToggle.IsOn;
        }
        finally
        {
            _initializing = false;
        }

        _ = LoadScrcpyVersionAsync();
    }

    private async Task LoadScrcpyVersionAsync()
    {
        if (_probingScrcpy)
        {
            return;
        }

        _probingScrcpy = true;
        ScrcpyVersionText.Text = "Checking…";
        try
        {
            var scrcpy = await _toolchain.ProbeScrcpyAsync();
            ScrcpyVersionText.Text = scrcpy.IsAvailable
                ? string.IsNullOrWhiteSpace(scrcpy.Version) ? "Installed (version unknown)" : scrcpy.Version
                : "Not detected";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The scrcpy version could not be read for the About page.");
            ScrcpyVersionText.Text = "Unavailable";
        }
        finally
        {
            _probingScrcpy = false;
        }
    }

    private async void OnUpdateCheckToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var enabled = UpdateCheckToggle.IsOn;
        try
        {
            await _configuration.SetAsync(SettingsPaths.UpdatesManualCheckEnabled, enabled);
            CheckUpdatesButton.IsEnabled = enabled;
            if (!enabled)
            {
                UpdateResultBar.IsOpen = false;
                OpenReleaseButton.Visibility = Visibility.Collapsed;
                _releasePage = null;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The update check setting could not be saved.");
            _initializing = true;
            try
            {
                UpdateCheckToggle.IsOn = !enabled;
            }
            finally
            {
                _initializing = false;
            }

            ShowUpdateResult(
                InfoBarSeverity.Error,
                "The update check setting could not be saved.");
        }
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        if (_checking || !_configuration.Current.Settings.Updates.ManualCheckEnabled)
        {
            return;
        }

        _checking = true;
        CheckUpdatesButton.IsEnabled = false;
        UpdateProgress.IsActive = true;
        UpdateResultBar.IsOpen = false;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        _releasePage = null;
        try
        {
            var result = await _updates.CheckAsync(enabled: true);
            var severity = result.Status switch
            {
                UpdateCheckStatus.UpdateAvailable => InfoBarSeverity.Informational,
                UpdateCheckStatus.UpToDate => InfoBarSeverity.Success,
                UpdateCheckStatus.Disabled => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Error,
            };
            ShowUpdateResult(severity, result.Message);
            if (result.UpdateAvailable && result.ReleasePage is { } releasePage)
            {
                _releasePage = releasePage;
                OpenReleaseButton.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _checking = false;
            UpdateProgress.IsActive = false;
            CheckUpdatesButton.IsEnabled = _configuration.Current.Settings.Updates.ManualCheckEnabled;
        }
    }

    private void ShowUpdateResult(InfoBarSeverity severity, string message)
    {
        UpdateResultBar.Severity = severity;
        UpdateResultBar.Message = message;
        UpdateResultBar.IsOpen = true;
    }

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        if (_releasePage is not { } page || page.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        OpenExternal(page.AbsoluteUri, "The release page could not be opened.");
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _paths.EnsureCreated();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.DataDirectory}\"",
                UseShellExecute = true,
            });
            StorageStatusText.Text = "Data folder opened.";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "The data folder could not be opened.");
            StorageStatusText.Text = "The data folder could not be opened.";
        }
    }

    private void OnCopyDataPathsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(
                $"Data: {_paths.DataDirectory}{Environment.NewLine}" +
                $"Logs: {_paths.LogsDirectory}{Environment.NewLine}" +
                $"Diagnostics: {_paths.DiagnosticsDirectory}{Environment.NewLine}" +
                $"Plugins: {_paths.PluginsDirectory}");
            Clipboard.SetContent(package);
            Clipboard.Flush();
            StorageStatusText.Text = "Data paths copied.";
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The data paths could not be copied.");
            StorageStatusText.Text = "The data paths could not be copied.";
        }
    }

    private void OnOpenDiagnosticsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("diagnostics");

    private void OpenExternal(string uri, string failureMessage)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The link {Uri} could not be opened.", uri);
            ShowUpdateResult(InfoBarSeverity.Error, failureMessage);
        }
    }
}

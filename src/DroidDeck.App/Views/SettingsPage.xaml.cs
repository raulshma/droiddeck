using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.App.ViewModels;
using DroidDeck.Core;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Toolchain;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DroidDeck.App.Views;

/// <summary>Application settings, stored in the local settings document.</summary>
public sealed partial class SettingsPage : Page, IRefreshablePage, IDisposable
{
    private readonly IConfigurationService _configuration;
    private readonly IToolchainService _toolchain;
    private readonly IDiagnosticsService _diagnostics;
    private readonly PluginHost _plugins;
    private readonly AppPaths _paths;
    private readonly ILogger<SettingsPage> _logger;
    private readonly ToolPathRow[] _toolPaths = ToolPathRow.CreateAll();
    private bool _initializing;
    private bool _disposed;

    /// <summary>Creates the settings page.</summary>
    public SettingsPage(
        IConfigurationService configuration,
        IToolchainService toolchain,
        IDiagnosticsService diagnostics,
        PluginHost plugins,
        AppPaths paths,
        ILogger<SettingsPage> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeComponent();
        ThemeCombo.ItemsSource = Enum.GetValues<AppTheme>();
        ToolPathsList.ItemsSource = _toolPaths;
        _toolchain.SnapshotChanged += OnToolchainSnapshotChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _toolchain.SnapshotChanged -= OnToolchainSnapshotChanged;
    }

    /// <inheritdoc />
    public void Refresh()
    {
        _initializing = true;
        try
        {
            var settings = _configuration.Current.Settings;
            ThemeCombo.SelectedItem = settings.Appearance.Theme;
            foreach (var row in _toolPaths)
            {
                row.Path = GetConfiguredPath(settings.Tools, row.Kind) ?? string.Empty;
            }

            SafeModeToggle.IsOn = settings.Features.SafeMode;
            DeveloperModeToggle.IsOn = settings.Features.DeveloperMode;
            UpdateSourceHints();
        }
        finally
        {
            _initializing = false;
        }

        DataFolderText.Text = _paths.DataDirectory;
        DiagnosticsIdText.Text =
            $"Diagnostics ID: {_diagnostics.DiagnosticsId} · Session: {_diagnostics.SessionId}";
        VersionText.Text = $"DroidDeck {AppInfo.Version}";
        RuntimeText.Text = $"Runtime {AppInfo.RuntimeVersion} · {AppInfo.Architecture}";
        OsText.Text = AppInfo.OperatingSystem;

        var notice = _configuration.LastLoadNotice;
        LoadNoticeBar.IsOpen = notice is not null;
        LoadNoticeBar.Message = notice?.Message ?? string.Empty;
        UpdateToolchainStatus();
    }

    private void UpdateSourceHints()
    {
        ThemeSourceText.Text = $"Source: {_configuration.GetSource(SettingsPaths.Theme).DisplayName}";
        foreach (var row in _toolPaths)
        {
            row.Source = $"Source: {_configuration.GetSource(row.SettingsPath).DisplayName}";
        }
    }

    private static string? GetConfiguredPath(ToolsSettings tools, ToolKind kind) => kind switch
    {
        ToolKind.AndroidSdk => tools.AndroidSdkPath,
        ToolKind.Adb => tools.AdbPath,
        ToolKind.Aapt2 => tools.Aapt2Path,
        ToolKind.Emulator => tools.EmulatorPath,
        ToolKind.Java => tools.JavaHome,
        ToolKind.Gradle => tools.GradlePath,
        ToolKind.Flutter => tools.FlutterPath,
        ToolKind.Node => tools.NodePath,
        ToolKind.Scrcpy => tools.ScrcpyPath,
        ToolKind.Unity => tools.UnityPath,
        _ => null,
    };

    private void UpdateToolchainStatus()
    {
        var snapshot = _toolchain.CachedSnapshot;
        if (snapshot is null)
        {
            ToolchainStatusText.Text = "Toolchain has not been checked in this session.";
            ToolchainList.ItemsSource = null;
            CopyToolchainGuidanceButton.IsEnabled = false;
            return;
        }

        var available = snapshot.Tools.Count(tool => tool.IsAvailable);
        ToolchainStatusText.Text =
            $"{available}/{snapshot.Tools.Count} tools available · checked {snapshot.CheckedAt.ToLocalTime():g}.";
        ToolchainList.ItemsSource = snapshot.Tools.Select(ToolchainToolRow.From).ToArray();
        CopyToolchainGuidanceButton.IsEnabled = snapshot.Tools.Any(
            tool => !tool.IsAvailable && !string.IsNullOrWhiteSpace(tool.Guidance));
    }

    private void OnToolchainSnapshotChanged(object? sender, ToolchainSnapshot snapshot) =>
        DispatcherQueue.TryEnqueue(UpdateToolchainStatus);

    private void OnCopyToolchainGuidanceClick(object sender, RoutedEventArgs e)
    {
        var snapshot = _toolchain.CachedSnapshot;
        var lines = snapshot?.Tools
            .Where(tool => !tool.IsAvailable && !string.IsNullOrWhiteSpace(tool.Guidance))
            .Select(tool => $"{tool.DisplayName}: {tool.Guidance}")
            .ToArray() ?? [];
        if (lines.Length == 0)
        {
            ShowAction("No missing-tool guidance is available.");
            return;
        }

        CopyText(string.Join(Environment.NewLine, lines), "Toolchain fix guidance copied.");
    }

    private void CopyText(string text, string successMessage)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowAction(successMessage);
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The toolchain guidance could not be copied.");
            ShowAction("The toolchain guidance could not be copied.", isError: true);
        }
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing ||
            ThemeCombo.SelectedItem is not AppTheme theme ||
            theme == _configuration.Current.Settings.Appearance.Theme)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.Theme, theme);
            App.Instance.ApplyTheme(theme);
            UpdateSourceHints();
            ShowAction($"Theme changed to {theme}.");
        }
        catch (Exception exception)
        {
            ReportFailure("Changing the theme", exception);
        }
    }

    private async void OnSaveToolPathClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ToolPathRow row)
        {
            return;
        }

        var path = row.Path.Trim();
        try
        {
            if (path.Length == 0)
            {
                await _configuration.ResetAsync(row.SettingsPath);
                ShowAction($"{row.DisplayName} path reset to auto-detection. Run Check toolchain to refresh results.");
            }
            else
            {
                await _configuration.SetAsync(row.SettingsPath, path);
                ShowAction($"{row.DisplayName} path saved. Run Check toolchain to apply it.");
            }

            row.Path = path;
            UpdateSourceHints();
        }
        catch (Exception exception)
        {
            ReportFailure($"Saving the {row.DisplayName} path", exception);
        }
    }

    private async void OnResetToolPathClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ToolPathRow row)
        {
            return;
        }

        try
        {
            await _configuration.ResetAsync(row.SettingsPath);
            row.Path = string.Empty;
            UpdateSourceHints();
            ShowAction($"{row.DisplayName} path reset to auto-detection.");
        }
        catch (Exception exception)
        {
            ReportFailure($"Resetting the {row.DisplayName} path", exception);
        }
    }

    private async void OnCheckToolchainClick(object sender, RoutedEventArgs e)
    {
        CheckToolchainButton.IsEnabled = false;
        ToolchainStatusText.Text = "Checking the toolchain…";
        try
        {
            await _toolchain.CheckAsync();
            ShowAction("Toolchain check finished.");
        }
        catch (Exception exception)
        {
            ReportFailure("Checking the toolchain", exception);
        }
        finally
        {
            CheckToolchainButton.IsEnabled = true;
            UpdateToolchainStatus();
        }
    }

    private async void OnSafeModeToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.SafeMode, SafeModeToggle.IsOn);
            ShowAction("Safe mode saved. Restart DroidDeck to apply it.");
        }
        catch (Exception exception)
        {
            ReportFailure("Saving safe mode", exception);
        }
    }

    private async void OnDeveloperModeToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.DeveloperMode, DeveloperModeToggle.IsOn);
            ShowAction("Developer mode saved.");
        }
        catch (Exception exception)
        {
            ReportFailure("Saving developer mode", exception);
        }
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
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "The data folder could not be opened.");
            ShowAction("The data folder could not be opened.", isError: true);
        }
    }

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(ShellDiagnostics.BuildReport(_diagnostics, _plugins));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowAction($"Diagnostics copied ({_diagnostics.DiagnosticsId}).");
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Diagnostics could not be copied from settings.");
            ShowAction("Diagnostics could not be copied. Open Diagnostics to save a report.", isError: true);
        }
    }

    private void OnOpenDiagnosticsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("diagnostics");

    private void ShowAction(string message, bool isError = false)
    {
        ActionBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }

    private void ReportFailure(string action, Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(exception, "{Action} failed.", action);
        }

        ShowAction(
            $"{action} failed: {exception.Message} (Diagnostics ID {_diagnostics.DiagnosticsId}).",
            isError: true);
    }
}

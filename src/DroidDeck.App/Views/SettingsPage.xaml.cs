using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.App.ViewModels;
using DroidDeck.Core;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Sessions;
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
    private readonly ISessionStore _sessions;
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
        ISessionStore sessions,
        PluginHost plugins,
        AppPaths paths,
        ILogger<SettingsPage> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
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
            NotificationsEnabledToggle.IsOn = settings.Notifications.Enabled;
            NotifySucceededToggle.IsOn = settings.Notifications.RunSucceeded;
            NotifyFailedToggle.IsOn = settings.Notifications.RunFailed;
            NotifyCancelledToggle.IsOn = settings.Notifications.RunCancelled;
            NotifyCrashToggle.IsOn = settings.Notifications.Crash;
            NotifyInstallFailedToggle.IsOn = settings.Notifications.InstallFailed;
            RetentionDaysBox.Value = settings.Logging.RetentionDays;
            RetentionGigabytesBox.Value = BytesToGigabytes(settings.Logging.MaxSessionBytes);
            RunConcurrencyBox.Value = settings.Run.MaxConcurrentDevices;
            UpdateRetentionSummary();
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

    private const double BytesPerGigabyte = 1024d * 1024d * 1024d;

    private static double BytesToGigabytes(long bytes) =>
        Math.Round(bytes / BytesPerGigabyte, 2);

    private void UpdateRetentionSummary()
    {
        var logging = _configuration.Current.Settings.Logging;
        var days = logging.RetentionDays <= 0 ? "no age limit" : $"{logging.RetentionDays} days";
        var size = logging.MaxSessionBytes <= 0
            ? "no storage limit"
            : $"{BytesToGigabytes(logging.MaxSessionBytes):0.##} GB";
        RetentionSummaryText.Text =
            $"Current policy: keep sessions for {days}, up to {size}. Oldest sessions are removed first.";
    }

    private async Task SaveNotificationPreferenceAsync(string path, bool value, string message)
    {
        if (_initializing)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(path, value);
            ShowAction(message);
        }
        catch (Exception exception)
        {
            ReportFailure("Saving notification preferences", exception);
        }
    }

    private async void OnNotificationsEnabledToggled(object sender, RoutedEventArgs e) =>
        await SaveNotificationPreferenceAsync(
            SettingsPaths.NotificationsEnabled,
            NotificationsEnabledToggle.IsOn,
            "Notification preferences saved.");

    private async void OnNotifySucceededToggled(object sender, RoutedEventArgs e) =>
        await SaveNotificationPreferenceAsync(
            SettingsPaths.NotifyRunSucceeded,
            NotifySucceededToggle.IsOn,
            "Successful-run notifications updated.");

    private async void OnNotifyFailedToggled(object sender, RoutedEventArgs e) =>
        await SaveNotificationPreferenceAsync(
            SettingsPaths.NotifyRunFailed,
            NotifyFailedToggle.IsOn,
            "Failed-run notifications updated.");

    private async void OnNotifyCancelledToggled(object sender, RoutedEventArgs e) =>
        await SaveNotificationPreferenceAsync(
            SettingsPaths.NotifyRunCancelled,
            NotifyCancelledToggle.IsOn,
            "Cancelled-run notifications updated.");

    private async void OnNotifyCrashToggled(object sender, RoutedEventArgs e) =>
        await SaveNotificationPreferenceAsync(
            SettingsPaths.NotifyCrash,
            NotifyCrashToggle.IsOn,
            "Crash notifications updated.");

    private async void OnNotifyInstallFailedToggled(object sender, RoutedEventArgs e) =>
        await SaveNotificationPreferenceAsync(
            SettingsPaths.NotifyInstallFailed,
            NotifyInstallFailedToggle.IsOn,
            "Install-failure notifications updated.");

    private async void OnRetentionDaysChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing || double.IsNaN(args.NewValue))
        {
            return;
        }

        var days = Math.Clamp((int)Math.Round(args.NewValue), 0, 365);
        if (days == _configuration.Current.Settings.Logging.RetentionDays)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.SessionRetentionDays, days);
            UpdateRetentionSummary();
            ShowAction(days == 0
                ? "Session age limit removed."
                : $"Sessions are kept for {days} days.");
        }
        catch (Exception exception)
        {
            ReportFailure("Saving the session age limit", exception);
        }
    }

    private async void OnRetentionGigabytesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing || double.IsNaN(args.NewValue))
        {
            return;
        }

        var gigabytes = Math.Clamp(args.NewValue, 0, 1024);
        var bytes = gigabytes <= 0 ? 0 : (long)Math.Round(gigabytes * BytesPerGigabyte);
        if (bytes == _configuration.Current.Settings.Logging.MaxSessionBytes)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.SessionMaxBytes, bytes);
            UpdateRetentionSummary();
            ShowAction(bytes == 0
                ? "Session storage limit removed."
                : $"Session storage is limited to {BytesToGigabytes(bytes):0.##} GB.");
        }
        catch (Exception exception)
        {
            ReportFailure("Saving the session storage limit", exception);
        }
    }

    private async void OnRunConcurrencyChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing || double.IsNaN(args.NewValue))
        {
            return;
        }

        var limit = Math.Clamp((int)Math.Round(args.NewValue), 1, 16);
        if (limit == _configuration.Current.Settings.Run.MaxConcurrentDevices)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.RunMaxConcurrentDevices, limit);
            ShowAction($"Multi-device runs use at most {limit} device(s) at a time.");
        }
        catch (Exception exception)
        {
            ReportFailure("Saving the multi-device concurrency limit", exception);
        }
    }

    private async void OnApplyRetentionClick(object sender, RoutedEventArgs e)
    {
        ApplyRetentionButton.IsEnabled = false;
        try
        {
            var logging = _configuration.Current.Settings.Logging;
            var result = await _sessions.ApplyRetentionAsync(logging.RetentionDays, logging.MaxSessionBytes);
            ShowAction(result.Summary);
        }
        catch (Exception exception)
        {
            ReportFailure("Applying session retention", exception);
        }
        finally
        {
            ApplyRetentionButton.IsEnabled = true;
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

    private async void OnRunSetupAgainClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _configuration.ResetAsync(SettingsPaths.OnboardingCompleted);
            await _configuration.ResetAsync(SettingsPaths.OnboardingStep);
            ShowAction("First-run setup was reset.");
            App.Instance.Navigate("onboarding");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Resetting first-run setup", exception);
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

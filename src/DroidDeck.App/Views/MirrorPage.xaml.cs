using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.Core;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Screen;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>
/// Starts docked or external scrcpy mirrors, captures screenshots and recordings,
/// and sends device keys. Docked sessions are stopped when the page unloads so no
/// scrcpy window is left floating over other pages; external windows keep running.
/// </summary>
public sealed partial class MirrorPage : Page, IRefreshablePage, IDisposable
{
    private static readonly string[] HostingLabels = ["Docked over pane", "External window"];

    private readonly IScreenService _screen;
    private readonly IDeviceService _devices;
    private readonly IConfigurationService _configuration;
    private readonly SessionWorkspace _workspace;
    private readonly AppPaths _paths;
    private readonly IShellDialogs _dialogs;
    private readonly ILogger<MirrorPage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private readonly HashSet<string> _startedSerials = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? _boundsTimer;
    private XamlRoot? _xamlRoot;
    private AppWindow? _appWindow;
    private bool _subscribed;
    private bool _busy;
    private bool _disposed;
    private string? _lastPath;

    /// <summary>Creates the mirror page.</summary>
    public MirrorPage(
        IScreenService screen,
        IDeviceService devices,
        IConfigurationService configuration,
        SessionWorkspace workspace,
        AppPaths paths,
        IShellDialogs dialogs,
        ILogger<MirrorPage> logger)
    {
        _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        HostingCombo.ItemsSource = HostingLabels;
        HostingCombo.SelectedIndex = string.Equals(
            _configuration.Current.Settings.Screen.HostingMode,
            "external",
            StringComparison.OrdinalIgnoreCase)
            ? 1
            : 0;
        RefreshDevices();
        UpdateState();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        RefreshDevices();
        UpdateState();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unsubscribe();
        StopStartedSessions(stopExternal: true);
    }

    private DeviceInfo? SelectedDevice => DeviceCombo.SelectedItem as DeviceInfo;

    private string? SelectedSerial => SelectedDevice?.Serial;

    private ScreenHostingMode CurrentMode => HostingCombo.SelectedIndex == 1
        ? ScreenHostingMode.ExternalWindow
        : ScreenHostingMode.DockedCompanion;

    private ScreenSessionInfo? CurrentSession => SelectedSerial is { } serial
        ? _screen.ActiveSessions.FirstOrDefault(session =>
            string.Equals(session.Serial, serial, StringComparison.Ordinal))
        : null;

    private void RefreshDevices()
    {
        var target = DeviceTargetSelection.Resolve(
            _devices.Devices,
            SelectedSerial,
            _workspace.DeviceSerial);
        DeviceCombo.ItemsSource = target.Usable;
        DeviceCombo.SelectedItem = target.Selected;
        AvailabilityBar.Message = _screen.AvailabilityError ?? string.Empty;
        AvailabilityBar.IsOpen = _screen.AvailabilityError is not null;
        if (target.Usable.Count == 0)
        {
            ShowGuidance(_devices.LastError
                ?? "Connect a device with USB debugging enabled and authorize this computer.");
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _subscribed = true;
            _screen.SessionsChanged += OnSessionsChanged;
            _devices.DevicesChanged += OnDevicesChanged;
        }

        if (XamlRoot is { } root && !ReferenceEquals(root, _xamlRoot))
        {
            if (_xamlRoot is not null)
            {
                _xamlRoot.Changed -= OnXamlRootChanged;
            }

            _xamlRoot = root;
            _xamlRoot.Changed += OnXamlRootChanged;
        }

        if (App.Instance.Window?.AppWindow is { } window && !ReferenceEquals(window, _appWindow))
        {
            if (_appWindow is not null)
            {
                _appWindow.Changed -= OnAppWindowChanged;
            }

            _appWindow = window;
            _appWindow.Changed += OnAppWindowChanged;
        }

        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unsubscribe();
        StopStartedSessions(stopExternal: false);
    }

    private void Unsubscribe()
    {
        if (_subscribed)
        {
            _subscribed = false;
            _screen.SessionsChanged -= OnSessionsChanged;
            _devices.DevicesChanged -= OnDevicesChanged;
        }

        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= OnXamlRootChanged;
            _xamlRoot = null;
        }

        if (_appWindow is not null)
        {
            _appWindow.Changed -= OnAppWindowChanged;
            _appWindow = null;
        }
    }

    private void OnSessionsChanged(object? sender, EventArgs e) => Dispatch(UpdateState);

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Dispatch(() =>
    {
        RefreshDevices();
        UpdateState();
        foreach (var serial in _startedSerials.ToArray())
        {
            if (!_devices.Devices.Any(device => string.Equals(device.Serial, serial, StringComparison.Ordinal)))
            {
                _startedSerials.Remove(serial);
            }
        }
    });

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedDevice is { } device)
        {
            _workspace.OpenDevice(device.Serial);
        }

        UpdateState();
    }

    private void OnHostingChanged(object sender, SelectionChangedEventArgs e)
    {
        StartButton.Content = CurrentMode == ScreenHostingMode.ExternalWindow ? "Start external" : "Start docked";
        ScheduleBoundsUpdate();
    }

    private void OnMirrorPaneSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleBoundsUpdate();

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ScheduleBoundsUpdate();

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange)
        {
            ScheduleBoundsUpdate();
        }
    }

    private void ScheduleBoundsUpdate()
    {
        if (CurrentMode != ScreenHostingMode.DockedCompanion)
        {
            return;
        }

        if (_boundsTimer is null)
        {
            _boundsTimer = DispatcherQueue.CreateTimer();
            _boundsTimer.Interval = TimeSpan.FromMilliseconds(120);
            _boundsTimer.IsRepeating = false;
            _boundsTimer.Tick += (_, _) => _ = UpdateDockedBoundsAsync();
        }

        _boundsTimer.Stop();
        _boundsTimer.Start();
    }

    private async Task UpdateDockedBoundsAsync()
    {
        if (CurrentSession is not { IsRunning: true } session ||
            session.HostingMode != ScreenHostingMode.DockedCompanion)
        {
            return;
        }

        if (GetPaneBounds() is not { } bounds)
        {
            return;
        }

        try
        {
            var result = await _screen.UpdateBoundsAsync(session.Serial, bounds);
            if (!result.Succeeded)
            {
                ShowResult(result);
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The mirror window could not be aligned: {exception.Message}", null, null);
        }
    }

    private ScreenBounds? GetPaneBounds() =>
        App.Instance.Window is { } window &&
        window.TryGetScreenBounds(MirrorPane, out var bounds)
            ? bounds
            : null;

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await _devices.RefreshAsync();
        Refresh();
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("settings");

    private async void OnStartClick(object sender, RoutedEventArgs e) => await StartAsync(CurrentMode);

    private async Task StartAsync(ScreenHostingMode mode)
    {
        if (SelectedDevice is not { } device)
        {
            ShowResult(false, "Select a device first.", null, null);
            return;
        }

        var settings = _configuration.Current.Settings.Screen;
        ScreenBounds? bounds = null;
        if (mode == ScreenHostingMode.DockedCompanion)
        {
            bounds = GetPaneBounds();
            if (bounds is null)
            {
                ShowResult(
                    false,
                    "The mirror pane is not visible yet, so the mirror could not be docked.",
                    null,
                    "Resize or reopen the page, or choose External window.");
                return;
            }
        }

        SetBusy(true);
        try
        {
            var result = await _screen.StartAsync(device.Serial, new ScreenStartOptions
            {
                HostingMode = mode,
                Bounds = bounds,
                MaxSize = settings.MaxSize,
                MaxFps = settings.MaxFps,
                VideoBitRate = settings.VideoBitRate,
                StayAwake = settings.StayAwake,
                ClipboardSync = settings.ClipboardSync,
                Audio = settings.Audio,
            });
            ShowResult(result);
            if (result.Succeeded)
            {
                _startedSerials.Add(device.Serial);
                if (mode == ScreenHostingMode.DockedCompanion)
                {
                    ScheduleBoundsUpdate();
                }

                var focus = await _screen.FocusAsync(device.Serial);
                if (!focus.Succeeded)
                {
                    ShowGuidance("The mirror started, but its window could not be focused. Select it from the taskbar.");
                }
                else
                {
                    ShowGuidance(
                        mode == ScreenHostingMode.ExternalWindow
                            ? "The scrcpy window has focus. Use it for touch, keyboard, and clipboard input."
                            : "The scrcpy window is docked over the pane and has focus.");
                }
            }
            else if (result.Guidance is { Length: > 0 } guidance)
            {
                ShowGuidance(guidance);
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The mirror could not start: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnStopClick(object sender, RoutedEventArgs e) => await StopAsync();

    private async Task StopAsync()
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _screen.StopAsync(serial);
            _startedSerials.Remove(serial);
            ShowResult(result);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The mirror could not stop: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnPopOutClick(object sender, RoutedEventArgs e)
    {
        if (CurrentSession is not { IsRunning: true } session)
        {
            return;
        }

        SetBusy(true);
        try
        {
            if (session.IsRecording &&
                !await _dialogs.ConfirmAsync(
                    "Recording in progress",
                    "Popping out restarts the mirror, which stops the current recording. The clip recorded so far is saved. Continue?"))
            {
                return;
            }

            var stopped = await _screen.StopAsync(session.Serial);
            _startedSerials.Remove(session.Serial);
            if (!stopped.Succeeded)
            {
                ShowResult(stopped);
                return;
            }

            await StartAsync(ScreenHostingMode.ExternalWindow);
            if (session.IsRecording && CurrentSession is { IsRunning: true })
            {
                ShowGuidance("The recording stopped when the mirror restarted. The clip is in the capture folder.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The mirror could not be popped out: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnFocusClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        ShowResult(await _screen.FocusAsync(serial));
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        if (App.Instance.Window?.AppWindow is not { } window)
        {
            return;
        }

        try
        {
            var fullscreen = window.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            window.SetPresenter(fullscreen ? AppWindowPresenterKind.Default : AppWindowPresenterKind.FullScreen);
            ShowGuidance(fullscreen
                ? "Fullscreen ended. The docked mirror follows the pane again."
                : "Fullscreen started. Press the button again to return.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            _logger.LogWarning(exception, "Fullscreen could not be toggled.");
            ShowResult(false, "Fullscreen could not be toggled on this display.", null, null);
        }
    }

    /// <summary>Starts a mirror for the selected device; used by the command palette.</summary>
    public Task StartMirrorAsync() => StartAsync(CurrentMode);

    /// <summary>Stops the active mirror; used by the command palette.</summary>
    public Task StopMirrorAsync() => StopAsync();

    /// <summary>Captures a screenshot of the selected device; used by the command palette.</summary>
    public async Task TakeScreenshotAsync()
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _screen.ScreenshotAsync(
                serial,
                _configuration.Current.Settings.Screen.ScreenshotsDirectory);
            RememberPath(result);
            ShowResult(result);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The screenshot failed: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Starts or stops a recording of the selected device; used by the command palette.</summary>
    public async Task ToggleRecordingAsync()
    {
        if (CurrentSession is { IsRecording: true })
        {
            await StopRecordingAsync();
        }
        else
        {
            await StartRecordingAsync();
        }
    }

    private async void OnScreenshotClick(object sender, RoutedEventArgs e) => await TakeScreenshotAsync();

    private async Task StartRecordingAsync()
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _screen.StartRecordingAsync(
                serial,
                _configuration.Current.Settings.Screen.RecordingsDirectory);
            RememberPath(result);
            ShowResult(result);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Recording could not start: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StopRecordingAsync()
    {
        if (SelectedSerial is not { } serial)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _screen.StopRecordingAsync(serial);
            RememberPath(result);
            ShowResult(result);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Recording could not stop: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnStartRecordingClick(object sender, RoutedEventArgs e) => await StartRecordingAsync();

    private async void OnStopRecordingClick(object sender, RoutedEventArgs e) => await StopRecordingAsync();

    private void RememberPath(ScreenOperationResult result)
    {
        if (result.Path is { Length: > 0 } path)
        {
            _lastPath = path;
        }
    }

    private void OnOpenLastPathClick(object sender, RoutedEventArgs e)
    {
        if (_lastPath is not { Length: > 0 } path || !File.Exists(path))
        {
            ShowResult(false, "The last screenshot or recording is no longer available.", null, null);
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "The captured file could not be opened.");
            ShowResult(false, $"The file could not be opened: {path}", null, null);
        }
    }

    private string CaptureFolder
    {
        get
        {
            if (_lastPath is { Length: > 0 } path &&
                File.Exists(path) &&
                Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                return folder;
            }

            var configured = _configuration.Current.Settings.Screen.ScreenshotsDirectory;
            return string.IsNullOrWhiteSpace(configured) ? _paths.ScreenshotsDirectory : configured;
        }
    }

    private void OnOpenCaptureFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = CaptureFolder;
        if (!Directory.Exists(folder))
        {
            ShowResult(
                false,
                "The capture folder does not exist yet.",
                null,
                "Take a screenshot or record the screen first.");
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "The capture folder could not be opened.");
            ShowResult(false, $"The capture folder could not be opened: {folder}", null, null);
        }
    }

    private async void OnKeyClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial ||
            (sender as FrameworkElement)?.Tag is not string tag ||
            !Enum.TryParse<DeviceControlKey>(tag, out var key))
        {
            return;
        }

        try
        {
            ShowResult(await _screen.PressKeyAsync(serial, key));
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The key could not be sent: {exception.Message}", null, null);
        }
    }

    private void UpdateState()
    {
        var session = CurrentSession;
        var running = session?.IsRunning == true;
        StartButton.Content = CurrentMode == ScreenHostingMode.ExternalWindow ? "Start external" : "Start docked";
        StartButton.IsEnabled = SelectedDevice is not null && !running && !_busy;
        StopButton.IsEnabled = running && !_busy;
        PopOutButton.IsEnabled = running && !_busy;
        FocusButton.IsEnabled = running && !_busy;
        ScreenshotButton.IsEnabled = SelectedDevice is not null && !_busy;
        RecordButton.IsEnabled = running && session?.IsRecording != true && !_busy;
        StopRecordButton.IsEnabled = session?.IsRecording == true && !_busy;
        OpenLastPathButton.IsEnabled = _lastPath is { Length: > 0 } path && File.Exists(path);
        OpenCaptureFolderButton.IsEnabled = Directory.Exists(CaptureFolder);
        CapturePathText.Text = session?.RecordingPath is { Length: > 0 } recording
            ? $"Recording: {recording}"
            : _lastPath is { Length: > 0 } last
                ? $"Last capture: {last}"
                : string.Empty;

        if (session is null)
        {
            SessionStateText.Text = "No mirror running.";
            MirrorPlaceholderTitle.Text = "No mirror running";
            MirrorPlaceholderText.Text =
                "Select a device and start a docked mirror. The scrcpy window is positioned over this pane.";
            SessionCommandText.Text = string.Empty;
            return;
        }

        SessionStateText.Text = session.State switch
        {
            ScreenSessionState.Running when session.IsRecording =>
                $"Running · pid {session.ProcessId} · recording · {Describe(session.HostingMode)}",
            ScreenSessionState.Running => $"Running · pid {session.ProcessId} · {Describe(session.HostingMode)}",
            ScreenSessionState.Starting => "Starting...",
            ScreenSessionState.Stopped => "Stopped.",
            _ => $"Failed · {session.Notice ?? "Check the Activity log."}",
        };
        if (session.Notice is { Length: > 0 } notice)
        {
            SessionStateText.Text += $" {notice}";
        }

        MirrorPlaceholderTitle.Text = session.HostingMode == ScreenHostingMode.ExternalWindow
            ? "Mirror in an external window"
            : "Mirror docked";
        MirrorPlaceholderText.Text = session.HostingMode == ScreenHostingMode.ExternalWindow
            ? "Use the separate scrcpy window for input. Press Pop out from a docked mirror to move it here."
            : "The scrcpy window covers this pane. Use it for touch, keyboard, and clipboard input.";
        SessionCommandText.Text = session.CommandLine;
    }

    private static string Describe(ScreenHostingMode mode) => mode == ScreenHostingMode.DockedCompanion
        ? "docked"
        : "external window";

    private void ShowResult(ScreenOperationResult result) =>
        ShowResult(result.Succeeded, result.Message, result.CommandLine, result.Guidance);

    private void ShowResult(bool succeeded, string message, string? commandLine, string? guidance)
    {
        ActionBar.Severity = succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ActionBar.Message = commandLine is { Length: > 0 } command
            ? $"{message} Command: {command}"
            : message;
        ActionBar.IsOpen = true;
        if (guidance is { Length: > 0 })
        {
            ShowGuidance(guidance);
        }
    }

    private void ShowGuidance(string? guidance)
    {
        GuidanceBar.Message = guidance ?? string.Empty;
        GuidanceBar.IsOpen = guidance is { Length: > 0 };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateState();
    }

    private void StopStartedSessions(bool stopExternal)
    {
        foreach (var serial in _startedSerials.ToArray())
        {
            var session = _screen.ActiveSessions.FirstOrDefault(candidate =>
                string.Equals(candidate.Serial, serial, StringComparison.Ordinal));
            if (session is null)
            {
                _startedSerials.Remove(serial);
                continue;
            }

            if (!stopExternal && session.HostingMode != ScreenHostingMode.DockedCompanion)
            {
                continue;
            }

            _startedSerials.Remove(serial);
            _ = StopSessionAsync(serial);
        }
    }

    private async Task StopSessionAsync(string serial)
    {
        try
        {
            await _screen.StopAsync(serial);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The mirror for {Serial} could not stop while leaving the page.", serial);
        }
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }
}

using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Toolchain;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DroidDeck.App.Views;

/// <summary>The landing page with a live status summary and quick actions.</summary>
public sealed partial class HomePage : Page, IRefreshablePage, IDisposable
{
    private readonly IDeviceService _devices;
    private readonly IToolchainService _toolchain;
    private readonly PluginHost _plugins;
    private readonly IDiagnosticsService _diagnostics;
    private readonly IConfigurationService _configuration;
    private readonly ILogger<HomePage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private bool _disposed;

    /// <summary>Creates the home page.</summary>
    public HomePage(
        IDeviceService devices,
        IToolchainService toolchain,
        PluginHost plugins,
        IDiagnosticsService diagnostics,
        IConfigurationService configuration,
        ILogger<HomePage> logger)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        _devices.DevicesChanged += OnDevicesChanged;
        _toolchain.SnapshotChanged += OnToolchainChanged;
        _plugins.ResultsChanged += OnPluginsChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        UpdateDevices();
        UpdateToolchain();
        UpdatePlugins();
        UpdateDiagnostics();
        var notice = _configuration.LastLoadNotice;
        LoadNoticeBar.IsOpen = notice is not null;
        LoadNoticeBar.Message = notice?.Message ?? string.Empty;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _devices.DevicesChanged -= OnDevicesChanged;
        _toolchain.SnapshotChanged -= OnToolchainChanged;
        _plugins.ResultsChanged -= OnPluginsChanged;
    }

    private void UpdateDevices()
    {
        SubtitleText.Text =
            $"Session {_diagnostics.SessionId} · Diagnostics {_diagnostics.DiagnosticsId}";

        var devices = _devices.Devices;
        DeviceSummaryText.Text = devices.Count == 0
            ? "No devices connected."
            : string.Join(
                Environment.NewLine,
                devices.Take(4).Select(device => $"{device.DisplayName} ({StateLabel(device.State)})"));

        var connection = _devices.IsAdbConnected
            ? "ADB socket tracker connected"
            : _devices.IsUsingCliFallback
                ? "ADB is using the CLI fallback"
                : "ADB is disconnected";
        AdbSummaryText.Text = string.IsNullOrWhiteSpace(_devices.LastError)
            ? $"{connection}."
            : $"{connection}. {_devices.LastError}";
    }

    private void UpdateToolchain()
    {
        var snapshot = _toolchain.CachedSnapshot;
        if (snapshot is null || snapshot.Tools.Count == 0)
        {
            ToolchainSummaryText.Text = "Toolchain check has not completed yet.";
            return;
        }

        var adb = snapshot.Get(ToolKind.Adb);
        var adbText = adb is null
            ? "adb unknown"
            : adb.IsAvailable
                ? $"adb {adb.Version ?? "available"}"
                : "adb missing";
        var available = snapshot.Tools.Count(tool => tool.IsAvailable);
        var missing = snapshot.Tools
            .Where(tool => !tool.IsAvailable)
            .Select(tool => tool.DisplayName)
            .Take(4)
            .ToArray();
        var missingText = missing.Length == 0
            ? "All tools found."
            : $"Missing: {string.Join(", ", missing)}.";
        ToolchainSummaryText.Text =
            $"{adbText} · {available}/{snapshot.Tools.Count} tools available. {missingText}";
    }

    private void UpdatePlugins()
    {
        if (_plugins.PluginsDisabled)
        {
            ExtensionsSummaryText.Text = $"Plugins are disabled ({_plugins.DisabledReason ?? "safe mode"}).";
            return;
        }

        if (!_plugins.IsInitialized)
        {
            ExtensionsSummaryText.Text = "Scanning installed plugins…";
            return;
        }

        var failed = _plugins.Results.Count(result => !result.Succeeded);
        var failureText = failed == 0 ? string.Empty : $", {failed} failed";
        ExtensionsSummaryText.Text =
            $"{_plugins.LoadedCount} plugin(s) loaded{failureText}. " +
            $"{_plugins.Registry.Commands.Count} commands available.";
    }

    private void UpdateDiagnostics()
    {
        DiagnosticsSummaryText.Text =
            $"Diagnostics {_diagnostics.DiagnosticsId}. " +
            "Copy a redacted report with devices, toolchain, and recent errors. Nothing is uploaded automatically.";
    }

    private async void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        RefreshDevicesButton.IsEnabled = false;
        try
        {
            await _devices.RefreshAsync();
            ShowAction("Device list refreshed.");
        }
        catch (Exception exception)
        {
            ReportFailure("Refreshing devices", exception);
        }
        finally
        {
            RefreshDevicesButton.IsEnabled = true;
            UpdateDevices();
        }
    }

    private async void OnCheckToolchainClick(object sender, RoutedEventArgs e)
    {
        CheckToolchainButton.IsEnabled = false;
        ToolchainSummaryText.Text = "Checking the toolchain…";
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
            UpdateToolchain();
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
            _logger.LogWarning(exception, "Diagnostics could not be copied from the home page.");
            ShowAction("Diagnostics could not be copied. Open Diagnostics to save a report.", isError: true);
        }
    }

    private void OnOpenDevicesClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("devices");

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("settings");

    private void OnOpenExtensionsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("extensions");

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

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Dispatch(UpdateDevices);

    private void OnToolchainChanged(object? sender, ToolchainSnapshot snapshot) => Dispatch(UpdateToolchain);

    private void OnPluginsChanged(object? sender, EventArgs e) => Dispatch(UpdatePlugins);

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    private static string StateLabel(DeviceState state) => state switch
    {
        DeviceState.Device => "ready",
        DeviceState.Unauthorized => "unauthorized",
        DeviceState.Authorizing => "authorizing",
        DeviceState.Offline => "offline",
        DeviceState.Connecting => "connecting",
        DeviceState.Recovery => "recovery",
        DeviceState.Sideload => "sideload",
        DeviceState.Bootloader => "bootloader",
        DeviceState.Disconnected => "disconnected",
        _ => "unknown",
    };
}

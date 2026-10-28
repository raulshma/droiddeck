using System.Globalization;
using DroidDeck.App.Services;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Toolbox;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>
/// Reads CPU, memory, frame timing, display latency, and battery state through
/// the toolbox shell so every measurement shows its exact adb command.
/// </summary>
public sealed partial class PerformancePage : Page, IRefreshablePage, IDisposable
{
    private readonly IDeviceToolboxService _toolbox;
    private readonly IDeviceService _devices;
    private readonly SessionWorkspace _workspace;
    private readonly DispatcherQueue? _dispatcher;
    private readonly UiOutputBuffer? _outputBuffer;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _disposed;

    /// <summary>Creates the performance page.</summary>
    public PerformancePage(
        IDeviceToolboxService toolbox,
        IDeviceService devices,
        SessionWorkspace workspace)
    {
        _toolbox = toolbox ?? throw new ArgumentNullException(nameof(toolbox));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _outputBuffer = _dispatcher is null ? null : new UiOutputBuffer(_dispatcher, AppendOutputText);

        InitializeComponent();
        _devices.DevicesChanged += OnDevicesChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        RefreshDevices();
        if (SelectedDevice is not null)
        {
            _ = LoadAppsAsync();
        }
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
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private DeviceInfo? SelectedDevice => DeviceCombo.SelectedItem as DeviceInfo;

    private string? SelectedSerial => SelectedDevice?.Serial;

    private string PackageName => PackageBox.Text.Trim();

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Dispatch(RefreshDevices);

    private void RefreshDevices()
    {
        var previous = SelectedSerial;
        var usable = _devices.Devices
            .Where(device => device.State == DeviceState.Device)
            .ToArray();
        DeviceCombo.ItemsSource = usable;
        DeviceCombo.SelectedItem = usable.FirstOrDefault(device =>
            string.Equals(device.Serial, previous, StringComparison.Ordinal))
            ?? usable.FirstOrDefault(device =>
                string.Equals(device.Serial, _workspace.DeviceSerial, StringComparison.Ordinal))
            ?? usable.FirstOrDefault();
        if (usable.Length == 0)
        {
            ShowGuidance(_devices.LastError
                ?? "Connect a device with USB debugging enabled to collect performance metrics.");
        }
    }

    private async void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedDevice is { } device)
        {
            _workspace.OpenDevice(device.Serial);
        }

        OutputBox.Text = string.Empty;
        MetricTitleText.Text = "No measurement yet";
        MetricSummaryText.Text = string.Empty;
        CommandText.Text = string.Empty;
        AppCombo.ItemsSource = null;
        PackageBox.Text = string.Empty;
        await LoadAppsAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await _devices.RefreshAsync();
        Refresh();
    }

    private async void OnReloadAppsClick(object sender, RoutedEventArgs e) => await LoadAppsAsync();

    private async Task LoadAppsAsync()
    {
        if (SelectedSerial is not { } serial || _busy)
        {
            return;
        }

        try
        {
            var apps = await _toolbox.ListAppsAsync(serial, AppListFilter.User);
            var previous = PackageName;
            var rows = apps
                .OrderBy(app => app.Label, StringComparer.OrdinalIgnoreCase)
                .Select(app => new AppRow(app))
                .ToArray();
            AppCombo.ItemsSource = rows;
            var selected = rows.FirstOrDefault(row =>
                string.Equals(row.App.PackageName, previous, StringComparison.Ordinal)) ?? rows.FirstOrDefault();
            AppCombo.SelectedItem = selected;
            if (previous.Length == 0 && selected is not null)
            {
                PackageBox.Text = selected.App.PackageName;
            }

            AppCombo.IsEnabled = rows.Length > 0;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Applications could not be listed: {exception.Message}", null);
        }
    }

    private void OnAppSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppCombo.SelectedItem is AppRow row)
        {
            PackageBox.Text = row.App.PackageName;
        }
    }

    private async void OnCpuClick(object sender, RoutedEventArgs e) =>
        await RunMetricAsync("CPU", "dumpsys cpuinfo", SummarizeCpu);

    private async void OnMemoryClick(object sender, RoutedEventArgs e)
    {
        var package = PackageName;
        var command = package.Length > 0
            ? $"dumpsys meminfo {package}"
            : "dumpsys meminfo";
        await RunMetricAsync("Memory", command, SummarizeMemory);
    }

    private async void OnFrameStatsClick(object sender, RoutedEventArgs e)
    {
        var package = PackageName;
        if (package.Length == 0)
        {
            ShowResult(
                false,
                "Enter an application package to read frame statistics.",
                "Choose an application above, or type its package name.");
            return;
        }

        await RunMetricAsync("Frame stats", $"dumpsys gfxinfo {package}", SummarizeFrameStats);
    }

    private async void OnLatencyClick(object sender, RoutedEventArgs e) =>
        await RunMetricAsync("Display latency", "dumpsys SurfaceFlinger --latency", SummarizeLatency);

    private async void OnBatteryClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial || _busy)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        OutputBox.Text = string.Empty;
        try
        {
            var battery = await _toolbox.RunShellAsync(serial, "dumpsys battery", cancellationToken: token);
            var thermal = await _toolbox.RunShellAsync(serial, "dumpsys thermalservice", cancellationToken: token);
            var output = battery.Output + Environment.NewLine + thermal.Output;
            SetOutput(output);
            CommandText.Text = $"{battery.CommandLine}{Environment.NewLine}{thermal.CommandLine}";
            MetricTitleText.Text = "Battery and thermal";
            MetricSummaryText.Text = SummarizeBattery(battery.Output);
            ShowResult(
                battery.Succeeded && thermal.Succeeded,
                battery.Succeeded && thermal.Succeeded
                    ? "Battery and thermal state read."
                    : "One of the battery or thermal commands failed.",
                null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ShowResult(false, "The measurement was cancelled.", null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The measurement failed: {exception.Message}", null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunMetricAsync(
        string title,
        string command,
        Func<string, string> summarize)
    {
        if (SelectedSerial is not { } serial || _busy)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        _outputBuffer?.Reset();
        OutputBox.Text = string.Empty;
        CommandText.Text = command;
        MetricTitleText.Text = title;
        MetricSummaryText.Text = "Running...";
        try
        {
            var progress = new ActionProgress<ProcessOutputLine>(line => _outputBuffer?.Append(line.Text));
            var result = await _toolbox.RunShellAsync(
                serial,
                command,
                output: progress,
                cancellationToken: token);
            _outputBuffer?.Reset();
            SetOutput(result.Output);
            CommandText.Text = result.CommandLine ?? command;
            MetricSummaryText.Text = result.Succeeded
                ? summarize(result.Output)
                : result.Message;
            ShowResult(
                result.Succeeded,
                result.Succeeded ? $"{title} measured." : result.Message,
                null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            MetricSummaryText.Text = "Cancelled.";
            ShowResult(false, "The measurement was cancelled.", null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MetricSummaryText.Text = exception.Message;
            ShowResult(false, $"The measurement failed: {exception.Message}", null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AppendOutputText(string text) => OutputBox.Text = Trim(OutputBox.Text + text);

    private void SetOutput(string output) => OutputBox.Text = Trim(output);

    private static string Trim(string text) =>
        text.Length <= 300_000 ? text : text[^300_000..];

    private static string SummarizeCpu(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var total = lines.FirstOrDefault(line =>
            line.Contains("TOTAL:", StringComparison.OrdinalIgnoreCase));
        var package = lines.FirstOrDefault(line =>
            line.Contains("com.", StringComparison.OrdinalIgnoreCase));
        var parts = new List<string>();
        if (total is not null)
        {
            parts.Add(total);
        }

        if (package is not null && !string.Equals(package, total, StringComparison.Ordinal))
        {
            parts.Add(package);
        }

        return parts.Count == 0
            ? "CPU info loaded. Expand the raw output for per-process details."
            : string.Join(Environment.NewLine, parts);
    }

    private static string SummarizeMemory(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var interesting = lines
            .Where(line =>
                line.StartsWith("TOTAL", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Java Heap", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Native Heap", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Graphics", StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToArray();
        return interesting.Length == 0
            ? "Memory info loaded. Expand the raw output for the full table."
            : string.Join(Environment.NewLine, interesting);
    }

    private static string SummarizeFrameStats(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var interesting = lines
            .Where(line =>
                line.Contains("Total frames rendered", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Janky frames", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("percentile", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Number Missed Vsync", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Number Slow UI thread", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Number Slow bitmap uploads", StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToArray();
        return interesting.Length == 0
            ? "Frame statistics loaded. Expand the raw output for histogram details."
            : string.Join(Environment.NewLine, interesting);
    }

    private static string SummarizeLatency(string output)
    {
        var first = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (first is null || !long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanoseconds) ||
            nanoseconds <= 0)
        {
            return "Display latency data loaded. The first line is the refresh period in nanoseconds.";
        }

        var hertz = 1_000_000_000d / nanoseconds;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Refresh period {nanoseconds} ns · about {hertz:F1} Hz");
    }

    private static string SummarizeBattery(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var interesting = lines
            .Where(line =>
                line.StartsWith("level:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("status:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("temperature:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("health:", StringComparison.OrdinalIgnoreCase))
            .Take(6)
            .ToArray();
        return interesting.Length == 0
            ? "Battery state loaded."
            : string.Join(Environment.NewLine, interesting);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
    }

    private void ShowResult(bool succeeded, string message, string? guidance)
    {
        ActionBar.Severity = succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ActionBar.Message = message;
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

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    /// <summary>One installed application shaped for the picker.</summary>
    public sealed class AppRow
    {
        /// <summary>Creates a row.</summary>
        public AppRow(ToolboxApp app)
        {
            App = app ?? throw new ArgumentNullException(nameof(app));
        }

        /// <summary>Gets the application.</summary>
        public ToolboxApp App { get; }

        /// <summary>Gets the picker label.</summary>
        public string Display => $"{App.Label} ({App.PackageName})";
    }
}

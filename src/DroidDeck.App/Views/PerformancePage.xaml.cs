using System.Globalization;
using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Performance;
using DroidDeck.Core.Processes;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Toolbox;
using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DroidDeck.App.Views;

/// <summary>
/// Samples launch time, frame timing, CPU, memory, and battery for a running
/// application. Live values feed lightweight charts, a session summary, and CSV
/// export; one-shot diagnostics remain available with their exact adb commands.
/// </summary>
public sealed partial class PerformancePage : Page, IRefreshablePage, IDisposable
{
    private const int ChartPoints = 120;

    private readonly IDeviceToolboxService _toolbox;
    private readonly IDeviceService _devices;
    private readonly SessionWorkspace _workspace;
    private readonly IConfigurationService _configuration;
    private readonly IRunSessionService _sessions;
    private readonly PerformanceSampler _sampler;
    private readonly RendererPane _chartRenderer;
    private readonly IShellDialogs _dialogs;
    private readonly DispatcherQueue? _dispatcher;
    private readonly UiOutputBuffer? _outputBuffer;
    private readonly List<PerformanceSample> _samples = [];
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _sampleCts;
    private bool _busy;
    private bool _sampling;
    private bool _initializing;
    private bool _disposed;

    /// <summary>Creates the performance page.</summary>
    public PerformancePage(
        IDeviceToolboxService toolbox,
        IDeviceService devices,
        SessionWorkspace workspace,
        IConfigurationService configuration,
        IRunSessionService sessions,
        IRendererHost renderers,
        IServiceProvider services,
        IShellDialogs dialogs)
    {
        _toolbox = toolbox ?? throw new ArgumentNullException(nameof(toolbox));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sampler = new PerformanceSampler(toolbox);
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _outputBuffer = _dispatcher is null ? null : new UiOutputBuffer(_dispatcher, AppendOutputText);

        InitializeComponent();
        _chartRenderer = new RendererPane(renderers, services);
        ChartRendererHost.Content = _chartRenderer;
        _chartRenderer.Render(RendererKind.Chart, BuildChartData([]));
        _initializing = true;
        IntervalBox.Value = _configuration.Current.Settings.Performance.EffectiveSampleIntervalMs;
        _initializing = false;

        _devices.DevicesChanged += OnDevicesChanged;
        _sessions.SessionChanged += OnSessionChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        RefreshDevices();
        UpdateLaunchTime(LatestLaunchSession());
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
        _sessions.SessionChanged -= OnSessionChanged;
        _cts?.Cancel();
        _cts?.Dispose();
        _sampleCts?.Cancel();
        _sampleCts?.Dispose();
        _chartRenderer.Dispose();
    }

    private DeviceInfo? SelectedDevice => DeviceCombo.SelectedItem as DeviceInfo;

    private string? SelectedSerial => SelectedDevice?.Serial;

    private string PackageName => PackageBox.Text.Trim();

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Dispatch(RefreshDevices);

    private void OnSessionChanged(object? sender, RunSessionChangedEventArgs e) =>
        Dispatch(() =>
        {
            if (e.Session.LaunchTime is not null)
            {
                UpdateLaunchTime(e.Session);
                if (PackageBox.Text.Length == 0 && !string.IsNullOrWhiteSpace(e.Session.PackageName))
                {
                    PackageBox.Text = e.Session.PackageName;
                }
            }
        });

    private RunSession? LatestLaunchSession()
    {
        var serial = SelectedSerial;
        var package = PackageName;
        return _sessions.Sessions.FirstOrDefault(session =>
                   session.LaunchTime.HasValue &&
                   (serial is null || string.Equals(session.DeviceSerial, serial, StringComparison.Ordinal)) &&
                   (package.Length == 0 ||
                    string.Equals(session.PackageName, package, StringComparison.OrdinalIgnoreCase)))
               ?? _sessions.Sessions.FirstOrDefault(session =>
                   session.LaunchTime.HasValue &&
                   (serial is null || string.Equals(session.DeviceSerial, serial, StringComparison.Ordinal)));
    }

    private void UpdateLaunchTime(RunSession? session)
    {
        if (session?.LaunchTime is not { } launchTime)
        {
            LaunchTimeText.Text = "No run launch time yet. Start a run to capture it.";
            return;
        }

        var device = string.IsNullOrWhiteSpace(session.DeviceName)
            ? session.DeviceSerial
            : session.DeviceName;
        LaunchTimeText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Last launch: {launchTime.TotalMilliseconds:0} ms on {device} ({session.ProjectName}).");
    }

    private void RefreshDevices()
    {
        var target = DeviceTargetSelection.Resolve(
            _devices.Devices,
            SelectedSerial,
            _workspace.DeviceSerial);
        DeviceCombo.ItemsSource = target.Usable;
        DeviceCombo.SelectedItem = target.Selected;
        if (target.Usable.Count == 0)
        {
            ShowGuidance(_devices.LastError
                ?? "Connect a device with USB debugging enabled to collect performance metrics.");
        }
    }

    private async void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopSampling();
        ResetSamples();
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
        UpdateLaunchTime(LatestLaunchSession());
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
            UpdateLaunchTime(LatestLaunchSession());
        }
    }

    private async void OnIntervalChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing || double.IsNaN(args.NewValue))
        {
            return;
        }

        var interval = PerformanceSettings.ClampSampleInterval((int)Math.Round(args.NewValue));
        if (interval == _configuration.Current.Settings.Performance.SampleIntervalMs)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.PerformanceSampleIntervalMs, interval);
            ShowResult(
                true,
                _sampling
                    ? $"The sampling interval is {interval} ms. Restart sampling to apply it."
                    : $"The sampling interval is {interval} ms.",
                _sampling ? "Stop and start sampling to use the new interval." : null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The sampling interval could not be saved: {exception.Message}", null);
        }
    }

    private async void OnStartSamplingClick(object sender, RoutedEventArgs e)
    {
        if (_sampling || SelectedSerial is not { } serial)
        {
            return;
        }

        var package = PackageName;
        if (package.Length == 0)
        {
            ShowResult(
                false,
                "Enter an application package to start sampling.",
                "Choose an application above, or type its package name.");
            return;
        }

        ResetSamples();
        _sampleCts?.Dispose();
        _sampleCts = new CancellationTokenSource();
        var token = _sampleCts.Token;
        var settings = _configuration.Current.Settings.Performance;
        var interval = TimeSpan.FromMilliseconds(settings.EffectiveSampleIntervalMs);
        var maxSamples = settings.EffectiveMaxSamples;
        _sampling = true;
        SetSamplingUi(true);
        SetBusy(true);
        ShowResult(true, $"Sampling {package} every {interval.TotalMilliseconds:0} ms.", null);

        try
        {
            await _sampler.RunAsync(
                serial,
                package,
                interval,
                sample =>
                {
                    Dispatch(() => AddSample(sample, maxSamples));
                    return Task.CompletedTask;
                },
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"Sampling stopped: {exception.Message}", null);
        }
        finally
        {
            _sampling = false;
            SetSamplingUi(false);
            SetBusy(false);
            UpdateSummary();
        }
    }

    private void OnStopSamplingClick(object sender, RoutedEventArgs e) => StopSampling();

    private void StopSampling()
    {
        if (!_sampling)
        {
            return;
        }

        _sampleCts?.Cancel();
    }

    private void AddSample(PerformanceSample sample, int maxSamples)
    {
        if (_disposed)
        {
            return;
        }

        _samples.Add(sample);
        while (_samples.Count > maxSamples)
        {
            _samples.RemoveAt(0);
        }

        if (sample.Warnings.Count > 0)
        {
            WarningText.Text = string.Join(Environment.NewLine, sample.Warnings.Distinct(StringComparer.Ordinal));
            WarningText.Visibility = Visibility.Visible;
        }

        UpdateCharts();
        StatusText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{_samples.Count} sample(s). Last point {sample.Timestamp.ToLocalTime():HH:mm:ss}.");
        ExportButton.IsEnabled = _samples.Count > 0;
        ClearButton.IsEnabled = _samples.Count > 0;
    }

    private void UpdateCharts()
    {
        var visible = _samples.Count <= ChartPoints
            ? _samples
            : _samples.GetRange(_samples.Count - ChartPoints, ChartPoints);

        CpuChart.SetValues(visible.Select(sample => sample.CpuPercent).ToArray(), 0, 100);
        var memoryMaximum = visible
            .Where(sample => sample.MemoryMegabytes.HasValue)
            .Select(sample => sample.MemoryMegabytes!.Value)
            .DefaultIfEmpty(1)
            .Max();
        MemoryChart.SetValues(
            visible.Select(sample => sample.MemoryMegabytes).ToArray(),
            0,
            Math.Max(1, Math.Ceiling(memoryMaximum * 1.1)));
        FpsChart.SetValues(visible.Select(sample => sample.Fps).ToArray(), 0, 60);
        JankChart.SetValues(visible.Select(sample => sample.JankPercent).ToArray(), 0, 100);
        BatteryChart.SetValues(
            visible.Select(sample => sample.BatteryPercent is { } level ? level : (double?)null).ToArray(),
            0,
            100);

        CpuValueText.Text = FormatPercent(visible.LastOrDefault()?.CpuPercent);
        MemoryValueText.Text = visible.LastOrDefault()?.MemoryMegabytes is { } memory
            ? string.Create(CultureInfo.InvariantCulture, $"{memory:0.0} MB")
            : "--";
        FpsValueText.Text = visible.LastOrDefault()?.Fps is { } fps
            ? string.Create(CultureInfo.InvariantCulture, $"{fps:0.0}")
            : "--";
        JankValueText.Text = FormatPercent(visible.LastOrDefault()?.JankPercent);
        BatteryValueText.Text = visible.LastOrDefault()?.BatteryPercent is { } battery
            ? string.Create(CultureInfo.InvariantCulture, $"{battery}%")
            : "--";

        _chartRenderer.Render(RendererKind.Chart, BuildChartData(visible));
    }

    private static ChartData BuildChartData(IReadOnlyList<PerformanceSample> samples) => new(
        "Live CPU and jank",
        [
            new ChartSeries("CPU %", samples.Select(sample => sample.CpuPercent ?? 0).ToArray()),
            new ChartSeries("Jank %", samples.Select(sample => sample.JankPercent ?? 0).ToArray()),
        ],
        Maximum: 100,
        Unit: "%");

    private void UpdateSummary()
    {
        var summary = PerformanceSummary.FromSamples(_samples);
        if (summary.SampleCount == 0)
        {
            SummaryText.Text = "Start sampling to build a summary.";
            return;
        }

        var lines = new List<string>
        {
            $"Samples: {summary.SampleCount} over {FormatDuration(summary.Duration)}",
            FormatStat("CPU", summary.Cpu, "%", "0.0"),
            FormatStat("Memory", summary.Memory, " MB", "0.0"),
            FormatStat("FPS", summary.Fps, string.Empty, "0.0"),
            FormatStat("Jank", summary.Jank, "%", "0.0"),
            FormatStat("Battery", summary.Battery, "%", "0"),
        };
        var last = _samples[^1];
        if (last.BatteryTemperatureCelsius is { } temperature)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Battery temperature: {temperature:0.0} C"));
        }

        if (LatestLaunchSession() is { LaunchTime: { } launchTime } launch)
        {
            var device = string.IsNullOrWhiteSpace(launch.DeviceName)
                ? launch.DeviceSerial
                : launch.DeviceName;
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Launch time: {launchTime.TotalMilliseconds:0} ms ({device}, {launch.ProjectName})"));
        }

        SummaryText.Text = string.Join(Environment.NewLine, lines);
    }

    private static string FormatStat(string label, PerformanceStat stat, string unit, string format)
    {
        if (stat.Count == 0)
        {
            return $"{label}: no data";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{label}: avg {stat.Average!.Value.ToString(format, CultureInfo.InvariantCulture)}{unit} " +
            $"(min {stat.Min!.Value.ToString(format, CultureInfo.InvariantCulture)}, " +
            $"max {stat.Max!.Value.ToString(format, CultureInfo.InvariantCulture)}; n={stat.Count})");
    }

    private static string FormatPercent(double? value) =>
        value is { } present
            ? string.Create(CultureInfo.InvariantCulture, $"{present:0.0}%")
            : "--";

    private static string FormatDuration(TimeSpan duration) =>
        duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private void ResetSamples()
    {
        _samples.Clear();
        CpuChart.Clear();
        MemoryChart.Clear();
        FpsChart.Clear();
        JankChart.Clear();
        BatteryChart.Clear();
        CpuValueText.Text = "--";
        MemoryValueText.Text = "--";
        FpsValueText.Text = "--";
        JankValueText.Text = "--";
        BatteryValueText.Text = "--";
        WarningText.Text = string.Empty;
        WarningText.Visibility = Visibility.Collapsed;
        SummaryText.Text = "Start sampling to build a summary.";
        StatusText.Text = "No samples yet.";
        ExportButton.IsEnabled = false;
        ClearButton.IsEnabled = false;
        _chartRenderer.Render(RendererKind.Chart, BuildChartData([]));
    }

    private void OnClearSamplesClick(object sender, RoutedEventArgs e)
    {
        StopSampling();
        ResetSamples();
    }

    private async void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_samples.Count == 0)
        {
            ShowResult(false, "There are no samples to export.", "Start sampling first.");
            return;
        }

        try
        {
            var csv = PerformanceCsvExporter.Export(_samples);
            var serial = SelectedSerial ?? "device";
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"performance-{Sanitize(serial)}-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultFileExtension = ".csv",
            };
            picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
            _dialogs.InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await File.WriteAllTextAsync(file.Path, csv);
            ShowResult(true, $"Exported {_samples.Count} samples to {file.Path}.", null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or COMException)
        {
            ShowResult(false, $"The samples could not be exported: {exception.Message}", null);
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
            $"Refresh period {nanoseconds} ns - about {hertz:F1} Hz");
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

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopSampling();
    }

    private void SetSamplingUi(bool sampling)
    {
        StartButton.IsEnabled = !sampling;
        StopButton.IsEnabled = sampling;
        DeviceCombo.IsEnabled = !sampling;
        AppCombo.IsEnabled = !sampling && AppCombo.Items.Count > 0;
        PackageBox.IsEnabled = !sampling;
        IntervalBox.IsEnabled = !sampling;
    }

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

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '-' : character));
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

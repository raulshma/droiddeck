using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.Core;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DroidDeck.App.Views;

/// <summary>Shows built-in extensions, installed plugins, and plugin health.</summary>
public sealed partial class ExtensionsPage : Page, IRefreshablePage, IDisposable
{
    private readonly PluginHost _plugins;
    private readonly IDiagnosticsService _diagnostics;
    private readonly AppPaths _paths;
    private readonly ILogger<ExtensionsPage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private readonly ObservableCollection<PluginRow> _rows = [];
    private bool _disposed;

    /// <summary>Creates the extensions page.</summary>
    public ExtensionsPage(
        PluginHost plugins,
        IDiagnosticsService diagnostics,
        AppPaths paths,
        ILogger<ExtensionsPage> logger)
    {
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        PluginList.ItemsSource = _rows;
        _plugins.ResultsChanged += OnResultsChanged;
        _plugins.Registry.Changed += OnRegistryChanged;
        _plugins.Health.HealthChanged += OnHealthChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        UpdateSummary();
        UpdateCommandRows();
        UpdateRows();
        RestartNotice.IsOpen = _plugins.RestartRequired;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _plugins.ResultsChanged -= OnResultsChanged;
        _plugins.Registry.Changed -= OnRegistryChanged;
        _plugins.Health.HealthChanged -= OnHealthChanged;
    }

    private void UpdateSummary()
    {
        if (_plugins.PluginsDisabled)
        {
            PluginsStatusText.Text =
                $"Plugins are disabled for this launch ({_plugins.DisabledReason ?? "safe mode"}). " +
                "Built-in commands are still available.";
        }
        else if (!_plugins.IsInitialized)
        {
            PluginsStatusText.Text = "The plugin scan is still running.";
        }
        else
        {
            var failed = _plugins.Statuses.Count(status =>
                status.State is PluginState.DisabledByError or PluginState.Incompatible);
            var unhealthy = _plugins.Health.Snapshot().Count(health => !health.IsHealthy);
            PluginsStatusText.Text = failed == 0
                ? unhealthy == 0
                    ? $"{_plugins.LoadedCount} plugin(s) loaded. No plugin issues were reported."
                    : $"{_plugins.LoadedCount} plugin(s) loaded, {unhealthy} unhealthy. See the health details below."
                : $"{_plugins.LoadedCount} plugin(s) loaded, {failed} failed. Review the reasons below and the Activity log.";
        }

        var registry = _plugins.Registry;
        BuiltInSummaryText.Text =
            $"{registry.Commands.Count} commands · {registry.ProjectDetectors.Count} project detectors · " +
            $"{registry.DeviceActions.Count} device actions · {registry.RunSteps.Count} run steps available.";
    }

    private void UpdateRows()
    {
        _rows.Clear();
        foreach (var status in _plugins.Statuses)
        {
            var health = _plugins.Health.Get(status.Id);
            _rows.Add(new PluginRow(status, health));
        }

        EmptyText.Text = _plugins.PluginsDisabled
            ? "Plugin loading is disabled for this launch."
            : "No third-party plugins are installed.";
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCommandRows()
    {
        var rows = _plugins.Registry.Commands.Select(CommandRow.From).ToArray();
        CommandList.ItemsSource = rows;
        NoCommandsText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnResultsChanged(object? sender, EventArgs e) => Dispatch(Refresh);

    private void OnRegistryChanged(object? sender, EventArgs e) => Dispatch(Refresh);

    private void OnHealthChanged(object? sender, EventArgs e) => Dispatch(Refresh);

    private async void OnRunCommandClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CommandRow row)
        {
            return;
        }

        try
        {
            await row.Command.ExecuteAsync(CancellationToken.None);
            ShowAction($"Ran \"{row.Title}\".");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The command {Command} failed.", row.Command.Id);
            ShowAction(
                $"\"{row.Title}\" failed: {exception.Message} (Diagnostics ID {_diagnostics.DiagnosticsId}).",
                isError: true);
        }
    }

    private async void OnEnablePluginClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginRow { CanEnable: true } row)
        {
            return;
        }

        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        try
        {
            var enabled = await _plugins.EnableAsync(row.PluginId);
            ShowAction(enabled
                ? $"Enabled {row.Title}. Restart DroidDeck to load it."
                : $"{row.Title} is already enabled.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The plugin {PluginId} could not be enabled.", row.PluginId);
            ShowAction(
                $"Enabling {row.Title} failed: {exception.Message} (Diagnostics ID {_diagnostics.DiagnosticsId}).",
                isError: true);
        }
        finally
        {
            if (sender is Button current)
            {
                current.IsEnabled = true;
            }
        }
    }

    private async void OnDisablePluginClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginRow { CanDisable: true } row)
        {
            return;
        }

        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        try
        {
            var disabled = await _plugins.DisableAsync(row.PluginId);
            ShowAction(disabled
                ? $"Disabled {row.Title}. Its extensions were removed; restart DroidDeck to unload it completely."
                : $"{row.Title} is not loaded.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The plugin {PluginId} could not be disabled.", row.PluginId);
            ShowAction(
                $"Disabling {row.Title} failed: {exception.Message} (Diagnostics ID {_diagnostics.DiagnosticsId}).",
                isError: true);
        }
        finally
        {
            if (sender is Button current)
            {
                current.IsEnabled = true;
            }
        }
    }

    private void OnOpenPluginFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginRow row)
        {
            return;
        }

        try
        {
            var dataDirectory = Path.Combine(row.InstallPath, "data");
            var target = Directory.Exists(dataDirectory) ? dataDirectory : row.InstallPath;
            Directory.CreateDirectory(target);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{target}\"",
                UseShellExecute = true,
            });
            ShowAction($"Opened the data folder for {row.Title}.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "The plugin folder could not be opened.");
            ShowAction("The plugin folder could not be opened.", isError: true);
        }
    }

    private void OnViewPluginLogsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginRow row)
        {
            return;
        }

        try
        {
            _paths.EnsureCreated();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.LogsDirectory}\"",
                UseShellExecute = true,
            });
            ShowAction(
                $"Opened the log folder. Entries for {row.Title} use the category \"DroidDeck.Plugins.{row.PluginId}\".");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "The log folder could not be opened from extensions.");
            ShowAction("The log folder could not be opened.", isError: true);
        }
    }

    private void OnOpenPluginsFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _paths.EnsureCreated();
            Directory.CreateDirectory(_paths.PluginsDirectory);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_paths.PluginsDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "The plugins folder could not be opened.");
            ShowAction("The plugins folder could not be opened.", isError: true);
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
            _logger.LogWarning(exception, "Diagnostics could not be copied from extensions.");
            ShowAction("Diagnostics could not be copied. Open Diagnostics to save a report.", isError: true);
        }
    }

    private void ShowAction(string message, bool isError = false)
    {
        ActionBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
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

    /// <summary>One registered command shaped for the extensions list.</summary>
    public sealed class CommandRow
    {
        private CommandRow(PluginExtension<ICommand> extension)
        {
            Command = extension.Value;
            Title = extension.Value.Title;
            Subtitle = string.IsNullOrWhiteSpace(extension.Value.Description)
                ? $"{extension.Value.Id} · {extension.PluginId}"
                : $"{extension.Value.Description} · {extension.Value.Id} · {extension.PluginId}";
            RunId = $"RunCommand_{extension.Value.Id}";
        }

        /// <summary>Gets the registered command.</summary>
        public ICommand Command { get; }

        /// <summary>Gets the command title.</summary>
        public string Title { get; }

        /// <summary>Gets the description and owning plugin.</summary>
        public string Subtitle { get; }

        /// <summary>Gets the run button automation id.</summary>
        public string RunId { get; }

        /// <summary>Creates a row from a registered command.</summary>
        public static CommandRow From(PluginExtension<ICommand> extension)
        {
            ArgumentNullException.ThrowIfNull(extension);
            return new CommandRow(extension);
        }
    }

    /// <summary>One installed plugin shaped for the extensions list.</summary>
    public sealed class PluginRow
    {
        /// <summary>Creates a row from a plugin status and its health.</summary>
        public PluginRow(PluginStatus status, PluginHealthSnapshot? health)
        {
            ArgumentNullException.ThrowIfNull(status);
            PluginId = status.Id;
            InstallPath = status.InstallPath;
            State = status.State;
            EnableId = $"EnablePlugin_{status.Id}";
            DisableId = $"DisablePlugin_{status.Id}";
            FolderId = $"PluginFolder_{status.Id}";
            LogsId = $"PluginLogs_{status.Id}";
            Title = status.Name;
            Subtitle = $"{status.Id} · version {status.Version} · API {status.ApiVersion}";
            Status = status.StatusLabel;
            StatusBrush = ResolveBrush(status.State switch
            {
                PluginState.Loaded => "SystemFillColorSuccessBrush",
                PluginState.Disabled or PluginState.EnabledPendingRestart => "SystemFillColorCautionBrush",
                _ => "SystemFillColorCriticalBrush",
            });

            Reason = status.Reason ??
                string.Join(Environment.NewLine, status.Issues.Select(issue => issue.ToString()));
            HasReason = Reason.Length > 0;

            HasHealth = status.State == PluginState.Loaded && health is not null;
            if (!HasHealth || health is null)
            {
                Health = string.Empty;
                HealthBrush = ResolveBrush("SystemFillColorSuccessBrush");
            }
            else if (health.IsHealthy)
            {
                var duration = status.LoadDuration.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
                Health = $"Healthy · loaded in {duration} ms · {health.FailureCount} error(s)";
                HealthBrush = ResolveBrush("SystemFillColorSuccessBrush");
            }
            else
            {
                var timeout = health.LastFailureTimedOut ? " (timeout)" : string.Empty;
                Health =
                    $"Unhealthy · {health.ConsecutiveFailures} consecutive failure(s){timeout}: {health.LastError}";
                HealthBrush = ResolveBrush("SystemFillColorCriticalBrush");
            }
        }

        /// <summary>Gets the plugin identifier.</summary>
        public string PluginId { get; }

        /// <summary>Gets the plugin installation directory.</summary>
        public string InstallPath { get; }

        /// <summary>Gets the plugin lifecycle state.</summary>
        public PluginState State { get; }

        /// <summary>Gets whether the plugin can be enabled.</summary>
        public bool CanEnable => State is PluginState.Disabled or PluginState.DisabledByError or PluginState.Incompatible;

        /// <summary>Gets whether the plugin can be disabled now.</summary>
        public bool CanDisable => State is PluginState.Loaded or PluginState.EnabledPendingRestart;

        /// <summary>Gets the enable button automation id.</summary>
        public string EnableId { get; }

        /// <summary>Gets the disable button automation id.</summary>
        public string DisableId { get; }

        /// <summary>Gets the open-folder button automation id.</summary>
        public string FolderId { get; }

        /// <summary>Gets the view-logs button automation id.</summary>
        public string LogsId { get; }

        /// <summary>Gets the plugin name or failure title.</summary>
        public string Title { get; }

        /// <summary>Gets the plugin identity line.</summary>
        public string Subtitle { get; }

        /// <summary>Gets the status label.</summary>
        public string Status { get; }

        /// <summary>Gets the status brush.</summary>
        public Brush StatusBrush { get; }

        /// <summary>Gets the rejection or failure reason, when one exists.</summary>
        public string Reason { get; }

        /// <summary>Gets whether the reason should be shown.</summary>
        public bool HasReason { get; }

        /// <summary>Gets the health summary.</summary>
        public string Health { get; }

        /// <summary>Gets whether the health summary should be shown.</summary>
        public bool HasHealth { get; }

        /// <summary>Gets the health brush.</summary>
        public Brush HealthBrush { get; }

        private static Brush ResolveBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}

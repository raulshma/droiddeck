using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.App.ViewModels;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Toolchain;
using DroidDeck.Plugins;
using DroidDeck.Plugin.UI.Kit;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DroidDeck.App.Views;

/// <summary>
/// The daily landing page. Every region loads on its own so a slow or failed
/// region shows its own skeleton or label instead of blocking the page.
/// </summary>
public sealed partial class HomePage : Page, IRefreshablePage, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleEnvironment = TimeSpan.FromHours(24);
    private const int RecentProjectCount = 5;

    private readonly IDeviceService _devices;
    private readonly IToolchainService _toolchain;
    private readonly PluginHost _plugins;
    private readonly PluginUiFaultReporter _faultReporter;
    private readonly IDiagnosticsService _diagnostics;
    private readonly IConfigurationService _configuration;
    private readonly IProjectService _projects;
    private readonly IRunSessionService _runs;
    private readonly QuickRunMemory _quickRun;
    private readonly SessionWorkspace _workspace;
    private readonly ILogger<HomePage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private readonly DispatcherTimer _ticker;
    private readonly ObservableCollection<ActiveSessionRow> _sessionRows = [];
    private readonly BulkObservableCollection<DeviceRowViewModel> _deviceRows = [];
    private readonly BulkObservableCollection<RecentProjectRow> _projectRows = [];
    private readonly BulkObservableCollection<EnvironmentToolRow> _environmentRows = [];
    private readonly Dictionary<string, HomeWidgetMount> _pluginWidgets = new(StringComparer.Ordinal);
    private QuickRunSelection? _selection;
    private string? _devicesProblem;
    private string? _projectsProblem;
    private bool _devicesLoaded;
    private bool _projectsLoaded;
    private bool _environmentChecked;
    private bool _syncingSelection;
    private bool _startingRun;
    private bool _disposed;

    /// <summary>Creates the home page.</summary>
    public HomePage(
        IDeviceService devices,
        IToolchainService toolchain,
        PluginHost plugins,
        PluginUiFaultReporter faultReporter,
        IDiagnosticsService diagnostics,
        IConfigurationService configuration,
        IProjectService projects,
        IRunSessionService runs,
        QuickRunMemory quickRun,
        SessionWorkspace workspace,
        ILogger<HomePage> logger)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _faultReporter = faultReporter ?? throw new ArgumentNullException(nameof(faultReporter));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _quickRun = quickRun ?? throw new ArgumentNullException(nameof(quickRun));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _ticker = new DispatcherTimer { Interval = TickInterval };
        _ticker.Tick += OnTick;

        InitializeComponent();
        SessionList.ItemsSource = _sessionRows;
        DeviceList.ItemsSource = _deviceRows;
        ProjectList.ItemsSource = _projectRows;
        EnvironmentList.ItemsSource = _environmentRows;

        _devices.DevicesChanged += OnDevicesChanged;
        _devices.DeviceUpdated += OnDeviceUpdated;
        _toolchain.SnapshotChanged += OnToolchainChanged;
        _plugins.ResultsChanged += OnPluginsChanged;
        _projects.ProjectsChanged += OnProjectsChanged;
        _runs.SessionChanged += OnSessionChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh() => Reload(force: false);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ticker.Stop();
        _ticker.Tick -= OnTick;
        _devices.DevicesChanged -= OnDevicesChanged;
        _devices.DeviceUpdated -= OnDeviceUpdated;
        _toolchain.SnapshotChanged -= OnToolchainChanged;
        _plugins.ResultsChanged -= OnPluginsChanged;
        _projects.ProjectsChanged -= OnProjectsChanged;
        _runs.SessionChanged -= OnSessionChanged;
        foreach (var widget in _pluginWidgets.Values)
        {
            widget.Boundary.Dispose();
        }

        _pluginWidgets.Clear();
    }

    private void Reload(bool force)
    {
        UpdateSubtitle();
        UpdateNotice();
        UpdateSessions();
        UpdatePlugins();

        if (force || !_devicesLoaded)
        {
            _ = LoadDevicesRegionAsync();
        }
        else
        {
            UpdateDevices();
        }

        if (force || !_projectsLoaded)
        {
            _ = LoadProjectsRegionAsync();
        }
        else
        {
            UpdateProjects();
            UpdateQuickRun();
        }

        if (_toolchain.CachedSnapshot is null)
        {
            _ = LoadEnvironmentRegionAsync();
        }
        else
        {
            UpdateEnvironment();
        }
    }

    private void UpdateSubtitle() =>
        SubtitleText.Text = $"Session {_diagnostics.SessionId} · Diagnostics {_diagnostics.DiagnosticsId}";

    private void UpdateNotice()
    {
        var notice = _configuration.LastLoadNotice;
        LoadNoticeBar.IsOpen = notice is not null;
        LoadNoticeBar.Message = notice?.Message ?? string.Empty;
    }

    private async Task LoadDevicesRegionAsync()
    {
        ShowLoading(DevicesSkeleton, DeviceList, DevicesEmptyText, DevicesErrorText);
        try
        {
            await _devices.RefreshAsync().ConfigureAwait(false);
            _devicesProblem = null;
        }
        catch (Exception exception)
        {
            LogRegionFailure("device list", exception);
            _devicesProblem = exception.Message;
        }

        _devicesLoaded = true;
        Dispatch(UpdateDevices);
    }

    private void UpdateDevices()
    {
        _deviceRows.ResetAll(_devices.Devices.Select(device => new DeviceRowViewModel(device)));

        DeviceConnectionText.Text = _devices.IsAdbConnected
            ? "ADB tracker connected · updates live"
            : _devices.IsUsingCliFallback
                ? "ADB via CLI fallback · updates live"
                : "ADB is disconnected";

        var problem = _devicesProblem
                      ?? (_devices.IsAdbConnected || _devices.IsUsingCliFallback ? null : _devices.LastError);
        ShowResult(
            DevicesSkeleton,
            DeviceList,
            DevicesEmptyText,
            DevicesErrorText,
            _deviceRows.Count > 0,
            problem is null ? null : $"{problem} The device list may be out of date.");
    }

    private async Task LoadProjectsRegionAsync()
    {
        ShowLoading(ProjectsSkeleton, ProjectList, ProjectsEmptyText, ProjectsErrorText);
        ShowLoading(QuickRunSkeleton, QuickRunContent, QuickRunEmptyPanel, QuickRunErrorText);
        try
        {
            await _projects.InitializeAsync().ConfigureAwait(false);
            _projectsProblem = null;
        }
        catch (Exception exception)
        {
            LogRegionFailure("project list", exception);
            _projectsProblem = exception.Message;
        }

        _projectsLoaded = true;
        Dispatch(() =>
        {
            UpdateProjects();
            UpdateQuickRun();
        });
    }

    private void UpdateProjects()
    {
        _projectRows.ResetAll(_projects.Projects
            .OrderByDescending(project => project.LastOpenedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(project => project.CreatedAt)
            .Take(RecentProjectCount)
            .Select(RecentProjectRow.From));

        ShowResult(
            ProjectsSkeleton,
            ProjectList,
            ProjectsEmptyText,
            ProjectsErrorText,
            _projectRows.Count > 0,
            _projectsProblem is null ? null : $"Projects could not be loaded: {_projectsProblem}");
    }

    private void UpdateQuickRun()
    {
        if (_projectsProblem is not null)
        {
            ShowResult(
                QuickRunSkeleton,
                QuickRunContent,
                QuickRunEmptyPanel,
                QuickRunErrorText,
                hasContent: false,
                $"Quick run is unavailable: {_projectsProblem}");
            return;
        }

        var projects = _projects.Projects.ToArray();
        var devices = _devices.Devices.ToArray();
        var selection = _quickRun.Resolve(projects, devices);
        _selection = selection;

        if (selection.Block == QuickRunBlock.NoProjects)
        {
            QuickRunEmptyText.Text = "No projects yet. Add one to build, install, and launch it from Home.";
            ShowResult(
                QuickRunSkeleton,
                QuickRunContent,
                QuickRunEmptyPanel,
                QuickRunErrorText,
                hasContent: false);
            return;
        }

        _syncingSelection = true;
        try
        {
            ProjectCombo.ItemsSource = projects;
            ProjectCombo.SelectedItem = selection.Project;
            ConfigurationCombo.ItemsSource = selection.Project?.RunConfigurations.ToArray();
            ConfigurationCombo.SelectedItem = selection.Configuration;
            DeviceCombo.ItemsSource = devices.Where(device => device.State == DeviceState.Device).ToArray();
            DeviceCombo.SelectedItem = selection.Device;
        }
        finally
        {
            _syncingSelection = false;
        }

        QuickRunBlockText.Text = selection.Block switch
        {
            QuickRunBlock.NoConfigurations =>
                $"\"{selection.Project?.Name}\" has no run configuration. Add one on the projects page.",
            QuickRunBlock.NoReadyDevices =>
                "No connected device is ready. Authorize or reconnect a device, then run again.",
            _ => string.Empty,
        };
        QuickRunBlockText.Visibility = QuickRunBlockText.Text.Length == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        QuickRunButton.IsEnabled = selection.CanRun;

        ShowResult(
            QuickRunSkeleton,
            QuickRunContent,
            QuickRunEmptyPanel,
            QuickRunErrorText,
            hasContent: true);
    }

    private void UpdateSessions()
    {
        var active = _runs.Sessions.Where(session => !session.IsFinished).ToArray();
        for (var index = _sessionRows.Count - 1; index >= 0; index--)
        {
            if (!active.Any(session => string.Equals(session.Id, _sessionRows[index].Id, StringComparison.Ordinal)))
            {
                _sessionRows.RemoveAt(index);
            }
        }

        for (var index = 0; index < active.Length; index++)
        {
            var session = active[index];
            var existing = _sessionRows.FirstOrDefault(row =>
                string.Equals(row.Id, session.Id, StringComparison.Ordinal));
            if (existing is null)
            {
                _sessionRows.Insert(index, new ActiveSessionRow(session));
                continue;
            }

            var currentIndex = _sessionRows.IndexOf(existing);
            if (currentIndex != index)
            {
                _sessionRows.RemoveAt(currentIndex);
                _sessionRows.Insert(index, existing);
            }

            existing.Update(session);
        }

        SessionList.Visibility = _sessionRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SessionsEmptyText.Visibility = _sessionRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_sessionRows.Count == 0)
        {
            _ticker.Stop();
        }
        else
        {
            _ticker.Start();
        }
    }

    private async Task LoadEnvironmentRegionAsync()
    {
        ShowLoading(EnvironmentSkeleton, EnvironmentList, EnvironmentMessageText);
        try
        {
            await _toolchain.LoadCachedAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogRegionFailure("toolchain cache", exception);
            Dispatch(() => ShowResult(
                EnvironmentSkeleton,
                EnvironmentList,
                EnvironmentMessageText,
                hasContent: false,
                $"The cached toolchain check could not be read: {exception.Message}"));
            return;
        }

        Dispatch(UpdateEnvironment);
    }

    private void UpdateEnvironment()
    {
        var snapshot = _toolchain.CachedSnapshot;
        if (snapshot is null || snapshot.Tools.Count == 0)
        {
            EnvironmentFreshnessText.Text = "Not checked yet.";
            ShowResult(
                EnvironmentSkeleton,
                EnvironmentList,
                EnvironmentMessageText,
                hasContent: false,
                "The doctor has not produced a result yet. Run a check to see which tools are missing.");
            return;
        }

        _environmentRows.ResetAll(snapshot.Tools.Select(EnvironmentToolRow.From));
        EnvironmentFreshnessText.Text = DescribeFreshness(snapshot.CheckedAt);

        var missing = snapshot.Tools
            .Where(tool => !tool.IsAvailable)
            .Select(tool => tool.DisplayName)
            .ToArray();
        ShowResult(
            EnvironmentSkeleton,
            EnvironmentList,
            EnvironmentMessageText,
            hasContent: true,
            missing.Length == 0
                ? null
                : $"Missing: {string.Join(", ", missing)}. Runs fail until these tools are available.");
    }

    private string DescribeFreshness(DateTimeOffset checkedAt)
    {
        if (checkedAt == DateTimeOffset.MinValue)
        {
            return "Never checked.";
        }

        var when = RelativeTime.Ago(checkedAt);
        if (_environmentChecked)
        {
            return $"Checked {when}";
        }

        return DateTimeOffset.UtcNow - checkedAt > StaleEnvironment
            ? $"Cached from {when} · may be out of date"
            : $"Cached from {when}";
    }

    private void UpdatePlugins()
    {
        if (!_plugins.PluginsDisabled && !_plugins.IsInitialized)
        {
            PluginsSkeleton.Visibility = Visibility.Visible;
            PluginWidgetHost.Visibility = Visibility.Collapsed;
            PluginsSummaryText.Visibility = Visibility.Collapsed;
            return;
        }

        UpdatePluginWidgets();

        PluginsSummaryText.Text = _plugins.PluginsDisabled
            ? $"Plugins are disabled ({_plugins.DisabledReason ?? "safe mode"})."
            : $"{_plugins.LoadedCount} plugin(s) loaded" +
              $"{FailureSummary()}. {_plugins.Registry.Commands.Count} commands available.";

        PluginsSkeleton.Visibility = Visibility.Collapsed;
        PluginWidgetHost.Visibility = Visibility.Visible;
        PluginsSummaryText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Mounts one lazily loaded card per registered home widget. Every widget
    /// loads on its own after the first frame, so a slow or failing widget can
    /// neither block Home nor its siblings.
    /// </summary>
    private void UpdatePluginWidgets()
    {
        var entries = _plugins.Extensions.HomeWidgets;

        // Stale widgets leave with both their boundary and their card: the
        // boundary must be disposed, and the card must leave the panel, or the
        // plugin would keep a permanent ghost card on Home after uninstall.
        foreach (var staleId in _pluginWidgets.Keys
            .Where(id => entries.All(entry => !string.Equals(entry.Widget.Id, id, StringComparison.Ordinal)))
            .ToArray())
        {
            if (_pluginWidgets.Remove(staleId, out var stale))
            {
                PluginWidgetList.Children.Remove(stale.Card);
                stale.Boundary.Dispose();
            }
        }

        if (entries.Count == 0)
        {
            PluginWidgetList.Children.Clear();
            PluginWidgetPlaceholderText.Text =
                "Plugin home widgets mount here. No installed plugin provides one yet.";
            PluginWidgetPlaceholderText.Visibility = Visibility.Visible;
            PluginWidgetList.Visibility = Visibility.Collapsed;
            return;
        }

        PluginWidgetPlaceholderText.Visibility = Visibility.Collapsed;
        PluginWidgetList.Visibility = Visibility.Visible;
        foreach (var entry in entries)
        {
            if (_pluginWidgets.ContainsKey(entry.Widget.Id))
            {
                continue;
            }

            var boundary = new PluginFaultBoundary(entry.PluginId, $"home widget {entry.Widget.Id}", _faultReporter);
            var card = BuildPluginWidgetCard(entry, boundary);
            PluginWidgetList.Children.Add(card);
            var host = new HomeWidgetMount(entry, boundary, card);
            _pluginWidgets[entry.Widget.Id] = host;
            QueuePluginWidgetLoad(entry, host);
        }
    }

    private static PluginCard BuildPluginWidgetCard(HomeWidgetEntry entry, PluginFaultBoundary boundary)
    {
        var source = new TextBlock
        {
            Text = entry.SourceLabel,
            FontSize = 11,
            Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(source);
        content.Children.Add(boundary);

        return new PluginCard(entry.Widget.Title) { Content = content };
    }

    private void QueuePluginWidgetLoad(HomeWidgetEntry entry, HomeWidgetMount host)
    {
        var afterFirstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher is null || _dispatcher.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => afterFirstFrame.TrySetResult());
        if (!queued)
        {
            afterFirstFrame.TrySetResult();
        }

        _ = LoadPluginWidgetAsync(entry, host, afterFirstFrame.Task);
    }

    private async Task LoadPluginWidgetAsync(
        HomeWidgetEntry entry,
        HomeWidgetMount host,
        Task afterFirstFrame)
    {
        try
        {
            await afterFirstFrame.ConfigureAwait(true);
            if (_disposed || !_pluginWidgets.TryGetValue(entry.Widget.Id, out var current) ||
                !ReferenceEquals(current, host))
            {
                return;
            }

            var result = await _plugins.Invoker.CreateViewAsync(
                entry.PluginId,
                $"home widget {entry.Widget.Id}",
                token => entry.Widget.CreateViewAsync(token),
                CancellationToken.None).ConfigureAwait(true);
            if (!_pluginWidgets.TryGetValue(entry.Widget.Id, out current) ||
                !ReferenceEquals(current, host))
            {
                return;
            }

            host.Boundary.Show(result.Content, result.Error);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Plugin home widget {WidgetId} from {PluginId} could not load: {Error}",
                    entry.Widget.Id,
                    entry.PluginId,
                    result.Error);
            }
        }
        catch (Exception exception)
        {
            LogRegionFailure($"plugin home widget {entry.Widget.Id}", exception);
            if (_pluginWidgets.TryGetValue(entry.Widget.Id, out var current) &&
                ReferenceEquals(current, host))
            {
                try
                {
                    host.Boundary.Show(null, exception.Message);
                }
                catch
                {
                    // One widget must never break the host page.
                }
            }
        }
    }

    private static Brush ResolveBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    private string FailureSummary()
    {
        var failed = _plugins.Results.Count(result => !result.Succeeded);
        return failed == 0 ? string.Empty : $", {failed} failed";
    }

    private async void OnQuickRunProjectChanged(object sender, SelectionChangedEventArgs e) =>
        await RememberSelectionAsync();

    private async void OnQuickRunConfigurationChanged(object sender, SelectionChangedEventArgs e) =>
        await RememberSelectionAsync();

    private async void OnQuickRunDeviceChanged(object sender, SelectionChangedEventArgs e) =>
        await RememberSelectionAsync();

    private async Task RememberSelectionAsync()
    {
        if (_syncingSelection)
        {
            return;
        }

        var project = ProjectCombo.SelectedItem as ProjectModel;
        if (project is null)
        {
            return;
        }

        // The configuration combo still lists the previous project's entries until UpdateQuickRun rebuilds it.
        var configuration = ConfigurationCombo.SelectedItem as RunConfiguration;
        if (configuration is not null && !project.RunConfigurations.Contains(configuration))
        {
            configuration = null;
        }

        var device = DeviceCombo.SelectedItem as DeviceInfo;

        try
        {
            await _quickRun.RememberAsync(project.Id, configuration?.Id, device?.Serial).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogRegionFailure("remembering the quick run target", exception);
        }

        Dispatch(UpdateQuickRun);
    }

    private async void OnQuickRunClick(object sender, RoutedEventArgs e)
    {
        var selection = _selection;
        if (selection is null)
        {
            return;
        }

        await StartRunAsync(selection.Project, selection.Configuration, selection.Device);
    }

    private async void OnRunProjectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RecentProjectRow row)
        {
            return;
        }

        var project = _projects.Get(row.Id);
        if (project is null)
        {
            ShowAction("That project is no longer available.", isError: true);
            return;
        }

        var configuration = project.RunConfigurations.FirstOrDefault(candidate => candidate.IsDefault)
                            ?? project.RunConfigurations.FirstOrDefault();
        var serial = configuration?.DeviceSerial;
        var device = string.IsNullOrWhiteSpace(serial)
            ? _devices.Devices.FirstOrDefault(candidate => candidate.State == DeviceState.Device)
            : _devices.Devices.FirstOrDefault(candidate =>
                string.Equals(candidate.Serial, serial, StringComparison.Ordinal));
        await StartRunAsync(project, configuration, device ?? _selection?.Device);
    }

    private async Task StartRunAsync(ProjectModel? project, RunConfiguration? configuration, DeviceInfo? device)
    {
        if (_startingRun)
        {
            return;
        }

        if (project is null || configuration is null)
        {
            ShowAction("Select a project and run configuration first.", isError: true);
            return;
        }

        var serial = device?.Serial ?? configuration.DeviceSerial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            ShowAction("Connect a device before running.", isError: true);
            return;
        }

        var validation = RunConfigurationValidator.Validate(project, configuration);
        if (!validation.IsValid)
        {
            ShowAction(
                "The run configuration is invalid: " +
                string.Join("; ", validation.Errors.Select(error => $"{error.Field}: {error.Message}")),
                isError: true);
            return;
        }

        _startingRun = true;
        QuickRunButton.IsEnabled = false;
        try
        {
            await _quickRun.RememberAsync(project.Id, configuration.Id, serial);

            var request = new RunRequest
            {
                Project = project,
                Configuration = configuration,
                Device = device,
                DeviceSerial = serial,
            };

            string? sessionId = null;
            void OnChanged(object? sender, RunSessionChangedEventArgs args)
            {
                if (sessionId is null &&
                    string.Equals(args.Session.ProjectId, project.Id, StringComparison.Ordinal) &&
                    string.Equals(args.Session.ConfigurationId, configuration.Id, StringComparison.Ordinal))
                {
                    sessionId = args.Session.Id;
                }
            }

            Task<RunSession> run;
            _runs.SessionChanged += OnChanged;
            try
            {
                run = _runs.RunAsync(request);
            }
            finally
            {
                _runs.SessionChanged -= OnChanged;
            }

            if (sessionId is not null)
            {
                _workspace.OpenProject(project.Id);
                _workspace.OpenSession(sessionId);
                App.Instance.Navigate("session");
            }

            await run;
        }
        catch (Exception exception)
        {
            ReportFailure("Starting the run", exception);
        }
        finally
        {
            _startingRun = false;
            QuickRunButton.IsEnabled = _selection?.CanRun ?? false;
        }
    }

    private void OnOpenQuickRunProjectClick(object sender, RoutedEventArgs e)
    {
        if (_selection?.Project is { } project)
        {
            _workspace.OpenProject(project.Id);
        }

        App.Instance.Navigate("projects");
    }

    private void OnOpenProjectClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RecentProjectRow row)
        {
            _workspace.OpenProject(row.Id);
        }

        App.Instance.Navigate("projects");
    }

    private void OnOpenSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActiveSessionRow row)
        {
            _workspace.OpenSession(row.Id);
            App.Instance.Navigate("session");
        }
    }

    private void OnOpenDeviceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceRowViewModel row)
        {
            _workspace.OpenDevice(row.Serial);
        }

        App.Instance.Navigate("devices");
    }

    private void OnSeeAllSessionsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("history");

    private void OnOpenProjectsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("projects");

    private void OnOpenDevicesClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("devices");

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("settings");

    private void OnOpenExtensionsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("extensions");

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Reload(force: true);

    private async void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        _devicesLoaded = false;
        await LoadDevicesRegionAsync();
        UpdateQuickRun();
    }

    private async void OnCheckToolchainClick(object sender, RoutedEventArgs e)
    {
        CheckToolchainButton.IsEnabled = false;
        EnvironmentFreshnessText.Text = "Checking the toolchain…";
        try
        {
            await _toolchain.CheckAsync();
            _environmentChecked = true;
            ShowAction("Toolchain check finished.");
        }
        catch (Exception exception)
        {
            ReportFailure("Checking the toolchain", exception);
        }
        finally
        {
            CheckToolchainButton.IsEnabled = true;
            UpdateEnvironment();
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

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Dispatch(() =>
    {
        UpdateDevices();
        UpdateQuickRun();
    });

    private void OnDeviceUpdated(object? sender, DeviceInfo device) => Dispatch(() =>
    {
        _deviceRows.FirstOrDefault(row => string.Equals(row.Serial, device.Serial, StringComparison.Ordinal))
            ?.Update(device);
    });

    private void OnToolchainChanged(object? sender, ToolchainSnapshot snapshot) => Dispatch(() =>
    {
        _environmentChecked = true;
        UpdateEnvironment();
    });

    private void OnPluginsChanged(object? sender, EventArgs e) => Dispatch(UpdatePlugins);

    private void OnProjectsChanged(object? sender, EventArgs e) => Dispatch(() =>
    {
        UpdateProjects();
        UpdateQuickRun();
    });

    private void OnSessionChanged(object? sender, RunSessionChangedEventArgs e) => Dispatch(UpdateSessions);

    private void OnTick(object? sender, object e)
    {
        foreach (var row in _sessionRows.ToArray())
        {
            row.Tick();
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

    private void LogRegionFailure(string region, Exception exception)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(exception, "The home {Region} region failed.", region);
        }
    }

    private void ShowAction(string message, bool isError = false)
    {
        ActionBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }

    private void ReportFailure(string action, Exception exception)
    {
        LogRegionFailure(action, exception);
        ShowAction(
            $"{action} failed: {exception.Message} (Diagnostics ID {_diagnostics.DiagnosticsId}).",
            isError: true);
    }

    private static void ShowLoading(UIElement skeleton, params UIElement[] hidden)
    {
        skeleton.Visibility = Visibility.Visible;
        foreach (var element in hidden)
        {
            element.Visibility = Visibility.Collapsed;
        }
    }

    private static void ShowResult(
        UIElement skeleton,
        UIElement content,
        UIElement empty,
        TextBlock error,
        bool hasContent,
        string? errorText = null)
    {
        empty.Visibility = !hasContent && errorText is null ? Visibility.Visible : Visibility.Collapsed;
        ShowResult(skeleton, content, error, hasContent, errorText);
    }

    private static void ShowResult(
        UIElement skeleton,
        UIElement content,
        TextBlock error,
        bool hasContent,
        string? errorText = null)
    {
        skeleton.Visibility = Visibility.Collapsed;
        content.Visibility = hasContent ? Visibility.Visible : Visibility.Collapsed;
        error.Text = errorText ?? string.Empty;
        error.Visibility = errorText is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>One home widget and the host chrome (fault boundary, card) that carries it.</summary>
    private sealed class HomeWidgetMount(
        HomeWidgetEntry entry,
        PluginFaultBoundary boundary,
        PluginCard card)
    {
        public HomeWidgetEntry Entry { get; } = entry;

        public PluginFaultBoundary Boundary { get; } = boundary;

        public PluginCard Card { get; } = card;
    }
}

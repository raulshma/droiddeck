using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using DroidDeck.App.Services;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.App.Views;

/// <summary>
/// Runs one project configuration on several devices, showing an aggregate
/// status and one independent, filterable log tab per device.
/// </summary>
public sealed partial class MultiDevicePage : Page, IRefreshablePage, IDisposable
{
    private const string AllTagsLabel = "All devices";

    private readonly IMultiDeviceRunService _runs;
    private readonly IRunSessionService _sessions;
    private readonly IProjectService _projects;
    private readonly IDeviceService _devices;
    private readonly SessionWorkspace _workspace;
    private readonly ILogger<MultiDevicePage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private readonly ObservableCollection<DeviceRow> _deviceRows = [];
    private readonly Dictionary<string, DeviceTabState> _tabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionSerials = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _pendingOutput =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectedSerials = new(StringComparer.Ordinal);
    private readonly object _sessionGate = new();
    private int _outputFlushScheduled;
    private RunGroup? _currentGroup;
    private string? _activeGroupId;
    private string? _tabsGroupId;
    private string? _selectedTag;
    private bool _syncingSelection;
    private bool _syncingTag;
    private bool _syncingProject;
    private bool _busy;
    private bool _disposed;

    /// <summary>Creates the multi-device page.</summary>
    public MultiDevicePage(
        IMultiDeviceRunService runs,
        IRunSessionService sessions,
        IProjectService projects,
        IDeviceService devices,
        SessionWorkspace workspace,
        ILogger<MultiDevicePage> logger)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        DeviceList.ItemsSource = _deviceRows;
        DeviceTabs.SelectionChanged += OnDeviceTabsSelectionChanged;
        _runs.GroupChanged += OnGroupChanged;
        _sessions.SessionChanged += OnSessionChanged;
        _sessions.OutputReceived += OnOutputReceived;
        _devices.DevicesChanged += OnDevicesChanged;
        _projects.ProjectsChanged += OnProjectsChanged;
        _projects.ProjectChanged += OnProjectChanged;
        LoadProjects();
        RefreshDevices();
        UpdateButtons();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        _ = RefreshAsync();
    }

    /// <summary>Starts a run for the current selection; used by the command palette.</summary>
    public Task RunSelectedAsync() => StartRunAsync(dryRun: false);

    /// <summary>Starts a dry run for the current selection; used by the command palette.</summary>
    public Task RunSelectedDryAsync() => StartRunAsync(dryRun: true);

    /// <summary>Cancels the active run group; used by the command palette.</summary>
    public async Task CancelActiveRunAsync()
    {
        if (_currentGroup is not { IsFinished: false } group)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await _runs.CancelAsync(group.Id);
            ApplyGroup(group);
            ShowResult(true, "The multi-device run was cancelled.", null, null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The run could not be cancelled: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
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
        _runs.GroupChanged -= OnGroupChanged;
        _sessions.SessionChanged -= OnSessionChanged;
        _sessions.OutputReceived -= OnOutputReceived;
        _devices.DevicesChanged -= OnDevicesChanged;
        _projects.ProjectsChanged -= OnProjectsChanged;
        _projects.ProjectChanged -= OnProjectChanged;
        _pendingOutput.Clear();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_activeGroupId is { } groupId && _runs.GetGroup(groupId) is { } group)
        {
            ApplyGroup(group);
        }
        else
        {
            UpdateButtons();
        }
    }

    private async Task RefreshAsync()
    {        try
        {
            await _devices.RefreshAsync();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Devices could not be refreshed for the multi-device page.");
        }

        LoadProjects();
        RefreshDevices();
        if (_activeGroupId is { } groupId && _runs.GetGroup(groupId) is { } group)
        {
            ApplyGroup(group);
        }
        else
        {
            UpdateButtons();
        }
    }

    private void LoadProjects()
    {
        var previous = (ProjectCombo.SelectedItem as ProjectModel)?.Id
            ?? _workspace.ProjectId;
        _syncingProject = true;
        try
        {
            ProjectCombo.ItemsSource = _projects.Projects;
            ProjectCombo.SelectedItem = _projects.Projects.FirstOrDefault(project =>
                string.Equals(project.Id, previous, StringComparison.Ordinal))
                ?? (_projects.Projects.Count > 0 ? _projects.Projects[0] : null);
        }
        finally
        {
            _syncingProject = false;
        }

        LoadConfigurations();
        ShowTargetGuidance();
    }

    private void LoadConfigurations()
    {
        var project = ProjectCombo.SelectedItem as ProjectModel;
        var previous = (ConfigurationCombo.SelectedItem as RunConfiguration)?.Id;
        ConfigurationCombo.ItemsSource = project?.RunConfigurations ?? [];
        ConfigurationCombo.SelectedItem = project?.RunConfigurations.FirstOrDefault(configuration =>
            string.Equals(configuration.Id, previous, StringComparison.Ordinal))
            ?? project?.RunConfigurations.FirstOrDefault(configuration => configuration.IsDefault)
            ?? project?.RunConfigurations.FirstOrDefault();
    }

    private void ShowTargetGuidance()
    {
        if (_projects.Projects.Count == 0)
        {
            ShowGuidance("Add a project on the Projects page before running across devices.");
            return;
        }

        var project = ProjectCombo.SelectedItem as ProjectModel;
        if (project is null)
        {
            ShowGuidance("Select a project.");
            return;
        }

        if (project.RunConfigurations.Count == 0)
        {
            ShowGuidance($"{project.Name} has no run configuration yet. Add one on the Projects page.");
            return;
        }

        ShowGuidance("Select one or more devices, then build once and run. Failed devices can be retried without rebuilding.");
    }

    private void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProject)
        {
            return;
        }

        if (ProjectCombo.SelectedItem is ProjectModel project)
        {
            _workspace.OpenProject(project.Id);
        }

        LoadConfigurations();
        ShowTargetGuidance();
        UpdateButtons();
    }

    private void OnTagChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTag)
        {
            return;
        }

        _selectedTag = TagCombo.SelectedItem as string;
        RefreshDevices();
    }

    private void RefreshDevices()
    {
        var usable = _devices.Devices
            .Where(device => device.CanRunActions)
            .ToArray();
        var tags = usable
            .SelectMany(device => device.Tags)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var targetTag = _selectedTag is { Length: > 0 } selected &&
                        tags.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? tags.First(tag => string.Equals(tag, selected, StringComparison.OrdinalIgnoreCase))
            : AllTagsLabel;
        _syncingTag = true;
        try
        {
            TagCombo.ItemsSource = new[] { AllTagsLabel }.Concat(tags).ToArray();
            TagCombo.SelectedItem = targetTag;
        }
        finally
        {
            _syncingTag = false;
        }

        _selectedTag = targetTag;
        var filtered = string.Equals(_selectedTag, AllTagsLabel, StringComparison.OrdinalIgnoreCase)
            ? usable
            : usable.Where(device => device.Tags.Contains(_selectedTag, StringComparer.OrdinalIgnoreCase)).ToArray();

        _syncingSelection = true;
        try
        {
            _deviceRows.Clear();
            foreach (var device in filtered)
            {
                var row = new DeviceRow(device);
                _deviceRows.Add(row);
                if (_selectedSerials.Contains(device.Serial))
                {
                    DeviceList.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        UpdateSelectionText();
        UpdateButtons();
    }

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) =>
        Dispatch(RefreshDevices);

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }

        _selectedSerials.Clear();
        foreach (var row in DeviceList.SelectedItems.OfType<DeviceRow>())
        {
            _selectedSerials.Add(row.Device.Serial);
        }

        UpdateSelectionText();
        UpdateButtons();
    }

    private void UpdateSelectionText()
    {
        var count = _selectedSerials.Count;
        SelectionText.Text = count == 0
            ? "No devices selected."
            : count == 1
                ? "1 device selected."
                : $"{count} devices selected.";
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        _syncingSelection = true;
        try
        {
            foreach (var row in _deviceRows)
            {
                if (!DeviceList.SelectedItems.Contains(row))
                {
                    DeviceList.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        _selectedSerials.Clear();
        foreach (var row in DeviceList.SelectedItems.OfType<DeviceRow>())
        {
            _selectedSerials.Add(row.Device.Serial);
        }

        UpdateSelectionText();
        UpdateButtons();
    }

    private void OnSelectNoneClick(object sender, RoutedEventArgs e)
    {
        _syncingSelection = true;
        try
        {
            DeviceList.SelectedItems.Clear();
        }
        finally
        {
            _syncingSelection = false;
        }

        _selectedSerials.Clear();
        UpdateSelectionText();
        UpdateButtons();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Refresh();

    private void OnOpenProjectsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("projects");

    private async void OnRunClick(object sender, RoutedEventArgs e) => await StartRunAsync(dryRun: false);

    private async void OnDryRunClick(object sender, RoutedEventArgs e) => await StartRunAsync(dryRun: true);

    private async Task StartRunAsync(bool dryRun)
    {
        if (ProjectCombo.SelectedItem is not ProjectModel project ||
            ConfigurationCombo.SelectedItem is not RunConfiguration configuration)
        {
            ShowResult(false, "Select a project and run configuration first.", null, null);
            return;
        }

        var devices = DeviceList.SelectedItems
            .OfType<DeviceRow>()
            .Select(row => row.Device)
            .ToArray();
        if (devices.Length == 0)
        {
            ShowResult(false, "Select at least one device.", null, "Use All, or select devices individually.");
            return;
        }

        var request = new MultiDeviceRunRequest
        {
            Project = project,
            Configuration = configuration,
            Devices = devices,
            GroupName = string.Equals(_selectedTag, AllTagsLabel, StringComparison.OrdinalIgnoreCase)
                ? null
                : _selectedTag,
            DryRun = dryRun,
            Options = new RunOptions { AttachLogs = !dryRun },
        };
        var previousGroupId = _activeGroupId;
        SetBusy(true);
        try
        {
            var task = _runs.RunAsync(request);
            var newest = _runs.Groups.Count > 0 ? _runs.Groups[0] : null;
            if (newest is not null && !string.Equals(newest.Id, previousGroupId, StringComparison.Ordinal))
            {
                _activeGroupId = newest.Id;
                ApplyGroup(newest);
            }

            ShowResult(
                true,
                dryRun
                    ? $"Dry run started for {devices.Length} device(s)."
                    : $"Building once for {devices.Length} device(s).",
                null,
                "Each device gets its own session. Switching tabs keeps each device's log and filter.");
            await ObserveGroupAsync(task);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The multi-device run could not start: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e) => await CancelActiveRunAsync();

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_currentGroup is not { } group)
        {
            return;
        }

        if (group.FailedCount == 0)
        {
            ShowResult(false, "There are no failed devices to retry.", null, null);
            return;
        }

        if (group.PrebuiltArtifactFiles.Count == 0)
        {
            ShowResult(
                false,
                "No built artifact is available for a retry.",
                null,
                "Run the project again so the artifact can be rebuilt.");
            return;
        }

        SetBusy(true);
        try
        {
            ShowResult(
                true,
                $"Retrying {group.FailedCount} failed device(s) with the existing artifact...",
                null,
                null);
            await ObserveGroupAsync(_runs.RetryFailedAsync(group.Id));
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(false, $"The retry could not start: {exception.Message}", null, null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ObserveGroupAsync(Task<RunGroup> task)
    {
        try
        {
            var group = await task;
            Dispatch(() =>
            {
                ApplyGroup(group);
                var succeeded = group.State == RunGroupState.Succeeded;
                ShowResult(
                    succeeded,
                    succeeded
                        ? group.Summary
                        : $"{group.State}: {group.Summary} {group.Error}".Trim(),
                    null,
                    group.State == RunGroupState.PartiallyFailed
                        ? "Retry failed devices without rebuilding, or open a device tab to see its error."
                        : null);
            });
        }
        catch (OperationCanceledException)
        {
            Dispatch(() => ShowResult(false, "The multi-device run was cancelled.", null, null));
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "A multi-device run failed.");
            Dispatch(() => ShowResult(false, $"The multi-device run failed: {exception.Message}", null, null));
        }
    }

    private void OnGroupChanged(object? sender, RunGroupChangedEventArgs e) => Dispatch(() =>
    {
        if (_activeGroupId is null || string.Equals(e.Group.Id, _activeGroupId, StringComparison.Ordinal))
        {
            _activeGroupId = e.Group.Id;
            ApplyGroup(e.Group);
        }
    });

    private void ApplyGroup(RunGroup group)
    {
        _currentGroup = group;
        GroupStateText.Text = $"{group.State} · {group.ProjectName} · {group.ConfigurationName}";
        GroupSummaryText.Text = group.Summary;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(group.GroupName))
        {
            parts.Add($"group {group.GroupName}");
        }

        parts.Add($"{group.Devices.Count} device(s)");
        if (group.Duration is { } duration)
        {
            parts.Add($"{duration.TotalSeconds:F1} s");
        }

        if (group.PrebuiltArtifactFiles.Count > 0)
        {
            parts.Add($"{group.PrebuiltArtifactFiles.Count} artifact file(s)");
        }

        if (!string.IsNullOrWhiteSpace(group.Error))
        {
            parts.Add(group.Error);
        }

        GroupDetailsText.Text = string.Join(" · ", parts);
        GroupProgress.Maximum = Math.Max(1, group.Devices.Count);
        GroupProgress.Value = group.Devices.Count(device =>
            device.State is RunGroupState.Succeeded or RunGroupState.Failed or RunGroupState.Cancelled);
        EnsureTabs(group);
        foreach (var device in group.Devices)
        {
            if (_tabs.TryGetValue(device.Serial, out var tab))
            {
                tab.UpdateDevice(device);
            }
        }

        UpdateButtons();
    }

    private void EnsureTabs(RunGroup group)
    {
        var sameDevices = _tabsGroupId is not null &&
                          string.Equals(_tabsGroupId, group.Id, StringComparison.Ordinal) &&
                          _tabs.Count == group.Devices.Count &&
                          group.Devices.All(device => _tabs.ContainsKey(device.Serial));
        if (sameDevices)
        {
            return;
        }

        DeviceTabs.TabItems.Clear();
        _tabs.Clear();
        _pendingOutput.Clear();
        _tabsGroupId = group.Id;
        foreach (var device in group.Devices)
        {
            var state = CreateTab(device);
            _tabs[device.Serial] = state;
            DeviceTabs.TabItems.Add(state.Item);
            if (!string.IsNullOrWhiteSpace(device.SessionId))
            {
                lock (_sessionGate)
                {
                    _sessionSerials[device.SessionId] = device.Serial;
                }

                if (_sessions.GetSession(device.SessionId) is { } session)
                {
                    state.AppendRange(session.BuildOutput);
                    state.UpdateSession(session);
                }
            }
        }

        TabsEmptyText.Visibility = _tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DeviceTabs.TabItems.Count > 0)
        {
            DeviceTabs.SelectedIndex = 0;
        }
    }

    private void OnDeviceTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceTabs.SelectedItem is not TabViewItem selected)
        {
            return;
        }

        foreach (var state in _tabs.Values)
        {
            if (ReferenceEquals(state.Item, selected))
            {
                state.RestoreScrollPosition();
                return;
            }
        }
    }

    private DeviceTabState CreateTab(RunGroupDevice device)
    {
        var filterBox = new TextBox
        {
            PlaceholderText = "Filter this device's log (case-insensitive).",
            Margin = new Thickness(0),
        };
        var statusText = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var logText = new TextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        };
        var logScroller = new ScrollViewer
        {
            Content = logText,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var openButton = new Button
        {
            Content = "Open session",
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(filterBox);
        Grid.SetColumn(openButton, 1);
        header.Children.Add(openButton);

        var content = new Grid { Padding = new Thickness(12), RowSpacing = 8 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(header);
        Grid.SetRow(statusText, 1);
        content.Children.Add(statusText);
        Grid.SetRow(logScroller, 2);
        content.Children.Add(logScroller);

        var item = new TabViewItem
        {
            Header = DeviceHeader(device),
            IsClosable = false,
            Content = content,
        };
        var state = new DeviceTabState(device, item, logScroller, logText, filterBox, statusText);
        filterBox.TextChanged += (_, _) => state.Render();
        openButton.Click += OnOpenSessionClick;
        openButton.DataContext = state;
        return state;
    }

    private static string DeviceHeader(RunGroupDevice device) =>
        $"{device.DeviceName ?? device.Serial} ({device.Serial})";

    private void OnOpenSessionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceTabState state)
        {
            return;
        }

        if (state.SessionId is not { Length: > 0 } sessionId)
        {
            ShowResult(
                false,
                "This device does not have a session yet.",
                null,
                "Wait for the run to reach the install or launch step.");
            return;
        }

        _workspace.OpenSession(sessionId);
        App.Instance.Navigate("session");
    }

    private void OnSessionChanged(object? sender, RunSessionChangedEventArgs e)
    {
        var session = e.Session;
        if (session.GroupId is not { Length: > 0 } groupId ||
            !string.Equals(groupId, _activeGroupId, StringComparison.Ordinal))
        {
            return;
        }

        lock (_sessionGate)
        {
            _sessionSerials[session.Id] = session.DeviceSerial;
        }

        Dispatch(() =>
        {
            if (_tabs.TryGetValue(session.DeviceSerial, out var tab))
            {
                tab.UpdateSession(session);
            }
        });
    }

    private void OnOutputReceived(object? sender, RunOutputEventArgs e)
    {
        string? serial;
        lock (_sessionGate)
        {
            _sessionSerials.TryGetValue(e.SessionId, out serial);
        }

        if (serial is null)
        {
            var session = _sessions.GetSession(e.SessionId);
            if (session?.GroupId is not { Length: > 0 } groupId ||
                !string.Equals(groupId, _activeGroupId, StringComparison.Ordinal))
            {
                return;
            }

            serial = session.DeviceSerial;
            lock (_sessionGate)
            {
                _sessionSerials[e.SessionId] = serial;
            }
        }

        var line = e.Line.TrimEnd('\r', '\n');
        if (line.Length == 0)
        {
            return;
        }

        var pending = _pendingOutput.GetOrAdd(serial, static _ => new ConcurrentQueue<string>());
        pending.Enqueue(line);
        ScheduleOutputFlush();
    }

    private void ScheduleOutputFlush()
    {
        if (Interlocked.CompareExchange(ref _outputFlushScheduled, 1, 0) != 0)
        {
            return;
        }

        Dispatch(FlushOutput);
    }

    private void FlushOutput()
    {
        Volatile.Write(ref _outputFlushScheduled, 0);
        foreach (var pair in _pendingOutput)
        {
            if (!_tabs.TryGetValue(pair.Key, out var tab))
            {
                while (pair.Value.TryDequeue(out _))
                {
                }

                continue;
            }

            var lines = new List<string>();
            while (pair.Value.TryDequeue(out var line))
            {
                lines.Add(line);
            }

            if (lines.Count > 0)
            {
                tab.AppendBatch(lines);
            }
        }

        // Lines that arrived while the UI thread was draining need another pass.
        if (!_pendingOutput.IsEmpty)
        {
            ScheduleOutputFlush();
        }
    }

    private void OnProjectsChanged(object? sender, EventArgs e) => Dispatch(() =>
    {
        LoadProjects();
        UpdateButtons();
    });

    private void OnProjectChanged(object? sender, ProjectModel project) => Dispatch(() =>
    {
        LoadProjects();
        UpdateButtons();
    });

    private void UpdateButtons()
    {
        var running = _currentGroup is { IsFinished: false };
        var canStart = !_busy && !running;
        RunButton.IsEnabled = canStart;
        DryRunButton.IsEnabled = canStart;
        CancelButton.IsEnabled = !_busy && running;
        RetryButton.IsEnabled = !_busy &&
                                _currentGroup is { IsFinished: true } group &&
                                group.FailedCount > 0 &&
                                group.PrebuiltArtifactFiles.Count > 0;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

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

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    /// <summary>One selectable device shaped for the picker.</summary>
    public sealed class DeviceRow
    {
        /// <summary>Creates a row.</summary>
        public DeviceRow(DeviceInfo device)
        {
            Device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>Gets the device.</summary>
        public DeviceInfo Device { get; }

        /// <summary>Gets the display name.</summary>
        public string DisplayName => Device.DisplayName;

        /// <summary>Gets the serial and tags line.</summary>
        public string Detail => Device.Tags.Count == 0
            ? Device.Serial
            : $"{Device.Serial} · {string.Join(", ", Device.Tags)}";
    }

    /// <summary>One per-device tab with its own log buffer, filter, and scroll position.</summary>
    public sealed class DeviceTabState
    {
        private const int MaxLines = 5_000;
        private const int MaxCharacters = 200_000;
        private const double TailThreshold = 24;

        private readonly List<string> _lines = [];
        private readonly ScrollViewer _logScroller;
        private readonly TextBlock _logText;
        private readonly TextBox _filterBox;
        private readonly TextBlock _statusText;
        private bool _followTail = true;
        private double _scrollOffset;

        /// <summary>Creates the tab state.</summary>
        public DeviceTabState(
            RunGroupDevice device,
            TabViewItem item,
            ScrollViewer logScroller,
            TextBlock logText,
            TextBox filterBox,
            TextBlock statusText)
        {
            Device = device ?? throw new ArgumentNullException(nameof(device));
            Item = item ?? throw new ArgumentNullException(nameof(item));
            _logScroller = logScroller ?? throw new ArgumentNullException(nameof(logScroller));
            _logText = logText ?? throw new ArgumentNullException(nameof(logText));
            _filterBox = filterBox ?? throw new ArgumentNullException(nameof(filterBox));
            _statusText = statusText ?? throw new ArgumentNullException(nameof(statusText));
            _logScroller.ViewChanged += OnLogViewChanged;
            UpdateStatus();
        }

        /// <summary>Gets the group device.</summary>
        public RunGroupDevice Device { get; }

        /// <summary>Gets the tab.</summary>
        public TabViewItem Item { get; }

        /// <summary>Gets the current child session identifier.</summary>
        public string? SessionId => Device.SessionId;

        /// <summary>Appends one output line.</summary>
        public void Append(string line) => AppendBatch([line]);

        /// <summary>Appends a burst of output lines with a single UI update.</summary>
        public void AppendBatch(IReadOnlyList<string> lines)
        {
            if (lines.Count == 0)
            {
                return;
            }

            var added = 0;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                _lines.Add(line);
                added++;
            }

            if (added == 0)
            {
                return;
            }

            if (_lines.Count > MaxLines)
            {
                _lines.RemoveRange(0, _lines.Count - MaxLines);
                Render();
                return;
            }

            var filter = _filterBox.Text;
            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                if (line.Length == 0 ||
                    (filter.Length > 0 && !line.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                builder.Append(line).AppendLine();
            }

            if (builder.Length > 0)
            {
                _logText.Text = Trim(_logText.Text + builder.ToString());
                if (_followTail)
                {
                    ScrollToEnd();
                }
            }
        }

        /// <summary>Appends existing output, such as a session's build log.</summary>
        public void AppendRange(IEnumerable<string> lines) =>
            AppendBatch(lines.Select(line => line.TrimEnd('\r', '\n')).ToArray());

        /// <summary>Re-renders the log using the current filter.</summary>
        public void Render()
        {
            var filter = _filterBox.Text;
            var builder = new StringBuilder();
            foreach (var line in _lines)
            {
                if (filter.Length == 0 || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    builder.AppendLine(line);
                }
            }

            _logText.Text = Trim(builder.ToString());
            if (_followTail)
            {
                ScrollToEnd();
            }
            else
            {
                RestoreScrollPosition();
            }
        }

        /// <summary>Restores the scroll offset remembered before the tab was hidden.</summary>
        public void RestoreScrollPosition()
        {
            _logScroller.DispatcherQueue?.TryEnqueue(() =>
            {
                _logScroller.UpdateLayout();
                var target = _followTail
                    ? _logScroller.ScrollableHeight
                    : Math.Min(_scrollOffset, _logScroller.ScrollableHeight);
                _logScroller.ChangeView(null, target, null, disableAnimation: true);
            });
        }

        /// <summary>Updates the status line from a session snapshot.</summary>
        public void UpdateSession(RunSession session)
        {
            ArgumentNullException.ThrowIfNull(session);
            var detail = session.Error is { Length: > 0 } error
                ? $"{session.State} · {session.StatusMessage ?? "No status"} · {error}"
                : $"{session.State} · {session.StatusMessage ?? "Waiting"}";
            _statusText.Text = detail;
            if (!string.IsNullOrWhiteSpace(session.Error))
            {
                Append("ERROR: " + session.Error);
            }
        }

        /// <summary>Updates the status line from a group-device snapshot.</summary>
        public void UpdateDevice(RunGroupDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            Item.Header = $"{device.DeviceName ?? device.Serial} ({device.Serial}) · {device.State}" +
                          (device.Attempts > 1 ? $" · attempt {device.Attempts}" : string.Empty);
            var detail = device.Error is { Length: > 0 } error
                ? $"{device.State} · {error}"
                : $"{device.State} · {device.Attempts} attempt(s)";
            _statusText.Text = detail;
        }

        private void OnLogViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!_logScroller.IsLoaded)
            {
                return;
            }

            _scrollOffset = _logScroller.VerticalOffset;
            _followTail = _logScroller.ScrollableHeight - _scrollOffset <= TailThreshold;
        }

        private void ScrollToEnd() => _logScroller.DispatcherQueue?.TryEnqueue(() =>
        {
            _logScroller.UpdateLayout();
            _logScroller.ChangeView(null, _logScroller.ScrollableHeight, null, disableAnimation: true);
        });

        private void UpdateStatus() => _statusText.Text = "Waiting for the run to start.";

        private static string Trim(string text) =>
            text.Length <= MaxCharacters ? text : text[^MaxCharacters..];
    }
}

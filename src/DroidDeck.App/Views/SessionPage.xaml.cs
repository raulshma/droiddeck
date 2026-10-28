using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using DroidDeck.App.Services;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Logs;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Sessions;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Text;

namespace DroidDeck.App.Views;

/// <summary>Shows a live or persisted run session with timeline, build output, and logs.</summary>
public sealed partial class SessionPage : Page, IRefreshablePage, IDisposable
{
    private const int MaxBuildLines = 100_000;
    private readonly IRunSessionService _runs;
    private readonly ILogcatService _logcat;
    private readonly ISessionStore _store;
    private readonly ISavedFilterStore _filters;
    private readonly SessionWorkspace _workspace;
    private readonly IConfigurationService _configuration;
    private readonly PluginHost _plugins;
    private readonly ILogger<SessionPage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private readonly BulkObservableCollection<string> _buildLines = [];
    private readonly BulkObservableCollection<LogRow> _logRows = [];
    private readonly ConcurrentQueue<string> _pendingBuildLines = new();
    private readonly List<LogEntry> _allEntries = [];
    private readonly List<LogRow> _searchRows = [];
    private readonly DispatcherTimer _buildTimer;
    private readonly List<HighlightRuleRow> _highlightRules = [];
    private HighlightEngine _highlightEngine = new([]);
    private RunSession? _session;
    private RunSession? _boundReadOnly;
    private ILogcatSession? _logcatSession;
    private LogFilter _filter = new(new LogFilterCriteria());
    private CrashEvent? _latestCrash;
    private int _searchIndex = -1;
    private int _maxLogLines = 20_000;
    private int _logRebuildGeneration;
    private bool _initializingFilters;
    private bool _disposed;

    /// <summary>Creates the session page.</summary>
    public SessionPage(
        IRunSessionService runs,
        ILogcatService logcat,
        ISessionStore store,
        ISavedFilterStore filters,
        SessionWorkspace workspace,
        IConfigurationService configuration,
        PluginHost plugins,
        ILogger<SessionPage> logger)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _logcat = logcat ?? throw new ArgumentNullException(nameof(logcat));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        LevelCombo.ItemsSource = new LogLevelChoice[]
        {
            new(null, "All levels"),
            new("V", "Verbose"),
            new("D", "Debug"),
            new("I", "Info"),
            new("W", "Warn"),
            new("E", "Error"),
            new("F", "Fatal"),
        };
        LevelCombo.SelectedIndex = 0;
        BuildOutputList.ItemsSource = _buildLines;
        LogList.ItemsSource = _logRows;
        ResetHighlightRules();

        _runs.SessionChanged += OnSessionChanged;
        _runs.OutputReceived += OnOutputReceived;
        _workspace.Changed += OnWorkspaceChanged;
        _plugins.Registry.Changed += OnPluginRegistryChanged;
        RebuildPluginToolbar();

        _buildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _buildTimer.Tick += OnBuildTimerTick;
        _buildTimer.Start();

        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        var readOnly = _workspace.ReadOnlySession;
        if (readOnly is not null &&
            string.Equals(readOnly.Id, _workspace.SessionId, StringComparison.Ordinal))
        {
            BindSession(readOnly, rebuildOutput: true);
            return;
        }

        var sessionId = _workspace.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            ShowEmpty();
            return;
        }

        var live = _runs.GetSession(sessionId);
        if (live is not null)
        {
            BindSession(live, rebuildOutput: true);
            return;
        }

        _ = LoadPersistedAsync(sessionId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buildTimer.Stop();
        _buildTimer.Tick -= OnBuildTimerTick;
        _runs.SessionChanged -= OnSessionChanged;
        _runs.OutputReceived -= OnOutputReceived;
        _workspace.Changed -= OnWorkspaceChanged;
        _plugins.Registry.Changed -= OnPluginRegistryChanged;
        DetachLogcatSession();
    }

    private async Task LoadPersistedAsync(string sessionId)
    {
        try
        {
            var session = await _store.LoadAsync(sessionId);
            if (session is null)
            {
                ShowEmpty();
                return;
            }

            _workspace.OpenHistorySession(session);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The persisted session {SessionId} could not be loaded.", sessionId);
            ShowEmpty();
            ShowAction($"The session could not be loaded: {exception.Message}", isError: true);
        }
    }

    private void BindSession(RunSession session, bool rebuildOutput)
    {
        _session = session;
        _boundReadOnly = session.IsReadOnly ? session : null;
        _maxLogLines = Math.Clamp(
            _configuration.Current.Settings.Logging.MaxUiLines,
            100,
            1_000_000);
        OutputTabs.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;

        if (rebuildOutput)
        {
            _pendingBuildLines.Clear();
            _buildLines.Clear();
            lock (session)
            {
                foreach (var line in session.BuildOutput)
                {
                    _buildLines.Add(line);
                }
            }

            _allEntries.Clear();
            lock (session)
            {
                _allEntries.AddRange(session.LogEntries);
            }

            _latestCrash = null;
            ResetSearchState();
            RebuildLogRows();
            AttachLogcatSession();
        }

        UpdateHeader();
        UpdateTimeline();
        UpdateCrashBar();
        UpdateButtons();
        UpdateBuildErrors();
        UpdateLogStatus();
        _ = LoadSavedFiltersAsync();
    }

    private void ShowEmpty()
    {
        _session = null;
        _boundReadOnly = null;
        _latestCrash = null;
        DetachLogcatSession();
        OutputTabs.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        TitleText.Text = "Session";
        SubtitleText.Text = "Run a project or open a session from History.";
        StatusText.Text = string.Empty;
        StateText.Text = string.Empty;
        CrashBar.IsOpen = false;
        StepList.ItemsSource = null;
        _buildLines.Clear();
        _allEntries.Clear();
        _logRows.Clear();
        _searchRows.Clear();
        SearchCountText.Text = string.Empty;
        BuildErrorList.ItemsSource = null;
        UpdateButtons();
    }

    private void UpdateHeader()
    {
        if (_session is null)
        {
            return;
        }

        TitleText.Text = _session.IsDryRun
            ? $"{_session.ProjectName} (dry run)"
            : _session.ProjectName;
        var duration = _session.Duration is { } elapsed
            ? $" · {elapsed.TotalSeconds:F1}s"
            : string.Empty;
        SubtitleText.Text =
            $"{_session.ConfigurationName} · {_session.DeviceName ?? _session.DeviceSerial} · " +
            $"{_session.StartedAt.ToLocalTime():g}{duration}";
        StateText.Text = _session.IsDryRun
            ? $"{_session.State} · dry run"
            : _session.State.ToString();
        StateText.Foreground = BrushForState(_session.State);
        StatusText.Text = _session.StatusMessage ?? _session.Error ?? string.Empty;
    }

    private void UpdateTimeline()
    {
        if (_session is null)
        {
            return;
        }

        List<object> items;
        lock (_session)
        {
            items = [];
            var crashes = new Queue<CrashEvent>(_session.Crashes);
            foreach (var step in _session.Steps)
            {
                while (crashes.Count > 0 &&
                       (step.StartedAt is null || crashes.Peek().Timestamp <= step.StartedAt))
                {
                    items.Add(new CrashRow(crashes.Dequeue()));
                }

                items.Add(StepRow.From(step));
            }

            while (crashes.Count > 0)
            {
                items.Add(new CrashRow(crashes.Dequeue()));
            }
        }

        StepList.ItemsSource = items;
        NoStepsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCrashBar()
    {
        var crash = _latestCrash;
        if (crash is null && _session is not null)
        {
            lock (_session)
            {
                crash = _session.Crashes.LastOrDefault();
            }
        }

        _latestCrash = crash;
        if (crash is null)
        {
            CrashBar.IsOpen = false;
            CopyStackButton.IsEnabled = false;
            return;
        }

        CrashBar.Title = crash.Kind switch
        {
            CrashKind.Anr => "Application not responding",
            CrashKind.Native => "Native crash detected",
            _ => "Crash detected",
        };
        CrashBar.Message = string.IsNullOrWhiteSpace(crash.Summary)
            ? "A crash or ANR was detected for this session."
            : crash.Summary;
        CrashBar.IsOpen = true;
        CopyStackButton.IsEnabled = true;
    }

    private void UpdateButtons()
    {
        var active = _session is { IsReadOnly: false, IsFinished: false };
        StopButton.IsEnabled = active;
        CancelButton.IsEnabled = active;
        RestartButton.IsEnabled = _session is { IsReadOnly: false } &&
                                  !string.IsNullOrWhiteSpace(_session.PackageName);
        CopyStackButton.IsEnabled = _latestCrash is not null || _session?.Crashes.Count > 0;
        PauseLogsButton.IsEnabled = _session is { IsReadOnly: false } && _logcatSession is not null;
        PauseLogsButton.Content = _logcatSession?.IsPaused == true ? "Resume logs" : "Pause logs";
        MirrorButton.IsEnabled = CurrentDeviceSerial is { Length: > 0 };
        foreach (var button in PluginToolbarPanel.Children.OfType<Button>())
        {
            button.IsEnabled = CurrentDeviceSerial is { Length: > 0 };
        }
    }

    private string? CurrentDeviceSerial =>
        _session?.DeviceSerial is { Length: > 0 } serial ? serial : _workspace.DeviceSerial;

    private void RebuildPluginToolbar()
    {
        PluginToolbarPanel.Children.Clear();
        foreach (var entry in _plugins.Extensions.ToolbarActions)
        {
            var button = new Button
            {
                Content = $"{entry.Action.Title} · {entry.SourceLabel}",
                Tag = entry,
                IsEnabled = CurrentDeviceSerial is { Length: > 0 },
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(
                button,
                $"PluginToolbarAction_{entry.Action.Id}");
            ToolTipService.SetToolTip(
                button,
                entry.Action.ToolTip is { Length: > 0 } tooltip
                    ? $"{tooltip} (plugin: {entry.SourceLabel})"
                    : $"Plugin: {entry.SourceLabel}");
            button.Click += OnPluginToolbarClick;
            PluginToolbarPanel.Children.Add(button);
        }
    }

    private void OnPluginRegistryChanged(object? sender, EventArgs e) => Dispatch(RebuildPluginToolbar);

    private void OnMirrorClick(object sender, RoutedEventArgs e)
    {
        if (CurrentDeviceSerial is not { Length: > 0 } serial)
        {
            ShowAction("Select a device before opening the mirror.", isError: true);
            return;
        }

        _workspace.OpenDevice(serial);
        App.Instance.Navigate("devices/mirror");
    }

    private async void OnPluginToolbarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ToolbarActionEntry entry } ||
            CurrentDeviceSerial is not { Length: > 0 } serial)
        {
            return;
        }

        if (entry.Action.RequiresConfirmation &&
            !await ConfirmAsync(
                "Run plugin action?",
                $"{entry.Action.Title} from {entry.SourceLabel} changes device state. Run it on {_session?.DeviceName ?? serial}?"))
        {
            return;
        }

        var result = await _plugins.Invoker.InvokeToolbarActionAsync(
            entry.PluginId,
            entry.Action,
            new DeviceActionContext(serial, _session?.DeviceName));
        if (result.Succeeded)
        {
            ShowAction(
                $"Ran {entry.Action.Title} from {entry.SourceLabel} ({result.Duration.TotalMilliseconds:F0} ms).");
        }
        else
        {
            ShowAction($"{entry.Action.Title} from {entry.SourceLabel} failed: {result.Error}", isError: true);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void UpdateBuildErrors()
    {
        if (_session is null)
        {
            return;
        }

        List<BuildError> errors;
        lock (_session)
        {
            errors = _session.Steps.SelectMany(step => step.Errors).ToList();
        }

        if (errors.Count == 0)
        {
            errors = [.. BuildErrorParser.Parse(string.Join(Environment.NewLine, _buildLines), _session.ProjectRoot)];
        }

        var distinct = errors
            .GroupBy(error => $"{error.FilePath}|{error.Line}|{error.Column}|{error.Message}")
            .Select(group => group.First())
            .ToArray();
        BuildErrorList.ItemsSource = distinct.Select(error => new BuildErrorRow(error)).ToArray();
        BuildErrorSummaryText.Text = distinct.Length == 0
            ? string.Empty
            : $"{distinct.Length} compiler error(s). Select one to open its source location.";
        BuildErrorSummaryText.Visibility = distinct.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoBuildErrorsText.Visibility = distinct.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateLogStatus(int? droppedLines = null)
    {
        if (_session is null)
        {
            return;
        }

        var dropped = droppedLines ?? _logcatSession?.DroppedLines ?? 0;
        var paused = _logcatSession?.IsPaused == true;
        var error = _logcatSession?.LastError;
        var parts = new List<string>
        {
            $"{_logRows.Count:N0} of {_allEntries.Count:N0} lines",
        };
        if (dropped > 0)
        {
            parts.Add($"{dropped:N0} dropped");
        }

        if (paused)
        {
            parts.Add("paused");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            parts.Add(error);
        }

        LogStatusText.Text = string.Join(" · ", parts);
        PauseLogsButton.Content = paused ? "Resume logs" : "Pause logs";
    }

    private void AttachLogcatSession()
    {
        var session = _session?.LogcatSessionId is { } id ? _logcat.GetSession(id) : null;
        if (ReferenceEquals(session, _logcatSession))
        {
            return;
        }

        DetachLogcatSession();
        _logcatSession = session;
        if (session is not null)
        {
            session.EntriesAdded += OnLogcatEntriesAdded;
            session.CrashDetected += OnLogcatCrashDetected;
        }

        UpdateButtons();
    }

    private void DetachLogcatSession()
    {
        if (_logcatSession is null)
        {
            return;
        }

        _logcatSession.EntriesAdded -= OnLogcatEntriesAdded;
        _logcatSession.CrashDetected -= OnLogcatCrashDetected;
        _logcatSession = null;
    }

    private void OnSessionChanged(object? sender, RunSessionChangedEventArgs e)
    {
        Dispatch(() =>
        {
            if (_session is null || !string.Equals(e.Session.Id, _session.Id, StringComparison.Ordinal))
            {
                return;
            }

            _session = e.Session;
            AttachLogcatSession();
            UpdateHeader();
            UpdateTimeline();
            UpdateCrashBar();
            UpdateButtons();
            if (e.Session.IsFinished)
            {
                UpdateBuildErrors();
            }

            UpdateLogStatus();
        });
    }

    private void OnOutputReceived(object? sender, RunOutputEventArgs e)
    {
        if (_session is null ||
            _session.IsReadOnly ||
            !string.Equals(e.SessionId, _session.Id, StringComparison.Ordinal))
        {
            return;
        }

        _pendingBuildLines.Enqueue(e.Line);
    }

    private void OnBuildTimerTick(object? sender, object e)
    {
        if (_session is null || _session.IsReadOnly)
        {
            while (_pendingBuildLines.TryDequeue(out _))
            {
            }

            return;
        }

        var appended = 0;
        while (appended < 1_000 && _pendingBuildLines.TryDequeue(out var line))
        {
            _buildLines.Add(line);
            appended++;
        }

        if (_buildLines.Count > MaxBuildLines)
        {
            _buildLines.RemoveFromStart(_buildLines.Count - MaxBuildLines);
        }
    }

    private void OnLogcatEntriesAdded(object? sender, LogEntriesEventArgs e)
    {
        Dispatch(() =>
        {
            if (_session is null || _session.IsReadOnly)
            {
                return;
            }

            AddEntries(e.Entries);
            UpdateLogStatus(e.DroppedLines);
        });
    }

    private void OnLogcatCrashDetected(object? sender, CrashDetectedEventArgs e)
    {
        Dispatch(() =>
        {
            if (_session is null)
            {
                return;
            }

            _latestCrash = e.Crash;
            UpdateCrashBar();
            UpdateButtons();
        });
    }

    private void AddEntries(IReadOnlyList<LogEntry> entries)
    {
        List<LogRow>? added = null;
        foreach (var entry in entries)
        {
            _allEntries.Add(entry);
            if (_filter.Matches(entry))
            {
                (added ??= []).Add(LogRow.Create(entry, _highlightEngine));
            }
        }

        if (added is not null)
        {
            _logRows.AddRange(added);
        }

        if (_allEntries.Count > _maxLogLines)
        {
            _allEntries.RemoveRange(0, _allEntries.Count - _maxLogLines);
        }

        _logRows.RemoveFromStart(_logRows.Count - _maxLogLines);
        NoLogsText.Visibility = _logRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RebuildLogRows() => _ = RebuildLogRowsAsync();

    /// <summary>
    /// Filters the buffered entries off the UI thread and swaps the visible rows in
    /// one reset, so filtering a full buffer never blocks input or rendering.
    /// </summary>
    private async Task RebuildLogRowsAsync()
    {
        var generation = ++_logRebuildGeneration;
        var entries = _allEntries.ToArray();
        var snapshotCount = entries.Length;
        var filter = _filter;
        var engine = _highlightEngine;
        var rows = await Task.Run(() =>
        {
            var result = new List<LogRow>(entries.Length);
            foreach (var entry in entries)
            {
                if (filter.Matches(entry))
                {
                    result.Add(LogRow.Create(entry, engine));
                }
            }

            return result;
        }).ConfigureAwait(true);

        if (_disposed || generation != _logRebuildGeneration)
        {
            return;
        }

        // Include entries appended between the snapshot and the swap so no line
        // is lost from view while a rebuild was in flight.
        if (_allEntries.Count > snapshotCount)
        {
            var appended = new List<LogRow>(_allEntries.Count - snapshotCount);
            for (var index = snapshotCount; index < _allEntries.Count; index++)
            {
                if (filter.Matches(_allEntries[index]))
                {
                    appended.Add(LogRow.Create(_allEntries[index], engine));
                }
            }

            rows.AddRange(appended);
        }

        _logRows.ResetAll(rows);
        NoLogsText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetSearchState();
        UpdateLogStatus();
    }

    private void ResetHighlightRules()
    {
        _highlightRules.Clear();
        foreach (var rule in HighlightRule.BuiltIn)
        {
            _highlightRules.Add(new HighlightRuleRow(rule));
        }

        RebuildHighlightEngine();
        HighlightList.ItemsSource = _highlightRules;
    }

    private void RebuildHighlightEngine() =>
        _highlightEngine = new HighlightEngine(_highlightRules.Select(row => row.Rule));

    private void OnHighlightToggled(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HighlightRuleRow row)
        {
            return;
        }

        row.Rule.IsEnabled = (sender as CheckBox)?.IsChecked == true;
        RebuildHighlightEngine();
        RebuildLogRows();
    }

    private LogFilterCriteria BuildCriteria()
    {
        var pid = int.TryParse(
            PidFilterBox.Text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsedPid)
            ? parsedPid
            : (int?)null;
        return new LogFilterCriteria
        {
            MinimumLevel = (LevelCombo.SelectedItem as LogLevelChoice)?.Value,
            Tags = NullIfEmpty(TagFilterBox.Text),
            Pid = pid,
            PackageName = NullIfEmpty(PackageFilterBox.Text),
            Text = NullIfEmpty(TextFilterBox.Text),
            UseRegex = RegexFilterCheck.IsChecked == true,
        };
    }

    private void SetFilterControls(LogFilterCriteria criteria)
    {
        _initializingFilters = true;
        try
        {
            var choices = LevelCombo.ItemsSource as IEnumerable<LogLevelChoice>;
            LevelCombo.SelectedItem = choices?.FirstOrDefault(choice =>
                string.Equals(choice.Value ?? string.Empty, criteria.MinimumLevel ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? choices?.FirstOrDefault();
            TagFilterBox.Text = criteria.Tags ?? string.Empty;
            PidFilterBox.Text = criteria.Pid?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            PackageFilterBox.Text = criteria.PackageName ?? string.Empty;
            TextFilterBox.Text = criteria.Text ?? string.Empty;
            RegexFilterCheck.IsChecked = criteria.UseRegex;
        }
        finally
        {
            _initializingFilters = false;
        }
    }

    private void OnApplyFilterClick(object sender, RoutedEventArgs e)
    {
        _filter = new LogFilter(BuildCriteria());
        RebuildLogRows();
        ShowAction("The log filter was applied.");
    }

    private void OnClearFilterClick(object sender, RoutedEventArgs e)
    {
        ClearFilter();
        ShowAction("The log filter was cleared.");
    }

    private void ClearFilter()
    {
        SetFilterControls(new LogFilterCriteria());
        SavedFilterCombo.SelectedItem = null;
        _filter = new LogFilter(new LogFilterCriteria());
        RebuildLogRows();
    }

    private async Task LoadSavedFiltersAsync()
    {
        if (_session is null || string.IsNullOrWhiteSpace(_session.ProjectId))
        {
            SavedFilterCombo.ItemsSource = null;
            return;
        }

        var projectId = _session.ProjectId;
        try
        {
            var filters = await _filters.ListAsync(projectId);
            if (_session is null || !string.Equals(_session.ProjectId, projectId, StringComparison.Ordinal))
            {
                return;
            }

            _initializingFilters = true;
            try
            {
                SavedFilterCombo.ItemsSource = filters;
                SavedFilterCombo.SelectedItem = null;
            }
            finally
            {
                _initializingFilters = false;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Saved log filters could not be loaded.");
        }
    }

    private void OnSavedFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingFilters || SavedFilterCombo.SelectedItem is not SavedLogFilter filter)
        {
            return;
        }

        SetFilterControls(filter.Criteria);
        _filter = new LogFilter(filter.Criteria);
        RebuildLogRows();
        ShowAction($"Applied saved filter \"{filter.Name}\".");
    }

    private async void OnSaveFilterClick(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var nameBox = new TextBox
        {
            Text = $"Filter {DateTime.Now:HH:mm}",
            PlaceholderText = "Filter name",
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Save log filter",
            Content = nameBox,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return;
        }

        try
        {
            await _filters.SaveAsync(new SavedLogFilter
            {
                Name = nameBox.Text.Trim(),
                ProjectId = _session.ProjectId,
                Criteria = BuildCriteria(),
            });
            await LoadSavedFiltersAsync();
            ShowAction($"Saved filter \"{nameBox.Text.Trim()}\".");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The log filter could not be saved.");
            ShowAction($"The filter could not be saved: {exception.Message}", isError: true);
        }
    }

    private async void OnDeleteFilterClick(object sender, RoutedEventArgs e)
    {
        if (SavedFilterCombo.SelectedItem is not SavedLogFilter filter)
        {
            ShowAction("Select a saved filter to delete.", isError: true);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete saved filter?",
            Content = $"Delete \"{filter.Name}\"?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _filters.DeleteAsync(filter.ProjectId, filter.Id);
            await LoadSavedFiltersAsync();
            ShowAction($"Deleted filter \"{filter.Name}\".");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The log filter could not be deleted.");
            ShowAction($"The filter could not be deleted: {exception.Message}", isError: true);
        }
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        ResetSearchState();
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query) || _logRows.Count == 0)
        {
            SearchCountText.Text = string.IsNullOrWhiteSpace(query) ? string.Empty : "No matches";
            return;
        }

        var entries = _logRows.Select(row => row.Entry).ToArray();
        var matches = LogFilter.Search(entries, query);
        if (matches.Count == 0)
        {
            SearchCountText.Text = "No matches";
            return;
        }

        var matchSet = new HashSet<LogEntry>(matches.Select(index => entries[index]));
        foreach (var row in _logRows)
        {
            row.IsSearchMatch = matchSet.Contains(row.Entry);
        }

        _searchRows.Clear();
        foreach (var index in matches)
        {
            _searchRows.Add(_logRows[index]);
        }

        _searchIndex = 0;
        SelectSearchRow();
    }

    private void OnSearchNextClick(object sender, RoutedEventArgs e) => MoveSearch(1);

    private void OnSearchPreviousClick(object sender, RoutedEventArgs e) => MoveSearch(-1);

    private void MoveSearch(int direction)
    {
        if (_searchRows.Count == 0)
        {
            OnSearchClick(sender: this, new RoutedEventArgs());
            if (_searchRows.Count == 0)
            {
                return;
            }
        }

        _searchIndex = (_searchIndex + direction + _searchRows.Count) % _searchRows.Count;
        SelectSearchRow();
    }

    private void SelectSearchRow()
    {
        if (_searchIndex < 0 || _searchIndex >= _searchRows.Count)
        {
            return;
        }

        var row = _searchRows[_searchIndex];
        LogList.SelectedItem = row;
        LogList.ScrollIntoView(row);
        SearchCountText.Text = $"{_searchIndex + 1} of {_searchRows.Count}";
    }

    private void ResetSearchState()
    {
        foreach (var row in _logRows)
        {
            row.IsSearchMatch = false;
        }

        _searchRows.Clear();
        _searchIndex = -1;
        SearchCountText.Text = string.Empty;
    }

    private void OnPauseLogsClick(object sender, RoutedEventArgs e)
    {
        if (_logcatSession is null)
        {
            ShowAction("No live logcat stream is attached to this session.", isError: true);
            return;
        }

        if (_logcatSession.IsPaused)
        {
            _logcatSession.Resume();
            ShowAction("Log delivery resumed.");
        }
        else
        {
            _logcatSession.Pause();
            ShowAction("Log delivery paused. Lines continue to buffer on the device.");
        }

        UpdateButtons();
        UpdateLogStatus();
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _session.IsReadOnly)
        {
            return;
        }

        try
        {
            await _runs.StopAsync(_session.Id);
            ShowAction("The session was stopped and logs were detached.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The session could not be stopped.");
            ShowAction($"The session could not be stopped: {exception.Message}", isError: true);
        }
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _session.IsReadOnly)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Cancel this run?",
            Content = "Running build, install, and custom step processes are terminated.",
            PrimaryButtonText = "Cancel run",
            CloseButtonText = "Keep running",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _runs.CancelAsync(_session.Id);
            ShowAction("The run was cancelled and its processes were terminated.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The session could not be cancelled.");
            ShowAction($"The session could not be cancelled: {exception.Message}", isError: true);
        }
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _session.IsReadOnly)
        {
            return;
        }

        try
        {
            await _runs.RestartAppAsync(_session.Id);
            ShowAction("The application was restarted.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The application could not be restarted.");
            ShowAction($"The application could not be restarted: {exception.Message}", isError: true);
        }
    }

    private async void OnRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (_session is null || _session.IsReadOnly)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is not RecoveryRow row)
        {
            return;
        }

        try
        {
            await _runs.ApplyRecoveryAsync(_session.Id, row.Kind);
            ShowAction($"Applied recovery action: {row.Label}.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Recovery action {Action} failed.", row.Kind);
            ShowAction($"The recovery action failed: {exception.Message}", isError: true);
        }
    }

    private void OnCopyStackClick(object sender, RoutedEventArgs e)
    {
        var crash = _latestCrash;
        if (crash is null && _session is not null)
        {
            lock (_session)
            {
                crash = _session.Crashes.LastOrDefault();
            }
        }

        if (crash is null)
        {
            ShowAction("No crash stack is available for this session.", isError: true);
            return;
        }

        CopyText(crash.StackTrace, "Crash stack copied.");
    }

    private void OnJumpToCrashClick(object sender, RoutedEventArgs e)
    {
        var crash = _latestCrash;
        if (crash is null || _session is null)
        {
            return;
        }

        OutputTabs.SelectedIndex = 1;
        var row = _logRows.FirstOrDefault(candidate =>
            candidate.Entry.Sequence >= crash.FirstSequence &&
            candidate.Entry.Sequence <= crash.LastSequence);
        if (row is null && !_filter.Criteria.IsEmpty)
        {
            ClearFilter();
            row = _logRows.FirstOrDefault(candidate =>
                candidate.Entry.Sequence >= crash.FirstSequence &&
                candidate.Entry.Sequence <= crash.LastSequence);
        }

        if (row is null)
        {
            ShowAction("The crash lines are not present in the current log buffer.", isError: true);
            return;
        }

        LogList.SelectedItem = row;
        LogList.ScrollIntoView(row);
    }

    private void OnBuildErrorClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BuildErrorRow row)
        {
            return;
        }

        var error = row.Error;
        if (!File.Exists(error.FilePath))
        {
            ShowAction($"The source file was not found: {error.FilePath}", isError: true);
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"--goto \"{error.FilePath}:{error.Line}:{error.Column}\"",
                UseShellExecute = true,
            });
            return;
        }
        catch (Win32Exception)
        {
            // Visual Studio Code is not installed; fall back to the file association.
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = error.FilePath,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "The build error source file could not be opened.");
            ShowAction($"The source file could not be opened: {error.FilePath}", isError: true);
        }
    }

    private void OnOpenProjectsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("projects");

    private void OnWorkspaceChanged(object? sender, EventArgs e)
    {
        Dispatch(() =>
        {
            if (!string.Equals(_workspace.SessionId, _session?.Id, StringComparison.Ordinal) ||
                !ReferenceEquals(_workspace.ReadOnlySession, _boundReadOnly))
            {
                Refresh();
            }
        });
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
            _logger.LogWarning(exception, "Text could not be copied from the session page.");
            ShowAction("The text could not be copied to the clipboard.", isError: true);
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

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Brush BrushForState(RunSessionState state)
    {
        var key = state switch
        {
            RunSessionState.Succeeded => "SystemFillColorSuccessBrush",
            RunSessionState.Failed or RunSessionState.PartiallyFailed => "SystemFillColorCriticalBrush",
            RunSessionState.Cancelled or RunSessionState.Stopping => "SystemFillColorCautionBrush",
            RunSessionState.Running => "AccentTextFillColorPrimaryBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        return ResolveBrush(key);
    }

    private static Brush ResolveBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    /// <summary>One log level choice.</summary>
    public sealed class LogLevelChoice
    {
        /// <summary>Creates a level choice.</summary>
        public LogLevelChoice(string? value, string label)
        {
            Value = value;
            Label = label;
        }

        /// <summary>Gets the level letter, or <see langword="null"/> for all levels.</summary>
        public string? Value { get; }

        /// <summary>Gets the display label.</summary>
        public string Label { get; }
    }

    /// <summary>One log line shaped for the log list.</summary>
    public sealed class LogRow : INotifyPropertyChanged
    {
        private static readonly Brush ErrorBrush = ResolveBrush("SystemFillColorCriticalBrush");
        private static readonly Brush WarningBrush = ResolveBrush("SystemFillColorCautionBrush");
        private static readonly Brush SecondaryBrush = ResolveBrush("TextFillColorSecondaryBrush");
        private static readonly Brush PrimaryBrush = ResolveBrush("TextFillColorPrimaryBrush");
        private static readonly Brush SearchBrush = new SolidColorBrush(
            Windows.UI.Color.FromArgb(64, 255, 193, 7));
        private static readonly Dictionary<string, Brush> RuleBrushCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Brush? _ruleBackground;
        private bool _isSearchMatch;

        private LogRow(LogEntry entry, HighlightRule? rule)
        {
            Entry = entry;
            Text = entry.IsContinuation ? $"    {entry.Raw}" : entry.Raw;
            Foreground = rule is { } highlight
                ? BrushFor(highlight.Foreground, BrushForLevel(entry.Level))
                : BrushForLevel(entry.Level);
            _ruleBackground = rule?.Background is { } background ? BrushFor(background, null) : null;
            Weight = rule?.Bold == true || entry.Level is "F" or "A"
                ? Microsoft.UI.Text.FontWeights.Bold
                : Microsoft.UI.Text.FontWeights.Normal;
        }

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Gets the parsed entry.</summary>
        public LogEntry Entry { get; }

        /// <summary>Gets the display text.</summary>
        public string Text { get; }

        /// <summary>Gets the level foreground.</summary>
        public Brush Foreground { get; }

        /// <summary>Gets the level font weight.</summary>
        public FontWeight Weight { get; }

        /// <summary>Gets the highlight or search background; the search marker wins.</summary>
        public Brush? Background => _isSearchMatch ? SearchBrush : _ruleBackground;

        /// <summary>Gets or sets whether the line matches the current search.</summary>
        public bool IsSearchMatch
        {
            get => _isSearchMatch;
            set
            {
                if (_isSearchMatch == value)
                {
                    return;
                }

                _isSearchMatch = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Background)));
            }
        }

        /// <summary>Creates a row from a log entry, applying the first matching highlight rule.</summary>
        public static LogRow Create(LogEntry entry, HighlightEngine? highlights = null)
        {
            ArgumentNullException.ThrowIfNull(entry);
            return new LogRow(entry, highlights?.Match(entry));
        }

        private static Brush BrushForLevel(string? level) => level switch
        {
            "E" or "F" or "A" => ErrorBrush,
            "W" => WarningBrush,
            "D" or "V" => SecondaryBrush,
            _ => PrimaryBrush,
        };

        private static Brush BrushFor(string color, Brush? fallback)
        {
            lock (RuleBrushCache)
            {
                if (RuleBrushCache.TryGetValue(color, out var cached))
                {
                    return cached;
                }
            }

            Brush? parsed = null;
            var hex = color.TrimStart('#');
            if (hex.Length is 6 or 8)
            {
                try
                {
                    var value = byte.Parse(hex[^6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var alpha = hex.Length == 8
                        ? byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                        : (byte)255;
                    parsed = new SolidColorBrush(Windows.UI.Color.FromArgb(
                        alpha,
                        (byte)(value >> 16),
                        (byte)(value >> 8),
                        (byte)value));
                }
                catch (FormatException)
                {
                    // A malformed color string falls back to the level color.
                }
                catch (OverflowException)
                {
                    // A malformed color string falls back to the level color.
                }
            }

            var brush = parsed ?? fallback ?? PrimaryBrush;
            lock (RuleBrushCache)
            {
                RuleBrushCache[color] = brush;
            }

            return brush;
        }
    }

    /// <summary>One timeline step shaped for the step list.</summary>
    public sealed class StepRow
    {
        private StepRow(RunStep step)
        {
            Name = step.Name;
            StateLabel = step.State.ToString();
            StateBrush = step.State switch
            {
                RunStepState.Succeeded => ResolveBrush("SystemFillColorSuccessBrush"),
                RunStepState.Failed => ResolveBrush("SystemFillColorCriticalBrush"),
                RunStepState.Cancelled or RunStepState.Skipped => ResolveBrush("SystemFillColorCautionBrush"),
                RunStepState.Running => ResolveBrush("AccentTextFillColorPrimaryBrush"),
                _ => ResolveBrush("TextFillColorSecondaryBrush"),
            };
            DurationLabel = step.Duration is { } duration ? $"{duration.TotalSeconds:F1}s" : string.Empty;
            CommandLine = step.CommandLine ?? string.Empty;
            HasCommand = CommandLine.Length > 0;
            Error = step.Error ?? string.Empty;
            HasError = Error.Length > 0;
            SkipReason = step.SkipReason ?? string.Empty;
            HasSkipReason = SkipReason.Length > 0;
            DiagnosisText = step.Diagnosis is { } diagnosis
                ? $"{diagnosis.Title}: {diagnosis.Explanation} {diagnosis.Guidance}".Trim()
                : string.Empty;
            HasDiagnosis = DiagnosisText.Length > 0;
            RecoveryRows = step.RecoveryActions
                .Select(action => new RecoveryRow(action))
                .ToArray();
            HasRecovery = RecoveryRows.Count > 0;
        }

        /// <summary>Gets the step name.</summary>
        public string Name { get; }

        /// <summary>Gets the state label.</summary>
        public string StateLabel { get; }

        /// <summary>Gets the state brush.</summary>
        public Brush StateBrush { get; }

        /// <summary>Gets the duration label.</summary>
        public string DurationLabel { get; }

        /// <summary>Gets the exact command line.</summary>
        public string CommandLine { get; }

        /// <summary>Gets whether a command line is available.</summary>
        public bool HasCommand { get; }

        /// <summary>Gets the failure message.</summary>
        public string Error { get; }

        /// <summary>Gets whether the step failed.</summary>
        public bool HasError { get; }

        /// <summary>Gets the reason the step was skipped.</summary>
        public string SkipReason { get; }

        /// <summary>Gets whether a skip reason is available.</summary>
        public bool HasSkipReason { get; }

        /// <summary>Gets the install diagnosis.</summary>
        public string DiagnosisText { get; }

        /// <summary>Gets whether a diagnosis is available.</summary>
        public bool HasDiagnosis { get; }

        /// <summary>Gets the recovery actions.</summary>
        public IReadOnlyList<RecoveryRow> RecoveryRows { get; }

        /// <summary>Gets whether recovery actions are available.</summary>
        public bool HasRecovery { get; }

        /// <summary>Creates a row from a run step.</summary>
        public static StepRow From(RunStep step)
        {
            ArgumentNullException.ThrowIfNull(step);
            return new StepRow(step);
        }
    }

    /// <summary>One crash or ANR event shaped for the timeline.</summary>
    public sealed class CrashRow
    {
        /// <summary>Creates a row from a crash event.</summary>
        public CrashRow(CrashEvent crash)
        {
            ArgumentNullException.ThrowIfNull(crash);
            KindLabel = crash.Kind switch
            {
                CrashKind.Anr => "Application not responding",
                CrashKind.Native => "Native crash",
                _ => "Crash",
            };
            Summary = crash.Summary;
            TimeLabel = crash.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        }

        /// <summary>Gets the event kind label.</summary>
        public string KindLabel { get; }

        /// <summary>Gets the short summary.</summary>
        public string Summary { get; }

        /// <summary>Gets the local timestamp label.</summary>
        public string TimeLabel { get; }
    }

    /// <summary>One toggleable highlight rule shown in the log panel.</summary>
    public sealed class HighlightRuleRow
    {
        /// <summary>Creates a row wrapping a highlight rule.</summary>
        public HighlightRuleRow(HighlightRule rule)
        {
            Rule = rule ?? throw new ArgumentNullException(nameof(rule));
            Label = string.IsNullOrWhiteSpace(rule.Name) ? rule.Pattern : rule.Name;
            AutomationId = $"HighlightRule_{rule.Id}";
        }

        /// <summary>Gets the wrapped rule.</summary>
        public HighlightRule Rule { get; }

        /// <summary>Gets the display label.</summary>
        public string Label { get; }

        /// <summary>Gets whether the rule is enabled.</summary>
        public bool IsEnabled => Rule.IsEnabled;

        /// <summary>Gets the pattern shown as a tooltip.</summary>
        public string Pattern => Rule.Pattern;

        /// <summary>Gets the automation identifier.</summary>
        public string AutomationId { get; }
    }

    /// <summary>One recovery action shaped for the timeline.</summary>
    public sealed class RecoveryRow
    {
        /// <summary>Creates a recovery row.</summary>
        public RecoveryRow(RecoveryAction action)
        {
            ArgumentNullException.ThrowIfNull(action);
            Kind = action.Kind;
            Label = action.Label;
            Description = action.Description;
            AutomationId = $"Recovery_{action.Kind}_{action.Id}";
        }

        /// <summary>Gets the recovery kind.</summary>
        public RecoveryActionKind Kind { get; }

        /// <summary>Gets the button label.</summary>
        public string Label { get; }

        /// <summary>Gets the explanation.</summary>
        public string Description { get; }

        /// <summary>Gets the automation identifier.</summary>
        public string AutomationId { get; }
    }

    /// <summary>One clickable build error.</summary>
    public sealed class BuildErrorRow
    {
        /// <summary>Creates a build error row.</summary>
        public BuildErrorRow(BuildError error)
        {
            Error = error ?? throw new ArgumentNullException(nameof(error));
            Display = error.Display;
        }

        /// <summary>Gets the parsed error.</summary>
        public BuildError Error { get; }

        /// <summary>Gets the display label.</summary>
        public string Display { get; }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DroidDeck.App.Services;
using DroidDeck.App.ViewModels;
using DroidDeck.Core;
using DroidDeck.Core.Commands;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Sessions;
using DroidDeck.Core.Toolchain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

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
    private readonly ICommandRegistry _commands;
    private readonly KeybindingMap _keybindings;
    private readonly ILogger<SettingsPage> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly ConfigurationHealthService _health;
    private readonly ToolPathRow[] _toolPaths = ToolPathRow.CreateAll();
    private SettingsResetSnapshot? _lastReset;
    private bool _initializing;
    private bool _uiReady;
    private bool _disposed;

    /// <summary>Creates the settings page.</summary>
    public SettingsPage(
        IConfigurationService configuration,
        IToolchainService toolchain,
        IDiagnosticsService diagnostics,
        ISessionStore sessions,
        PluginHost plugins,
        AppPaths paths,
        ICommandRegistry commands,
        KeybindingMap keybindings,
        ILogger<SettingsPage> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _keybindings = keybindings ?? throw new ArgumentNullException(nameof(keybindings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _health = new ConfigurationHealthService(configuration, toolchain);

        InitializeComponent();
        InitializeSections();
        ThemeCombo.ItemsSource = Enum.GetValues<AppTheme>();
        ToolPathsList.ItemsSource = _toolPaths;
        _uiReady = true;
        _toolchain.SnapshotChanged += OnToolchainSnapshotChanged;
        _commands.Changed += OnCommandsChanged;
        _keybindings.Changed += OnKeybindingsChanged;
        _configuration.Changed += OnConfigurationChanged;
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
        _uiReady = false;
        _toolchain.SnapshotChanged -= OnToolchainSnapshotChanged;
        _commands.Changed -= OnCommandsChanged;
        _keybindings.Changed -= OnKeybindingsChanged;
        _configuration.Changed -= OnConfigurationChanged;
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
            PerformanceIntervalBox.Value = settings.Performance.SampleIntervalMs;
            PerformanceSamplesBox.Value = settings.Performance.MaxSamples;
            InspectorDepthBox.Value = settings.Inspector.EffectiveMaxDepth;
            InspectorInvisibleToggle.IsOn = settings.Inspector.IncludeInvisible;
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
        RefreshShortcutList();
        UpdateSearchResults();
        _ = RefreshHealthAsync();
    }

    private void OnCommandsChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(RefreshShortcutList);

    private void OnKeybindingsChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(RefreshShortcutList);

    private void RefreshShortcutList()
    {
        ShortcutList.ItemsSource = _commands.Commands
            .Select(command => new ShortcutRow(
                command.Id,
                command.Title,
                command.Category,
                _keybindings.GetEffectiveShortcut(command)?.ToString() ?? "Not set",
                _keybindings.GetOverride(command.Id) is not null))
            .ToArray();
    }

    private void OnConfigurationChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        _dispatcher.TryEnqueue(UpdateSearchResults);
    }

    private void InitializeSections()
    {
        var rows = new List<SettingsSectionRow> { new(null, "All sections") };
        rows.AddRange(SettingsCatalog.Default.Entries
            .GroupBy(static entry => entry.Section, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new SettingsSectionRow(group.Key, group.First().Category)));
        ResetSectionCombo.ItemsSource = rows;
        ResetSectionCombo.SelectedIndex = 0;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateSearchResults();
    }

    private void OnSearchLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateSearchResults();
    }

    private void UpdateSearchResults()
    {
        if (!_uiReady || _disposed)
        {
            return;
        }

        var level = SelectedSearchLevel();
        var text = SearchBox.Text;
        var matches = SettingsCatalog.Default.Search(
            new SettingsSearchQuery(text, MaxLevel: level),
            _configuration.Current);
        var snapshot = _configuration.Current;
        SearchResultsList.ItemsSource = matches.Take(50).Select(entry => CreateSearchRow(entry, snapshot)).ToArray();

        var searching = !string.IsNullOrWhiteSpace(text);
        SearchStatusText.Text = searching
            ? $"{matches.Count} setting(s) match \"{text.Trim()}\" at {level} level. Searches include names, descriptions, paths, and current values."
            : $"{matches.Count} setting(s) available at {level} level. Type to search by name, description, path, or current value.";
        NoSearchResultsText.Visibility = searching && matches.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static SearchResultRow CreateSearchRow(SettingsCatalogEntry entry, ConfigSnapshot snapshot)
    {
        var source = snapshot.GetSource(entry.Path);
        return new SearchResultRow(
            entry,
            FormatNode(snapshot.GetValue(entry.Path)),
            $"Source: {source.DisplayName}",
            source.Scope is not (ConfigScope.BuiltInDefaults or ConfigScope.Device),
            source.Scope);
    }

    private async void OnResetSearchSettingClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SearchResultRow row || !row.CanReset)
        {
            return;
        }

        try
        {
            await _configuration.ResetAsync(row.Entry.Path, row.ResetScope);
            ShowAction($"'{row.Entry.Label}' was reset in the {DisplayScope(row.ResetScope)} layer.");
            Refresh();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure($"Resetting '{row.Entry.Label}'", exception);
        }
    }

    private ConfigScope SelectedTransferScope() =>
        Enum.TryParse<ConfigScope>(
            (TransferScopeCombo.SelectedItem as ComboBoxItem)?.Tag as string,
            out var scope)
            ? scope
            : ConfigScope.Global;

    private SettingsLevel SelectedSearchLevel() =>
        Enum.TryParse<SettingsLevel>(
            (SearchLevelCombo.SelectedItem as ComboBoxItem)?.Tag as string,
            out var level)
            ? level
            : SettingsLevel.Expert;

    private async void OnResetSettingsClick(object sender, RoutedEventArgs e)
    {
        var scope = SelectedTransferScope();
        var sectionRow = ResetSectionCombo.SelectedItem as SettingsSectionRow;
        var section = sectionRow?.Section;
        var target = section is null
            ? $"every setting in the {DisplayScope(scope)} layer"
            : $"the {sectionRow?.Name ?? section} section in the {DisplayScope(scope)} layer";
        if (await ConfirmApplyAsync("Reset settings", $"Remove {target}? You can undo this until the page is closed.") !=
            ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            _lastReset = section is null
                ? await _configuration.ResetScopeAsync(scope)
                : await _configuration.ResetSectionAsync(section, scope);
            UndoResetButton.IsEnabled = _lastReset.Removed.Count > 0;
            TransferStatusText.Text = _lastReset.Removed.Count == 0
                ? "That layer did not override anything, so nothing changed."
                : $"Reset {_lastReset.Removed.Count} value(s) in the {DisplayScope(scope)} layer.";
            ShowAction(_lastReset.Removed.Count == 0 ? "Nothing needed resetting." : "Settings were reset.");
            Refresh();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or NotSupportedException)
        {
            ReportFailure($"Resetting the {DisplayScope(scope)} layer", exception);
        }
    }

    private async void OnUndoResetClick(object sender, RoutedEventArgs e)
    {
        if (_lastReset is not { } snapshot)
        {
            return;
        }

        try
        {
            await _configuration.RestoreSettingsAsync(snapshot);
            _lastReset = null;
            UndoResetButton.IsEnabled = false;
            TransferStatusText.Text =
                $"Restored {snapshot.Removed.Count} value(s) in the {DisplayScope(snapshot.Scope)} layer.";
            ShowAction("The reset was undone.");
            Refresh();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or SettingsValidationException)
        {
            ReportFailure("Undoing the reset", exception);
        }
    }

    private async void OnEditJsonClick(object sender, RoutedEventArgs e)
    {
        var scope = SelectedTransferScope();
        try
        {
            var export = await _configuration.ExportSettingsAsync(
                new SettingsExportOptions { Scopes = [scope] });
            var document = export.Scopes.TryGetValue(scope, out var layer)
                ? layer
                : new JsonObject();
            var editor = new TextBox
            {
                Text = document.ToJsonString(DroidDeckJson.Options),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                MinWidth = 480,
                Height = 360,
            };
            var dialog = new ContentDialog
            {
                Title = $"Edit {DisplayScope(scope)} settings as JSON",
                Content = new ScrollViewer
                {
                    Content = editor,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = "Review changes",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            {
                return;
            }

            await ReviewAndImportAsync(editor.Text, scope, SettingsImportMode.Replace);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or NotSupportedException)
        {
            ReportFailure($"Opening the {DisplayScope(scope)} layer", exception);
        }
    }

    private async void OnImportSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".json");
            InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var json = await File.ReadAllTextAsync(file.Path);
            await ReviewAndImportAsync(
                json,
                SelectedTransferScope(),
                ImportReplaceToggle.IsOn ? SettingsImportMode.Replace : SettingsImportMode.Merge);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            ReportFailure("Reading the settings file", exception);
        }
    }

    private async void OnExportSettingsClick(object sender, RoutedEventArgs e)
    {
        var scope = SelectedTransferScope();
        try
        {
            var export = await _configuration.ExportSettingsAsync(
                new SettingsExportOptions { Scopes = [scope] });
            if (export.Scopes.Count == 0)
            {
                ShowAction("That layer has no settings to export.", isError: true);
                return;
            }

            var fileName = DisplayScope(scope).Replace(' ', '-');
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"droiddeck-settings-{fileName}-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultFileExtension = ".json",
            };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await File.WriteAllTextAsync(file.Path, export.ToJson());
            TransferStatusText.Text = export.ExcludedPaths.Count == 0
                ? $"Exported the {DisplayScope(scope)} layer to {file.Path}."
                : $"Exported the {DisplayScope(scope)} layer to {file.Path}. " +
                    $"{export.ExcludedPaths.Count} secret value(s) were omitted: " +
                    string.Join(", ", export.ExcludedPaths) + ".";
            ShowAction("Settings exported.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                COMException or InvalidOperationException or NotSupportedException)
        {
            ReportFailure("Exporting settings", exception);
        }
    }

    private async Task ReviewAndImportAsync(string json, ConfigScope scope, SettingsImportMode mode)
    {
        SettingsImportPreview preview;
        try
        {
            preview = await _configuration.PreviewSettingsImportAsync(json, scope, mode);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or NotSupportedException)
        {
            ReportFailure("Reading the settings document", exception);
            return;
        }

        var errors = preview.Validation.Issues
            .Where(static issue => issue.Severity == SettingsIssueSeverity.Error)
            .ToArray();
        if (errors.Length > 0)
        {
            await ShowMessageAsync("Settings cannot be imported", FormatIssues(errors));
            return;
        }

        if (!preview.HasChanges)
        {
            await ShowMessageAsync(
                "Nothing to import",
                $"The document matches the existing {DisplayScope(scope)} layer, so no values would change.");
            return;
        }

        if (await ConfirmApplyAsync("Apply settings changes?", BuildPreviewText(preview)) !=
            ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var result = await _configuration.ImportSettingsAsync(json, scope, mode);
            TransferStatusText.Text = result.BackupPath is null
                ? $"Imported {result.Preview.Changes.Count} change(s) into the {DisplayScope(scope)} layer."
                : $"Imported {result.Preview.Changes.Count} change(s) into the {DisplayScope(scope)} layer. " +
                    $"Previous values were backed up to {result.BackupPath}.";
            ShowAction($"Settings imported into the {DisplayScope(scope)} layer.");
            Refresh();
        }
        catch (SettingsValidationException exception)
        {
            await ShowMessageAsync("Settings cannot be imported", FormatIssues(exception.Result.Issues));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidOperationException or NotSupportedException)
        {
            ReportFailure("Importing settings", exception);
        }
    }

    private static string BuildPreviewText(SettingsImportPreview preview)
    {
        var lines = new List<string>
        {
            $"{preview.Changes.Count} change(s) to the {DisplayScope(preview.Scope)} layer ({preview.Mode}):",
            string.Empty,
        };
        lines.AddRange(preview.Changes.Take(20).Select(DescribeChange));
        if (preview.Changes.Count > 20)
        {
            lines.Add($"…and {preview.Changes.Count - 20} more.");
        }

        var warnings = preview.Validation.Issues
            .Where(static issue => issue.Severity == SettingsIssueSeverity.Warning)
            .ToArray();
        if (warnings.Length > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Warnings:");
            lines.AddRange(warnings.Take(5).Select(static issue =>
                (issue.Path.Length == 0 ? "(document)" : issue.Path) + ": " + issue.Message));
        }

        if (preview.ExcludedPaths.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(
                $"{preview.ExcludedPaths.Count} secret value(s) are not part of the import and keep their existing values: " +
                string.Join(", ", preview.ExcludedPaths) + ".");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DescribeChange(SettingsChange change) => change.Kind switch
    {
        SettingsChangeKind.Added => $"+ {change.Path} = {FormatNode(change.NewValue)}",
        SettingsChangeKind.Changed =>
            $"~ {change.Path}: {FormatNode(change.OldValue)} → {FormatNode(change.NewValue)}",
        SettingsChangeKind.Removed => $"- {change.Path} (was {FormatNode(change.OldValue)})",
        _ => change.Path,
    };

    private static string FormatIssues(IEnumerable<SettingsValidationIssue> issues) =>
        string.Join(Environment.NewLine, issues.Select(static issue =>
            (issue.Path.Length == 0 ? "(document)" : issue.Path) + ": " + issue.Message));

    private async Task RefreshHealthAsync()
    {
        if (!_uiReady || _disposed)
        {
            return;
        }

        try
        {
            var report = await _health.AnalyzeAsync();
            var problems = report.Issues.Count(static issue => issue.Severity != SettingsHealthSeverity.Info);
            HealthSummaryText.Text = report.Issues.Count == 0
                ? "No configuration problems were found."
                : problems == 0
                    ? $"No warnings or errors. {report.Issues.Count} informational finding(s)."
                    : $"{problems} problem(s) need attention.";
            HealthList.ItemsSource = report.Issues
                .Select(issue => new SettingsHealthRow(issue, ResolveSeverityBrush(issue.Severity)))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Checking settings health", exception);
        }
    }

    private async void OnHealthFixClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SettingsHealthRow row)
        {
            return;
        }

        if (row.Issue.Fix.Kind == SettingsFixKind.OpenDataDirectory)
        {
            OnOpenDataFolderClick(sender, e);
            return;
        }

        try
        {
            var applied = await _health.ApplyFixAsync(row.Issue);
            if (!applied)
            {
                ShowAction("This problem has to be fixed manually.", isError: true);
                return;
            }

            ShowAction($"Applied the suggested fix for '{row.Issue.Title}'.");
            Refresh();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure($"Fixing '{row.Issue.Title}'", exception);
        }
    }

    private async void OnRecheckHealthClick(object sender, RoutedEventArgs e)
    {
        await RefreshHealthAsync();
        ShowAction("Settings health was checked again.");
    }

    private static Brush ResolveSeverityBrush(SettingsHealthSeverity severity)
    {
        var key = severity switch
        {
            SettingsHealthSeverity.Error => "SystemFillColorCriticalBrush",
            SettingsHealthSeverity.Warning => "SystemFillColorCautionBrush",
            _ => "SystemFillColorSuccessBrush",
        };
        return Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = CreateDialogContent(message),
            CloseButtonText = "Close",
        };
        await ShowDialogAsync(dialog);
    }

    private async Task<ContentDialogResult> ConfirmApplyAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = CreateDialogContent(message),
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await ShowDialogAsync(dialog);
    }

    private static ScrollViewer CreateDialogContent(string message) => new()
    {
        Content = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        },
        MaxHeight = 400,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private static string FormatNode(JsonNode? node) => node switch
    {
        null => "(not set)",
        JsonValue value when value.TryGetValue<string>(out var text) =>
            text.Length == 0 ? "(empty)" : text,
        _ => node.ToJsonString(DroidDeckJson.Options),
    };

    private static string DisplayScope(ConfigScope scope) => scope switch
    {
        ConfigScope.BuiltInDefaults => "defaults",
        ConfigScope.Global => "global",
        ConfigScope.Profile => "profile",
        ConfigScope.TeamProject => "team project",
        ConfigScope.LocalProject => "project",
        ConfigScope.RunConfiguration => "run configuration",
        ConfigScope.Device => "device",
        ConfigScope.Session => "session",
        _ => scope.ToString(),
    };

    private static void InitializePicker(object picker)
    {
        var window = App.Instance.Services.GetRequiredService<MainWindow>();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
    }

    /// <summary>One settings search match shaped for the results list.</summary>
    public sealed class SearchResultRow
    {
        /// <summary>Creates a row for a catalog entry.</summary>
        public SearchResultRow(
            SettingsCatalogEntry entry,
            string value,
            string source,
            bool canReset,
            ConfigScope resetScope)
        {
            Entry = entry;
            Value = value;
            Source = source;
            CanReset = canReset;
            ResetScope = resetScope;
            ResetId = AutomationId.For("ResetSetting", entry.Path);
        }

        /// <summary>Gets the catalog entry.</summary>
        public SettingsCatalogEntry Entry { get; }

        /// <summary>Gets the setting name.</summary>
        public string Label => Entry.Label;

        /// <summary>Gets the description, path, and restart note.</summary>
        public string Detail => Entry.RestartRequired
            ? $"{Entry.Description} · {Entry.Path} · Requires a restart"
            : $"{Entry.Description} · {Entry.Path}";

        /// <summary>Gets the formatted current value.</summary>
        public string Value { get; }

        /// <summary>Gets the layer that supplies the value.</summary>
        public string Source { get; }

        /// <summary>Gets whether the value can be reset from here.</summary>
        public bool CanReset { get; }

        /// <summary>Gets the layer the reset applies to.</summary>
        public ConfigScope ResetScope { get; }

        /// <summary>Gets the automation identifier of the reset button.</summary>
        public string ResetId { get; }
    }

    /// <summary>One settings health finding shaped for the health list.</summary>
    public sealed class SettingsHealthRow
    {
        /// <summary>Creates a row for a health finding.</summary>
        public SettingsHealthRow(SettingsHealthIssue issue, Brush severityBrush)
        {
            Issue = issue;
            SeverityBrush = severityBrush;
            FixId = AutomationId.For("FixSetting", issue.Id);
        }

        /// <summary>Gets the health finding.</summary>
        public SettingsHealthIssue Issue { get; }

        /// <summary>Gets the finding title.</summary>
        public string Title => Issue.Title;

        /// <summary>Gets the finding explanation.</summary>
        public string Detail => Issue.Detail;

        /// <summary>Gets the brush matching the finding severity.</summary>
        public Brush SeverityBrush { get; }

        /// <summary>Gets the repair caption.</summary>
        public string FixLabel => Issue.Fix.Label;

        /// <summary>Gets whether a repair can be offered.</summary>
        public bool HasFix => Issue.Fix.Kind != SettingsFixKind.None;

        /// <summary>Gets the automation identifier of the repair button.</summary>
        public string FixId { get; }
    }

    /// <summary>One top-level settings section offered for reset.</summary>
    public sealed record SettingsSectionRow(string? Section, string Name);

    private async void OnChangeShortcutClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutRow row ||
            _commands.Find(row.Id) is not { } command)
        {
            return;
        }

        var dialog = new ShortcutCaptureDialog(command, _keybindings.GetEffectiveShortcut(command));
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary ||
            dialog.CapturedShortcut is not { } gesture)
        {
            return;
        }

        var outcome = _keybindings.SetOverride(command.Id, gesture);
        if (!outcome.Applied)
        {
            ShortcutConflictBar.Message = $"{gesture} is already used by " +
                $"'{outcome.ConflictingWith?.Title ?? "another command"}'. Reset that shortcut first, then try again.";
            ShortcutConflictBar.IsOpen = true;
            return;
        }

        await SaveKeybindingsAsync($"'{command.Title}' now uses {gesture}.");
    }

    private async void OnResetShortcutClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutRow row ||
            _commands.Find(row.Id) is not { } command)
        {
            return;
        }

        if (!_keybindings.Reset(row.Id))
        {
            return;
        }

        await SaveKeybindingsAsync(
            $"'{command.Title}' was reset to {command.DefaultShortcut?.ToString() ?? "no shortcut"}.");
    }

    private async Task SaveKeybindingsAsync(string message)
    {
        try
        {
            await _keybindings.SaveAsync();
            ShortcutConflictBar.IsOpen = false;
            RefreshShortcutList();
            ShowAction(message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Saving the keyboard shortcut", exception);
        }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot = XamlRoot;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "A dialog could not be opened because another dialog is active.");
            ShowAction("Close the open dialog and try again.", isError: true);
            return ContentDialogResult.None;
        }
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

    private async void OnPerformanceIntervalChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
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
            ShowAction($"Performance samples are captured every {interval} ms.");
        }
        catch (Exception exception)
        {
            ReportFailure("Saving the performance sampling interval", exception);
        }
    }

    private async void OnPerformanceSamplesChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing || double.IsNaN(args.NewValue))
        {
            return;
        }

        var count = PerformanceSettings.ClampSampleCount((int)Math.Round(args.NewValue));
        if (count == _configuration.Current.Settings.Performance.MaxSamples)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.PerformanceMaxSamples, count);
            ShowAction($"Performance charts retain the latest {count} samples.");
        }
        catch (Exception exception)
        {
            ReportFailure("Saving the performance sample retention", exception);
        }
    }

    private async void OnInspectorDepthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing || double.IsNaN(args.NewValue))
        {
            return;
        }

        var depth = InspectorSettings.ClampDepth((int)Math.Round(args.NewValue));
        if (depth == _configuration.Current.Settings.Inspector.MaxDepth)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.InspectorMaxDepth, depth);
            ShowAction($"The UI inspector shows nodes up to depth {depth}.");
        }
        catch (Exception exception)
        {
            ReportFailure("Saving the inspector depth", exception);
        }
    }

    private async void OnInspectorInvisibleChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var include = InspectorInvisibleToggle.IsOn;
        if (include == _configuration.Current.Settings.Inspector.IncludeInvisible)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.InspectorIncludeInvisible, include);
            ShowAction(include ? "Invisible nodes are shown." : "Invisible nodes are hidden.");
        }
        catch (Exception exception)
        {
            ReportFailure("Saving the invisible-node filter", exception);
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
        ToolKind.Dart => tools.DartPath,
        ToolKind.Node => tools.NodePath,
        ToolKind.Scrcpy => tools.ScrcpyPath,
        ToolKind.Unity => tools.UnityPath,
        ToolKind.DotNet => tools.DotNetPath,
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

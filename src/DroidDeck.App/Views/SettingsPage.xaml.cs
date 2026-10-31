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
using DroidDeck.Core.Features;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Localization;
using DroidDeck.Core.Profiles;
using DroidDeck.Core.Sessions;
using DroidDeck.Core.Themes;
using DroidDeck.Core.Toolchain;
using DroidDeck.Plugins;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.UI.Kit;
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
    private readonly ThemeManager _themes;
    private readonly ProfileService _profiles;
    private readonly FeatureVisibilityService _features;
    private readonly PluginUiFaultReporter _faultReporter;
    private readonly IShellDialogs _dialogs;
    private readonly ILogger<SettingsPage> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly ConfigurationHealthService _health;
    private readonly ToolPathRow[] _toolPaths = ToolPathRow.CreateAll();
    private readonly Dictionary<string, PluginSectionHost> _pluginSections = new(StringComparer.Ordinal);
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
        ThemeManager themes,
        ProfileService profiles,
        FeatureVisibilityService features,
        PluginUiFaultReporter faultReporter,
        IShellDialogs dialogs,
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
        _themes = themes ?? throw new ArgumentNullException(nameof(themes));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _features = features ?? throw new ArgumentNullException(nameof(features));
        _faultReporter = faultReporter ?? throw new ArgumentNullException(nameof(faultReporter));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
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
        _plugins.Registry.Changed += OnPluginRegistryChanged;
        QueuePluginSectionLoad();
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
        _plugins.Registry.Changed -= OnPluginRegistryChanged;
        DisposePluginSections();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        _initializing = true;
        try
        {
            var settings = _configuration.Current.Settings;
            ThemeCombo.SelectedItem = settings.Appearance.Theme;
            AccentBox.Text = _themes.AccentOverride ?? string.Empty;
            FontBox.Text = _themes.FontOverride ?? string.Empty;
            ReducedMotionToggle.IsOn = _themes.IsReducedMotion();
            SelectLanguage(settings.Appearance.Language);
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
        RefreshFeatureList();
        UpdateSearchResults();
        _ = RefreshHealthAsync();
        _ = RefreshThemeFilesAsync();
        _ = RefreshProfilesAsync();
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
                _keybindings.GetEffectiveChord(command)?.ToString() ?? "Not set",
                _keybindings.GetOverrideChord(command.Id) is not null))
            .ToArray();

        var conflicts = _keybindings.FindChordConflicts();
        if (conflicts.Count > 0)
        {
            ShortcutConflictBar.Title = "Shortcut conflicts";
            ShortcutConflictBar.Message = string.Join(
                " ",
                conflicts.Select(conflict =>
                    $"{conflict.Chord} is used by " +
                    string.Join(", ", conflict.Commands.Select(static command => $"'{command.Title}'")) + "."));
            ShortcutConflictBar.Tag = "conflicts";
            ShortcutConflictBar.IsOpen = true;
        }
        else if (Equals(ShortcutConflictBar.Tag, "conflicts"))
        {
            ShortcutConflictBar.IsOpen = false;
            ShortcutConflictBar.Tag = null;
        }
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
            var result = await _dialogs.ShowAsync(dialog);
            if (result.WasBlocked)
            {
                ShowAction("Close the open dialog and try again.", isError: true);
            }

            if (result.Result != ContentDialogResult.Primary)
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
            _dialogs.InitializePicker(picker);
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
            _dialogs.InitializePicker(picker);
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
        var result = await _dialogs.ShowAsync(dialog);
        if (result.WasBlocked)
        {
            ShowAction("Close the open dialog and try again.", isError: true);
        }
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
        var result = await _dialogs.ShowAsync(dialog);
        if (result.WasBlocked)
        {
            ShowAction("Close the open dialog and try again.", isError: true);
        }

        return result.Result;
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

        var dialog = new ShortcutCaptureDialog(command, _keybindings.GetEffectiveChord(command));
        var result = await _dialogs.ShowAsync(dialog);
        if (result.WasBlocked)
        {
            ShowAction("Close the open dialog and try again.", isError: true);
        }

        if (result.Result != ContentDialogResult.Primary ||
            dialog.CapturedChord is not { } chord)
        {
            return;
        }

        var outcome = _keybindings.SetOverride(command.Id, chord);
        if (!outcome.Applied)
        {
            ShortcutConflictBar.Title = "Shortcut conflict";
            ShortcutConflictBar.Message = $"{chord} is already used by " +
                $"'{outcome.ConflictingWith?.Title ?? "another command"}'. Reset that shortcut first, then try again.";
            ShortcutConflictBar.Tag = "user";
            ShortcutConflictBar.IsOpen = true;
            return;
        }

        await SaveKeybindingsAsync($"'{command.Title}' now uses {chord}.");
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
            ShortcutConflictBar.Tag = null;
            RefreshShortcutList();
            ShowAction(message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Saving the keyboard shortcut", exception);
        }
    }

    private async void OnExportKeybindingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"droiddeck-shortcuts-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultFileExtension = ".json",
            };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            _dialogs.InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await File.WriteAllTextAsync(file.Path, _keybindings.Export());
            ShowAction("Keyboard shortcuts exported.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            ReportFailure("Exporting keyboard shortcuts", exception);
        }
    }

    private async void OnImportKeybindingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".json");
            _dialogs.InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var result = _keybindings.Import(await File.ReadAllTextAsync(file.Path));
            if (result.Skipped.Count == 0 && result.Conflicts.Count == 0)
            {
                await SaveKeybindingsAsync($"Imported {result.Applied.Count} shortcut(s).");
                return;
            }

            var details = new List<string>();
            if (result.Conflicts.Count > 0)
            {
                details.Add(
                    $"{result.Conflicts.Count} shortcut(s) were rejected because another command already uses them: " +
                    string.Join(", ", result.Conflicts.Select(conflict => conflict.Chord.ToString())) + ".");
            }

            if (result.Skipped.Count > 0)
            {
                details.Add($"{result.Skipped.Count} entry(ies) were skipped because the command is unknown.");
            }

            await SaveKeybindingsAsync(
                $"Imported {result.Applied.Count} shortcut(s). " + string.Join(" ", details));
        }
        catch (KeybindingImportException exception)
        {
            ShowAction($"The shortcuts file could not be imported: {exception.Message}", isError: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            ReportFailure("Importing keyboard shortcuts", exception);
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

        var days = SettingsValuePolicy.Clamp(
            SettingsPaths.SessionRetentionDays,
            (int)Math.Round(args.NewValue));
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

        var limit = SettingsValuePolicy.Clamp(
            SettingsPaths.RunMaxConcurrentDevices,
            (int)Math.Round(args.NewValue));
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

        var interval = SettingsValuePolicy.Clamp(
            SettingsPaths.PerformanceSampleIntervalMs,
            (int)Math.Round(args.NewValue));
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

        var count = SettingsValuePolicy.Clamp(
            SettingsPaths.PerformanceMaxSamples,
            (int)Math.Round(args.NewValue));
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

        var depth = SettingsValuePolicy.Clamp(
            SettingsPaths.InspectorMaxDepth,
            (int)Math.Round(args.NewValue));
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

    private async Task RefreshThemeFilesAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var themes = await _themes.Catalog.ListAsync();
            var choices = new List<ThemeChoice> { new(null, "Theme file (none)") };
            choices.AddRange(themes.Select(theme => new ThemeChoice(theme.Id, theme.Name)));
            var selected = _themes.SelectedThemeId;
            _initializing = true;
            try
            {
                ThemeFileCombo.ItemsSource = choices;
                ThemeFileCombo.SelectedItem = choices.FirstOrDefault(choice =>
                    string.Equals(choice.Id, selected, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
            }
            finally
            {
                _initializing = false;
            }

            ThemeFileStatusText.Text = selected is null
                ? "The base theme palette is used; pick a theme file for custom colors, fonts, and log colors."
                : $"Theme file '{selected}' is applied.";
            DeleteThemeButton.IsEnabled = selected is not null && ThemeCatalog.FindBuiltIn(selected) is null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Loading theme files", exception);
        }
    }

    private async void OnThemeFileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeFileCombo.SelectedItem is not ThemeChoice choice)
        {
            return;
        }

        try
        {
            if (choice.Id is null)
            {
                await _configuration.ResetAsync(SettingsPaths.ThemeId);
                ThemeFileStatusText.Text = "The base theme palette is used.";
                DeleteThemeButton.IsEnabled = false;
            }
            else
            {
                var applied = await _themes.ApplyAsync(choice.Id);
                ThemeFileStatusText.Text = $"Theme file '{applied.Name}' is applied.";
                DeleteThemeButton.IsEnabled = ThemeCatalog.FindBuiltIn(choice.Id) is null;
            }
        }
        catch (ThemeValidationException exception)
        {
            ThemeFileStatusText.Text = ExplainContrast(exception.Result);
            ShowAction("The theme file has unreadable colors.", isError: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ReportFailure("Applying the theme file", exception);
        }
    }

    private async void OnImportThemeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".json");
            _dialogs.InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var applied = await _themes.ImportAsync(await File.ReadAllTextAsync(file.Path));
            await RefreshThemeFilesAsync();
            ShowAction($"Imported and applied theme '{applied.Name}'.");
        }
        catch (ThemeValidationException exception)
        {
            ShowAction(ExplainContrast(exception.Result), isError: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or COMException or
                ArgumentException or InvalidOperationException)
        {
            ReportFailure("Importing the theme", exception);
        }
    }

    private async void OnExportThemeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var theme = _themes.GetCurrent();
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"droiddeck-theme-{theme.Id}-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultFileExtension = ".json",
            };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            _dialogs.InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await File.WriteAllTextAsync(file.Path, ThemeManager.Export(theme));
            ShowAction($"Exported theme '{theme.Name}'.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            ReportFailure("Exporting the theme", exception);
        }
    }

    private async void OnDeleteThemeClick(object sender, RoutedEventArgs e)
    {
        if (ThemeFileCombo.SelectedItem is not ThemeChoice { Id: not null } choice)
        {
            return;
        }

        try
        {
            if (!_themes.Catalog.Delete(choice.Id))
            {
                ShowAction("Built-in themes cannot be deleted.", isError: true);
                return;
            }

            await _configuration.ResetAsync(SettingsPaths.ThemeId);
            await RefreshThemeFilesAsync();
            ShowAction("The custom theme was deleted; the base palette is used again.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Deleting the theme", exception);
        }
    }

    private async void OnApplyAccentClick(object sender, RoutedEventArgs e)
    {
        var color = AccentBox.Text.Trim();
        if (!ThemeContrast.TryParseColor(color, out _, out _, out _))
        {
            AccentStatusText.Text = "Use a #RRGGBB color, for example #0078D4.";
            return;
        }

        try
        {
            await _themes.SetAccentAsync(color);
            AccentStatusText.Text = $"Accent {color.ToUpperInvariant()} applies on top of the theme file.";
            ShowAction("Accent color saved.");
        }
        catch (ThemeValidationException exception)
        {
            AccentStatusText.Text = ExplainContrast(exception.Result);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Saving the accent color", exception);
        }
    }

    private async void OnClearAccentClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _themes.SetAccentAsync(null);
            AccentBox.Text = string.Empty;
            AccentStatusText.Text = "The theme file accent is used.";
            ShowAction("Accent color reset to the theme value.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Resetting the accent color", exception);
        }
    }

    private async void OnApplyFontClick(object sender, RoutedEventArgs e)
    {
        var family = FontBox.Text.Trim();
        try
        {
            await _themes.SetFontAsync(family.Length == 0 ? null : family);
            ShowAction(family.Length == 0
                ? "The theme font is used."
                : $"UI font set to {family}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Saving the UI font", exception);
        }
    }

    private async void OnClearFontClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _themes.SetFontAsync(null);
            FontBox.Text = string.Empty;
            ShowAction("The theme font is used.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Resetting the UI font", exception);
        }
    }

    private void SelectLanguage(string? preference)
    {
        var normalized = string.IsNullOrWhiteSpace(preference)
            ? LocalizationPreferences.System
            : preference;
        foreach (var item in LanguageCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(
                    item.Tag as string,
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
            {
                LanguageCombo.SelectedItem = item;
                return;
            }
        }

        LanguageCombo.SelectedIndex = 0;
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string preference })
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.Language, preference);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Saving the language preference", exception);
        }
    }

    private async void OnReducedMotionToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        try
        {
            await _themes.SetReducedMotionAsync(ReducedMotionToggle.IsOn);
            ShowAction(ReducedMotionToggle.IsOn
                ? "Motion is reduced where the interface animates."
                : "Full motion is used.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Saving the motion preference", exception);
        }
    }

    private static string ExplainContrast(ThemeValidationResult result) =>
        "The theme cannot be applied: " + string.Join(
            " ",
            result.Findings
                .Where(static finding => finding.IsError)
                .Select(static finding => finding.Explanation));

    private async Task RefreshProfilesAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var profiles = await _profiles.ListAsync();
            var active = _profiles.GetActiveProfileId();
            ProfileList.ItemsSource = profiles
                .Select(profile => new ProfileRow(profile, string.Equals(profile.Id, active, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            DeactivateProfileButton.IsEnabled = active is not null;
            ProfileStatusText.Text = active is null
                ? "Global settings are in effect."
                : $"Profile '{profiles.FirstOrDefault(profile => string.Equals(profile.Id, active, StringComparison.OrdinalIgnoreCase))?.Name ?? active}' is in effect.";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Loading profiles", exception);
        }
    }

    private async Task<string?> PromptForTextAsync(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 320 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                    box,
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await _dialogs.ShowAsync(dialog);
        if (result.WasBlocked)
        {
            ShowAction("Close the open dialog and try again.", isError: true);
        }

        return result.Result == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    private async void OnCreateProfileClick(object sender, RoutedEventArgs e)
    {
        var name = await PromptForTextAsync(
            "New profile",
            "Profiles store settings that layer over the global values. Name the workflow this profile is for.",
            string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            await _profiles.CreateAsync(name);
            await RefreshProfilesAsync();
            ShowAction($"Profile '{name}' was created. Edit its settings while it is applied.");
        }
        catch (Exception exception) when (
            exception is ProfileException or IOException or UnauthorizedAccessException)
        {
            ShowAction($"The profile could not be created: {exception.Message}", isError: true);
        }
    }

    private async void OnImportProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".json");
            _dialogs.InitializePicker(picker);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var result = await _profiles.ImportAsync(await File.ReadAllTextAsync(file.Path));
            await RefreshProfilesAsync();
            ProfileStatusText.Text = result.ExcludedPaths.Count == 0
                ? $"Imported profile '{result.Metadata.Name}'."
                : $"Imported profile '{result.Metadata.Name}'. {result.ExcludedPaths.Count} secret value(s) were not imported: " +
                    string.Join(", ", result.ExcludedPaths) + ".";
            ShowAction("Profile imported.");
        }
        catch (ProfileException exception)
        {
            ShowAction($"The profile could not be imported: {exception.Message}", isError: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            ReportFailure("Importing the profile", exception);
        }
    }

    private async void OnDeactivateProfileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _profiles.ApplyAsync(null);
            await RefreshProfilesAsync();
            ShowAction("Global settings are in effect again.");
        }
        catch (Exception exception) when (
            exception is ProfileException or IOException or UnauthorizedAccessException)
        {
            ReportFailure("Deactivating the profile", exception);
        }
    }

    private async void OnApplyProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileRow row)
        {
            return;
        }

        try
        {
            await _profiles.ApplyAsync(row.Id);
            await RefreshProfilesAsync();
            ShowAction($"Profile '{row.Name}' is in effect.");
        }
        catch (Exception exception) when (
            exception is ProfileException or IOException or UnauthorizedAccessException)
        {
            ReportFailure("Applying the profile", exception);
        }
    }

    private async void OnExportProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileRow row)
        {
            return;
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"droiddeck-profile-{row.Id}-{DateTime.Now:yyyyMMdd-HHmmss}",
                DefaultFileExtension = ".json",
            };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            _dialogs.InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await File.WriteAllTextAsync(file.Path, await _profiles.ExportAsync(row.Id));
            ShowAction($"Exported profile '{row.Name}' without secrets.");
        }
        catch (Exception exception) when (
            exception is ProfileException or IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            ReportFailure("Exporting the profile", exception);
        }
    }

    private async void OnRenameProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileRow row)
        {
            return;
        }

        var name = await PromptForTextAsync("Rename profile", "The new name for this profile.", row.Name);
        if (string.IsNullOrWhiteSpace(name) || name == row.Name)
        {
            return;
        }

        try
        {
            await _profiles.UpdateMetadataAsync(row.Id, name);
            await RefreshProfilesAsync();
            ShowAction($"Profile renamed to '{name}'.");
        }
        catch (Exception exception) when (
            exception is ProfileException or IOException or UnauthorizedAccessException)
        {
            ShowAction($"The profile could not be renamed: {exception.Message}", isError: true);
        }
    }

    private async void OnDuplicateProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileRow row)
        {
            return;
        }

        try
        {
            var copy = await _profiles.DuplicateAsync(row.Id);
            await RefreshProfilesAsync();
            ShowAction($"Created profile '{copy.Name}'.");
        }
        catch (Exception exception) when (
            exception is ProfileException or IOException or UnauthorizedAccessException)
        {
            ShowAction($"The profile could not be duplicated: {exception.Message}", isError: true);
        }
    }

    private async void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileRow row)
        {
            return;
        }

        if (await ConfirmApplyAsync(
                "Delete profile",
                $"Delete '{row.Name}'? The settings stored in the profile are removed; global settings are unaffected.") !=
            ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _profiles.DeleteAsync(row.Id);
            await RefreshProfilesAsync();
            ShowAction($"Profile '{row.Name}' was deleted.");
        }
        catch (Exception exception) when (
            exception is ProfileException or IOException or UnauthorizedAccessException)
        {
            ShowAction($"The profile could not be deleted: {exception.Message}", isError: true);
        }
    }

    private void RefreshFeatureList()
    {
        FeatureList.ItemsSource = _features.Features
            .Select(feature => new FeatureRow(
                feature,
                _features.GetBadge(feature.Id),
                !_features.IsVisible(feature.Id)))
            .ToArray();
    }

    private async void OnFeatureToggleClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FeatureRow row)
        {
            return;
        }

        try
        {
            if (row.IsHidden)
            {
                await _features.ShowAsync(row.Id);
                ShowAction($"'{row.Name}' is visible again; its configuration was kept.");
            }
            else
            {
                await _features.HideAsync(row.Id);
                ShowAction($"'{row.Name}' is hidden from navigation. Show it here at any time.");
            }

            RefreshFeatureList();
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Changing feature visibility", exception);
        }
    }

    private async void OnShowAllFeaturesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _features.ShowAllAsync();
            RefreshFeatureList();
            ShowAction("Every hidden feature is visible again.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportFailure("Restoring feature visibility", exception);
        }
    }

    /// <summary>One theme file choice in the theme picker.</summary>
    public sealed record ThemeChoice(string? Id, string Label)
    {
        /// <inheritdoc />
        public override string ToString() => Label;
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

    private void OnOpenAboutClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("about");

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
            FailureNotice.Format(action, exception, _diagnostics.DiagnosticsId),
            isError: true);
    }

    private void OnPluginRegistryChanged(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        RemoveStalePluginSections();
        QueuePluginSectionLoad();
    });

    /// <summary>Loads each plugin section after the first frame, one at a time, so a slow section cannot block Settings.</summary>
    private void QueuePluginSectionLoad()
    {
        if (_disposed)
        {
            return;
        }

        var afterFirstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _dispatcher.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => afterFirstFrame.TrySetResult());
        if (!queued)
        {
            afterFirstFrame.TrySetResult();
        }

        foreach (var entry in _plugins.Extensions.SettingsSections)
        {
            if (_pluginSections.ContainsKey(entry.Section.Id))
            {
                continue;
            }

            _ = LoadPluginSectionAsync(entry, afterFirstFrame.Task);
        }

        NoPluginSectionsText.Visibility = _pluginSections.Count == 0 && _plugins.Extensions.SettingsSections.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task LoadPluginSectionAsync(SettingsSectionEntry entry, Task afterFirstFrame)
    {
        try
        {
            await afterFirstFrame.ConfigureAwait(true);
            if (_disposed || _pluginSections.ContainsKey(entry.Section.Id))
            {
                return;
            }

            var settings = await CreatePluginSettingsAsync(entry.PluginId).ConfigureAwait(true);
            if (settings is null)
            {
                _logger.LogWarning(
                    "The settings section {SectionId} from {PluginId} was skipped because the plugin is not installed.",
                    entry.Section.Id,
                    entry.PluginId);
                return;
            }

            if (_pluginSections.ContainsKey(entry.Section.Id))
            {
                // Another queued load won the race; drop the duplicate store.
                DropHostSettings(settings);
                return;
            }

            var boundary = new PluginFaultBoundary(
                entry.PluginId,
                $"settings section {entry.Section.Id}",
                _faultReporter);
            var header = new StackPanel { Spacing = 2 };
            header.Children.Add(new TextBlock
            {
                Text = $"{entry.Section.Title} · {SourceLabel(entry.PluginId)}",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            if (!string.IsNullOrWhiteSpace(entry.Section.Description))
            {
                header.Children.Add(new TextBlock
                {
                    Text = entry.Section.Description,
                    FontSize = 12,
                    Foreground = ResolveBrush("TextFillColorSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var card = new StackPanel { Spacing = 8 };
            card.Children.Add(header);
            card.Children.Add(boundary);
            PluginSectionsPanel.Children.Add(card);

            var host = new PluginSectionHost(entry, boundary, card, settings);
            _pluginSections[entry.Section.Id] = host;

            NoPluginSectionsText.Visibility = Visibility.Collapsed;

            // The section context hands the plugin its host-managed settings
            // store, so every write is validated by the host before persisting.
            var context = new PluginSettingsSectionContext(settings);
            var result = await _plugins.Invoker.CreateViewAsync(
                entry.PluginId,
                $"settings section {entry.Section.Id}",
                token => entry.Section.CreateViewAsync(context, token),
                CancellationToken.None).ConfigureAwait(true);
            if (!_pluginSections.ContainsKey(entry.Section.Id))
            {
                return;
            }

            boundary.Show(result.Content, result.Error);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Plugin settings section {SectionId} from {PluginId} could not load: {Error}",
                    entry.Section.Id,
                    entry.PluginId,
                    result.Error);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Loading the settings section {SectionId} failed.", entry.Section.Id);
        }
    }

    private void RemoveStalePluginSections()
    {
        var entries = _plugins.Extensions.SettingsSections;
        foreach (var staleId in _pluginSections.Keys
            .Where(id => entries.All(entry => !string.Equals(entry.Section.Id, id, StringComparison.Ordinal)))
            .ToArray())
        {
            if (_pluginSections.Remove(staleId, out var stale))
            {
                PluginSectionsPanel.Children.Remove(stale.Card);
                stale.Boundary.Dispose();
                DropHostSettings(stale.Settings);
            }
        }

        NoPluginSectionsText.Visibility =
            PluginSectionsPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DisposePluginSections()
    {
        foreach (var section in _pluginSections.Values)
        {
            PluginSectionsPanel.Children.Remove(section.Card);
            section.Boundary.Dispose();
            DropHostSettings(section.Settings);
        }

        _pluginSections.Clear();
    }

    /// <summary>
    /// Disposes a settings store this page owns. Schema stores are shared per
    /// settings file with the plugin loader and the settings dialog, so the
    /// settings factory owns them and they are never disposed here.
    /// </summary>
    private static void DropHostSettings(IPluginSettings settings)
    {
        if (settings is not SchemaPluginSettings)
        {
            (settings as IDisposable)?.Dispose();
        }
    }

    /// <summary>Builds the host settings store for one plugin, mirroring the per-plugin settings dialog.</summary>
    private async Task<IPluginSettings?> CreatePluginSettingsAsync(string pluginId)
    {
        var status = _plugins.Statuses.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (status is null || string.IsNullOrWhiteSpace(status.InstallPath))
        {
            return null;
        }

        var creation = await _plugins.SettingsFactory
            .CreateForInstalledPluginAsync(pluginId, status.InstallPath)
            .ConfigureAwait(true);
        if (creation.Issue is { } issue)
        {
            _logger.LogWarning(
                "The settings schema for {PluginId} was not used ({Code}): {Message}",
                pluginId,
                issue.Code,
                issue.Message);
        }

        return creation.Settings;
    }

    private static string SourceLabel(string pluginId) =>
        pluginId == PluginConstants.BuiltInPluginId ? "Built-in" : pluginId;

    private static Brush ResolveBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    /// <summary>One plugin settings section and its host chrome.</summary>
    private sealed class PluginSectionHost(
        SettingsSectionEntry entry,
        PluginFaultBoundary boundary,
        StackPanel card,
        IPluginSettings settings)
    {
        public SettingsSectionEntry Entry { get; } = entry;

        public PluginFaultBoundary Boundary { get; } = boundary;

        public StackPanel Card { get; } = card;

        public IPluginSettings Settings { get; } = settings;
    }
}

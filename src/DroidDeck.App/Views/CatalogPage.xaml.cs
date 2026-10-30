using System.Collections.ObjectModel;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugins.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.App.Views;

/// <summary>
/// Browses the plugin catalogs the user added, and installs or updates a
/// plugin when the user asks for it. Catalogs are fetched only when the user
/// refreshes; installing always runs the trust dialog before any file is
/// extracted, and nothing here installs or updates on its own.
/// </summary>
public sealed partial class CatalogPage : Page, IRefreshablePage, IDisposable
{
    private const string AllTagsLabel = "All tags";

    private readonly PluginCatalogService _catalog;
    private readonly CatalogInstaller _installer;
    private readonly ILogger<CatalogPage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private readonly ObservableCollection<CatalogEntryRow> _rows = [];
    private readonly ObservableCollection<CatalogSourceRow> _sourceRows = [];
    private readonly Dictionary<string, CatalogEntry> _entries = new(StringComparer.Ordinal);
    private Dictionary<string, string> _installedVersions = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;
    private bool _updatingFilters;
    private bool _disposed;

    /// <summary>Creates the catalog page.</summary>
    public CatalogPage(PluginCatalogService catalog, CatalogInstaller installer, ILogger<CatalogPage> logger)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        CatalogList.ItemsSource = _rows;
        SourceList.ItemsSource = _sourceRows;
        _catalog.Changed += OnCatalogChanged;
        _ = LoadAsync();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        if (_initialized)
        {
            Render();
            return;
        }

        _ = LoadAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _catalog.Changed -= OnCatalogChanged;
    }

    private async Task LoadAsync()
    {
        try
        {
            await _catalog.InitializeAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The catalog sources and cache could not be loaded.");
            Dispatch(() => ShowAction(
                $"Catalog sources could not be loaded: {exception.Message}",
                isError: true));
        }

        try
        {
            var installed = await _installer.GetInstalledAsync();
            _installedVersions = installed.ToDictionary(
                plugin => plugin.PluginId,
                plugin => plugin.Version,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The installed plugins could not be scanned for the catalog page.");
        }

        Dispatch(() =>
        {
            _initialized = true;
            Render();
        });
    }

    private void Render()
    {
        RenderSources();
        UpdateFilters();
        RenderEntries();
    }

    private void RenderSources()
    {
        var statuses = _catalog.SourceStatuses;
        _sourceRows.Clear();
        foreach (var status in statuses)
        {
            _sourceRows.Add(new CatalogSourceRow(status));
        }

        NoSourcesText.Visibility = statuses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateFilters()
    {
        _updatingFilters = true;
        try
        {
            var previousSource = (SourceFilter.SelectedItem as CatalogFilterOption)?.Id;
            var sourceOptions = new List<CatalogFilterOption>
            {
                new(null, "All sources"),
            };
            sourceOptions.AddRange(
                _catalog.Sources.Select(source => new CatalogFilterOption(source.Id, source.Name)));
            SourceFilter.ItemsSource = sourceOptions;
            SourceFilter.SelectedIndex = sourceOptions.FindIndex(
                option => string.Equals(option.Id, previousSource, StringComparison.OrdinalIgnoreCase));
            if (SourceFilter.SelectedIndex < 0)
            {
                SourceFilter.SelectedIndex = 0;
            }

            var previousTag = TagFilter.SelectedItem as string;
            var tags = new List<string> { AllTagsLabel };
            tags.AddRange(_catalog.AllTags);
            TagFilter.ItemsSource = tags;
            TagFilter.SelectedItem = previousTag is not null && tags.Contains(previousTag, StringComparer.OrdinalIgnoreCase)
                ? tags.First(tag => string.Equals(tag, previousTag, StringComparison.OrdinalIgnoreCase))
                : AllTagsLabel;
        }
        finally
        {
            _updatingFilters = false;
        }
    }

    private void RenderEntries()
    {
        var tag = TagFilter.SelectedItem as string;
        var tags = string.IsNullOrEmpty(tag) || string.Equals(tag, AllTagsLabel, StringComparison.Ordinal)
            ? Array.Empty<string>()
            : [tag];
        var sourceId = (SourceFilter.SelectedItem as CatalogFilterOption)?.Id;
        var entries = _catalog.Browse(new CatalogQuery
        {
            SearchText = SearchBox.Text,
            Tags = tags,
            SourceId = sourceId,
            IncludePrerelease = PrereleaseCheck.IsChecked == true,
        });

        _rows.Clear();
        _entries.Clear();
        foreach (var entry in entries)
        {
            _installedVersions.TryGetValue(entry.Plugin.Id, out var installedVersion);
            var row = new CatalogEntryRow(entry, installedVersion);
            _rows.Add(row);
            _entries[row.EntryId] = entry;
        }

        ResultsText.Text = entries.Count == 1
            ? "1 plugin matches."
            : $"{entries.Count} plugins match.";
        EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CatalogEntryRow row || sender is not Button button)
        {
            return;
        }

        if (!_entries.TryGetValue(row.EntryId, out var entry))
        {
            return;
        }

        // The row shows "Update" when the plugin is installed with a newer
        // catalog version; that branch must call UpdateAsync, because
        // InstallAsync refuses installed plugins by design.
        var isUpdate = _installedVersions.TryGetValue(entry.Plugin.Id, out var installedVersion) &&
            SemanticVersion.TryParse(entry.Version.Version, out var candidate) &&
            SemanticVersion.TryParse(installedVersion, out var current) &&
            candidate > current;

        button.IsEnabled = false;
        ShowAction($"Downloading and verifying {row.Title} from {row.SourceName}…");
        try
        {
            var result = isUpdate
                ? await _installer.UpdateAsync(entry, ShowTrustDecisionAsync)
                : await _installer.InstallAsync(entry, ShowTrustDecisionAsync);
            if (result.Succeeded)
            {
                _installedVersions[entry.Plugin.Id] = result.ToVersion ?? entry.Version.Version;
                ShowAction(result.Action == CatalogInstallAction.Update
                    ? $"Updated {row.Title} to v{result.ToVersion}. The previous version " +
                        $"{result.FromVersion} is kept for rollback in Extensions. " +
                        "Approve the new files there and restart DroidDeck to load them."
                    : $"Installed {row.Title} v{result.ToVersion}. Review and approve it in Extensions; " +
                        "restart DroidDeck to load it.");
            }
            else
            {
                ShowAction(result.Error ?? "The install failed.", isError: true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "{Verb} {PluginId} failed.",
                isUpdate ? "Updating" : "Installing",
                entry.Plugin.Id);
            ShowAction(
                $"{(isUpdate ? "Updating" : "Installing")} {row.Title} failed: {exception.Message}",
                isError: true);
        }
        finally
        {
            button.IsEnabled = true;
            Render();
        }
    }

    private async Task<bool> ShowTrustDecisionAsync(CatalogInstallPlan plan)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            return await CatalogInstallDialogs.ShowTrustAsync(XamlRoot, plan);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await CatalogInstallDialogs.ShowTrustAsync(XamlRoot, plan));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return await completion.Task;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            await LoadAsync();
        }

        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "Refreshing…";
        try
        {
            var result = await _catalog.RefreshAsync();
            if (result.Errors.Count == 0)
            {
                ShowAction($"Catalog refreshed from {_catalog.Sources.Count(source => source.Enabled)} source(s).");
            }
            else
            {
                var details = string.Join(
                    Environment.NewLine,
                    result.Errors.Select(error => $"{error.SourceName}: {error.Message}"));
                ShowAction(details, isError: true);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The catalog refresh failed.");
            ShowAction($"The catalog refresh failed: {exception.Message}", isError: true);
        }
        finally
        {
            RefreshButton.Content = "Refresh";
            RefreshButton.IsEnabled = true;
            Render();
        }
    }

    private async void OnAddSourceClick(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            await LoadAsync();
        }

        var nameBox = new TextBox
        {
            Header = "Name",
            PlaceholderText = "Team plugin feed",
        };
        AutomationProperties.SetAutomationId(nameBox, "CatalogSourceNameBox");
        var urlBox = new TextBox
        {
            Header = "URL or local path",
            PlaceholderText = "https://example.com/catalog.json",
        };
        AutomationProperties.SetAutomationId(urlBox, "CatalogSourceUrlBox");
        var prerelease = new CheckBox
        {
            Content = "Allow pre-release versions from this source",
        };
        AutomationProperties.SetAutomationId(prerelease, "CatalogSourcePrereleaseBox");
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(nameBox);
        content.Children.Add(urlBox);
        content.Children.Add(prerelease);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add catalog source",
            Content = content,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var result = await _catalog.AddSourceAsync(
                nameBox.Text,
                urlBox.Text,
                prerelease.IsChecked == true);
            ShowAction(
                result.Succeeded
                    ? $"Added \"{result.Source!.Name}\". Choose Refresh to fetch its catalog."
                    : result.Error ?? "The source could not be added.",
                isError: !result.Succeeded);
            Render();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The catalog source could not be added.");
            ShowAction($"The source could not be added: {exception.Message}", isError: true);
        }
    }

    private async void OnToggleSourceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CatalogSourceRow row)
        {
            return;
        }

        try
        {
            var enabled = !row.Enabled;
            if (await _catalog.SetSourceEnabledAsync(row.Id, enabled))
            {
                ShowAction($"{(enabled ? "Enabled" : "Disabled")} the source \"{row.Name}\".");
            }
            else
            {
                ShowAction($"The source \"{row.Name}\" could not be updated.", isError: true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The catalog source {Source} could not be updated.", row.Id);
            ShowAction($"The source \"{row.Name}\" could not be updated: {exception.Message}", isError: true);
        }

        Render();
    }

    private async void OnRemoveSourceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CatalogSourceRow row)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Remove \"{row.Name}\"?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "This only removes the source from DroidDeck. Plugins you installed stay installed " +
                    "and keep working, but they can no longer be browsed, updated, or reinstalled from " +
                    "this source. Cached catalog files stay on disk.",
            },
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            if (await _catalog.RemoveSourceAsync(row.Id))
            {
                ShowAction($"Removed the source \"{row.Name}\". Installed plugins were not changed.");
            }
            else
            {
                ShowAction($"The source \"{row.Name}\" could not be removed.", isError: true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The catalog source {Source} could not be removed.", row.Id);
            ShowAction($"The source \"{row.Name}\" could not be removed: {exception.Message}", isError: true);
        }

        Render();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initialized && !_updatingFilters)
        {
            RenderEntries();
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (_initialized && !_updatingFilters)
        {
            RenderEntries();
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => Dispatch(Render);

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

    /// <summary>One source filter choice; a null id means all sources.</summary>
    public sealed record CatalogFilterOption(string? Id, string Name)
    {
        /// <inheritdoc />
        public override string ToString() => Name;
    }

    /// <summary>One configured source shaped for the source list.</summary>
    public sealed class CatalogSourceRow
    {
        /// <summary>Creates a row from a source status.</summary>
        public CatalogSourceRow(CatalogSourceStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            Id = status.Source.Id;
            Name = status.Source.Name;
            Target = status.Source.IsLocalPath ? $"Local: {status.Source.Url}" : status.Source.Url;
            Enabled = status.Source.Enabled;
            ToggleLabel = Enabled ? "Disable" : "Enable";
            ToggleId = $"CatalogToggleSource_{Id}";
            RemoveId = $"CatalogRemoveSource_{Id}";
            Status = status.Error is not null
                ? "Last refresh failed"
                : status.FetchedAt is { } fetched
                    ? $"{(status.IsFromCache ? "Cached" : "Live")} · {fetched.ToLocalTime():g}"
                    : "Not fetched yet";
            StatusBrush = ResolveBrush(status.Error is null
                ? "TextFillColorSecondaryBrush"
                : "SystemFillColorCautionBrush");
            Error = status.Error ?? string.Empty;
            HasError = status.Error is not null;
        }

        /// <summary>Gets the source identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the display name.</summary>
        public string Name { get; }

        /// <summary>Gets the URL or local path.</summary>
        public string Target { get; }

        /// <summary>Gets whether the source is enabled.</summary>
        public bool Enabled { get; }

        /// <summary>Gets the enable or disable button label.</summary>
        public string ToggleLabel { get; }

        /// <summary>Gets the toggle button automation id.</summary>
        public string ToggleId { get; }

        /// <summary>Gets the remove button automation id.</summary>
        public string RemoveId { get; }

        /// <summary>Gets the fetch state summary.</summary>
        public string Status { get; }

        /// <summary>Gets the fetch state brush.</summary>
        public Brush StatusBrush { get; }

        /// <summary>Gets the last refresh error, when one exists.</summary>
        public string Error { get; }

        /// <summary>Gets whether the error line should be shown.</summary>
        public bool HasError { get; }
    }

    /// <summary>One catalog entry shaped for the browse list.</summary>
    public sealed class CatalogEntryRow
    {
        /// <summary>Creates a row from a catalog entry and the installed version, when present.</summary>
        public CatalogEntryRow(CatalogEntry entry, string? installedVersion)
        {
            ArgumentNullException.ThrowIfNull(entry);
            EntryId = $"CatalogEntry_{entry.Plugin.Id}_{entry.Source.Id}";
            Title = entry.Plugin.Name;
            VersionLabel = entry.IsPrerelease
                ? $"v{entry.Version.Version} (pre-release)"
                : $"v{entry.Version.Version}";
            Publisher = string.IsNullOrWhiteSpace(entry.Plugin.Publisher)
                ? string.Empty
                : $"by {entry.Plugin.Publisher}";
            Summary = entry.Plugin.Summary ?? string.Empty;
            HasSummary = Summary.Length > 0;
            CompatibilityBadge = entry.Compatibility switch
            {
                CatalogCompatibility.Compatible => "Compatible",
                CatalogCompatibility.Partial => "Compatibility warning",
                _ => "Incompatible",
            };
            CompatibilityLabel = entry.CompatibilityLabel;
            CompatibilityBrush = ResolveBrush(entry.Compatibility switch
            {
                CatalogCompatibility.Compatible => "SystemFillColorSuccessBrush",
                CatalogCompatibility.Partial => "SystemFillColorCautionBrush",
                _ => "SystemFillColorCriticalBrush",
            });
            Changelog = entry.Changelog ?? string.Empty;
            HasChangelog = Changelog.Length > 0;
            Tags = entry.Plugin.Tags is { Count: > 0 } tags ? string.Join(" · ", tags) : string.Empty;
            HasTags = Tags.Length > 0;
            SourceLabel = $"Source: {entry.Source.Name}";
            SourceName = entry.Source.Name;
            var updateAvailable = installedVersion is not null &&
                SemanticVersion.TryParse(entry.Version.Version, out var candidate) &&
                SemanticVersion.TryParse(installedVersion, out var current) &&
                candidate > current;
            CanInstall = entry.Compatibility != CatalogCompatibility.Incompatible &&
                (installedVersion is null || updateAvailable);
            InstallLabel = installedVersion is null
                ? "Install"
                : updateAvailable ? "Update" : "Up to date";
            InstallId = $"CatalogInstall_{entry.Plugin.Id}_{entry.Source.Id}";
            InstalledLabel = installedVersion is null
                ? string.Empty
                : updateAvailable ? $"Installed v{installedVersion} · update available" : $"Installed v{installedVersion}";
            HasInstalledLabel = InstalledLabel.Length > 0;
        }

        /// <summary>Gets the entry automation id.</summary>
        public string EntryId { get; }

        /// <summary>Gets the plugin name.</summary>
        public string Title { get; }

        /// <summary>Gets the selected version label.</summary>
        public string VersionLabel { get; }

        /// <summary>Gets the publisher label.</summary>
        public string Publisher { get; }

        /// <summary>Gets the one-line summary.</summary>
        public string Summary { get; }

        /// <summary>Gets whether the summary should be shown.</summary>
        public bool HasSummary { get; }

        /// <summary>Gets the short compatibility badge.</summary>
        public string CompatibilityBadge { get; }

        /// <summary>Gets the compatibility explanation.</summary>
        public string CompatibilityLabel { get; }

        /// <summary>Gets the compatibility badge brush.</summary>
        public Brush CompatibilityBrush { get; }

        /// <summary>Gets the version notes.</summary>
        public string Changelog { get; }

        /// <summary>Gets whether the version notes should be shown.</summary>
        public bool HasChangelog { get; }

        /// <summary>Gets the plugin tags.</summary>
        public string Tags { get; }

        /// <summary>Gets whether tags should be shown.</summary>
        public bool HasTags { get; }

        /// <summary>Gets the source label.</summary>
        public string SourceLabel { get; }

        /// <summary>Gets the source display name used in install progress messages.</summary>
        public string SourceName { get; }

        /// <summary>Gets whether the install or update action is offered.</summary>
        public bool CanInstall { get; }

        /// <summary>Gets the install button label: Install, Update, or Up to date.</summary>
        public string InstallLabel { get; }

        /// <summary>Gets the install button automation id.</summary>
        public string InstallId { get; }

        /// <summary>Gets the installed-state label, when the plugin is installed.</summary>
        public string InstalledLabel { get; }

        /// <summary>Gets whether the installed-state label should be shown.</summary>
        public bool HasInstalledLabel { get; }

        private static Brush ResolveBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private static Brush ResolveBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}

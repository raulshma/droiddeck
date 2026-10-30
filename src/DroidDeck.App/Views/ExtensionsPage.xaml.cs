using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.Core;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Security;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugins;
using DroidDeck.Plugins.Catalog;
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
    private readonly RendererService _renderers;
    private readonly IDiagnosticsService _diagnostics;
    private readonly AppPaths _paths;
    private readonly PluginCatalogService _catalog;
    private readonly CatalogInstaller _installer;
    private readonly PluginAuditLog _audit;
    private readonly ISecretStore _secrets;
    private readonly ILogger<ExtensionsPage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private readonly ObservableCollection<PluginRow> _rows = [];
    private Dictionary<string, string> _updateVersions = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _rollbackVersions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Creates the extensions page.</summary>
    public ExtensionsPage(
        PluginHost plugins,
        RendererService renderers,
        IDiagnosticsService diagnostics,
        AppPaths paths,
        PluginCatalogService catalog,
        CatalogInstaller installer,
        PluginAuditLog audit,
        ISecretStore secrets,
        ILogger<ExtensionsPage> logger)
    {
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _renderers = renderers ?? throw new ArgumentNullException(nameof(renderers));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        PluginList.ItemsSource = _rows;
        _plugins.ResultsChanged += OnResultsChanged;
        _plugins.Registry.Changed += OnRegistryChanged;
        _plugins.Health.HealthChanged += OnHealthChanged;
        _renderers.Changed += OnRenderersChanged;
        _renderers.Catalog.Changed += OnRenderersChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        UpdateSummary();
        UpdateCommandRows();
        UpdateRendererRows();
        UpdateRows();
        RestartNotice.IsOpen = _plugins.RestartRequired;
        _ = RefreshInstallStateAsync();
    }

    /// <summary>
    /// Computes which plugins have a catalog update or a rollback backup, then
    /// re-renders the rows. Manual only: this runs when the page refreshes, never
    /// on a timer, and nothing is downloaded or installed by it.
    /// </summary>
    private async Task RefreshInstallStateAsync()
    {
        try
        {
            if (!_catalog.IsInitialized)
            {
                await _catalog.InitializeAsync();
            }

            var rollback = await _installer.GetRollbackVersionsAsync();
            var installed = await _installer.GetInstalledAsync();
            var installedVersions = installed.ToDictionary(
                plugin => plugin.PluginId,
                plugin => plugin.Version,
                StringComparer.OrdinalIgnoreCase);
            var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _catalog.Browse(new CatalogQuery()))
            {
                if (entry.Compatibility == CatalogCompatibility.Incompatible ||
                    !installedVersions.TryGetValue(entry.Plugin.Id, out var current))
                {
                    continue;
                }

                // Merged sources can offer several newer versions; keep the
                // highest so the shown update never trails what the update
                // would actually install.
                if (SemanticVersion.TryParse(entry.Version.Version, out var candidate) &&
                    SemanticVersion.TryParse(current, out var parsedCurrent) &&
                    candidate > parsedCurrent &&
                    (!updates.TryGetValue(entry.Plugin.Id, out var best) ||
                        !SemanticVersion.TryParse(best, out var bestVersion) ||
                        candidate > bestVersion))
                {
                    updates[entry.Plugin.Id] = entry.Version.Version;
                }
            }

            _rollbackVersions = new Dictionary<string, string>(rollback, StringComparer.OrdinalIgnoreCase);
            _updateVersions = updates;
            Dispatch(UpdateRows);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Catalog update detection for the extensions page failed.");
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
        _plugins.ResultsChanged -= OnResultsChanged;
        _plugins.Registry.Changed -= OnRegistryChanged;
        _plugins.Health.HealthChanged -= OnHealthChanged;
        _renderers.Changed -= OnRenderersChanged;
        _renderers.Catalog.Changed -= OnRenderersChanged;
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
            var awaitingTrust = _plugins.Statuses.Count(status => status.State == PluginState.TrustRequired);
            var unhealthy = _plugins.Health.Snapshot().Count(health => !health.IsHealthy);
            PluginsStatusText.Text = failed > 0
                ? $"{_plugins.LoadedCount} plugin(s) loaded, {failed} failed. Review the reasons below and the Activity log."
                : awaitingTrust > 0
                    ? $"{_plugins.LoadedCount} plugin(s) loaded, {awaitingTrust} awaiting approval. Review the plugin files before choosing Trust."
                    : unhealthy == 0
                        ? $"{_plugins.LoadedCount} plugin(s) loaded. No plugin issues were reported."
                        : $"{_plugins.LoadedCount} plugin(s) loaded, {unhealthy} unhealthy. See the health details below.";
        }

        var registry = _plugins.Registry;
        BuiltInSummaryText.Text =
            $"{registry.Commands.Count} commands · {registry.ProjectDetectors.Count} project detectors · " +
            $"{registry.DeviceActions.Count} device actions · {registry.RunSteps.Count} run steps · " +
            $"{registry.LogParsers.Count} log parsers · {registry.HighlightRuleProviders.Count} highlight rule providers · " +
            $"{registry.TestRunners.Count} test runners · {registry.Renderers.Count} renderers available.";
    }

    private void UpdateRows()
    {
        _rows.Clear();
        foreach (var status in _plugins.Statuses)
        {
            var health = _plugins.Health.Get(status.Id);
            _updateVersions.TryGetValue(status.Id, out var updateVersion);
            _rollbackVersions.TryGetValue(status.Id, out var rollbackVersion);
            _rows.Add(new PluginRow(status, health, updateVersion, rollbackVersion));
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

    private void UpdateRendererRows()
    {
        var rows = _renderers.Catalog.Statuses.Select(RendererRow.From).ToArray();
        RendererList.ItemsSource = rows;
        NoRenderersText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnResultsChanged(object? sender, EventArgs e) => Dispatch(Refresh);

    private void OnRegistryChanged(object? sender, EventArgs e) => Dispatch(Refresh);

    private void OnHealthChanged(object? sender, EventArgs e) => Dispatch(Refresh);

    private void OnRenderersChanged(object? sender, EventArgs e) => Dispatch(UpdateRendererRows);

    private async void OnResetRendererChoicesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _renderers.ResetDefaultsAsync();
            UpdateRendererRows();
            ShowAction("View as choices were reset to the highest-priority renderer.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Renderer choices could not be reset.");
            ShowAction("The renderer choices could not be saved.", isError: true);
        }
    }

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

    private async void OnTrustPluginClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginRow { CanTrust: true } row)
        {
            return;
        }

        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        try
        {
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = row.CapabilitiesLabel,
            });
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text =
                    $"Approving records a SHA-256 fingerprint of the current plugin files ({row.CurrentHashLabel}). " +
                    "If any of those files change, DroidDeck asks again before loading the plugin. " +
                    "Only approve plugins whose files and source you trust. Approval does not run the plugin until DroidDeck restarts.",
            });
            content.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75,
                Text =
                    "Capabilities describe which extension points the plugin declares. " +
                    "They are disclosed here, not enforced as a sandbox.",
            });

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Trust {row.Title}?",
                Content = content,
                PrimaryButtonText = "Trust",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var trusted = await _plugins.TrustAsync(row.PluginId);
            if (trusted)
            {
                await RecordTrustAuditAsync(row.PluginId, row.Version);
                ShowAction($"Approved {row.Title}. Restart DroidDeck to load it.");
            }
            else
            {
                ShowAction($"{row.Title} no longer needs approval.");
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The plugin {PluginId} could not be trusted.", row.PluginId);
            ShowAction(
                $"Trusting {row.Title} failed: {exception.Message} (Diagnostics ID {_diagnostics.DiagnosticsId}).",
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

    private async void OnUpdatePluginClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginRow { CanUpdate: true } row ||
            sender is not Button button)
        {
            return;
        }

        // Match the exact entry the row advertised (id and version): browse
        // order alone could pick a different version from another merged
        // source than the one the user was shown.
        var entry = _catalog.Browse(new CatalogQuery()).FirstOrDefault(candidate =>
            string.Equals(candidate.Plugin.Id, row.PluginId, StringComparison.OrdinalIgnoreCase) &&
            candidate.Compatibility != CatalogCompatibility.Incompatible &&
            string.Equals(candidate.Version.Version, row.UpdateVersion, StringComparison.Ordinal));
        if (entry is null)
        {
            ShowAction($"No catalog entry for {row.Title} is available right now. Refresh the Catalog page.", isError: true);
            return;
        }

        button.IsEnabled = false;
        ShowAction($"Downloading and verifying {row.Title} v{entry.Version.Version} from {entry.Source.Name}…");
        try
        {
            var result = await _installer.UpdateAsync(entry, ShowTrustDecisionAsync);
            if (result.Succeeded)
            {
                ShowAction($"Updated {row.Title} to v{result.ToVersion}. The previous version " +
                    $"{result.FromVersion} is kept for rollback. Approve the new files and restart " +
                    "DroidDeck to load them.");
            }
            else
            {
                ShowAction(result.Error ?? "The update failed.", isError: true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Updating {PluginId} failed.", row.PluginId);
            ShowAction($"Updating {row.Title} failed: {exception.Message} (Diagnostics ID {_diagnostics.DiagnosticsId}).",
                isError: true);
        }
        finally
        {
            button.IsEnabled = true;
            _ = RefreshInstallStateAsync();
        }
    }

    private async void OnRollbackPluginClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginRow { CanRollback: true } row ||
            sender is not Button button)
        {
            return;
        }

        // A concurrent refresh can replace the version dictionaries after the
        // row was rendered, so never index directly: a missing version just
        // disables the button until the next render shows the current state.
        if (!_rollbackVersions.TryGetValue(row.PluginId, out var rollbackVersion))
        {
            button.IsEnabled = false;
            ShowAction(
                $"No rollback backup for {row.Title} is available right now. Refresh Extensions.",
                isError: true);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Roll back {row.Title} to v{rollbackVersion}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"This replaces version {row.Version} with the kept version " +
                    $"{rollbackVersion}. The restored files match the hash you approved " +
                    "before, so no new approval is needed. Restart DroidDeck to load the restored version." +
                    (row.State == PluginState.Loaded
                        ? " Restart DroidDeck first if the plugin is loaded; its files are in use."
                        : string.Empty),
            },
            PrimaryButtonText = "Roll back",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            var result = await _installer.RollbackAsync(row.PluginId);
            ShowAction(result.Succeeded
                ? $"Rolled back {row.Title} to v{result.ToVersion}. Restart DroidDeck to load it."
                : result.Error ?? "The rollback failed.", isError: !result.Succeeded);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Rolling back {PluginId} failed.", row.PluginId);
            ShowAction($"Rolling back {row.Title} failed: {exception.Message} (Diagnostics ID {_diagnostics.DiagnosticsId}).",
                isError: true);
        }
        finally
        {
            button.IsEnabled = true;
            _ = RefreshInstallStateAsync();
        }
    }

    private async void OnUninstallPluginClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginRow row || sender is not Button button)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Uninstall {row.Title}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"This removes the plugin files of version {row.Version} from disk, including its " +
                    "data folder. The catalog source it came from is not changed, and the removal is " +
                    "recorded in the plugin audit log." +
                    (row.State == PluginState.Loaded
                        ? " Restart DroidDeck first if the plugin is loaded; its files are in use."
                        : string.Empty),
            },
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            var result = await _installer.UninstallAsync(row.PluginId);
            ShowAction(result.Succeeded
                ? $"Uninstalled {row.Title}. The plugin list updates after DroidDeck restarts."
                : result.Error ?? "The uninstall failed.", isError: !result.Succeeded);
            _ = RefreshInstallStateAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Uninstalling {PluginId} failed.", row.PluginId);
            ShowAction($"Uninstalling {row.Title} failed: {exception.Message} (Diagnostics ID {_diagnostics.DiagnosticsId}).",
                isError: true);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnPluginSettingsClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PluginRow row)
        {
            return;
        }

        if (sender is Button button)
        {
            button.IsEnabled = false;
        }

        try
        {
            await PluginSettingsDialogs.ShowAsync(XamlRoot, row, _secrets);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The settings for {PluginId} could not be opened or saved.", row.PluginId);
            ShowAction(
                $"The settings for {row.Title} could not be opened or saved: {exception.Message}",
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

    private async Task RecordTrustAuditAsync(string pluginId, string version)
    {
        try
        {
            await _audit.AppendAsync(new PluginAuditEntry(
                DateTimeOffset.UtcNow,
                "trust",
                pluginId,
                FromVersion: version,
                ToVersion: version,
                Result: "success",
                Reason: null,
                SourceId: null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The trust audit entry for {PluginId} could not be written.", pluginId);
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
            OpenFolderInExplorer(target);
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
            OpenFolderInExplorer(_paths.LogsDirectory);
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
            OpenFolderInExplorer(_paths.PluginsDirectory);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(exception, "The plugins folder could not be opened.");
            ShowAction("The plugins folder could not be opened.", isError: true);
        }
    }

    private static void OpenFolderInExplorer(string path)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true,
        });
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

    /// <summary>One registered renderer shaped for the extensions list.</summary>
    public sealed class RendererRow
    {
        private RendererRow(RendererStatus status)
        {
            Title = status.DisplayName;
            Subtitle = status.Renderer.IsBuiltIn
                ? $"Built-in · {status.Kind} · priority {status.Renderer.Priority}"
                : $"{status.Renderer.PluginId} · {status.Kind} · priority {status.Renderer.Priority}";
            Health = status.Health switch
            {
                RendererHealth.Healthy => "Healthy",
                RendererHealth.Failing => $"Failing ({status.FailureCount})",
                RendererHealth.Disabled => "Disabled",
                _ => "Not used yet",
            };
        }

        /// <summary>Gets the renderer display name.</summary>
        public string Title { get; }

        /// <summary>Gets the kind, owner, and priority.</summary>
        public string Subtitle { get; }

        /// <summary>Gets the health label.</summary>
        public string Health { get; }

        /// <summary>Creates a row from a renderer status.</summary>
        public static RendererRow From(RendererStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            return new RendererRow(status);
        }
    }

    /// <summary>One installed plugin shaped for the extensions list.</summary>
    public sealed class PluginRow
    {
        /// <summary>Creates a row from a plugin status, its health, and its catalog install state.</summary>
        public PluginRow(
            PluginStatus status,
            PluginHealthSnapshot? health,
            string? updateVersion,
            string? rollbackVersion)
        {
            ArgumentNullException.ThrowIfNull(status);
            PluginId = status.Id;
            InstallPath = status.InstallPath;
            State = status.State;
            EnableId = $"EnablePlugin_{status.Id}";
            DisableId = $"DisablePlugin_{status.Id}";
            TrustId = $"TrustPlugin_{status.Id}";
            FolderId = $"PluginFolder_{status.Id}";
            LogsId = $"PluginLogs_{status.Id}";
            SettingsId = $"PluginSettings_{status.Id}";
            UpdateId = $"UpdatePlugin_{status.Id}";
            RollbackId = $"RollbackPlugin_{status.Id}";
            UninstallId = $"UninstallPlugin_{status.Id}";
            Title = status.Name;
            Version = status.Version;
            Subtitle = string.IsNullOrWhiteSpace(status.Author)
                ? $"{status.Id} · version {status.Version} · API {status.ApiVersion}"
                : $"{status.Id} · version {status.Version} · API {status.ApiVersion} · by {status.Author}";
            CanUpdate = updateVersion is not null;
            UpdateVersion = updateVersion;
            UpdateLabel = updateVersion is null ? string.Empty : $"Update to v{updateVersion}";
            CanRollback = rollbackVersion is not null;
            RollbackLabel = rollbackVersion is null ? string.Empty : $"Roll back to v{rollbackVersion}";
            var capabilities = PluginCapabilities.Parse(status.Capabilities);
            CapabilitiesLabel = capabilities.Known == PluginCapability.None && !capabilities.HasUnknown
                ? "Declared capabilities: none."
                : capabilities.HasUnknown
                    ? $"Declared capabilities: {PluginCapabilities.ToDisplayString(capabilities.Known)} " +
                        $"(unrecognized: {string.Join(", ", capabilities.Unknown)})."
                    : $"Declared capabilities: {PluginCapabilities.ToDisplayString(capabilities.Known)}.";
            CurrentHashLabel = status.ContentHash is { Length: > 0 } hash
                ? hash[..Math.Min(12, hash.Length)]
                : "unavailable";
            Status = status.StatusLabel;
            StatusBrush = ResolveBrush(status.State switch
            {
                PluginState.Loaded => "SystemFillColorSuccessBrush",
                PluginState.Disabled or PluginState.EnabledPendingRestart or PluginState.TrustRequired =>
                    "SystemFillColorCautionBrush",
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

        /// <summary>Gets whether the plugin needs a trust decision before it can load.</summary>
        public bool CanTrust => State == PluginState.TrustRequired;

        /// <summary>Gets the enable button automation id.</summary>
        public string EnableId { get; }

        /// <summary>Gets the disable button automation id.</summary>
        public string DisableId { get; }

        /// <summary>Gets the trust button automation id.</summary>
        public string TrustId { get; }

        /// <summary>Gets the short content hash shown before approval.</summary>
        public string CurrentHashLabel { get; }

        /// <summary>Gets the open-folder button automation id.</summary>
        public string FolderId { get; }

        /// <summary>Gets the view-logs button automation id.</summary>
        public string LogsId { get; }

        /// <summary>Gets the settings button automation id.</summary>
        public string SettingsId { get; }

        /// <summary>Gets the update button automation id.</summary>
        public string UpdateId { get; }

        /// <summary>Gets the rollback button automation id.</summary>
        public string RollbackId { get; }

        /// <summary>Gets the uninstall button automation id.</summary>
        public string UninstallId { get; }

        /// <summary>Gets whether a newer catalog version is available for this plugin.</summary>
        public bool CanUpdate { get; }

        /// <summary>Gets the catalog version the update button offers, when one is available.</summary>
        public string? UpdateVersion { get; }

        /// <summary>Gets the update button label, when an update is available.</summary>
        public string UpdateLabel { get; }

        /// <summary>Gets whether a rollback backup exists for this plugin.</summary>
        public bool CanRollback { get; }

        /// <summary>Gets the rollback button label, when a backup exists.</summary>
        public string RollbackLabel { get; }

        /// <summary>Gets the plugin name or failure title.</summary>
        public string Title { get; }

        /// <summary>Gets the plugin version.</summary>
        public string Version { get; }

        /// <summary>Gets the plugin identity line.</summary>
        public string Subtitle { get; }

        /// <summary>Gets the declared capabilities shown before approval.</summary>
        public string CapabilitiesLabel { get; }

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

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        UpdateSummary();
        UpdateCommandRows();
        UpdateRows();
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
            var failed = _plugins.Results.Count(result => !result.Succeeded);
            PluginsStatusText.Text = failed == 0
                ? $"{_plugins.LoadedCount} plugin(s) loaded. No plugin issues were reported."
                : $"{_plugins.LoadedCount} plugin(s) loaded, {failed} failed. Review the issues below and the Activity log.";
        }

        var registry = _plugins.Registry;
        BuiltInSummaryText.Text =
            $"{registry.Commands.Count} commands · {registry.ProjectDetectors.Count} project detectors · " +
            $"{registry.DeviceActions.Count} device actions · {registry.RunSteps.Count} run steps available.";
    }

    private void UpdateRows()
    {
        _rows.Clear();
        foreach (var result in _plugins.Results)
        {
            _rows.Add(new PluginRow(result));
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

    /// <summary>One plugin result shaped for the extensions list.</summary>
    public sealed class PluginRow
    {
        /// <summary>Creates a row from a plugin load result.</summary>
        public PluginRow(PluginLoadResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (result.Plugin is { } plugin)
            {
                Title = plugin.Name;
                Subtitle = $"{plugin.Id} · version {plugin.Version} · API {plugin.ApiVersion}";
            }
            else
            {
                Title = "Plugin failed to load";
                Subtitle = "A plugin folder did not pass validation or could not be loaded.";
            }

            Status = result.Succeeded ? "Loaded" : "Failed";
            StatusBrush = ResolveBrush(result.Succeeded
                ? "SystemFillColorSuccessBrush"
                : "SystemFillColorCriticalBrush");
            Issues = string.Join(
                Environment.NewLine,
                result.Validation.Issues.Select(issue => issue.ToString()));
        }

        /// <summary>Gets the plugin name or failure title.</summary>
        public string Title { get; }

        /// <summary>Gets the plugin identity line.</summary>
        public string Subtitle { get; }

        /// <summary>Gets the load status.</summary>
        public string Status { get; }

        /// <summary>Gets the status brush.</summary>
        public Brush StatusBrush { get; }

        /// <summary>Gets validation and load issues.</summary>
        public string Issues { get; }

        /// <summary>Gets whether issues should be shown.</summary>
        public bool HasIssues => Issues.Length > 0;

        private static Brush ResolveBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}

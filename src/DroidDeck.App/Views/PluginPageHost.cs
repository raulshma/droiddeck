using DroidDeck.App.Services;
using DroidDeck.Plugins;
using DroidDeck.Plugin.UI.Kit;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.App.Views;

/// <summary>
/// Hosts one plugin navigation page. The plugin view is created lazily on
/// first load through the isolated invoker, and rendered inside a fault
/// boundary so a failing plugin shows a fallback card instead of breaking
/// navigation.
/// </summary>
public sealed class PluginPageHost : Page, IDisposable
{
    private readonly NavigationPageEntry _entry;
    private readonly PluginHost _plugins;
    private readonly PluginFaultBoundary _boundary;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _loaded;
    private bool _disposed;

    /// <summary>Creates a host page for a plugin navigation page.</summary>
    public PluginPageHost(
        NavigationPageEntry entry,
        PluginHost plugins,
        PluginUiFaultReporter faultReporter,
        ILogger logger)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var title = new TextBlock
        {
            Text = entry.Page.Title,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var source = new TextBlock
        {
            Text = $"Plugin page · {entry.SourceLabel}",
            FontSize = 12,
            Foreground = ResolveSecondaryBrush(),
            TextWrapping = TextWrapping.Wrap,
        };

        _boundary = new PluginFaultBoundary(entry.PluginId, $"navigation page {entry.Page.Id}", faultReporter);
        var root = new StackPanel { Spacing = 12, Padding = new Thickness(24) };
        root.Children.Add(new StackPanel { Spacing = 2, Children = { title, source } });
        root.Children.Add(_boundary);

        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await LoadAsync().ConfigureAwait(true);

    private async Task LoadAsync()
    {
        if (_loaded || _disposed)
        {
            return;
        }

        _loaded = true;
        var operation = $"navigation page {_entry.Page.Id}";
        try
        {
            var result = await _plugins.Invoker.CreateViewAsync(
                _entry.PluginId,
                operation,
                token => _entry.Page.CreateViewAsync(token),
                _cancellation.Token).ConfigureAwait(true);
            _boundary.Show(result.Content, result.Error);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Plugin page {PageId} from {PluginId} could not load: {Error}",
                    _entry.Page.Id,
                    _entry.PluginId,
                    result.Error);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Hosting plugin page {PageId} failed.", _entry.Page.Id);
            try
            {
                _boundary.Show(null, exception.Message);
            }
            catch
            {
                // The boundary is the last resort; never fail the host because of it.
            }
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
        Loaded -= OnLoaded;
        _cancellation.Cancel();
        _cancellation.Dispose();
        _boundary.Dispose();
    }

    private static Brush ResolveSecondaryBrush() =>
        Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var value) &&
        value is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}

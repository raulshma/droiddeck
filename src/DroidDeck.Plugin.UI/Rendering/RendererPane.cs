using DroidDeck.Plugin.Abstractions.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.Plugin.UI.Rendering;

/// <summary>The renderer services a View as pane needs from the shell.</summary>
public interface IRendererHost
{
    /// <summary>Gets the renderer catalog that owns selection and health.</summary>
    RendererCatalog Catalog { get; }

    /// <summary>Gets the available renderers of a kind in registration order.</summary>
    IReadOnlyList<RendererStatus> Renderers(RendererKind kind);

    /// <summary>Creates a renderer instance. The caller owns and disposes it.</summary>
    IRenderer CreateRenderer(RendererDescriptor descriptor);

    /// <summary>Remembers a renderer as the default for a kind.</summary>
    Task SetDefaultAsync(
        RendererKind kind,
        string rendererId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets theme facts for renderers.</summary>
    IThemeService Theme { get; }

    /// <summary>Gets the dispatcher renderers use for async loading.</summary>
    IUiDispatcher Dispatcher { get; }

    /// <summary>Raised when renderers or their health change.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// A reusable View as surface: it lists the renderers that accept the current
/// item, creates the selected renderer, and shows a warning when the renderer
/// failed and the built-in view is used instead.
/// </summary>
public sealed class RendererPane : UserControl, IDisposable
{
    private readonly IRendererHost _host;
    private readonly IServiceProvider _services;
    private readonly ComboBox _selector = new()
    {
        MinWidth = 220,
        DisplayMemberPath = nameof(RendererChoice.Display),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly InfoBar _errorBar = new()
    {
        Severity = InfoBarSeverity.Warning,
        IsOpen = false,
        IsClosable = true,
    };
    private readonly ContentPresenter _presenter = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
    };
    private readonly TextBlock _emptyText = new()
    {
        Text = "No renderer accepts this item.",
        FontSize = 12,
        Opacity = 0.7,
        Margin = new Thickness(8),
    };
    private readonly Dictionary<string, IRenderer> _instances = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private RendererKind? _kind;
    private object? _data;
    private bool _updatingSelection;
    private bool _rendering;
    private bool _disposed;

    /// <summary>Creates a pane bound to the shell renderer host.</summary>
    public RendererPane(IRendererHost host, IServiceProvider services)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _services = services ?? throw new ArgumentNullException(nameof(services));

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_selector, "ViewAsSelector");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_errorBar, "RendererErrorBar");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_presenter, "RendererContent");

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(new TextBlock
        {
            Text = "View as",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(_selector);

        var root = new Grid { RowSpacing = 6 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(_errorBar, 1);
        var content = new Grid();
        content.Children.Add(_presenter);
        content.Children.Add(_emptyText);
        Grid.SetRow(content, 2);
        root.Children.Add(header);
        root.Children.Add(_errorBar);
        root.Children.Add(content);
        Content = root;

        _selector.SelectionChanged += OnSelectionChanged;
        _host.Changed += OnHostChanged;
    }

    /// <summary>Renders an item with the remembered renderer or the best candidate.</summary>
    public void Render(RendererKind kind, object data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _rendering = true;
        try
        {
            _kind = kind;
            _data = data;
            _errorBar.IsOpen = false;
            DisposeViews();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var rendererStatuses = _host.Renderers(kind);
            var choices = new List<RendererChoice>();
            foreach (var status in rendererStatuses)
            {
                if (!status.IsAvailable)
                {
                    continue;
                }

                try
                {
                    var renderer = GetOrCreate(status.Renderer);
                    var probe = CreateContext(kind, data, token);
                    if (renderer.CanRender(probe))
                    {
                        choices.Add(new RendererChoice(status));
                    }
                }
                catch (Exception)
                {
                    // A renderer that cannot even describe itself is skipped;
                    // the host catalog records creation failures.
                }
            }

            _updatingSelection = true;
            try
            {
                _selector.ItemsSource = choices;
                _selector.IsEnabled = choices.Count > 0;
            }
            finally
            {
                _updatingSelection = false;
            }

            if (choices.Count == 0)
            {
                _presenter.Content = null;
                _emptyText.Visibility = Visibility.Visible;
                return;
            }

            _emptyText.Visibility = Visibility.Collapsed;
            var allowed = choices.Select(choice => choice.Status.Id).ToHashSet(StringComparer.Ordinal);
            var context = CreateContext(kind, data, token);
            var result = RendererRunner.Render(
                _host.Catalog,
                kind,
                preferredId: null,
                canRender: descriptor => allowed.Contains(descriptor.Id),
                create: descriptor => GetOrCreate(descriptor).CreateView(context));

            if (result.View is FrameworkElement view)
            {
                _presenter.Content = view;
                _emptyText.Visibility = Visibility.Collapsed;
            }
            else
            {
                _presenter.Content = null;
                _emptyText.Visibility = Visibility.Visible;
                _emptyText.Text = "The renderer did not produce a view.";
            }

            if (result.Error is not null)
            {
                var target = result.View is null
                    ? "No renderer could display this item."
                    : $"The built-in \u201c{result.Renderer?.DisplayName}\u201d view is shown instead.";
                _errorBar.Title = "A renderer failed";
                _errorBar.Message = $"{target} {result.Error.Message}";
                _errorBar.IsOpen = true;
            }

            _updatingSelection = true;
            try
            {
                _selector.SelectedItem = choices.FirstOrDefault(choice =>
                    string.Equals(choice.Status.Id, result.Renderer?.Id, StringComparison.Ordinal));
            }
            finally
            {
                _updatingSelection = false;
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>Clears the rendered item and releases the renderer.</summary>
    public void Clear()
    {
        _kind = null;
        _data = null;
        _presenter.Content = null;
        _errorBar.IsOpen = false;
        DisposeViews();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _selector.SelectionChanged -= OnSelectionChanged;
        _host.Changed -= OnHostChanged;
        DisposeViews();
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || _rendering || _kind is not { } kind || _data is null)
        {
            return;
        }

        if (_selector.SelectedItem is not RendererChoice choice)
        {
            return;
        }

        try
        {
            await _host.SetDefaultAsync(kind, choice.Status.Id);
            Render(kind, _data);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _errorBar.Title = "The renderer choice could not be saved";
            _errorBar.Message = exception.Message;
            _errorBar.IsOpen = true;
        }
    }

    private void OnHostChanged(object? sender, EventArgs e)
    {
        if (_rendering || _disposed)
        {
            return;
        }

        if (_kind is { } kind && _data is not null)
        {
            _ = _host.Dispatcher.InvokeAsync(() => Render(kind, _data));
        }
    }

    private RendererContext CreateContext(RendererKind kind, object data, CancellationToken token) =>
        new(
            data,
            kind,
            _services,
            _host.Theme,
            _host.Dispatcher,
            new Windows.Foundation.Size(
                Math.Max(320, ActualWidth),
                Math.Max(200, ActualHeight)),
            token);

    private IRenderer GetOrCreate(RendererDescriptor descriptor)
    {
        if (!_instances.TryGetValue(descriptor.Id, out var renderer))
        {
            renderer = _host.CreateRenderer(descriptor);
            _instances[descriptor.Id] = renderer;
        }

        return renderer;
    }

    private void DisposeViews()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        foreach (var renderer in _instances.Values)
        {
            try
            {
                renderer.Dispose();
            }
            catch (Exception)
            {
                // A plugin renderer must not break the pane while it is closing.
            }
        }

        _instances.Clear();
    }

    private sealed record RendererChoice(RendererStatus Status)
    {
        public string Display => Status.Renderer.IsBuiltIn
            ? Status.DisplayName
            : $"{Status.DisplayName}  \u00b7  {Status.Renderer.PluginId}";
    }
}

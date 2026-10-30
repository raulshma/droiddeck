using System.Reflection;
using DroidDeck.Core;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugin.UI.Rendering;
using DroidDeck.Plugins;
using DroidDeck.Plugins.Rendering;
using Microsoft.Extensions.Logging;

namespace DroidDeck.App.Services;

/// <summary>
/// Projects the plugin registry's renderer types into the selection catalog,
/// persists View as choices, and creates renderer instances on demand.
/// </summary>
public sealed class RendererService : IRendererHost, IDisposable
{
    private readonly PluginRegistry _registry;
    private readonly AppPaths _paths;
    private readonly AppThemeService _theme;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<RendererService> _logger;
    private readonly Dictionary<string, Type> _types = new(StringComparer.Ordinal);
    private readonly HashSet<string> _registeredIds = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private RendererPreferences? _preferences;
    private bool _dirty = true;
    private bool _disposed;

    /// <summary>Creates the renderer host over the shared plugin registry.</summary>
    public RendererService(
        PluginRegistry registry,
        AppPaths paths,
        AppThemeService theme,
        IUiDispatcher dispatcher,
        ILogger<RendererService> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _registry.Changed += OnRegistryChanged;
    }

    /// <inheritdoc />
    public RendererCatalog Catalog { get; } = new();

    /// <inheritdoc />
    public IThemeService Theme => _theme;

    /// <inheritdoc />
    public IUiDispatcher Dispatcher => _dispatcher;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Loads remembered choices and registers the renderers available so far.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _preferences = await RendererPreferences
            .LoadAsync(_paths.DataDirectory, cancellationToken)
            .ConfigureAwait(false);
        ApplySavedDefaults();
        Sync();
        RaiseChanged();
    }

    /// <inheritdoc />
    public IReadOnlyList<RendererStatus> Renderers(RendererKind kind)
    {
        Sync();
        return Catalog.Statuses.Where(status => status.Kind == kind).ToArray();
    }

    /// <inheritdoc />
    public IRenderer CreateRenderer(RendererDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Sync();

        Type? rendererType;
        lock (_sync)
        {
            _types.TryGetValue(descriptor.Id, out rendererType);
        }

        return (rendererType is null ? null : Activator.CreateInstance(rendererType) as IRenderer)
            ?? throw new InvalidOperationException($"The renderer '{descriptor.Id}' is no longer available.");
    }

    /// <inheritdoc />
    public async Task SetDefaultAsync(
        RendererKind kind,
        string rendererId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rendererId);
        Catalog.SetDefault(kind, rendererId);
        var preferences = _preferences ??= await RendererPreferences
            .LoadAsync(_paths.DataDirectory, cancellationToken)
            .ConfigureAwait(false);
        await preferences.SetDefaultAsync(kind, rendererId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears every remembered renderer choice.</summary>
    public async Task ResetDefaultsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var kind in Enum.GetValues<RendererKind>())
        {
            Catalog.SetDefault(kind, null);
        }

        var preferences = _preferences ??= await RendererPreferences
            .LoadAsync(_paths.DataDirectory, cancellationToken)
            .ConfigureAwait(false);
        await preferences.ResetAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registry.Changed -= OnRegistryChanged;
    }

    private void OnRegistryChanged(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            _dirty = true;
        }

        try
        {
            Sync();
            RaiseChanged();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The renderer catalog could not be refreshed.");
        }
    }

    private void Sync()
    {
        lock (_sync)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            var current = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (var registration in _registry.Renderers)
            {
                if (Describe(registration) is not { } descriptor)
                {
                    continue;
                }

                current[descriptor.Id] = registration.RendererType;
                Catalog.Register(descriptor);
            }

            foreach (var id in _registeredIds.Where(id => !current.ContainsKey(id)).ToArray())
            {
                Catalog.Remove(id);
            }

            _registeredIds.Clear();
            _types.Clear();
            foreach (var pair in current)
            {
                _registeredIds.Add(pair.Key);
                _types[pair.Key] = pair.Value;
            }
        }
    }

    private RendererDescriptor? Describe(RendererRegistration registration)
    {
        IRenderer? renderer = null;
        try
        {
            renderer = Activator.CreateInstance(registration.RendererType) as IRenderer;
            return renderer is null
                ? null
                : new RendererDescriptor(
                    renderer.Id,
                    registration.PluginId,
                    renderer.Kind,
                    renderer.DisplayName,
                    renderer.Priority,
                    registration.PluginId == PluginConstants.BuiltInPluginId);
        }
        catch (Exception exception) when (
            exception is MissingMethodException or MemberAccessException or TargetInvocationException or
                InvalidCastException or TypeLoadException or FileNotFoundException or FileLoadException)
        {
            _logger.LogWarning(
                exception,
                "Renderer {RendererType} could not be inspected and was skipped.",
                registration.RendererType.FullName);
            return null;
        }
        finally
        {
            renderer?.Dispose();
        }
    }

    private void ApplySavedDefaults()
    {
        if (_preferences is null)
        {
            return;
        }

        foreach (var pair in _preferences.Defaults)
        {
            var existing = Catalog.Find(pair.Value);
            try
            {
                if (existing is null || existing.Kind == pair.Key)
                {
                    Catalog.SetDefault(pair.Key, pair.Value);
                }
            }
            catch (ArgumentException exception)
            {
                _logger.LogWarning(
                    exception,
                    "The saved {Kind} renderer {RendererId} no longer matches its surface.",
                    pair.Key,
                    pair.Value);
            }
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A renderer change handler failed.");
        }
    }
}

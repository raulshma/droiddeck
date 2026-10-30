using DroidDeck.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Catalog;

/// <summary>One source that failed during a refresh.</summary>
/// <param name="SourceId">The source identifier.</param>
/// <param name="SourceName">The source display name.</param>
/// <param name="Message">A clear explanation of the failure.</param>
public sealed record CatalogSourceError(string SourceId, string SourceName, string Message);

/// <summary>The outcome of refreshing every enabled catalog source.</summary>
/// <param name="Errors">The sources that could not be refreshed.</param>
public sealed record CatalogRefreshResult(IReadOnlyList<CatalogSourceError> Errors)
{
    /// <summary>Gets a value indicating whether every enabled source refreshed.</summary>
    public bool Succeeded => Errors.Count == 0;
}

/// <summary>The browse state of one catalog source.</summary>
/// <param name="Source">The source.</param>
/// <param name="IsFromCache">Whether the current data came from the offline cache.</param>
/// <param name="FetchedAt">When the current data was fetched.</param>
/// <param name="Error">The last refresh error, if any.</param>
public sealed record CatalogSourceStatus(
    CatalogSource Source,
    bool IsFromCache,
    DateTimeOffset? FetchedAt,
    string? Error);

/// <summary>
/// Aggregates the user's catalog sources for the catalog page: it loads cached
/// catalogs on startup so browsing works offline, refreshes sources on request,
/// and exposes search over the merged results. It never downloads a package,
/// installs a plugin, or records a trust decision.
/// </summary>
public sealed class PluginCatalogService
{
    private readonly AppPaths _paths;
    private readonly PluginCatalogClient _client;
    private readonly string _hostVersion;
    private readonly string _hostApiVersion;
    private readonly ILogger<PluginCatalogService> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, SourceData> _data = new(StringComparer.OrdinalIgnoreCase);
    private CatalogSourceStore? _store;

    /// <summary>Creates a catalog service.</summary>
    public PluginCatalogService(
        AppPaths paths,
        PluginCatalogClient client,
        string hostVersion,
        string hostApiVersion,
        ILogger<PluginCatalogService>? logger = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostApiVersion);
        _hostVersion = hostVersion;
        _hostApiVersion = hostApiVersion;
        _logger = logger ?? NullLogger<PluginCatalogService>.Instance;
    }

    /// <summary>Raised after sources or browsable entries change.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the configured sources in priority order.</summary>
    public IReadOnlyList<CatalogSource> Sources => _store?.Sources ?? [];

    /// <summary>Gets the browse state of every configured source.</summary>
    public IReadOnlyList<CatalogSourceStatus> SourceStatuses
    {
        get
        {
            var sources = Sources;
            lock (_sync)
            {
                return sources
                    .Select(source => _data.TryGetValue(source.Id, out var data)
                        ? new CatalogSourceStatus(source, data.IsFromCache, data.FetchedAt, data.Error ?? source.LastError)
                        : new CatalogSourceStatus(source, false, null, source.LastError))
                    .ToArray();
            }
        }
    }

    /// <summary>Gets every tag declared by a browsable plugin, sorted alphabetically.</summary>
    public IReadOnlyList<string> AllTags
    {
        get
        {
            lock (_sync)
            {
                return _data.Values
                    .Where(data => data.Catalog is not null)
                    .SelectMany(data => data.Catalog!.Plugins)
                    .SelectMany(plugin => plugin.Tags ?? [])
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    /// <summary>Gets whether the source list and cached catalogs have been loaded.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>Loads the source list and every enabled cache, without using the network.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var store = await CatalogSourceStore.LoadAsync(_paths.CatalogDirectory, cancellationToken)
            .ConfigureAwait(false);
        var data = new Dictionary<string, SourceData>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in store.Sources.Where(source => source.Enabled))
        {
            var cached = await _client.LoadCachedAsync(source, cancellationToken).ConfigureAwait(false);
            if (cached.Succeeded)
            {
                data[source.Id] = new SourceData(cached.Catalog, IsFromCache: true, cached.FetchedAt, cached.Error);
            }
        }

        lock (_sync)
        {
            _store = store;
            _data.Clear();
            foreach (var pair in data)
            {
                _data[pair.Key] = pair.Value;
            }

            IsInitialized = true;
        }
    }

    /// <summary>Records a new source. This never downloads or installs a plugin.</summary>
    public async Task<CatalogSourceResult> AddSourceAsync(
        string name,
        string url,
        bool allowPrerelease = false,
        CancellationToken cancellationToken = default)
    {
        var store = _store;
        if (store is null)
        {
            throw new InvalidOperationException("The catalog service has not been initialized.");
        }

        var result = await store.AddAsync(name, url, allowPrerelease, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Source is null)
        {
            return result;
        }

        var cached = await _client.LoadCachedAsync(result.Source, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _data[result.Source.Id] = new SourceData(
                cached.Catalog,
                cached.IsFromCache,
                cached.FetchedAt,
                cached.Error);
        }

        RaiseChanged();
        return result;
    }

    /// <summary>Removes a source. Installed plugins and cached files are untouched.</summary>
    public async Task<bool> RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        var store = _store;
        if (store is null)
        {
            throw new InvalidOperationException("The catalog service has not been initialized.");
        }

        var removed = await store.RemoveAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            return false;
        }

        lock (_sync)
        {
            _data.Remove(sourceId);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Enables or disables a source and reloads its cache when it is enabled.</summary>
    public async Task<bool> SetSourceEnabledAsync(
        string sourceId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var store = _store;
        if (store is null)
        {
            throw new InvalidOperationException("The catalog service has not been initialized.");
        }

        if (!await store.SetEnabledAsync(sourceId, enabled, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var source = store.Sources.FirstOrDefault(
            candidate => string.Equals(candidate.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            return false;
        }

        if (!enabled)
        {
            lock (_sync)
            {
                _data.Remove(sourceId);
            }

            RaiseChanged();
            return true;
        }

        var cached = await _client.LoadCachedAsync(source, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _data[sourceId] = new SourceData(cached.Catalog, cached.IsFromCache, cached.FetchedAt, cached.Error);
        }

        RaiseChanged();
        return true;
    }

    /// <summary>Refreshes every enabled source on demand and merges the results.</summary>
    public async Task<CatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var store = _store;
        if (store is null)
        {
            throw new InvalidOperationException("The catalog service has not been initialized.");
        }

        var errors = new List<CatalogSourceError>();
        foreach (var source in store.Sources.Where(source => source.Enabled))
        {
            CatalogFetchResult result;
            try
            {
                result = await _client.FetchAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                result = new CatalogFetchResult(null, exception.Message, IsFromCache: false, FetchedAt: null);
            }

            if (result.Succeeded)
            {
                lock (_sync)
                {
                    _data[source.Id] = new SourceData(
                        result.Catalog,
                        result.IsFromCache,
                        result.FetchedAt,
                        result.Error);
                }
            }
            else if (result.Error is not null)
            {
                _logger.LogWarning(
                    "Catalog source {Source} could not be refreshed: {Error}",
                    source.Name,
                    result.Error);
            }

            if (result.Error is not null)
            {
                errors.Add(new CatalogSourceError(source.Id, source.Name, result.Error));
            }

            await store.UpdateStatusAsync(
                source.Id,
                DateTimeOffset.UtcNow,
                result.Error,
                cancellationToken).ConfigureAwait(false);
        }

        RaiseChanged();
        return new CatalogRefreshResult(errors);
    }

    /// <summary>Browses the merged catalogs with search, tags, source, and pre-release filters.</summary>
    public IReadOnlyList<CatalogEntry> Browse(CatalogQuery? query = null)
    {
        CatalogPluginSource[] plugins;
        lock (_sync)
        {
            var sources = _store?.Sources ?? [];
            var pairs = new List<CatalogPluginSource>();
            foreach (var pair in _data)
            {
                if (pair.Value.Catalog is null)
                {
                    continue;
                }

                var source = sources.FirstOrDefault(
                    candidate => string.Equals(candidate.Id, pair.Key, StringComparison.OrdinalIgnoreCase))
                    ?? new CatalogSource { Id = pair.Key, Name = pair.Key };
                foreach (var plugin in pair.Value.Catalog.Plugins)
                {
                    pairs.Add(new CatalogPluginSource(source, plugin));
                }
            }

            plugins = [.. pairs];
        }

        return CatalogBrowser.Browse(plugins, query, _hostVersion, _hostApiVersion);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed record SourceData(
        CatalogDocument? Catalog,
        bool IsFromCache,
        DateTimeOffset? FetchedAt,
        string? Error);
}

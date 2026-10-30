using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Catalog;

/// <summary>The outcome of fetching one catalog source.</summary>
/// <param name="Catalog">The catalog, or <see langword="null"/> when neither the source nor its cache was usable.</param>
/// <param name="Error">A clear explanation when the live fetch failed.</param>
/// <param name="IsFromCache">Whether the catalog came from the offline cache.</param>
/// <param name="FetchedAt">When the returned catalog was fetched.</param>
public sealed record CatalogFetchResult(
    CatalogDocument? Catalog,
    string? Error,
    bool IsFromCache,
    DateTimeOffset? FetchedAt)
{
    /// <summary>Gets a value indicating whether a catalog is available.</summary>
    public bool Succeeded => Catalog is not null;
}

/// <summary>
/// Fetches and validates catalogs from HTTPS or local sources. Every successful
/// fetch is cached so browsing keeps working offline; fetch failures fall back
/// to the cache and report why the live source could not be reached.
/// </summary>
public sealed class PluginCatalogClient
{
    /// <summary>The largest catalog this host accepts by default.</summary>
    public const int DefaultMaxBytes = 8 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly int _maxBytes;
    private readonly string _userAgent;
    private readonly ILogger<PluginCatalogClient> _logger;

    /// <summary>Creates a catalog client.</summary>
    public PluginCatalogClient(
        HttpClient httpClient,
        string catalogDirectory,
        int maxBytes = DefaultMaxBytes,
        string userAgent = "DroidDeck/1.0.0",
        ILogger<PluginCatalogClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        _cacheDirectory = Path.Combine(catalogDirectory, "cache");
        _maxBytes = maxBytes;
        _userAgent = userAgent;
        _logger = logger ?? NullLogger<PluginCatalogClient>.Instance;
    }

    /// <summary>Gets the cache file for a source.</summary>
    public string GetCachePath(CatalogSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Id);
        if (!string.Equals(Path.GetFileName(source.Id), source.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("The source id must be a simple file name.", nameof(source));
        }

        return Path.Combine(_cacheDirectory, source.Id + ".json");
    }

    /// <summary>Fetches a source, falling back to the cached catalog when the live fetch fails.</summary>
    public Task<CatalogFetchResult> FetchAsync(
        CatalogSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Url);
        if (CatalogSource.IsHttpsUrl(source.Url))
        {
            return FetchRemoteAsync(source, cancellationToken);
        }

        if (source.IsLocalPath)
        {
            return ReadLocalAsync(source, cancellationToken);
        }

        return Error(
            source,
            $"The source \"{source.Name}\" must use an HTTPS URL or a local catalog path.");
    }

    /// <summary>Loads the cached catalog for a source, when one exists and is valid.</summary>
    public async Task<CatalogFetchResult> LoadCachedAsync(
        CatalogSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var path = GetCachePath(source);
        if (!File.Exists(path))
        {
            return new CatalogFetchResult(null, null, IsFromCache: false, FetchedAt: null);
        }

        try
        {
            var capturedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            await using var stream = File.OpenRead(path);
            var read = await PluginCatalogParser.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            return read.Succeeded
                ? new CatalogFetchResult(read.Catalog, null, IsFromCache: true, capturedAt)
                : new CatalogFetchResult(
                    null,
                    $"The cached catalog from {source.Name} is no longer valid: {read.Validation.Summary}",
                    IsFromCache: true,
                    null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CatalogFetchResult(
                null,
                $"The cached catalog from {source.Name} could not be read: {exception.Message}",
                IsFromCache: true,
                null);
        }
    }

    private async Task<CatalogFetchResult> FetchRemoteAsync(
        CatalogSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
            request.Headers.UserAgent.ParseAdd(_userAgent);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // Redirects are followed by the HTTP stack, so the requested address being
            // HTTPS does not guarantee the catalog arrived over HTTPS.
            if (response.RequestMessage?.RequestUri is { } finalUri &&
                !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return await WithCacheAsync(
                    source,
                    $"The catalog at {source.Url} redirected to a non-HTTPS address; only HTTPS catalogs are allowed.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return await WithCacheAsync(
                    source,
                    $"The catalog at {source.Url} could not be fetched: the server returned " +
                    $"{(int)response.StatusCode} {response.ReasonPhrase}.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (response.Content.Headers.ContentLength is { } length && length > _maxBytes)
            {
                return await WithCacheAsync(
                    source,
                    $"The catalog at {source.Url} is too large ({length} bytes); the limit is {_maxBytes} bytes.",
                    cancellationToken).ConfigureAwait(false);
            }

            var bytes = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return await WithCacheAsync(
                    source,
                    $"The catalog at {source.Url} is too large; the limit is {_maxBytes} bytes.",
                    cancellationToken).ConfigureAwait(false);
            }

            using var stream = new MemoryStream(bytes);
            var read = await PluginCatalogParser.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!read.Succeeded)
            {
                return await WithCacheAsync(
                    source,
                    $"The catalog at {source.Url} is not valid: {read.Validation.Summary}",
                    cancellationToken).ConfigureAwait(false);
            }

            await WriteCacheAsync(source, bytes, cancellationToken).ConfigureAwait(false);
            return new CatalogFetchResult(
                read.Catalog,
                null,
                IsFromCache: false,
                DateTimeOffset.UtcNow);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await WithCacheAsync(
                source,
                $"Fetching the catalog from {source.Url} timed out.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            return await WithCacheAsync(
                source,
                $"The catalog at {source.Url} could not be fetched: {exception.Message}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<CatalogFetchResult> ReadLocalAsync(
        CatalogSource source,
        CancellationToken cancellationToken)
    {
        var path = source.Url;
        if (Directory.Exists(path))
        {
            path = Path.Combine(path, "catalog.json");
        }

        if (!File.Exists(path))
        {
            return await WithCacheAsync(
                source,
                $"The local catalog file '{path}' does not exist.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var length = new FileInfo(path).Length;
            if (length > _maxBytes)
            {
                return await WithCacheAsync(
                    source,
                    $"The local catalog '{path}' is too large ({length} bytes); the limit is {_maxBytes} bytes.",
                    cancellationToken).ConfigureAwait(false);
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            using var stream = new MemoryStream(bytes);
            var read = await PluginCatalogParser.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!read.Succeeded)
            {
                return await WithCacheAsync(
                    source,
                    $"The local catalog '{path}' is not valid: {read.Validation.Summary}",
                    cancellationToken).ConfigureAwait(false);
            }

            await WriteCacheAsync(source, bytes, cancellationToken).ConfigureAwait(false);
            return new CatalogFetchResult(
                read.Catalog,
                null,
                IsFromCache: false,
                DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return await WithCacheAsync(
                source,
                $"The local catalog '{path}' could not be read: {exception.Message}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<byte[]?> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private async Task WriteCacheAsync(
        CatalogSource source,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = GetCachePath(source);
            Directory.CreateDirectory(_cacheDirectory);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The cache is a convenience; a failure to write it does not fail browsing.
            _logger.LogWarning(exception, "The catalog cache for {Source} could not be written.", source.Id);
        }
    }

    private async Task<CatalogFetchResult> WithCacheAsync(
        CatalogSource source,
        string error,
        CancellationToken cancellationToken)
    {
        var cached = await LoadCachedAsync(source, cancellationToken).ConfigureAwait(false);
        if (cached.Succeeded)
        {
            return new CatalogFetchResult(
                cached.Catalog,
                $"{error} Showing the cached catalog from {cached.FetchedAt?.ToLocalTime():g}.",
                IsFromCache: true,
                cached.FetchedAt);
        }

        var suffix = cached.Error is null ? string.Empty : " " + cached.Error;
        return new CatalogFetchResult(null, error + suffix, IsFromCache: false, FetchedAt: null);
    }

    private Task<CatalogFetchResult> Error(CatalogSource source, string error) =>
        WithCacheAsync(source, error, CancellationToken.None);
}

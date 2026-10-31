using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using DroidDeck.Core.Infrastructure;

namespace DroidDeck.Plugins.Catalog;

/// <summary>
/// Acquires catalog packages over HTTPS and safely extracts their archives.
/// The installer owns staging, trust, swaps, and auditing; this type owns the
/// two hostile-input seams: the download (redirects, caps, hash) and the zip
/// (path escapes, device names, duplicates, extraction caps, cleanup).
/// </summary>
internal sealed class PackageAcquirer
{
    /// <summary>The largest package this host accepts by default.</summary>
    public const int DefaultMaxBytes = 8 * 1024 * 1024;

    private const long MaxExtractedBytes = 64 * 1024 * 1024;
    private const int MaxRedirectHops = 5;
    private const int BufferSize = 81_920;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly HttpClient _httpClient;
    private readonly int _maxBytes;
    private readonly string _userAgent;

    /// <summary>Creates a package acquirer.</summary>
    /// <param name="httpClient">The client used for package downloads.</param>
    /// <param name="maxBytes">The largest package accepted, in bytes.</param>
    /// <param name="userAgent">The user agent sent with download requests.</param>
    public PackageAcquirer(
        HttpClient httpClient,
        int maxBytes = DefaultMaxBytes,
        string userAgent = "DroidDeck/1.0.0")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        _maxBytes = maxBytes;
        _userAgent = userAgent;
    }

    /// <summary>
    /// Downloads a catalog package over HTTPS into <paramref name="destinationPath"/>
    /// and verifies its SHA-256 hash. Returns a clear error instead of throwing
    /// when the package is too large, redirects away from HTTPS, or does not match
    /// the catalog's size or hash.
    /// </summary>
    /// <param name="entry">The catalog entry that describes the package.</param>
    /// <param name="destinationPath">The file that receives the downloaded package.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns><see langword="null"/> on success; otherwise the reason the package was refused.</returns>
    public async Task<string?> AcquireAsync(
        CatalogEntry entry,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var downloadError = await DownloadAsync(entry, destinationPath, cancellationToken).ConfigureAwait(false);
        return downloadError ?? await VerifyHashAsync(entry, destinationPath).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the archive's entry names before anything is extracted. Checks
    /// for paths that escape the destination, Windows reserved device names, and
    /// duplicate entries; a duplicate manifest could otherwise be validated as
    /// one file and extracted as another.
    /// </summary>
    /// <param name="archive">The package archive.</param>
    /// <param name="pluginId">The plugin id used in error messages.</param>
    /// <returns><see langword="null"/> when the archive is safe; otherwise the reason it was refused.</returns>
    public static string? ValidateArchive(ZipArchive archive, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var unsafeName = FindUnsafeEntryName(archive);
        if (unsafeName is not null)
        {
            return $"The package for '{pluginId}' contains an unsafe path '{unsafeName}'.";
        }

        var duplicateName = FindDuplicateEntryName(archive);
        return duplicateName is null
            ? null
            : $"The package for '{pluginId}' contains the entry '{duplicateName}' more than once.";
    }

    /// <summary>
    /// Validates an archive and extracts it into an empty destination directory.
    /// Returns a clear error and removes the destination when any entry is unsafe,
    /// exceeds the per-entry or total extraction cap, or cannot be written.
    /// </summary>
    /// <param name="archive">The package archive.</param>
    /// <param name="pluginId">The plugin id used in error messages.</param>
    /// <param name="destinationDirectory">The directory owned by this extraction.</param>
    /// <returns><see langword="null"/> on success; otherwise the reason extraction failed.</returns>
    public static async Task<string?> ExtractAsync(
        ZipArchive archive,
        string pluginId,
        string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        var archiveError = ValidateArchive(archive, pluginId);
        if (archiveError is not null)
        {
            return archiveError;
        }

        Directory.CreateDirectory(destinationDirectory);
        var root = Path.GetFullPath(destinationDirectory);
        var extracted = 0L;
        var buffer = new byte[BufferSize];
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0 || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            (long Bytes, string? Error) outcome;
            try
            {
                outcome = await ExtractEntryAsync(entry, pluginId, destinationDirectory, root, buffer, extracted)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Hostile or broken entries (file/dir collisions, reserved
                // device names, paths the OS refuses) surface here as raw I/O
                // errors. Turn them into a graceful failure after removing the
                // partially extracted staging directory.
                DeleteDirectoryIfExists(destinationDirectory);
                return $"The package for '{pluginId}' could not be extracted: " +
                    $"the entry '{entry.FullName}' failed ({exception.Message}).";
            }

            extracted += outcome.Bytes;
            if (outcome.Error is not null)
            {
                DeleteDirectoryIfExists(destinationDirectory);
                return outcome.Error;
            }
        }

        return null;
    }

    private async Task<string?> DownloadAsync(
        CatalogEntry entry,
        string tempPath,
        CancellationToken cancellationToken)
    {
        var pluginId = entry.Plugin.Id;
        if (entry.Version.Size > _maxBytes)
        {
            return $"The package for '{pluginId}' is advertised as {entry.Version.Size} bytes; " +
                $"the limit is {_maxBytes} bytes.";
        }

        // Redirects are followed by hand, one hop at a time, so every address in
        // the chain is checked against the HTTPS requirement before anything is
        // sent to it. (The host builds this client with AllowAutoRedirect =
        // false; if a host ever left auto-follow on, this loop would never see
        // the hops and only the final-address check below would still apply.)
        // The PluginCatalogClient keeps the same final-address precedent for
        // the catalogs it fetches.
        var address = new Uri(entry.Version.DownloadUrl, UriKind.Absolute);
        for (var hop = 0; hop <= MaxRedirectHops; hop++)
        {
            if (!string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return $"The package at {entry.Version.DownloadUrl} redirected to the non-HTTPS " +
                    $"address '{address}'; only HTTPS downloads are allowed.";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd(_userAgent);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response) && response.Headers.Location is { } location)
            {
                address = location.IsAbsoluteUri ? location : new Uri(address, location);
                continue;
            }

            // An HTTP stack that follows redirects on its own reports the final
            // address here; a non-HTTPS final address is still refused.
            if (response.RequestMessage?.RequestUri is { } finalUri &&
                !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return $"The package at {entry.Version.DownloadUrl} redirected to a non-HTTPS address; " +
                    "only HTTPS downloads are allowed.";
            }

            return await DownloadBodyAsync(entry, response, tempPath, cancellationToken)
                .ConfigureAwait(false);
        }

        return $"The package at {entry.Version.DownloadUrl} redirected too many times; " +
            $"the limit is {MaxRedirectHops} hops.";
    }

    private static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is 301 or 302 or 303 or 307 or 308;

    private async Task<string?> DownloadBodyAsync(
        CatalogEntry entry,
        HttpResponseMessage response,
        string tempPath,
        CancellationToken cancellationToken)
    {
        var pluginId = entry.Plugin.Id;

        if (!response.IsSuccessStatusCode)
        {
            return $"The package for '{pluginId}' could not be downloaded from {entry.Version.DownloadUrl}: " +
                $"the server returned {(int)response.StatusCode} {response.ReasonPhrase}.";
        }

        if (response.Content.Headers.ContentLength is { } length && length > _maxBytes)
        {
            return $"The package for '{pluginId}' is too large ({length} bytes); the limit is {_maxBytes} bytes.";
        }

        await using var source = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var output = File.Create(tempPath);
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > _maxBytes)
            {
                return $"The package for '{pluginId}' is too large; the limit is {_maxBytes} bytes.";
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (entry.Version.Size > 0 && total != entry.Version.Size)
        {
            return $"The downloaded package for '{pluginId}' is {total} bytes, " +
                $"but the catalog advertises {entry.Version.Size} bytes.";
        }

        return null;
    }

    private static async Task<string?> VerifyHashAsync(CatalogEntry entry, string tempPath)
    {
        await using var stream = File.OpenRead(tempPath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
        var expected = entry.Version.Sha256.Trim();
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"The package hash does not match the catalog for '{entry.Plugin.Id}': " +
                $"expected {HashText.ShortHash(expected)}, got {HashText.ShortHash(actual)}.";
    }

    private static string? FindUnsafeEntryName(ZipArchive archive)
    {
        foreach (var entry in archive.Entries)
        {
            if (IsUnsafeEntryName(entry.FullName))
            {
                return entry.FullName;
            }
        }

        return null;
    }

    private static string? FindDuplicateEntryName(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!names.Add(NormalizeEntryKey(entry.FullName)))
            {
                return entry.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// Maps an entry name to the path Windows will actually write, so aliases
    /// such as <c>lib\Chart.dll</c>, <c>lib/./Chart.dll</c>, and
    /// <c>LIB/CHART.DLL</c> are recognised as the same file and cannot slip a
    /// second, unvalidated payload past the duplicate check.
    /// </summary>
    private static string NormalizeEntryKey(string fullName)
    {
        var segments = new List<string>();
        foreach (var segment in fullName.Replace('\\', '/').Split('/'))
        {
            if (segment.Length > 0 && segment is not ".")
            {
                segments.Add(segment.TrimEnd('.', ' '));
            }
        }

        return string.Join('/', segments);
    }

    private static bool IsUnsafeEntryName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName))
        {
            return true;
        }

        var normalized = fullName.Replace('\\', '/');
        if (normalized.Contains(':') || Path.IsPathRooted(normalized) || normalized.StartsWith('/'))
        {
            return true;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment is ".")
            {
                continue;
            }

            if (segment is "..")
            {
                return true;
            }

            // Windows silently strips trailing dots and spaces from a segment,
            // so "manifest.json." would be extracted over the validated
            // "manifest.json". Reject the alias instead of letting it collide.
            if (segment[^1] is '.' or ' ')
            {
                return true;
            }

            // Windows reserves device names even with an extension, so a file
            // named "CON.txt" or "aux.dll" would fail to extract with a raw
            // error; reject the package up front instead.
            var dot = segment.IndexOf('.');
            var stem = dot < 0 ? segment : segment[..dot];
            if (stem.Length > 0 && ReservedDeviceNames.Contains(stem))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<(long Bytes, string? Error)> ExtractEntryAsync(
        ZipArchiveEntry entry,
        string pluginId,
        string stagingDirectory,
        string root,
        byte[] buffer,
        long extractedSoFar)
    {
        var target = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName.Replace('\\', '/')));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return (0, $"The package for '{pluginId}' contains an entry that escapes the plugin directory.");
        }

        if (extractedSoFar + Math.Max(entry.Length, 0) > MaxExtractedBytes)
        {
            return (0, $"The package for '{pluginId}' unpacks to more than {MaxExtractedBytes} bytes.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var source = entry.Open();
        await using var output = File.Create(target);
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            written += read;
            if (extractedSoFar + written > MaxExtractedBytes)
            {
                return (written, $"The package for '{pluginId}' unpacks to more than {MaxExtractedBytes} bytes.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }

        return (written, null);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort; a failed cleanup leaves only staging content behind.
        }
    }
}

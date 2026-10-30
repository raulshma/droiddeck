using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DroidDeck.Core;
using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Catalog;

/// <summary>The lifecycle action an installer run performed.</summary>
public enum CatalogInstallAction
{
    /// <summary>A fresh installation of a catalog plugin.</summary>
    Install,

    /// <summary>An update that replaces an older installed version.</summary>
    Update,

    /// <summary>A restore of the version kept before the last update.</summary>
    Rollback,

    /// <summary>A removal of an installed plugin.</summary>
    Uninstall,
}

/// <summary>
/// The trust decision an installer surfaces before any plugin file is extracted
/// into the plugins root. The app shows every field to the user; the signature
/// is display-only and optional.
/// </summary>
/// <param name="PluginId">The plugin identifier from the catalog.</param>
/// <param name="PluginName">The plugin display name from the catalog.</param>
/// <param name="Version">The version being installed.</param>
/// <param name="Capabilities">The capabilities the package declares.</param>
/// <param name="Sha256">The SHA-256 hash the catalog published for the package.</param>
/// <param name="Signature">The optional package signature from the catalog, when present.</param>
/// <param name="DownloadUrl">The HTTPS URL the package was downloaded from.</param>
/// <param name="SourceId">The catalog source identifier.</param>
/// <param name="SourceName">The catalog source display name.</param>
/// <param name="IsUpdate">Whether this decision belongs to an update of an installed plugin.</param>
/// <param name="CurrentVersion">The installed version before an update, when one exists.</param>
public sealed record CatalogInstallPlan(
    string PluginId,
    string PluginName,
    string Version,
    IReadOnlyList<string> Capabilities,
    string Sha256,
    string? Signature,
    string DownloadUrl,
    string SourceId,
    string SourceName,
    bool IsUpdate,
    string? CurrentVersion);

/// <summary>The outcome of one installer run.</summary>
/// <param name="Action">The action that was attempted.</param>
/// <param name="PluginId">The plugin the action belonged to.</param>
/// <param name="FromVersion">The version before the action, when one existed.</param>
/// <param name="ToVersion">The version after the action, when one exists.</param>
/// <param name="Error">A clear explanation when the action failed.</param>
/// <param name="ContentHash">The SHA-256 hash of the installed contents, when they were installed.</param>
/// <param name="Trust">The trust decision for the installed contents, when they were installed.</param>
public sealed record CatalogInstallResult(
    CatalogInstallAction Action,
    string PluginId,
    string? FromVersion,
    string? ToVersion,
    string? Error,
    string? ContentHash,
    PluginTrustDecision? Trust)
{
    /// <summary>Gets a value indicating whether the action succeeded.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>A callback that renders a trust decision and returns whether the user approved it.</summary>
/// <param name="plan">The install plan to show the user before any file is extracted.</param>
/// <returns><see langword="true"/> when the user approved the install or update.</returns>
public delegate Task<bool> CatalogTrustDecision(CatalogInstallPlan plan);

/// <summary>One installed plugin found by scanning the plugins root.</summary>
/// <param name="PluginId">The plugin identifier from its manifest.</param>
/// <param name="Version">The plugin version from its manifest.</param>
/// <param name="Directory">The plugin installation directory.</param>
public sealed record CatalogInstalledPlugin(string PluginId, string Version, string Directory);

/// <summary>
/// Installs, updates, rolls back, and uninstalls catalog plugins. Every run
/// downloads over HTTPS into quarantine, verifies the catalog's SHA-256 hash
/// (a mismatch aborts and deletes the download), validates the package zip,
/// checks dependencies, and asks a trust decision <em>before</em> any file is
/// extracted into the plugins root. Updates are manual only — nothing here runs
/// on a timer or checks for updates by itself. A new install is never approved;
/// the existing plugin manager trust flow pins and approves contents before the
/// plugin can load. Updates keep the previous version under the backup
/// directory (<c>plugin-backups\&lt;pluginId&gt;</c>) until the next update or
/// rollback, and a failed update leaves the previous version in place.
/// </summary>
public sealed class CatalogInstaller
{
    /// <summary>The largest package this host accepts by default.</summary>
    public const int DefaultMaxBytes = 8 * 1024 * 1024;

    /// <summary>The package file extension for DroidDeck plugins.</summary>
    public const string PackageExtension = ".ddplugin";

    private const long MaxExtractedBytes = 64 * 1024 * 1024;
    private const string StagingPrefix = "staging-";

    private readonly HttpClient _httpClient;
    private readonly AppPaths _paths;
    private readonly PluginAuditLog _audit;
    private readonly int _maxBytes;
    private readonly string _userAgent;
    private readonly ILogger<CatalogInstaller> _logger;

    /// <summary>Creates a catalog installer.</summary>
    public CatalogInstaller(
        HttpClient httpClient,
        AppPaths paths,
        PluginAuditLog audit,
        int maxBytes = DefaultMaxBytes,
        string userAgent = "DroidDeck/1.0.0",
        ILogger<CatalogInstaller>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        _maxBytes = maxBytes;
        _userAgent = userAgent;
        _logger = logger ?? NullLogger<CatalogInstaller>.Instance;
    }

    /// <summary>Installs a catalog plugin. Fails when the plugin is already installed.</summary>
    public Task<CatalogInstallResult> InstallAsync(
        CatalogEntry entry,
        CatalogTrustDecision trustDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(trustDecision);
        return InstallCoreAsync(entry, CatalogInstallAction.Install, trustDecision, cancellationToken);
    }

    /// <summary>
    /// Updates an installed plugin to a newer catalog version. Fails when the
    /// plugin is missing or the catalog version is not newer. The previous
    /// version is kept for rollback.
    /// </summary>
    public Task<CatalogInstallResult> UpdateAsync(
        CatalogEntry entry,
        CatalogTrustDecision trustDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(trustDecision);
        return InstallCoreAsync(entry, CatalogInstallAction.Update, trustDecision, cancellationToken);
    }

    /// <summary>
    /// Restores the version that was kept before the last update and removes the
    /// backup. The restored contents match the hash that was pinned before the
    /// update, so the plugin is trusted again without a new decision.
    /// </summary>
    public async Task<CatalogInstallResult> RollbackAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var final = FinalDirectory(pluginId);
        var backup = Path.Combine(_paths.PluginBackupsDirectory, pluginId);
        var installed = (await GetInstalledAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(plugin => string.Equals(plugin.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        if (installed is null)
        {
            return await FailAsync(
                CatalogInstallAction.Rollback,
                pluginId,
                null,
                null,
                null,
                $"The plugin '{pluginId}' is not installed, so it cannot be rolled back.",
                cancellationToken).ConfigureAwait(false);
        }

        var backupRead = await PluginManifestReader.ReadAsync(backup, cancellationToken).ConfigureAwait(false);
        if (backupRead.Manifest is not { } backupManifest)
        {
            return await FailAsync(
                CatalogInstallAction.Rollback,
                pluginId,
                installed.Version,
                null,
                null,
                $"No backup of '{pluginId}' exists, so there is nothing to roll back to.",
                cancellationToken).ConfigureAwait(false);
        }

        var trash = Path.Combine(_paths.PluginBackupsDirectory, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(final, trash);
            try
            {
                Directory.Move(backup, final);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Directory.Move(trash, final);
                return await FailAsync(
                    CatalogInstallAction.Rollback,
                    pluginId,
                    installed.Version,
                    backupManifest.Version,
                    null,
                    $"The backup of '{pluginId}' could not be restored: {exception.Message}. " +
                        "The current version is still installed.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            DeleteDirectoryIfExists(trash);
        }

        return await SucceedAsync(
            CatalogInstallAction.Rollback,
            pluginId,
            installed.Version,
            backupManifest.Version,
            sourceId: null,
            final,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes an installed plugin directory and records the removal. Catalog
    /// sources and cached catalogs are untouched; removing a source never calls
    /// this.
    /// </summary>
    public async Task<CatalogInstallResult> UninstallAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        var installed = (await GetInstalledAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(plugin => string.Equals(plugin.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        if (installed is null)
        {
            return await FailAsync(
                CatalogInstallAction.Uninstall,
                pluginId,
                null,
                null,
                null,
                $"The plugin '{pluginId}' is not installed.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            Directory.Delete(installed.Directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return await FailAsync(
                CatalogInstallAction.Uninstall,
                pluginId,
                installed.Version,
                null,
                null,
                $"The plugin '{pluginId}' could not be removed: {exception.Message}. " +
                    "Restart DroidDeck if the plugin is loaded and try again.",
                cancellationToken).ConfigureAwait(false);
        }

        return await SucceedAsync(
            CatalogInstallAction.Uninstall,
            pluginId,
            installed.Version,
            null,
            sourceId: null,
            finalDirectory: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Scans the plugins root and reports the id and manifest version of every installed plugin.</summary>
    public async Task<IReadOnlyList<CatalogInstalledPlugin>> GetInstalledAsync(
        CancellationToken cancellationToken = default)
    {
        var root = _paths.PluginsDirectory;
        var installed = new List<CatalogInstalledPlugin>();
        if (!Directory.Exists(root))
        {
            return installed;
        }

        foreach (var directory in Directory
            .EnumerateDirectories(root)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(PluginManifestReader.GetManifestPath(directory)))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var read = await PluginManifestReader.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
            if (read.Manifest is { } manifest &&
                !string.IsNullOrWhiteSpace(manifest.Id) &&
                IsSafePluginId(manifest.Id))
            {
                installed.Add(new CatalogInstalledPlugin(manifest.Id, manifest.Version, directory));
            }
        }

        return installed;
    }

    /// <summary>Reports the version kept in the backup directory for each plugin, if any.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetRollbackVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var root = _paths.PluginBackupsDirectory;
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return versions;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory).StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var read = await PluginManifestReader.ReadAsync(directory, cancellationToken).ConfigureAwait(false);
            if (read.Manifest is { } manifest &&
                !string.IsNullOrWhiteSpace(manifest.Id) &&
                !versions.ContainsKey(manifest.Id))
            {
                versions[manifest.Id] = manifest.Version;
            }
        }

        return versions;
    }

    private async Task<CatalogInstallResult> InstallCoreAsync(
        CatalogEntry entry,
        CatalogInstallAction action,
        CatalogTrustDecision trustDecision,
        CancellationToken cancellationToken)
    {
        var pluginId = entry.Plugin.Id;
        var sourceId = entry.Source.Id;
        var installed = await GetInstalledAsync(cancellationToken).ConfigureAwait(false);
        var current = installed.FirstOrDefault(candidate =>
            string.Equals(candidate.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        var fromVersion = current?.Version;

        if (action == CatalogInstallAction.Install && current is not null)
        {
            return await FailAsync(
                action,
                pluginId,
                fromVersion,
                entry.Version.Version,
                sourceId,
                $"The plugin '{pluginId}' is already installed (version {current.Version}). " +
                    "Use Update to move to a newer version.",
                cancellationToken).ConfigureAwait(false);
        }

        if (action == CatalogInstallAction.Update)
        {
            if (current is null)
            {
                return await FailAsync(
                    action,
                    pluginId,
                    null,
                    entry.Version.Version,
                    sourceId,
                    $"The plugin '{pluginId}' is not installed, so it cannot be updated. Install it instead.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (!IsNewer(entry.Version.Version, current.Version))
            {
                return await FailAsync(
                    action,
                    pluginId,
                    current.Version,
                    entry.Version.Version,
                    sourceId,
                    $"The installed version {current.Version} of '{pluginId}' is up to date; " +
                        $"the catalog offers {entry.Version.Version}. Updates only move forward.",
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var validationError = ValidateEntry(entry);
        if (validationError is not null)
        {
            return await FailAsync(
                action, pluginId, fromVersion, entry.Version.Version, sourceId, validationError,
                cancellationToken).ConfigureAwait(false);
        }

        var dependencyError = CheckDependencies(entry, installed);
        if (dependencyError is not null)
        {
            return await FailAsync(
                action, pluginId, fromVersion, entry.Version.Version, sourceId, dependencyError,
                cancellationToken).ConfigureAwait(false);
        }

        var tempPath = Path.Combine(
            _paths.QuarantineDirectory,
            $"{pluginId}-{Guid.NewGuid():N}{PackageExtension}");
        string? stagingDirectory = null;
        try
        {
            Directory.CreateDirectory(_paths.QuarantineDirectory);
            var downloadError = await DownloadAsync(entry, tempPath, cancellationToken).ConfigureAwait(false);
            if (downloadError is not null)
            {
                return await FailAsync(
                    action, pluginId, fromVersion, entry.Version.Version, sourceId, downloadError,
                    cancellationToken, tempPath).ConfigureAwait(false);
            }

            var hashError = await VerifyHashAsync(entry, tempPath).ConfigureAwait(false);
            if (hashError is not null)
            {
                return await FailAsync(
                    action, pluginId, fromVersion, entry.Version.Version, sourceId, hashError,
                    cancellationToken, tempPath).ConfigureAwait(false);
            }

            string? error;
            using (var archiveStream = File.OpenRead(tempPath))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
            {
                var unsafeName = FindUnsafeEntryName(archive);
                error = unsafeName is null
                    ? null
                    : $"The package for '{pluginId}' contains an unsafe path '{unsafeName}'.";
                PluginManifest? manifest = null;
                if (error is null)
                {
                    (manifest, error) = await ReadManifestAsync(archive, entry).ConfigureAwait(false);
                }

                if (error is null)
                {
                    error = CheckDependencies(entry, installed);
                }

                if (error is null)
                {
                    error = await DecideTrustAsync(
                        entry,
                        action,
                        fromVersion,
                        trustDecision,
                        cancellationToken).ConfigureAwait(false);
                }

                if (error is null)
                {
                    stagingDirectory = Path.Combine(
                        _paths.PluginBackupsDirectory,
                        StagingPrefix + Guid.NewGuid().ToString("N"));
                    error = await ExtractAsync(archive, pluginId, stagingDirectory).ConfigureAwait(false);
                    if (error is not null)
                    {
                        DeleteDirectoryIfExists(stagingDirectory);
                        stagingDirectory = null;
                    }
                }
            }

            if (error is not null)
            {
                return await FailAsync(
                    action, pluginId, fromVersion, entry.Version.Version, sourceId, error,
                    cancellationToken, tempPath).ConfigureAwait(false);
            }

            var swapError = SwapIntoPlace(action, pluginId, stagingDirectory!);
            stagingDirectory = null;
            if (swapError is not null)
            {
                return await FailAsync(
                    action, pluginId, fromVersion, entry.Version.Version, sourceId, swapError,
                    cancellationToken, tempPath).ConfigureAwait(false);
            }

            return await SucceedAsync(
                action,
                pluginId,
                fromVersion,
                entry.Version.Version,
                sourceId,
                FinalDirectory(pluginId),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteFileIfExists(tempPath);
            if (stagingDirectory is not null)
            {
                DeleteDirectoryIfExists(stagingDirectory);
            }
        }
    }

    private string FinalDirectory(string pluginId) =>
        Path.Combine(_paths.PluginsDirectory, pluginId);

    private static string? ValidateEntry(CatalogEntry entry)
    {
        var pluginId = entry.Plugin.Id;
        if (!IsSafePluginId(pluginId))
        {
            return $"The plugin id '{pluginId}' is not usable as an installation directory.";
        }

        var sha256 = entry.Version.Sha256?.Trim() ?? string.Empty;
        if (sha256.Length == 0)
        {
            return $"The catalog entry for '{pluginId}' has no SHA-256 hash. " +
                "Packages without a published hash are never installed.";
        }

        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
        {
            return $"The SHA-256 hash of '{pluginId}' must be 64 hexadecimal characters.";
        }

        if (!Uri.TryCreate(entry.Version.DownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return $"The download URL for '{pluginId}' must use HTTPS. " +
                "Plain HTTP or local package downloads are not allowed.";
        }

        return null;
    }

    private static bool IsSafePluginId(string pluginId) =>
        !string.IsNullOrWhiteSpace(pluginId) &&
        pluginId is not ("." or "..") &&
        string.Equals(Path.GetFileName(pluginId), pluginId, StringComparison.Ordinal) &&
        pluginId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsNewer(string candidate, string current) =>
        SemanticVersion.TryParse(candidate, out var parsedCandidate) &&
        SemanticVersion.TryParse(current, out var parsedCurrent) &&
        parsedCandidate > parsedCurrent;

    private static string? CheckDependencies(
        CatalogEntry entry,
        IReadOnlyList<CatalogInstalledPlugin> installed)
    {
        var unmet = new List<string>();
        foreach (var dependency in entry.Version.Dependencies ?? [])
        {
            var found = installed.FirstOrDefault(candidate =>
                string.Equals(candidate.PluginId, dependency.Id, StringComparison.OrdinalIgnoreCase));
            if (found is null)
            {
                unmet.Add($"{dependency.Id} {dependency.MinVersion}+ (not installed)");
                continue;
            }

            if (SemanticVersion.TryParse(dependency.MinVersion, out var minimum) &&
                SemanticVersion.TryParse(found.Version, out var actual) &&
                actual < minimum)
            {
                unmet.Add($"{dependency.Id} {dependency.MinVersion}+ (installed {found.Version})");
            }
        }

        return unmet.Count == 0
            ? null
            : $"The plugin '{entry.Plugin.Id}' has dependencies that are not satisfied: " +
                $"{string.Join("; ", unmet)}. Install the missing versions from a catalog source first; " +
                "DroidDeck does not install dependencies automatically.";
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

        using var request = new HttpRequestMessage(HttpMethod.Get, entry.Version.DownloadUrl);
        request.Headers.UserAgent.ParseAdd(_userAgent);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // Redirects are followed by the HTTP stack, so the requested address being
        // HTTPS does not guarantee the package arrived over HTTPS.
        if (response.RequestMessage?.RequestUri is { } finalUri &&
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return $"The package at {entry.Version.DownloadUrl} redirected to a non-HTTPS address; " +
                "only HTTPS downloads are allowed.";
        }

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
        var buffer = new byte[81_920];
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
                $"expected {ShortHash(expected)}, got {ShortHash(actual)}. The download was deleted.";
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
        }

        return false;
    }

    private static async Task<(PluginManifest? Manifest, string? Error)> ReadManifestAsync(
        ZipArchive archive,
        CatalogEntry entry)
    {
        var manifestEntry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.FullName, PluginManifestReader.FileName, StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null)
        {
            return (null,
                $"The package for '{entry.Plugin.Id}' does not contain a " +
                $"{PluginManifestReader.FileName} manifest at its root.");
        }

        PluginManifest? manifest;
        await using var stream = manifestEntry.Open();
        try
        {
            manifest = await JsonSerializer
                .DeserializeAsync(stream, PluginJsonContext.Default.PluginManifest)
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            return (null,
                $"The manifest in the package for '{entry.Plugin.Id}' is not valid JSON: {exception.Message}");
        }

        if (manifest is null)
        {
            return (null, $"The manifest in the package for '{entry.Plugin.Id}' did not contain a plugin.");
        }

        if (!string.Equals(manifest.Id, entry.Plugin.Id, StringComparison.OrdinalIgnoreCase))
        {
            return (null,
                $"The package manifest id '{manifest.Id}' does not match the catalog entry '{entry.Plugin.Id}'.");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out _))
        {
            return (null,
                $"The package manifest version '{manifest.Version}' of '{entry.Plugin.Id}' is not a semantic version.");
        }

        if (!string.Equals(manifest.Version, entry.Version.Version, StringComparison.OrdinalIgnoreCase))
        {
            return (null,
                $"The package manifest version {manifest.Version} of '{entry.Plugin.Id}' does not match " +
                $"the catalog version {entry.Version.Version}.");
        }

        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            return (null, $"The package manifest of '{entry.Plugin.Id}' does not name an entry assembly.");
        }

        return (manifest, null);
    }

    private static async Task<string?> DecideTrustAsync(
        CatalogEntry entry,
        CatalogInstallAction action,
        string? currentVersion,
        CatalogTrustDecision trustDecision,
        CancellationToken cancellationToken)
    {
        var plan = new CatalogInstallPlan(
            entry.Plugin.Id,
            entry.Plugin.Name,
            entry.Version.Version,
            [.. entry.Version.Capabilities ?? []],
            entry.Version.Sha256.Trim(),
            entry.Version.Signature,
            entry.Version.DownloadUrl,
            entry.Source.Id,
            entry.Source.Name,
            action == CatalogInstallAction.Update,
            currentVersion);
        bool approved;
        try
        {
            approved = await trustDecision(plan).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return $"The trust decision for '{entry.Plugin.Id}' could not be made: {exception.Message}";
        }

        return approved
            ? null
            : $"The trust decision for '{entry.Plugin.Id}' was declined, so the package was not extracted " +
                "or installed.";
    }

    private static async Task<string?> ExtractAsync(
        ZipArchive archive,
        string pluginId,
        string stagingDirectory)
    {
        Directory.CreateDirectory(stagingDirectory);
        var root = Path.GetFullPath(stagingDirectory);
        var extracted = 0L;
        var buffer = new byte[81_920];
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.Length == 0 || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName.Replace('\\', '/')));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return $"The package for '{pluginId}' contains an entry that escapes the plugin directory.";
            }

            if (extracted + Math.Max(entry.Length, 0) > MaxExtractedBytes)
            {
                return $"The package for '{pluginId}' unpacks to more than {MaxExtractedBytes} bytes.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = File.Create(target);
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                extracted += read;
                if (extracted > MaxExtractedBytes)
                {
                    return $"The package for '{pluginId}' unpacks to more than {MaxExtractedBytes} bytes.";
                }

                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            }
        }

        return null;
    }

    private string? SwapIntoPlace(CatalogInstallAction action, string pluginId, string stagingDirectory)
    {
        var final = FinalDirectory(pluginId);
        Directory.CreateDirectory(_paths.PluginsDirectory);
        if (action == CatalogInstallAction.Install)
        {
            if (Directory.Exists(final))
            {
                return $"The plugin '{pluginId}' appeared on disk while installing; nothing was changed.";
            }

            Directory.Move(stagingDirectory, final);
            return null;
        }

        var backup = Path.Combine(_paths.PluginBackupsDirectory, pluginId);
        DeleteDirectoryIfExists(backup);
        Directory.Move(final, backup);
        try
        {
            Directory.Move(stagingDirectory, final);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Directory.Move(backup, final);
            return $"The new version of '{pluginId}' could not be put in place: {exception.Message}. " +
                "The previous version is still installed.";
        }
    }

    private async Task<CatalogInstallResult> SucceedAsync(
        CatalogInstallAction action,
        string pluginId,
        string? fromVersion,
        string? toVersion,
        string? sourceId,
        string? finalDirectory,
        CancellationToken cancellationToken)
    {
        string? contentHash = null;
        PluginTrustDecision? trust = null;
        if (finalDirectory is not null)
        {
            contentHash = await PluginContentHasher
                .ComputeAsync(finalDirectory, cancellationToken)
                .ConfigureAwait(false);
            var store = await PluginTrustStore
                .LoadAsync(_paths.PluginsDirectory, cancellationToken)
                .ConfigureAwait(false);
            trust = store.Evaluate(pluginId, contentHash);
        }

        await AuditAsync(action, pluginId, fromVersion, toVersion, "success", null, sourceId, cancellationToken)
            .ConfigureAwait(false);
        return new CatalogInstallResult(action, pluginId, fromVersion, toVersion, null, contentHash, trust);
    }

    private async Task<CatalogInstallResult> FailAsync(
        CatalogInstallAction action,
        string pluginId,
        string? fromVersion,
        string? toVersion,
        string? sourceId,
        string error,
        CancellationToken cancellationToken,
        string? tempPath = null)
    {
        DeleteFileIfExists(tempPath);
        await AuditAsync(action, pluginId, fromVersion, toVersion, "failure", error, sourceId, cancellationToken)
            .ConfigureAwait(false);
        return new CatalogInstallResult(action, pluginId, fromVersion, toVersion, error, null, null);
    }

    private async Task AuditAsync(
        CatalogInstallAction action,
        string pluginId,
        string? fromVersion,
        string? toVersion,
        string result,
        string? reason,
        string? sourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _audit.AppendAsync(
                new PluginAuditEntry(
                    DateTimeOffset.UtcNow,
                    action.ToString().ToLowerInvariant(),
                    pluginId,
                    fromVersion,
                    toVersion,
                    result,
                    reason,
                    sourceId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "The audit entry for {Action} of {PluginId} could not be written.",
                action,
                pluginId);
        }
    }

    private static string ShortHash(string hash) =>
        hash.Length <= 12 ? hash : hash[..12] + "…";

    private static void DeleteFileIfExists(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort; quarantine leftovers are cleaned by the OS or the next run.
        }
    }

    private static void DeleteDirectoryIfExists(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort; a staging leftover never reaches the plugins root.
        }
    }
}

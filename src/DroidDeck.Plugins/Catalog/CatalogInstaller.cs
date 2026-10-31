using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DroidDeck.Core;
using DroidDeck.Core.Infrastructure;
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
/// downloads over HTTPS into quarantine (redirects are followed by hand so
/// every hop must stay HTTPS), verifies the catalog's SHA-256 hash
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
    private const int StagingSuffixLength = 32;
    private const int MaxRedirectHops = 5;

    private readonly HttpClient _httpClient;
    private readonly AppPaths _paths;
    private readonly PluginAuditLog _audit;
    private readonly int _maxBytes;
    private readonly string _userAgent;
    private readonly string _hostVersion;
    private readonly ILogger<CatalogInstaller> _logger;

    /// <summary>Creates a catalog installer.</summary>
    public CatalogInstaller(
        HttpClient httpClient,
        AppPaths paths,
        PluginAuditLog audit,
        int maxBytes = DefaultMaxBytes,
        string userAgent = "DroidDeck/1.0.0",
        ILogger<CatalogInstaller>? logger = null,
        string? hostVersion = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        _maxBytes = maxBytes;
        _userAgent = userAgent;
        _hostVersion = hostVersion ?? AppInfo.Version;
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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return await FailAsync(
                CatalogInstallAction.Rollback,
                pluginId,
                installed.Version,
                backupManifest.Version,
                null,
                $"The installed version of '{pluginId}' could not be moved aside: {exception.Message}. " +
                    "The current version is still installed.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            Directory.Move(backup, final);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Put the moved-aside version back; if even that fails, keep the
            // moved copy on disk — it is the only remaining copy of the
            // plugin, so it is never deleted on a failure path.
            try
            {
                Directory.Move(trash, final);
            }
            catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
            {
                return await FailAsync(
                    CatalogInstallAction.Rollback,
                    pluginId,
                    installed.Version,
                    backupManifest.Version,
                    null,
                    $"The backup of '{pluginId}' could not be restored: {exception.Message}, and the " +
                        $"installed version could not be moved back either ({restoreException.Message}). " +
                        $"Its files are kept at '{trash}'.",
                    cancellationToken).ConfigureAwait(false);
            }

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

        // Both moves completed: the backup copy is now installed and the
        // moved-aside version is safe to discard.
        DeleteDirectoryIfExists(trash);

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

        // Rename the plugin out of the plugins root first: the rename is
        // atomic on the same volume, so a locked file can never leave a
        // half-deleted plugin behind — either the whole directory moves, or
        // nothing at all is touched. Only the moved copy is then deleted; a
        // locked file there merely leaves a staging-shaped leftover that the
        // rollback scan ignores.
        var trash = Path.Combine(_paths.PluginBackupsDirectory, StagingPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(_paths.PluginBackupsDirectory);
            Directory.Move(installed.Directory, trash);
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
                    "Restart DroidDeck if the plugin is loaded and try again. Nothing was deleted.",
                cancellationToken).ConfigureAwait(false);
        }

        DeleteDirectoryIfExists(trash);

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
            if (IsStagingDirectoryName(Path.GetFileName(directory)))
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

    /// <summary>
    /// Matches only the exact staging shape the installer generates
    /// (<c>staging-</c> plus 32 hexadecimal characters), so a plugin whose id
    /// legitimately starts with <c>staging-</c> keeps its rollback backup
    /// visible while move-aside leftovers stay hidden.
    /// </summary>
    private static bool IsStagingDirectoryName(string name) =>
        name.Length == StagingPrefix.Length + StagingSuffixLength &&
        name.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase) &&
        name[StagingPrefix.Length..].All(char.IsAsciiHexDigit);

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

        var compatibilityError = DescribeIncompatibility(
            PluginCompatibilityPolicy.EvaluateVersion(
                entry.Version.ApiVersion,
                entry.Version.MinHostVersion,
                entry.Version.MaxHostVersion,
                _hostVersion),
            pluginId,
            entry.Version.ApiVersion);
        if (compatibilityError is not null)
        {
            return await FailAsync(
                action, pluginId, fromVersion, entry.Version.Version, sourceId, compatibilityError,
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

                if (error is null && manifest is not null)
                {
                    error = DescribeIncompatibility(
                        PluginCompatibilityPolicy.Evaluate(manifest, _hostVersion),
                        manifest.Id,
                        manifest.ApiVersion);
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
            if (swapError is not null)
            {
                // A failed swap never leaves staging as the only copy of an
                // installed version, so the finally block below deletes it
                // instead of leaking a staging directory.
                return await FailAsync(
                    action, pluginId, fromVersion, entry.Version.Version, sourceId, swapError,
                    cancellationToken, tempPath).ConfigureAwait(false);
            }

            stagingDirectory = null;
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

    /// <summary>
    /// Explains why a catalog package cannot load on this host. The installer
    /// enforces the same API and host-version rules as the loader so a package
    /// it accepts is one the loader will accept.
    /// </summary>
    private static string? DescribeIncompatibility(
        PluginCompatibilityVerdict verdict,
        string pluginId,
        string apiVersion)
    {
        if (verdict.Api == ApiCompatibility.Invalid)
        {
            return $"The plugin '{pluginId}' declares an invalid plugin API version '{apiVersion}'.";
        }

        if (verdict.Api == ApiCompatibility.MajorMismatch)
        {
            return $"The plugin '{pluginId}' targets plugin API {apiVersion}; " +
                $"this host implements {PluginApi.Version}.";
        }

        foreach (var issue in verdict.HostRangeIssues)
        {
            switch (issue.Kind)
            {
                case PluginHostRangeIssueKind.MinimumHostVersionInvalid:
                case PluginHostRangeIssueKind.MaximumHostVersionInvalid:
                    return $"The plugin '{pluginId}' declares a host version range this host cannot read.";
                case PluginHostRangeIssueKind.HostTooOld:
                    return $"The plugin '{pluginId}' requires DroidDeck {issue.BoundaryVersion} or newer; " +
                        $"this is {verdict.HostVersion}.";
                case PluginHostRangeIssueKind.HostTooNew:
                    return $"The plugin '{pluginId}' supports DroidDeck up to {issue.BoundaryVersion}; " +
                        $"this is {verdict.HostVersion}.";
            }
        }

        return null;
    }

    private static string? CheckDependencies(
        CatalogEntry entry,
        IReadOnlyList<CatalogInstalledPlugin> installed)
    {
        var available = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in installed)
        {
            available[plugin.PluginId] = plugin.Version;
        }

        var dependencies = (entry.Version.Dependencies ?? [])
            .Select(static dependency => new PluginDependency
            {
                Id = dependency.Id,
                MinVersion = dependency.MinVersion,
            })
            .ToArray();
        var unmet = PluginCompatibilityPolicy.EvaluateDependencies(dependencies, available);
        if (unmet.Count == 0)
        {
            return null;
        }

        var descriptions = unmet.Select(static issue => issue.Kind == PluginDependencyIssueKind.Missing
            ? $"{issue.DependencyId} {issue.RequiredVersion}+ (not installed)"
            : $"{issue.DependencyId} {issue.RequiredVersion}+ (installed {issue.InstalledVersion})");
        return $"The plugin '{entry.Plugin.Id}' has dependencies that are not satisfied: " +
            $"{string.Join("; ", descriptions)}. Install the missing versions from a catalog source first; " +
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

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

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

            (long Bytes, string? Error) outcome;
            try
            {
                outcome = await ExtractEntryAsync(entry, pluginId, stagingDirectory, root, buffer, extracted)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Hostile or broken entries (file/dir collisions, reserved
                // device names, paths the OS refuses) surface here as raw I/O
                // errors. Turn them into a graceful failure so the caller can
                // report the error, audit it, and clean the staging directory.
                return $"The package for '{pluginId}' could not be extracted: " +
                    $"the entry '{entry.FullName}' failed ({exception.Message}).";
            }

            extracted += outcome.Bytes;
            if (outcome.Error is not null)
            {
                return outcome.Error;
            }
        }

        return null;
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

            try
            {
                Directory.Move(stagingDirectory, final);
                return null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return $"The new version of '{pluginId}' could not be moved into place: {exception.Message}. " +
                    "Nothing was installed.";
            }
        }

        var backup = Path.Combine(_paths.PluginBackupsDirectory, pluginId);
        DeleteDirectoryIfExists(backup);
        try
        {
            Directory.Move(final, backup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The installed version never moved, so nothing is lost; the audit
            // and the returned error record why the update stopped here.
            return $"The previous version of '{pluginId}' could not be moved to the backup location: " +
                $"{exception.Message}. The installed version was not changed.";
        }

        try
        {
            Directory.Move(stagingDirectory, final);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Put the previous version back so the plugin is never left
            // missing. The backup keeps a second copy until the next update
            // either way, so no failure path below deletes the only version.
            try
            {
                Directory.Move(backup, final);
            }
            catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
            {
                return $"The new version of '{pluginId}' could not be put in place: {exception.Message}, and " +
                    $"the previous version could not be moved back from '{backup}' ({restoreException.Message}). " +
                    "Its files are kept in the backup location.";
            }

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
        var removed = DeleteFileIfExists(tempPath);
        var reason = removed || tempPath is null
            ? error
            : $"{error} The download could not be removed from quarantine and was left at '{tempPath}'.";
        await AuditAsync(action, pluginId, fromVersion, toVersion, "failure", reason, sourceId, cancellationToken)
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
        var entry = new PluginAuditEntry(
            DateTimeOffset.UtcNow,
            action.ToString().ToLowerInvariant(),
            pluginId,
            fromVersion,
            toVersion,
            result,
            reason,
            sourceId);

        try
        {
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "The audit entry for {Action} of {PluginId} could not be written; retrying once.",
                action,
                pluginId);
        }

        try
        {
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Non-fatal by design: an install, update, or uninstall must never
            // depend on audit writability. Mirror the full payload into the
            // normal file log so the decision is still recorded somewhere
            // recoverable.
            _logger.LogError(
                exception,
                "The audit entry for {Action} of {PluginId} could not be written after a retry. " +
                    "Audit payload: action={EntryAction} pluginId={EntryPluginId} fromVersion={FromVersion} " +
                    "toVersion={ToVersion} result={EntryResult} reason={EntryReason} sourceId={EntrySourceId} " +
                    "timestampUtc={TimestampUtc:o}",
                action,
                pluginId,
                entry.Action,
                entry.PluginId,
                entry.FromVersion,
                entry.ToVersion,
                entry.Result,
                entry.Reason,
                entry.SourceId,
                entry.TimestampUtc);
        }
    }

    private static bool DeleteFileIfExists(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return true;
        }

        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort; the caller reports the leftover so it is not silently lost.
            return false;
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

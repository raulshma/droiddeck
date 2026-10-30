using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidDeck.Plugins;

/// <summary>Whether a plugin's current contents may run on this machine.</summary>
public enum PluginTrustDecision
{
    /// <summary>The plugin was never approved on this machine.</summary>
    Unknown,

    /// <summary>The plugin contents match the approved hash.</summary>
    Trusted,

    /// <summary>The plugin contents changed after the last approval.</summary>
    Changed,
}

/// <summary>How the host treats plugins that were not approved yet.</summary>
public enum PluginTrustPolicy
{
    /// <summary>Plugins must be approved before their assemblies are loaded.</summary>
    RequireApproval,

    /// <summary>Plugins load without approval; only for tests and development builds.</summary>
    TrustAll,
}

/// <summary>An approved plugin and the hash that was approved.</summary>
public sealed record PluginTrustEntry
{
    /// <summary>Gets the plugin identifier.</summary>
    public required string PluginId { get; init; }

    /// <summary>Gets the SHA-256 hash of the approved plugin contents.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Gets when the contents were approved.</summary>
    public DateTimeOffset ApprovedAt { get; init; }

    /// <summary>Gets the plugin name at approval time.</summary>
    public string? Name { get; init; }

    /// <summary>Gets the plugin version at approval time.</summary>
    public string? Version { get; init; }
}

/// <summary>
/// Persists which plugins the user approved in <c>trust.json</c> next to the
/// plugin directories. Approval pins the SHA-256 hash of the plugin contents,
/// so a plugin that changes after approval needs a new trust decision.
/// </summary>
public sealed class PluginTrustStore
{
    /// <summary>The trust file name inside the plugin root.</summary>
    public const string FileName = "trust.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, PluginTrustEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private PluginTrustStore(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>Gets the file the trust state persists to.</summary>
    public string FilePath { get; }

    /// <summary>Gets the approved plugins ordered by identifier.</summary>
    public IReadOnlyList<PluginTrustEntry> Entries
    {
        get
        {
            lock (_sync)
            {
                return _entries.Values
                    .OrderBy(static entry => entry.PluginId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// Loads the trust state for a plugin root. A missing, corrupt, or unreadable
    /// file yields an empty store, so every plugin needs approval again.
    /// </summary>
    public static async Task<PluginTrustStore> LoadAsync(
        string pluginsRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsRoot);
        var store = new PluginTrustStore(Path.Combine(pluginsRoot, FileName));
        if (!File.Exists(store.FilePath))
        {
            return store;
        }

        try
        {
            await using var stream = File.OpenRead(store.FilePath);
            var saved = await JsonSerializer
                .DeserializeAsync<SavedTrust>(stream, Options, cancellationToken)
                .ConfigureAwait(false);
            if (saved?.Plugins is not null)
            {
                foreach (var entry in saved.Plugins)
                {
                    if (!string.IsNullOrWhiteSpace(entry.PluginId) &&
                        !string.IsNullOrWhiteSpace(entry.ContentHash))
                    {
                        store._entries[entry.PluginId] = entry;
                    }
                }
            }

            return store;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // A missing or corrupt trust record must fail closed: every plugin
            // is treated as unapproved and has to be reviewed again.
            return new PluginTrustStore(store.FilePath);
        }
    }

    /// <summary>Finds the approval for a plugin, ignoring case.</summary>
    public PluginTrustEntry? Find(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_sync)
        {
            return _entries.TryGetValue(pluginId, out var entry) ? entry : null;
        }
    }

    /// <summary>Compares a plugin's current contents with its approval.</summary>
    public PluginTrustDecision Evaluate(string pluginId, string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        var entry = Find(pluginId);
        if (entry is null)
        {
            return PluginTrustDecision.Unknown;
        }

        return string.Equals(entry.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase)
            ? PluginTrustDecision.Trusted
            : PluginTrustDecision.Changed;
    }

    /// <summary>
    /// Approves a plugin's current contents and persists the pinned hash. Approving
    /// an already approved plugin replaces its hash with the new contents.
    /// </summary>
    public async Task ApproveAsync(
        string pluginId,
        string contentHash,
        string? name = null,
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);

        PluginTrustEntry[] snapshot;
        lock (_sync)
        {
            _entries[pluginId] = new PluginTrustEntry
            {
                PluginId = pluginId,
                ContentHash = contentHash,
                ApprovedAt = DateTimeOffset.UtcNow,
                Name = name,
                Version = version,
            };
            snapshot = [.. _entries.Values];
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(
            FilePath,
            JsonSerializer.Serialize(new SavedTrust(snapshot), Options),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record SavedTrust(IReadOnlyList<PluginTrustEntry> Plugins);
}

/// <summary>
/// Computes a SHA-256 hash over a plugin's files so trust can be pinned to exact
/// contents. Runtime data and the plugin's private settings are excluded because
/// they change while the plugin runs and are not part of the installed code.
/// </summary>
public static class PluginContentHasher
{
    /// <summary>Hashes every relevant file in a plugin directory.</summary>
    public static async Task<string> ComputeAsync(
        string pluginDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        var root = Path.GetFullPath(pluginDirectory);
        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (
                FullPath: path,
                RelativePath: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(static file => !IsIgnored(file.RelativePath))
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = new FileInfo(file.FullPath).Length;
            hash.AppendData(Encoding.UTF8.GetBytes(
                file.RelativePath + "\n" + length.ToString(CultureInfo.InvariantCulture) + "\n"));
            await using var stream = File.OpenRead(file.FullPath);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool IsIgnored(string relativePath) =>
        relativePath.Equals("settings.json", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith("data/", StringComparison.OrdinalIgnoreCase);
}

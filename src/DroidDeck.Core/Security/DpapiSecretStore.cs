using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DroidDeck.Core.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Security;

/// <summary>
/// A per-user DPAPI-backed secret store. Values are never written in plaintext.
/// </summary>
public sealed class DpapiSecretStore : ISecretStore, IDisposable
{
    private static readonly byte[] Entropy = "DroidDeck.Secrets.v1"u8.ToArray();
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private readonly AppPaths _paths;
    private readonly ILogger<DpapiSecretStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _plaintextCache = new(StringComparer.Ordinal);
    private SecretsDocument _document = new();
    private bool _loaded;
    private bool _disposed;

    /// <summary>Creates a secret store rooted at the supplied data paths.</summary>
    public DpapiSecretStore(
        AppPaths paths,
        ILogger<DpapiSecretStore>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<DpapiSecretStore>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets a warning from the last load, such as a quarantined corrupt file.</summary>
    public string? LastLoadWarning { get; private set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            return _document.Secrets.Keys.ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(value);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);

            var candidate = _document.Clone();
            candidate.Secrets[name] = Convert.ToBase64String(Protect(value));
            await SaveLockedAsync(candidate, cancellationToken).ConfigureAwait(false);
            _document = candidate;
            _plaintextCache[name] = value;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (_plaintextCache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            if (!_document.Secrets.TryGetValue(name, out var encrypted))
            {
                return null;
            }

            var value = TryUnprotect(name, encrypted);
            if (value is not null)
            {
                _plaintextCache[name] = value;
            }

            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!_document.Secrets.ContainsKey(name))
            {
                return false;
            }

            var candidate = _document.Clone();
            candidate.Secrets.Remove(name);
            await SaveLockedAsync(candidate, cancellationToken).ConfigureAwait(false);
            _document = candidate;
            _plaintextCache.Remove(name);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetKnownValuesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);

            var values = new List<string>(_document.Secrets.Count);
            foreach (var name in _document.Secrets.Keys)
            {
                if (!_plaintextCache.TryGetValue(name, out var value))
                {
                    value = TryUnprotect(name, _document.Secrets[name]);
                    if (value is null)
                    {
                        continue;
                    }

                    _plaintextCache[name] = value;
                }

                if (value.Length >= 4)
                {
                    values.Add(value);
                }
            }

            return values;
        }
        finally
        {
            _gate.Release();
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
        _plaintextCache.Clear();
        _gate.Dispose();
    }

    private async Task EnsureLoadedLockedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        var path = _paths.SecretsFile;
        if (!File.Exists(path))
        {
            _loaded = true;
            return;
        }

        try
        {
            await ProcessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                _document = JsonSerializer.Deserialize<SecretsDocument>(json, DroidDeckJson.Options) ?? new SecretsDocument();
            }
            finally
            {
                ProcessGate.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "The secrets file is invalid and will be quarantined.");
            try
            {
                var quarantined = CorruptFileQuarantine.Quarantine(
                    path,
                    _paths.QuarantineDirectory,
                    _timeProvider.GetUtcNow());
                LastLoadWarning = $"The secrets file was unreadable and was moved to {quarantined}.";
            }
            catch (IOException)
            {
                LastLoadWarning = "The secrets file was unreadable. No secrets could be loaded.";
            }

            _document = new SecretsDocument();
        }

        _loaded = true;
    }

    private async Task SaveLockedAsync(SecretsDocument document, CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var json = JsonSerializer.Serialize(document, DroidDeckJson.Options);
        await AtomicFile.WriteAllTextAsync(_paths.SecretsFile, json, keepBackup: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] Protect(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
    }

    private string? TryUnprotect(string name, string encryptedBase64)
    {
        try
        {
            return Unprotect(encryptedBase64);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            _logger.LogWarning(
                exception,
                "Secret '{Name}' could not be decrypted and was ignored.",
                name);
            LastLoadWarning ??= $"Secret '{name}' could not be decrypted and was ignored.";
            return null;
        }
    }

    private static string Unprotect(string encryptedBase64)
    {
        var encrypted = Convert.FromBase64String(encryptedBase64);
        var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 ||
            !name.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                "Secret names must be 1-128 characters of letters, digits, dots, underscores, or hyphens.",
                nameof(name));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class SecretsDocument
    {
        public int SchemaVersion { get; set; } = 1;

        public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);

        public SecretsDocument Clone()
        {
            return new SecretsDocument
            {
                SchemaVersion = SchemaVersion,
                Secrets = new Dictionary<string, string>(Secrets, StringComparer.Ordinal),
            };
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using DroidDeck.Core.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Security;

/// <summary>
/// Stores repository script trust decisions in <c>trust.json</c> under the application
/// data directory, keyed by the SHA-256 of each script's content.
/// </summary>
public sealed class RepoScriptTrustService : IRepoScriptTrustService, IDisposable
{
    private readonly AppPaths _paths;
    private readonly ILogger<RepoScriptTrustService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TrustDocument _document = new();
    private bool _loaded;
    private bool _disposed;

    /// <summary>Creates a trust service rooted at the supplied data paths.</summary>
    public RepoScriptTrustService(
        AppPaths paths,
        ILogger<RepoScriptTrustService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<RepoScriptTrustService>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event EventHandler<ScriptTrustRequestedEventArgs>? TrustRequested;

    /// <summary>Gets the path of the machine-local trust document.</summary>
    public string TrustFile => Path.Combine(_paths.DataDirectory, "trust.json");

    /// <inheritdoc />
    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = File.OpenRead(path);
        return ToHex(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<ScriptTrustDecision> AuthorizeAsync(
        string repositoryRoot,
        string scriptPath,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(repositoryRoot, scriptPath, cancellationToken).ConfigureAwait(false);
        var state = await ReadStateAsync(context, cancellationToken).ConfigureAwait(false);
        if (state is ScriptTrustState.Trusted or ScriptTrustState.Denied)
        {
            return new ScriptTrustDecision(context.ScriptPath, context.Sha256, state);
        }

        // Raise the prompt, then re-read the store in case a handler decided inline.
        // Handlers that decide later authorize through ApproveAsync or DenyAsync.
        if (TrustRequested is not null)
        {
            TrustRequested.Invoke(
                this,
                new ScriptTrustRequestedEventArgs(
                    context.RepositoryRoot,
                    context.ScriptPath,
                    context.RelativePath,
                    context.Sha256));
            state = await ReadStateAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return new ScriptTrustDecision(context.ScriptPath, context.Sha256, state);
    }

    /// <inheritdoc />
    public async Task<ScriptTrustState> GetStateAsync(
        string repositoryRoot,
        string scriptPath,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(repositoryRoot, scriptPath, cancellationToken).ConfigureAwait(false);
        return await ReadStateAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task ApproveAsync(string repositoryRoot, string scriptPath, CancellationToken cancellationToken = default) =>
        SetDecisionAsync(repositoryRoot, scriptPath, ScriptTrustState.Trusted, cancellationToken);

    /// <inheritdoc />
    public Task DenyAsync(string repositoryRoot, string scriptPath, CancellationToken cancellationToken = default) =>
        SetDecisionAsync(repositoryRoot, scriptPath, ScriptTrustState.Denied, cancellationToken);

    /// <inheritdoc />
    public async Task RevokeAsync(string repositoryRoot, string scriptPath, CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(repositoryRoot, scriptPath, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            var candidate = _document.Clone();
            var entry = FindEntryIn(candidate, context);
            if (entry is null)
            {
                return;
            }

            candidate.Entries.Remove(entry);
            await SaveLockedAsync(candidate, cancellationToken).ConfigureAwait(false);
            _document = candidate;
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
        _gate.Dispose();
    }

    private async Task SetDecisionAsync(
        string repositoryRoot,
        string scriptPath,
        ScriptTrustState state,
        CancellationToken cancellationToken)
    {
        var context = await ResolveAsync(repositoryRoot, scriptPath, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            var candidate = _document.Clone();
            var entry = FindEntryIn(candidate, context);
            if (entry is null)
            {
                entry = new ScriptTrustEntry
                {
                    RepositoryRoot = context.RepositoryRoot,
                    ScriptPath = context.RelativePath,
                };
                candidate.Entries.Add(entry);
            }

            entry.Sha256 = context.Sha256;
            entry.State = state;
            entry.DecidedAtUtc = _timeProvider.GetUtcNow();
            await SaveLockedAsync(candidate, cancellationToken).ConfigureAwait(false);
            _document = candidate;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ScriptTrustState> ReadStateAsync(ScriptContext context, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            var entry = FindEntry(context);
            if (entry is null || !string.Equals(entry.Sha256, context.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return ScriptTrustState.PendingApproval;
            }

            return entry.State;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedLockedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        if (File.Exists(TrustFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(TrustFile, cancellationToken).ConfigureAwait(false);
                _document = JsonSerializer.Deserialize<TrustDocument>(json, DroidDeckJson.Options) ?? new TrustDocument();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(exception, "The script trust file is invalid and will be quarantined.");
                try
                {
                    CorruptFileQuarantine.Quarantine(TrustFile, _paths.QuarantineDirectory, _timeProvider.GetUtcNow());
                }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(cleanupException, "The invalid trust file could not be quarantined.");
                }

                _document = new TrustDocument();
            }
        }

        _loaded = true;
    }

    private async Task SaveLockedAsync(TrustDocument document, CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var json = JsonSerializer.Serialize(document, DroidDeckJson.Options);
        await AtomicFile.WriteAllTextAsync(TrustFile, json, keepBackup: true, cancellationToken).ConfigureAwait(false);
    }

    private ScriptTrustEntry? FindEntry(ScriptContext context) => FindEntryIn(_document, context);

    private static ScriptTrustEntry? FindEntryIn(TrustDocument document, ScriptContext context)
    {
        foreach (var entry in document.Entries)
        {
            if (SameLocation(entry, context))
            {
                return entry;
            }
        }

        return null;
    }

    private static bool SameLocation(ScriptTrustEntry entry, ScriptContext context) =>
        string.Equals(entry.RepositoryRoot, context.RepositoryRoot, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.ScriptPath, context.RelativePath, StringComparison.Ordinal);

    private async Task<ScriptContext> ResolveAsync(string repositoryRoot, string scriptPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        var repository = Path.GetFullPath(repositoryRoot);
        var script = Path.IsPathRooted(scriptPath)
            ? Path.GetFullPath(scriptPath)
            : Path.GetFullPath(Path.Combine(repository, scriptPath));
        var relativePath = Path.GetRelativePath(repository, script).Replace('\\', '/');
        if (relativePath.StartsWith("../", StringComparison.Ordinal) ||
            relativePath == ".." ||
            Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("The script must live inside the repository.", nameof(scriptPath));
        }
        var sha256 = await ComputeSha256Async(script, cancellationToken).ConfigureAwait(false);
        return new ScriptContext(repository, script, relativePath, sha256);
    }

    private static string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record ScriptContext(
        string RepositoryRoot,
        string ScriptPath,
        string RelativePath,
        string Sha256);

    private sealed class TrustDocument
    {
        public int SchemaVersion { get; set; } = 1;

        public List<ScriptTrustEntry> Entries { get; set; } = [];

        public TrustDocument Clone()
        {
            var clone = new TrustDocument();
            foreach (var entry in Entries)
            {
                clone.Entries.Add(entry.Clone());
            }

            return clone;
        }
    }

    private sealed class ScriptTrustEntry
    {
        public string RepositoryRoot { get; set; } = string.Empty;

        public string ScriptPath { get; set; } = string.Empty;

        public string Sha256 { get; set; } = string.Empty;

        public ScriptTrustState State { get; set; } = ScriptTrustState.PendingApproval;

        public DateTimeOffset DecidedAtUtc { get; set; }

        public ScriptTrustEntry Clone() => new()
        {
            RepositoryRoot = RepositoryRoot,
            ScriptPath = ScriptPath,
            Sha256 = Sha256,
            State = State,
            DecidedAtUtc = DecidedAtUtc,
        };
    }
}

namespace DroidDeck.Core.Security;

/// <summary>Whether a repository script may execute on this machine.</summary>
public enum ScriptTrustState
{
    /// <summary>An approved hash matches the script on disk.</summary>
    Trusted,

    /// <summary>The script needs a one-time trust decision on this machine.</summary>
    PendingApproval,

    /// <summary>The user denied this script hash.</summary>
    Denied,
}

/// <summary>The trust decision for one script.</summary>
/// <param name="ScriptPath">The absolute script path.</param>
/// <param name="Sha256">The SHA-256 of the script content the decision applies to.</param>
/// <param name="State">The decision.</param>
public sealed record ScriptTrustDecision(string ScriptPath, string Sha256, ScriptTrustState State);

/// <summary>Raised when a script needs a trust decision before it may run.</summary>
public sealed class ScriptTrustRequestedEventArgs : EventArgs
{
    /// <summary>Creates the event data for a pending trust decision.</summary>
    public ScriptTrustRequestedEventArgs(string repositoryRoot, string scriptPath, string relativePath, string sha256)
    {
        RepositoryRoot = repositoryRoot;
        ScriptPath = scriptPath;
        RelativePath = relativePath;
        Sha256 = sha256;
    }

    /// <summary>Gets the repository the script belongs to.</summary>
    public string RepositoryRoot { get; }

    /// <summary>Gets the absolute script path.</summary>
    public string ScriptPath { get; }

    /// <summary>Gets the repository-relative path for display.</summary>
    public string RelativePath { get; }

    /// <summary>Gets the SHA-256 of the script content asking for trust.</summary>
    public string Sha256 { get; }
}

/// <summary>
/// Pins one-time trust approvals for executable scripts referenced by team-shared
/// configuration. Approvals are keyed by the SHA-256 of the script content and stored
/// in application data, never in the repository.
/// </summary>
public interface IRepoScriptTrustService
{
    /// <summary>
    /// Raised when a script needs a trust decision. The prompt UI decides later by
    /// calling <see cref="ApproveAsync"/> or <see cref="DenyAsync"/>; authorization never
    /// waits for the decision.
    /// </summary>
    event EventHandler<ScriptTrustRequestedEventArgs>? TrustRequested;

    /// <summary>Computes the SHA-256 of a file.</summary>
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether the script may run on this machine. Pending scripts raise
    /// <see cref="TrustRequested"/> and the result stays
    /// <see cref="ScriptTrustState.PendingApproval"/> until a decision is stored.
    /// </summary>
    Task<ScriptTrustDecision> AuthorizeAsync(string repositoryRoot, string scriptPath, CancellationToken cancellationToken = default);

    /// <summary>Returns the current trust state without prompting.</summary>
    Task<ScriptTrustState> GetStateAsync(string repositoryRoot, string scriptPath, CancellationToken cancellationToken = default);

    /// <summary>Approves the script by pinning the SHA-256 of its current content.</summary>
    Task ApproveAsync(string repositoryRoot, string scriptPath, CancellationToken cancellationToken = default);

    /// <summary>Denies the script by pinning the denied SHA-256 of its current content.</summary>
    Task DenyAsync(string repositoryRoot, string scriptPath, CancellationToken cancellationToken = default);

    /// <summary>Removes any stored decision so the script prompts again.</summary>
    Task RevokeAsync(string repositoryRoot, string scriptPath, CancellationToken cancellationToken = default);
}

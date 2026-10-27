namespace DroidDeck.Core.Security;

/// <summary>
/// Stores named secrets protected by Windows DPAPI.
/// </summary>
public interface ISecretStore
{
    /// <summary>Lists secret names without decrypting values.</summary>
    Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores or replaces a secret.</summary>
    Task SetAsync(string name, string value, CancellationToken cancellationToken = default);

    /// <summary>Reads and decrypts a secret.</summary>
    Task<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes a secret and returns whether it existed.</summary>
    Task<bool> RemoveAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Returns decrypted values so diagnostics can redact them.</summary>
    Task<IReadOnlyList<string>> GetKnownValuesAsync(CancellationToken cancellationToken = default);
}

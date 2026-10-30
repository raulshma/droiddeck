namespace DroidDeck.Core.Infrastructure;

/// <summary>Display helpers for content hashes.</summary>
public static class HashText
{
    /// <summary>
    /// Shortens a hash for display: the first twelve characters, with an
    /// ellipsis when anything was cut.
    /// </summary>
    public static string ShortHash(string hash) =>
        hash.Length <= 12 ? hash : hash[..12] + "…";
}

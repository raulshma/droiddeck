using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DroidDeck.Plugins;

/// <summary>
/// Builds, parses, and validates the secret-store names used by schema secret
/// fields.
/// </summary>
/// <remarks>
/// <para>
/// A current name is <c>_p{pluginIdLength}-{pluginId}.{fieldKey}</c>. The
/// canonical decimal length prefix makes the boundary between the plugin id and
/// the field key explicit even though both may contain dots. The leading
/// underscore puts current names in a namespace no legacy name can enter: a
/// manifest-valid plugin id starts with a letter or digit, so the legacy
/// <c>{pluginId}.{fieldKey}</c> format can never start with <c>_p</c>.
/// </para>
/// <para>
/// The format is injective. Two equal current names have the same canonical
/// length digits, so they parse the same plugin id length, the same plugin id
/// substring, and the same field-key remainder, and therefore represent the same
/// pair. The legacy format is provided only for reading and deleting names
/// written by older versions.
/// </para>
/// <para>
/// Pairs whose length-prefixed name would exceed the store limit fall back to a
/// <c>_h</c> name holding the lowercase SHA-256 of a length-delimited encoding of
/// the pair. The fallback is stable and collision-resistant but not reversible,
/// so <see cref="TryParse"/> rejects it. It keeps pairs that produced a valid
/// legacy name readable instead of failing the whole lookup.
/// </para>
/// </remarks>
internal static class PluginSecretName
{
    /// <summary>The maximum length <c>DroidDeck.Core.Security.DpapiSecretStore</c> accepts.</summary>
    internal const int MaxLength = 128;

    private const string Prefix = "_p";
    private const string HashedPrefix = "_h";

    /// <summary>Builds the current, collision-resistant secret name for a field.</summary>
    /// <exception cref="ArgumentException">
    /// The parts contain characters outside the secret-name alphabet or the
    /// plugin id does not start with a letter or digit.
    /// </exception>
    internal static string Build(string pluginId, string fieldKey)
    {
        ValidatePluginId(pluginId);
        ValidateFieldKey(fieldKey);

        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}{pluginId.Length}-{pluginId}.{fieldKey}");
        return name.Length <= MaxLength ? name : BuildHashed(pluginId, fieldKey);
    }

    /// <summary>
    /// Builds the legacy <c>{pluginId}.{fieldKey}</c> name used by versions
    /// before the length-prefixed format. It is only for reading and deleting
    /// existing entries, never for new writes. Returns <see langword="null"/>
    /// when the name exceeds the store limit, because the store can never hold
    /// an entry under such a name.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The parts contain characters outside the secret-name alphabet or the
    /// plugin id does not start with a letter or digit.
    /// </exception>
    internal static string? BuildLegacy(string pluginId, string fieldKey)
    {
        ValidatePluginId(pluginId);
        ValidateFieldKey(fieldKey);

        var name = $"{pluginId}.{fieldKey}";
        return name.Length <= MaxLength ? name : null;
    }

    /// <summary>
    /// Parses a current name back into its plugin id and field key. Legacy names
    /// and malformed names return <see langword="false"/>.
    /// </summary>
    internal static bool TryParse(string? name, out string pluginId, out string fieldKey)
    {
        pluginId = string.Empty;
        fieldKey = string.Empty;
        if (!IsValidName(name) || !name!.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = name.IndexOf('-', Prefix.Length);
        if (separator <= Prefix.Length)
        {
            return false;
        }

        if (!int.TryParse(
                name.AsSpan(Prefix.Length, separator - Prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var idLength) ||
            idLength <= 0)
        {
            return false;
        }

        var contentStart = separator + 1;
        var remaining = name.Length - contentStart;

        // The remainder must hold the plugin id, the '.' separator, and at
        // least one field-key character.
        if (remaining < 3 || idLength > remaining - 2)
        {
            return false;
        }

        var parsedPluginId = name.Substring(contentStart, idLength);
        var dot = contentStart + idLength;
        if (name[dot] != '.')
        {
            return false;
        }

        var parsedFieldKey = name[(dot + 1)..];
        if (!IsValidPluginId(parsedPluginId) || !IsValidFieldKey(parsedFieldKey))
        {
            return false;
        }

        // Rebuild to reject non-canonical spellings, such as zero-padded lengths.
        if (!string.Equals(Build(parsedPluginId, parsedFieldKey), name, StringComparison.Ordinal))
        {
            return false;
        }

        pluginId = parsedPluginId;
        fieldKey = parsedFieldKey;
        return true;
    }

    /// <summary>
    /// Gets whether a name is acceptable to the secret store: 1-128 characters
    /// of ASCII letters, digits, dots, underscores, and hyphens.
    /// </summary>
    internal static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!IsAllowedCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidatePluginId(string pluginId)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        if (!IsValidPluginId(pluginId))
        {
            throw new ArgumentException(
                "The plugin id must start with an ASCII letter or digit and contain only ASCII " +
                    "letters, digits, dots, underscores, or hyphens.",
                nameof(pluginId));
        }
    }

    private static void ValidateFieldKey(string fieldKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldKey);
        if (!IsValidFieldKey(fieldKey))
        {
            throw new ArgumentException(
                "The field key must contain only ASCII letters, digits, dots, underscores, or hyphens.",
                nameof(fieldKey));
        }
    }

    private static bool IsValidPluginId(string pluginId)
        => !string.IsNullOrEmpty(pluginId) &&
            char.IsAsciiLetterOrDigit(pluginId[0]) &&
            pluginId.All(static character => IsAllowedCharacter(character));

    private static bool IsValidFieldKey(string fieldKey)
        => !string.IsNullOrEmpty(fieldKey) &&
            fieldKey.All(static character => IsAllowedCharacter(character));

    private static bool IsAllowedCharacter(char character)
        => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';

    private static string BuildHashed(string pluginId, string fieldKey)
    {
        // The length delimiter keeps dotted ids and keys from colliding the way
        // the legacy format did, and ':' is outside the secret-name alphabet.
        var identity = string.Create(
            CultureInfo.InvariantCulture,
            $"{pluginId.Length}:{pluginId}:{fieldKey.Length}:{fieldKey}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"{HashedPrefix}{Convert.ToHexStringLower(digest)}";
    }
}

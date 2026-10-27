using System.Globalization;

namespace DroidDeck.Plugin.Abstractions;

/// <summary>
/// A semantic version as defined by SemVer 2.0.0.
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    public SemanticVersion(int major, int minor, int patch, string? preRelease = null, string? buildMetadata = null)
    {
        if (major < 0 || minor < 0 || patch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), "Version parts must be non-negative.");
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = string.IsNullOrWhiteSpace(preRelease) ? null : preRelease;
        BuildMetadata = string.IsNullOrWhiteSpace(buildMetadata) ? null : buildMetadata;
    }

    /// <summary>Gets the major version.</summary>
    public int Major { get; }

    /// <summary>Gets the minor version.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch version.</summary>
    public int Patch { get; }

    /// <summary>Gets the optional pre-release label.</summary>
    public string? PreRelease { get; }

    /// <summary>Gets the optional build metadata.</summary>
    public string? BuildMetadata { get; }

    /// <summary>Parses a semantic version, throwing when it is invalid.</summary>
    public static SemanticVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new FormatException($"'{value}' is not a valid semantic version.");
        }

        return version;
    }

    /// <summary>Attempts to parse a semantic version.</summary>
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var buildIndex = text.IndexOf('+', StringComparison.Ordinal);
        string? build = null;
        if (buildIndex >= 0)
        {
            build = text[(buildIndex + 1)..];
            text = text[..buildIndex];
            if (!AreIdentifiersValid(build, allowLeadingZeroes: true))
            {
                return false;
            }
        }

        var preIndex = text.IndexOf('-', StringComparison.Ordinal);
        string? pre = null;
        if (preIndex >= 0)
        {
            pre = text[(preIndex + 1)..];
            text = text[..preIndex];
            if (!AreIdentifiersValid(pre, allowLeadingZeroes: false))
            {
                return false;
            }
        }

        var parts = text.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        if (HasLeadingZero(parts[0]) || HasLeadingZero(parts[1]) || HasLeadingZero(parts[2]))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, pre, build);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <inheritdoc />
    public bool Equals(SemanticVersion? other)
        => other is not null && CompareTo(other) == 0;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);

    /// <summary>Compares two versions for equality.</summary>
    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) => Equals(left, right);

    /// <summary>Compares two versions for inequality.</summary>
    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !Equals(left, right);

    /// <summary>Returns whether the left version precedes the right version.</summary>
    public static bool operator <(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) < 0;

    /// <summary>Returns whether the left version precedes or equals the right version.</summary>
    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) <= 0;

    /// <summary>Returns whether the left version follows the right version.</summary>
    public static bool operator >(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) > 0;

    /// <summary>Returns whether the left version follows or equals the right version.</summary>
    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) >= 0;

    private static int Compare(SemanticVersion? left, SemanticVersion? right) => left is null
        ? right is null ? 0 : -1
        : left.CompareTo(right);

    /// <summary>Returns the normalized semantic version string.</summary>
    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        if (PreRelease is not null)
        {
            value += "-" + PreRelease;
        }

        if (BuildMetadata is not null)
        {
            value += "+" + BuildMetadata;
        }

        return value;
    }

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var leftIds = left.Split('.');
        var rightIds = right.Split('.');
        var length = Math.Min(leftIds.Length, rightIds.Length);
        for (var index = 0; index < length; index++)
        {
            var leftId = leftIds[index];
            var rightId = rightIds[index];
            var leftNumeric = long.TryParse(leftId, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = long.TryParse(rightId, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

            if (leftNumeric && rightNumeric)
            {
                var numericResult = leftNumber.CompareTo(rightNumber);
                if (numericResult != 0)
                {
                    return numericResult;
                }
            }
            else if (leftNumeric)
            {
                return -1;
            }
            else if (rightNumeric)
            {
                return 1;
            }
            else
            {
                var textResult = string.CompareOrdinal(leftId, rightId);
                if (textResult != 0)
                {
                    return textResult;
                }
            }
        }

        return leftIds.Length.CompareTo(rightIds.Length);
    }

    private static bool AreIdentifiersValid(string value, bool allowLeadingZeroes)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0 || !identifier.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }

            if (!allowLeadingZeroes && identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasLeadingZero(string value) => value.Length > 1 && value[0] == '0';
}

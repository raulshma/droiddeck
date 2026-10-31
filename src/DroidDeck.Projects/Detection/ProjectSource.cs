using System.Text.RegularExpressions;

namespace DroidDeck.Projects.Detection;

/// <summary>
/// Reads project source files and extracts the values detectors share: the first existing file in a
/// preference list, build script text, Gradle-style string values, and application identity patterns.
/// </summary>
internal static partial class ProjectSource
{
    /// <summary>Returns the first existing file in <paramref name="names"/>, or <see langword="null"/>.</summary>
    internal static string? FindFirst(string directory, params string[] names)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        foreach (var name in names)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Returns the Kotlin or Groovy Gradle build file in a directory, or <see langword="null"/>.</summary>
    internal static string? FindBuildFile(string directory) =>
        FindFirst(directory, "build.gradle.kts", "build.gradle");

    /// <summary>Reads a file as text, returning <see langword="null"/> when it is missing or unreadable.</summary>
    internal static async Task<string?> ReadTextAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the trimmed <c>value</c> group of the first match, or <see langword="null"/>. Pass
    /// <paramref name="trim"/> as <see langword="false"/> where a detector historically preserved whitespace.
    /// </summary>
    internal static string? MatchValue(Regex regex, string? text, bool trim = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = regex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["value"].Value;
        return trim ? value.Trim() : value;
    }

    /// <summary>Removes line comments so declaration scans do not read commented-out code.</summary>
    internal static string StripLineComments(string text) => LineCommentRegex().Replace(text, string.Empty);

    /// <summary>
    /// Returns the body of the first <c>blockName { ... }</c> block, without the braces, or
    /// <see langword="null"/> when the block is missing. Nested braces are matched.
    /// </summary>
    internal static string? ExtractBlockBody(string? text, string blockName)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var search = 0;
        while (search < text.Length)
        {
            var start = text.IndexOf(blockName, search, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            search = start + blockName.Length;
            if (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            {
                continue;
            }

            var brace = search;
            while (brace < text.Length && char.IsWhiteSpace(text[brace]))
            {
                brace++;
            }

            if (brace >= text.Length || text[brace] != '{')
            {
                continue;
            }

            var depth = 0;
            var end = brace;
            for (; end < text.Length; end++)
            {
                if (text[end] == '{')
                {
                    depth++;
                }
                else if (text[end] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }
            }

            return end < text.Length ? text[(brace + 1)..end] : text[(brace + 1)..];
        }

        return null;
    }

    /// <summary>Matches a Gradle or Kotlin DSL application ID declaration.</summary>
    [GeneratedRegex(
        @"applicationId\s*=?\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    internal static partial Regex ApplicationIdPattern();

    /// <summary>Matches a Gradle or Kotlin DSL namespace declaration.</summary>
    [GeneratedRegex(
        @"namespace\s*=?\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    internal static partial Regex NamespacePattern();

    /// <summary>Matches an Android application ID suffix declaration.</summary>
    [GeneratedRegex(
        @"applicationIdSuffix\s*=?\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    internal static partial Regex ApplicationIdSuffixPattern();

    [GeneratedRegex(@"//[^\r\n]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LineCommentRegex();
}

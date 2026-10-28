using System.Text.RegularExpressions;

namespace DroidDeck.Core.Runs;

/// <summary>A compiler or build error with a clickable source location.</summary>
public sealed class BuildError
{
    /// <summary>Gets or sets the absolute or project-relative file path.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the one-based line number.</summary>
    public int Line { get; set; }

    /// <summary>Gets or sets the one-based column number.</summary>
    public int Column { get; set; }

    /// <summary>Gets or sets the error message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the severity.</summary>
    public string Severity { get; set; } = "error";

    /// <summary>Gets or sets the compiler error code, when one was reported.</summary>
    public string? Code { get; set; }

    /// <summary>Gets or sets the raw line.</summary>
    public string Raw { get; set; } = string.Empty;

    /// <summary>Gets a compact display label.</summary>
    public string Display => $"{Path.GetFileName(FilePath)}:{Line}:{Column} {Message}";
}

/// <summary>Parses Gradle, Kotlin, and Java output for errors with source locations.</summary>
public static partial class BuildErrorParser
{
    /// <summary>Extracts distinct errors from build output.</summary>
    public static IReadOnlyList<BuildError> Parse(string? output, string? projectRoot = null)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var errors = new List<BuildError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var parsed = ParseKotlin(line, projectRoot)
                ?? ParseJava(line, projectRoot)
                ?? ParseDart(line, projectRoot)
                ?? ParseGenericGradle(line, projectRoot);
            if (parsed is null)
            {
                continue;
            }

            var key = $"{parsed.FilePath}|{parsed.Line}|{parsed.Column}|{parsed.Message}";
            if (seen.Add(key))
            {
                errors.Add(parsed);
            }
        }

        return errors;
    }

    /// <summary>Parses one line into an error, returning <see langword="null"/> when it is not an error.</summary>
    public static BuildError? ParseLine(string line, string? projectRoot = null)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return ParseKotlin(line, projectRoot)
            ?? ParseJava(line, projectRoot)
            ?? ParseDart(line, projectRoot)
            ?? ParseGenericGradle(line, projectRoot);
    }

    private static BuildError? ParseKotlin(string line, string? projectRoot)
    {
        var match = KotlinErrorRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var path = NormalizePath(match.Groups["file"].Value, projectRoot);
        var message = match.Groups["message"].Value.Trim();
        return new BuildError
        {
            FilePath = path,
            Line = ParseInt(match.Groups["line"].Value),
            Column = ParseInt(match.Groups["column"].Value),
            Message = message,
            Severity = "error",
            Raw = line.Trim(),
        };
    }

    private static BuildError? ParseJava(string line, string? projectRoot)
    {
        var match = JavaErrorRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var path = NormalizePath(match.Groups["file"].Value, projectRoot);
        var message = match.Groups["message"].Value.Trim();
        return new BuildError
        {
            FilePath = path,
            Line = ParseInt(match.Groups["line"].Value),
            Column = 0,
            Message = message,
            Severity = "error",
            Raw = line.Trim(),
        };
    }

    private static BuildError? ParseDart(string line, string? projectRoot)
    {
        var match = DartErrorRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        return new BuildError
        {
            FilePath = NormalizePath(match.Groups["file"].Value, projectRoot),
            Line = ParseInt(match.Groups["line"].Value),
            Column = ParseInt(match.Groups["column"].Value),
            Message = match.Groups["message"].Value.Trim(),
            Severity = "error",
            Raw = line.Trim(),
        };
    }

    private static BuildError? ParseGenericGradle(string line, string? projectRoot)
    {
        var match = GenericErrorRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        return new BuildError
        {
            FilePath = NormalizePath(match.Groups["file"].Value, projectRoot),
            Line = ParseInt(match.Groups["line"].Value),
            Column = 0,
            Message = match.Groups["message"].Value.Trim(),
            Severity = "error",
            Raw = line.Trim(),
        };
    }

    private static string NormalizePath(string path, string? projectRoot)
    {
        var trimmed = path.Trim().Trim('"');
        if (trimmed.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[8..];
        }
        else if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }

        trimmed = Uri.UnescapeDataString(trimmed.Replace('/', Path.DirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(projectRoot) && !Path.IsPathRooted(trimmed))
        {
            return Path.GetFullPath(Path.Combine(projectRoot, trimmed));
        }

        return trimmed;
    }

    private static int ParseInt(string value) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    [GeneratedRegex(
        @"^\s*e:\s+(?<file>.+?):\s*\((?<line>\d+),\s*(?<column>\d+)\)\s*:?\s*(?<message>.+)$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex KotlinErrorRegex();

    [GeneratedRegex(
        @"^\s*(?<file>(?:[A-Za-z]:[\\/]|/)?[^:\r\n]+?\.(?:java|kt|kts)):(?<line>\d+):\s*error:\s*(?<message>.+)$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex JavaErrorRegex();

    [GeneratedRegex(
        @"^\s*(?<file>(?:[A-Za-z]:[\\/]|/)?[^:\r\n]+?\.dart):(?<line>\d+):(?<column>\d+):\s*(?:error|Error):\s*(?<message>.+)$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DartErrorRegex();

    [GeneratedRegex(
        @"^\s*(?<file>(?:[A-Za-z]:[\\/]|/)?[^:\r\n]+?\.(?:kt|kts|java|xml|gradle)):(?<line>\d+):\s*(?:error|Error):\s*(?<message>.+)$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex GenericErrorRegex();
}

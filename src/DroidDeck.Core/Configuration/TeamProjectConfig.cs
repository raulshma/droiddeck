using System.Text.Json.Nodes;

namespace DroidDeck.Core.Configuration;

/// <summary>The kind of commit-safety violation in team-shared configuration.</summary>
public enum TeamConfigViolationKind
{
    /// <summary>A machine-specific absolute path.</summary>
    AbsolutePath,

    /// <summary>A DPAPI-protected secret blob.</summary>
    SecretBlob,
}

/// <summary>A commit-safety violation in team-shared configuration.</summary>
/// <param name="Path">The JSON path of the offending value.</param>
/// <param name="Kind">The kind of violation.</param>
/// <param name="Value">The offending value.</param>
/// <param name="Message">The user-facing explanation.</param>
public sealed record TeamConfigViolation(
    string Path,
    TeamConfigViolationKind Kind,
    string Value,
    string Message);

/// <summary>The result of validating team-shared configuration for commit safety.</summary>
public sealed class TeamConfigValidationResult
{
    /// <summary>Gets the shared result for documents without violations.</summary>
    internal static TeamConfigValidationResult Valid { get; } = new();

    private TeamConfigValidationResult()
    {
        Violations = [];
    }

    /// <summary>Initializes a result with violations.</summary>
    public TeamConfigValidationResult(IReadOnlyList<TeamConfigViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        Violations = violations;
    }

    /// <summary>Gets the violations found.</summary>
    public IReadOnlyList<TeamConfigViolation> Violations { get; }

    /// <summary>Gets whether the document is safe to commit.</summary>
    public bool IsValid => Violations.Count == 0;
}

/// <summary>
/// Thrown when a write would make the team-shared configuration unsafe to commit.
/// </summary>
public sealed class TeamConfigViolationException : InvalidOperationException
{
    /// <summary>Creates the exception for a set of violations.</summary>
    public TeamConfigViolationException(IReadOnlyList<TeamConfigViolation> violations)
        : base($"Team-shared configuration cannot be saved: {violations[0].Message}")
    {
        Violations = violations;
    }

    /// <summary>Gets every violation that blocked the write.</summary>
    public IReadOnlyList<TeamConfigViolation> Violations { get; }
}

/// <summary>
/// Validates team-shared project configuration so <c>.droiddeck/project.json</c> can be
/// committed without machine paths or secrets.
/// </summary>
public static class TeamProjectConfigValidator
{
    // Every DPAPI user blob begins with this provider header, so base64 serializations
    // of protected secrets always start with the same leading characters.
    private const string DpapiBase64Prefix = "AQAAANCMnd8BFdERjHoAwE/Cl+sB";
    private const int MinimumBlobLength = 48;

    private static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ps1",
        ".cmd",
        ".bat",
        ".sh",
    };

    /// <summary>Returns every commit-safety violation in the document.</summary>
    public static TeamConfigValidationResult Validate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var violations = new List<TeamConfigViolation>();
        CollectViolations(document, string.Empty, violations);
        return violations.Count == 0 ? TeamConfigValidationResult.Valid : new TeamConfigValidationResult(violations);
    }

    /// <summary>
    /// Returns the executable scripts referenced by the document, in document order
    /// without duplicates, so callers can request repository script trust for each.
    /// </summary>
    public static IReadOnlyList<string> GetScriptReferences(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var scripts = new List<string>();
        CollectScripts(document, scripts, new HashSet<string>(StringComparer.Ordinal));
        return scripts;
    }

    /// <summary>Returns whether a value names an executable script.</summary>
    public static bool IsExecutableScript(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return ScriptExtensions.Contains(Path.GetExtension(value));
    }

    private static void CollectViolations(JsonNode? node, string path, List<TeamConfigViolation> violations)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    CollectViolations(property.Value, CombinePath(path, property.Key), violations);
                }

                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    CollectViolations(array[index], $"{path}[{index}]", violations);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (IsMachinePath(text))
                {
                    violations.Add(new TeamConfigViolation(
                        path,
                        TeamConfigViolationKind.AbsolutePath,
                        text,
                        $"\"{text}\" is an absolute path. Team-shared configuration must use project-relative values."));
                }
                else if (IsDpapiBlob(text))
                {
                    violations.Add(new TeamConfigViolation(
                        path,
                        TeamConfigViolationKind.SecretBlob,
                        text,
                        $"The value at \"{path}\" is an encrypted secret blob. Reference secrets with ${{secret:NAME}} instead."));
                }

                break;
        }
    }

    private static void CollectScripts(JsonNode? node, List<string> scripts, HashSet<string> seen)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    CollectScripts(property.Value, scripts, seen);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    CollectScripts(item, scripts, seen);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text) &&
                                      IsExecutableScript(text) &&
                                      seen.Add(text):
                scripts.Add(text);
                break;
        }
    }

    private static bool IsMachinePath(string value) => Path.IsPathRooted(value);

    private static bool IsDpapiBlob(string value) =>
        value.Length >= MinimumBlobLength && value.StartsWith(DpapiBase64Prefix, StringComparison.Ordinal);

    private static string CombinePath(string prefix, string name) => prefix.Length == 0 ? name : prefix + "." + name;
}

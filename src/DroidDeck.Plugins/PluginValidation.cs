namespace DroidDeck.Plugins;

/// <summary>
/// The severity of a plugin validation issue.
/// </summary>
public enum PluginValidationSeverity
{
    /// <summary>The plugin can still load, but the user or developer should know.</summary>
    Warning,

    /// <summary>The plugin cannot load.</summary>
    Error,
}

/// <summary>
/// A single problem found while validating or loading a plugin.
/// </summary>
/// <param name="Code">A stable machine-readable issue code.</param>
/// <param name="Message">A human-readable explanation.</param>
/// <param name="Severity">The severity of the issue.</param>
public sealed record PluginValidationIssue(string Code, string Message, PluginValidationSeverity Severity)
{
    /// <inheritdoc />
    public override string ToString() => $"{Severity} {Code}: {Message}";
}

/// <summary>
/// The issues found while validating a plugin. Errors block loading; warnings do not.
/// </summary>
public sealed class PluginValidationResult
{
    private readonly List<PluginValidationIssue> _issues = [];

    /// <summary>Gets all issues in the order they were found.</summary>
    public IReadOnlyList<PluginValidationIssue> Issues => _issues;

    /// <summary>Gets a value indicating whether the plugin can load.</summary>
    public bool IsValid => !_issues.Exists(issue => issue.Severity == PluginValidationSeverity.Error);

    /// <summary>Gets a value indicating whether any warning was recorded.</summary>
    public bool HasWarnings => _issues.Exists(issue => issue.Severity == PluginValidationSeverity.Warning);

    internal void Error(string code, string message)
        => _issues.Add(new PluginValidationIssue(code, message, PluginValidationSeverity.Error));

    internal void Warning(string code, string message)
        => _issues.Add(new PluginValidationIssue(code, message, PluginValidationSeverity.Warning));

    internal void AddRange(PluginValidationResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _issues.AddRange(other._issues);
    }
}

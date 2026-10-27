namespace DroidDeck.Core.Diagnostics;

/// <summary>An additional named section supplied by the application shell.</summary>
/// <param name="Title">The section heading.</param>
/// <param name="Body">The section content.</param>
public sealed record DiagnosticsSection(string Title, string Body);

/// <summary>Controls what a diagnostics report contains.</summary>
public sealed class DiagnosticsReportOptions
{
    /// <summary>Gets whether local folder paths are included.</summary>
    public bool IncludePaths { get; init; } = true;

    /// <summary>Gets whether recent error entries are included.</summary>
    public bool IncludeRecentErrors { get; init; } = true;

    /// <summary>Gets whether the tail of the application log is included.</summary>
    public bool IncludeRecentLogs { get; init; } = true;

    /// <summary>Gets the maximum number of recent log entries included.</summary>
    public int RecentLogCount { get; init; } = 50;

    /// <summary>Gets the maximum number of recent errors included.</summary>
    public int RecentErrorCount { get; init; } = 20;

    /// <summary>Gets extra sections contributed by the shell, such as plugin health.</summary>
    public IReadOnlyList<DiagnosticsSection> ExtraSections { get; init; } = [];
}

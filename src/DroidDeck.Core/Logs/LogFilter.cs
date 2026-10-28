using System.Text.RegularExpressions;
using DroidDeck.Core.Infrastructure;

namespace DroidDeck.Core.Logs;

/// <summary>Criteria used to filter a log view.</summary>
public sealed class LogFilterCriteria
{
    /// <summary>Gets or sets the minimum level. Empty means all levels.</summary>
    public string? MinimumLevel { get; set; }

    /// <summary>Gets or sets the tag filter. Multiple tags are separated by commas.</summary>
    public string? Tags { get; set; }

    /// <summary>Gets or sets the PID filter.</summary>
    public int? Pid { get; set; }

    /// <summary>Gets or sets the package filter.</summary>
    public string? PackageName { get; set; }

    /// <summary>Gets or sets the text filter.</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets whether the text filter is a regular expression.</summary>
    public bool UseRegex { get; set; }

    /// <summary>Gets or sets whether matching is case sensitive.</summary>
    public bool CaseSensitive { get; set; }

    /// <summary>Gets or sets whether the text must match a whole word.</summary>
    public bool WholeWord { get; set; }

    /// <summary>Gets or sets text that excludes a line.</summary>
    public string? ExcludeText { get; set; }

    /// <summary>Gets whether the criteria filter anything.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(MinimumLevel) &&
        string.IsNullOrWhiteSpace(Tags) &&
        Pid is null &&
        string.IsNullOrWhiteSpace(PackageName) &&
        string.IsNullOrWhiteSpace(Text) &&
        string.IsNullOrWhiteSpace(ExcludeText);
}

/// <summary>A compiled log filter that can be applied to a snapshot.</summary>
public sealed class LogFilter
{
    private readonly LogFilterCriteria _criteria;
    private readonly HashSet<string> _tags;
    private readonly Regex? _text;
    private readonly Regex? _exclude;
    private readonly int _minimumRank;

    /// <summary>Compiles a filter.</summary>
    public LogFilter(LogFilterCriteria criteria)
    {
        _criteria = criteria ?? throw new ArgumentNullException(nameof(criteria));
        _tags = string.IsNullOrWhiteSpace(criteria.Tags)
            ? []
            : criteria.Tags
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _minimumRank = LogLevels.Rank(criteria.MinimumLevel);
        _text = BuildRegex(criteria.Text, criteria.UseRegex, criteria.CaseSensitive, criteria.WholeWord);
        _exclude = BuildRegex(criteria.ExcludeText, criteria.UseRegex, criteria.CaseSensitive, wholeWord: false);
    }

    /// <summary>Gets the source criteria.</summary>
    public LogFilterCriteria Criteria => _criteria;

    /// <summary>Returns whether one entry matches.</summary>
    public bool Matches(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_minimumRank > 0 && LogLevels.Rank(entry.Level) < _minimumRank)
        {
            return false;
        }

        if (_tags.Count > 0 && !_tags.Contains(entry.Tag))
        {
            return false;
        }

        if (_criteria.Pid is { } pid && entry.Pid != pid)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_criteria.PackageName) &&
            !string.Equals(entry.PackageName, _criteria.PackageName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (_text is not null)
        {
            var haystack = entry.Message + "\n" + entry.Tag;
            if (!_text.IsMatch(haystack))
            {
                return false;
            }
        }

        if (_exclude is not null && _exclude.IsMatch(entry.Message))
        {
            return false;
        }

        return true;
    }

    /// <summary>Filters a snapshot in one pass.</summary>
    public IReadOnlyList<LogEntry> Apply(IReadOnlyList<LogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (_criteria.IsEmpty)
        {
            return entries;
        }

        var result = new List<LogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (Matches(entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>Returns the indices of entries that match a search query.</summary>
    public static IReadOnlyList<int> Search(
        IReadOnlyList<LogEntry> entries,
        string? query,
        bool useRegex = false,
        bool caseSensitive = false,
        bool wholeWord = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrEmpty(query))
        {
            return [];
        }

        var regex = BuildRegex(query, useRegex, caseSensitive, wholeWord);
        if (regex is null)
        {
            return [];
        }

        var matches = new List<int>();
        for (var index = 0; index < entries.Count; index++)
        {
            if (regex.IsMatch(entries[index].Message) || regex.IsMatch(entries[index].Tag))
            {
                matches.Add(index);
            }
        }

        return matches;
    }

    private static Regex? BuildRegex(string? pattern, bool useRegex, bool caseSensitive, bool wholeWord)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        var options = RegexOptions.CultureInvariant;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        if (!useRegex)
        {
            pattern = Regex.Escape(pattern);
        }

        if (wholeWord)
        {
            pattern = $@"\b(?:{pattern})\b";
        }

        try
        {
            return new Regex(pattern, options, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            // An invalid user pattern should show no results rather than break the view.
            return null;
        }
    }
}

/// <summary>Maps log levels to sortable ranks.</summary>
public static class LogLevels
{
    /// <summary>Returns the rank for a level, where verbose is 1 and fatal is 6.</summary>
    public static int Rank(string? level) => level?.Trim().ToUpperInvariant() switch
    {
        "V" or "VERBOSE" => 1,
        "D" or "DEBUG" => 2,
        "I" or "INFO" => 3,
        "W" or "WARN" or "WARNING" => 4,
        "E" or "ERROR" => 5,
        "F" or "A" or "FATAL" or "ASSERT" => 6,
        _ => 0,
    };
}

/// <summary>A highlight rule applied to log lines.</summary>
public sealed class HighlightRule
{
    /// <summary>Gets or sets the stable rule identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the pattern.</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the pattern is a regular expression.</summary>
    public bool IsRegex { get; set; }

    /// <summary>Gets or sets the foreground color as <c>#RRGGBB</c>.</summary>
    public string Foreground { get; set; } = "#FF5555";

    /// <summary>Gets or sets the optional background color as <c>#RRGGBB</c>.</summary>
    public string? Background { get; set; }

    /// <summary>Gets or sets whether the line is bold.</summary>
    public bool Bold { get; set; }

    /// <summary>Gets or sets whether the rule is enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets whether the rule is built in.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Gets the built-in rules.</summary>
    public static IReadOnlyList<HighlightRule> BuiltIn { get; } =
    [
        new() { Id = "fatal", Name = "Crashes", Pattern = "FATAL EXCEPTION|ANR in |*** *** ***", IsRegex = true, Foreground = "#FF4D6D", Bold = true, IsBuiltIn = true },
        new() { Id = "error", Name = "Errors", Pattern = "E/|error:", IsRegex = true, Foreground = "#FF6B6B", IsBuiltIn = true },
        new() { Id = "warning", Name = "Warnings", Pattern = "W/|warning:", IsRegex = true, Foreground = "#F5A524", IsBuiltIn = true },
        new() { Id = "test-failure", Name = "Test failures", Pattern = "FAILED|AssertionError", IsRegex = true, Foreground = "#FF6B6B", Bold = true, IsBuiltIn = true },
    ];
}

/// <summary>A saved filter scoped to a project.</summary>
public sealed class SavedLogFilter
{
    /// <summary>Gets or sets the identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the project identifier, or empty for a global filter.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Gets or sets the criteria.</summary>
    public LogFilterCriteria Criteria { get; set; } = new();
}

/// <summary>Persists saved filters per project.</summary>
public interface ISavedFilterStore
{
    /// <summary>Lists filters for a project, including global filters.</summary>
    Task<IReadOnlyList<SavedLogFilter>> ListAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Saves a filter.</summary>
    Task SaveAsync(SavedLogFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Deletes a filter.</summary>
    Task DeleteAsync(string projectId, string filterId, CancellationToken cancellationToken = default);
}

/// <summary>Stores saved filters as JSON files under the data directory.</summary>
public sealed class JsonSavedFilterStore : ISavedFilterStore
{
    private readonly string _directory;
    private readonly object _sync = new();

    /// <summary>Creates a store under the supplied data paths.</summary>
    public JsonSavedFilterStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = paths.FiltersDirectory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedLogFilter>> ListAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var filters = new List<SavedLogFilter>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var filter = DroidDeckJson.Deserialize<SavedLogFilter>(json);
                if (filter is not null &&
                    (string.IsNullOrEmpty(filter.ProjectId) ||
                     string.Equals(filter.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)))
                {
                    filters.Add(filter);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // A broken filter file must not break the log view.
            }
        }

        return filters.OrderBy(filter => filter.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <inheritdoc />
    public async Task SaveAsync(SavedLogFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Directory.CreateDirectory(_directory);
        var path = GetPath(filter.ProjectId, filter.Id);
        await AtomicFile.WriteAllTextAsync(path, DroidDeckJson.Serialize(filter), keepBackup: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string projectId, string filterId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var path = GetPath(projectId, filterId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        return Task.CompletedTask;
    }

    private string GetPath(string projectId, string filterId)
    {
        var safeProject = Safe(projectId);
        var safeFilter = Safe(filterId);
        return Path.Combine(_directory, $"{safeProject}-{safeFilter}.json");
    }

    private static string Safe(string value)
    {
        var safe = new string((value ?? string.Empty)
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')
            .ToArray());
        return safe.Length == 0 ? "global" : safe;
    }
}

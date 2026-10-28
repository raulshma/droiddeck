using DroidDeck.Core;
using DroidDeck.Core.Logs;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Logcat;

public sealed class LogFilterTests
{
    [Fact]
    public void Apply_FiltersByMinimumLevelTagPidAndPackage()
    {
        var entries = new[]
        {
            Entry("D", "Sample", 1, "com.example", "debug line"),
            Entry("E", "Sample", 1, "com.example", "error line"),
            Entry("E", "Other", 1, "com.example", "other tag"),
            Entry("E", "Sample", 2, "com.example", "other pid"),
            Entry("E", "Sample", 1, "com.other", "other package"),
        };
        var filter = new LogFilter(new LogFilterCriteria
        {
            MinimumLevel = "W",
            Tags = "Sample",
            Pid = 1,
            PackageName = "com.example",
        });

        var result = filter.Apply(entries);

        Assert.Single(result);
        Assert.Equal("error line", result[0].Message);
    }

    [Fact]
    public void Apply_SupportsRegexCaseSensitivityWholeWordAndExclusion()
    {
        var entries = new[]
        {
            Entry("E", "Network", 1, "com.example", "Connection timeout"),
            Entry("E", "Network", 1, "com.example", "connection reset"),
            Entry("E", "Network", 1, "com.example", "Reconnect timeout ignored"),
        };
        var filter = new LogFilter(new LogFilterCriteria
        {
            Text = "timeout",
            UseRegex = false,
            CaseSensitive = true,
            WholeWord = true,
            ExcludeText = "ignored",
        });

        var result = filter.Apply(entries);

        Assert.Single(result);
        Assert.Equal("Connection timeout", result[0].Message);
    }

    [Fact]
    public void Apply_ReturnsAllEntriesWhenCriteriaAreEmpty()
    {
        IReadOnlyList<LogEntry> entries =
        [
            Entry("I", "A", 1, null, "one"),
            Entry("W", "B", 2, null, "two"),
        ];
        var filter = new LogFilter(new LogFilterCriteria());

        Assert.Same(entries, filter.Apply(entries));
    }

    [Fact]
    public void Search_ReturnsMatchingIndicesInOrder()
    {
        var entries = new[]
        {
            Entry("I", "Main", 1, null, "first"),
            Entry("I", "Main", 1, null, "ERROR here"),
            Entry("E", "ErrorTag", 1, null, "third"),
        };

        var matches = LogFilter.Search(entries, "error");

        Assert.Equal([1, 2], matches);
    }

    [Fact]
    public void Search_InvalidRegexReturnsNoMatches()
    {
        var entries = new[] { Entry("I", "Main", 1, null, "text") };

        var matches = LogFilter.Search(entries, "(", useRegex: true);

        Assert.Empty(matches);
    }

    [Fact]
    public void Search_WholeWordDoesNotMatchSubstring()
    {
        var entries = new[]
        {
            Entry("I", "Main", 1, null, "connect"),
            Entry("I", "Main", 1, null, "disconnect"),
        };

        var matches = LogFilter.Search(entries, "connect", wholeWord: true);

        Assert.Equal([0], matches);
    }

    [Fact]
    public async Task SavedFilterStore_PersistsProjectAndGlobalFilters()
    {
        using var temp = new TempDirectory();
        var store = new JsonSavedFilterStore(new AppPaths(temp.Path));
        var projectFilter = new SavedLogFilter
        {
            Id = "project-filter",
            Name = "Errors",
            ProjectId = "project-1",
            Criteria = new LogFilterCriteria { MinimumLevel = "E" },
        };
        var globalFilter = new SavedLogFilter
        {
            Id = "global-filter",
            Name = "Crash tags",
            ProjectId = string.Empty,
            Criteria = new LogFilterCriteria { Tags = "AndroidRuntime" },
        };

        await store.SaveAsync(projectFilter);
        await store.SaveAsync(globalFilter);

        var forProject = await store.ListAsync("project-1");
        var forOtherProject = await store.ListAsync("project-2");

        Assert.Equal(2, forProject.Count);
        Assert.Single(forOtherProject);
        Assert.Equal("Crash tags", forOtherProject[0].Name);

        await store.DeleteAsync("project-1", "project-filter");

        Assert.Single(await store.ListAsync("project-1"));
    }

    private static LogEntry Entry(
        string level,
        string tag,
        int pid,
        string? packageName,
        string message) => new()
        {
            Level = level,
            Tag = tag,
            Pid = pid,
            PackageName = packageName,
            Message = message,
            Raw = $"{level}/{tag}: {message}",
        };
}

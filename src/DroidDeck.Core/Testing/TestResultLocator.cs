using System.Text;
using System.Text.RegularExpressions;

namespace DroidDeck.Core.Testing;

/// <summary>Finds the files a test command wrote, optionally ignoring older runs.</summary>
internal static class TestResultLocator
{
    private const int MaxCandidateFiles = 20_000;

    /// <summary>Resolves a result path, directory, or glob to files written by this run.</summary>
    public static List<string> Find(
        string projectRoot,
        string resultPath,
        DateTime? notOlderThanUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultPath);
        var normalizedGlob = resultPath.Replace('\\', '/');
        var rooted = Path.IsPathRooted(resultPath);
        var absolute = rooted
            ? Path.GetFullPath(resultPath)
            : Path.GetFullPath(Path.Combine(projectRoot, resultPath));
        if (File.Exists(absolute))
        {
            return IsFresh(absolute, notOlderThanUtc) ? [absolute] : [];
        }

        if (Directory.Exists(absolute))
        {
            return Directory
                .EnumerateFiles(absolute, "*", SearchOption.AllDirectories)
                .Where(path => IsFresh(path, notOlderThanUtc))
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var searchRoot = DetermineSearchRoot(projectRoot, resultPath);
        if (!Directory.Exists(searchRoot))
        {
            return [];
        }

        var pattern = GlobToRegex(normalizedGlob);
        var matches = new List<string>();
        var visited = 0;
        foreach (var file in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > MaxCandidateFiles)
            {
                break;
            }

            if (!IsFresh(file, notOlderThanUtc))
            {
                continue;
            }

            var comparison = rooted
                ? Path.GetFullPath(file).Replace('\\', '/')
                : Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            if (pattern.IsMatch(comparison))
            {
                matches.Add(Path.GetFullPath(file));
            }
        }

        matches.Sort(StringComparer.OrdinalIgnoreCase);
        return matches;
    }

    private static bool IsFresh(string file, DateTime? notOlderThanUtc) =>
        notOlderThanUtc is null ||
        File.GetLastWriteTimeUtc(file) >= notOlderThanUtc.Value.AddSeconds(-2);

    private static string DetermineSearchRoot(string projectRoot, string glob)
    {
        var normalized = glob.Replace('\\', '/');
        var wildcard = normalized.IndexOfAny(['*', '?']);
        var prefix = wildcard < 0 ? normalized : normalized[..wildcard];
        var separator = prefix.LastIndexOf('/');
        var directoryPart = separator <= 0 ? prefix : prefix[..separator];
        if (string.IsNullOrEmpty(directoryPart))
        {
            return projectRoot;
        }

        return Path.IsPathRooted(glob)
            ? Path.GetFullPath(directoryPart)
            : Path.GetFullPath(Path.Combine(projectRoot, directoryPart));
    }

    private static Regex GlobToRegex(string glob)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < glob.Length; index++)
        {
            var character = glob[index];
            if (character == '*')
            {
                if (index + 1 < glob.Length && glob[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < glob.Length && glob[index + 1] == '/')
                    {
                        index++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (character == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(character.ToString()));
            }
        }

        builder.Append('$');
        return new Regex(
            builder.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }
}

using System.Text;
using System.Text.RegularExpressions;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;

namespace DroidDeck.Projects.Artifacts;

/// <summary>Resolves an artifact glob to a base APK and its split APKs.</summary>
public sealed partial class GlobArtifactResolver : IArtifactResolver
{
    private const int MaxCandidateFiles = 20_000;
    private readonly IApkInspector _inspector;

    /// <summary>Creates a resolver that inspects candidates with the supplied inspector.</summary>
    public GlobArtifactResolver(IApkInspector inspector)
    {
        _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
    }

    /// <inheritdoc />
    public async Task<ArtifactResolution> ResolveAsync(
        ProjectModel project,
        RunConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(configuration);
        var context = RunTemplateContext.ForProject(project, configuration);
        var glob = TemplateEngine.Expand(configuration.Artifact.Glob, context);
        if (string.IsNullOrWhiteSpace(glob) && project.ApkFiles.Count > 0)
        {
            glob = project.ApkFiles[0].Path;
        }

        if (string.IsNullOrWhiteSpace(glob))
        {
            return new ArtifactResolution
            {
                Glob = glob,
                Error = "No artifact glob is configured.",
                Candidates = [],
            };
        }

        IReadOnlyList<string> matches;
        try
        {
            matches = FindMatches(project.RootPath, glob, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ArtifactResolution
            {
                Glob = glob,
                Error = $"The artifact folder could not be searched: {exception.Message}",
            };
        }

        if (matches.Count == 0)
        {
            return new ArtifactResolution
            {
                Glob = glob,
                Error = $"No APK matched the artifact glob \"{glob}\".",
                Candidates = [],
            };
        }

        if (matches.Count == 1)
        {
            var single = await _inspector.InspectAsync(matches[0], cancellationToken).ConfigureAwait(false);
            if (!single.Succeeded)
            {
                return new ArtifactResolution
                {
                    Glob = glob,
                    Candidates = matches.ToList(),
                    Error = single.Error,
                };
            }

            var mismatch = ValidatePackage(configuration, single.Info!);
            if (mismatch is not null)
            {
                return new ArtifactResolution
                {
                    Glob = glob,
                    Candidates = matches.ToList(),
                    Error = mismatch,
                };
            }

            return new ArtifactResolution
            {
                Glob = glob,
                BaseApk = matches[0],
                Files = [matches[0]],
                Info = single.Info,
                Candidates = matches.ToList(),
            };
        }

        var inspections = await _inspector.InspectManyAsync(matches, cancellationToken).ConfigureAwait(false);
        var valid = new List<(string Path, ApkInfo Info)>();
        for (var index = 0; index < matches.Count; index++)
        {
            if (index < inspections.Count && inspections[index].Succeeded)
            {
                valid.Add((matches[index], inspections[index].Info!));
            }
        }

        var baseCandidates = valid
            .Where(entry => !entry.Info.IsSplit && !entry.Info.PackageName.EndsWith(".test", StringComparison.Ordinal))
            .ToList();
        if (baseCandidates.Count == 0)
        {
            return new ArtifactResolution
            {
                Glob = glob,
                Candidates = matches.ToList(),
                Error = $"No base APK was found among {matches.Count} files matched by \"{glob}\". " +
                        "Point the glob at the base APK or include the split APK set.",
            };
        }

        if (baseCandidates.Count > 1)
        {
            return new ArtifactResolution
            {
                Glob = glob,
                Candidates = baseCandidates.Select(entry => entry.Path).ToList(),
                Error = $"The artifact glob \"{glob}\" is ambiguous: {baseCandidates.Count} base APKs matched. " +
                        "Use a variant-specific glob such as app/build/outputs/apk/debug/*.apk.",
            };
        }

        var baseEntry = baseCandidates[0];
        var packageMismatch = ValidatePackage(configuration, baseEntry.Info);
        if (packageMismatch is not null)
        {
            return new ArtifactResolution
            {
                Glob = glob,
                Candidates = matches.ToList(),
                Error = packageMismatch,
            };
        }

        var files = new List<string> { baseEntry.Path };
        if (configuration.Artifact.IncludeSplits)
        {
            var directory = Path.GetDirectoryName(baseEntry.Path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                foreach (var sibling in Directory.EnumerateFiles(directory, "*.apk", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(sibling, baseEntry.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var inspected = valid.FirstOrDefault(entry =>
                        string.Equals(entry.Path, sibling, StringComparison.OrdinalIgnoreCase));
                    ApkInfo? info = inspected.Info;
                    if (info is null)
                    {
                        var result = await _inspector.InspectAsync(sibling, cancellationToken).ConfigureAwait(false);
                        info = result.Info;
                    }

                    if (info is { IsSplit: true } &&
                        string.Equals(info.PackageName, baseEntry.Info.PackageName, StringComparison.Ordinal))
                    {
                        files.Add(sibling);
                    }
                }
            }
        }

        return new ArtifactResolution
        {
            Glob = glob,
            BaseApk = baseEntry.Path,
            Files = files,
            Info = baseEntry.Info,
            Candidates = matches.ToList(),
        };
    }

    /// <summary>Finds APK files that match a glob. Exposed for tests.</summary>
    public static IReadOnlyList<string> FindMatches(
        string projectRoot,
        string glob,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(glob);
        var normalizedGlob = glob.Replace('\\', '/');
        var rooted = Path.IsPathRooted(glob);
        var absolute = rooted
            ? Path.GetFullPath(glob)
            : Path.GetFullPath(Path.Combine(projectRoot, glob));
        if (File.Exists(absolute))
        {
            return [absolute];
        }

        if (Directory.Exists(absolute))
        {
            return Directory
                .EnumerateFiles(absolute, "*.apk", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var searchRoot = DetermineSearchRoot(projectRoot, glob);
        if (!Directory.Exists(searchRoot))
        {
            return [];
        }

        var pattern = GlobToRegex(normalizedGlob);
        var matches = new List<string>();
        var visited = 0;
        foreach (var file in Directory.EnumerateFiles(searchRoot, "*.apk", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++visited > MaxCandidateFiles)
            {
                break;
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
        var pattern = glob;
        var builder = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
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

    private static string? ValidatePackage(RunConfiguration configuration, ApkInfo info)
    {
        var expected = configuration.Artifact.PackageName;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return null;
        }

        return string.Equals(expected, info.PackageName, StringComparison.Ordinal)
            ? null
            : $"The APK package is {info.PackageName}, but the configuration expects {expected}.";
    }
}

using DroidDeck.Core.Projects;
using DroidDeck.Projects.Detection;

namespace DroidDeck.Projects.Tests.Detection;

public sealed class ProjectScannerTests : IDisposable
{
    private readonly string _root;

    public ProjectScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "droiddeck-scan-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Scan_LargeMonorepo_CompletesWithinTwoSecondBudget()
    {
        // A synthetic monorepo: 40 modules, each with 8 subfolders, every module
        // carrying a detection marker. 360 folders must be visited in total.
        for (var module = 0; module < 40; module++)
        {
            var modulePath = Path.Combine(_root, $"module{module:00}");
            Directory.CreateDirectory(modulePath);
            File.WriteAllText(Path.Combine(modulePath, "droiddeck.marker"), string.Empty);
            for (var nested = 0; nested < 8; nested++)
            {
                Directory.CreateDirectory(Path.Combine(modulePath, $"nested{nested}"));
            }
        }

        var scanner = new ProjectScanner([new MarkerDetector()]);

        var result = await scanner.ScanAsync(new ProjectScanContext(
            _root,
            new ProjectScanOptions { MaxDepth = 2, Timeout = TimeSpan.FromSeconds(2) }));

        Assert.False(result.WasTruncated);
        Assert.Equal(40, result.Candidates.Count);
        Assert.True(
            result.Duration <= TimeSpan.FromSeconds(2),
            $"The scan took {result.Duration.TotalMilliseconds:F0} ms, over the 2 s budget.");
    }

    [Fact]
    public async Task Scan_HonoursIgnoreFoldersAndFindsNestedProjects()
    {
        var scanner = new ProjectScanner([new MarkerDetector()]);
        Directory.CreateDirectory(Path.Combine(_root, "app"));
        File.WriteAllText(Path.Combine(_root, "app", "droiddeck.marker"), string.Empty);
        Directory.CreateDirectory(Path.Combine(_root, "feature", "cards"));
        File.WriteAllText(Path.Combine(_root, "feature", "cards", "droiddeck.marker"), string.Empty);
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "vendor"));
        File.WriteAllText(Path.Combine(_root, "node_modules", "vendor", "droiddeck.marker"), string.Empty);

        var result = await scanner.ScanAsync(new ProjectScanContext(
            _root,
            new ProjectScanOptions { MaxDepth = 4, IncludeRoot = false }));

        var roots = result.Candidates.Select(candidate => candidate.RootPath).ToList();
        Assert.Equal(2, roots.Count);
        Assert.Contains(Path.Combine(_root, "app"), roots);
        Assert.Contains(Path.Combine(_root, "feature", "cards"), roots);
        Assert.DoesNotContain(roots, root => root.Contains("node_modules", StringComparison.Ordinal));
    }

    /// <summary>Detects any folder holding a <c>droiddeck.marker</c> file.</summary>
    private sealed class MarkerDetector : IProjectDetector
    {
        public string Id => "marker";

        public int Priority => 0;

        public Task<ProjectDetectionResult?> DetectAsync(
            ProjectScanContext context,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(Path.Combine(context.RootPath, "droiddeck.marker")))
            {
                return Task.FromResult<ProjectDetectionResult?>(null);
            }

            return Task.FromResult<ProjectDetectionResult?>(new ProjectDetectionResult
            {
                Project = new ProjectModel
                {
                    Id = context.RootPath,
                    Name = new DirectoryInfo(context.RootPath).Name,
                    RootPath = context.RootPath,
                    Kind = ProjectKind.Custom,
                    Confidence = DetectionConfidence.High,
                },
            });
        }
    }
}

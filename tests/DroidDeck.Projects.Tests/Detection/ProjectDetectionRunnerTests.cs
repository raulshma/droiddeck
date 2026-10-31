using DroidDeck.Core.Projects;
using DroidDeck.Projects.Detection;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Detection;

public sealed class ProjectDetectionRunnerTests
{
    [Fact]
    public async Task DetectAsync_RunsDetectorsInPriorityOrderAndStopsAtTheFirstResult()
    {
        using var temp = new TempDirectory();
        var calls = new List<string>();
        var runner = new ProjectDetectionRunner(
        [
            Match("late", 30, calls, ProjectKind.Unity, "late"),
            Skip("early", 5, calls),
            Match("middle", 20, calls, ProjectKind.Flutter, "middle"),
            Match("never", 40, calls, ProjectKind.Maui, "never"),
        ]);

        var result = await runner.DetectAsync(Context(temp));

        Assert.NotNull(result);
        Assert.Equal("middle", result.Project.Name);
        Assert.Equal(["early", "middle"], calls);
    }

    [Fact]
    public async Task DetectAsync_WithoutAMatch_ReturnsNullAndDoesNotInvokeTheFallback()
    {
        using var temp = new TempDirectory();
        var calls = new List<string>();
        var fallbackCalls = new List<string>();
        var runner = new ProjectDetectionRunner(
            [Skip("first", 10, calls)],
            Match("custom", 1_000, fallbackCalls, ProjectKind.Custom, "fallback"));

        var result = await runner.DetectAsync(Context(temp));

        Assert.Null(result);
        Assert.Empty(fallbackCalls);
    }

    [Fact]
    public async Task DetectProjectAsync_WithoutAMatch_UsesTheCustomFallback()
    {
        using var temp = new TempDirectory();
        var calls = new List<string>();
        var fallbackCalls = new List<string>();
        var runner = new ProjectDetectionRunner(
            [Skip("first", 10, calls)],
            Match("custom", 1_000, fallbackCalls, ProjectKind.Custom, "fallback"));

        var project = await runner.DetectProjectAsync(Context(temp), kind: null);

        Assert.Equal("fallback", project.Name);
        Assert.Equal(ProjectKind.Custom, project.Kind);
        Assert.Equal(["custom"], fallbackCalls);
    }

    [Fact]
    public async Task DetectProjectAsync_WithADifferentExplicitKind_RelabelsTheDetectedProject()
    {
        using var temp = new TempDirectory();
        var calls = new List<string>();
        var runner = new ProjectDetectionRunner(
            [Match("gradle", 10, calls, ProjectKind.AndroidGradle, "detected")]);

        var project = await runner.DetectProjectAsync(Context(temp), ProjectKind.Flutter);

        Assert.Equal("detected", project.Name);
        Assert.Equal(ProjectKind.Flutter, project.Kind);
        Assert.Equal("Flutter", project.KindLabel);
    }

    [Fact]
    public async Task DetectProjectAsync_WithoutAnExplicitKind_KeepsTheDetectedKind()
    {
        using var temp = new TempDirectory();
        var calls = new List<string>();
        var runner = new ProjectDetectionRunner(
            [Match("gradle", 10, calls, ProjectKind.AndroidGradle, "detected")]);

        var project = await runner.DetectProjectAsync(Context(temp), kind: null);

        Assert.Equal(ProjectKind.AndroidGradle, project.Kind);
        Assert.Equal("Android Gradle", project.KindLabel);
    }

    [Fact]
    public async Task DetectProjectAsync_WithTheCustomKind_RunsOnlyTheFallback()
    {
        using var temp = new TempDirectory();
        var calls = new List<string>();
        var fallbackCalls = new List<string>();
        var runner = new ProjectDetectionRunner(
            [Match("gradle", 10, calls, ProjectKind.AndroidGradle, "detected")],
            Match("custom", 1_000, fallbackCalls, ProjectKind.Custom, "fallback"));

        var project = await runner.DetectProjectAsync(Context(temp), ProjectKind.Custom);

        Assert.Equal("fallback", project.Name);
        Assert.Empty(calls);
        Assert.Equal(["custom"], fallbackCalls);
    }

    [Fact]
    public async Task DetectProjectAsync_WithAnExplicitKindAndNoMatch_CreatesAMinimalProjectOfThatKind()
    {
        using var temp = new TempDirectory();
        var context = Context(temp);
        var runner = new ProjectDetectionRunner([]);

        var project = await runner.DetectProjectAsync(context, ProjectKind.Maui);

        Assert.Equal(ProjectKind.Maui, project.Kind);
        Assert.Equal(".NET MAUI", project.KindLabel);
        Assert.Equal(context.RootPath, project.RootPath);
        Assert.Equal(ProjectModel.CreateId(context.RootPath), project.Id);
        Assert.Equal(new DirectoryInfo(context.RootPath).Name, project.Name);
    }

    [Fact]
    public async Task DetectProjectAsync_WithTheCustomKindAndNoFallback_CreatesAMinimalCustomProject()
    {
        using var temp = new TempDirectory();
        var context = Context(temp);
        var runner = new ProjectDetectionRunner([]);

        var project = await runner.DetectProjectAsync(context, ProjectKind.Custom);

        Assert.Equal(ProjectKind.Custom, project.Kind);
        Assert.Equal("Custom", project.KindLabel);
        Assert.Equal(context.RootPath, project.RootPath);
    }

    [Fact]
    public async Task DetectProjectAsync_DoesNotRelabelTheFallbackProject()
    {
        using var temp = new TempDirectory();
        var calls = new List<string>();
        var fallbackCalls = new List<string>();
        var runner = new ProjectDetectionRunner(
            [Skip("first", 10, calls)],
            Match("custom", 1_000, fallbackCalls, ProjectKind.Custom, "fallback"));

        var project = await runner.DetectProjectAsync(Context(temp), ProjectKind.Flutter);

        Assert.Equal(ProjectKind.Custom, project.Kind);
        Assert.Equal("Custom", project.KindLabel);
    }

    [Fact]
    public async Task DetectAsync_WithACancelledToken_ThrowsBeforeRunningADetector()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var calls = new List<string>();
        var runner = new ProjectDetectionRunner(
            [Match("first", 10, calls, ProjectKind.Flutter, "first")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.DetectAsync(Context(temp), cancellation.Token));

        Assert.Empty(calls);
    }

    [Fact]
    public async Task DetectAsync_StopsWhenADetectorRequestsCancellation()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var calls = new List<string>();
        var cancelling = new RecordingDetector("cancelling", 10, calls, (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult<ProjectDetectionResult?>(null);
        });
        var runner = new ProjectDetectionRunner(
            [cancelling, Match("second", 20, calls, ProjectKind.Flutter, "second")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.DetectAsync(Context(temp), cancellation.Token));

        Assert.Equal(["cancelling"], calls);
    }

    [Fact]
    public async Task DetectAsync_PassesTheCancellationTokenToDetectors()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        CancellationToken observed = default;
        var calls = new List<string>();
        var detector = new RecordingDetector("capture", 10, calls, (_, token) =>
        {
            observed = token;
            return Task.FromResult<ProjectDetectionResult?>(null);
        });
        var runner = new ProjectDetectionRunner([detector]);

        await runner.DetectAsync(Context(temp), cancellation.Token);

        Assert.Equal(cancellation.Token, observed);
    }

    private static ProjectScanContext Context(TempDirectory temp) => new(temp.Path, ProjectScanOptions.Default);

    private static RecordingDetector Match(
        string id,
        int priority,
        List<string> calls,
        ProjectKind kind,
        string name) =>
        new(id, priority, calls, (_, _) => Task.FromResult<ProjectDetectionResult?>(new ProjectDetectionResult
        {
            Project = new ProjectModel
            {
                Name = name,
                Kind = kind,
                KindLabel = ProjectModel.GetKindLabel(kind),
            },
        }));

    private static RecordingDetector Skip(string id, int priority, List<string> calls) =>
        new(id, priority, calls, (_, _) => Task.FromResult<ProjectDetectionResult?>(null));

    private sealed class RecordingDetector : IProjectDetector
    {
        private readonly List<string> _calls;
        private readonly Func<ProjectScanContext, CancellationToken, Task<ProjectDetectionResult?>> _detect;

        public RecordingDetector(
            string id,
            int priority,
            List<string> calls,
            Func<ProjectScanContext, CancellationToken, Task<ProjectDetectionResult?>> detect)
        {
            Id = id;
            Priority = priority;
            _calls = calls;
            _detect = detect;
        }

        public string Id { get; }

        public int Priority { get; }

        public Task<ProjectDetectionResult?> DetectAsync(
            ProjectScanContext context,
            CancellationToken cancellationToken = default)
        {
            _calls.Add(Id);
            return _detect(context, cancellationToken);
        }
    }
}

using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Projects.Artifacts;
using DroidDeck.Projects.Tests.Fakes;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Resolution;

public sealed class GlobArtifactResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReturnsSingleBaseApk()
    {
        using var temp = new TempDirectory();
        var apk = temp.WriteFile("out/app-debug.apk", "apk");
        var inspector = new FakeApkInspector();
        inspector.Add(apk, BaseApk("com.example"));
        var resolver = new GlobArtifactResolver(inspector);

        var result = await resolver.ResolveAsync(
            Project(temp),
            Configuration("out/*.apk"));

        Assert.True(result.Succeeded);
        Assert.Equal(Path.GetFullPath(apk), result.BaseApk);
        Assert.Single(result.Files);
        Assert.Equal("com.example", result.Info?.PackageName);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ResolveAsync_IncludesMatchingSplitApks()
    {
        using var temp = new TempDirectory();
        var baseApk = temp.WriteFile("out/app-debug.apk", "apk");
        var abiSplit = temp.WriteFile("out/app-debug-config.arm64.apk", "apk");
        var densitySplit = temp.WriteFile("out/app-debug-config.xxhdpi.apk", "apk");
        var inspector = new FakeApkInspector();
        inspector.Add(baseApk, BaseApk("com.example"));
        inspector.Add(abiSplit, SplitApk("com.example", "config.arm64"));
        inspector.Add(densitySplit, SplitApk("com.example", "config.xxhdpi"));
        var resolver = new GlobArtifactResolver(inspector);

        var result = await resolver.ResolveAsync(
            Project(temp),
            Configuration("out/*.apk", includeSplits: true));

        Assert.True(result.Succeeded);
        Assert.True(result.HasSplits);
        Assert.Equal(3, result.Files.Count);
        Assert.Equal(Path.GetFullPath(baseApk), result.Files[0]);
    }

    [Fact]
    public async Task ResolveAsync_ExcludesSplitsWhenDisabled()
    {
        using var temp = new TempDirectory();
        var baseApk = temp.WriteFile("out/app-debug.apk", "apk");
        var abiSplit = temp.WriteFile("out/app-debug-config.arm64.apk", "apk");
        var inspector = new FakeApkInspector();
        inspector.Add(baseApk, BaseApk("com.example"));
        inspector.Add(abiSplit, SplitApk("com.example", "config.arm64"));
        var resolver = new GlobArtifactResolver(inspector);

        var result = await resolver.ResolveAsync(
            Project(temp),
            Configuration("out/*.apk", includeSplits: false));

        Assert.True(result.Succeeded);
        Assert.False(result.HasSplits);
        Assert.Single(result.Files);
    }

    [Fact]
    public async Task ResolveAsync_ReportsAmbiguousBaseApks()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteFile("a/app-debug.apk", "apk");
        var second = temp.WriteFile("b/app-debug.apk", "apk");
        var inspector = new FakeApkInspector();
        inspector.Add(first, BaseApk("com.example.a"));
        inspector.Add(second, BaseApk("com.example.b"));
        var resolver = new GlobArtifactResolver(inspector);

        var result = await resolver.ResolveAsync(Project(temp), Configuration("**/*.apk"));

        Assert.False(result.Succeeded);
        Assert.Contains("ambiguous", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task ResolveAsync_ReportsPackageMismatch()
    {
        using var temp = new TempDirectory();
        var apk = temp.WriteFile("out/app-debug.apk", "apk");
        var inspector = new FakeApkInspector();
        inspector.Add(apk, BaseApk("com.actual"));
        var resolver = new GlobArtifactResolver(inspector);
        var configuration = Configuration("out/*.apk");
        configuration.Artifact.PackageName = "com.expected";

        var result = await resolver.ResolveAsync(Project(temp), configuration);

        Assert.False(result.Succeeded);
        Assert.Contains("com.actual", result.Error);
        Assert.Contains("com.expected", result.Error);
    }

    [Fact]
    public async Task ResolveAsync_ReportsMissingArtifact()
    {
        using var temp = new TempDirectory();
        var resolver = new GlobArtifactResolver(new FakeApkInspector());

        var result = await resolver.ResolveAsync(Project(temp), Configuration("out/*.apk"));

        Assert.False(result.Succeeded);
        Assert.Contains("No APK matched", result.Error);
    }

    [Fact]
    public void FindMatches_SupportsRecursiveGlobs()
    {
        using var temp = new TempDirectory();
        var expected = temp.WriteFile("app/build/outputs/apk/debug/app-debug.apk", "apk");
        temp.WriteFile("app/build/outputs/apk/release/app-release.apk", "apk");

        var matches = GlobArtifactResolver.FindMatches(temp.Path, "**/build/outputs/apk/debug/*.apk");

        Assert.Single(matches);
        Assert.Equal(Path.GetFullPath(expected), matches[0]);
    }

    private static ProjectModel Project(TempDirectory temp) => new()
    {
        Id = "project",
        Name = "Sample",
        RootPath = temp.Path,
        Kind = ProjectKind.AndroidGradle,
    };

    private static RunConfiguration Configuration(string glob, bool includeSplits = true) => new()
    {
        Id = "debug",
        Name = "Debug",
        Kind = RunConfigurationKind.Gradle,
        Artifact = new ArtifactConfiguration
        {
            Glob = glob,
            IncludeSplits = includeSplits,
        },
    };

    private static ApkInfo BaseApk(string packageName) => new()
    {
        PackageName = packageName,
        VersionName = "1.0",
        IsSplit = false,
    };

    private static ApkInfo SplitApk(string packageName, string splitName) => new()
    {
        PackageName = packageName,
        SplitName = splitName,
        IsSplit = true,
    };
}

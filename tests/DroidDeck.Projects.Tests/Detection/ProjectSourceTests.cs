using System.Text.RegularExpressions;
using DroidDeck.Projects.Detection;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Detection;

public sealed partial class ProjectSourceTests
{
    [Fact]
    public void MatchValue_ReturnsNullForMissingTextAndMatches()
    {
        Assert.Null(ProjectSource.MatchValue(AppIdPattern(), null));
        Assert.Null(ProjectSource.MatchValue(AppIdPattern(), string.Empty));
        Assert.Null(ProjectSource.MatchValue(AppIdPattern(), "no identifier here"));
    }

    [Fact]
    public void MatchValue_TrimsTheValueGroupByDefault()
    {
        Assert.Equal(
            "com.example.app",
            ProjectSource.MatchValue(AppIdPattern(), "appId = 'com.example.app'"));
    }

    [Fact]
    public void MatchValue_CanPreserveTheRawGroupForDetectorsThatNeverTrimmed()
    {
        Assert.Equal(
            "com.example.app ",
            ProjectSource.MatchValue(AppIdPattern(), "appId = 'com.example.app '", trim: false));
    }

    [Fact]
    public void FindFirst_ReturnsTheFirstExistingNameInPreferenceOrder()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("second.txt", string.Empty);
        temp.WriteFile("first.txt", string.Empty);

        Assert.Equal(
            temp.Combine("first.txt"),
            ProjectSource.FindFirst(temp.Path, "missing.txt", "first.txt", "second.txt"));
        Assert.Null(ProjectSource.FindFirst(temp.Combine("missing"), "first.txt"));
    }

    [Fact]
    public void FindBuildFile_PrefersTheKotlinDslBuildScript()
    {
        using var temp = new TempDirectory();
        temp.WriteFile("build.gradle", string.Empty);
        temp.WriteFile("build.gradle.kts", string.Empty);

        Assert.Equal(temp.Combine("build.gradle.kts"), ProjectSource.FindBuildFile(temp.Path));
    }

    [Fact]
    public void FindBuildFile_FallsBackToTheGroovyBuildScript()
    {
        using var temp = new TempDirectory();
        var buildFile = temp.WriteFile("build.gradle", string.Empty);

        Assert.Equal(buildFile, ProjectSource.FindBuildFile(temp.Path));
    }

    [Fact]
    public async Task ReadTextAsync_ReturnsNullForAMissingFile()
    {
        using var temp = new TempDirectory();

        Assert.Null(await ProjectSource.ReadTextAsync(temp.Combine("missing.txt"), CancellationToken.None));
        Assert.Null(await ProjectSource.ReadTextAsync(null, CancellationToken.None));
    }

    [Theory]
    [InlineData("applicationId = \"com.example.app\"")]
    [InlineData("applicationId 'com.example.app'")]
    [InlineData("applicationId 'com.example.app' ")]
    public void ApplicationIdPattern_MatchesGradleAndKotlinDslShapes(string text)
    {
        Assert.Equal("com.example.app", ProjectSource.MatchValue(ProjectSource.ApplicationIdPattern(), text));
    }

    [Theory]
    [InlineData("namespace = \"com.example.app\"")]
    [InlineData("namespace 'com.example.app'")]
    public void NamespacePattern_MatchesGradleAndKotlinDslShapes(string text)
    {
        Assert.Equal("com.example.app", ProjectSource.MatchValue(ProjectSource.NamespacePattern(), text));
    }

    [GeneratedRegex(
        @"appId\s*=\s*[""'](?<value>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex AppIdPattern();
}

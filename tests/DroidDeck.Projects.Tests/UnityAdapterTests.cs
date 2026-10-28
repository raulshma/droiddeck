using DroidDeck.Core.Projects;
using DroidDeck.Projects.Adapters;

namespace DroidDeck.Projects.Tests;

public sealed class UnityAdapterTests
{
    [Fact]
    public void GetArtifactGlob_UsesTheStandardBuildFolder()
    {
        Assert.Equal("Builds/**/*.apk", UnityAdapter.GetArtifactGlob(null));
        Assert.Equal("Builds/Android/*.apk", UnityAdapter.GetArtifactGlob("Builds/Android"));
    }

    [Fact]
    public void CreateDefaultRunConfigurations_InstallsTheBuiltApk()
    {
        var project = new ProjectModel
        {
            ApplicationId = "com.example.game",
            LauncherActivity = "com.unity3d.player.UnityPlayerActivity",
        };

        var configuration = Assert.Single(UnityAdapter.CreateDefaultRunConfigurations(project));

        Assert.Equal(RunConfigurationKind.Apk, configuration.Kind);
        Assert.Equal("Install built APK", configuration.Name);
        Assert.True(configuration.IsDefault);
        Assert.True(configuration.Build.Skip);
        Assert.Equal("Builds/**/*.apk", configuration.Artifact.Glob);
        Assert.Equal("com.example.game", configuration.Artifact.PackageName);
        Assert.Equal("com.example.game", configuration.Launch.PackageName);
        Assert.Equal("com.unity3d.player.UnityPlayerActivity", configuration.Launch.Activity);
    }

    [Fact]
    public void BuildTemplateSample_MatchesTheEmbeddedTemplate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DroidDeck.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var samplePath = Path.Combine(directory.FullName, "samples", "unity", "DroidDeckAndroidBuild.cs");
        Assert.True(File.Exists(samplePath), $"The sample Unity build script is missing: {samplePath}");

        var expected = UnityAdapter.BuildTemplate.ReplaceLineEndings("\n").TrimEnd('\n');
        var actual = File.ReadAllText(samplePath).ReplaceLineEndings("\n").TrimEnd('\n');
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildTemplate_ProvidesTheBatchModeEntryPoint()
    {
        Assert.Contains("namespace DroidDeck.Unity", UnityAdapter.BuildTemplate, StringComparison.Ordinal);
        Assert.Contains("public static void Build()", UnityAdapter.BuildTemplate, StringComparison.Ordinal);
        Assert.Contains("BuildPipeline.BuildPlayer", UnityAdapter.BuildTemplate, StringComparison.Ordinal);
        Assert.Contains("BuildTarget.Android", UnityAdapter.BuildTemplate, StringComparison.Ordinal);
        Assert.Contains("EditorApplication.Exit(1)", UnityAdapter.BuildTemplate, StringComparison.Ordinal);
        Assert.Contains("Builds/Android", UnityAdapter.BuildTemplate, StringComparison.Ordinal);
        Assert.Equal("DroidDeck.Unity.AndroidBuild.Build", UnityAdapter.BuildMethod);
        Assert.Equal("Assets/Editor/DroidDeckAndroidBuild.cs", UnityAdapter.BuildTemplatePath);
    }
}

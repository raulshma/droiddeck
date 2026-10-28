using DroidDeck.Core.Projects;
using DroidDeck.Projects.Detection;
using DroidDeck.TestSupport;

namespace DroidDeck.Projects.Tests.Detection;

public sealed class UnityProjectDetectorTests
{
    [Fact]
    public async Task DetectAsync_DetectsUnityProjectsWithTheBuiltApk()
    {
        using var temp = new TempDirectory();
        WriteUnityProject(temp);
        temp.WriteFile("Builds/Android/Game.apk", string.Empty);
        var detector = new UnityProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        var project = result.Project;
        Assert.Equal(ProjectKind.Unity, project.Kind);
        Assert.Equal("Unity (custom support)", project.KindLabel);
        Assert.Equal("My Game", project.Name);
        Assert.Equal("com.example.game", project.ApplicationId);
        Assert.NotNull(project.UnityInfo);
        Assert.Equal("2022.3.10f1", project.UnityInfo.EditorVersion);
        Assert.Equal("My Game", project.UnityInfo.ProductName);
        Assert.Equal("Example Studio", project.UnityInfo.CompanyName);
        Assert.Contains(project.Evidence, evidence => evidence.Id == "unity-project");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "unity-editor-version");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "unity-product-name");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "unity-application-id");
        Assert.Contains(project.Evidence, evidence => evidence.Id == "unity-apk");
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Install));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Launch));
        Assert.True(project.Capabilities.HasFlag(ProjectCapability.Limited));
        Assert.False(project.Capabilities.HasFlag(ProjectCapability.Build));
        Assert.False(string.IsNullOrWhiteSpace(project.Limitation));
        Assert.Contains("Unity", project.Limitation, StringComparison.Ordinal);
        Assert.Contains("Assets/Editor/DroidDeckAndroidBuild.cs", project.Limitation, StringComparison.Ordinal);
        Assert.Contains("never", project.Limitation, StringComparison.OrdinalIgnoreCase);
        var configuration = Assert.Single(project.RunConfigurations);
        Assert.Equal(RunConfigurationKind.Apk, configuration.Kind);
        Assert.Equal("Builds/Android/*.apk", configuration.Artifact.Glob);
        Assert.Equal("com.example.game", configuration.Launch.PackageName);
    }

    [Fact]
    public async Task DetectAsync_WarnsWhenNoBuiltApkExists()
    {
        using var temp = new TempDirectory();
        WriteUnityProject(temp);
        var detector = new UnityProjectDetector();

        var result = await detector.DetectAsync(Context(temp));

        Assert.NotNull(result);
        Assert.Equal("Builds/**/*.apk", result.Project.RunConfigurations[0].Artifact.Glob);
        Assert.Contains(
            result.Project.Warnings,
            warning => warning.Contains("Builds", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullWithoutUnityMarkers()
    {
        using var temp = new TempDirectory();
        temp.CreateDirectory("Assets");
        var detector = new UnityProjectDetector();

        Assert.Null(await detector.DetectAsync(Context(temp)));
    }

    [Fact]
    public async Task DetectAsync_ReturnsNullForAnEmptyFolder()
    {
        using var temp = new TempDirectory();
        var detector = new UnityProjectDetector();

        Assert.Null(await detector.DetectAsync(Context(temp)));
    }

    private static ProjectScanContext Context(TempDirectory temp) => new(temp.Path, ProjectScanOptions.Default);

    private static void WriteUnityProject(TempDirectory temp)
    {
        temp.CreateDirectory("Assets");
        temp.WriteFile(
            "ProjectSettings/ProjectVersion.txt",
            """
            m_EditorVersion: 2022.3.10f1
            m_EditorVersionWithRevision: 2022.3.10f1 (ff3792e53c62)
            """);
        temp.WriteFile(
            "ProjectSettings/ProjectSettings.asset",
            """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!129 &1
            PlayerSettings:
              m_ObjectHideFlags: 0
              companyName: Example Studio
              productName: My Game
              applicationIdentifier:
                Android: com.example.game
                Standalone: com.example.game
            """);
    }
}

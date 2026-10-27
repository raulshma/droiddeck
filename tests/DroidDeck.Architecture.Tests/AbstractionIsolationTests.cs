namespace DroidDeck.Architecture.Tests;

public sealed class AbstractionIsolationTests
{
    [Fact]
    public void PluginContractsTargetPlainDotNetWithoutUiDependencies()
    {
        var project = RepositoryLayout.LoadProject("DroidDeck.Plugin.Abstractions");
        var targetFramework = project.Descendants().Single(element => element.Name.LocalName == "TargetFramework");
        var packageReferences = project
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .ToArray();

        Assert.Equal("net10.0", targetFramework.Value);
        Assert.DoesNotContain(
            project.Descendants(),
            element => element.Name.LocalName is "UseWinUI" or "FrameworkReference");
        Assert.DoesNotContain("Microsoft.WindowsAppSDK", packageReferences, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyUiProjectsEnableWinUi()
    {
        var winUiProjects = Directory
            .EnumerateFiles(Path.Combine(RepositoryLayout.Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("<UseWinUI>true</UseWinUI>", StringComparison.Ordinal))
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["DroidDeck.App", "DroidDeck.Plugin.UI"], winUiProjects);
    }
}

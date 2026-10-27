using System.Xml.Linq;

namespace DroidDeck.Architecture.Tests;

internal static class RepositoryLayout
{
    public static string Root { get; } = FindRoot();

    public static Dictionary<string, string[]> LoadSourceProjectGraph()
    {
        var projects = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var sourceDirectory = Path.Combine(Root, "src");
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*.csproj", SearchOption.AllDirectories))
        {
            projects[Path.GetFileNameWithoutExtension(file)] = ReadProjectReferences(file);
        }

        return projects;
    }

    public static XDocument LoadProject(string projectName) =>
        XDocument.Load(Path.Combine(Root, "src", projectName, projectName + ".csproj"));

    public static string[] ReadProjectReferences(string projectFile) =>
        XDocument.Load(projectFile)
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .ToArray();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DroidDeck.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root could not be located from the test output directory.");
    }
}

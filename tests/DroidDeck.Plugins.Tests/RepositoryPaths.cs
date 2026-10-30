namespace DroidDeck.Plugins.Tests;

internal static class RepositoryPaths
{
    public static string FindRepositoryRoot()
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

        throw new InvalidOperationException("Could not find the repository root above the test output.");
    }
}

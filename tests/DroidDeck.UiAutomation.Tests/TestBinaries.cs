namespace DroidDeck.UiAutomation.Tests;

internal static class TestBinaries
{
#if DEBUG
    private const string Configuration = "Debug";
#else
    private const string Configuration = "Release";
#endif

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string FakeAdbExecutable => Find(
        Path.Combine(RepositoryRoot, "tests", "DroidDeck.Testing.FakeAdb", "bin"),
        "FakeAdb.exe");

    public static string AppExecutable => Find(
        Path.Combine(RepositoryRoot, "src", "DroidDeck.App", "bin"),
        "DroidDeck.exe");

    private static string Find(string root, string fileName)
    {
        if (!Directory.Exists(root))
        {
            throw new FileNotFoundException(
                $"{fileName} was not found under '{root}'. Build the solution in {Configuration} first.");
        }

        var candidates = Directory
            .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new FileNotFoundException(
                $"{fileName} was not found under '{root}'. Build the solution in {Configuration} first.");
        }

        var configurationSegment = Path.DirectorySeparatorChar + Configuration + Path.DirectorySeparatorChar;
        return candidates
            .OrderByDescending(path =>
                path.Contains(configurationSegment, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    private static string FindRepositoryRoot()
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

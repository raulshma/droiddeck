namespace DroidDeck.TestSupport;

/// <summary>A unique temporary directory removed on disposal.</summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "droiddeck-tests")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] segments) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine([Path, .. segments]));

    public string CreateDirectory(params string[] segments)
    {
        var path = Combine(segments);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteFile(string relativePath, string contents)
    {
        var path = Combine(relativePath);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup; the OS temp cleaner handles leftovers.
        }
    }
}

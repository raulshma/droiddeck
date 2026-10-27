using DroidDeck.Core.Processes;

namespace DroidDeck.TestSupport;

/// <summary>Locates and invokes the process helper used by process-runner tests.</summary>
public static class TestProcessHelper
{
    public const string AssemblyFileName = "DroidDeck.Testing.ProcessHelper.dll";

    public static string AssemblyPath { get; } = Path.Combine(AppContext.BaseDirectory, AssemblyFileName);

    public static bool IsAvailable => File.Exists(AssemblyPath);

    public static ProcessRequest Create(params string[] arguments) => new()
    {
        FileName = "dotnet",
        Arguments = [AssemblyPath, .. arguments],
        MaxOutputCharacters = 1024 * 1024,
    };
}

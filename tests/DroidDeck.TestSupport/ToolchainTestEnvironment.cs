using DroidDeck.Core.Toolchain;

namespace DroidDeck.TestSupport;

/// <summary>Builds deterministic toolchain environments and fake tool scripts.</summary>
public static class ToolchainTestEnvironment
{
    public static ToolchainEnvironment Create(
        TempDirectory sandbox,
        IReadOnlyList<string>? pathDirectories = null,
        IReadOnlyDictionary<string, string>? variables = null)
    {
        return new ToolchainEnvironment
        {
            PathDirectories = pathDirectories ?? [],
            GetVariable = name => variables is not null && variables.TryGetValue(name, out var value) ? value : null,
            LocalApplicationData = sandbox.Combine("localAppData"),
            ProgramFiles = sandbox.Combine("programFiles"),
            UserProfile = sandbox.Combine("userProfile"),
        };
    }

    public static string WriteScript(
        TempDirectory directory,
        string relativePath,
        string output,
        int exitCode = 0)
    {
        var path = directory.Combine(relativePath);
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var body = string.Join(
            "\r\n",
            output.Split('\n').Select(line => "echo " + line.TrimEnd('\r')));
        var script = $"@echo off\r\n{body}\r\nexit /b {exitCode}\r\n";
        File.WriteAllText(path, script);
        return path;
    }

    public static string WriteScript(
        TempDirectory directory,
        string relativePath,
        IReadOnlyList<string> lines,
        int exitCode = 0)
    {
        return WriteScript(directory, relativePath, string.Join("\r\n", lines), exitCode);
    }
}

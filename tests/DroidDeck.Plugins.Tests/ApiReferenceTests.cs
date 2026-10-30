using System.Reflection;
using System.Text.RegularExpressions;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.UI.Kit;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Keeps the SDK documentation honest: every public contract must appear in the
/// API reference, and every relative link in the SDK docs must resolve to a file.
/// </summary>
public sealed partial class ApiReferenceTests
{
    private static readonly Assembly[] SdkAssemblies =
    [
        typeof(IPlugin).Assembly,
        typeof(PluginCard).Assembly,
    ];

    [Fact]
    public void ApiReferenceDocumentsEveryPublicInterfaceAndEnum()
    {
        var referencePath = Path.Combine(
            RepositoryPaths.FindRepositoryRoot(),
            "docs",
            "plugin-sdk",
            "api-reference.md");
        Assert.True(File.Exists(referencePath), $"'{referencePath}' is missing.");
        var reference = File.ReadAllText(referencePath);

        var missing = new List<string>();
        foreach (var assembly in SdkAssemblies)
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (!type.IsInterface && !type.IsEnum)
                {
                    continue;
                }

                if (!reference.Contains($"`{type.Name}`", StringComparison.Ordinal))
                {
                    missing.Add($"{type.Namespace}.{type.Name}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "The API reference does not document: " + string.Join(", ", missing));
    }

    [Fact]
    public void PluginSdkDocsRelativeLinksResolve()
    {
        var docsRoot = Path.Combine(RepositoryPaths.FindRepositoryRoot(), "docs");
        var files = Directory
            .GetFiles(Path.Combine(docsRoot, "plugin-sdk"), "*.md")
            .Append(Path.Combine(docsRoot, "index.md"))
            .Append(Path.Combine(docsRoot, "catalog.md"))
            .ToArray();

        var missing = new List<string>();
        foreach (var file in files)
        {
            foreach (Match match in MarkdownLink().Matches(File.ReadAllText(file)))
            {
                var target = match.Groups[1].Value;
                if (target.StartsWith('#') ||
                    target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var path = target.Split('#')[0];
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, path));
                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    missing.Add($"{Path.GetFileName(file)} -> {target}");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "The SDK documentation has unresolved links: " + string.Join(", ", missing));
    }

    [GeneratedRegex(@"\[[^\]]+\]\(([^)]+)\)")]
    private static partial Regex MarkdownLink();
}

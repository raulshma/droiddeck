using System.Text.Json;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginDependencyTests
{
    private const string DependencyId = "com.example.dependency";
    private const string DependentId = "com.example.dependent";

    [Fact]
    public async Task MissingDependencyIsRejectedWithAClearReason()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(
            temp,
            CreateManifest(DependentId, dependencies: [new PluginDependency { Id = DependencyId, MinVersion = "1.0.0" }]));
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var result = await loader.LoadAsync(directory);

        Assert.False(result.Succeeded);
        var issue = Assert.Single(
            result.Validation.Issues,
            candidate => candidate.Code == "plugin.dependencies.unresolved");
        Assert.Contains(DependencyId, issue.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", issue.Message, StringComparison.Ordinal);
        Assert.Empty(loader.Plugins);
        Assert.Empty(registry.Commands);
    }

    [Fact]
    public async Task LoadInstalledResolvesDependenciesBeforeDependents()
    {
        using var temp = new TempDirectory();
        // The dependent sorts before its dependency alphabetically, so loading in
        // directory order would fail; the host must resolve the graph instead.
        StagePlugin(temp, CreateManifest(DependentId), directoryName: "aaa-dependent");
        StagePlugin(temp, CreateManifest(DependencyId), directoryName: "zzz-dependency");
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var results = await loader.LoadInstalledAsync(temp.Combine("plugins"));

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Succeeded, Describe(result.Validation)));
        Assert.Equal(2, loader.Plugins.Count);
        Assert.Equal(2, registry.Commands.Count);
    }

    [Fact]
    public async Task AnOlderDependencyIsRejectedWithAClearReason()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(
            temp,
            CreateManifest(
                DependentId,
                dependencies: [new PluginDependency { Id = DependencyId, MinVersion = "2.0.0" }]));
        StagePlugin(temp, CreateManifest(DependencyId, version: "1.0.0"));
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var results = await loader.LoadInstalledAsync(temp.Combine("plugins"));

        Assert.Equal(2, results.Count);
        var dependent = Assert.Single(results, result => result.Manifest?.Id == DependentId);
        var issue = Assert.Single(
            dependent.Validation.Issues,
            candidate => candidate.Code == "plugin.dependencies.incompatible");
        Assert.Contains(DependencyId, issue.Message, StringComparison.Ordinal);
        Assert.Contains("2.0.0", issue.Message, StringComparison.Ordinal);
        Assert.Contains("1.0.0", issue.Message, StringComparison.Ordinal);
        Assert.Single(loader.Plugins);
    }

    [Fact]
    public async Task ADependencyCycleRejectsEveryMemberWithoutLoadingAssemblies()
    {
        using var temp = new TempDirectory();
        StagePlugin(
            temp,
            CreateManifest(
                DependencyId,
                "first-cycle",
                dependencies: [new PluginDependency { Id = DependentId, MinVersion = "1.0.0" }]));
        StagePlugin(
            temp,
            CreateManifest(
                DependentId,
                "second-cycle",
                dependencies: [new PluginDependency { Id = DependencyId, MinVersion = "1.0.0" }]));
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var results = await loader.LoadInstalledAsync(temp.Combine("plugins"));

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.False(result.Succeeded));
        Assert.All(
            results,
            result => Assert.Contains(
                result.Validation.Issues,
                issue => issue.Code == "plugin.dependencies.cycle"));
        Assert.Empty(loader.Plugins);
        Assert.Empty(registry.Commands);
    }

    [Fact]
    public async Task AnIncompatibleApiVersionIsRejectedWithAReasonNamingBothVersions()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest(DependentId);
        manifest.ApiVersion = "99.0";
        var directory = StagePlugin(temp, manifest);
        var registry = new PluginRegistry();
        await using var loader = CreateLoader(registry);

        var result = await loader.LoadAsync(directory);

        Assert.False(result.Succeeded);
        var issue = Assert.Single(
            result.Validation.Issues,
            candidate => candidate.Code == "manifest.apiVersion.majorMismatch");
        Assert.Contains("99.0", issue.Message, StringComparison.Ordinal);
        Assert.Contains("1.0", issue.Message, StringComparison.Ordinal);
    }

    private static PluginLoader CreateLoader(PluginRegistry registry)
        => new(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");

    private static PluginManifest CreateManifest(
        string id,
        string entryType = "SampleCommandPlugin.SamplePlugin",
        string version = "1.0.0",
        IReadOnlyList<PluginDependency>? dependencies = null) => new()
        {
            Id = id,
            Name = "Sample Plugin",
            Version = version,
            ApiVersion = "1.0",
            MinHostVersion = "0.1.0",
            EntryAssembly = "SampleCommandPlugin.dll",
            EntryType = entryType,
            Capabilities = ["command"],
            Dependencies = dependencies ?? [],
        };

    private static string StagePlugin(TempDirectory temp, PluginManifest manifest, string? directoryName = null)
    {
        var directory = temp.CreateDirectory("plugins", directoryName ?? manifest.Id);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "SampleCommandPlugin.dll"),
            Path.Combine(directory, "SampleCommandPlugin.dll"),
            overwrite: true);
        File.WriteAllText(
            Path.Combine(directory, PluginManifestReader.FileName),
            JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        return directory;
    }

    private static string Describe(PluginValidationResult result)
        => string.Join(Environment.NewLine, result.Issues.Select(issue => issue.ToString()));
}

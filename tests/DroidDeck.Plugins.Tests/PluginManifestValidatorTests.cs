using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginManifestValidatorTests
{
    private const string HostVersion = "1.0.0";

    [Fact]
    public void ValidManifestPasses()
    {
        using var temp = new TempDirectory();
        var directory = CreatePluginDirectory(temp);
        var manifest = CreateManifest();

        var result = PluginManifestValidator.Validate(manifest, directory, HostVersion);

        Assert.True(result.IsValid, Describe(result));
        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData(null, "manifest.id.required")]
    [InlineData("", "manifest.id.required")]
    [InlineData("Com.Example", "manifest.id.invalid")]
    [InlineData("com/example", "manifest.id.invalid")]
    [InlineData("com..example", "manifest.id.invalid")]
    public void PluginIdMustBeLowercaseAndDotted(string? id, string expectedCode)
    {
        using var temp = new TempDirectory();
        var directory = CreatePluginDirectory(temp);
        var manifest = CreateManifest();
        manifest.Id = id!;

        var result = PluginManifestValidator.Validate(manifest, directory, HostVersion);

        AssertHasError(result, expectedCode);
    }

    [Fact]
    public void NameIsRequired()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.Name = "";

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, "manifest.name.required");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("not-a-version")]
    public void VersionMustBeSemantic(string version)
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.Version = version;

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, "manifest.version.invalid");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("abc")]
    public void ApiVersionMustBeValid(string apiVersion)
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.ApiVersion = apiVersion;

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, "manifest.apiVersion.invalid");
    }

    [Fact]
    public void MajorApiMismatchBlocksLoading()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.ApiVersion = "2.0";

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, "manifest.apiVersion.majorMismatch");
        Assert.False(result.IsValid);
    }

    [Fact]
    public void MinorApiMismatchWarnsButLoads()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.ApiVersion = "1.1";

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        Assert.True(result.IsValid, Describe(result));
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "manifest.apiVersion.minorMismatch" &&
                issue.Severity == PluginValidationSeverity.Warning);
    }

    [Fact]
    public void MinimumHostVersionNewerThanHostBlocksLoading()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.MinHostVersion = "2.0.0";

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, "manifest.minHostVersion.newerThanHost");
    }

    [Fact]
    public void MaximumHostVersionOlderThanHostBlocksLoading()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.MaxHostVersion = "0.9.0";

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, "manifest.maxHostVersion.olderThanHost");
    }

    [Fact]
    public void InvalidHostVersionConstraintsAreErrors()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.MinHostVersion = "latest";
        manifest.MaxHostVersion = "soon";

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, "manifest.minHostVersion.invalid");
        AssertHasError(result, "manifest.maxHostVersion.invalid");
    }

    [Fact]
    public void MissingEntryAssemblyIsAnError()
    {
        using var temp = new TempDirectory();
        var directory = CreatePluginDirectory(temp);
        var manifest = CreateManifest();
        manifest.EntryAssembly = "Missing.dll";

        var result = PluginManifestValidator.Validate(manifest, directory, HostVersion);

        AssertHasError(result, "manifest.entryAssembly.missing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("bin/Plugin.dll")]
    [InlineData("..\\Plugin.dll")]
    [InlineData("C:\\Windows\\evil.dll")]
    [InlineData("Plugin.exe")]
    public void EntryAssemblyMustBeASimpleDllFileName(string entryAssembly)
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.EntryAssembly = entryAssembly;

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(
            result,
            string.IsNullOrEmpty(entryAssembly)
                ? "manifest.entryAssembly.required"
                : "manifest.entryAssembly.invalid");
    }

    [Fact]
    public void EntryTypeIsRequired()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.EntryType = "";

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, "manifest.entryType.required");
    }

    [Fact]
    public void UnknownCapabilitiesWarnButDoNotBlockLoading()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.Capabilities = ["command", "warp-drive"];

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        Assert.True(result.IsValid, Describe(result));
        Assert.Contains(
            result.Issues,
            issue => issue.Code == "manifest.capabilities.unknown" &&
                issue.Severity == PluginValidationSeverity.Warning);
    }

    [Fact]
    public void DependencyOnSelfIsAnError()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.Dependencies =
        [
            new PluginDependency { Id = manifest.Id, MinVersion = "1.0.0" },
        ];

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, "manifest.dependencies.self");
    }

    [Theory]
    [InlineData("", "1.0.0", "manifest.dependencies.invalidId")]
    [InlineData("Not A Plugin", "1.0.0", "manifest.dependencies.invalidId")]
    [InlineData("com.example.base", "", "manifest.dependencies.invalidVersion")]
    [InlineData("com.example.base", "1.0", "manifest.dependencies.invalidVersion")]
    public void DependenciesMustHaveValidIdsAndVersions(string id, string minVersion, string expectedCode)
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.Dependencies =
        [
            new PluginDependency { Id = id, MinVersion = minVersion },
        ];

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, expectedCode);
    }

    [Fact]
    public void DuplicateDependenciesAreAnError()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.Dependencies =
        [
            new PluginDependency { Id = "com.example.base", MinVersion = "1.0.0" },
            new PluginDependency { Id = "com.example.base", MinVersion = "1.1.0" },
        ];

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        AssertHasError(result, "manifest.dependencies.duplicate");
    }

    [Fact]
    public void MissingIconWarnsButDoesNotBlockLoading()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.Icon = "icon.png";

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        Assert.True(result.IsValid, Describe(result));
        Assert.Contains(result.Issues, issue => issue.Code == "manifest.icon.missing");
    }

    [Fact]
    public void InvalidHomepageWarnsButDoesNotBlockLoading()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();
        manifest.Homepage = "not a url";

        var result = PluginManifestValidator.Validate(manifest, CreatePluginDirectory(temp), HostVersion);

        Assert.True(result.IsValid, Describe(result));
        Assert.Contains(result.Issues, issue => issue.Code == "manifest.homepage.invalid");
    }

    [Fact]
    public void MissingPluginDirectoryIsAnError()
    {
        using var temp = new TempDirectory();
        var manifest = CreateManifest();

        var result = PluginManifestValidator.Validate(manifest, temp.Combine("absent"), HostVersion);

        AssertHasError(result, "plugin.directory.missing");
    }

    [Fact]
    public void ValidationThrowsForNullManifest()
    {
        using var temp = new TempDirectory();

        Assert.Throws<ArgumentNullException>(
            () => PluginManifestValidator.Validate(null!, temp.Path, HostVersion));
    }

    private static PluginManifest CreateManifest() => new()
    {
        Id = "com.example.test",
        Name = "Test Plugin",
        Version = "1.0.0",
        ApiVersion = "1.0",
        EntryAssembly = "TestPlugin.dll",
        EntryType = "TestPlugin.Plugin",
        Capabilities = ["command"],
    };

    private static string CreatePluginDirectory(TempDirectory temp)
    {
        var directory = temp.CreateDirectory("plugin");
        File.WriteAllText(Path.Combine(directory, "TestPlugin.dll"), string.Empty);
        return directory;
    }

    private static void AssertHasError(PluginValidationResult result, string code)
    {
        Assert.False(result.IsValid, Describe(result));
        Assert.Contains(
            result.Issues,
            issue => issue.Code == code && issue.Severity == PluginValidationSeverity.Error);
    }

    private static string Describe(PluginValidationResult result)
        => string.Join(Environment.NewLine, result.Issues.Select(issue => issue.ToString()));
}

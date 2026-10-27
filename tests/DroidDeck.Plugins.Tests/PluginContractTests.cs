using System.Text.Json;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginContractTests
{
    private const string FullManifestJson = """
        {
          // Comments and trailing commas are tolerated.
          "id": "com.example.full",
          "name": "Full Plugin",
          "version": "2.3.4-beta.1+build.7",
          "author": "Example",
          "description": "Exercises every manifest field.",
          "apiVersion": "1.0",
          "minHostVersion": "1.0.0",
          "maxHostVersion": "2.0.0",
          "entryAssembly": "Full.dll",
          "entryType": "Full.Plugin",
          "capabilities": ["command", "ui-page", "future-thing"],
          "dependencies": [
            { "id": "com.example.base", "minVersion": "1.2.3" }
          ],
          "icon": "icon.png",
          "homepage": "https://example.com",
          "license": "MIT",
        }
        """;

    [Fact]
    public void ManifestDeserializesEveryField()
    {
        var manifest = JsonSerializer.Deserialize(FullManifestJson, PluginJsonContext.Default.PluginManifest);

        Assert.NotNull(manifest);
        Assert.Equal("com.example.full", manifest.Id);
        Assert.Equal("Full Plugin", manifest.Name);
        Assert.Equal("2.3.4-beta.1+build.7", manifest.Version);
        Assert.Equal("Example", manifest.Author);
        Assert.Equal("Exercises every manifest field.", manifest.Description);
        Assert.Equal("1.0", manifest.ApiVersion);
        Assert.Equal("1.0.0", manifest.MinHostVersion);
        Assert.Equal("2.0.0", manifest.MaxHostVersion);
        Assert.Equal("Full.dll", manifest.EntryAssembly);
        Assert.Equal("Full.Plugin", manifest.EntryType);
        Assert.Equal(["command", "ui-page", "future-thing"], manifest.Capabilities);
        var dependency = Assert.Single(manifest.Dependencies);
        Assert.Equal("com.example.base", dependency.Id);
        Assert.Equal("1.2.3", dependency.MinVersion);
        Assert.Equal("icon.png", manifest.Icon);
        Assert.Equal("https://example.com", manifest.Homepage);
        Assert.Equal("MIT", manifest.License);
    }

    [Fact]
    public void ManifestCapabilitiesAreMatchedByFlag()
    {
        var manifest = JsonSerializer.Deserialize(FullManifestJson, PluginJsonContext.Default.PluginManifest);
        Assert.NotNull(manifest);

        Assert.True(manifest.KnownCapabilities.HasFlag(PluginCapability.Command));
        Assert.True(manifest.KnownCapabilities.HasFlag(PluginCapability.UiPage));
        Assert.False(manifest.KnownCapabilities.HasFlag(PluginCapability.Theme));
        Assert.Equal(["future-thing"], manifest.UnknownCapabilities);
        Assert.True(manifest.HasCapability(PluginCapability.Command));
        Assert.False(manifest.HasCapability(PluginCapability.DeviceAction));
    }

    [Fact]
    public void ManifestDefaultsToNoCapabilitiesOrDependencies()
    {
        const string json = """
            { "id": "com.example.min", "name": "Min", "version": "1.0.0", "apiVersion": "1.0",
              "entryAssembly": "Min.dll", "entryType": "Min.Plugin" }
            """;

        var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);

        Assert.NotNull(manifest);
        Assert.Equal(PluginCapability.None, manifest.KnownCapabilities);
        Assert.Empty(manifest.UnknownCapabilities);
        Assert.Empty(manifest.Capabilities);
        Assert.Empty(manifest.Dependencies);
    }

    [Fact]
    public void EveryKnownCapabilityRoundTripsThroughItsManifestName()
    {
        foreach (var capability in Enum.GetValues<PluginCapability>())
        {
            if (capability == PluginCapability.None)
            {
                continue;
            }

            var name = PluginCapabilities.ToManifestName(capability);
            var parsed = PluginCapabilities.Parse([name]);

            Assert.Equal(capability, parsed.Known);
            Assert.Empty(parsed.Unknown);
            Assert.False(string.IsNullOrWhiteSpace(PluginCapabilities.ToDisplayString(capability)));
        }
    }

    [Fact]
    public void CapabilityParsingIgnoresBlanksIsCaseInsensitiveAndRetainsUnknownNames()
    {
        var parsed = PluginCapabilities.Parse(["COMMAND", " ", "ui-page", "warp-drive"]);

        Assert.Equal(PluginCapability.Command | PluginCapability.UiPage, parsed.Known);
        Assert.Equal(["warp-drive"], parsed.Unknown);
        Assert.True(parsed.HasUnknown);
    }

    [Fact]
    public void CapabilityParsingHandlesNullAndNone()
    {
        var parsed = PluginCapabilities.Parse(null);

        Assert.Equal(PluginCapability.None, parsed.Known);
        Assert.Empty(parsed.Unknown);
        Assert.Equal("None", PluginCapabilities.ToDisplayString(PluginCapability.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginCapabilities.ToManifestName(PluginCapability.None));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PluginCapabilities.ToManifestName(PluginCapability.Command | PluginCapability.Theme));
    }

    [Theory]
    [InlineData("1.0", "1.0", ApiCompatibility.Compatible)]
    [InlineData("1.0.0", "1.0", ApiCompatibility.Compatible)]
    [InlineData("1.1", "1.0", ApiCompatibility.MinorMismatch)]
    [InlineData("1.0", "1.1", ApiCompatibility.MinorMismatch)]
    [InlineData("0.9", "1.0", ApiCompatibility.MajorMismatch)]
    [InlineData("2.0", "1.0", ApiCompatibility.MajorMismatch)]
    [InlineData("", "1.0", ApiCompatibility.Invalid)]
    [InlineData(null, "1.0", ApiCompatibility.Invalid)]
    [InlineData("1.0", "not-a-version", ApiCompatibility.Invalid)]
    public void ApiCompatibilityExplainsMismatches(
        string? pluginApiVersion,
        string? hostApiVersion,
        ApiCompatibility expected)
    {
        Assert.Equal(expected, PluginApi.CheckCompatibility(pluginApiVersion, hostApiVersion));
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.1", true)]
    [InlineData("2.0", false)]
    [InlineData("garbage", false)]
    public void OnlyCompatibleVersionsCanLoad(string pluginApiVersion, bool expected)
    {
        Assert.Equal(expected, PluginApi.IsCompatible(pluginApiVersion, "1.0"));
    }

    [Fact]
    public void HostVersionIsParsedFromTheTwoPartApiVersion()
    {
        Assert.Equal(1, PluginApi.HostVersion.Major);
        Assert.Equal(0, PluginApi.HostVersion.Minor);
        Assert.Equal(0, PluginApi.HostVersion.Patch);
        Assert.Equal("1.0.0", PluginApi.HostVersion.ToString());
        Assert.Equal(ApiCompatibility.Compatible, PluginApi.CheckCompatibility(PluginApi.Version));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.3.0", "1.2.9", 1)]
    [InlineData("2.0.0", "2.0.0", 0)]
    [InlineData("1.0.0-alpha", "1.0.0", -1)]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    public void SemanticVersionsComparePerSpecification(string left, string right, int expectedSign)
    {
        var result = SemanticVersion.Parse(left).CompareTo(SemanticVersion.Parse(right));

        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Fact]
    public void BuildMetadataDoesNotAffectEqualityOrOrdering()
    {
        var withBuild = SemanticVersion.Parse("1.0.0+build.1");
        var otherBuild = SemanticVersion.Parse("1.0.0+build.2");
        var withoutBuild = SemanticVersion.Parse("1.0.0");

        Assert.Equal(withBuild, otherBuild);
        Assert.Equal(withBuild, withoutBuild);
        Assert.True(withBuild >= withoutBuild);
        Assert.False(withBuild > withoutBuild);
    }

    [Fact]
    public void PreReleaseIdentifiersCompareCaseSensitively()
    {
        var lower = SemanticVersion.Parse("1.0.0-alpha");
        var upper = SemanticVersion.Parse("1.0.0-ALPHA");

        Assert.NotEqual(lower, upper);
        Assert.Equal(lower, SemanticVersion.Parse("1.0.0-alpha"));
        Assert.Equal(lower.GetHashCode(), SemanticVersion.Parse("1.0.0-alpha").GetHashCode());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0+")]
    [InlineData("a.b.c")]
    public void InvalidSemanticVersionsAreRejected(string? value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
    }

    [Fact]
    public void SemanticVersionParseThrowsForInvalidInput()
    {
        Assert.Throws<FormatException>(() => SemanticVersion.Parse("1.0"));
    }
}

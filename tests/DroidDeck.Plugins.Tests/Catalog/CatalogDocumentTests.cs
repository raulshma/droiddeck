using DroidDeck.Plugins.Catalog;

namespace DroidDeck.Plugins.Tests.Catalog;

public sealed class CatalogDocumentTests
{
    internal const string ValidCatalogJson = """
        {
          "schemaVersion": 1,
          "generatedAt": "2026-10-27T12:00:00Z",
          "plugins": [
            {
              "id": "com.example.chart",
              "name": "Chart Renderer",
              "publisher": "Example",
              "summary": "Charts for device metrics.",
              "description": "A longer description.",
              "homepage": "https://example.com/chart",
              "license": "MIT",
              "tags": ["renderer", "charts"],
              "versions": [
                {
                  "version": "1.0.0",
                  "apiVersion": "1.0",
                  "minHostVersion": "1.0.0",
                  "downloadUrl": "https://example.com/chart-1.0.0.ddplugin",
                  "sha256": "9F2C0000000000000000000000000000000000000000000000000000000000AA",
                  "size": 123456,
                  "publishedAt": "2026-10-20T10:00:00Z",
                  "changelog": "Initial release.",
                  "capabilities": ["renderer"],
                  "dependencies": [{ "id": "com.example.shared", "minVersion": "1.0.0" }]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task AValidCatalogParsesEveryField()
    {
        var result = await ParseAsync(ValidCatalogJson);

        Assert.True(result.Succeeded, result.Validation.Summary);
        var catalog = Assert.IsType<CatalogDocument>(result.Catalog);
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T12:00:00Z"), catalog.GeneratedAt);
        var plugin = Assert.Single(catalog.Plugins);
        Assert.Equal("com.example.chart", plugin.Id);
        Assert.Equal("Chart Renderer", plugin.Name);
        Assert.Equal("Example", plugin.Publisher);
        Assert.Equal("Charts for device metrics.", plugin.Summary);
        Assert.Equal("A longer description.", plugin.Description);
        Assert.Equal("https://example.com/chart", plugin.Homepage);
        Assert.Equal("MIT", plugin.License);
        Assert.Equal(["renderer", "charts"], plugin.Tags);
        var version = Assert.Single(plugin.Versions);
        Assert.Equal("1.0.0", version.Version);
        Assert.Equal("1.0", version.ApiVersion);
        Assert.Equal("1.0.0", version.MinHostVersion);
        Assert.Equal("https://example.com/chart-1.0.0.ddplugin", version.DownloadUrl);
        Assert.Equal("9F2C0000000000000000000000000000000000000000000000000000000000AA", version.Sha256);
        Assert.Equal(123456, version.Size);
        Assert.Equal("Initial release.", version.Changelog);
        Assert.Equal(["renderer"], version.Capabilities);
        var dependency = Assert.Single(version.Dependencies);
        Assert.Equal("com.example.shared", dependency.Id);
        Assert.Equal("1.0.0", dependency.MinVersion);
    }

    [Fact]
    public async Task AnUnsupportedSchemaVersionIsRejectedWithAClearError()
    {
        var json = ValidCatalogJson.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);

        var result = await ParseAsync(json);

        Assert.False(result.Succeeded);
        var issue = Assert.Single(result.Validation.Issues);
        Assert.Equal("catalog.schemaVersion.unsupported", issue.Code);
        Assert.Contains("2", issue.Message, StringComparison.Ordinal);
        Assert.Contains("1", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedDocumentIsRejectedWithAClearError()
    {
        var result = await ParseAsync("{ \"schemaVersion\": 1, ");

        Assert.False(result.Succeeded);
        Assert.Null(result.Catalog);
        var issue = Assert.Single(result.Validation.Issues);
        Assert.Equal("catalog.invalidJson", issue.Code);
        Assert.False(string.IsNullOrWhiteSpace(issue.Message));
    }

    [Fact]
    public async Task InvalidPluginEntriesAreReportedTogether()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "plugins": [
                {
                  "id": "Com.Example.Bad",
                  "name": "",
                  "tags": [],
                  "versions": []
                },
                {
                  "id": "com.example.hashes",
                  "name": "Bad Hash",
                  "versions": [
                    {
                      "version": "1.0.0",
                      "apiVersion": "1.0",
                      "downloadUrl": "http://example.com/plugin.ddplugin",
                      "sha256": "not-a-hash",
                      "size": -1
                    }
                  ]
                }
              ]
            }
            """;

        var result = await ParseAsync(json);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "catalog.plugin.id.invalid");
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "catalog.plugin.name.required");
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "catalog.plugin.versions.required");
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "catalog.version.sha256.invalid");
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "catalog.version.downloadUrl.insecure");
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "catalog.version.size.invalid");
    }

    [Fact]
    public async Task OversizedMetadataIsRejected()
    {
        var name = new string('n', 200);
        var json = ValidCatalogJson.Replace("Chart Renderer", name, StringComparison.Ordinal);

        var result = await ParseAsync(json);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "catalog.plugin.name.tooLong");
    }

    [Fact]
    public async Task DuplicatePluginIdsAndVersionsAreRejected()
    {
        var single = SinglePluginJson();
        var json = $$"""
            {
              "schemaVersion": 1,
              "plugins": [ {{single}}, {{single}} ]
            }
            """;

        var result = await ParseAsync(json);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "catalog.plugin.duplicateId");
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "catalog.version.duplicateVersion");
    }

    [Fact]
    public void BrowseFindsPluginsBySearchTextAndTags()
    {
        var entries = CatalogBrowser.Browse(
            [
                Entry("com.example.chart", "Chart Renderer", summary: "Charts for device metrics.", tags: ["renderer", "charts"]),
                Entry("com.example.detector", "Unity Detector", summary: "Finds Unity projects.", tags: ["detector"]),
            ],
            new CatalogQuery { SearchText = "chart renderer" },
            hostVersion: "1.0.0");

        var entry = Assert.Single(entries);
        Assert.Equal("com.example.chart", entry.Plugin.Id);

        var tagged = CatalogBrowser.Browse(
            [
                Entry("com.example.chart", "Chart Renderer", summary: "Charts for device metrics.", tags: ["renderer", "charts"]),
                Entry("com.example.detector", "Unity Detector", summary: "Finds Unity projects.", tags: ["detector"]),
            ],
            new CatalogQuery { Tags = ["RENDERER"] },
            hostVersion: "1.0.0");

        Assert.Equal("com.example.chart", Assert.Single(tagged).Plugin.Id);
    }

    [Fact]
    public void SearchRequiresEveryTermAndIgnoresCase()
    {
        var entries = CatalogBrowser.Browse(
            [Entry("com.example.chart", "Chart Renderer", summary: "Charts for device metrics.", tags: [])],
            new CatalogQuery { SearchText = "CHARTS metrics" },
            hostVersion: "1.0.0");

        Assert.Single(entries);

        var missing = CatalogBrowser.Browse(
            [Entry("com.example.chart", "Chart Renderer", summary: "Charts for device metrics.", tags: [])],
            new CatalogQuery { SearchText = "charts missing" },
            hostVersion: "1.0.0");

        Assert.Empty(missing);
    }

    [Fact]
    public void TheHighestCompatibleStableVersionIsSelected()
    {
        var plugin = PluginWithVersions(
            ("1.0.0", "1.0", "1.0.0"),
            ("2.0.0", "2.0", "1.0.0"),
            ("1.1.0", "1.0", "2.0.0"));

        var entries = CatalogBrowser.Browse(
            [new CatalogPluginSource(Source(), plugin)],
            query: null,
            hostVersion: "1.5.0");

        var version = Assert.Single(entries).Version;
        Assert.Equal("1.0.0", version.Version);
    }

    [Fact]
    public void PrereleasesAreOnlyShownWhenTheSourceAndQueryAllowThem()
    {
        var plugin = PluginWithVersions(("1.1.0-preview.1", "1.0", "1.0.0"));
        var stableSource = new CatalogPluginSource(Source(allowPrerelease: false), plugin);
        var previewSource = new CatalogPluginSource(Source(allowPrerelease: true), plugin);

        Assert.Empty(CatalogBrowser.Browse([stableSource], new CatalogQuery { IncludePrerelease = true }, "1.0.0"));
        Assert.Empty(CatalogBrowser.Browse([previewSource], new CatalogQuery(), "1.0.0"));

        var allowed = CatalogBrowser.Browse(
            [previewSource],
            new CatalogQuery { IncludePrerelease = true },
            "1.0.0");

        var entry = Assert.Single(allowed);
        Assert.True(entry.IsPrerelease);
        Assert.Equal("1.1.0-preview.1", entry.Version.Version);
    }

    [Fact]
    public void CompatibilityBadgesExplainApiAndHostMismatches()
    {
        var entries = CatalogBrowser.Browse(
            [
                new CatalogPluginSource(Source(), PluginWithVersions(("1.0.0", "2.0", "1.0.0"))),
                new CatalogPluginSource(Source(), PluginWithVersions(("1.0.0", "1.0", "9.9.9"))),
                new CatalogPluginSource(Source(), PluginWithVersions(("1.0.0", "1.1", "1.0.0"))),
                new CatalogPluginSource(Source(), PluginWithVersions(("1.0.0", "1.0", "1.0.0"))),
            ],
            query: null,
            hostVersion: "1.5.0");

        var incompatibilities = entries
            .Where(entry => entry.Compatibility == CatalogCompatibility.Incompatible)
            .ToArray();
        Assert.Equal(2, incompatibilities.Length);
        Assert.Contains(
            incompatibilities,
            entry => entry.CompatibilityLabel.Contains("plugin API 2.0", StringComparison.OrdinalIgnoreCase));

        var hostTooNew = incompatibilities.First(
            entry => entry.CompatibilityLabel.Contains("9.9.9", StringComparison.Ordinal));
        Assert.Contains("requires", hostTooNew.CompatibilityLabel, StringComparison.OrdinalIgnoreCase);

        var compatible = CatalogBrowser.Browse(
            [
                new CatalogPluginSource(Source(), PluginWithVersions(("1.0.0", "1.0", "1.0.0"))),
                new CatalogPluginSource(Source(), PluginWithVersions(("1.0.0", "1.1", "1.0.0"))),
            ],
            query: null,
            hostVersion: "1.5.0");

        Assert.Equal(CatalogCompatibility.Compatible, compatible[0].Compatibility);
        Assert.Equal(CatalogCompatibility.Partial, compatible[1].Compatibility);
        Assert.Contains("API 1.1", compatible[1].CompatibilityLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownFieldsAreIgnoredForForwardCompatibility()
    {
        var json = ValidCatalogJson.Replace(
            "\"license\": \"MIT\",",
            "\"license\": \"MIT\", \"futureField\": { \"nested\": true },",
            StringComparison.Ordinal);

        var result = await ParseAsync(json);

        Assert.True(result.Succeeded, result.Validation.Summary);
    }

    internal static CatalogPluginSource Entry(
        string id,
        string name,
        string? summary = null,
        string? publisher = null,
        IReadOnlyList<string>? tags = null,
        params (string Version, string Api, string MinHost)[] versions)
    {
        if (versions.Length == 0)
        {
            versions = [("1.0.0", "1.0", "1.0.0")];
        }

        return new CatalogPluginSource(
            Source(),
            PluginWithVersions(versions, id, name, summary, publisher, tags));
    }

    internal static CatalogSource Source(bool allowPrerelease = false) => new()
    {
        Id = "src-0123456789ab",
        Name = "Example",
        Url = "https://example.com/catalog.json",
        AllowPrerelease = allowPrerelease,
    };

    internal static CatalogPlugin PluginWithVersions(
        params (string Version, string Api, string MinHost)[] versions) =>
        PluginWithVersions(versions, "com.example.chart", "Chart Renderer", "Charts.", "Example", ["renderer"]);

    private static CatalogPlugin PluginWithVersions(
        (string Version, string Api, string MinHost)[] versions,
        string id,
        string name,
        string? summary,
        string? publisher,
        IReadOnlyList<string>? tags) => new()
    {
        Id = id,
        Name = name,
        Publisher = publisher,
        Summary = summary,
        Tags = [.. tags ?? []],
        Versions = versions.Select(version => new CatalogVersion
        {
            Version = version.Version,
            ApiVersion = version.Api,
            MinHostVersion = version.MinHost,
            DownloadUrl = $"https://example.com/{id}-{version.Version}.ddplugin",
            Sha256 = new string('A', 64),
            Size = 1024,
        }).ToList(),
    };

    private static string SinglePluginJson() => """
        {
          "id": "com.example.chart",
          "name": "Chart Renderer",
          "versions": [
            {
              "version": "1.0.0",
              "apiVersion": "1.0",
              "downloadUrl": "https://example.com/chart.ddplugin",
              "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "size": 1
            },
            {
              "version": "1.0.0",
              "apiVersion": "1.0",
              "downloadUrl": "https://example.com/chart.ddplugin",
              "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "size": 1
            }
          ]
        }
        """;

    private static async Task<CatalogReadResult> ParseAsync(string json)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return await PluginCatalogParser.ReadAsync(stream);
    }
}

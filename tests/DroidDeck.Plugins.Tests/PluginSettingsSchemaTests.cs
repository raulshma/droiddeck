using System.Text.Json.Nodes;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginSettingsSchemaTests
{
    [Fact]
    public async Task MissingSchemaFileIsNotAnError()
    {
        using var temp = new TempDirectory();

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.True(result.Validation.IsValid);
        Assert.Empty(result.Validation.Issues);
    }

    [Fact]
    public async Task ValidSchemaParsesEverySupportedFieldType()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "version": 3,
              "title": "Sample settings",
              "description": "Every supported field kind.",
              "properties": {
                "endpoint": {
                  "type": "string",
                  "title": "Endpoint",
                  "description": "The server endpoint.",
                  "default": "https://example.com",
                  "minLength": 8,
                  "maxLength": 100,
                  "pattern": "^https://"
                },
                "retries": { "type": "integer", "default": 3, "minimum": 0, "maximum": 10 },
                "ratio": { "type": "number", "default": 0.5 },
                "verbose": { "type": "boolean", "default": false },
                "mode": { "type": "string", "enum": ["fast", "safe"], "default": "safe" },
                "sdkPath": { "type": "string", "format": "path" },
                "apiKey": { "type": "string", "format": "secret" },
                "tags": { "type": "array", "items": { "type": "string" } },
                "connection": {
                  "type": "object",
                  "properties": {
                    "host": { "type": "string", "default": "localhost" },
                    "port": { "type": "integer", "default": 8080 }
                  },
                  "required": ["host"]
                }
              },
              "required": ["endpoint", "mode"]
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.True(result.Validation.IsValid, Describe(result.Validation));
        var schema = result.Schema!;
        Assert.Equal(3, schema.Version);
        Assert.Equal(9, schema.Fields.Count);

        var endpoint = schema.Find("endpoint")!;
        Assert.Equal(PluginSettingsFieldType.Text, endpoint.Type);
        Assert.Equal("Endpoint", endpoint.DisplayName);
        Assert.True(endpoint.Required);
        Assert.Equal("https://example.com", endpoint.Default!.GetValue<string>());
        Assert.Equal(8, endpoint.MinLength);
        Assert.Equal(100, endpoint.MaxLength);
        Assert.Equal("^https://", endpoint.Pattern);
        Assert.NotNull(endpoint.PatternRegex);
        Assert.Matches(endpoint.Pattern!, "https://example.com");
        Assert.DoesNotMatch(endpoint.Pattern!, "http://example.com");

        Assert.Equal(PluginSettingsFieldType.WholeNumber, schema.Find("retries")!.Type);
        Assert.Equal(0, schema.Find("retries")!.Minimum);
        Assert.Equal(10, schema.Find("retries")!.Maximum);
        Assert.Equal(PluginSettingsFieldType.Number, schema.Find("ratio")!.Type);
        Assert.Equal(PluginSettingsFieldType.Boolean, schema.Find("verbose")!.Type);

        var mode = schema.Find("mode")!;
        Assert.Equal(PluginSettingsFieldType.Enum, mode.Type);
        Assert.Equal(["fast", "safe"], mode.EnumValues);
        Assert.True(mode.Required);

        Assert.Equal(PluginSettingsFieldType.Path, schema.Find("sdkPath")!.Type);
        Assert.Equal(PluginSettingsFieldType.Secret, schema.Find("apiKey")!.Type);

        var tags = schema.Find("tags")!;
        Assert.Equal(PluginSettingsFieldType.List, tags.Type);
        Assert.Equal(PluginSettingsFieldType.Text, tags.ItemType);

        var connection = schema.Find("connection")!;
        Assert.Equal(PluginSettingsFieldType.Group, connection.Type);
        Assert.Equal(2, connection.Properties.Count);
        Assert.True(connection.Properties.Single(field => field.Key == "host").Required);
        Assert.False(connection.Properties.Single(field => field.Key == "port").Required);
    }

    [Fact]
    public async Task VersionDefaultsToOne()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """{ "properties": {} }""");

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Equal(1, result.Schema!.Version);
    }

    [Fact]
    public async Task UnknownTopLevelConstructsAreRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "properties": {},
              "definitions": { "thing": { "type": "string" } }
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(
            result.Validation.Issues,
            issue => issue.Code == "settingsSchema.unknownKey" &&
                issue.Message.Contains("definitions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownFieldConstructsAreRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "properties": {
                "value": { "type": "string", "oneOf": [] }
              }
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(
            result.Validation.Issues,
            issue => issue.Code == "settingsSchema.unknownKey" &&
                issue.Message.Contains("oneOf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsupportedFormatsAndTypesAreRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "properties": {
                "when": { "type": "string", "format": "date-time" },
                "count": { "type": "int64" }
              }
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "settingsSchema.field.invalid");
    }

    [Fact]
    public async Task InvalidPatternIsRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "properties": {
                "value": { "type": "string", "pattern": "([" }
              }
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "settingsSchema.pattern.invalid");
    }

    [Fact]
    public async Task DuplicateAndReservedKeysAreRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "properties": {
                "mode": { "type": "string" },
                "Mode": { "type": "string" },
                "values": { "type": "string" },
                "secrets": { "type": "string" }
              }
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "settingsSchema.key.duplicate");
        Assert.Equal(2, result.Validation.Issues.Count(issue => issue.Code == "settingsSchema.key.reserved"));
    }

    [Fact]
    public async Task RequiredNamingAnUndeclaredFieldIsRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "properties": { "value": { "type": "string" } },
              "required": ["absent"]
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "settingsSchema.required.invalid");
    }

    [Fact]
    public async Task NestedObjectsDeeperThanOneLevelAreRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "properties": {
                "outer": {
                  "type": "object",
                  "properties": {
                    "inner": { "type": "object", "properties": { "leaf": { "type": "string" } } }
                  }
                }
              }
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "settingsSchema.properties.invalid");
    }

    [Fact]
    public async Task NonScalarListItemsAreRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "properties": {
                "rows": { "type": "array", "items": { "type": "object" } }
              }
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "settingsSchema.items.invalid");
    }

    [Fact]
    public async Task DefaultViolatingTheFieldIsRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "properties": {
                "retries": { "type": "integer", "minimum": 5, "default": 1 }
              }
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "settingsSchema.default.invalid");
    }

    [Fact]
    public async Task InvalidJsonIsReported()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, "{ this is not json");

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "settingsSchema.invalidJson");
    }

    [Fact]
    public async Task MigrationsParseWithRenamesDefaultsAndRemovals()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "version": 2,
              "properties": { "endpoint": { "type": "string" } },
              "migrations": [
                {
                  "fromVersion": 1,
                  "renames": { "server": "endpoint" },
                  "defaults": { "endpoint": "https://fallback.example" },
                  "removed": ["obsolete"]
                }
              ]
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.True(result.Validation.IsValid, Describe(result.Validation));
        var step = Assert.Single(result.Schema!.Migrations);
        Assert.Equal(1, step.FromVersion);
        Assert.Equal("endpoint", step.Renames["server"]);
        Assert.Equal("https://fallback.example", step.Defaults["endpoint"]!.GetValue<string>());
        Assert.Equal(["obsolete"], step.Removed);
    }

    [Fact]
    public async Task MigrationFromVersionAtOrAfterSchemaVersionIsRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "version": 2,
              "properties": {},
              "migrations": [ { "fromVersion": 2, "removed": ["old"] } ]
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "settingsSchema.migrations.invalid");
    }

    [Fact]
    public async Task DuplicateMigrationVersionsAreRejected()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(PluginSettingsSchemaReader.FileName, """
            {
              "version": 3,
              "properties": {},
              "migrations": [
                { "fromVersion": 1, "removed": ["a"] },
                { "fromVersion": 1, "removed": ["b"] }
              ]
            }
            """);

        var result = await PluginSettingsSchemaReader.ReadAsync(temp.Path);

        Assert.Null(result.Schema);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "settingsSchema.migrations.invalid");
    }

    private static string Describe(PluginValidationResult result)
        => string.Join(Environment.NewLine, result.Issues.Select(issue => issue.ToString()));
}

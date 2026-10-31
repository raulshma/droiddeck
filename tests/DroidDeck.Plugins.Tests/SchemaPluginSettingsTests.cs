using System.Text.Json.Nodes;
using DroidDeck.Core;
using DroidDeck.Core.Security;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

public sealed class SchemaPluginSettingsTests : IDisposable
{
    private const string PluginId = "com.example.sample";
    private const string SecretSchema = """
        { "version": 1, "properties": { "apiKey": { "type": "string", "format": "secret" } } }
        """;

    private readonly TempDirectory _temp = new();
    private readonly DpapiSecretStore _secrets;

    public SchemaPluginSettingsTests()
    {
        _secrets = new DpapiSecretStore(new AppPaths(_temp.Combine("data")));
    }

    public void Dispose()
    {
        _secrets.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task DefaultsAreAppliedForMissingValuesWithoutWritingTheFile()
    {
        using var settings = await CreateStoreAsync("""
            { "version": 1, "properties": {
                "endpoint": { "type": "string", "default": "https://example.com" },
                "retries": { "type": "integer", "default": 3 }
              } }
            """);

        Assert.Equal("https://example.com", await settings.GetAsync("endpoint"));
        Assert.Equal("3", await settings.GetAsync("retries"));
        Assert.Equal(3, (await settings.GetNodeAsync("retries"))!.GetValue<int>());
        Assert.False(File.Exists(settings.FilePath));
    }

    [Fact]
    public async Task TypedValuesRoundTripAndStayTypedOnDisk()
    {
        var directory = NewPluginDirectory();
        using var settings = await CreateStoreAsync("""
            { "version": 1, "properties": {
                "endpoint": { "type": "string" },
                "retries": { "type": "integer" },
                "ratio": { "type": "number" },
                "verbose": { "type": "boolean" },
                "tags": { "type": "array", "items": { "type": "string" } }
              } }
            """, directory);

        await settings.SetNodeAsync("endpoint", JsonValue.Create("https://example.com"));
        await settings.SetNodeAsync("retries", JsonValue.Create(5));
        await settings.SetNodeAsync("ratio", JsonValue.Create(0.75));
        await settings.SetNodeAsync("verbose", JsonValue.Create(true));
        await settings.SetNodeAsync("tags", new JsonArray("alpha", "beta"));

        var json = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
        Assert.Contains("\"retries\": 5", json, StringComparison.Ordinal);
        Assert.Contains("\"ratio\": 0.75", json, StringComparison.Ordinal);
        Assert.Contains("\"verbose\": true", json, StringComparison.Ordinal);

        Assert.Equal("https://example.com", await settings.GetAsync("endpoint"));
        Assert.Equal("5", await settings.GetAsync("retries"));
        Assert.Equal("0.75", await settings.GetAsync("ratio"));
        Assert.Equal("true", await settings.GetAsync("verbose"));
        Assert.Equal("""["alpha","beta"]""", await settings.GetAsync("tags"));
    }

    [Fact]
    public async Task ValuesSurviveReload()
    {
        var directory = NewPluginDirectory();
        using (var settings = await CreateStoreAsync(
            """{ "version": 1, "properties": { "endpoint": { "type": "string" } } }""",
            directory))
        {
            await settings.SetNodeAsync("endpoint", JsonValue.Create("https://example.com"));
        }

        using var reloaded = await CreateStoreAsync(
            """{ "version": 1, "properties": { "endpoint": { "type": "string" } } }""",
            directory);
        Assert.Equal("https://example.com", await reloaded.GetAsync("endpoint"));
    }

    [Fact]
    public async Task RawStringSetParsesValuesByFieldType()
    {
        using var settings = await CreateStoreAsync("""
            { "version": 1, "properties": {
                "retries": { "type": "integer", "minimum": 1 },
                "verbose": { "type": "boolean" }
              } }
            """);

        await settings.SetAsync("retries", "4");
        await settings.SetAsync("verbose", "true");

        Assert.Equal(4L, (await settings.GetNodeAsync("retries"))!.GetValue<long>());
        Assert.True((await settings.GetNodeAsync("verbose"))!.GetValue<bool>());
    }

    [Fact]
    public async Task InvalidSetBlocksSavingAndThrowsWithFieldErrors()
    {
        var directory = NewPluginDirectory();
        const string schema = """
            { "version": 1, "properties": { "retries": { "type": "integer", "minimum": 1, "maximum": 5 } } }
            """;
        using var settings = await CreateStoreAsync(schema, directory);
        await settings.SetNodeAsync("retries", JsonValue.Create(3));

        var rejected = await Assert.ThrowsAsync<PluginSettingsValidationException>(
            () => settings.SetAsync("retries", "12"));
        Assert.Equal("retries", rejected.Errors[0].Field);
        await Assert.ThrowsAsync<PluginSettingsValidationException>(
            () => settings.SetAsync("retries", "not-a-number"));

        // Nothing was saved: memory and disk still hold the previous value.
        Assert.Equal(3, (await settings.GetNodeAsync("retries"))!.GetValue<int>());
        settings.Dispose();
        using var reloaded = await CreateStoreAsync(schema, directory);
        Assert.Equal(3, (await reloaded.GetNodeAsync("retries"))!.GetValue<int>());
    }

    [Fact]
    public async Task SetNodeReturnsFieldLevelErrorsForEveryConstraintKind()
    {
        using var settings = await CreateStoreAsync("""
            { "version": 1, "properties": {
                "retries": { "type": "integer", "minimum": 1, "maximum": 5 },
                "mode": { "type": "string", "enum": ["fast", "safe"] },
                "endpoint": { "type": "string", "pattern": "^https://", "minLength": 12 },
                "verbose": { "type": "boolean" }
              } }
            """);

        var wrongType = await settings.SetNodeAsync("retries", JsonValue.Create("soon"));
        Assert.False(wrongType.IsSaved);
        Assert.Equal("retries", Assert.Single(wrongType.Errors).Field);
        Assert.Equal("settings.value.type", wrongType.Errors[0].Code);

        var tooSmall = await settings.SetNodeAsync("retries", JsonValue.Create(0));
        Assert.Equal("settings.value.minimum", Assert.Single(tooSmall.Errors).Code);
        var tooLarge = await settings.SetNodeAsync("retries", JsonValue.Create(6));
        Assert.Equal("settings.value.maximum", Assert.Single(tooLarge.Errors).Code);

        var notAnEnumValue = await settings.SetNodeAsync("mode", JsonValue.Create("turbo"));
        Assert.Equal("mode", Assert.Single(notAnEnumValue.Errors).Field);
        Assert.Equal("settings.value.enum", notAnEnumValue.Errors[0].Code);

        var badPattern = await settings.SetNodeAsync("endpoint", JsonValue.Create("http://example.com"));
        Assert.Equal("settings.value.pattern", Assert.Single(badPattern.Errors).Code);

        var tooShort = await settings.SetNodeAsync("endpoint", JsonValue.Create("https://a"));
        Assert.Equal("settings.value.minLength", Assert.Single(tooShort.Errors).Code);

        var notABoolean = await settings.SetNodeAsync("verbose", JsonValue.Create("yes"));
        Assert.Equal("settings.value.type", Assert.Single(notABoolean.Errors).Code);
    }

    [Fact]
    public async Task ValidateReturnsAllFieldErrorsKeyedByField()
    {
        // The store rejects invalid writes, so an invalid stored value can only
        // come from a hand-edited file or a schema change on disk.
        var directory = NewPluginDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "settings.json"),
            """{ "settingsVersion": 1, "values": { "retries": 0, "connection": {} } }""");
        using var settings = await CreateStoreAsync("""
            { "version": 1, "properties": {
                "endpoint": { "type": "string" },
                "retries": { "type": "integer", "minimum": 1 },
                "connection": {
                  "type": "object",
                  "properties": { "port": { "type": "integer" } },
                  "required": ["port"]
                }
              } }
            """, directory);

        var errors = await settings.ValidateAsync();

        Assert.Contains(errors, error => error.Field == "connection.port" &&
            error.Code == "settings.value.required");
        Assert.Contains(errors, error => error.Field == "retries" &&
            error.Code == "settings.value.minimum");
        Assert.DoesNotContain(errors, error => error.Field == "endpoint");
    }

    [Fact]
    public async Task UnknownKeysArePreservedAcrossSaves()
    {
        var directory = NewPluginDirectory();
        using var settings = await CreateStoreAsync(
            """{ "version": 1, "properties": { "endpoint": { "type": "string" } } }""",
            directory);
        await settings.SetNodeAsync("endpoint", JsonValue.Create("https://example.com"));
        await settings.SetAsync("futureSetting", "kept");

        await settings.SetNodeAsync("endpoint", JsonValue.Create("https://other.example"));

        Assert.Equal("kept", await settings.GetAsync("futureSetting"));
        var json = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
        Assert.Contains("futureSetting", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyFlatSettingsAreImportedIntoValues()
    {
        var directory = NewPluginDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "settings.json"),
            """{ "endpoint": "https://legacy.example", "retries": "7" }""");
        using var settings = await CreateStoreAsync(
            """{ "version": 1, "properties": { "endpoint": { "type": "string" } } }""",
            directory);

        Assert.Equal("https://legacy.example", await settings.GetAsync("endpoint"));
        Assert.Equal("7", await settings.GetAsync("retries"));

        await settings.SetNodeAsync("endpoint", JsonValue.Create("https://updated.example"));
        var document = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")))!;
        Assert.Equal("https://updated.example", document["values"]!["endpoint"]!.GetValue<string>());
    }

    [Fact]
    public async Task CorruptSettingsFileIsQuarantined()
    {
        var directory = NewPluginDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{ this is not json");
        using var settings = await CreateStoreAsync(
            """{ "version": 1, "properties": { "endpoint": { "type": "string" } } }""",
            directory);

        Assert.Null(await settings.GetAsync("endpoint"));
        await settings.SetAsync("endpoint", "https://example.com");
        Assert.Equal("https://example.com", await settings.GetAsync("endpoint"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(directory, "quarantine"), "*.corrupt"));
    }

    [Fact]
    public async Task MigrationRenamesValuesAndFillsDefaultsAndRemovesKeys()
    {
        var directory = NewPluginDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "settings.json"),
            """{ "settingsVersion": 1, "values": { "server": "https://example.com", "obsolete": "x", "keepMe": "yes" } }""");
        using var settings = await CreateStoreAsync("""
            { "version": 2, "properties": { "endpoint": { "type": "string" }, "keepMe": { "type": "string" } },
              "migrations": [ {
                "fromVersion": 1,
                "renames": { "server": "endpoint" },
                "defaults": { "endpoint": "https://fallback.example" },
                "removed": ["obsolete"]
              } ]
            }
            """, directory);

        Assert.Equal("https://example.com", await settings.GetAsync("endpoint"));
        Assert.Equal("yes", await settings.GetAsync("keepMe"));
        Assert.Null(await settings.GetAsync("obsolete"));
        Assert.Null(await settings.GetAsync("server"));

        settings.Dispose();
        var document = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")))!;
        Assert.Equal(2, document["settingsVersion"]!.GetValue<int>());
        Assert.Equal("https://example.com", document["values"]!["endpoint"]!.GetValue<string>());
        Assert.Null(document["values"]!["server"]);
        Assert.Null(document["values"]!["obsolete"]);
    }

    [Fact]
    public async Task StoredVersionAtSchemaVersionSkipsMigrations()
    {
        var directory = NewPluginDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "settings.json"),
            """{ "settingsVersion": 2, "values": { "endpoint": "https://example.com" } }""");
        using var settings = await CreateStoreAsync("""
            { "version": 2, "properties": { "endpoint": { "type": "string" } },
              "migrations": [ { "fromVersion": 1, "defaults": { "endpoint": "https://fallback.example" } } ]
            }
            """, directory);

        Assert.Equal("https://example.com", await settings.GetAsync("endpoint"));
    }

    [Fact]
    public async Task SecretsRoundTripThroughTheDpapiStoreAndNeverReachSettingsJson()
    {
        var directory = NewPluginDirectory();
        using var settings = await CreateStoreAsync(
            """{ "version": 1, "properties": { "apiKey": { "type": "string", "format": "secret" } } }""",
            directory);
        const string secret = "correct-horse-battery-staple-9271";

        var result = await settings.SetSecretAsync("apiKey", secret);

        Assert.True(result.IsSaved);
        Assert.True(await settings.IsSecretSetAsync("apiKey"));
        Assert.Equal(secret, await settings.GetAsync("apiKey"));
        Assert.Equal("_p18-com.example.sample.apiKey", settings.SecretName("apiKey"));

        // The plaintext never reaches the settings file or any backup of it.
        foreach (var file in Directory.EnumerateFiles(directory, "settings.json*", SearchOption.AllDirectories))
        {
            var contents = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(secret, contents, StringComparison.Ordinal);
        }

        // Reloaded stores find the secret again through the DPAPI store.
        settings.Dispose();
        using var reloaded = await CreateStoreAsync(
            """{ "version": 1, "properties": { "apiKey": { "type": "string", "format": "secret" } } }""",
            directory);
        Assert.True(await reloaded.IsSecretSetAsync("apiKey"));
        Assert.Equal(secret, await reloaded.GetAsync("apiKey"));
    }

    [Fact]
    public async Task RequiredSecretStopsBlockingValidationOnceSet()
    {
        using var settings = await CreateStoreAsync("""
            { "version": 1, "properties": { "apiKey": { "type": "string", "format": "secret" } },
              "required": ["apiKey"] }
            """);

        var before = await settings.ValidateAsync();
        Assert.Contains(before, error => error.Field == "apiKey" && error.Code == "settings.value.required");

        await settings.SetSecretAsync("apiKey", "configured-value-6604");

        Assert.Empty(await settings.ValidateAsync());
    }

    [Fact]
    public async Task LegacyPlaintextSecretValuesMigrateIntoTheSecretStore()
    {
        var directory = NewPluginDirectory();
        const string secret = "legacy-plaintext-5508";
        await File.WriteAllTextAsync(
            Path.Combine(directory, "settings.json"),
            """{ "endpoint": "https://legacy.example", "apiKey": "legacy-plaintext-5508" }""");
        const string schema = """
            { "version": 1, "properties": {
                "endpoint": { "type": "string" },
                "apiKey": { "type": "string", "format": "secret" }
              } }
            """;

        using var settings = await CreateStoreAsync(schema, directory);

        // The plaintext moved into the DPAPI store and is readable only there.
        Assert.True(await settings.IsSecretSetAsync("apiKey"));
        Assert.Equal(secret, await settings.GetAsync("apiKey"));
        Assert.Equal(secret, await _secrets.GetAsync(settings.SecretName("apiKey")));

        // Saving rewrites the file without the plaintext, including the backup
        // the atomic write keeps of the previous contents.
        await settings.SetNodeAsync("endpoint", JsonValue.Create("https://updated.example"));
        foreach (var file in Directory.EnumerateFiles(directory, "settings.json*", SearchOption.AllDirectories))
        {
            var contents = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain(secret, contents, StringComparison.Ordinal);
        }

        settings.Dispose();
        var document = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")))!;
        Assert.Null(document["values"]!["apiKey"]);
        Assert.Equal(
            ["apiKey"],
            (document["secrets"] as JsonArray)!.Select(static node => node!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task SecretPresenceMarkerIsRecordedButValueStaysOut()
    {
        var directory = NewPluginDirectory();
        using var settings = await CreateStoreAsync(
            """{ "version": 1, "properties": { "apiKey": { "type": "string", "format": "secret" } } }""",
            directory);
        await settings.SetSecretAsync("apiKey", "hunter2-with-entropy-42");

        var json = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));

        Assert.Contains("apiKey", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.Null(await settings.GetNodeAsync("apiKey"));
    }

    [Fact]
    public async Task ClearSecretRemovesValueAndMarker()
    {
        var directory = NewPluginDirectory();
        using var settings = await CreateStoreAsync(
            """{ "version": 1, "properties": { "apiKey": { "type": "string", "format": "secret" } } }""",
            directory);
        await settings.SetSecretAsync("apiKey", "hunter2-with-entropy-42");

        await settings.ClearSecretAsync("apiKey");

        Assert.False(await settings.IsSecretSetAsync("apiKey"));
        Assert.Null(await settings.GetAsync("apiKey"));
        settings.Dispose();
        var document = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine(directory, "settings.json")))!;
        Assert.Empty((document["secrets"] as JsonArray)!);
    }

    [Fact]
    public async Task LegacySecretNamesAreReadAndMigratedOnAccess()
    {
        var directory = NewPluginDirectory();
        const string secret = "legacy-store-secret-8812";
        await _secrets.SetAsync($"{PluginId}.apiKey", secret);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "settings.json"),
            """{ "settingsVersion": 1, "values": {}, "secrets": ["apiKey"] }""");
        using var settings = await CreateStoreAsync(SecretSchema, directory);

        Assert.True(await settings.IsSecretSetAsync("apiKey"));
        Assert.Equal(secret, await settings.GetAsync("apiKey"));
        Assert.NotEqual($"{PluginId}.apiKey", settings.SecretName("apiKey"));

        // Reading migrated the value to the current name and removed the alias.
        Assert.Equal(secret, await _secrets.GetAsync(settings.SecretName("apiKey")));
        Assert.Null(await _secrets.GetAsync($"{PluginId}.apiKey"));
    }

    [Fact]
    public async Task LegacySecretsBeyondTheCurrentNameLimitStillMigrate()
    {
        var directory = NewPluginDirectory();
        var pluginId = new string('p', 60);
        var key = new string('k', 64);
        const string secret = "legacy-oversized-secret-9913";
        Assert.Equal(125, $"{pluginId}.{key}".Length);
        await _secrets.SetAsync($"{pluginId}.{key}", secret);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "settings.json"),
            """{ "settingsVersion": 1, "values": {}, "secrets": ["KEY"] }""".Replace("KEY", key));
        var schema = """{ "version": 1, "properties": { "KEY": { "type": "string", "format": "secret" } } }"""
            .Replace("KEY", key);
        using var settings = await CreateStoreAsync(schema, directory, pluginId);

        Assert.True(await settings.IsSecretSetAsync(key));
        Assert.Equal(secret, await settings.GetAsync(key));

        // The length-prefixed name would exceed the store limit, so the read
        // migrated the value to the stable hashed fallback instead.
        var currentName = settings.SecretName(key);
        Assert.StartsWith("_h", currentName, StringComparison.Ordinal);
        Assert.True(PluginSecretName.IsValidName(currentName));
        Assert.Equal(secret, await _secrets.GetAsync(currentName));
        Assert.Null(await _secrets.GetAsync($"{pluginId}.{key}"));
    }

    [Fact]
    public async Task WritingASecretMigratesItFromTheLegacyName()
    {
        var directory = NewPluginDirectory();
        await _secrets.SetAsync($"{PluginId}.apiKey", "legacy-store-secret-3301");
        using var settings = await CreateStoreAsync(SecretSchema, directory);

        await settings.SetSecretAsync("apiKey", "current-store-secret-3302");

        Assert.Equal(
            "current-store-secret-3302",
            await _secrets.GetAsync(settings.SecretName("apiKey")));
        Assert.Null(await _secrets.GetAsync($"{PluginId}.apiKey"));
        Assert.Equal(settings.SecretName("apiKey"), Assert.Single(await _secrets.ListNamesAsync()));
    }

    [Fact]
    public async Task ClearingASecretDeletesTheCurrentAndLegacyEntries()
    {
        var directory = NewPluginDirectory();
        using var settings = await CreateStoreAsync(SecretSchema, directory);
        var currentName = settings.SecretName("apiKey");
        await _secrets.SetAsync(currentName, "current-store-secret-4401");
        await _secrets.SetAsync($"{PluginId}.apiKey", "legacy-store-secret-4402");

        await settings.ClearSecretAsync("apiKey");

        Assert.Null(await _secrets.GetAsync(currentName));
        Assert.Null(await _secrets.GetAsync($"{PluginId}.apiKey"));
        Assert.Empty(await _secrets.ListNamesAsync());
    }

    [Fact]
    public async Task RemovalPathDeletesSecretsUnderTheCurrentScheme()
    {
        // Uninstall/removal cleanup removes each declared secret field through
        // RemoveAsync; it must delete the current names, not only legacy aliases.
        const string schema = """
            { "version": 1, "properties": {
                "apiKey": { "type": "string", "format": "secret" },
                "token": { "type": "string", "format": "secret" }
              } }
            """;
        var directory = NewPluginDirectory();
        using var settings = await CreateStoreAsync(schema, directory);
        foreach (var key in new[] { "apiKey", "token" })
        {
            await settings.SetSecretAsync(key, $"current-store-secret-{key}");
            await _secrets.SetAsync($"{PluginId}.{key}", $"legacy-store-secret-{key}");
        }

        foreach (var field in settings.Schema.Fields)
        {
            await settings.RemoveAsync(field.Key);
        }

        Assert.Empty(await _secrets.ListNamesAsync());
        Assert.False(await settings.IsSecretSetAsync("apiKey"));
        Assert.False(await settings.IsSecretSetAsync("token"));
    }

    [Fact]
    public async Task PluginsWithDottedIdsAndKeysCannotReadEachOthersSecrets()
    {
        var firstDirectory = _temp.CreateDirectory("plugins", "first");
        var secondDirectory = _temp.CreateDirectory("plugins", "second");
        using var first = await CreateStoreAsync(
            """{ "version": 1, "properties": { "bar.apiKey": { "type": "string", "format": "secret" } } }""",
            firstDirectory,
            "com.foo");
        using var second = await CreateStoreAsync(
            """{ "version": 1, "properties": { "apiKey": { "type": "string", "format": "secret" } } }""",
            secondDirectory,
            "com.foo.bar");

        await first.SetSecretAsync("bar.apiKey", "first-plugin-secret-6601");
        await second.SetSecretAsync("apiKey", "second-plugin-secret-6602");

        Assert.NotEqual(first.SecretName("bar.apiKey"), second.SecretName("apiKey"));
        Assert.Equal("first-plugin-secret-6601", await first.GetAsync("bar.apiKey"));
        Assert.Equal("second-plugin-secret-6602", await second.GetAsync("apiKey"));
        Assert.Equal(
            "first-plugin-secret-6601",
            await _secrets.GetAsync(first.SecretName("bar.apiKey")));
        Assert.Equal(
            "second-plugin-secret-6602",
            await _secrets.GetAsync(second.SecretName("apiKey")));
    }

    [Fact]
    public async Task SecretFieldsRejectPlainValueWritesAndPlainFieldsRejectSecretWrites()
    {
        using var settings = await CreateStoreAsync("""
            { "version": 1, "properties": {
                "apiKey": { "type": "string", "format": "secret" },
                "endpoint": { "type": "string" }
              } }
            """);

        var viaNode = await settings.SetNodeAsync("apiKey", JsonValue.Create("hunter2"));
        Assert.False(viaNode.IsSaved);

        var viaSecret = await settings.SetSecretAsync("endpoint", "hunter2");
        Assert.False(viaSecret.IsSaved);
    }

    [Fact]
    public async Task MigrationMovesSecretsToTheRenamedKeyWithoutLeakingTheValue()
    {
        var directory = NewPluginDirectory();
        await _secrets.SetAsync($"{PluginId}.apiKey", "legacy-secret-4711");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "settings.json"),
            """{ "settingsVersion": 1, "values": {}, "secrets": ["apiKey"] }""");
        const string schema = """
            { "version": 2, "properties": { "token": { "type": "string", "format": "secret" } },
              "migrations": [ { "fromVersion": 1, "renames": { "apiKey": "token" } } ]
            }
            """;

        using var migrated = await CreateStoreAsync(schema, directory);

        Assert.True(await migrated.IsSecretSetAsync("token"));
        Assert.Equal("legacy-secret-4711", await migrated.GetAsync("token"));
        Assert.False(await migrated.IsSecretSetAsync("apiKey"));
        Assert.Null(await _secrets.GetAsync($"{PluginId}.apiKey"));

        // The rename moved the value to the current name of the new key and
        // left no current-scheme or legacy entry behind for the old keys.
        var tokenName = PluginSecretName.Build(PluginId, "token");
        Assert.Equal("legacy-secret-4711", await _secrets.GetAsync(tokenName));
        Assert.Null(await _secrets.GetAsync(PluginSecretName.Build(PluginId, "apiKey")));
        Assert.Null(await _secrets.GetAsync($"{PluginId}.token"));
        Assert.Equal(tokenName, Assert.Single(await _secrets.ListNamesAsync()));

        migrated.Dispose();
        var json = await File.ReadAllTextAsync(Path.Combine(directory, "settings.json"));
        Assert.DoesNotContain("legacy-secret-4711", json, StringComparison.Ordinal);
        Assert.Contains("token", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportOmitsSecretValuesAndReportsThem()
    {
        using var settings = await CreateStoreAsync("""
            { "version": 1, "properties": {
                "endpoint": { "type": "string" },
                "apiKey": { "type": "string", "format": "secret" }
              } }
            """);
        await settings.SetNodeAsync("endpoint", JsonValue.Create("https://example.com"));
        await settings.SetSecretAsync("apiKey", "export-should-drop-this-1234");

        var export = await settings.ExportAsync();

        Assert.Equal("https://example.com", export.Values["endpoint"]!.GetValue<string>());
        Assert.Null(export.Values["apiKey"]);
        Assert.Equal(["apiKey"], export.ExcludedSecrets);
        var json = export.ToJson();
        Assert.DoesNotContain("export-should-drop-this-1234", json, StringComparison.Ordinal);
        Assert.Contains("endpoint", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportKeepsUnknownKeysForRoundTrips()
    {
        using var settings = await CreateStoreAsync(
            """{ "version": 1, "properties": { "endpoint": { "type": "string" } } }""");
        await settings.SetAsync("futureSetting", "kept");

        var export = await settings.ExportAsync();

        Assert.Equal("kept", export.Values["futureSetting"]!.GetValue<string>());
    }

    /// <summary>
    /// The UI-adjacent masking rule, tested headlessly: a set secret displays
    /// "Configured" and an unset one "Not set", and nothing offers the plaintext
    /// for display.
    /// </summary>
    [Fact]
    public async Task SecretMaskingDecisionIsHeadless()
    {
        using var settings = await CreateStoreAsync(
            """{ "version": 1, "properties": { "apiKey": { "type": "string", "format": "secret" } } }""");

        Assert.False(await settings.IsSecretSetAsync("apiKey"));
        Assert.Equal("Not set", DescribeSecretForDisplay(await settings.IsSecretSetAsync("apiKey")));

        await settings.SetSecretAsync("apiKey", "configured-value-777");
        Assert.True(await settings.IsSecretSetAsync("apiKey"));
        Assert.Equal("Configured", DescribeSecretForDisplay(await settings.IsSecretSetAsync("apiKey")));
    }

    private static string DescribeSecretForDisplay(bool isSet) => isSet ? "Configured" : "Not set";

    private string NewPluginDirectory() => _temp.CreateDirectory("plugin");

    private async Task<SchemaPluginSettings> CreateStoreAsync(string schemaJson)
        => await CreateStoreAsync(schemaJson, NewPluginDirectory());

    private async Task<SchemaPluginSettings> CreateStoreAsync(string schemaJson, string directory)
        => await CreateStoreAsync(schemaJson, directory, pluginId: null);

    private async Task<SchemaPluginSettings> CreateStoreAsync(
        string schemaJson,
        string directory,
        string? pluginId)
    {
        await File.WriteAllTextAsync(
            Path.Combine(directory, PluginSettingsSchemaReader.FileName),
            schemaJson);
        var read = await PluginSettingsSchemaReader.ReadAsync(directory);
        Assert.True(read.Validation.IsValid, string.Join(Environment.NewLine, read.Validation.Issues));
        return new SchemaPluginSettings(
            Path.Combine(directory, "settings.json"),
            read.Schema!,
            pluginId ?? PluginId,
            _secrets);
    }
}

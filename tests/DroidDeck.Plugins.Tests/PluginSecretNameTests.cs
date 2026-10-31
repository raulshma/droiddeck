namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Verifies the secret-name codec: the length-prefixed format is unambiguous,
/// stays inside the secret store's name alphabet and length limit, and rejects
/// inputs that cannot produce a valid name.
/// </summary>
public sealed class PluginSecretNameTests
{
    [Fact]
    public void AdversarialPluginIdAndFieldKeyPairsProduceDifferentNames()
    {
        // Both pairs collided under the legacy "pluginId.fieldKey" format.
        var first = PluginSecretName.Build("com.foo", "bar.apiKey");
        var second = PluginSecretName.Build("com.foo.bar", "apiKey");

        Assert.Equal("_p7-com.foo.bar.apiKey", first);
        Assert.Equal("_p11-com.foo.bar.apiKey", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void EveryCombinationOfDottedDashedAndUnderscoredPartsIsUnique()
    {
        string[] pluginIds =
        [
            "a",
            "a.b",
            "a.b.c",
            "a-b",
            "a.b-c",
            "p7-a",
            "com.foo",
            "com.example.sample",
        ];
        string[] fieldKeys =
        [
            "a",
            "a.b",
            "a.b.c",
            "a-b",
            "a.b-c",
            "underscore_key.value",
            "bar.apiKey",
        ];
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pluginId in pluginIds)
        {
            foreach (var fieldKey in fieldKeys)
            {
                var name = PluginSecretName.Build(pluginId, fieldKey);

                Assert.True(names.Add(name), $"The name '{name}' was produced twice.");
                AssertStoreCompatible(name);
                Assert.True(PluginSecretName.TryParse(name, out var parsedId, out var parsedKey));
                Assert.Equal(pluginId, parsedId);
                Assert.Equal(fieldKey, parsedKey);
            }
        }

        Assert.Equal(pluginIds.Length * fieldKeys.Length, names.Count);
    }

    [Theory]
    [InlineData("com.example.sample", "apiKey")]
    [InlineData("com.foo", "bar.apiKey")]
    [InlineData("com.foo.bar", "apiKey")]
    [InlineData("com.foo-bar", "api.key")]
    [InlineData("com.foo", "bar-api.key")]
    [InlineData("p7-com.foo", "bar.baz")]
    [InlineData("0", "0")]
    [InlineData("a", "a..b")]
    [InlineData("com.example", "underscore_key.value")]
    public void ParserRoundTripsThePluginIdAndFieldKey(string pluginId, string fieldKey)
    {
        var name = PluginSecretName.Build(pluginId, fieldKey);

        Assert.True(PluginSecretName.TryParse(name, out var parsedId, out var parsedKey));
        Assert.Equal(pluginId, parsedId);
        Assert.Equal(fieldKey, parsedKey);
    }

    [Fact]
    public void NamesStayInsideTheSecretStoreAlphabetAndLengthLimit()
    {
        var key = new string('k', 64);
        var pluginId = new string('a', 58);

        var name = PluginSecretName.Build(pluginId, key);

        Assert.Equal(PluginSecretName.MaxLength, name.Length);
        AssertStoreCompatible(name);
        Assert.True(PluginSecretName.TryParse(name, out var parsedId, out var parsedKey));
        Assert.Equal(pluginId, parsedId);
        Assert.Equal(key, parsedKey);
    }

    [Fact]
    public void NamesThatWouldExceedTheStoreLimitFallBackToAStableHashedName()
    {
        var key = new string('k', 64);
        var longest = PluginSecretName.Build(new string('a', 58), key);
        Assert.Equal(PluginSecretName.MaxLength, longest.Length);

        var hashed = PluginSecretName.Build(new string('a', 59), key);

        Assert.StartsWith("_h", hashed, StringComparison.Ordinal);
        Assert.Equal("_h".Length + 64, hashed.Length);
        Assert.Equal(hashed, PluginSecretName.Build(new string('a', 59), key));
        AssertStoreCompatible(hashed);
        Assert.False(PluginSecretName.TryParse(hashed, out _, out _));

        // The legacy-format collision stays separated in the hashed fallback.
        Assert.NotEqual(
            PluginSecretName.Build(new string('a', 59), "bar." + new string('k', 60)),
            PluginSecretName.Build(new string('a', 59) + ".bar", new string('k', 60)));
    }

    [Fact]
    public void LegacyNamesBeyondTheStoreLimitAreAbsentInsteadOfThrown()
    {
        var key = new string('k', 64);

        Assert.Null(PluginSecretName.BuildLegacy(new string('a', 64), key));
        Assert.Null(PluginSecretName.BuildLegacy(new string('a', 65), key));
        Assert.Equal(
            PluginSecretName.MaxLength,
            PluginSecretName.BuildLegacy(new string('a', 63), key)!.Length);
    }

    [Theory]
    [InlineData("", "apiKey")]
    [InlineData("com.foo", "")]
    [InlineData("com/foo", "apiKey")]
    [InlineData("com.foo", "api key")]
    [InlineData("com.foo", "api:key")]
    [InlineData(".com.foo", "apiKey")]
    [InlineData("-com.foo", "apiKey")]
    [InlineData("_com.foo", "apiKey")]
    [InlineData("com.föö", "apiKey")]
    public void InvalidPartsThrowInsteadOfProducingAnUnusableName(string pluginId, string fieldKey)
    {
        Assert.Throws<ArgumentException>(() => PluginSecretName.Build(pluginId, fieldKey));
        Assert.Throws<ArgumentException>(() => PluginSecretName.BuildLegacy(pluginId, fieldKey));
    }

    [Fact]
    public void LegacyNamesUseTheOldFormatAndAreNotMistakenForCurrentNames()
    {
        var legacy = PluginSecretName.BuildLegacy("com.foo", "bar.apiKey");
        var current = PluginSecretName.Build("com.foo", "bar.apiKey");

        Assert.Equal("com.foo.bar.apiKey", legacy);
        Assert.Equal("_p7-com.foo.bar.apiKey", current);
        Assert.NotEqual(legacy, current);
        Assert.True(PluginSecretName.IsValidName(legacy));
        Assert.False(PluginSecretName.TryParse(legacy, out _, out _));

        // A legacy name starts with the plugin id, which is always a letter or
        // digit, while a current name starts with the reserved "_p" prefix.
        Assert.True(char.IsAsciiLetterOrDigit(legacy![0]));
        Assert.StartsWith("_p", current, StringComparison.Ordinal);
        Assert.False(PluginSecretName.TryParse(
            PluginSecretName.BuildLegacy("p7-com.foo", "bar"),
            out _,
            out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("apiKey")]
    [InlineData("com.foo.apiKey")]
    [InlineData("_p")]
    [InlineData("_p-")]
    [InlineData("_p7-")]
    [InlineData("_p7-com.foo")]
    [InlineData("_p7-com.foo.")]
    [InlineData("_p07-com.foo.x")]
    [InlineData("_p7com.foo.x")]
    [InlineData("_p7-com.foo.x/y")]
    [InlineData("_P7-com.foo.x")]
    [InlineData("_p1--.x")]
    public void ParserRejectsLegacyAndMalformedNames(string? name)
    {
        Assert.False(PluginSecretName.TryParse(name, out var pluginId, out var fieldKey));
        Assert.Empty(pluginId);
        Assert.Empty(fieldKey);
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("com.foo.apiKey", true)]
    [InlineData("_p7-com.foo.bar", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("api key", false)]
    [InlineData("api/key", false)]
    public void IsValidNameMatchesTheSecretStoreAlphabet(string? name, bool expected)
    {
        Assert.Equal(expected, PluginSecretName.IsValidName(name));
    }

    [Fact]
    public void IsValidNameRejectsNamesLongerThanTheSecretStoreLimit()
    {
        var tooLong = new string('a', PluginSecretName.MaxLength + 1);

        Assert.False(PluginSecretName.IsValidName(tooLong));
        Assert.True(PluginSecretName.IsValidName(tooLong[1..]));
    }

    private static void AssertStoreCompatible(string name)
    {
        Assert.InRange(name.Length, 1, PluginSecretName.MaxLength);
        Assert.All(name, character => Assert.True(
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-',
            $"'{character}' is outside the secret-name alphabet."));
    }
}

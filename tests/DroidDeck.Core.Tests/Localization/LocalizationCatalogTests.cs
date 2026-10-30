using System.Reflection;
using DroidDeck.Core.Localization;

namespace DroidDeck.Core.Tests.Localization;

public sealed class LocalizationCatalogTests
{
    [Fact]
    public void EveryKeyConstantHasAnEnglishString()
    {
        var missing = KeyConstants()
            .Where(key => !LocalizationCatalog.English.ContainsKey(key))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "Missing English strings for: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheCatalogContainsOnlyKnownKeyConstants()
    {
        var constants = KeyConstants().ToHashSet(StringComparer.Ordinal);
        var unknown = LocalizationCatalog.English.Keys
            .Where(key => !constants.Contains(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unknown.Length == 0,
            "Catalog keys without a LocalizationKeys constant: " + string.Join(", ", unknown));
    }

    [Fact]
    public void KeysAreUniqueDotSeparatedIdentifiers()
    {
        var constants = KeyConstants();
        var duplicates = constants
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        Assert.Empty(duplicates);
        Assert.All(constants, static key =>
        {
            Assert.Contains(".", key, StringComparison.Ordinal);
            Assert.DoesNotContain(" ", key, StringComparison.Ordinal);
            Assert.Equal(key.ToLowerInvariant(), key);
        });
    }

    [Fact]
    public void EnglishStringsAreNonEmptyAndTrimmed()
    {
        foreach (var (key, value) in LocalizationCatalog.English)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"{key} is empty.");
            Assert.Equal(value.Trim(), value);
        }
    }

    private static string[] KeyConstants() =>
        typeof(LocalizationKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .ToArray();
}

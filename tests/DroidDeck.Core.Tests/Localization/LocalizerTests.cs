using System.Globalization;
using DroidDeck.Core.Localization;

namespace DroidDeck.Core.Tests.Localization;

public sealed class LocalizerTests
{
    private static readonly CultureInfo Arabic = CultureInfo.GetCultureInfo("ar-SA");

    [Fact]
    public void EnglishIsTheDefaultCatalog()
    {
        var localizer = new Localizer("english");

        Assert.Equal("Home", localizer.Get(LocalizationKeys.NavHome));
        Assert.False(localizer.IsPseudoLocale);
        Assert.False(localizer.IsRightToLeft);
        Assert.Equal("en-US", localizer.Language);
    }

    [Fact]
    public void SystemCultureDrivesTextDirection()
    {
        var localizer = new Localizer("system", Arabic);

        Assert.Equal("ar-SA", localizer.Language);
        Assert.True(localizer.IsRightToLeft);
        Assert.False(localizer.IsPseudoLocale);
        Assert.Equal("Home", localizer.Get(LocalizationKeys.NavHome));
    }

    [Fact]
    public void ExplicitEnglishStaysLeftToRightOnAnArabicSystem()
    {
        var localizer = new Localizer("english", Arabic);

        Assert.False(localizer.IsRightToLeft);
        Assert.Equal("en-US", localizer.Language);
    }

    [Fact]
    public void PseudoLocaleExpandsEveryEnglishStringAndKeepsPlaceholders()
    {
        var localizer = new Localizer("pseudo");

        Assert.True(localizer.IsPseudoLocale);
        Assert.Equal(Localizer.PseudoLocaleName, localizer.Language);
        Assert.False(localizer.IsRightToLeft);

        foreach (var (key, english) in LocalizationCatalog.English)
        {
            var pseudo = localizer.Get(key);

            Assert.NotEqual(english, pseudo);
            Assert.StartsWith(PseudoLocalizer.Prefix, pseudo, StringComparison.Ordinal);
            Assert.EndsWith(PseudoLocalizer.Suffix, pseudo, StringComparison.Ordinal);
            Assert.Equal(Placeholders(english), Placeholders(pseudo));
            if (english.Any(static character => char.IsAsciiLetter(character)))
            {
                Assert.Contains(pseudo, static character => character > 0x7F);
            }

            if (english.Length >= 4)
            {
                Assert.True(
                    pseudo.Length >= (int)Math.Ceiling(english.Length * 1.3),
                    $"{key}: '{pseudo}' is not at least 30% longer than '{english}'.");
            }
        }
    }

    [Fact]
    public void PseudoFormattingPreservesArgumentsAndPlaceholders()
    {
        var localizer = new Localizer("pseudo");

        var text = localizer.Format(LocalizationKeys.ShellStatusDeviceMany, 4);

        Assert.Contains("4", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{0}", text, StringComparison.Ordinal);
        Assert.StartsWith(PseudoLocalizer.Prefix, text, StringComparison.Ordinal);
    }

    [Fact]
    public void ResourcesArePrefixedForXamlLookup()
    {
        var localizer = new Localizer("english");

        Assert.Equal(LocalizationCatalog.English.Count, localizer.Resources.Count);
        Assert.All(localizer.Resources, pair => Assert.StartsWith("Loc.", pair.Key, StringComparison.Ordinal));
        Assert.Equal(
            localizer.Get(LocalizationKeys.NavHome),
            localizer.Resources[Localizer.ResourceKey(LocalizationKeys.NavHome)]);
    }

    [Fact]
    public void MissingKeysFailLoudly()
    {
        var localizer = new Localizer("english");

        var exception = Assert.Throws<KeyNotFoundException>(() => localizer.Get("shell.missing.key"));

        Assert.Contains("shell.missing.key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DynamicFallbacksAreNotPseudoLocalized()
    {
        var localizer = new Localizer("pseudo");

        Assert.Equal("Plugin command", localizer.GetOrFallback("command.plugin.title", "Plugin command"));
    }

    [Fact]
    public void UpdatingThePreferenceSwapsTheCatalogAndRaisesChanged()
    {
        var localizer = new Localizer("english");
        var changes = 0;
        localizer.Changed += (_, _) => changes++;

        localizer.Update("pseudo", Arabic);

        Assert.Equal(1, changes);
        Assert.True(localizer.IsPseudoLocale);
        Assert.False(localizer.IsRightToLeft);
        Assert.StartsWith(PseudoLocalizer.Prefix, localizer.Get(LocalizationKeys.NavHome), StringComparison.Ordinal);

        localizer.Update("system", Arabic);

        Assert.Equal(2, changes);
        Assert.True(localizer.IsRightToLeft);
        Assert.Equal("Home", localizer.Get(LocalizationKeys.NavHome));
    }

    [Fact]
    public void PseudoTransformLeavesEmptyTextAlone()
    {
        Assert.Equal(string.Empty, PseudoLocalizer.Transform(string.Empty));
        Assert.Equal(" ", PseudoLocalizer.Transform(" "));
    }

    private static string[] Placeholders(string value)
    {
        var placeholders = new List<string>();
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '{')
            {
                continue;
            }

            var end = value.IndexOf('}', index + 1);
            if (end < 0)
            {
                continue;
            }

            var inner = value.AsSpan(index + 1, end - index - 1);
            if (inner.Length > 0 && inner.ToString().All(char.IsAsciiDigit))
            {
                placeholders.Add(value[index..(end + 1)]);
            }

            index = end;
        }

        return [.. placeholders];
    }
}

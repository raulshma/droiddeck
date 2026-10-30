using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DroidDeck.Architecture.Tests;

public sealed partial class LocalizationArchitectureTests
{
    private static readonly string[] ShellXamlFiles =
    [
        Path.Combine("src", "DroidDeck.App", "MainWindow.xaml"),
        Path.Combine("src", "DroidDeck.App", "Controls", "CommandPaletteOverlay.xaml"),
    ];

    [Fact]
    public void EveryXamlLocReferenceExistsInTheEnglishCatalog()
    {
        var catalog = LoadEnglishCatalog();
        var references = new List<(string File, string Key)>();
        foreach (var file in EnumerateXamlFiles())
        {
            foreach (Match match in LocReference().Matches(File.ReadAllText(file)))
            {
                references.Add((file, match.Groups["key"].Value["Loc.".Length..]));
            }
        }

        Assert.NotEmpty(references);
        var missing = references
            .Where(reference => !catalog.ContainsKey(reference.Key))
            .Select(reference => $"{Path.GetFileName(reference.File)}: {reference.Key}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Empty(missing);
    }

    [Fact]
    public void PageTitlesAreLocalizedLevelOneHeadingsThatWrap()
    {
        var titles = new List<(string File, XElement Title)>();
        foreach (var file in EnumeratePageXamlFiles())
        {
            var document = XDocument.Load(file);
            foreach (var element in document.Descendants())
            {
                if (element.Name.LocalName == "TextBlock" &&
                    Attribute(element, "Style")?.Contains(
                        "TitleTextBlockStyle",
                        StringComparison.Ordinal) == true)
                {
                    titles.Add((file, element));
                }
            }
        }

        Assert.NotEmpty(titles);
        foreach (var (file, title) in titles)
        {
            var name = Path.GetFileName(file);
            var text = Attribute(title, "Text");
            Assert.NotNull(text);
            Assert.StartsWith("{StaticResource Loc.", text, StringComparison.Ordinal);
            Assert.True(
                string.Equals(
                    Attribute(title, "AutomationProperties.HeadingLevel"),
                    "Level1",
                    StringComparison.Ordinal),
                $"{name}: the page title must be a Level1 heading.");
            Assert.True(
                string.Equals(Attribute(title, "TextWrapping"), "Wrap", StringComparison.Ordinal),
                $"{name}: the page title must wrap.");
            Assert.True(
                text.Contains("Loc.page.", StringComparison.Ordinal),
                $"{name}: the page title does not use a page title catalog key.");
        }
    }

    [Fact]
    public void NavigationAndShellActionsAreLocalizedAndKeyboardReachable()
    {
        var document = XDocument.Load(Path.Combine(
            RepositoryLayout.Root,
            "src",
            "DroidDeck.App",
            "MainWindow.xaml"));
        var navigationItems = document
            .Descendants()
            .Where(element => element.Name.LocalName == "NavigationViewItem")
            .ToArray();
        Assert.Equal(14, navigationItems.Length);
        Assert.All(navigationItems, item =>
        {
            Assert.StartsWith(
                "{StaticResource Loc.shell.nav.",
                Attribute(item, "Content"),
                StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(Attribute(item, "AccessKey")));
        });

        var root = document.Root?.Elements().Single(element => element.Name.LocalName == "Grid");
        Assert.NotNull(root);
        Assert.Equal("Cycle", Attribute(root, "TabFocusNavigation"));

        var buttons = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Button" &&
                Attribute(element, "AutomationProperties.AutomationId") is
                    "InstallApkButton" or "FinishSetupButton" or "CopyDiagnosticsButton")
            .ToArray();
        Assert.Equal(3, buttons.Length);
        Assert.All(buttons, button =>
        {
            Assert.StartsWith(
                "{StaticResource Loc.shell.action.",
                Attribute(button, "Content"),
                StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(Attribute(button, "AccessKey")));
        });

        var liveRegions = document
            .Descendants()
            .Count(element => Attribute(element, "AutomationProperties.LiveSetting") is not null);
        Assert.True(liveRegions >= 4, "The shell status and notification surfaces must announce changes.");

        var navigation = document
            .Descendants()
            .Single(element => element.Name.LocalName == "NavigationView");
        Assert.StartsWith(
            "{StaticResource Loc.shell.navigation.name}",
            Attribute(navigation, "AutomationProperties.Name"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShellAndPaletteXamlKeepNoHardcodedUserFacingText()
    {
        foreach (var relativePath in ShellXamlFiles)
        {
            var document = XDocument.Load(Path.Combine(RepositoryLayout.Root, relativePath));
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var name = attribute.Name.LocalName;
                    if (name is not ("Text" or "Content" or "PlaceholderText" or "Header" or "ToolTip"))
                    {
                        continue;
                    }

                    var value = attribute.Value.Trim();
                    if (value.Length == 0 ||
                        value.StartsWith('{') ||
                        string.Equals(value, "DroidDeck", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Assert.Fail(
                        $"{Path.GetFileName(relativePath)}: <{element.Name.LocalName} {name}=\"{value}\"> " +
                        "shows hardcoded text instead of a Loc resource.");
                }
            }
        }
    }

    [Fact]
    public void ShellCodeRoutesStatusMessagesThroughTheLocalizer()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryLayout.Root,
            "src",
            "DroidDeck.App",
            "MainWindow.xaml.cs"));
        Assert.DoesNotContain("ShowStatusMessage(\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowStatusMessage($\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationBar.Title = $\"", source, StringComparison.Ordinal);
        Assert.Contains("_localizer.IsRightToLeft", source, StringComparison.Ordinal);
        Assert.Contains("ReloadLocalization", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePseudoLocaleLanguageOptionIsReachableFromSettings()
    {
        var settings = File.ReadAllText(Path.Combine(
            RepositoryLayout.Root,
            "src",
            "DroidDeck.App",
            "Views",
            "SettingsPage.xaml"));
        Assert.Contains("{StaticResource Loc.settings.language.pseudo}", settings, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"LanguageSelector\"", settings, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> LoadEnglishCatalog()
    {
        using var stream = File.OpenRead(Path.Combine(
            RepositoryLayout.Root,
            "src",
            "DroidDeck.Core",
            "Localization",
            "en-US.json"));
        using var document = JsonDocument.Parse(stream);
        return document.RootElement
            .GetProperty("strings")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString() ?? string.Empty,
                StringComparer.Ordinal);
    }

    private static IEnumerable<string> EnumerateXamlFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(RepositoryLayout.Root, "src", "DroidDeck.App"),
            "*.xaml",
            SearchOption.AllDirectories);

    private static IEnumerable<string> EnumeratePageXamlFiles() =>
        Directory.EnumerateFiles(
            Path.Combine(RepositoryLayout.Root, "src", "DroidDeck.App", "Views"),
            "*.xaml",
            SearchOption.TopDirectoryOnly);

    private static string? Attribute(XElement element, string localName) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;

    [GeneratedRegex(@"\{StaticResource\s+(?<key>Loc\.[^}]+)\}")]
    private static partial Regex LocReference();
}

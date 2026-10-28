using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DroidDeck.Projects.Detection;

/// <summary>Extracts the application label and launcher activity from an Android manifest.</summary>
internal static partial class AndroidManifestParser
{
    private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

    public static async Task<ManifestInfo> ParseAsync(string manifestPath, CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            return Parse(text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new ManifestInfo();
        }
    }

    public static ManifestInfo Parse(string text)
    {
        try
        {
            var document = XDocument.Parse(text);
            var manifest = document.Root;
            var application = manifest?.Element("application");
            if (application is null)
            {
                return new ManifestInfo();
            }

            var label = application.Attribute(XName.Get("label", AndroidNamespace))?.Value;
            string? launcher = null;
            foreach (var activity in application.Elements("activity"))
            {
                var hasLauncher = activity
                    .Elements("intent-filter")
                    .Any(filter =>
                        filter.Elements("action").Any(action =>
                            string.Equals(
                                action.Attribute(XName.Get("name", AndroidNamespace))?.Value,
                                "android.intent.action.MAIN",
                                StringComparison.Ordinal)) &&
                        filter.Elements("category").Any(category =>
                            string.Equals(
                                category.Attribute(XName.Get("name", AndroidNamespace))?.Value,
                                "android.intent.category.LAUNCHER",
                                StringComparison.Ordinal)));
                if (!hasLauncher)
                {
                    continue;
                }

                var name = activity.Attribute(XName.Get("name", AndroidNamespace))?.Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    launcher = name;
                    break;
                }
            }

            return new ManifestInfo
            {
                ApplicationLabel = label,
                LauncherActivity = launcher,
            };
        }
        catch (System.Xml.XmlException)
        {
            // Manifest XML may contain unescaped placeholders before a build. Fall back to text matching.
            var launcherMatch = LauncherFallbackRegex().Match(text);
            var labelMatch = LabelFallbackRegex().Match(text);
            return new ManifestInfo
            {
                LauncherActivity = launcherMatch.Success ? launcherMatch.Groups["name"].Value : null,
                ApplicationLabel = labelMatch.Success ? labelMatch.Groups["label"].Value : null,
            };
        }
    }

    [GeneratedRegex(
        @"<activity[^>]*android:name\s*=\s*[""'](?<name>[^""']+)[""'][^>]*>(?:(?!</activity>).)*?android\.intent\.category\.LAUNCHER",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex LauncherFallbackRegex();

    [GeneratedRegex(
        @"<application[^>]*android:label\s*=\s*[""'](?<label>[^""']+)[""']",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex LabelFallbackRegex();
}

/// <summary>Manifest values relevant to detection.</summary>
internal sealed class ManifestInfo
{
    public string? LauncherActivity { get; init; }

    public string? ApplicationLabel { get; init; }
}

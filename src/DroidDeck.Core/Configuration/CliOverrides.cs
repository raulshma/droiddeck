using System.Text.Json;
using System.Text.Json.Nodes;

namespace DroidDeck.Core.Configuration;

/// <summary>The result of parsing command-line overrides.</summary>
/// <param name="Settings">Dotted configuration paths paired with raw values.</param>
/// <param name="ProfileId">The profile requested with <c>--profile</c>, when present.</param>
public sealed record CliOverrideSet(IReadOnlyList<KeyValuePair<string, string>> Settings, string? ProfileId)
{
    /// <summary>Gets an empty override set.</summary>
    public static CliOverrideSet Empty { get; } = new([], null);
}

/// <summary>
/// Parses the <c>--set</c> and <c>--profile</c> command-line options and
/// <c>DROIDDECK__SECTION__KEY</c> environment variables into session-layer overrides.
/// </summary>
public static class CliOverrides
{
    private const string EnvironmentPrefix = "DROIDDECK__";

    /// <summary>Parses command-line arguments. Unrecognized arguments are ignored.</summary>
    public static CliOverrideSet ParseArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var settings = new List<KeyValuePair<string, string>>();
        string? profileId = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--set", StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Count)
            {
                var pair = arguments[++index];
                var separator = pair.IndexOf('=');
                if (separator > 0)
                {
                    settings.Add(new KeyValuePair<string, string>(
                        pair[..separator].Trim(),
                        pair[(separator + 1)..].Trim()));
                }
            }
            else if (string.Equals(argument, "--profile", StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < arguments.Count)
            {
                profileId = arguments[++index].Trim();
            }
        }

        return new CliOverrideSet(settings, profileId);
    }

    /// <summary>
    /// Extracts <c>DROIDDECK__SECTION__KEY</c> overrides from an environment snapshot;
    /// each double underscore becomes a path segment.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> ParseEnvironment(
        IEnumerable<KeyValuePair<string, string?>> environment,
        string prefix = EnvironmentPrefix)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var settings = new List<KeyValuePair<string, string>>();
        foreach (var (name, value) in environment)
        {
            if (value is null || !name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var segments = name[prefix.Length..]
                .Split("__", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            var path = string.Join('.', segments.Select(static segment => segment.ToLowerInvariant()));
            settings.Add(new KeyValuePair<string, string>(path, value));
        }

        return settings;
    }

    /// <summary>Converts a raw override value into JSON; text that is not JSON becomes a string.</summary>
    public static JsonNode? ParseValue(string raw)
    {
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return JsonValue.Create(raw);
        }
    }
}

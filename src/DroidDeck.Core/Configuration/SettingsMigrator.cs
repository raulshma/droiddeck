using System.Text.Json.Nodes;

namespace DroidDeck.Core.Configuration;

/// <summary>
/// Applies forward-safe schema migrations to versioned configuration documents.
/// Steps are keyed by the version they upgrade from and run in ascending order until
/// the target version is reached; keys a step does not touch are preserved.
/// </summary>
public sealed class SettingsMigrator
{
    private static readonly IReadOnlyDictionary<int, Action<JsonObject>> BuiltInSteps =
        new Dictionary<int, Action<JsonObject>>
        {
            // Version 0 documents predate the schema stamp; onboarding defaulted to complete.
            [0] = document =>
            {
                if (document.ContainsKey("onboarding"))
                {
                    return;
                }

                document["onboarding"] = new JsonObject { ["completed"] = true };
            },
        };

    /// <summary>Gets the shared migrator used for all application configuration files.</summary>
    public static SettingsMigrator Default { get; } = new();

    private readonly IReadOnlyDictionary<int, Action<JsonObject>> _steps;
    private readonly int _targetVersion;

    /// <summary>Creates a migrator, optionally replacing the built-in steps or the target version.</summary>
    public SettingsMigrator(
        IReadOnlyDictionary<int, Action<JsonObject>>? steps = null,
        int? targetVersion = null)
    {
        _steps = steps ?? BuiltInSteps;
        _targetVersion = targetVersion ?? AppSettings.CurrentSchemaVersion;
    }

    /// <summary>Gets the version this migrator upgrades documents to.</summary>
    public int TargetVersion => _targetVersion;

    /// <summary>
    /// Applies every migration between <paramref name="fromVersion"/> and the target version,
    /// stamps the document with the target version, and returns it.
    /// </summary>
    public int Migrate(JsonObject document, int fromVersion)
    {
        for (var version = Math.Max(fromVersion, 0); version < _targetVersion; version++)
        {
            if (_steps.TryGetValue(version, out var step))
            {
                step(document);
            }
        }

        document["schemaVersion"] = _targetVersion;
        return _targetVersion;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Projects;

namespace DroidDeck.Core.Configuration;

/// <summary>
/// Projects a <see cref="RunConfiguration"/> into the dotted override paths used by
/// the run-configuration layer of the configuration service.
/// </summary>
public static class RunConfigurationOverrides
{
    /// <summary>Returns the settings a run configuration overrides while it is active.</summary>
    public static JsonObject Project(RunConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new JsonObject
        {
            ["run"] = new JsonObject
            {
                ["configurationId"] = configuration.Id,
                ["configurationName"] = configuration.Name,
                ["kind"] = Json(configuration.Kind),
                ["variantName"] = configuration.VariantName,
            },
            ["build"] = new JsonObject
            {
                ["task"] = configuration.Build.Task,
                ["clean"] = configuration.Build.Clean,
                ["skip"] = configuration.Build.Skip,
                ["timeoutSeconds"] = configuration.Build.TimeoutSeconds,
                ["javaHome"] = configuration.Build.JavaHome,
                ["targetFramework"] = configuration.Build.TargetFramework,
            },
            ["install"] = new JsonObject
            {
                ["replace"] = configuration.Install.Replace,
                ["grantPermissions"] = configuration.Install.GrantPermissions,
            },
            ["launch"] = new JsonObject
            {
                ["packageName"] = configuration.Launch.PackageName,
                ["activity"] = configuration.Launch.Activity,
            },
            ["logs"] = new JsonObject
            {
                ["maxLines"] = configuration.Logs.MaxLines,
                ["clearBeforeLaunch"] = configuration.Logs.ClearBeforeLaunch,
            },
            ["screen"] = new JsonObject
            {
                ["mode"] = Json(configuration.Screen.Mode),
                ["autoOpen"] = configuration.Screen.AutoOpen,
            },
        };
    }

    private static JsonNode? Json<T>(T value) where T : notnull =>
        JsonSerializer.SerializeToNode(value, DroidDeckJson.Options);
}

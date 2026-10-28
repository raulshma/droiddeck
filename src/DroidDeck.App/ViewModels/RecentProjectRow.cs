using DroidDeck.Core.Projects;

namespace DroidDeck.App.ViewModels;

/// <summary>A recently used project shaped for the home cockpit list.</summary>
/// <param name="Id">The project identifier.</param>
/// <param name="Name">The project name.</param>
/// <param name="Subtitle">The kind, configuration count, and last opened time.</param>
/// <param name="CanRun">Whether a run configuration exists.</param>
/// <param name="RunId">The run button automation identifier.</param>
/// <param name="OpenId">The open button automation identifier.</param>
public sealed record RecentProjectRow(
    string Id,
    string Name,
    string Subtitle,
    bool CanRun,
    string RunId,
    string OpenId)
{
    /// <summary>Creates a row from a project.</summary>
    public static RecentProjectRow From(ProjectModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var configurations = project.RunConfigurations.Count;
        var configurationText = configurations switch
        {
            0 => "no run configuration",
            1 => "1 configuration",
            _ => $"{configurations} configurations",
        };
        var opened = project.LastOpenedAt is { } lastOpened
            ? $"opened {RelativeTime.Ago(lastOpened)}"
            : "never opened";
        return new RecentProjectRow(
            project.Id,
            project.Name,
            $"{project.KindLabel} · {configurationText} · {opened}",
            configurations > 0,
            AutomationId.For("HomeProjectRun", project.Id),
            AutomationId.For("HomeProjectOpen", project.Id));
    }
}

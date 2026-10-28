using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Core.Runs;

/// <summary>Why a quick run cannot start yet.</summary>
public enum QuickRunBlock
{
    /// <summary>A project, configuration, and ready device are available.</summary>
    None,

    /// <summary>No project has been added.</summary>
    NoProjects,

    /// <summary>The selected project has no run configuration.</summary>
    NoConfigurations,

    /// <summary>No connected device is ready to accept an install.</summary>
    NoReadyDevices,
}

/// <summary>The project, configuration, and device a quick run will use.</summary>
public sealed class QuickRunSelection
{
    /// <summary>Gets or sets the resolved project, when one exists.</summary>
    public ProjectModel? Project { get; init; }

    /// <summary>Gets or sets the resolved run configuration, when one exists.</summary>
    public RunConfiguration? Configuration { get; init; }

    /// <summary>Gets or sets the resolved device, when one is ready.</summary>
    public DeviceInfo? Device { get; init; }

    /// <summary>Gets or sets what prevents the quick run from starting.</summary>
    public QuickRunBlock Block { get; init; } = QuickRunBlock.None;

    /// <summary>Gets whether every part of the run is resolved.</summary>
    public bool CanRun => Block == QuickRunBlock.None &&
                          Project is not null &&
                          Configuration is not null &&
                          Device is not null;
}

/// <summary>
/// Remembers the last quick run target and resolves it against the projects and
/// devices that are currently available, so the home cockpit can start a run in
/// one click after a restart.
/// </summary>
public sealed class QuickRunMemory
{
    private readonly IConfigurationService _configuration;
    private readonly ILogger<QuickRunMemory> _logger;

    /// <summary>Creates the quick run memory.</summary>
    public QuickRunMemory(IConfigurationService configuration, ILogger<QuickRunMemory>? logger = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? NullLogger<QuickRunMemory>.Instance;
    }

    /// <summary>Resolves the quick run target from remembered and live state.</summary>
    public QuickRunSelection Resolve(
        IReadOnlyList<ProjectModel> projects,
        IReadOnlyList<DeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(devices);

        var remembered = _configuration.Current.Settings.Home;
        var project = FindProject(projects, remembered.LastProjectId)
                      ?? projects
                          .OrderByDescending(project => project.LastOpenedAt ?? DateTimeOffset.MinValue)
                          .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                          .FirstOrDefault();
        if (project is null)
        {
            return new QuickRunSelection { Block = QuickRunBlock.NoProjects };
        }

        var configuration = FindConfiguration(project, remembered.LastConfigurationId)
                            ?? project.RunConfigurations.FirstOrDefault(candidate => candidate.IsDefault)
                            ?? project.RunConfigurations.FirstOrDefault();
        if (configuration is null)
        {
            return new QuickRunSelection
            {
                Project = project,
                Block = QuickRunBlock.NoConfigurations,
            };
        }

        var ready = devices.Where(device => device.State == DeviceState.Device).ToArray();
        var device = FindDevice(ready, remembered.LastDeviceSerial)
                     ?? FindDevice(ready, configuration.DeviceSerial)
                     ?? ready.FirstOrDefault();
        return device is null
            ? new QuickRunSelection
            {
                Project = project,
                Configuration = configuration,
                Block = QuickRunBlock.NoReadyDevices,
            }
            : new QuickRunSelection
            {
                Project = project,
                Configuration = configuration,
                Device = device,
            };
    }

    /// <summary>Remembers a quick run target for the next launch.</summary>
    public async Task RememberAsync(
        string? projectId,
        string? configurationId,
        string? deviceSerial,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _configuration
                .SetAsync(SettingsPaths.HomeLastProjectId, NullIfEmpty(projectId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await _configuration
                .SetAsync(
                    SettingsPaths.HomeLastConfigurationId,
                    NullIfEmpty(configurationId),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await _configuration
                .SetAsync(
                    SettingsPaths.HomeLastDeviceSerial,
                    NullIfEmpty(deviceSerial),
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            // A quick run still works when the remembered target cannot be saved.
            _logger.LogWarning(exception, "The quick run target could not be remembered.");
        }
    }

    private static ProjectModel? FindProject(IReadOnlyList<ProjectModel> projects, string? projectId) =>
        string.IsNullOrWhiteSpace(projectId)
            ? null
            : projects.FirstOrDefault(project =>
                string.Equals(project.Id, projectId, StringComparison.Ordinal));

    private static RunConfiguration? FindConfiguration(ProjectModel project, string? configurationId) =>
        string.IsNullOrWhiteSpace(configurationId)
            ? null
            : project.RunConfigurations.FirstOrDefault(configuration =>
                string.Equals(configuration.Id, configurationId, StringComparison.Ordinal));

    private static DeviceInfo? FindDevice(IReadOnlyList<DeviceInfo> devices, string? serial) =>
        string.IsNullOrWhiteSpace(serial)
            ? null
            : devices.FirstOrDefault(device =>
                string.Equals(device.Serial, serial, StringComparison.Ordinal));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

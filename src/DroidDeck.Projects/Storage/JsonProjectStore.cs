using System.Text.Json;
using DroidDeck.Core;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Projects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Projects.Storage;

/// <summary>Stores one JSON document per project under the application data directory.</summary>
public sealed class JsonProjectStore : IProjectStore
{
    private readonly string _directory;
    private readonly ILogger<JsonProjectStore> _logger;

    /// <summary>Creates a store under the supplied data paths.</summary>
    public JsonProjectStore(AppPaths paths, ILogger<JsonProjectStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = paths.ProjectsDirectory;
        _logger = logger ?? NullLogger<JsonProjectStore>.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectModel>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var projects = new List<ProjectModel>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var project = DroidDeckJson.Deserialize<ProjectModel>(json);
                if (project is not null)
                {
                    projects.Add(project);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogWarning(exception, "The project file {File} could not be read.", file);
            }
        }

        return projects
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<ProjectModel?> GetAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var path = GetPath(projectId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return DroidDeckJson.Deserialize<ProjectModel>(json);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "The project {ProjectId} could not be read.", projectId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ProjectModel project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(project.Id);
        Directory.CreateDirectory(_directory);
        var path = GetPath(project.Id);
        await AtomicFile.WriteAllTextAsync(path, DroidDeckJson.Serialize(project), keepBackup: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var path = GetPath(projectId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string GetPath(string projectId) => Path.Combine(_directory, Safe(projectId) + ".json");

    private static string Safe(string value)
    {
        var safe = new string(value
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_')
            .ToArray());
        return safe.Length == 0 ? "project" : safe;
    }
}

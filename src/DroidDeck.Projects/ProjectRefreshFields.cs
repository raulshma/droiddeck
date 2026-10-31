using DroidDeck.Core.Projects;

namespace DroidDeck.Projects;

/// <summary>
/// Describes one project field that refresh can compare and apply. The reader produces the value
/// shown in a <see cref="ProjectFieldChange"/>; the writer copies the detected model into the target.
/// </summary>
internal abstract class ProjectRefreshField
{
    protected ProjectRefreshField(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    internal string Name { get; }

    internal abstract string? Read(ProjectModel project);

    internal abstract void Apply(ProjectModel target, ProjectModel detected);
}

/// <summary>Typed implementation of a <see cref="ProjectRefreshField"/>.</summary>
internal sealed class ProjectRefreshField<T> : ProjectRefreshField
{
    private readonly Func<ProjectModel, T> _reader;
    private readonly Action<ProjectModel, ProjectModel> _writer;
    private readonly Func<T, string?>? _format;

    internal ProjectRefreshField(
        string name,
        Func<ProjectModel, T> reader,
        Action<ProjectModel, ProjectModel> writer,
        Func<T, string?>? format = null)
        : base(name)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _format = format;
    }

    internal override string? Read(ProjectModel project)
    {
        var value = _reader(project);
        return _format is null ? Format(value) : _format(value);
    }

    internal override void Apply(ProjectModel target, ProjectModel detected) => _writer(target, detected);

    private static string? Format(T value) => value is null ? null : value.ToString();
}

/// <summary>
/// The single table of fields that project refresh compares and applies. This keeps the emitted
/// field names, their display values, and their writers from drifting apart.
/// </summary>
internal static class ProjectRefreshFields
{
    private static readonly ProjectRefreshField[] Fields =
    [
        new ProjectRefreshField<ProjectKind>(
            "kind",
            project => project.Kind,
            (target, detected) =>
            {
                target.Kind = detected.Kind;
                target.KindLabel = detected.KindLabel;
            }),
        new ProjectRefreshField<string?>(
            "applicationId",
            project => project.ApplicationId,
            (target, detected) => target.ApplicationId = detected.ApplicationId),
        new ProjectRefreshField<string?>(
            "namespace",
            project => project.Namespace,
            (target, detected) => target.Namespace = detected.Namespace),
        new ProjectRefreshField<string?>(
            "launcherActivity",
            project => project.LauncherActivity,
            (target, detected) => target.LauncherActivity = detected.LauncherActivity),
        new ProjectRefreshField<string?>(
            "applicationLabel",
            project => project.ApplicationLabel,
            (target, detected) => target.ApplicationLabel = detected.ApplicationLabel),
        new ProjectRefreshField<string?>(
            "applicationModule",
            project => project.ApplicationModule,
            (target, detected) => target.ApplicationModule = detected.ApplicationModule),
        new ProjectRefreshField<string?>(
            "wrapperVersion",
            project => project.WrapperVersion,
            (target, detected) => target.WrapperVersion = detected.WrapperVersion),
        new ProjectRefreshField<string?>(
            "agpVersion",
            project => project.AgpVersion,
            (target, detected) => target.AgpVersion = detected.AgpVersion),
        new ProjectRefreshField<string?>(
            "kotlinVersion",
            project => project.KotlinVersion,
            (target, detected) => target.KotlinVersion = detected.KotlinVersion),
        new ProjectRefreshField<bool>(
            "isKotlinMultiplatform",
            project => project.IsKotlinMultiplatform,
            (target, detected) => target.IsKotlinMultiplatform = detected.IsKotlinMultiplatform),
        new ProjectRefreshField<bool>(
            "isComposeMultiplatform",
            project => project.IsComposeMultiplatform,
            (target, detected) => target.IsComposeMultiplatform = detected.IsComposeMultiplatform),
        new ProjectRefreshField<List<ProjectModule>>(
            "modules",
            project => project.Modules,
            (target, detected) => target.Modules = detected.Modules,
            modules => string.Join(", ", modules.Select(module => $"{module.Name} ({module.Type})"))),
        new ProjectRefreshField<List<ProjectVariant>>(
            "variants",
            project => project.Variants,
            (target, detected) => target.Variants = detected.Variants,
            variants => string.Join(", ", variants.Select(variant => variant.Name))),
        new ProjectRefreshField<List<ApkFileReference>>(
            "apkFiles",
            project => project.ApkFiles,
            (target, detected) => target.ApkFiles = detected.ApkFiles,
            files => string.Join(", ", files.Select(reference => Path.GetFileName(reference.Path)))),
    ];

    /// <summary>Gets every refreshable field in comparison order.</summary>
    internal static IReadOnlyList<ProjectRefreshField> All => Fields;

    /// <summary>Compares two project models and returns the changed fields.</summary>
    internal static List<ProjectFieldChange> Compare(ProjectModel stored, ProjectModel detected)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(detected);
        var changes = new List<ProjectFieldChange>();
        foreach (var field in Fields)
        {
            var oldValue = field.Read(stored);
            var newValue = field.Read(detected);
            if (string.Equals(oldValue ?? string.Empty, newValue ?? string.Empty, StringComparison.Ordinal))
            {
                continue;
            }

            changes.Add(new ProjectFieldChange
            {
                Field = field.Name,
                OldValue = oldValue,
                NewValue = newValue,
                Kind = string.IsNullOrEmpty(oldValue) ? "added" : string.IsNullOrEmpty(newValue) ? "removed" : "updated",
            });
        }

        return changes;
    }

    /// <summary>Applies the named fields from <paramref name="detected"/> to <paramref name="target"/>.</summary>
    internal static void Apply(ProjectModel target, ProjectModel detected, IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(detected);
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var name in fields)
        {
            Find(name)?.Apply(target, detected);
        }
    }

    private static ProjectRefreshField? Find(string name) =>
        Array.Find(Fields, field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));
}

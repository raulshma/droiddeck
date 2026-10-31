namespace DroidDeck.Projects.Detection;

/// <summary>
/// Probes the Android host folder shared by the Flutter, React Native, Capacitor, and Cordova
/// detectors: the application module's Gradle build file, its application ID and namespace, and
/// the application manifest.
/// </summary>
internal static class AndroidHostProbe
{
    /// <summary>
    /// Probes the Android host at <paramref name="androidFolder"/> beneath <paramref name="rootPath"/>.
    /// </summary>
    /// <param name="rootPath">The absolute project root.</param>
    /// <param name="androidFolder">The host folder relative to the root, such as <c>android</c> or <c>platforms/android</c>.</param>
    /// <param name="cancellationToken">A token that cancels file reads.</param>
    /// <returns>The host facts; <see cref="AndroidHostFacts.HasAndroidHost"/> is false when the folder is missing.</returns>
    internal static async Task<AndroidHostFacts> ProbeAsync(
        string rootPath,
        string androidFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(androidFolder);
        cancellationToken.ThrowIfCancellationRequested();

        var androidDirectory = Path.Combine(rootPath, androidFolder);
        if (!Directory.Exists(androidDirectory))
        {
            return AndroidHostFacts.Missing(androidFolder);
        }

        var buildFile = ProjectSource.FindBuildFile(Path.Combine(androidDirectory, "app"));
        var buildText = await ProjectSource.ReadTextAsync(buildFile, cancellationToken).ConfigureAwait(false);
        var applicationId = ProjectSource.MatchValue(ProjectSource.ApplicationIdPattern(), buildText);
        var namespaceName = ProjectSource.MatchValue(ProjectSource.NamespacePattern(), buildText);

        var manifestPath = Path.Combine(androidDirectory, "app", "src", "main", "AndroidManifest.xml");
        var manifest = File.Exists(manifestPath)
            ? await AndroidManifestParser.ParseAsync(manifestPath, cancellationToken).ConfigureAwait(false)
            : new ManifestInfo();

        return new AndroidHostFacts
        {
            AndroidFolder = androidFolder,
            HasAndroidHost = true,
            AndroidDirectory = androidDirectory,
            BuildFile = buildFile,
            BuildFileRelativePath = buildFile is null ? null : Path.GetRelativePath(rootPath, buildFile),
            ApplicationId = applicationId,
            Namespace = namespaceName,
            LauncherActivity = manifest.LauncherActivity,
            ApplicationLabel = manifest.ApplicationLabel,
        };
    }
}

/// <summary>The Android host facts the cross-platform detectors consume.</summary>
internal sealed class AndroidHostFacts
{
    /// <summary>Creates facts for a project without an Android host folder.</summary>
    /// <param name="androidFolder">The host folder that was probed, relative to the project root.</param>
    /// <returns>Facts with <see cref="HasAndroidHost"/> set to false.</returns>
    internal static AndroidHostFacts Missing(string androidFolder) => new() { AndroidFolder = androidFolder };

    /// <summary>Gets the host folder relative to the project root, using forward slashes.</summary>
    internal string AndroidFolder { get; init; } = "android";

    /// <summary>Gets whether the host folder exists.</summary>
    internal bool HasAndroidHost { get; init; }

    /// <summary>Gets the absolute host folder, or <see langword="null"/> when it is missing.</summary>
    internal string? AndroidDirectory { get; init; }

    /// <summary>Gets the absolute Gradle build file of the application module, or <see langword="null"/>.</summary>
    internal string? BuildFile { get; init; }

    /// <summary>Gets the build file path relative to the project root as reported by the file system, or <see langword="null"/>.</summary>
    internal string? BuildFileRelativePath { get; init; }

    /// <summary>Gets the Gradle <c>applicationId</c> value, or <see langword="null"/>.</summary>
    internal string? ApplicationId { get; init; }

    /// <summary>Gets the Gradle <c>namespace</c> value, or <see langword="null"/>.</summary>
    internal string? Namespace { get; init; }

    /// <summary>Gets the application ID the Gradle file declares, falling back to its namespace.</summary>
    internal string? GradleApplicationId => ApplicationId ?? Namespace;

    /// <summary>Gets the launcher activity declared by the manifest, or <see langword="null"/>.</summary>
    internal string? LauncherActivity { get; init; }

    /// <summary>Gets the application label declared by the manifest, or <see langword="null"/>.</summary>
    internal string? ApplicationLabel { get; init; }

    /// <summary>Gets the build file path relative to the project root with forward slashes, or <see langword="null"/>.</summary>
    internal string? BuildFileModulePath => BuildFileRelativePath?.Replace('\\', '/');

    /// <summary>Gets the manifest path used as evidence, relative to the project root.</summary>
    internal string ManifestSource => $"{AndroidFolder.TrimEnd('/')}/app/src/main/AndroidManifest.xml";

    /// <summary>
    /// Returns the build file path used as evidence, or <paramref name="missingBuildFileSource"/> when
    /// the application module has no Gradle build file.
    /// </summary>
    /// <param name="missingBuildFileSource">The source label the detector uses for a missing build file.</param>
    /// <returns>The evidence source for the Android host identity.</returns>
    internal string BuildFileEvidenceSource(string missingBuildFileSource) =>
        BuildFileRelativePath ?? missingBuildFileSource;
}

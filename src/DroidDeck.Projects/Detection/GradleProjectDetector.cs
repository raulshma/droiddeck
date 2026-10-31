using System.Text;
using System.Text.RegularExpressions;
using DroidDeck.Core.Projects;

namespace DroidDeck.Projects.Detection;

/// <summary>
/// Detects Android Gradle and Kotlin Multiplatform projects from marker files,
/// settings scripts, build scripts, version catalogs, and manifests. The detector
/// never invokes Gradle.
/// </summary>
public sealed partial class GradleProjectDetector : IProjectDetector
{
    /// <inheritdoc />
    public string Id => "gradle";

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public async Task<ProjectDetectionResult?> DetectAsync(
        ProjectScanContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var root = context.RootPath;
        var settingsFile = ProjectSource.FindFirst(root, "settings.gradle.kts", "settings.gradle");
        var rootBuildFile = ProjectSource.FindBuildFile(root);
        var wrapperFile = ProjectSource.FindFirst(root, "gradlew.bat", "gradlew");
        if (settingsFile is null && (rootBuildFile is null || wrapperFile is null))
        {
            return null;
        }

        var project = new ProjectModel
        {
            Id = ProjectModel.CreateId(root),
            Name = new DirectoryInfo(root).Name,
            RootPath = root,
            RepositoryRoot = context.RepositoryRoot,
            DetectedAt = DateTimeOffset.UtcNow,
            DetectionVersion = 1,
        };

        var evidence = new List<DetectionEvidence>();
        var warnings = new List<string>();
        var settingsText = await ProjectSource.ReadTextAsync(settingsFile, cancellationToken).ConfigureAwait(false);
        var moduleDirectories = ParseModuleDirectories(settingsText);
        if (settingsFile is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "gradle-settings",
                Source = Path.GetFileName(settingsFile),
                Detail = "A Gradle settings script marks the project root.",
                Confidence = DetectionConfidence.High,
            });
        }

        if (rootBuildFile is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "gradle-build",
                Source = Path.GetFileName(rootBuildFile),
                Detail = "A root Gradle build script was found.",
                Confidence = DetectionConfidence.Medium,
            });
        }

        if (wrapperFile is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "gradle-wrapper",
                Source = Path.GetFileName(wrapperFile),
                Detail = "The Gradle wrapper makes builds reproducible without a global Gradle install.",
                Confidence = DetectionConfidence.Medium,
            });
        }

        var wrapperProperties = Path.Combine(root, "gradle", "wrapper", "gradle-wrapper.properties");
        if (File.Exists(wrapperProperties))
        {
            var wrapperText = await ProjectSource.ReadTextAsync(wrapperProperties, cancellationToken).ConfigureAwait(false);
            project.WrapperVersion = wrapperText is null ? null : ParseWrapperVersion(wrapperText);
            if (project.WrapperVersion is not null)
            {
                evidence.Add(new DetectionEvidence
                {
                    Id = "gradle-wrapper-version",
                    Source = "gradle-wrapper.properties",
                    Detail = $"The Gradle wrapper version is {project.WrapperVersion}.",
                    Snippet = project.WrapperVersion,
                    Confidence = DetectionConfidence.High,
                });
            }
        }

        var versionCatalog = Path.Combine(root, "gradle", "libs.versions.toml");
        var catalogText = File.Exists(versionCatalog)
            ? await ProjectSource.ReadTextAsync(versionCatalog, cancellationToken).ConfigureAwait(false)
            : null;
        var catalog = catalogText is null ? null : VersionCatalog.Parse(catalogText);
        if (catalog is not null)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "version-catalog",
                Source = "gradle/libs.versions.toml",
                Detail = "A Gradle version catalog supplies plugin versions.",
                Confidence = DetectionConfidence.Medium,
            });
        }

        var rootBuildText = await ProjectSource.ReadTextAsync(rootBuildFile, cancellationToken).ConfigureAwait(false);
        var modules = new List<ProjectModule>();
        var moduleBuildTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (moduleName, relativePath) in moduleDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var moduleDirectory = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var buildFile = ProjectSource.FindBuildFile(moduleDirectory);
            if (buildFile is null)
            {
                continue;
            }

            var buildText = await ProjectSource.ReadTextAsync(buildFile, cancellationToken).ConfigureAwait(false);
            if (buildText is null)
            {
                continue;
            }

            var module = ParseModule(moduleName, relativePath, buildFile, buildText, root);
            modules.Add(module);
            moduleBuildTexts[module.Path] = buildText;
            if (module.IsKotlinMultiplatform)
            {
                evidence.Add(new DetectionEvidence
                {
                    Id = $"kmp-{module.Name}",
                    Source = Path.GetRelativePath(root, buildFile),
                    Detail = $"Module {module.Name} applies the Kotlin Multiplatform plugin.",
                    Confidence = DetectionConfidence.High,
                });
            }

            if (module.Type == ProjectModuleType.Application)
            {
                evidence.Add(new DetectionEvidence
                {
                    Id = $"android-app-{module.Name}",
                    Source = Path.GetRelativePath(root, buildFile),
                    Detail = $"Module {module.Name} applies the Android application plugin.",
                    Confidence = DetectionConfidence.High,
                });
            }

            if (!string.IsNullOrEmpty(module.ApplicationId))
            {
                evidence.Add(new DetectionEvidence
                {
                    Id = $"application-id-{module.Name}",
                    Source = Path.GetRelativePath(root, buildFile),
                    Detail = $"Module {module.Name} declares applicationId {module.ApplicationId}.",
                    Snippet = module.ApplicationId,
                    Confidence = DetectionConfidence.High,
                });
            }
        }

        if (modules.Count == 0)
        {
            if (rootBuildText is not null &&
                (ContainsPlugin(rootBuildText, "com.android.application") ||
                 ContainsPlugin(rootBuildText, "com.android.library")))
            {
                var rootModule = ParseModule(":", ".", rootBuildFile!, rootBuildText, root);
                modules.Add(rootModule);
                moduleBuildTexts[rootModule.Path] = rootBuildText;
            }
        }

        var applicationModule = modules.FirstOrDefault(module => module.Type == ProjectModuleType.Application);
        var isKmp = modules.Any(module => module.IsKotlinMultiplatform);
        var isComposeMultiplatform = ContainsPlugin(rootBuildText, "org.jetbrains.compose") ||
                                     modules.Any(module => module.IsComposeMultiplatform);
        if (isComposeMultiplatform)
        {
            evidence.Add(new DetectionEvidence
            {
                Id = "compose-multiplatform",
                Source = "build scripts",
                Detail = "The Compose Multiplatform plugin is applied.",
                Confidence = DetectionConfidence.High,
            });
        }

        project.Kind = isKmp ? ProjectKind.KotlinMultiplatform : ProjectKind.AndroidGradle;
        project.KindLabel = ProjectModel.GetKindLabel(project.Kind);
        project.IsKotlinMultiplatform = isKmp;
        project.IsComposeMultiplatform = isComposeMultiplatform;
        project.Modules = modules;
        project.ApplicationModule = applicationModule?.Name;
        project.ApplicationId = applicationModule?.ApplicationId;
        project.Namespace = applicationModule?.Namespace;
        project.AgpVersion = catalog?.AgpVersion ?? ParsePluginVersion(rootBuildText, "com.android.application") ??
            modules.Select(module => module.AgpVersion).FirstOrDefault(version => version is not null);
        project.KotlinVersion = catalog?.KotlinVersion ??
            ParsePluginVersion(rootBuildText, "org.jetbrains.kotlin.android") ??
            modules.Select(module => module.KotlinVersion).FirstOrDefault(version => version is not null);

        foreach (var module in modules)
        {
            if (project.AgpVersion is null && module.AgpVersion is not null)
            {
                project.AgpVersion = module.AgpVersion;
            }

            if (project.KotlinVersion is null && module.KotlinVersion is not null)
            {
                project.KotlinVersion = module.KotlinVersion;
            }
        }

        if (applicationModule is not null)
        {
            var manifestPath = FindManifest(applicationModule, root);
            if (manifestPath is not null)
            {
                var manifest = await AndroidManifestParser.ParseAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                project.LauncherActivity = manifest.LauncherActivity;
                project.ApplicationLabel = manifest.ApplicationLabel;
                if (project.LauncherActivity is not null)
                {
                    evidence.Add(new DetectionEvidence
                    {
                        Id = "launcher-activity",
                        Source = Path.GetRelativePath(root, manifestPath),
                        Detail = $"The launcher activity is {project.LauncherActivity}.",
                        Snippet = project.LauncherActivity,
                        Confidence = DetectionConfidence.Medium,
                    });
                }
            }
        }

        project.Variants = BuildVariants(modules, moduleBuildTexts);
        project.Capabilities = ProjectCapability.Build | ProjectCapability.Variants | ProjectCapability.Tests;
        if (project.Variants.Count > 0)
        {
            project.Capabilities |= ProjectCapability.Install | ProjectCapability.Launch;
        }

        if (isKmp)
        {
            project.Capabilities |= ProjectCapability.MultiDevice;
        }

        if (applicationModule is null)
        {
            warnings.Add("No Android application module was found. Add a run configuration manually or choose a different module.");
            project.Capabilities |= ProjectCapability.Limited;
            project.Limitation = "No application module was detected, so build, install, and launch defaults are incomplete.";
        }

        if (project.LauncherActivity is null && applicationModule is not null)
        {
            warnings.Add("The launcher activity was not found in the manifest. DroidDeck will resolve it after the first build.");
        }

        project.Confidence = applicationModule is not null && project.ApplicationId is not null
            ? DetectionConfidence.High
            : modules.Count > 0
                ? DetectionConfidence.Medium
                : DetectionConfidence.Low;
        project.Evidence = evidence;
        project.Warnings = warnings;
        project.RunConfigurations = CreateDefaultRunConfigurations(project);

        return new ProjectDetectionResult { Project = project };
    }

    private static ProjectModule ParseModule(
        string moduleName,
        string relativePath,
        string buildFile,
        string buildText,
        string root)
    {
        var isKmp = ContainsKmpPlugin(buildText);
        var isApplication = ContainsPlugin(buildText, "com.android.application");
        var isLibrary = ContainsPlugin(buildText, "com.android.library");
        var module = new ProjectModule
        {
            Name = moduleName,
            Path = relativePath.Replace('\\', '/'),
            BuildFile = Path.GetRelativePath(root, buildFile).Replace('\\', '/'),
            IsKotlinMultiplatform = isKmp,
            HasAndroidTarget = isKmp && ContainsAndroidTarget(buildText),
            Type = isApplication
                ? ProjectModuleType.Application
                : isLibrary || isKmp
                    ? ProjectModuleType.Library
                    : ProjectModuleType.Unknown,
            ApplicationId = ProjectSource.MatchValue(ProjectSource.ApplicationIdPattern(), buildText, trim: false),
            Namespace = ProjectSource.MatchValue(ProjectSource.NamespacePattern(), buildText, trim: false),
            IsComposeMultiplatform = ContainsPlugin(buildText, "org.jetbrains.compose"),
            AgpVersion = ParsePluginVersion(buildText, "com.android.application"),
            KotlinVersion = ParsePluginVersion(buildText, "org.jetbrains.kotlin"),
        };

        return module;
    }

    private static List<ProjectVariant> BuildVariants(
        IReadOnlyList<ProjectModule> modules,
        Dictionary<string, string> moduleBuildTexts)
    {
        var variants = new List<ProjectVariant>();
        foreach (var module in modules.Where(module => module.Type == ProjectModuleType.Application))
        {
            moduleBuildTexts.TryGetValue(module.Path, out var buildText);
            var buildTypes = ParseBuildTypes(buildText);
            var flavors = ParseFlavors(buildText);
            var flavorDimensions = flavors.Count == 0 ? new List<string?> { null } : flavors.Cast<string?>().ToList();
            foreach (var flavor in flavorDimensions)
            {
                foreach (var buildType in buildTypes)
                {
                    var name = flavor is null ? buildType : flavor + Capitalize(buildType);
                    var buildTypeFolder = buildType.ToLowerInvariant();
                    var flavorFolder = flavor is null ? string.Empty : flavor.ToLowerInvariant() + "/";
                    var modulePath = module.Path == "." ? string.Empty : module.Path + "/";
                    variants.Add(new ProjectVariant
                    {
                        Name = name,
                        ModuleName = module.Name,
                        BuildType = buildType,
                        Flavors = flavor is null ? [] : [flavor],
                        BuildTask = $"assemble{Capitalize(name)}",
                        ArtifactGlob = $"{modulePath}build/outputs/apk/{flavorFolder}{buildTypeFolder}/*.apk",
                        Source = DetectionSource.Detected,
                        IsDefault = string.Equals(buildType, "debug", StringComparison.OrdinalIgnoreCase) &&
                                    (flavor is null || variants.Count == 0),
                    });
                }
            }
        }

        if (variants.Count > 0 && !variants.Any(variant => variant.IsDefault))
        {
            variants[0].IsDefault = true;
        }

        return variants;
    }

    private static List<string> ParseBuildTypes(string? buildText)
    {
        var names = buildText is null ? new List<string>() : ExtractBlockNames(buildText, "buildTypes");
        names.Add("debug");
        names.Add("release");
        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => name.Length > 0 && !IsBlockKeyword(name))
            .OrderBy(name => name.Equals("debug", StringComparison.OrdinalIgnoreCase)
                ? 0
                : name.Equals("release", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 2)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ParseFlavors(string? buildText)
    {
        if (buildText is null)
        {
            return [];
        }

        return ExtractBlockNames(buildText, "productFlavors")
            .Where(name => name.Length > 0 && !IsBlockKeyword(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static List<string> ExtractBlockNames(string text, string blockName)
    {
        var names = new List<string>();
        var start = text.IndexOf(blockName, StringComparison.Ordinal);
        if (start < 0)
        {
            return names;
        }

        var brace = text.IndexOf('{', start);
        if (brace < 0)
        {
            return names;
        }

        var depth = 0;
        var end = brace;
        for (; end < text.Length; end++)
        {
            if (text[end] == '{')
            {
                depth++;
            }
            else if (text[end] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
        }

        var body = text[(brace + 1)..Math.Min(end, text.Length)];
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            var match = BlockNameRegex().Match(trimmed);
            if (match.Success)
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names;
    }

    private static List<(string Name, string Path)> ParseModuleDirectories(string? settingsText)
    {
        var modules = new List<(string Name, string Path)>();
        if (string.IsNullOrWhiteSpace(settingsText))
        {
            return modules;
        }

        var explicitPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ProjectDirRegex().Matches(settingsText))
        {
            var name = NormalizeModuleName(match.Groups["name"].Value);
            var path = match.Groups["path"].Value.Replace('\\', '/').TrimEnd('/');
            explicitPaths[name] = path;
        }

        foreach (Match match in IncludeRegex().Matches(settingsText))
        {
            foreach (var raw in match.Groups["names"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var name = NormalizeModuleName(raw.Trim().Trim('"', '\''));
                if (name.Length == 0)
                {
                    continue;
                }

                var path = explicitPaths.TryGetValue(name, out var explicitPath)
                    ? explicitPath
                    : name.TrimStart(':').Replace(':', '/');
                if (!modules.Any(module => string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    modules.Add((name, path));
                }
            }
        }

        return modules;
    }

    private static string NormalizeModuleName(string value)
    {
        var name = value.Trim().Trim('"', '\'').Replace('\\', '/');
        if (name.Length == 0)
        {
            return name;
        }

        return name.StartsWith(':') ? name : ":" + name.Replace('/', ':');
    }

    private static string? FindManifest(ProjectModule module, string root)
    {
        var moduleDirectory = Path.Combine(root, module.Path.Replace('/', Path.DirectorySeparatorChar));
        var mainManifest = Path.Combine(moduleDirectory, "src", "main", "AndroidManifest.xml");
        if (File.Exists(mainManifest))
        {
            return mainManifest;
        }

        var sourceRoot = Path.Combine(moduleDirectory, "src");
        if (!Directory.Exists(sourceRoot))
        {
            return null;
        }

        return Directory.EnumerateFiles(sourceRoot, "AndroidManifest.xml", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static List<RunConfiguration> CreateDefaultRunConfigurations(ProjectModel project)
    {
        var configurations = new List<RunConfiguration>();
        var applicationModules = project.Modules
            .Where(module => module.Type == ProjectModuleType.Application)
            .Select(module => module.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in project.Variants)
        {
            if (variant.ModuleName is null || !applicationModules.Contains(variant.ModuleName))
            {
                continue;
            }

            var configuration = new RunConfiguration
            {
                Id = $"gradle-{Sanitize(variant.Name)}",
                Name = variant.Name,
                Kind = RunConfigurationKind.Gradle,
                VariantName = variant.Name,
                ModuleName = variant.ModuleName,
                IsDefault = variant.IsDefault,
                Build = new BuildConfiguration
                {
                    Task = variant.BuildTask ?? $"assemble{Capitalize(variant.Name)}",
                },
                Artifact = new ArtifactConfiguration
                {
                    Glob = variant.ArtifactGlob ?? string.Empty,
                    PackageName = project.ApplicationId,
                },
                Launch = new LaunchConfiguration
                {
                    PackageName = project.ApplicationId,
                    Activity = project.LauncherActivity,
                },
            };
            configurations.Add(configuration);
        }

        if (configurations.Count == 0)
        {
            configurations.Add(new RunConfiguration
            {
                Id = "gradle-default",
                Name = "Debug",
                Kind = RunConfigurationKind.Gradle,
                Build = new BuildConfiguration { Task = "assembleDebug" },
                Artifact = new ArtifactConfiguration { Glob = "**/build/outputs/apk/debug/*.apk" },
                Launch = new LaunchConfiguration { PackageName = project.ApplicationId },
                IsDefault = true,
            });
        }

        return configurations;
    }

    private static bool ContainsPlugin(string? text, string plugin)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.Contains(plugin, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsKmpPlugin(string text) =>
        text.Contains("org.jetbrains.kotlin.multiplatform", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("kotlin(\"multiplatform\")", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("kotlin('multiplatform')", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAndroidTarget(string text) =>
        text.Contains("androidTarget", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("android()", StringComparison.OrdinalIgnoreCase);

    private static string? ParseWrapperVersion(string text)
    {
        var match = WrapperVersionRegex().Match(text);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static string? ParsePluginVersion(string? text, string plugin)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var escaped = Regex.Escape(plugin);
        var match = Regex.Match(
            text,
            $@"(?:id\s*\(\s*[""']{escaped}[""']\s*\)|[""']{escaped}[""'])\s*(?:version\s*)?[""'](?<version>\d+[^""']*)[""']",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static bool IsBlockKeyword(string value) =>
        value is "getByName" or "create" or "maybeCreate" or "named" or "register" or "configureEach";

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        return builder.ToString().Trim('-');
    }

    [GeneratedRegex(@"include\s*\(?\s*(?<names>[^)\r\n]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IncludeRegex();

    [GeneratedRegex(
        @"project\s*\(\s*[""'](?<name>[^""']+)[""']\s*\)\s*\.projectDir\s*=\s*(?:file|File)\s*\(\s*[""'](?<path>[^""']+)[""']",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProjectDirRegex();

    [GeneratedRegex(
        @"^\s*(?:(?:getByName|create|maybeCreate|named|register)\s*\(\s*[""'](?<name>[^""']+)[""']|(?<name>[A-Za-z_][\w]*)\s*\{)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlockNameRegex();

    [GeneratedRegex(
        @"gradle-(?<version>\d+(?:\.\d+)+)-(?:bin|all)\.zip",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex WrapperVersionRegex();

    private sealed class VersionCatalog
    {
        public string? AgpVersion { get; private init; }

        public string? KotlinVersion { get; private init; }

        public static VersionCatalog Parse(string text)
        {
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? agp = null;
            string? kotlin = null;
            var section = string.Empty;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                var key = line[..equals].Trim().Trim('"', '\'');
                var value = line[(equals + 1)..].Trim().Trim(',');
                if (section.Equals("versions", StringComparison.OrdinalIgnoreCase))
                {
                    versions[key] = value.Trim('"', '\'');
                }
                else if (section.Equals("plugins", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Contains("com.android.application", StringComparison.OrdinalIgnoreCase))
                    {
                        agp = ResolveVersion(value, versions);
                    }
                    else if (value.Contains("org.jetbrains.kotlin", StringComparison.OrdinalIgnoreCase) &&
                             !value.Contains("compose", StringComparison.OrdinalIgnoreCase))
                    {
                        kotlin = ResolveVersion(value, versions);
                    }
                }
            }

            return new VersionCatalog { AgpVersion = agp, KotlinVersion = kotlin };
        }

        private static string? ResolveVersion(string value, Dictionary<string, string> versions)
        {
            var reference = Regex.Match(
                value,
                @"version\.ref\s*=\s*[""'](?<key>[^""']+)[""']",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            if (reference.Success && versions.TryGetValue(reference.Groups["key"].Value, out var resolved))
            {
                return resolved;
            }

            var literal = Regex.Match(
                value,
                @"version\s*=\s*[""'](?<version>[^""']+)[""']",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            return literal.Success ? literal.Groups["version"].Value : null;
        }
    }
}

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
        var pluginResolver = await GradlePluginResolver
            .LoadAsync(root, settingsText, cancellationToken)
            .ConfigureAwait(false);
        var moduleDirectories = ParseModuleDirectories(
            settingsText is null ? null : ProjectSource.StripLineComments(settingsText));
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
        if (File.Exists(versionCatalog))
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

            var module = ParseModule(moduleName, relativePath, buildFile, buildText, root, pluginResolver);
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

        var rootApplied = pluginResolver.Expand(pluginResolver.ParseUses(rootBuildText));
        if (modules.Count == 0 && rootBuildFile is not null && rootBuildText is not null &&
            (rootApplied.Contains(GradlePluginResolver.AndroidApplicationPlugin) ||
             rootApplied.Contains(GradlePluginResolver.AndroidLibraryPlugin) ||
             rootApplied.Contains(GradlePluginResolver.KotlinMultiplatformPlugin) ||
             rootApplied.Contains(GradlePluginResolver.AndroidKmpLibraryPlugin)))
        {
            var rootModule = ParseModule(":", ".", rootBuildFile, rootBuildText, root, pluginResolver);
            modules.Add(rootModule);
            moduleBuildTexts[rootModule.Path] = rootBuildText;
        }

        var applicationModule = modules.FirstOrDefault(module => module.Type == ProjectModuleType.Application);
        var isKmp = modules.Any(module => module.IsKotlinMultiplatform);
        var isComposeMultiplatform = rootApplied.Contains(GradlePluginResolver.ComposeMultiplatformPlugin) ||
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
        project.ApplicationId = applicationModule?.ApplicationId ?? applicationModule?.Namespace;
        project.Namespace = applicationModule?.Namespace;
        project.AgpVersion = pluginResolver.AgpVersion ??
            modules.Select(module => module.AgpVersion).FirstOrDefault(version => version is not null) ??
            ParsePluginVersion(rootBuildText, GradlePluginResolver.AndroidApplicationPlugin) ??
            ParsePluginVersion(rootBuildText, GradlePluginResolver.AndroidLibraryPlugin);
        project.KotlinVersion = pluginResolver.KotlinVersion ??
            modules.Select(module => module.KotlinVersion).FirstOrDefault(version => version is not null) ??
            ParsePluginVersion(rootBuildText, GradlePluginResolver.KotlinMultiplatformPlugin) ??
            ParsePluginVersion(rootBuildText, "org.jetbrains.kotlin.android");

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
        string root,
        GradlePluginResolver pluginResolver)
    {
        var uses = pluginResolver.ParseUses(buildText);
        var applied = pluginResolver.Expand(uses);
        var moduleDirectory = Path.GetDirectoryName(buildFile) ?? root;
        var isKmpLibrary = applied.Contains(GradlePluginResolver.AndroidKmpLibraryPlugin);
        var isKmp = isKmpLibrary ||
                    applied.Contains(GradlePluginResolver.KotlinMultiplatformPlugin) ||
                    Directory.Exists(Path.Combine(moduleDirectory, "src", "commonMain"));
        var isApplication = applied.Contains(GradlePluginResolver.AndroidApplicationPlugin);
        var isLibrary = applied.Contains(GradlePluginResolver.AndroidLibraryPlugin) || isKmpLibrary;
        var applicationId = ProjectSource.MatchValue(
            ProjectSource.ApplicationIdPattern(),
            ProjectSource.ExtractBlockBody(buildText, "defaultConfig") ?? buildText,
            trim: true);
        if (!IsValidApplicationId(applicationId))
        {
            applicationId = null;
        }

        if (!isApplication && !isLibrary && applicationId is not null)
        {
            // A declared application ID is the remaining signal when a convention plugin
            // applies com.android.application outside the module script.
            isApplication = true;
        }

        return new ProjectModule
        {
            Name = moduleName,
            Path = relativePath.Replace('\\', '/'),
            BuildFile = Path.GetRelativePath(root, buildFile).Replace('\\', '/'),
            IsKotlinMultiplatform = isKmp,
            HasAndroidTarget = isKmp &&
                               (isKmpLibrary ||
                                ContainsKmpAndroidBlock(buildText) ||
                                ContainsAndroidTarget(buildText) ||
                                Directory.Exists(Path.Combine(moduleDirectory, "src", "androidMain"))),
            Type = isApplication
                ? ProjectModuleType.Application
                : isLibrary || isKmp
                    ? ProjectModuleType.Library
                    : ProjectModuleType.Unknown,
            ApplicationId = applicationId,
            Namespace = ProjectSource.MatchValue(ProjectSource.NamespacePattern(), buildText, trim: true),
            IsComposeMultiplatform = applied.Contains(GradlePluginResolver.ComposeMultiplatformPlugin),
            AgpVersion = FirstPluginVersion(
                uses,
                pluginResolver,
                GradlePluginResolver.AndroidApplicationPlugin,
                GradlePluginResolver.AndroidLibraryPlugin,
                GradlePluginResolver.AndroidKmpLibraryPlugin),
            KotlinVersion = FirstPluginVersion(
                uses,
                pluginResolver,
                GradlePluginResolver.KotlinMultiplatformPlugin,
                "org.jetbrains.kotlin.android",
                "org.jetbrains.kotlin.jvm"),
        };
    }

    private static string? FirstPluginVersion(
        IReadOnlyList<GradlePluginUse> uses,
        GradlePluginResolver pluginResolver,
        params string[] pluginIds)
    {
        foreach (var pluginId in pluginIds)
        {
            var declared = uses.FirstOrDefault(use =>
                string.Equals(use.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            var version = declared?.Version ?? pluginResolver.GetVersion(pluginId);
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        return null;
    }

    private static List<ProjectVariant> BuildVariants(
        IReadOnlyList<ProjectModule> modules,
        Dictionary<string, string> moduleBuildTexts)
    {
        var variants = new List<ProjectVariant>();
        foreach (var module in modules.Where(module => module.Type == ProjectModuleType.Application))
        {
            moduleBuildTexts.TryGetValue(module.Path, out var buildText);
            var text = buildText is null ? null : ProjectSource.StripLineComments(buildText);
            var buildTypes = ParseBuildTypes(text);
            var flavors = ParseFlavors(text);
            if (flavors.Count == 0)
            {
                flavors.Add(new VariantBlock(null, null, null, false));
            }

            var defaultFlavor = flavors
                .FirstOrDefault(flavor => flavor.IsDefaultFlavor && flavor.Name is not null)
                ?.Name;
            var hasDefault = variants.Any(variant => variant.IsDefault);
            var baseId = module.ApplicationId ?? module.Namespace;
            var modulePath = module.Path == "." ? string.Empty : module.Path + "/";
            var taskPrefix = module.Name is ":" or "" ? string.Empty : module.Name + ":";
            foreach (var flavor in flavors)
            {
                foreach (var buildType in buildTypes)
                {
                    var buildTypeName = buildType.Name!;
                    var name = flavor.Name is null
                        ? buildTypeName
                        : flavor.Name + Capitalize(buildTypeName);
                    var flavorFolder = flavor.Name is null ? string.Empty : flavor.Name.ToLowerInvariant() + "/";
                    var isDefault = !hasDefault &&
                                    string.Equals(buildTypeName, "debug", StringComparison.OrdinalIgnoreCase) &&
                                    (defaultFlavor is null ||
                                     string.Equals(flavor.Name, defaultFlavor, StringComparison.OrdinalIgnoreCase));
                    hasDefault |= isDefault;
                    variants.Add(new ProjectVariant
                    {
                        Name = name,
                        ModuleName = module.Name,
                        BuildType = buildTypeName,
                        Flavors = flavor.Name is null ? [] : [flavor.Name],
                        BuildTask = $"{taskPrefix}assemble{Capitalize(name)}",
                        ArtifactGlob =
                            $"{modulePath}build/outputs/apk/{flavorFolder}{buildTypeName.ToLowerInvariant()}/*.apk",
                        ApplicationId = ComposeApplicationId(baseId, flavor, buildType),
                        Source = DetectionSource.Detected,
                        IsDefault = isDefault,
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

    private static string? ComposeApplicationId(
        string? baseId,
        VariantBlock flavor,
        VariantBlock buildType)
    {
        var applicationId = flavor.ApplicationId ?? baseId;
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return null;
        }

        return applicationId +
               (flavor.ApplicationIdSuffix ?? string.Empty) +
               (buildType.ApplicationIdSuffix ?? string.Empty);
    }

    private static List<VariantBlock> ParseBuildTypes(string? text)
    {
        var blocks = ParseVariantBlocks(text, "buildTypes")
            .Where(block => block.Name is not null && !IsBlockKeyword(block.Name))
            .ToList();
        foreach (var implicitType in new[] { "debug", "release" })
        {
            if (!blocks.Any(block => string.Equals(block.Name, implicitType, StringComparison.OrdinalIgnoreCase)))
            {
                blocks.Add(new VariantBlock(implicitType, null, null, false));
            }
        }

        return blocks
            .OrderBy(block => string.Equals(block.Name, "debug", StringComparison.OrdinalIgnoreCase)
                ? 0
                : string.Equals(block.Name, "release", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 2)
            .ThenBy(block => block.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<VariantBlock> ParseFlavors(string? text) =>
        ParseVariantBlocks(text, "productFlavors")
            .Where(block => block.Name is not null && !IsBlockKeyword(block.Name))
            .ToList();

    private static List<VariantBlock> ParseVariantBlocks(string? text, string blockName)
    {
        var blocks = new List<VariantBlock>();
        var body = ProjectSource.ExtractBlockBody(text, blockName);
        if (body is null)
        {
            return blocks;
        }

        var depth = 0;
        var sawBrace = false;
        string? name = null;
        var blockText = new StringBuilder();
        foreach (var rawLine in body.Split('\n'))
        {
            if (depth == 0 && name is null)
            {
                var match = BlockNameRegex().Match(rawLine.Trim());
                if (!match.Success)
                {
                    continue;
                }

                name = match.Groups["name"].Value;
                sawBrace = false;
                blockText.Clear();
            }

            blockText.AppendLine(rawLine);
            foreach (var character in rawLine)
            {
                if (character == '{')
                {
                    depth++;
                    sawBrace = true;
                }
                else if (character == '}')
                {
                    depth--;
                }
            }

            if (name is not null && sawBrace && depth == 0)
            {
                blocks.Add(CreateVariantBlock(name, blockText.ToString()));
                name = null;
                blockText.Clear();
            }
        }

        return blocks;
    }

    private static VariantBlock CreateVariantBlock(string name, string text)
    {
        var applicationId = ProjectSource.MatchValue(
            ProjectSource.ApplicationIdPattern(),
            text,
            trim: true);
        var suffix = ProjectSource.MatchValue(
            ProjectSource.ApplicationIdSuffixPattern(),
            text,
            trim: true);
        return new VariantBlock(
            name,
            IsValidApplicationId(applicationId) ? applicationId : null,
            IsValidApplicationIdSuffix(suffix) ? suffix : null,
            VariantDefaultRegex().IsMatch(text));
    }

    private static bool IsValidApplicationId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ApplicationIdValidationRegex().IsMatch(value);

    private static bool IsValidApplicationIdSuffix(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ApplicationIdSuffixValidationRegex().IsMatch(value);

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
        var qualifyNames = applicationModules.Count > 1;
        foreach (var variant in project.Variants)
        {
            if (variant.ModuleName is null || !applicationModules.Contains(variant.ModuleName))
            {
                continue;
            }

            var packageName = variant.ApplicationId ?? project.ApplicationId;
            var configuration = new RunConfiguration
            {
                Id = CreateVariantConfigurationId(variant),
                Name = qualifyNames
                    ? $"{ModuleDisplayName(variant.ModuleName)} {variant.Name}"
                    : variant.Name,
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
                    PackageName = packageName,
                },
                Launch = new LaunchConfiguration
                {
                    PackageName = packageName,
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

    private static bool ContainsAndroidTarget(string text) =>
        text.Contains("androidTarget", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("android()", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsKmpAndroidBlock(string text) =>
        ProjectSource.ExtractBlockBody(text, "kotlin") is { } kotlinBlock &&
        KmpAndroidBlockRegex().IsMatch(kotlinBlock);

    private static string CreateVariantConfigurationId(ProjectVariant variant)
    {
        var parts = new[] { variant.ModuleName, variant.Name }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => Sanitize(part!))
            .Where(part => part.Length > 0);
        var suffix = string.Join('-', parts);
        return suffix.Length == 0 ? "gradle-default" : $"gradle-{suffix}";
    }

    private static string ModuleDisplayName(string moduleName) =>
        moduleName.TrimStart(':').Replace(':', '/');

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

    [GeneratedRegex(
        @"\binclude(?!Build|Flat|Group|Module|Version)\s*\(?\s*(?<names>[^)\r\n]+)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
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

    [GeneratedRegex(
        @"^[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z][A-Za-z0-9_]*)+$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ApplicationIdValidationRegex();

    [GeneratedRegex(
        @"^(?:\.[A-Za-z0-9_]+)+$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ApplicationIdSuffixValidationRegex();

    [GeneratedRegex(@"\bandroid\s*\{", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KmpAndroidBlockRegex();

    [GeneratedRegex(@"\bisDefault\s*=?\s*true\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VariantDefaultRegex();

    private sealed record VariantBlock(
        string? Name,
        string? ApplicationId,
        string? ApplicationIdSuffix,
        bool IsDefaultFlavor);
}

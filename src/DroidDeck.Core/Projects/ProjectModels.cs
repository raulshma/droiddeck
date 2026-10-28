using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DroidDeck.Core.Projects;

/// <summary>The kind of project DroidDeck detected.</summary>
public enum ProjectKind
{
    /// <summary>An Android Gradle project.</summary>
    AndroidGradle,

    /// <summary>A Kotlin Multiplatform project with an Android target.</summary>
    KotlinMultiplatform,

    /// <summary>A Flutter project with an Android host folder.</summary>
    Flutter,

    /// <summary>A React Native or Expo project with an Android host folder.</summary>
    ReactNative,

    /// <summary>A .NET MAUI project targeting Android.</summary>
    Maui,

    /// <summary>An Ionic Capacitor project with an Android host folder.</summary>
    Capacitor,

    /// <summary>An Apache Cordova project with an Android host folder.</summary>
    Cordova,

    /// <summary>A prebuilt APK or split APK set.</summary>
    ApkOnly,

    /// <summary>A user-defined project driven by custom steps.</summary>
    Custom,
}

/// <summary>How strongly the evidence supports the detected project.</summary>
public enum DetectionConfidence
{
    /// <summary>Only weak markers were found.</summary>
    Low,

    /// <summary>Markers and some project structure were found.</summary>
    Medium,

    /// <summary>Markers, an application module, and an application ID were found.</summary>
    High,
}

/// <summary>The type of a Gradle module.</summary>
public enum ProjectModuleType
{
    /// <summary>The module type is unknown.</summary>
    Unknown,

    /// <summary>The module produces an application.</summary>
    Application,

    /// <summary>The module produces a library.</summary>
    Library,
}

/// <summary>Features a project supports.</summary>
[Flags]
public enum ProjectCapability
{
    /// <summary>No capabilities are known.</summary>
    None = 0,

    /// <summary>The project can be built.</summary>
    Build = 1,

    /// <summary>An artifact can be installed on a device.</summary>
    Install = 2,

    /// <summary>An application can be launched.</summary>
    Launch = 4,

    /// <summary>Build variants can be listed.</summary>
    Variants = 8,

    /// <summary>Hot reload is supported.</summary>
    HotReload = 16,

    /// <summary>Tests can be run.</summary>
    Tests = 32,

    /// <summary>The artifact is device independent and can be built once.</summary>
    MultiDevice = 64,

    /// <summary>The workflow is intentionally limited and needs an explanation.</summary>
    Limited = 128,
}

/// <summary>Where a detected or overridden value came from.</summary>
public enum DetectionSource
{
    /// <summary>The value was detected from project files.</summary>
    Detected,

    /// <summary>The user supplied the value.</summary>
    Manual,

    /// <summary>The value came from AGP build output.</summary>
    BuildOutput,

    /// <summary>The value came from aapt2 inspection.</summary>
    ApkInspection,
}

/// <summary>A single piece of evidence supporting a detection.</summary>
public sealed class DetectionEvidence
{
    /// <summary>Gets or sets the stable evidence identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the file or source that produced the evidence.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the human-readable explanation.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional matched text.</summary>
    public string? Snippet { get; set; }

    /// <summary>Gets or sets how strongly this evidence supports the result.</summary>
    public DetectionConfidence Confidence { get; set; } = DetectionConfidence.Medium;
}

/// <summary>A Gradle module and its Android settings.</summary>
public sealed class ProjectModule
{
    /// <summary>Gets or sets the Gradle module name, including a leading colon for nested modules.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the module path relative to the project root.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the module type.</summary>
    public ProjectModuleType Type { get; set; } = ProjectModuleType.Unknown;

    /// <summary>Gets or sets the Android application ID.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>Gets or sets the Android namespace.</summary>
    public string? Namespace { get; set; }

    /// <summary>Gets or sets whether the module uses the Kotlin Multiplatform plugin.</summary>
    public bool IsKotlinMultiplatform { get; set; }

    /// <summary>Gets or sets whether the module targets Android.</summary>
    public bool HasAndroidTarget { get; set; }

    /// <summary>Gets or sets whether the module applies Compose Multiplatform.</summary>
    public bool IsComposeMultiplatform { get; set; }

    /// <summary>Gets or sets the Android Gradle Plugin version declared by the module.</summary>
    public string? AgpVersion { get; set; }

    /// <summary>Gets or sets the Kotlin plugin version declared by the module.</summary>
    public string? KotlinVersion { get; set; }

    /// <summary>Gets or sets the module build file path relative to the project root.</summary>
    public string? BuildFile { get; set; }

    /// <summary>Gets or sets variants detected for this module.</summary>
    public List<ProjectVariant> Variants { get; set; } = [];
}

/// <summary>A build variant of a module.</summary>
public sealed class ProjectVariant
{
    /// <summary>Gets or sets the variant name, such as <c>prodDebug</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the module that owns the variant.</summary>
    public string? ModuleName { get; set; }

    /// <summary>Gets or sets the build type portion of the variant.</summary>
    public string BuildType { get; set; } = "debug";

    /// <summary>Gets or sets the product flavors applied to the variant.</summary>
    public List<string> Flavors { get; set; } = [];

    /// <summary>Gets or sets the build task that produces the variant.</summary>
    public string? BuildTask { get; set; }

    /// <summary>Gets or sets the APK glob for the variant.</summary>
    public string? ArtifactGlob { get; set; }

    /// <summary>Gets or sets where the variant was detected or edited.</summary>
    public DetectionSource Source { get; set; } = DetectionSource.Detected;

    /// <summary>Gets or sets whether this variant is the default selection.</summary>
    public bool IsDefault { get; set; }
}

/// <summary>A persisted project and everything detected about it.</summary>
public sealed class ProjectModel
{
    /// <summary>Gets or sets the stable project identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the absolute project root.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the project kind.</summary>
    public ProjectKind Kind { get; set; } = ProjectKind.Custom;

    /// <summary>Gets or sets the detected stack label shown in the UI.</summary>
    public string KindLabel { get; set; } = "Custom";

    /// <summary>Gets or sets the detection confidence.</summary>
    public DetectionConfidence Confidence { get; set; } = DetectionConfidence.Low;

    /// <summary>Gets or sets the application ID used for launch and logs.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>Gets or sets the namespace of the application module.</summary>
    public string? Namespace { get; set; }

    /// <summary>Gets or sets the launcher activity, when known.</summary>
    public string? LauncherActivity { get; set; }

    /// <summary>Gets or sets the application label, when known.</summary>
    public string? ApplicationLabel { get; set; }

    /// <summary>Gets or sets the Gradle wrapper version.</summary>
    public string? WrapperVersion { get; set; }

    /// <summary>Gets or sets the Android Gradle Plugin version.</summary>
    public string? AgpVersion { get; set; }

    /// <summary>Gets or sets the Kotlin plugin version.</summary>
    public string? KotlinVersion { get; set; }

    /// <summary>Gets or sets whether this is a Kotlin Multiplatform project.</summary>
    public bool IsKotlinMultiplatform { get; set; }

    /// <summary>Gets or sets whether Compose Multiplatform is in use.</summary>
    public bool IsComposeMultiplatform { get; set; }

    /// <summary>Gets or sets the Flutter environment constraint declared by pubspec.yaml.</summary>
    public string? FlutterEnvironment { get; set; }

    /// <summary>Gets or sets the React Native specifics detected from package.json and Expo config.</summary>
    public ReactNativeProjectInfo? ReactNativeInfo { get; set; }

    /// <summary>Gets or sets the .NET MAUI specifics detected from the project file.</summary>
    public MauiProjectInfo? MauiInfo { get; set; }

    /// <summary>Gets or sets the Capacitor specifics detected from package.json and the config.</summary>
    public CapacitorProjectInfo? CapacitorInfo { get; set; }

    /// <summary>Gets or sets the Cordova specifics detected from config.xml and package.json.</summary>
    public CordovaProjectInfo? CordovaInfo { get; set; }

    /// <summary>Gets or sets the application module name.</summary>
    public string? ApplicationModule { get; set; }

    /// <summary>Gets or sets the supported capabilities.</summary>
    public ProjectCapability Capabilities { get; set; } = ProjectCapability.None;

    /// <summary>Gets or sets the modules found in the project.</summary>
    public List<ProjectModule> Modules { get; set; } = [];

    /// <summary>Gets or sets all variants across modules.</summary>
    public List<ProjectVariant> Variants { get; set; } = [];

    /// <summary>Gets or sets the run configurations for the project.</summary>
    public List<RunConfiguration> RunConfigurations { get; set; } = [];

    /// <summary>Gets or sets the evidence that justified detection.</summary>
    public List<DetectionEvidence> Evidence { get; set; } = [];

    /// <summary>Gets or sets warnings that do not block adding the project.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Gets or sets APK files for APK-only projects.</summary>
    public List<ApkFileReference> ApkFiles { get; set; } = [];

    /// <summary>Gets or sets the repository root when this project belongs to a monorepo.</summary>
    public string? RepositoryRoot { get; set; }

    /// <summary>Gets or sets a user-facing explanation when the workflow is limited.</summary>
    public string? Limitation { get; set; }

    /// <summary>Gets or sets the detection schema version used to write this project.</summary>
    public int DetectionVersion { get; set; } = 1;

    /// <summary>Gets or sets when the project was added.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets when the project was last opened.</summary>
    public DateTimeOffset? LastOpenedAt { get; set; }

    /// <summary>Gets or sets when detection last ran.</summary>
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets values the user overrode manually.</summary>
    public Dictionary<string, string> ManualOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets unknown keys preserved for forward compatibility.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? AdditionalData { get; set; }

    /// <summary>Gets the display name for a project kind.</summary>
    public static string GetKindLabel(ProjectKind kind) => kind switch
    {
        ProjectKind.AndroidGradle => "Android Gradle",
        ProjectKind.KotlinMultiplatform => "Kotlin Multiplatform",
        ProjectKind.Flutter => "Flutter",
        ProjectKind.ReactNative => "React Native",
        ProjectKind.Maui => ".NET MAUI",
        ProjectKind.Capacitor => "Ionic Capacitor",
        ProjectKind.Cordova => "Apache Cordova",
        ProjectKind.ApkOnly => "APK only",
        _ => "Custom",
    };

    /// <summary>Creates a stable identifier for a project path.</summary>
    public static string CreateId(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var normalized = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(normalized);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "project";
        }

        var safeName = new string(name
            .ToLowerInvariant()
            .Select(static character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');
        if (safeName.Length == 0)
        {
            safeName = "project";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant()));
        return $"{safeName}-{Convert.ToHexString(hash)[..8].ToLowerInvariant()}";
    }

    /// <summary>Returns a deep-ish copy suitable for editing before persisting.</summary>
    public ProjectModel Clone()
    {
        var clone = DroidDeck.Core.Infrastructure.DroidDeckJson.Deserialize<ProjectModel>(
            DroidDeck.Core.Infrastructure.DroidDeckJson.Serialize(this));
        return clone ?? throw new InvalidOperationException("The project could not be copied.");
    }
}

/// <summary>React Native specifics detected from package.json, Expo config, and the Android host.</summary>
public sealed class ReactNativeProjectInfo
{
    /// <summary>Gets the default Metro development server port.</summary>
    public const int DefaultMetroPort = 8081;

    /// <summary>Gets or sets whether Expo manages the project.</summary>
    public bool IsExpo { get; set; }

    /// <summary>
    /// Gets or sets the workflow: <c>bare</c> for plain React Native, <c>expo-prebuild</c> when Expo
    /// has generated the android folder, or <c>expo-managed</c> when there is no native host yet.
    /// </summary>
    public string Workflow { get; set; } = "bare";

    /// <summary>Gets or sets the Metro development server port.</summary>
    public int MetroPort { get; set; } = DefaultMetroPort;

    /// <summary>Gets whether the workflow can build, install, and launch without extra setup.</summary>
    [JsonIgnore]
    public bool SupportsNativeBuild =>
        !string.Equals(Workflow, "expo-managed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>.NET MAUI specifics detected from the project file.</summary>
public sealed class MauiProjectInfo
{
    /// <summary>Gets or sets the project file path relative to the project root.</summary>
    public string? ProjectFile { get; set; }

    /// <summary>Gets or sets every target framework declared by the project.</summary>
    public List<string> TargetFrameworks { get; set; } = [];

    /// <summary>Gets or sets the Android target frameworks, in declaration order.</summary>
    public List<string> AndroidTargetFrameworks { get; set; } = [];

    /// <summary>Gets or sets whether Microsoft.Maui.Controls is referenced explicitly.</summary>
    public bool ReferencesMauiControls { get; set; }

    /// <summary>Gets whether the project has an Android target framework.</summary>
    [JsonIgnore]
    public bool SupportsAndroid => AndroidTargetFrameworks.Count > 0;
}

/// <summary>Capacitor specifics detected from package.json, the config, and the android host.</summary>
public sealed class CapacitorProjectInfo
{
    /// <summary>Gets or sets the Capacitor config file name, when one exists.</summary>
    public string? ConfigFile { get; set; }

    /// <summary>Gets or sets whether the generated android host exists.</summary>
    public bool HasAndroidHost { get; set; }
}

/// <summary>Cordova specifics detected from config.xml, package.json, and the generated platform.</summary>
public sealed class CordovaProjectInfo
{
    /// <summary>Gets or sets the Cordova config file name, when one exists.</summary>
    public string? ConfigFile { get; set; }

    /// <summary>Gets or sets whether the generated android platform exists.</summary>
    public bool HasAndroidHost { get; set; }
}

/// <summary>A reference to an APK that belongs to an APK-only project.</summary>
public sealed class ApkFileReference
{
    /// <summary>Gets or sets the absolute APK path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this is a split configuration APK.</summary>
    public bool IsSplit { get; set; }

    /// <summary>Gets or sets the split name when known.</summary>
    public string? SplitName { get; set; }
}

/// <summary>A project candidate returned by a monorepo scan.</summary>
public sealed class ProjectScanCandidate
{
    /// <summary>Gets or sets the absolute project root.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the project kind.</summary>
    public ProjectKind Kind { get; set; } = ProjectKind.Custom;

    /// <summary>Gets or sets the detection confidence.</summary>
    public DetectionConfidence Confidence { get; set; } = DetectionConfidence.Low;

    /// <summary>Gets or sets the application ID, when known.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>Gets or sets the number of variants found.</summary>
    public int VariantCount { get; set; }

    /// <summary>Gets or sets whether the project is already known.</summary>
    public bool AlreadyAdded { get; set; }

    /// <summary>Gets or sets the repository root that contains the candidate.</summary>
    public string? RepositoryRoot { get; set; }
}

/// <summary>The result of scanning a folder for projects.</summary>
public sealed class ProjectScanResult
{
    /// <summary>Gets or sets the scanned root.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the candidates found.</summary>
    public List<ProjectScanCandidate> Candidates { get; set; } = [];

    /// <summary>Gets or sets how long the scan took.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Gets or sets whether the scan stopped at a limit.</summary>
    public bool WasTruncated { get; set; }

    /// <summary>Gets or sets why the scan stopped early.</summary>
    public string? TruncationReason { get; set; }
}

/// <summary>Options that control a project scan.</summary>
public sealed class ProjectScanOptions
{
    /// <summary>Gets or sets the default scan options.</summary>
    public static ProjectScanOptions Default { get; } = new();

    /// <summary>Gets or sets how many folder levels below the root are scanned.</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>Gets or sets the maximum number of folders visited.</summary>
    public int MaxFolders { get; set; } = 2_000;

    /// <summary>Gets or sets the maximum scan duration.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets folder names that are never scanned.</summary>
    public List<string> IgnoreFolders { get; set; } =
    [
        ".git", ".gradle", ".idea", "build", "node_modules", "dist", "out", "Pods",
        ".dart_tool", ".vs", "bin", "obj", "packages",
    ];

    /// <summary>Gets or sets whether the root itself is included.</summary>
    public bool IncludeRoot { get; set; } = true;

    /// <summary>Gets or sets whether only Gradle and APK candidates are reported.</summary>
    public bool DetectNestedGradleProjects { get; set; } = true;

    /// <summary>Gets or sets the cancellation timeout used for the scan.</summary>
    public TimeSpan GetEffectiveTimeout() => Timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : Timeout;

    /// <summary>Gets the effective scan depth.</summary>
    public int GetEffectiveDepth() => Math.Clamp(MaxDepth, 0, 12);
}

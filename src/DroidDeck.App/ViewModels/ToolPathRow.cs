using System.ComponentModel;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Toolchain;

namespace DroidDeck.App.ViewModels;

/// <summary>An editable tool path override row in settings.</summary>
public sealed class ToolPathRow : INotifyPropertyChanged
{
    private string _path = string.Empty;
    private string _source = string.Empty;

    /// <summary>Creates a row for one tool override.</summary>
    public ToolPathRow(
        ToolKind kind,
        string displayName,
        string label,
        string placeholder,
        string settingsPath)
    {
        Kind = kind;
        DisplayName = displayName;
        Label = label;
        Placeholder = placeholder;
        SettingsPath = settingsPath;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the tool this row configures.</summary>
    public ToolKind Kind { get; }

    /// <summary>Gets the short tool name used in messages.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the field label.</summary>
    public string Label { get; }

    /// <summary>Gets the placeholder shown when no override is set.</summary>
    public string Placeholder { get; }

    /// <summary>Gets the settings path that stores the override.</summary>
    public string SettingsPath { get; }

    /// <summary>Gets the text box automation id.</summary>
    public string BoxId => $"{Kind}PathBox";

    /// <summary>Gets the save button automation id.</summary>
    public string SaveId => $"Save{Kind}PathButton";

    /// <summary>Gets the reset button automation id.</summary>
    public string ResetId => $"Reset{Kind}PathButton";

    /// <summary>Gets the source hint automation id.</summary>
    public string SourceId => $"{Kind}PathSource";

    /// <summary>Gets or sets the configured override, empty when auto-detection applies.</summary>
    public string Path
    {
        get => _path;
        set
        {
            if (string.Equals(_path, value, StringComparison.Ordinal))
            {
                return;
            }

            _path = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path)));
        }
    }

    /// <summary>Gets or sets the layer that supplies the effective value.</summary>
    public string Source
    {
        get => _source;
        set
        {
            if (string.Equals(_source, value, StringComparison.Ordinal))
            {
                return;
            }

            _source = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Source)));
        }
    }

    /// <summary>Creates one row per supported path override.</summary>
    public static ToolPathRow[] CreateAll() =>
    [
        new(
            ToolKind.AndroidSdk,
            "Android SDK",
            "Android SDK root",
            "SDK folder (ANDROID_HOME, ANDROID_SDK_ROOT, or default install)",
            SettingsPaths.AndroidSdkPath),
        new(ToolKind.Adb, "ADB", "adb executable path", "adb.exe (resolve from PATH)", SettingsPaths.AdbPath),
        new(
            ToolKind.Aapt2,
            "aapt2",
            "aapt2 executable path",
            "aapt2.exe (latest SDK build-tools)",
            SettingsPaths.Aapt2Path),
        new(
            ToolKind.Emulator,
            "Android emulator",
            "emulator executable path",
            "emulator.exe (SDK emulator folder)",
            SettingsPaths.EmulatorPath),
        new(
            ToolKind.Java,
            "Java",
            "JDK folder",
            "Java home (JAVA_HOME or the Android Studio JBR)",
            SettingsPaths.JavaHome),
        new(
            ToolKind.Gradle,
            "Gradle",
            "gradle executable path",
            "gradle.bat (resolve from PATH)",
            SettingsPaths.GradlePath),
        new(
            ToolKind.Flutter,
            "Flutter",
            "flutter executable path",
            "flutter.bat (FLUTTER_ROOT or PATH)",
            SettingsPaths.FlutterPath),
        new(
            ToolKind.Dart,
            "Dart",
            "dart executable path",
            "dart.bat (bundled with the Flutter SDK)",
            SettingsPaths.DartPath),
        new(
            ToolKind.Node,
            "Node.js",
            "Node.js executable path",
            "node.exe (also locates npm and npx)",
            SettingsPaths.NodePath),
        new(
            ToolKind.Scrcpy,
            "scrcpy",
            "scrcpy executable path",
            "scrcpy.exe (resolve from PATH)",
            SettingsPaths.ScrcpyPath),
        new(
            ToolKind.Unity,
            "Unity",
            "Unity executable path",
            "Unity.exe (detect beneath the Unity Hub editor folder)",
            SettingsPaths.UnityPath),
        new(
            ToolKind.DotNet,
            ".NET SDK",
            "dotnet executable path",
            "dotnet.exe (resolve from PATH or the default install)",
            SettingsPaths.DotNetPath),
    ];
}

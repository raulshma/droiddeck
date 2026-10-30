using System.Collections.ObjectModel;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Features;
using DroidDeck.Core.Profiles;
using DroidDeck.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>Edits a run configuration with field-level validation and a command preview.</summary>
public sealed partial class RunConfigurationDialog : ContentDialog
{
    private readonly ProjectModel _project;
    private readonly IRunPlanBuilder _plans;
    private readonly IReadOnlyList<RunStepOption> _registeredSteps;
    private readonly ObservableCollection<StepRow> _steps = [];
    private bool _initializing;

    /// <summary>Creates a run configuration editor.</summary>
    public RunConfigurationDialog(
        ProjectModel project,
        RunConfiguration configuration,
        IReadOnlyList<DeviceInfo> devices,
        IRunPlanBuilder plans,
        IReadOnlyList<RunStepOption>? registeredSteps = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _registeredSteps = registeredSteps ?? [];
        ArgumentNullException.ThrowIfNull(devices);

        InitializeComponent();
        _initializing = true;
        try
        {
            KindCombo.ItemsSource = Enum.GetValues<RunConfigurationKind>();
            ScreenModeCombo.ItemsSource = Enum.GetValues<ScreenMode>();
            ModuleCombo.ItemsSource = project.Modules
                .Select(module => module.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            VariantCombo.ItemsSource = project.Variants
                .Select(variant => variant.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            TargetFrameworkBox.ItemsSource = (project.MauiInfo?.AndroidTargetFrameworks ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            DeviceCombo.ItemsSource = devices
                .Select(device => new DeviceChoice(device.Serial, $"{device.DisplayName} · {device.Serial}"))
                .ToArray();

            PresetCombo.ItemsSource = PresetChoice.ForProject(_project);
            CapabilityText.Text = DescribeCapability(_project.Kind);
            LoadFromConfiguration(configuration);

            foreach (var step in configuration.Steps)
            {
                _steps.Add(new StepRow(step));
            }

            StepList.ItemsSource = _steps;
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>Gets the edited configuration. It is only applied after the dialog is accepted.</summary>
    public RunConfiguration Configuration { get; }

    private void LoadFromConfiguration(RunConfiguration configuration)
    {
        NameBox.Text = configuration.Name;
        KindCombo.SelectedItem = configuration.Kind;
        ModuleCombo.Text = configuration.ModuleName ?? string.Empty;
        VariantCombo.Text = configuration.VariantName ?? string.Empty;
        DeviceCombo.Text = configuration.DeviceSerial ?? string.Empty;

        BuildTaskBox.Text = configuration.Build.Task ?? string.Empty;
        BuildArgumentsBox.Text = string.Join(Environment.NewLine, configuration.Build.Arguments);
        BuildEnvironmentBox.Text = FormatEnvironment(configuration.Build.Environment);
        JavaHomeBox.Text = configuration.Build.JavaHome ?? string.Empty;
        TargetFrameworkBox.Text = configuration.Build.TargetFramework ?? string.Empty;
        BuildTimeoutBox.Value = configuration.Build.TimeoutSeconds;
        BuildWorkingDirectoryBox.Text = configuration.Build.WorkingDirectory ?? string.Empty;
        CleanCheck.IsChecked = configuration.Build.Clean;
        SkipBuildCheck.IsChecked = configuration.Build.Skip;

        ArtifactGlobBox.Text = configuration.Artifact.Glob;
        ArtifactPackageBox.Text = configuration.Artifact.PackageName ?? string.Empty;
        IncludeSplitsCheck.IsChecked = configuration.Artifact.IncludeSplits;
        InspectAapt2Check.IsChecked = configuration.Artifact.InspectWithAapt2;

        ReplaceCheck.IsChecked = configuration.Install.Replace;
        TestOnlyCheck.IsChecked = configuration.Install.TestOnly;
        GrantPermissionsCheck.IsChecked = configuration.Install.GrantPermissions;
        AllowDowngradeCheck.IsChecked = configuration.Install.AllowDowngrade;
        InstallUserBox.Text = configuration.Install.User ?? string.Empty;
        InstallArgumentsBox.Text = string.Join(Environment.NewLine, configuration.Install.ExtraArguments);

        LaunchPackageBox.Text = configuration.Launch.PackageName ?? string.Empty;
        LaunchActivityBox.Text = configuration.Launch.Activity ?? string.Empty;
        LaunchActionBox.Text = configuration.Launch.Action ?? string.Empty;
        LaunchDataBox.Text = configuration.Launch.Data ?? string.Empty;
        LaunchArgumentsBox.Text = string.Join(Environment.NewLine, configuration.Launch.ExtraArguments);
        WaitLaunchCheck.IsChecked = configuration.Launch.Wait;
        StopBeforeLaunchCheck.IsChecked = configuration.Launch.StopBeforeLaunch;
        WaitForDebuggerCheck.IsChecked = configuration.Launch.WaitForDebugger;

        LogBuffersBox.Text = string.Join(", ", configuration.Logs.Buffers);
        LogMaxLinesBox.Value = configuration.Logs.MaxLines;
        AttachLogsCheck.IsChecked = configuration.Logs.Attach;
        ClearLogsCheck.IsChecked = configuration.Logs.ClearBeforeLaunch;
        AppOnlyLogsCheck.IsChecked = configuration.Logs.AppOnly;

        ScreenModeCombo.SelectedItem = configuration.Screen.Mode;
        AutoOpenScreenCheck.IsChecked = configuration.Screen.AutoOpen;
    }

    private static string DescribeCapability(ProjectKind kind)
    {
        var badge = CapabilityBadges.For(RunConfigurationPresetLibrary.GetCapability(kind));
        return $"{badge.Title}: {badge.Description}";
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || PresetCombo.SelectedItem is not PresetChoice { Id: not null } choice)
        {
            return;
        }

        var preset = RunConfigurationPresetLibrary.Create(choice.Id, _project);
        Configuration.Name = preset.Name;
        Configuration.Kind = preset.Kind;
        Configuration.ModuleName = preset.ModuleName;
        Configuration.VariantName = preset.VariantName;
        Configuration.DeviceSerial = Configuration.DeviceSerial ?? preset.DeviceSerial;
        Configuration.Build = preset.Build;
        Configuration.Artifact = preset.Artifact;
        Configuration.Install = preset.Install;
        Configuration.Launch = preset.Launch;
        Configuration.Logs = preset.Logs;
        Configuration.Screen = preset.Screen;
        Configuration.Steps = preset.Steps;

        LoadFromConfiguration(Configuration);
        _steps.Clear();
        foreach (var step in Configuration.Steps)
        {
            _steps.Add(new StepRow(step));
        }
    }

    private void OnVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || VariantCombo.SelectedItem is not string name)
        {
            return;
        }

        var variant = _project.Variants.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (variant is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(variant.BuildTask))
        {
            BuildTaskBox.Text = variant.BuildTask;
        }

        if (!string.IsNullOrWhiteSpace(variant.ArtifactGlob))
        {
            ArtifactGlobBox.Text = variant.ArtifactGlob;
        }

        if (!string.IsNullOrWhiteSpace(variant.ModuleName))
        {
            ModuleCombo.Text = variant.ModuleName;
        }
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && DeviceCombo.SelectedItem is DeviceChoice choice)
        {
            DeviceCombo.Text = choice.Serial;
        }
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        SyncFromFields();
        var serial = string.IsNullOrWhiteSpace(DeviceCombo.Text) ? null : DeviceCombo.Text.Trim();
        var preview = _plans.BuildPreview(_project, Configuration, serial);
        var text = new System.Text.StringBuilder();
        if (preview.Errors.Count > 0)
        {
            text.AppendLine("Configuration problems:");
            foreach (var error in preview.Errors)
            {
                text.AppendLine($"  {error.Field}: {error.Message}");
            }

            text.AppendLine();
        }

        foreach (var step in preview.Steps)
        {
            text.Append(step.WillRun ? "[run] " : "[skip] ");
            text.Append(step.Name);
            text.Append(": ");
            text.AppendLine(string.IsNullOrWhiteSpace(step.CommandLine) ? "(no command)" : step.CommandLine);
            if (!step.WillRun && !string.IsNullOrWhiteSpace(step.SkipReason))
            {
                text.AppendLine($"        {step.SkipReason}");
            }
        }

        PreviewText.Text = text.ToString().TrimEnd();
        PreviewPanel.Visibility = Visibility.Visible;
        ShowValidation(preview.Errors);
    }

    private void OnValidateClick(object sender, RoutedEventArgs e)
    {
        SyncFromFields();
        var validation = RunConfigurationValidator.Validate(_project, Configuration);
        ShowValidation(validation.Errors);
        if (validation.IsValid)
        {
            StatusText.Text = "No validation problems were found.";
            StatusText.Visibility = Visibility.Visible;
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        SyncFromFields();
        var validation = RunConfigurationValidator.Validate(_project, Configuration);
        if (!validation.IsValid)
        {
            args.Cancel = true;
            ShowValidation(validation.Errors);
            ErrorText.Text = "Fix the validation problems before saving.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ShowValidation(List<RunConfigurationValidationError> errors)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (errors.Count == 0)
        {
            ValidationPanel.Visibility = Visibility.Collapsed;
            ValidationList.ItemsSource = null;
            return;
        }

        StatusText.Visibility = Visibility.Collapsed;
        ValidationList.ItemsSource = errors
            .Select(error => new ValidationRow(error.Field, error.Message))
            .ToArray();
        ValidationPanel.Visibility = Visibility.Visible;
    }

    private void SyncFromFields()
    {
        Configuration.Name = NameBox.Text.Trim();
        Configuration.Kind = KindCombo.SelectedItem is RunConfigurationKind kind
            ? kind
            : RunConfigurationKind.Gradle;
        Configuration.ModuleName = NullIfEmpty(ModuleCombo.Text);
        Configuration.VariantName = NullIfEmpty(VariantCombo.Text);
        Configuration.DeviceSerial = NullIfEmpty(DeviceCombo.Text);

        Configuration.Build.Task = NullIfEmpty(BuildTaskBox.Text);
        Configuration.Build.Arguments = ParseLines(BuildArgumentsBox.Text);
        Configuration.Build.Environment = ParseEnvironment(BuildEnvironmentBox.Text);
        Configuration.Build.JavaHome = NullIfEmpty(JavaHomeBox.Text);
        Configuration.Build.TargetFramework = NullIfEmpty(TargetFrameworkBox.Text);
        Configuration.Build.TimeoutSeconds = ReadInt(BuildTimeoutBox, 900, 5, 86_400);
        Configuration.Build.WorkingDirectory = NullIfEmpty(BuildWorkingDirectoryBox.Text);
        Configuration.Build.Clean = CleanCheck.IsChecked == true;
        Configuration.Build.Skip = SkipBuildCheck.IsChecked == true;

        Configuration.Artifact.Glob = ArtifactGlobBox.Text.Trim();
        Configuration.Artifact.PackageName = NullIfEmpty(ArtifactPackageBox.Text);
        Configuration.Artifact.IncludeSplits = IncludeSplitsCheck.IsChecked == true;
        Configuration.Artifact.InspectWithAapt2 = InspectAapt2Check.IsChecked == true;

        Configuration.Install.Replace = ReplaceCheck.IsChecked == true;
        Configuration.Install.TestOnly = TestOnlyCheck.IsChecked == true;
        Configuration.Install.GrantPermissions = GrantPermissionsCheck.IsChecked == true;
        Configuration.Install.AllowDowngrade = AllowDowngradeCheck.IsChecked == true;
        Configuration.Install.User = NullIfEmpty(InstallUserBox.Text);
        Configuration.Install.ExtraArguments = ParseLines(InstallArgumentsBox.Text);

        Configuration.Launch.PackageName = NullIfEmpty(LaunchPackageBox.Text);
        Configuration.Launch.Activity = NullIfEmpty(LaunchActivityBox.Text);
        Configuration.Launch.Action = NullIfEmpty(LaunchActionBox.Text);
        Configuration.Launch.Data = NullIfEmpty(LaunchDataBox.Text);
        Configuration.Launch.ExtraArguments = ParseLines(LaunchArgumentsBox.Text);
        Configuration.Launch.Wait = WaitLaunchCheck.IsChecked == true;
        Configuration.Launch.StopBeforeLaunch = StopBeforeLaunchCheck.IsChecked == true;
        Configuration.Launch.WaitForDebugger = WaitForDebuggerCheck.IsChecked == true;

        var buffers = LogBuffersBox.Text
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Configuration.Logs.Buffers = buffers.Count > 0 ? buffers : ["main", "system", "crash", "events"];
        Configuration.Logs.MaxLines = ReadInt(LogMaxLinesBox, 20_000, 100, 1_000_000);
        Configuration.Logs.Attach = AttachLogsCheck.IsChecked == true;
        Configuration.Logs.ClearBeforeLaunch = ClearLogsCheck.IsChecked == true;
        Configuration.Logs.AppOnly = AppOnlyLogsCheck.IsChecked == true;

        Configuration.Screen.Mode = ScreenModeCombo.SelectedItem is ScreenMode mode ? mode : ScreenMode.Off;
        Configuration.Screen.AutoOpen = AutoOpenScreenCheck.IsChecked == true;

        Configuration.Steps = _steps.Select(row => CloneStep(row.Step)).ToList();
    }

    private async void OnAddStepClick(object sender, RoutedEventArgs e)
    {
        var dialog = new StepEditorDialog(registeredSteps: _registeredSteps) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _steps.Add(new StepRow(dialog.Step));
        }
    }

    private async void OnEditStepClick(object sender, RoutedEventArgs e)
    {
        if (StepList.SelectedItem is not StepRow row)
        {
            return;
        }

        var dialog = new StepEditorDialog(row.Step, _registeredSteps) { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var index = _steps.IndexOf(row);
        if (index >= 0)
        {
            _steps[index] = new StepRow(dialog.Step);
        }
    }

    private void OnRemoveStepClick(object sender, RoutedEventArgs e)
    {
        if (StepList.SelectedItem is StepRow row)
        {
            _steps.Remove(row);
        }
    }

    private void OnMoveStepUpClick(object sender, RoutedEventArgs e)
    {
        if (StepList.SelectedItem is not StepRow row)
        {
            return;
        }

        var index = _steps.IndexOf(row);
        if (index > 0)
        {
            _steps.Move(index, index - 1);
            StepList.SelectedItem = row;
        }
    }

    private void OnMoveStepDownClick(object sender, RoutedEventArgs e)
    {
        if (StepList.SelectedItem is not StepRow row)
        {
            return;
        }

        var index = _steps.IndexOf(row);
        if (index >= 0 && index < _steps.Count - 1)
        {
            _steps.Move(index, index + 1);
            StepList.SelectedItem = row;
        }
    }

    private static string FormatEnvironment(IReadOnlyDictionary<string, string> environment) =>
        string.Join(Environment.NewLine, environment.Select(pair => $"{pair.Key}={pair.Value}"));

    private static List<string> ParseLines(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0)
            .ToList();

    private static Dictionary<string, string> ParseEnvironment(string text)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length > 0)
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    private static int ReadInt(NumberBox box, int fallback, int minimum, int maximum)
    {
        var value = double.IsNaN(box.Value) ? fallback : (int)Math.Round(box.Value);
        return Math.Clamp(value, minimum, maximum);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static RunStepDefinition CloneStep(RunStepDefinition step) => new()
    {
        Id = step.Id,
        Name = step.Name,
        Kind = step.Kind,
        Command = step.Command,
        StepId = step.StepId,
        Arguments = [.. step.Arguments],
        WorkingDirectory = step.WorkingDirectory,
        Environment = new Dictionary<string, string>(step.Environment, StringComparer.OrdinalIgnoreCase),
        TimeoutSeconds = step.TimeoutSeconds,
        Condition = step.Condition,
        FailurePolicy = step.FailurePolicy,
        RetryCount = step.RetryCount,
        Enabled = step.Enabled,
    };

    /// <summary>One device choice in the device combo box.</summary>
    public sealed class DeviceChoice
    {
        /// <summary>Creates a device choice.</summary>
        public DeviceChoice(string serial, string label)
        {
            Serial = serial;
            Label = label;
        }

        /// <summary>Gets the device serial.</summary>
        public string Serial { get; }

        /// <summary>Gets the display label.</summary>
        public string Label { get; }
    }

    /// <summary>One preset choice in the preset picker.</summary>
    public sealed class PresetChoice
    {
        /// <summary>Creates a preset choice for an identifier and label.</summary>
        public PresetChoice(string? id, string label)
        {
            Id = id;
            Label = label;
        }

        /// <summary>Gets the preset identifier, or <see langword="null"/> for no preset.</summary>
        public string? Id { get; }

        /// <summary>Gets the display label.</summary>
        public string Label { get; }

        /// <summary>Builds the preset choices suggested for a project.</summary>
        public static IReadOnlyList<PresetChoice> ForProject(ProjectModel project)
        {
            var choices = new List<PresetChoice> { new(null, "No preset (configure from scratch)") };
            choices.AddRange(RunConfigurationPresetLibrary.Suggest(project.Kind)
                .Select(preset => new PresetChoice(preset.Id, $"{preset.Name} — {preset.Description}")));
            return choices;
        }
    }

    /// <summary>One validation message.</summary>
    public sealed class ValidationRow
    {
        /// <summary>Creates a validation row.</summary>
        public ValidationRow(string field, string message)
        {
            Field = field;
            Message = $"{field}: {message}";
        }

        /// <summary>Gets the field path.</summary>
        public string Field { get; }

        /// <summary>Gets the field and message.</summary>
        public string Message { get; }
    }

    /// <summary>One custom step row.</summary>
    public sealed class StepRow
    {
        /// <summary>Creates a row around a step.</summary>
        public StepRow(RunStepDefinition step)
        {
            Step = step;
            Name = step.Enabled ? step.Name : $"{step.Name} (disabled)";
            Summary = $"{step.FailurePolicy} · {step.TimeoutSeconds}s" +
                      (string.IsNullOrWhiteSpace(step.Condition) ? string.Empty : " · conditional");
            Command = !string.IsNullOrWhiteSpace(step.StepId)
                ? $"Registered step: {step.StepId}"
                : string.IsNullOrWhiteSpace(step.Command)
                    ? "(no command)"
                    : step.Command;
        }

        /// <summary>Gets the step.</summary>
        public RunStepDefinition Step { get; }

        /// <summary>Gets the display name.</summary>
        public string Name { get; }

        /// <summary>Gets the policy summary.</summary>
        public string Summary { get; }

        /// <summary>Gets the command line.</summary>
        public string Command { get; }
    }
}

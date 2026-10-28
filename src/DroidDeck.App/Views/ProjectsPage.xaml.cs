using System.Runtime.InteropServices;
using DroidDeck.App.Services;
using DroidDeck.Core;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Runs;
using DroidDeck.Projects.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace DroidDeck.App.Views;

/// <summary>Adds, inspects, configures, and runs Android projects.</summary>
public sealed partial class ProjectsPage : Page, IRefreshablePage, IDisposable
{
    private readonly IProjectService _projects;
    private readonly IDeviceService _devices;
    private readonly IRunSessionService _runs;
    private readonly IRunPlanBuilder _plans;
    private readonly SessionWorkspace _workspace;
    private readonly IConfigurationService _configuration;
    private readonly ILogger<ProjectsPage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private ProjectModel? _selected;
    private RunConfiguration? _selectedConfiguration;
    private bool _syncingSelection;
    private bool _disposed;

    /// <summary>Creates the projects page.</summary>
    public ProjectsPage(
        IProjectService projects,
        IDeviceService devices,
        IRunSessionService runs,
        IRunPlanBuilder plans,
        SessionWorkspace workspace,
        IConfigurationService configuration,
        ILogger<ProjectsPage> logger)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        _projects.ProjectsChanged += OnProjectsChanged;
        _projects.ProjectChanged += OnProjectChanged;
        _devices.DevicesChanged += OnDevicesChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        var previousId = _selected?.Id ?? _workspace.ProjectId;
        var rows = _projects.Projects.Select(ProjectRow.From).ToArray();
        ProjectList.ItemsSource = rows;
        EmptyProjectsPanel.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var selectedRow = rows.FirstOrDefault(row => string.Equals(row.Id, previousId, StringComparison.OrdinalIgnoreCase))
                          ?? rows.FirstOrDefault();
        _syncingSelection = true;
        try
        {
            ProjectList.SelectedItem = selectedRow;
        }
        finally
        {
            _syncingSelection = false;
        }

        if (selectedRow is null)
        {
            ShowDetail(null);
            return;
        }

        SelectProject(selectedRow.Id, persistOpened: false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _projects.ProjectsChanged -= OnProjectsChanged;
        _projects.ProjectChanged -= OnProjectChanged;
        _devices.DevicesChanged -= OnDevicesChanged;
    }

    private void SelectProject(string projectId, bool persistOpened)
    {
        var project = _projects.Get(projectId);
        if (project is null)
        {
            ShowDetail(null);
            return;
        }

        _selected = project;
        _workspace.OpenProject(project.Id);
        ShowDetail(project);
        if (persistOpened)
        {
            _ = MarkOpenedAsync(project.Id);
        }
    }

    private async Task MarkOpenedAsync(string projectId)
    {
        try
        {
            await _projects.MarkOpenedAsync(projectId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(exception, "The project open timestamp could not be saved.");
        }
    }

    private void ShowDetail(ProjectModel? project)
    {
        DetailScroll.Visibility = project is null ? Visibility.Collapsed : Visibility.Visible;
        EmptyDetailPanel.Visibility = project is null ? Visibility.Visible : Visibility.Collapsed;
        if (project is null)
        {
            _selected = null;
            _selectedConfiguration = null;
            return;
        }

        DetailNameText.Text = project.Name;
        DetailKindText.Text = $"{project.KindLabel} · detected {project.DetectedAt.ToLocalTime():g}";
        DetailRootText.Text = project.RootPath;
        DetailConfidenceText.Text = $"{project.Confidence} confidence";
        DetailConfidenceText.Foreground = BrushForConfidence(project.Confidence);

        LimitationBar.IsOpen = !string.IsNullOrWhiteSpace(project.Limitation);
        LimitationBar.Message = project.Limitation ?? string.Empty;
        WarningsText.Text = string.Join(Environment.NewLine, project.Warnings);
        WarningsText.Visibility = project.Warnings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        AppIdText.Text = ValueOrUnknown(project.ApplicationId);
        NamespaceText.Text = ValueOrUnknown(project.Namespace);
        LauncherText.Text = ValueOrUnknown(project.LauncherActivity);
        ApplicationModuleText.Text = ValueOrUnknown(project.ApplicationModule);
        WrapperText.Text = ValueOrUnknown(project.WrapperVersion);
        VersionsText.Text = $"AGP {ValueOrUnknown(project.AgpVersion)} · Kotlin {ValueOrUnknown(project.KotlinVersion)}";
        MultiplatformText.Text =
            $"{(project.IsKotlinMultiplatform ? "Yes" : "No")} · Compose Multiplatform {(project.IsComposeMultiplatform ? "yes" : "no")}";
        CapabilitiesText.Text = project.Capabilities == ProjectCapability.None
            ? "None"
            : string.Join(", ", Enum.GetValues<ProjectCapability>()
                .Where(capability => capability != ProjectCapability.None && project.Capabilities.HasFlag(capability)));

        OverrideNameBox.Text = project.ManualOverrides.GetValueOrDefault("name", project.Name);
        OverrideApplicationIdBox.Text = project.ManualOverrides.GetValueOrDefault("applicationId", project.ApplicationId ?? string.Empty);
        OverrideNamespaceBox.Text = project.ManualOverrides.GetValueOrDefault("namespace", project.Namespace ?? string.Empty);
        OverrideLauncherBox.Text = project.ManualOverrides.GetValueOrDefault("launcherActivity", project.LauncherActivity ?? string.Empty);
        OverrideModuleBox.Text = project.ManualOverrides.GetValueOrDefault("applicationModule", project.ApplicationModule ?? string.Empty);

        EvidenceList.ItemsSource = project.Evidence
            .Select(evidence => new EvidenceRow(evidence.Source, evidence.Detail))
            .ToArray();
        ModuleList.ItemsSource = project.Modules
            .Select(module => new ModuleRow(module.Name, DescribeModule(module)))
            .ToArray();
        VariantList.ItemsSource = project.Variants
            .Select(variant => new VariantRow(
                variant.Name,
                $"{variant.BuildTask ?? "no task"} · {variant.ArtifactGlob ?? "no artifact glob"}" +
                (variant.IsDefault ? " · default" : string.Empty)))
            .ToArray();
        ApkList.ItemsSource = project.ApkFiles
            .Select(reference => new ApkRow(
                Path.GetFileName(reference.Path),
                (reference.IsSplit ? $"Split {reference.SplitName ?? "APK"}" : "Base APK") + $" · {reference.Path}"))
            .ToArray();
        NoApksText.Visibility = project.ApkFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateDeviceChoices();
        ReloadRunConfigurations();
        UpdateRunHint();
    }

    private void UpdateDeviceChoices()
    {
        var previous = (DeviceCombo.SelectedItem as DeviceInfo)?.Serial;
        var devices = _devices.Devices
            .Where(device => device.State == DeviceState.Device)
            .ToArray();
        DeviceCombo.ItemsSource = devices;
        DeviceCombo.SelectedItem = devices.FirstOrDefault(device =>
            string.Equals(device.Serial, previous, StringComparison.Ordinal)) ?? devices.FirstOrDefault();
    }

    private void ReloadRunConfigurations()
    {
        if (_selected is null)
        {
            return;
        }

        var selectedId = _selectedConfiguration?.Id;
        var rows = _selected.RunConfigurations.Select(RunConfigRow.From).ToArray();
        RunConfigList.ItemsSource = rows;
        RunConfigCombo.ItemsSource = rows;

        var selected = rows.FirstOrDefault(row => string.Equals(row.Id, selectedId, StringComparison.Ordinal))
                       ?? rows.FirstOrDefault(row => row.IsDefault)
                       ?? rows.FirstOrDefault();
        _selectedConfiguration = selected?.Configuration;
        _syncingSelection = true;
        try
        {
            RunConfigList.SelectedItem = selected;
            RunConfigCombo.SelectedItem = selected;
        }
        finally
        {
            _syncingSelection = false;
        }

        UpdateRunHint();
    }

    private void UpdateRunHint()
    {
        if (_selected is null || _selectedConfiguration is null)
        {
            RunHintText.Text = "Add a run configuration to build, install, and launch this project.";
            return;
        }

        var configuration = _selectedConfiguration;
        RunHintText.Text = configuration.Kind switch
        {
            RunConfigurationKind.Gradle =>
                $"{configuration.Build.Task ?? "assembleDebug"} · artifact {configuration.Artifact.Glob}",
            RunConfigurationKind.Flutter =>
                $"Flutter {FlutterAdapter.NormalizeMode(configuration.VariantName)} · artifact {configuration.Artifact.Glob}",
            RunConfigurationKind.ReactNative =>
                $"React Native {ReactNativeAdapter.NormalizeMode(configuration.VariantName)} · artifact {configuration.Artifact.Glob}",
            RunConfigurationKind.Maui =>
                $"MAUI {MauiAdapter.NormalizeConfiguration(configuration.VariantName)} · " +
                $"{configuration.Build.TargetFramework ?? "Android"} · artifact {configuration.Artifact.Glob}",
            RunConfigurationKind.Capacitor =>
                $"Capacitor {CapacitorAdapter.NormalizeMode(configuration.VariantName)} · " +
                $"sync + Gradle build · artifact {configuration.Artifact.Glob}",
            RunConfigurationKind.Cordova =>
                $"Cordova {CordovaAdapter.NormalizeMode(configuration.VariantName)} · " +
                $"prepare + Gradle build · artifact {configuration.Artifact.Glob}",
            RunConfigurationKind.Apk =>
                $"Prebuilt APK · {configuration.Artifact.Glob}",
            _ => $"{configuration.Steps.Count} custom step(s)",
        };
    }

    private static string DescribeModule(ProjectModule module)
    {
        var parts = new List<string> { module.Type.ToString() };
        if (!string.IsNullOrWhiteSpace(module.ApplicationId))
        {
            parts.Add(module.ApplicationId);
        }

        if (module.IsKotlinMultiplatform)
        {
            parts.Add(module.IsComposeMultiplatform ? "KMP + Compose" : "KMP");
        }

        if (!string.IsNullOrWhiteSpace(module.BuildFile))
        {
            parts.Add(module.BuildFile);
        }

        return string.Join(" · ", parts);
    }

    private static string ValueOrUnknown(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Not detected" : value;

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        await RunBusyAsync("Adding the project", async () =>
        {
            var project = await _projects.AddAsync(folder.Path);
            Refresh();
            SelectRow(project.Id);
            ShowAction($"Added {project.Name} ({project.KindLabel}, {project.Confidence} confidence).");
        });
    }

    private async void OnAddApkClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".apk");
        InitializePicker(picker);
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0)
        {
            return;
        }

        await RunBusyAsync("Inspecting the APK", async () =>
        {
            var project = await _projects.AddApkAsync(files.Select(file => file.Path).ToArray());
            Refresh();
            SelectRow(project.Id);
            ShowAction($"Added APK-only project {project.Name}. Build and variants are unavailable.");
        });
    }

    private async void OnScanClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var dialog = new ScanProjectsDialog(_projects, folder.Path, _configuration.Current.Settings.Projects)
        {
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
        {
            return;
        }

        await RunBusyAsync("Adding scanned projects", async () =>
        {
            if (result == ContentDialogResult.Secondary)
            {
                var added = await _projects.AddAllAsync(folder.Path, dialog.Options);
                Refresh();
                ShowAction($"Scanned {folder.Path} and added {added.Count} project(s).");
                return;
            }

            var count = 0;
            foreach (var candidate in dialog.SelectedCandidates)
            {
                if (candidate.Kind == ProjectKind.ApkOnly)
                {
                    var apks = Directory.Exists(candidate.RootPath)
                        ? Directory.GetFiles(candidate.RootPath, "*.apk", SearchOption.TopDirectoryOnly)
                        : [];
                    if (apks.Length > 0)
                    {
                        await _projects.AddApkAsync(apks);
                        count++;
                    }

                    continue;
                }

                await _projects.AddAsync(candidate.RootPath, candidate.Kind);
                count++;
            }

            Refresh();
            ShowAction($"Added {count} selected project(s) from {folder.Path}.");
        });
    }

    private async void OnRefreshProjectClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        await RunBusyAsync("Refreshing project detection", async () =>
        {
            var diff = await _projects.PreviewRefreshAsync(_selected.Id);
            if (!diff.HasChanges)
            {
                ShowAction("Detection is up to date. No changes were found.");
                return;
            }

            var dialog = new RefreshDiffDialog(_selected.Name, diff)
            {
                XamlRoot = XamlRoot,
            };
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                ShowAction("Detection changes were dismissed.");
                return;
            }

            var project = await _projects.ApplyRefreshAsync(diff, dialog.AcceptedFields);
            _selected = project;
            ShowDetail(project);
            ShowAction($"Applied {dialog.AcceptedFields.Count} detected change(s).");
        });
    }

    private async void OnRemoveProjectClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove project?",
            Content = $"Remove \"{_selected.Name}\" from DroidDeck? The project files are not deleted.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunBusyAsync("Removing the project", async () =>
        {
            await _projects.RemoveAsync(_selected.Id);
            _selected = null;
            _selectedConfiguration = null;
            Refresh();
            ShowAction("Project removed. Its files were left untouched.");
        });
    }

    private async void OnSaveOverridesClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        await RunBusyAsync("Saving manual overrides", async () =>
        {
            ApplyOverride(_selected, "name", OverrideNameBox.Text, value => _selected.Name = value);
            ApplyOverride(_selected, "applicationId", OverrideApplicationIdBox.Text, value => _selected.ApplicationId = value);
            ApplyOverride(_selected, "namespace", OverrideNamespaceBox.Text, value => _selected.Namespace = value);
            ApplyOverride(_selected, "launcherActivity", OverrideLauncherBox.Text, value => _selected.LauncherActivity = value);
            ApplyOverride(_selected, "applicationModule", OverrideModuleBox.Text, value => _selected.ApplicationModule = value);
            await _projects.SaveAsync(_selected);
            ShowDetail(_selected);
            ShowAction("Manual overrides saved. They will be reapplied after detection refreshes.");
        });
    }

    private static void ApplyOverride(
        ProjectModel project,
        string field,
        string? text,
        Action<string> apply)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            project.ManualOverrides.Remove(field);
            return;
        }

        project.ManualOverrides[field] = value;
        apply(value);
    }

    private void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || ProjectList.SelectedItem is not ProjectRow row)
        {
            return;
        }

        SelectProject(row.Id, persistOpened: true);
    }

    private void OnRunConfigSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }

        if (RunConfigCombo.SelectedItem is RunConfigRow row)
        {
            _selectedConfiguration = row.Configuration;
            _syncingSelection = true;
            try
            {
                RunConfigList.SelectedItem = row;
            }
            finally
            {
                _syncingSelection = false;
            }

            UpdateRunHint();
        }
    }

    private void OnRunConfigListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
        {
            return;
        }

        if (RunConfigList.SelectedItem is RunConfigRow row)
        {
            _selectedConfiguration = row.Configuration;
            _syncingSelection = true;
            try
            {
                RunConfigCombo.SelectedItem = row;
            }
            finally
            {
                _syncingSelection = false;
            }

            UpdateRunHint();
        }
    }

    private async void OnAddRunConfigClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var configuration = CreateDefaultConfiguration(_selected);
        var dialog = new RunConfigurationDialog(_selected, configuration, _devices.Devices, _plans)
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunBusyAsync("Adding the run configuration", async () =>
        {
            _selected.RunConfigurations.Add(dialog.Configuration);
            EnsureDefault(_selected);
            await _projects.SaveAsync(_selected);
            _selectedConfiguration = dialog.Configuration;
            ReloadRunConfigurations();
            ShowAction($"Added run configuration \"{dialog.Configuration.Name}\".");
        });
    }

    private async void OnEditRunConfigClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selectedConfiguration is null)
        {
            return;
        }

        var dialog = new RunConfigurationDialog(_selected, _selectedConfiguration.Clone(), _devices.Devices, _plans)
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunBusyAsync("Saving the run configuration", async () =>
        {
            var index = _selected.RunConfigurations.FindIndex(configuration =>
                string.Equals(configuration.Id, dialog.Configuration.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                _selected.RunConfigurations[index] = dialog.Configuration;
            }
            else
            {
                _selected.RunConfigurations.Add(dialog.Configuration);
            }

            EnsureDefault(_selected);
            await _projects.SaveAsync(_selected);
            _selectedConfiguration = dialog.Configuration;
            ReloadRunConfigurations();
            ShowAction($"Saved run configuration \"{dialog.Configuration.Name}\".");
        });
    }

    private async void OnDuplicateRunConfigClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selectedConfiguration is null)
        {
            return;
        }

        await RunBusyAsync("Duplicating the run configuration", async () =>
        {
            var copy = _selectedConfiguration.Clone();
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = $"{_selectedConfiguration.Name} copy";
            copy.IsDefault = false;
            _selected.RunConfigurations.Add(copy);
            EnsureDefault(_selected);
            await _projects.SaveAsync(_selected);
            _selectedConfiguration = copy;
            ReloadRunConfigurations();
            ShowAction($"Duplicated the configuration as \"{copy.Name}\".");
        });
    }

    private async void OnRenameRunConfigClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selectedConfiguration is null)
        {
            return;
        }

        var box = new TextBox { Text = _selectedConfiguration.Name };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename run configuration",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(box.Text))
        {
            return;
        }

        await RunBusyAsync("Renaming the run configuration", async () =>
        {
            _selectedConfiguration.Name = box.Text.Trim();
            await _projects.SaveAsync(_selected);
            ReloadRunConfigurations();
            ShowAction($"Renamed the configuration to \"{_selectedConfiguration.Name}\".");
        });
    }

    private async void OnSetDefaultRunConfigClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selectedConfiguration is null)
        {
            return;
        }

        await RunBusyAsync("Setting the default configuration", async () =>
        {
            foreach (var configuration in _selected.RunConfigurations)
            {
                configuration.IsDefault = string.Equals(
                    configuration.Id,
                    _selectedConfiguration.Id,
                    StringComparison.Ordinal);
            }

            await _projects.SaveAsync(_selected);
            ReloadRunConfigurations();
            ShowAction($"\"{_selectedConfiguration.Name}\" is now the default configuration.");
        });
    }

    private async void OnDeleteRunConfigClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selectedConfiguration is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete run configuration?",
            Content = $"Delete \"{_selectedConfiguration.Name}\"? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunBusyAsync("Deleting the run configuration", async () =>
        {
            _selected.RunConfigurations.RemoveAll(configuration =>
                string.Equals(configuration.Id, _selectedConfiguration.Id, StringComparison.Ordinal));
            EnsureDefault(_selected);
            await _projects.SaveAsync(_selected);
            _selectedConfiguration = null;
            ReloadRunConfigurations();
            ShowAction("Run configuration deleted.");
        });
    }

    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _selectedConfiguration is null)
        {
            ShowAction("Select a run configuration first.", isError: true);
            return;
        }

        var serial = (DeviceCombo.SelectedItem as DeviceInfo)?.Serial ?? _selectedConfiguration.DeviceSerial;
        var preview = _plans.BuildPreview(_selected, _selectedConfiguration, serial);
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

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Command preview",
            PrimaryButtonText = "Close",
            Content = new ScrollViewer
            {
                MaxHeight = 460,
                Content = new TextBlock
                {
                    Text = text.ToString().TrimEnd(),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        await dialog.ShowAsync();
    }

    private async void OnRunClick(object sender, RoutedEventArgs e) => await StartRunAsync(dryRun: false);

    private async void OnDryRunClick(object sender, RoutedEventArgs e) => await StartRunAsync(dryRun: true);

    private async Task StartRunAsync(bool dryRun)
    {
        if (_selected is null || _selectedConfiguration is null)
        {
            ShowAction("Select a project and run configuration first.", isError: true);
            return;
        }

        var device = DeviceCombo.SelectedItem as DeviceInfo;
        var serial = device?.Serial ?? _selectedConfiguration.DeviceSerial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            ShowAction("Select a connected device before running.", isError: true);
            return;
        }

        var validation = RunConfigurationValidator.Validate(_selected, _selectedConfiguration);
        if (!validation.IsValid)
        {
            ShowAction(
                "The run configuration is invalid: " +
                string.Join("; ", validation.Errors.Select(error => $"{error.Field}: {error.Message}")),
                isError: true);
            return;
        }

        var request = new RunRequest
        {
            Project = _selected,
            Configuration = _selectedConfiguration,
            Device = device,
            DeviceSerial = serial,
            DryRun = dryRun,
        };

        string? sessionId = null;
        void OnChanged(object? sender, RunSessionChangedEventArgs args)
        {
            if (sessionId is null &&
                string.Equals(args.Session.ProjectId, _selected.Id, StringComparison.Ordinal) &&
                string.Equals(args.Session.ConfigurationId, _selectedConfiguration.Id, StringComparison.Ordinal))
            {
                sessionId = args.Session.Id;
            }
        }

        _runs.SessionChanged += OnChanged;
        Task<RunSession> task;
        try
        {
            task = _runs.RunAsync(request);
        }
        finally
        {
            _runs.SessionChanged -= OnChanged;
        }

        if (sessionId is not null)
        {
            _workspace.OpenSession(sessionId);
            App.Instance.Navigate("session");
        }

        try
        {
            await task;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The run could not be started.");
            ShowAction($"The run could not be started: {exception.Message}", isError: true);
        }
    }

    private static RunConfiguration CreateDefaultConfiguration(ProjectModel project)
    {
        var stackDefault = project.Kind switch
        {
            ProjectKind.Capacitor when project.CapacitorInfo is { HasAndroidHost: true } =>
                CapacitorAdapter.CreateDefaultRunConfigurations(project).FirstOrDefault(),
            ProjectKind.Cordova when project.CordovaInfo is { HasAndroidHost: true } =>
                CordovaAdapter.CreateDefaultRunConfigurations(project).FirstOrDefault(),
            ProjectKind.Maui when project.MauiInfo is { SupportsAndroid: true } mauiInfo =>
                MauiAdapter.CreateDefaultRunConfigurations(project, mauiInfo).FirstOrDefault(),
            _ => null,
        };
        if (stackDefault is not null)
        {
            stackDefault.Id = Guid.NewGuid().ToString("N");
            stackDefault.Name = "New configuration";
            stackDefault.IsDefault = false;
            return stackDefault;
        }

        var variant = project.Variants.FirstOrDefault(variant => variant.IsDefault) ?? project.Variants.FirstOrDefault();
        var configuration = new RunConfiguration
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = project.Kind == ProjectKind.ApkOnly ? "Install APK" : "New configuration",
            Kind = project.Kind switch
            {
                ProjectKind.ApkOnly => RunConfigurationKind.Apk,
                ProjectKind.Custom => RunConfigurationKind.Custom,
                ProjectKind.Flutter => RunConfigurationKind.Flutter,
                ProjectKind.ReactNative => RunConfigurationKind.ReactNative,
                ProjectKind.Capacitor => RunConfigurationKind.Capacitor,
                ProjectKind.Cordova => RunConfigurationKind.Cordova,
                _ => RunConfigurationKind.Gradle,
            },
            ModuleName = project.ApplicationModule,
            VariantName = variant?.Name,
            Build = new BuildConfiguration
            {
                Task = project.Kind == ProjectKind.Flutter
                    ? null
                    : variant?.BuildTask ?? "assembleDebug",
                WorkingDirectory = project.Kind switch
                {
                    ProjectKind.ReactNative => ReactNativeAdapter.AndroidHostFolder,
                    ProjectKind.Capacitor => CapacitorAdapter.AndroidHostFolder,
                    ProjectKind.Cordova => CordovaAdapter.AndroidHostFolder,
                    _ => null,
                },
            },
            Artifact = new ArtifactConfiguration
            {
                Glob = variant?.ArtifactGlob
                       ?? (project.ApkFiles.FirstOrDefault()?.Path ?? string.Empty),
                PackageName = project.ApplicationId,
            },
            Launch = new LaunchConfiguration
            {
                PackageName = project.ApplicationId,
                Activity = project.LauncherActivity,
            },
        };
        return configuration;
    }

    private static void EnsureDefault(ProjectModel project)
    {
        if (project.RunConfigurations.Count == 0)
        {
            return;
        }

        if (!project.RunConfigurations.Any(configuration => configuration.IsDefault))
        {
            project.RunConfigurations[0].IsDefault = true;
        }
    }

    private async Task RunBusyAsync(string action, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            InvalidDataException or FileNotFoundException or ArgumentException)
        {
            _logger.LogWarning(exception, "{Action} failed.", action);
            ShowAction($"{action} failed: {exception.Message}", isError: true);
        }
    }

    private void ShowAction(string message, bool isError = false)
    {
        ActionBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }

    private void SelectRow(string projectId)
    {
        var row = (ProjectList.ItemsSource as IEnumerable<ProjectRow>)?
            .FirstOrDefault(candidate => string.Equals(candidate.Id, projectId, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            ProjectList.SelectedItem = row;
        }
    }

    private void OnProjectsChanged(object? sender, EventArgs e) => Dispatch(Refresh);

    private void OnProjectChanged(object? sender, ProjectModel project)
    {
        Dispatch(() =>
        {
            if (_selected is not null && string.Equals(_selected.Id, project.Id, StringComparison.OrdinalIgnoreCase))
            {
                _selected = project;
                ShowDetail(project);
            }
        });
    }

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) =>
        Dispatch(UpdateDeviceChoices);

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    private static Brush BrushForConfidence(DetectionConfidence confidence)
    {
        var key = confidence switch
        {
            DetectionConfidence.High => "SystemFillColorSuccessBrush",
            DetectionConfidence.Medium => "SystemFillColorCautionBrush",
            _ => "SystemFillColorCriticalBrush",
        };
        return Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private static void InitializePicker(object picker)
    {
        var window = App.Instance.Services.GetRequiredService<MainWindow>();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
    }

    /// <summary>One project row in the list.</summary>
    public sealed class ProjectRow
    {
        private ProjectRow(ProjectModel project)
        {
            Id = project.Id;
            Name = project.Name;
            Subtitle = $"{project.KindLabel} · {project.RootPath}";
            ConfidenceLabel = project.Confidence.ToString();
            ConfidenceBrush = BrushForConfidence(project.Confidence);
        }

        /// <summary>Gets the project identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the display name.</summary>
        public string Name { get; }

        /// <summary>Gets the kind and path summary.</summary>
        public string Subtitle { get; }

        /// <summary>Gets the confidence label.</summary>
        public string ConfidenceLabel { get; }

        /// <summary>Gets the confidence brush.</summary>
        public Brush ConfidenceBrush { get; }

        /// <summary>Creates a row from a project.</summary>
        public static ProjectRow From(ProjectModel project)
        {
            ArgumentNullException.ThrowIfNull(project);
            return new ProjectRow(project);
        }
    }

    /// <summary>One evidence row.</summary>
    public sealed class EvidenceRow
    {
        /// <summary>Creates an evidence row.</summary>
        public EvidenceRow(string source, string detail)
        {
            Source = source;
            Detail = detail;
        }

        /// <summary>Gets the evidence source.</summary>
        public string Source { get; }

        /// <summary>Gets the explanation.</summary>
        public string Detail { get; }
    }

    /// <summary>One module row.</summary>
    public sealed class ModuleRow
    {
        /// <summary>Creates a module row.</summary>
        public ModuleRow(string name, string detail)
        {
            Name = name;
            Detail = detail;
        }

        /// <summary>Gets the module name.</summary>
        public string Name { get; }

        /// <summary>Gets the module detail.</summary>
        public string Detail { get; }
    }

    /// <summary>One variant row.</summary>
    public sealed class VariantRow
    {
        /// <summary>Creates a variant row.</summary>
        public VariantRow(string name, string detail)
        {
            Name = name;
            Detail = detail;
        }

        /// <summary>Gets the variant name.</summary>
        public string Name { get; }

        /// <summary>Gets the variant detail.</summary>
        public string Detail { get; }
    }

    /// <summary>One APK row.</summary>
    public sealed class ApkRow
    {
        /// <summary>Creates an APK row.</summary>
        public ApkRow(string fileName, string detail)
        {
            FileName = fileName;
            Detail = detail;
        }

        /// <summary>Gets the file name.</summary>
        public string FileName { get; }

        /// <summary>Gets the APK detail.</summary>
        public string Detail { get; }
    }

    /// <summary>One run configuration row.</summary>
    public sealed class RunConfigRow
    {
        private RunConfigRow(RunConfiguration configuration)
        {
            Configuration = configuration;
            Id = configuration.Id;
            Name = configuration.Name;
            IsDefault = configuration.IsDefault;
            Detail = configuration.Kind switch
            {
                RunConfigurationKind.Gradle =>
                    $"{configuration.Build.Task ?? "assembleDebug"} · {configuration.Artifact.Glob}",
                RunConfigurationKind.Flutter =>
                    $"Flutter {FlutterAdapter.NormalizeMode(configuration.VariantName)} · {configuration.Artifact.Glob}",
                RunConfigurationKind.ReactNative =>
                    $"React Native {ReactNativeAdapter.NormalizeMode(configuration.VariantName)} · {configuration.Artifact.Glob}",
                RunConfigurationKind.Maui =>
                    $"MAUI {MauiAdapter.NormalizeConfiguration(configuration.VariantName)} · " +
                    $"{configuration.Build.TargetFramework ?? "Android"} · {configuration.Artifact.Glob}",
                RunConfigurationKind.Capacitor =>
                    $"Capacitor {CapacitorAdapter.NormalizeMode(configuration.VariantName)} · {configuration.Artifact.Glob}",
                RunConfigurationKind.Cordova =>
                    $"Cordova {CordovaAdapter.NormalizeMode(configuration.VariantName)} · {configuration.Artifact.Glob}",
                RunConfigurationKind.Apk =>
                    $"APK · {configuration.Artifact.Glob}",
                _ => $"Custom · {configuration.Steps.Count} step(s)",
            };
        }

        /// <summary>Gets the configuration.</summary>
        public RunConfiguration Configuration { get; }

        /// <summary>Gets the configuration identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the display name.</summary>
        public string Name { get; }

        /// <summary>Gets whether this is the default.</summary>
        public bool IsDefault { get; }

        /// <summary>Gets the configuration summary.</summary>
        public string Detail { get; }

        /// <summary>Creates a row from a configuration.</summary>
        public static RunConfigRow From(RunConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return new RunConfigRow(configuration);
        }
    }
}

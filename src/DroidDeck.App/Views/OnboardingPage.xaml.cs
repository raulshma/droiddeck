using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Toolchain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace DroidDeck.App.Views;

/// <summary>
/// The first-run onboarding flow: check tools, add a project, connect a device.
/// Every step can be skipped, progress is persisted, and finishing lands on Home.
/// </summary>
public sealed partial class OnboardingPage : Page, IRefreshablePage, IDisposable
{
    private const string ToolsStep = "tools";
    private const string ProjectStep = "project";
    private const string DeviceStep = "device";
    private const int StepCount = 3;

    private readonly IConfigurationService _configuration;
    private readonly IToolchainService _toolchain;
    private readonly IProjectService _projects;
    private readonly IDeviceService _devices;
    private readonly ILogger<OnboardingPage> _logger;
    private readonly DispatcherQueue? _dispatcher;
    private string _step = ToolsStep;
    private bool _checking;
    private bool _busy;
    private bool _completing;
    private bool _disposed;

    /// <summary>Creates the onboarding page.</summary>
    public OnboardingPage(
        IConfigurationService configuration,
        IToolchainService toolchain,
        IProjectService projects,
        IDeviceService devices,
        ILogger<OnboardingPage> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        _toolchain.SnapshotChanged += OnToolchainSnapshotChanged;
        _devices.DevicesChanged += OnDevicesChanged;
        _projects.ProjectsChanged += OnProjectsChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        _step = NormalizeStep(_configuration.Current.Settings.Onboarding.Step);
        ShowStep(_step, persist: false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _toolchain.SnapshotChanged -= OnToolchainSnapshotChanged;
        _devices.DevicesChanged -= OnDevicesChanged;
        _projects.ProjectsChanged -= OnProjectsChanged;
    }

    private void ShowStep(string step, bool persist)
    {
        _step = NormalizeStep(step);
        if (persist)
        {
            _ = PersistStepAsync(_step);
        }

        ProgressText.Text = $"Step {StepNumber(_step)} of {StepCount} · {StepTitle(_step)}";
        StepTitleText.Text = StepTitle(_step);
        ToolsPanel.Visibility = _step == ToolsStep ? Visibility.Visible : Visibility.Collapsed;
        ProjectPanel.Visibility = _step == ProjectStep ? Visibility.Visible : Visibility.Collapsed;
        DevicePanel.Visibility = _step == DeviceStep ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = _step != ToolsStep;
        NextButton.Content = _step == DeviceStep ? "Finish" : "Next";

        switch (_step)
        {
            case ToolsStep:
                UpdateToolsStep();
                break;
            case ProjectStep:
                UpdateProjectsStep();
                break;
            default:
                UpdateDevicesStep();
                break;
        }
    }

    private static string NormalizeStep(string? step) => step switch
    {
        ProjectStep => ProjectStep,
        DeviceStep => DeviceStep,
        _ => ToolsStep,
    };

    private static int StepNumber(string step) => step switch
    {
        ProjectStep => 2,
        DeviceStep => 3,
        _ => 1,
    };

    private static string StepTitle(string step) => step switch
    {
        ProjectStep => "Add a project",
        DeviceStep => "Connect a device",
        _ => "Check your tools",
    };

    private async Task PersistStepAsync(string step)
    {
        try
        {
            await _configuration.SetAsync(SettingsPaths.OnboardingStep, step);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The onboarding step could not be saved.");
        }
    }

    private async Task CompleteAsync(string outcome)
    {
        if (_completing)
        {
            return;
        }

        _completing = true;
        try
        {
            await _configuration.SetAsync(SettingsPaths.OnboardingCompleted, true);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Onboarding {Outcome}.", outcome);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Completing onboarding could not be saved.");
        }
        finally
        {
            _completing = false;
        }

        App.Instance.Navigate("home");
    }

    private void UpdateToolsStep()
    {
        var snapshot = _toolchain.CachedSnapshot;
        if (snapshot is null || snapshot.Tools.Count == 0)
        {
            ToolchainStatusText.Text = "Checking your tools…";
            ToolList.ItemsSource = Array.Empty<OnboardingToolRow>();
            return;
        }

        UpdateToolsSnapshot(snapshot);
    }

    private void UpdateToolsSnapshot(ToolchainSnapshot snapshot)
    {
        var rows = snapshot.Tools
            .Select(tool => new OnboardingToolRow(
                tool.DisplayName,
                tool.IsAvailable
                    ? string.IsNullOrWhiteSpace(tool.Version) ? "Available" : $"Available · {tool.Version}"
                    : "Missing",
                tool.IsAvailable ? string.Empty : tool.Guidance ?? tool.Error ?? string.Empty))
            .ToArray();
        ToolList.ItemsSource = rows;
        var available = rows.Count(row => row.IsAvailable);
        ToolchainStatusText.Text = available == rows.Length
            ? $"{available} of {rows.Length} tools available. Everything is ready."
            : $"{available} of {rows.Length} tools available. Missing tools show what to do next, or set paths in tool settings. Checked {snapshot.CheckedAt.ToLocalTime():g}.";
    }

    private void UpdateProjectsStep()
    {
        var projects = _projects.Projects.ToArray();
        ProjectsSummaryText.Text = projects.Length == 0
            ? "No projects added yet."
            : $"{projects.Length} project(s) added: " +
              string.Join(", ", projects.Take(3).Select(project => project.Name)) +
              (projects.Length > 3 ? ", …" : string.Empty);
    }

    private void UpdateDevicesStep()
    {
        var devices = _devices.Devices.ToArray();
        if (devices.Length == 0)
        {
            DeviceStatusText.Text = _devices.IsAdbConnected
                ? "No devices detected yet. Connect a device over USB to continue."
                : "ADB is not connected yet. Connect a device over USB and DroidDeck will detect it automatically.";
            DeviceGuidanceText.Text = string.Empty;
            DeviceHelpList.Visibility = Visibility.Visible;
            return;
        }

        var ready = devices.Where(device => device.State == DeviceState.Device).ToArray();
        if (ready.Length > 0)
        {
            DeviceStatusText.Text = ready.Length == 1
                ? $"{ready[0].DisplayName} ({ready[0].Serial}) is ready."
                : $"{ready.Length} devices are ready.";
            DeviceGuidanceText.Text = string.Empty;
            DeviceHelpList.Visibility = Visibility.Collapsed;
            return;
        }

        var attention = devices.FirstOrDefault(device =>
                            device.State is DeviceState.Unauthorized or DeviceState.Authorizing) ??
                        devices[0];
        DeviceStatusText.Text = $"{attention.DisplayName} ({attention.Serial}) is not ready yet.";
        DeviceGuidanceText.Text = DeviceStateGuidance.For(attention.State);
        DeviceHelpList.Visibility = Visibility.Visible;
    }

    private async void OnNextClick(object sender, RoutedEventArgs e) => await AdvanceAsync("finished");

    private async void OnSkipStepClick(object sender, RoutedEventArgs e) =>
        await AdvanceAsync("finished with the last step skipped");

    private async Task AdvanceAsync(string lastStepOutcome)
    {
        if (_step == DeviceStep)
        {
            await CompleteAsync(lastStepOutcome);
            return;
        }

        ShowStep(_step == ToolsStep ? ProjectStep : DeviceStep, persist: true);
    }

    private async void OnSkipSetupClick(object sender, RoutedEventArgs e) => await CompleteAsync("skipped");

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_step == DeviceStep)
        {
            ShowStep(ProjectStep, persist: true);
        }
        else if (_step == ProjectStep)
        {
            ShowStep(ToolsStep, persist: true);
        }
    }

    private async void OnCheckToolsClick(object sender, RoutedEventArgs e)
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        ToolchainStatusText.Text = "Checking your tools…";
        try
        {
            UpdateToolsSnapshot(await _toolchain.CheckAsync());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The onboarding toolchain check failed.");
            ToolchainStatusText.Text = "The toolchain check failed. Open tool settings to configure tool paths.";
        }
        finally
        {
            _checking = false;
        }
    }

    private void OnOpenToolSettingsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("settings");

    private void OnOpenProjectsClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("projects");

    private void OnOpenDevicesClick(object sender, RoutedEventArgs e) => App.Instance.Navigate("devices");

    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = CreateFolderPicker();
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        await RunBusyAsync("Adding the project", async () =>
        {
            var project = await _projects.AddAsync(folder.Path);
            ShowAction($"Added {project.Name} ({project.KindLabel}, {project.Confidence} confidence).");
            UpdateProjectsStep();
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
            ShowAction($"Added APK-only project {project.Name}.");
            UpdateProjectsStep();
        });
    }

    private async void OnScanFolderClick(object sender, RoutedEventArgs e)
    {
        var picker = CreateFolderPicker();
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
                ShowAction($"Scanned {folder.Path} and added {added.Count} project(s).");
            }
            else
            {
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
                        }

                        continue;
                    }

                    await _projects.AddAsync(candidate.RootPath, candidate.Kind);
                }

                ShowAction($"Added the selected project(s) from {folder.Path}.");
            }

            UpdateProjectsStep();
        });
    }

    private async void OnCheckDevicesClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        try
        {
            await _devices.RefreshAsync();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The onboarding device refresh failed.");
        }
        finally
        {
            _busy = false;
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

    private void OnToolchainSnapshotChanged(object? sender, ToolchainSnapshot snapshot) =>
        Dispatch(() =>
        {
            if (_step == ToolsStep)
            {
                UpdateToolsSnapshot(snapshot);
            }
        });

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Dispatch(UpdateDevicesStep);

    private void OnProjectsChanged(object? sender, EventArgs e) => Dispatch(UpdateProjectsStep);

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    private static FolderPicker CreateFolderPicker()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        InitializePicker(picker);
        return picker;
    }

    private static void InitializePicker(object picker)
    {
        var window = App.Instance.Services.GetRequiredService<MainWindow>();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
    }

    /// <summary>One tool row in the tools step.</summary>
    public sealed class OnboardingToolRow
    {
        /// <summary>Creates a tool row.</summary>
        public OnboardingToolRow(string name, string status, string guidance)
        {
            Name = name;
            Status = status;
            Guidance = guidance;
            IsAvailable = guidance.Length == 0;
            GuidanceVisibility = guidance.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Gets the tool display name.</summary>
        public string Name { get; }

        /// <summary>Gets the availability status.</summary>
        public string Status { get; }

        /// <summary>Gets fix guidance when the tool is missing.</summary>
        public string Guidance { get; }

        /// <summary>Gets whether the tool is available.</summary>
        public bool IsAvailable { get; }

        /// <summary>Gets whether the guidance line is visible.</summary>
        public Visibility GuidanceVisibility { get; }
    }
}

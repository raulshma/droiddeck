using System.Text;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Projects;
using DroidDeck.Core.Testing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;

namespace DroidDeck.App.Views;

/// <summary>
/// Runs the supported test runners, shows suites and cases with stack traces
/// and attachments, and reruns only the failing selection per device.
/// </summary>
public sealed partial class TestsPage : Page, IRefreshablePage, IDisposable
{
    private readonly IProjectService _projects;
    private readonly IDeviceService _devices;
    private readonly ITestRunService _testRuns;
    private readonly DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _cts;
    private TestRunSummary? _lastSummary;
    private bool _busy;
    private bool _disposed;

    /// <summary>Creates the tests page.</summary>
    public TestsPage(
        IProjectService projects,
        IDeviceService devices,
        ITestRunService testRuns)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _testRuns = testRuns ?? throw new ArgumentNullException(nameof(testRuns));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        _devices.DevicesChanged += OnDevicesChanged;
        RefreshDevices();
        _ = LoadProjectsAsync();
        ShowGuidance("Choose a project and runner, then run tests. Select devices for connected or multi-device runs.");
    }

    /// <inheritdoc />
    public void Refresh()
    {
        RefreshDevices();
        if (!_projects.IsInitialized)
        {
            _ = LoadProjectsAsync();
        }
        else
        {
            PopulateProjects();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _devices.DevicesChanged -= OnDevicesChanged;
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task LoadProjectsAsync()
    {
        try
        {
            await _projects.InitializeAsync().ConfigureAwait(false);
            Dispatch(PopulateProjects);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Dispatch(() => ShowGuidance($"Projects could not be loaded: {exception.Message}"));
        }
    }

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Dispatch(RefreshDevices);

    private void RefreshDevices()
    {
        var previous = DeviceList.SelectedItems
            .OfType<DeviceChoice>()
            .Select(choice => choice.Device.Serial)
            .ToHashSet(StringComparer.Ordinal);
        var choices = _devices.Devices
            .Where(device => device.CanRunActions)
            .Select(device => new DeviceChoice(device))
            .ToArray();
        DeviceList.ItemsSource = choices;
        foreach (var choice in choices.Where(choice => previous.Contains(choice.Device.Serial)))
        {
            DeviceList.SelectedItems.Add(choice);
        }

        if (choices.Length == 0)
        {
            ShowGuidance(_devices.LastError
                ?? "Connect a device to run connected tests, or run host tests without one.");
        }
    }

    private void PopulateProjects()
    {
        var previous = (ProjectCombo.SelectedItem as ProjectModel)?.Id;
        var projects = _projects.Projects;
        ProjectCombo.ItemsSource = projects;
        var selected = projects.FirstOrDefault(project =>
            string.Equals(project.Id, previous, StringComparison.Ordinal)) ?? (projects.Count == 0 ? null : projects[0]);
        ProjectCombo.SelectedItem = selected;
        if (projects.Count == 0)
        {
            ConfigCombo.ItemsSource = null;
            RunnerCombo.ItemsSource = null;
            CustomPanel.Visibility = Visibility.Collapsed;
            ShowGuidance("Add a project on the Projects page to run its tests.");
        }
    }

    private void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateConfigurations();
        PopulateRunners();
    }

    private void PopulateConfigurations()
    {
        var project = ProjectCombo.SelectedItem as ProjectModel;
        var configurations = project is null ? [] : GetConfigurations(project);
        var previous = (ConfigCombo.SelectedItem as RunConfiguration)?.Id;
        ConfigCombo.ItemsSource = configurations;
        ConfigCombo.SelectedItem = configurations.FirstOrDefault(configuration =>
            string.Equals(configuration.Id, previous, StringComparison.Ordinal))
            ?? configurations.FirstOrDefault(configuration => configuration.IsDefault)
            ?? configurations.FirstOrDefault();
    }

    private void PopulateRunners()
    {
        var project = ProjectCombo.SelectedItem as ProjectModel;
        var choices = project is null ? [] : GetRunnerChoices(project);
        var previous = (RunnerCombo.SelectedItem as RunnerChoice)?.Kind;
        RunnerCombo.ItemsSource = choices;
        RunnerCombo.SelectedItem = choices.FirstOrDefault(choice => choice.Kind == previous)
            ?? choices.FirstOrDefault();
        UpdateCustomPanel();
    }

    private void OnRunnerSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCustomPanel();

    private void UpdateCustomPanel()
    {
        var runner = RunnerCombo.SelectedItem as RunnerChoice;
        var isCustom = runner?.Kind == TestRunnerKind.Custom;
        CustomPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        if (runner?.Kind == TestRunnerKind.GradleConnected)
        {
            ShowGuidance("Select at least one device; connected tests run on every selected device.");
        }
        else if (isCustom && runner is not null)
        {
            ShowGuidance("Point the command at the test tool and at a JUnit XML result glob.");
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await _devices.RefreshAsync();
        Refresh();
    }

    private async void OnRunClick(object sender, RoutedEventArgs e) => await RunAsync(null);

    private async void OnRerunClick(object sender, RoutedEventArgs e)
    {
        var failed = _lastSummary?.CollectFailedTests() ?? [];
        if (failed.Count == 0)
        {
            ShowGuidance("There are no failing tests to rerun.");
            return;
        }

        await RunAsync(failed);
    }

    private async Task RunAsync(IReadOnlyList<TestTarget>? failedTests)
    {
        if (_busy)
        {
            return;
        }

        if (ProjectCombo.SelectedItem is not ProjectModel project ||
            ConfigCombo.SelectedItem is not RunConfiguration configuration ||
            RunnerCombo.SelectedItem is not RunnerChoice runner)
        {
            ShowGuidance("Choose a project, a run configuration, and a runner.");
            return;
        }

        CustomTestSettings? custom = null;
        if (runner.Kind == TestRunnerKind.Custom)
        {
            custom = BuildCustomSettings();
            if (custom is null)
            {
                ShowResult(
                    false,
                    "A custom test command and result glob are required.",
                    "Fill in the command and the result glob in the custom panel.");
                return;
            }
        }

        var request = new TestRunRequest
        {
            Project = project,
            Configuration = configuration,
            Runner = runner.Kind,
            DeviceSerials = DeviceList.SelectedItems
                .OfType<DeviceChoice>()
                .Select(choice => choice.Device.Serial)
                .ToArray(),
            FailedTests = failedTests ?? [],
            Custom = custom,
        };

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        _lastSummary = null;
        ResultsTree.RootNodes.Clear();
        ClearDetails();
        OutputBox.Text = string.Empty;
        TestSummaryText.Text = request.IsRerun
            ? $"Rerunning {request.FailedTests.Count} failing test(s)..."
            : "Running...";
        try
        {
            var progress = new Progress<TestRunProgress>(OnProgress);
            var summary = await _testRuns.RunAsync(request, progress, token);
            _lastSummary = summary;
            RenderSummary(summary);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            TestSummaryText.Text = "Cancelled.";
            ShowResult(false, "The test run was cancelled.", null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            TestSummaryText.Text = "The test run failed.";
            ShowResult(false, $"The test run failed: {exception.Message}", null);
        }
        finally
        {
            ProgressText.Text = string.Empty;
            SetBusy(false);
        }
    }

    private void OnProgress(TestRunProgress progress)
    {
        ProgressText.Text = string.IsNullOrWhiteSpace(progress.DeviceSerial)
            ? progress.Message
            : $"{progress.DeviceSerial}: {progress.Message}";
    }

    private void RenderSummary(TestRunSummary summary)
    {
        var errors = summary.Devices
            .SelectMany(device => device.Errors.Select(error =>
                string.IsNullOrWhiteSpace(device.DeviceSerial) ? error : $"{device.DeviceSerial}: {error}"))
            .ToArray();
        var failures = summary.CollectFailedTests();
        TestSummaryText.Text = BuildSummaryText(summary);
        ResultsTree.RootNodes.Clear();
        foreach (var device in summary.Devices)
        {
            var deviceNode = new TreeViewNode
            {
                Content = new DeviceNode(device),
                IsExpanded = true,
            };
            foreach (var suite in device.Suites)
            {
                var suiteNode = new TreeViewNode
                {
                    Content = new SuiteNode(suite),
                    IsExpanded = failures.Count > 0 || device.Suites.Count == 1,
                };
                foreach (var test in suite.Cases)
                {
                    suiteNode.Children.Add(new TreeViewNode { Content = new CaseNode(test) });
                }

                deviceNode.Children.Add(suiteNode);
            }

            ResultsTree.RootNodes.Add(deviceNode);
        }

        var output = new StringBuilder();
        foreach (var device in summary.Devices)
        {
            output.Append("===== ").AppendLine(device.DisplayName);
            if (!string.IsNullOrWhiteSpace(device.Output))
            {
                output.AppendLine(device.Output.TrimEnd());
            }

            foreach (var error in device.Errors)
            {
                output.Append("! ").AppendLine(error);
            }
        }

        OutputBox.Text = output.ToString();
        RerunButton.IsEnabled = failures.Count > 0;
        if (summary.IsCanceled)
        {
            ShowResult(false, "The test run was cancelled.", null);
        }
        else if (errors.Length > 0)
        {
            ShowResult(
                false,
                errors[0],
                errors.Length > 1
                    ? $"{errors.Length - 1} more error(s). Open the command output for details."
                    : null);
        }
        else if (failures.Count > 0)
        {
            ActionBar.Severity = InfoBarSeverity.Warning;
            ActionBar.Message =
                $"{summary.FailedCount + summary.ErroredCount} test(s) failed across {summary.Devices.Count} device(s). Rerun failed to run only those tests.";
            ActionBar.IsOpen = true;
        }
        else
        {
            ShowResult(
                true,
                summary.TotalCount == 0
                    ? "The runner did not report any tests."
                    : $"All {summary.TotalCount} test(s) passed.",
                null);
        }
    }

    private static string BuildSummaryText(TestRunSummary summary)
    {
        if (summary.Devices.Count == 0)
        {
            return "No results.";
        }

        var deviceLabel = summary.Devices.Count == 1
            ? summary.Devices[0].DisplayName
            : $"{summary.Devices.Count} devices";
        return $"{deviceLabel} · {summary.TotalCount} tests · {summary.PassedCount} passed · " +
               $"{summary.FailedCount + summary.ErroredCount} failed · {summary.SkippedCount} skipped";
    }

    private CustomTestSettings? BuildCustomSettings()
    {
        var command = CustomCommandBox.Text.Trim();
        var resultGlob = CustomResultGlobBox.Text.Trim();
        if (command.Length == 0 || resultGlob.Length == 0)
        {
            return null;
        }

        var workingDirectory = CustomWorkingDirectoryBox.Text.Trim();
        return new CustomTestSettings
        {
            Command = command,
            Arguments = CustomArgumentsBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            WorkingDirectory = workingDirectory.Length == 0 ? null : workingDirectory,
            ResultGlob = resultGlob,
            RequiresDevice = CustomRequiresDeviceCheck.IsChecked == true,
        };
    }

    private void OnResultSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        switch (sender.SelectedNode?.Content)
        {
            case CaseNode node:
                ShowCaseDetails(node.Test);
                break;
            case SuiteNode node:
                ShowSuiteDetails(node.Suite);
                break;
            case DeviceNode node:
                ShowDeviceDetails(node.Device);
                break;
            default:
                ClearDetails();
                break;
        }
    }

    private void ShowCaseDetails(TestCaseResult test)
    {
        DetailTitle.Text = test.FullyQualifiedName;
        var location = string.IsNullOrWhiteSpace(test.FilePath)
            ? string.Empty
            : test.Line is null
                ? $" · {test.FilePath}"
                : $" · {test.FilePath}:{test.Line}";
        DetailStatus.Text = $"{Describe(test.Status)} · {FormatDuration(test.Duration)}{location}";
        ShowText(DetailMessageBox, "Message", test.Message);
        ShowText(DetailStackBox, "Stack trace", test.StackTrace);
        AttachmentsList.ItemsSource = test.Attachments;
        var hasAttachments = test.Attachments.Count > 0;
        AttachmentLabel.Visibility = hasAttachments ? Visibility.Visible : Visibility.Collapsed;
        AttachmentsList.Visibility = hasAttachments ? Visibility.Visible : Visibility.Collapsed;
        OpenAttachmentButton.Visibility = hasAttachments ? Visibility.Visible : Visibility.Collapsed;
        OpenAttachmentButton.IsEnabled = false;
        AttachmentsList.SelectedItem = null;
    }

    private void ShowSuiteDetails(TestSuiteResult suite)
    {
        DetailTitle.Text = suite.Name;
        DetailStatus.Text =
            $"{Describe(suite.Status)} · {suite.Cases.Count} tests · {suite.PassedCount} passed · " +
            $"{suite.FailedCount + suite.ErroredCount} failed · {suite.SkippedCount} skipped · " +
            FormatDuration(suite.Duration);
        HideCaseDetails();
    }

    private void ShowDeviceDetails(TestRunDeviceResult device)
    {
        DetailTitle.Text = device.DisplayName;
        DetailStatus.Text =
            $"{device.Outcome} · {device.TotalCount} tests · {device.PassedCount} passed · " +
            $"{device.FailedCount + device.ErroredCount} failed · {device.SkippedCount} skipped";
        ShowText(
            DetailMessageBox,
            "Errors",
            device.Errors.Count == 0 ? null : string.Join(Environment.NewLine, device.Errors));
        DetailStackBox.Visibility = Visibility.Collapsed;
        HideAttachments();
    }

    private void ClearDetails()
    {
        DetailTitle.Text = "Nothing selected";
        DetailStatus.Text = string.Empty;
        DetailMessageBox.Visibility = Visibility.Collapsed;
        DetailStackBox.Visibility = Visibility.Collapsed;
        HideAttachments();
    }

    private void HideCaseDetails()
    {
        DetailMessageBox.Visibility = Visibility.Collapsed;
        HideText(DetailStackBox);
        HideAttachments();
    }

    private void HideAttachments()
    {
        AttachmentLabel.Visibility = Visibility.Collapsed;
        AttachmentsList.Visibility = Visibility.Collapsed;
        AttachmentsList.ItemsSource = null;
        OpenAttachmentButton.Visibility = Visibility.Collapsed;
        OpenAttachmentButton.IsEnabled = false;
    }

    private static void ShowText(TextBox box, string header, string? text)
    {
        box.Header = header;
        box.Text = text ?? string.Empty;
        box.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void HideText(TextBox box)
    {
        box.Text = string.Empty;
        box.Visibility = Visibility.Collapsed;
    }

    private void OnAttachmentSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OpenAttachmentButton.IsEnabled = AttachmentsList.SelectedItem is TestAttachment;

    private async void OnOpenAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (AttachmentsList.SelectedItem is not TestAttachment attachment)
        {
            return;
        }

        if (attachment.IsFile)
        {
            if (attachment.Path is not { } path || !File.Exists(path))
            {
                ShowResult(false, "The attachment file no longer exists.", attachment.Path);
                return;
            }

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                await Launcher.LaunchFileAsync(file);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ShowResult(false, $"The attachment could not be opened: {exception.Message}", null);
            }

            return;
        }

        if (string.IsNullOrEmpty(attachment.Content))
        {
            return;
        }

        var viewer = new ContentDialog
        {
            Title = attachment.Name,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = attachment.Content,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    FontFamily = new FontFamily("Consolas"),
                },
            },
        };
        await viewer.ShowAsync();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        RunButton.IsEnabled = !busy;
        RerunButton.IsEnabled = !busy && (_lastSummary?.CollectFailedTests().Count ?? 0) > 0;
    }

    private void ShowResult(bool succeeded, string message, string? guidance)
    {
        ActionBar.Severity = succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
        if (guidance is { Length: > 0 })
        {
            ShowGuidance(guidance);
        }
    }

    private void ShowGuidance(string? guidance)
    {
        GuidanceBar.Message = guidance ?? string.Empty;
        GuidanceBar.IsOpen = guidance is { Length: > 0 };
    }

    private void Dispatch(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
    }

    private static List<RunConfiguration> GetConfigurations(ProjectModel project)
    {
        if (project.RunConfigurations.Count > 0)
        {
            return project.RunConfigurations;
        }

        var kind = project.Kind switch
        {
            ProjectKind.AndroidGradle or ProjectKind.KotlinMultiplatform
                or ProjectKind.Capacitor or ProjectKind.Cordova => RunConfigurationKind.Gradle,
            ProjectKind.Flutter => RunConfigurationKind.Flutter,
            ProjectKind.ReactNative => RunConfigurationKind.ReactNative,
            ProjectKind.Maui => RunConfigurationKind.Maui,
            _ => RunConfigurationKind.Custom,
        };
        return
        [
            new RunConfiguration
            {
                Id = "tests",
                Name = "Tests",
                Kind = kind,
                VariantName = "debug",
                IsDefault = true,
            },
        ];
    }

    private static List<RunnerChoice> GetRunnerChoices(ProjectModel project) => project.Kind switch
    {
        ProjectKind.AndroidGradle or ProjectKind.KotlinMultiplatform or ProjectKind.Capacitor or ProjectKind.Cordova =>
        [
            new(TestRunnerKind.GradleUnit, "Gradle unit tests"),
            new(TestRunnerKind.GradleConnected, "Gradle connected tests"),
            new(TestRunnerKind.Custom, "Custom command"),
        ],
        ProjectKind.Flutter =>
        [
            new(TestRunnerKind.Flutter, "Flutter tests"),
            new(TestRunnerKind.Custom, "Custom command"),
        ],
        ProjectKind.ReactNative =>
        [
            new(TestRunnerKind.Jest, "Jest tests"),
            new(TestRunnerKind.Custom, "Custom command"),
        ],
        ProjectKind.Maui =>
        [
            new(TestRunnerKind.DotNet, ".NET tests"),
            new(TestRunnerKind.Custom, "Custom command"),
        ],
        _ => [new(TestRunnerKind.Custom, "Custom command")],
    };

    private static string Describe(TestStatus status) => status switch
    {
        TestStatus.Passed => "Passed",
        TestStatus.Failed => "Failed",
        TestStatus.Errored => "Errored",
        _ => "Skipped",
    };

    private static string FormatDuration(TimeSpan? duration) => duration switch
    {
        null => "no duration",
        { TotalSeconds: >= 1 } value => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{value.TotalSeconds:F2}s"),
        { } value => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{value.TotalMilliseconds:F0} ms"),
    };

    /// <summary>A device shaped for the multi-select list.</summary>
    public sealed class DeviceChoice
    {
        /// <summary>Creates a choice.</summary>
        public DeviceChoice(DeviceInfo device)
        {
            Device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>Gets the device.</summary>
        public DeviceInfo Device { get; }

        /// <summary>Gets the picker label.</summary>
        public string Display => $"{Device.DisplayName} · {Device.Serial}";
    }

    /// <summary>A runner shaped for the picker.</summary>
    public sealed class RunnerChoice
    {
        /// <summary>Creates a runner choice.</summary>
        public RunnerChoice(TestRunnerKind kind, string label)
        {
            Kind = kind;
            Label = label;
        }

        /// <summary>Gets the runner kind.</summary>
        public TestRunnerKind Kind { get; }

        /// <summary>Gets the picker label.</summary>
        public string Label { get; }
    }

    private sealed class DeviceNode
    {
        public DeviceNode(TestRunDeviceResult device) => Device = device;

        public TestRunDeviceResult Device { get; }

        public override string ToString()
        {
            var failed = Device.FailedCount + Device.ErroredCount;
            var errors = Device.Errors.Count == 0 ? string.Empty : $" · {Device.Errors.Count} error(s)";
            return $"{Device.DisplayName} · {Device.TotalCount} tests · {Device.PassedCount} passed · {failed} failed{errors}";
        }
    }

    private sealed class SuiteNode
    {
        public SuiteNode(TestSuiteResult suite) => Suite = suite;

        public TestSuiteResult Suite { get; }

        public override string ToString()
        {
            var failed = Suite.FailedCount + Suite.ErroredCount;
            return $"{Suite.Name} · {Suite.Cases.Count} tests · {failed} failed · {FormatDuration(Suite.Duration)}";
        }
    }

    private sealed class CaseNode
    {
        public CaseNode(TestCaseResult test) => Test = test;

        public TestCaseResult Test { get; }

        public override string ToString() =>
            $"[{Describe(Test.Status).ToUpperInvariant()}] {Test.Name} · {FormatDuration(Test.Duration)}";
    }
}

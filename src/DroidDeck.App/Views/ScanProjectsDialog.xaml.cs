using DroidDeck.Core.Configuration;
using DroidDeck.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.App.Views;

/// <summary>Scans a folder for nested projects and lets the user choose what to add.</summary>
public sealed partial class ScanProjectsDialog : ContentDialog
{
    private readonly IProjectService _projects;
    private readonly string _rootPath;
    private bool _scanning;
    private bool _closed;

    /// <summary>Creates a scan dialog for a repository root.</summary>
    public ScanProjectsDialog(IProjectService projects, string rootPath, ProjectsSettings settings)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(settings);
        _rootPath = rootPath;

        InitializeComponent();
        DepthBox.Value = settings.ScanDepth;
        TimeoutBox.Value = settings.ScanTimeoutSeconds;
        NestedCheck.IsChecked = settings.ScanMonorepos;
        IgnoreBox.Text = string.Join(", ", settings.IgnoreFolders);
        Opened += OnOpened;
        Closing += OnClosing;
    }

    /// <summary>Gets the scan options currently shown in the dialog.</summary>
    public ProjectScanOptions Options => new()
    {
        MaxDepth = ReadInt(DepthBox, 3, 0, 12),
        MaxFolders = 2_000,
        Timeout = TimeSpan.FromSeconds(ReadInt(TimeoutBox, 5, 1, 60)),
        IgnoreFolders = ParseIgnoreFolders(),
        IncludeRoot = true,
        DetectNestedGradleProjects = NestedCheck.IsChecked == true,
    };

    /// <summary>Gets the candidates the user selected.</summary>
    public IReadOnlyList<ProjectScanCandidate> SelectedCandidates =>
        (CandidateList.ItemsSource as IEnumerable<CandidateRow>)?
        .Where(row => row.IsSelected && row.CanSelect)
        .Select(row => row.Candidate)
        .ToArray() ?? [];

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args) => await ScanAsync();

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        _closed = true;
    }

    private async void OnRescanClick(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_scanning || _closed)
        {
            return;
        }

        _scanning = true;
        IsPrimaryButtonEnabled = false;
        IsSecondaryButtonEnabled = false;
        RescanButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Scanning {_rootPath}…";
        try
        {
            var result = await _projects.ScanAsync(_rootPath, Options);
            if (_closed)
            {
                return;
            }

            var rows = result.Candidates
                .Select(candidate => new CandidateRow(candidate))
                .ToArray();
            CandidateList.ItemsSource = rows;
            var selectable = rows.Count(row => row.CanSelect);
            StatusText.Text =
                $"Found {rows.Length} candidate(s) in {result.Duration.TotalSeconds:F1}s. " +
                $"{selectable} can be added." +
                (result.WasTruncated
                    ? $" Scan stopped early: {result.TruncationReason ?? "a limit was reached"}."
                    : string.Empty);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or DirectoryNotFoundException)
        {
            if (_closed)
            {
                return;
            }

            CandidateList.ItemsSource = null;
            StatusText.Text = "The scan did not finish.";
            ErrorText.Text = exception.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _scanning = false;
            if (!_closed)
            {
                IsPrimaryButtonEnabled = true;
                IsSecondaryButtonEnabled = true;
                RescanButton.IsEnabled = true;
            }
        }
    }

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (SelectedCandidates.Count == 0)
        {
            args.Cancel = true;
            ErrorText.Text = "Select at least one project to add, or use Add all.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private List<string> ParseIgnoreFolders()
    {
        var folders = IgnoreBox.Text
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return folders.Count > 0 ? folders : [.. ProjectScanOptions.Default.IgnoreFolders];
    }

    private static int ReadInt(NumberBox box, int fallback, int minimum, int maximum)
    {
        var value = double.IsNaN(box.Value) ? fallback : (int)Math.Round(box.Value);
        return Math.Clamp(value, minimum, maximum);
    }

    /// <summary>One candidate row in the scan result list.</summary>
    public sealed class CandidateRow
    {
        /// <summary>Creates a row from a scan candidate.</summary>
        public CandidateRow(ProjectScanCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            Candidate = candidate;
            Name = candidate.Name;
            Badge = $"{ProjectModel.GetKindLabel(candidate.Kind)} · {candidate.Confidence}";
            BadgeBrush = BrushForConfidence(candidate.Confidence);
            CanSelect = !candidate.AlreadyAdded;
            IsSelected = CanSelect;
            Detail = BuildDetail(candidate);
        }

        /// <summary>Gets the underlying candidate.</summary>
        public ProjectScanCandidate Candidate { get; }

        /// <summary>Gets the display name.</summary>
        public string Name { get; }

        /// <summary>Gets the kind and confidence badge.</summary>
        public string Badge { get; }

        /// <summary>Gets the badge brush.</summary>
        public Brush BadgeBrush { get; }

        /// <summary>Gets the path and detection detail.</summary>
        public string Detail { get; }

        /// <summary>Gets whether the candidate can be added.</summary>
        public bool CanSelect { get; }

        /// <summary>Gets or sets whether the candidate is selected.</summary>
        public bool IsSelected { get; set; }

        private static string BuildDetail(ProjectScanCandidate candidate)
        {
            var parts = new List<string> { candidate.RootPath };
            if (!string.IsNullOrWhiteSpace(candidate.ApplicationId))
            {
                parts.Add(candidate.ApplicationId);
            }

            if (candidate.VariantCount > 0)
            {
                parts.Add($"{candidate.VariantCount} variant(s)");
            }

            if (candidate.AlreadyAdded)
            {
                parts.Add("Already added");
            }

            return string.Join(" · ", parts);
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
    }
}

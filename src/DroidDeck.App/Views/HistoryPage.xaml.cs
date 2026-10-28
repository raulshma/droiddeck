using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using DroidDeck.App.Services;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace DroidDeck.App.Views;

/// <summary>Lists persisted run sessions and supports read-only reopen, export, and deletion.</summary>
public sealed partial class HistoryPage : Page, IRefreshablePage
{
    private readonly ISessionStore _store;
    private readonly SessionWorkspace _workspace;
    private readonly IConfigurationService _configuration;
    private readonly ILogger<HistoryPage> _logger;
    private bool _loading;

    /// <summary>Creates the history page.</summary>
    public HistoryPage(
        ISessionStore store,
        SessionWorkspace workspace,
        IConfigurationService configuration,
        ILogger<HistoryPage> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        InitializeComponent();
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        UpdateRetentionText();
        _ = LoadAsync();
    }

    private void UpdateRetentionText()
    {
        var logging = _configuration.Current.Settings.Logging;
        var gigabytes = logging.MaxSessionBytes / 1024.0 / 1024.0 / 1024.0;
        RetentionText.Text =
            $"Retention: {logging.RetentionDays} day(s) or {gigabytes:F1} GB. " +
            "Older sessions are removed at startup and when retention is applied.";
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            var summaries = await _store.ListAsync();
            var rows = summaries.Select(SessionSummaryRow.From).ToArray();
            HistoryList.ItemsSource = rows;
            EmptyPanel.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Session history could not be loaded.");
            ShowAction($"Session history could not be loaded: {exception.Message}", isError: true);
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        Refresh();
        ShowAction("Session history refreshed.");
    }

    private async void OnApplyRetentionClick(object sender, RoutedEventArgs e)
    {
        var logging = _configuration.Current.Settings.Logging;
        try
        {
            var result = await _store.ApplyRetentionAsync(logging.RetentionDays, logging.MaxSessionBytes);
            ShowAction(result.Summary);
            await LoadAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Session retention could not be applied.");
            ShowAction($"Retention could not be applied: {exception.Message}", isError: true);
        }
    }

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionSummaryRow row)
        {
            return;
        }

        var session = await LoadSessionAsync(row.Id);
        if (session is null)
        {
            return;
        }

        _workspace.OpenHistorySession(session);
        App.Instance.Navigate("session");
    }

    private async void OnExportTextClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionSummaryRow row)
        {
            await ExportAsync(row, SessionExportFormat.Text);
        }
    }

    private async void OnExportJsonClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionSummaryRow row)
        {
            await ExportAsync(row, SessionExportFormat.Json);
        }
    }

    private async Task ExportAsync(SessionSummaryRow row, SessionExportFormat format)
    {
        var session = await LoadSessionAsync(row.Id);
        if (session is null)
        {
            return;
        }

        try
        {
            var content = await _store.ExportAsync(session, format, RedactCheck.IsChecked == true);
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"{Sanitize(row.Title)}-{row.Id[..Math.Min(8, row.Id.Length)]}",
                DefaultFileExtension = format == SessionExportFormat.Json ? ".json" : ".txt",
            };
            picker.FileTypeChoices.Add(
                format == SessionExportFormat.Json ? "JSON" : "Text",
                new List<string> { format == SessionExportFormat.Json ? ".json" : ".txt" });
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return;
            }

            await File.WriteAllTextAsync(file.Path, content);
            ShowAction($"Exported the session to {file.Path}.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or COMException)
        {
            _logger.LogWarning(exception, "The session could not be exported.");
            ShowAction($"The session could not be exported: {exception.Message}", isError: true);
        }
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SessionSummaryRow row)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete session?",
            Content = $"Delete \"{row.Title}\" from history? The session files are removed.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _store.DeleteAsync(row.Id);
            if (string.Equals(_workspace.SessionId, row.Id, StringComparison.Ordinal))
            {
                _workspace.ClearSession();
            }

            await LoadAsync();
            ShowAction($"Deleted \"{row.Title}\" from history.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The session could not be deleted.");
            ShowAction($"The session could not be deleted: {exception.Message}", isError: true);
        }
    }

    private async Task<RunSession?> LoadSessionAsync(string sessionId)
    {
        try
        {
            var session = await _store.LoadAsync(sessionId);
            if (session is null)
            {
                ShowAction("The session files are no longer available.", isError: true);
            }

            return session;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "The session {SessionId} could not be loaded.", sessionId);
            ShowAction($"The session could not be loaded: {exception.Message}", isError: true);
            return null;
        }
    }

    private void ShowAction(string message, bool isError = false)
    {
        ActionBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray())
            .Trim('-', ' ');
        return cleaned.Length == 0 ? "session" : cleaned;
    }

    private static void InitializePicker(object picker)
    {
        var window = App.Instance.Services.GetRequiredService<MainWindow>();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
    }

    private static Brush BrushForState(RunSessionState state)
    {
        var key = state switch
        {
            RunSessionState.Succeeded => "SystemFillColorSuccessBrush",
            RunSessionState.Failed or RunSessionState.PartiallyFailed => "SystemFillColorCriticalBrush",
            RunSessionState.Cancelled or RunSessionState.Stopping => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        return Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    /// <summary>One persisted session summary shaped for the history list.</summary>
    public sealed class SessionSummaryRow
    {
        private SessionSummaryRow(SessionSummary summary)
        {
            Id = summary.Id;
            Title = string.IsNullOrWhiteSpace(summary.ConfigurationName)
                ? summary.ProjectName
                : $"{summary.ProjectName} / {summary.ConfigurationName}";
            var duration = summary.Duration is { } elapsed
                ? $" · {elapsed.TotalSeconds:F1}s"
                : string.Empty;
            Subtitle =
                $"{summary.DeviceName ?? summary.DeviceSerial} · " +
                $"{summary.StartedAt.ToLocalTime():g}{duration} · {summary.StepCount} step(s)";
            StateLabel = summary.State.ToString();
            StateBrush = BrushForState(summary.State);
            Error = summary.Error ?? string.Empty;
            HasError = Error.Length > 0;
            var safeId = new string(summary.Id
                .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_')
                .ToArray());
            OpenId = $"HistoryOpen_{safeId}";
            ExportTextId = $"HistoryExportText_{safeId}";
            ExportJsonId = $"HistoryExportJson_{safeId}";
            DeleteId = $"HistoryDelete_{safeId}";
        }

        /// <summary>Gets the session identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the project and configuration title.</summary>
        public string Title { get; }

        /// <summary>Gets the device, start time, duration, and step count.</summary>
        public string Subtitle { get; }

        /// <summary>Gets the final state label.</summary>
        public string StateLabel { get; }

        /// <summary>Gets the final state brush.</summary>
        public Brush StateBrush { get; }

        /// <summary>Gets the final error, when one was recorded.</summary>
        public string Error { get; }

        /// <summary>Gets whether an error was recorded.</summary>
        public bool HasError { get; }

        /// <summary>Gets the open button automation identifier.</summary>
        public string OpenId { get; }

        /// <summary>Gets the text export button automation identifier.</summary>
        public string ExportTextId { get; }

        /// <summary>Gets the JSON export button automation identifier.</summary>
        public string ExportJsonId { get; }

        /// <summary>Gets the delete button automation identifier.</summary>
        public string DeleteId { get; }

        /// <summary>Creates a row from a session summary.</summary>
        public static SessionSummaryRow From(SessionSummary summary)
        {
            ArgumentNullException.ThrowIfNull(summary);
            return new SessionSummaryRow(summary);
        }
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DroidDeck.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace DroidDeck.App.Views;

/// <summary>Shows the in-memory application log for the current session.</summary>
public sealed partial class ActivityPage : Page, IRefreshablePage, IDisposable
{
    private const int MaxRows = 500;
    private const int MaxCopyLines = 200;

    private readonly BoundedLogBuffer _buffer;
    private readonly IDiagnosticsService _diagnostics;
    private readonly ILogger<ActivityPage> _logger;
    private readonly ObservableCollection<ActivityRow> _rows = [];
    private readonly DispatcherTimer _timer;
    private long _lastWritten = -1;
    private bool _disposed;

    /// <summary>Creates the activity page.</summary>
    public ActivityPage(
        BoundedLogBuffer buffer,
        IDiagnosticsService diagnostics,
        ILogger<ActivityPage> logger)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeComponent();
        ActivityList.ItemsSource = _rows;
        LevelFilter.Items.Add(new ComboBoxItem { Content = "All levels", Tag = null });
        LevelFilter.Items.Add(new ComboBoxItem { Content = "Information+", Tag = LogLevel.Information });
        LevelFilter.Items.Add(new ComboBoxItem { Content = "Warnings+", Tag = LogLevel.Warning });
        LevelFilter.Items.Add(new ComboBoxItem { Content = "Errors+", Tag = LogLevel.Error });
        LevelFilter.SelectedIndex = 0;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTimerTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RefreshRows(force: true);
    }

    /// <inheritdoc />
    public void Refresh() => RefreshRows(force: true);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshRows(force: true);
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timer.Stop();

    private void OnTimerTick(object? sender, object e) => RefreshRows(force: false);

    private void RefreshRows(bool force)
    {
        var written = _buffer.TotalWritten;
        if (!force && written == _lastWritten)
        {
            return;
        }

        _lastWritten = written;
        var minimum = GetMinimumLevel();
        var entries = _buffer.Snapshot();
        _rows.Clear();
        for (var index = entries.Count - 1; index >= 0 && _rows.Count < MaxRows; index--)
        {
            if (minimum is { } level && entries[index].Level < level)
            {
                continue;
            }

            _rows.Add(new ActivityRow(entries[index]));
        }

        var summary = _rows.Count == entries.Count
            ? $"{entries.Count} entries"
            : $"{entries.Count} entries · showing {_rows.Count}";
        if (minimum is { } filter)
        {
            summary += $" at {filter} or higher";
        }

        if (_buffer.DroppedCount > 0)
        {
            summary += $" · {_buffer.DroppedCount} older entries dropped";
        }

        SummaryText.Text = summary;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private LogLevel? GetMinimumLevel() =>
        (LevelFilter.SelectedItem as ComboBoxItem)?.Tag is LogLevel level ? level : null;

    private void OnLevelFilterChanged(object sender, SelectionChangedEventArgs e) => RefreshRows(force: true);

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshRows(force: true);

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var minimum = GetMinimumLevel();
            var entries = _buffer.Snapshot()
                .Where(entry => minimum is null || entry.Level >= minimum)
                .TakeLast(MaxCopyLines)
                .ToArray();
            var builder = new StringBuilder();
            builder.AppendLine("DroidDeck recent activity");
            builder.AppendLine(CultureInfo.InvariantCulture, $"Diagnostics ID: {_diagnostics.DiagnosticsId}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"Session ID: {_diagnostics.SessionId}");
            builder.AppendLine();
            foreach (var entry in entries)
            {
                builder.AppendLine(Redactor.Redact(entry.Format()));
            }

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(builder.ToString());
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowAction($"Copied {entries.Length} log entries.");
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Recent activity could not be copied.");
            ShowAction("The log could not be copied to the clipboard.", isError: true);
        }
    }

    private void ShowAction(string message, bool isError = false)
    {
        ActionBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        ActionBar.Message = $"{message} Diagnostics ID {_diagnostics.DiagnosticsId}.";
        ActionBar.IsOpen = true;
    }

    /// <summary>One log entry shaped for the activity list.</summary>
    public sealed class ActivityRow
    {
        /// <summary>Creates a row from a log entry.</summary>
        public ActivityRow(LogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            Time = entry.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            Level = entry.Level.ToString();
            Category = entry.Category;
            Message = entry.Message;
            Tooltip = entry.Format();
            LevelBrush = ResolveBrush(entry.Level switch
            {
                LogLevel.Critical or LogLevel.Error => "SystemFillColorCriticalBrush",
                LogLevel.Warning => "SystemFillColorCautionBrush",
                _ => "TextFillColorSecondaryBrush",
            });
            LevelBackground = ResolveBrush("SubtleFillColorSecondaryBrush");
        }

        /// <summary>Gets the local time label.</summary>
        public string Time { get; }

        /// <summary>Gets the level label.</summary>
        public string Level { get; }

        /// <summary>Gets the logger category.</summary>
        public string Category { get; }

        /// <summary>Gets the formatted message.</summary>
        public string Message { get; }

        /// <summary>Gets the full entry text for a tooltip.</summary>
        public string Tooltip { get; }

        /// <summary>Gets the level foreground brush.</summary>
        public Brush LevelBrush { get; }

        /// <summary>Gets the level background brush.</summary>
        public Brush LevelBackground { get; }

        private static Brush ResolveBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
                ? brush
                : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}

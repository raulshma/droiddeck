using System.Globalization;
using System.Text;
using System.Xml;
using DroidDeck.App.Services;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Screen;
using DroidDeck.Core.Toolbox;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace DroidDeck.App.Views;

/// <summary>
/// Dumps the current Android view hierarchy with uiautomator and exposes the
/// flattened tree, screenshot bounds overlay, tap, selectors, and filters.
/// </summary>
public sealed partial class InspectorPage : Page, IRefreshablePage, IDisposable
{
    private const string DumpCommand = "uiautomator dump /sdcard/window_dump.xml";
    private const string ReadCommand = "cat /sdcard/window_dump.xml";

    private readonly IDeviceToolboxService _toolbox;
    private readonly IDeviceService _devices;
    private readonly IThumbnailService _thumbnails;
    private readonly IConfigurationService _configuration;
    private readonly SessionWorkspace _workspace;
    private readonly DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _cts;
    private UiHierarchy? _hierarchy;
    private IReadOnlyList<UiHierarchyNode> _visibleNodes = [];
    private UiHierarchyNode? _selectedNode;
    private bool _busy;
    private bool _disposed;
    private bool _initializing;

    /// <summary>Creates the inspector page.</summary>
    public InspectorPage(
        IDeviceToolboxService toolbox,
        IDeviceService devices,
        IThumbnailService thumbnails,
        IConfigurationService configuration,
        SessionWorkspace workspace)
    {
        _toolbox = toolbox ?? throw new ArgumentNullException(nameof(toolbox));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        SyncSettings();

        _devices.DevicesChanged += OnDevicesChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh()
    {
        SyncSettings();
        RefreshDevices();
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

    private DeviceInfo? SelectedDevice => DeviceCombo.SelectedItem as DeviceInfo;

    private string? SelectedSerial => SelectedDevice?.Serial;

    private InspectorSettings Settings => _configuration.Current.Settings.Inspector;

    private void SyncSettings()
    {
        _initializing = true;
        try
        {
            var inspector = Settings;
            DepthBox.Value = inspector.EffectiveMaxDepth;
            IncludeInvisibleToggle.IsOn = inspector.IncludeInvisible;
        }
        finally
        {
            _initializing = false;
        }
    }

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Dispatch(RefreshDevices);

    private void RefreshDevices()
    {
        var target = DeviceTargetSelection.Resolve(
            _devices.Devices,
            SelectedSerial,
            _workspace.DeviceSerial);
        DeviceCombo.ItemsSource = target.Usable;
        DeviceCombo.SelectedItem = target.Selected;

        if (target.Usable.Count == 0)
        {
            ShowGuidance(_devices.LastError
                ?? "Connect a device with USB debugging enabled and unlock it before dumping the hierarchy.");
        }
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedDevice is { } device)
        {
            _workspace.OpenDevice(device.Serial);
        }

        ResetHierarchy();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await _devices.RefreshAsync();
        Refresh();
    }

    private async void OnDumpClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial || _busy)
        {
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        ResetHierarchy();
        CommandText.Text = $"adb shell {DumpCommand}{Environment.NewLine}adb shell {ReadCommand}";

        try
        {
            var dump = await _toolbox.RunShellAsync(serial, DumpCommand, cancellationToken: token);
            if (!dump.Succeeded)
            {
                CommandText.Text = dump.CommandLine ?? $"adb shell {DumpCommand}";
                ShowResult(
                    false,
                    string.IsNullOrWhiteSpace(dump.Message)
                        ? "The UI hierarchy could not be dumped."
                        : dump.Message,
                    "Unlock the device, keep it awake, and make sure the current app is visible.");
                return;
            }

            var read = await _toolbox.RunShellAsync(serial, ReadCommand, cancellationToken: token);
            CommandText.Text = string.Join(
                Environment.NewLine,
                new[] { dump.CommandLine, read.CommandLine }.Where(line => !string.IsNullOrWhiteSpace(line)));
            if (!read.Succeeded)
            {
                ShowResult(
                    false,
                    string.IsNullOrWhiteSpace(read.Message)
                        ? "The hierarchy file could not be read."
                        : read.Message,
                    "The dump may have been removed by the system. Dump the hierarchy again.");
                return;
            }

            if (!LoadHierarchy(serial, read.Output))
            {
                return;
            }

            await CaptureScreenshotAsync(serial, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (ScreenshotImage.Source is null)
            {
                ScreenshotEmptyText.Text = "The screen capture was cancelled. Dump the hierarchy again to retry.";
                ScreenshotEmptyText.Visibility = Visibility.Visible;
            }

            ShowResult(
                false,
                "The hierarchy dump was cancelled.",
                "Dump the hierarchy again when the device is ready.");
        }
        catch (IOException exception)
        {
            ShowResult(
                false,
                $"The hierarchy dump failed: {exception.Message}",
                "Reconnect the device, unlock it, and dump the hierarchy again.");
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowResult(
                false,
                $"The hierarchy dump failed: {exception.Message}",
                "Reconnect the device, unlock it, and dump the hierarchy again.");
        }
        catch (InvalidOperationException exception)
        {
            ShowResult(
                false,
                $"The hierarchy dump failed: {exception.Message}",
                "Reconnect the device, unlock it, and dump the hierarchy again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool LoadHierarchy(string serial, string xml)
    {
        try
        {
            _hierarchy = UiHierarchy.Parse(xml);
        }
        catch (XmlException exception)
        {
            ShowResult(
                false,
                $"The hierarchy XML could not be parsed: {exception.Message}",
                "Some devices return a system window dump instead of your app. Bring the app to the foreground and try again.");
            return false;
        }
        catch (InvalidOperationException exception)
        {
            ShowResult(
                false,
                $"The hierarchy XML could not be parsed: {exception.Message}",
                "Bring the app under test to the foreground and dump the hierarchy again.");
            return false;
        }

        RawXmlBox.Text = _hierarchy.RawXml;
        ApplyFilter();
        ShowResult(
            true,
            $"Dumped {_hierarchy.Nodes.Count} nodes from {serial}.",
            null);
        return true;
    }

    private async Task CaptureScreenshotAsync(string serial, CancellationToken cancellationToken)
    {
        ScreenshotEmptyText.Text = "Capturing the screen...";
        ScreenshotEmptyText.Visibility = Visibility.Visible;
        try
        {
            var thumbnail = await _thumbnails.CaptureAsync(serial, cancellationToken);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(thumbnail.Png);
                await writer.StoreAsync();
                _ = writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            ScreenshotImage.Source = bitmap;
            ScreenshotEmptyText.Text = "Dump the hierarchy to capture the screen.";
            DrawOverlay();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ScreenshotEmptyText.Text = $"The screenshot could not be captured: {exception.Message}";
            ScreenshotEmptyText.Visibility = Visibility.Visible;
            ShowGuidance(
                "Keep the device unlocked and the app visible, then dump the hierarchy again to retry the screenshot.");
        }
    }

    private void OnScreenshotOpened(object sender, RoutedEventArgs e)
    {
        ScreenshotEmptyText.Visibility = Visibility.Collapsed;
        DrawOverlay();
    }

    private void OnScreenshotHostSizeChanged(object sender, SizeChangedEventArgs e) => DrawOverlay();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void OnDepthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_initializing || double.IsNaN(args.NewValue))
        {
            return;
        }

        var depth = InspectorSettings.ClampDepth((int)Math.Round(args.NewValue));
        if (depth == Settings.MaxDepth)
        {
            ApplyFilter();
            return;
        }

        try
        {
            await _configuration.SetAsync(SettingsPaths.InspectorMaxDepth, depth);
            ApplyFilter();
            ShowResult(true, $"The inspector shows nodes up to depth {depth}.", null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(
                false,
                $"The inspector depth could not be saved: {exception.Message}",
                "The previous depth is still in effect. Retry the change, or reset it from Settings.");
        }
    }

    private async void OnInvisibleToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        try
        {
            await _configuration.SetAsync(
                SettingsPaths.InspectorIncludeInvisible,
                IncludeInvisibleToggle.IsOn);
            ApplyFilter();
            ShowResult(
                true,
                IncludeInvisibleToggle.IsOn
                    ? "Invisible nodes are shown."
                    : "Invisible nodes are hidden.",
                null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(
                false,
                $"The invisible-node filter could not be saved: {exception.Message}",
                "The previous filter is still in effect. Retry the change, or reset it from Settings.");
        }
    }

    private void ApplyFilter()
    {
        if (_hierarchy is null)
        {
            _visibleNodes = [];
            NodeCountText.Text = "No hierarchy loaded.";
            NodeEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var inspector = Settings;
        var nodes = _hierarchy
            .Search(SearchBox.Text, inspector.IncludeInvisible, inspector.EffectiveMaxDepth);
        _visibleNodes = nodes;
        var rows = nodes.Select(node => new NodeRow(node)).ToArray();
        var previousNode = _selectedNode;
        NodeList.ItemsSource = rows;
        NodeList.SelectedItem = rows.FirstOrDefault(row =>
            previousNode is not null && ReferenceEquals(row.Node, previousNode));
        if (NodeList.SelectedItem is null)
        {
            _selectedNode = null;
            UpdateSelectedNode(null);
        }

        NodeEmptyText.Text = rows.Length == 0
            ? "No nodes match the filter."
            : "Dump the hierarchy to list the current screen's views.";
        NodeEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NodeCountText.Text = rows.Length == _hierarchy.Nodes.Count
            ? $"{rows.Length} nodes."
            : $"{rows.Length} of {_hierarchy.Nodes.Count} nodes shown.";
        DrawOverlay();
    }

    private void OnNodeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedNode = (NodeList.SelectedItem as NodeRow)?.Node;
        UpdateSelectedNode(_selectedNode);
    }

    private void UpdateSelectedNode(UiHierarchyNode? node)
    {
        var hasNode = node is not null;
        var hasBounds = node?.BoundsRect is { HasArea: true };
        TapButton.IsEnabled = hasBounds;
        CopySelectorButton.IsEnabled = hasNode;
        CopyCoordinatesButton.IsEnabled = hasBounds;
        NodeDetailsText.Text = node is null ? "Select a node to see its bounds, selector, and state." : Describe(node);
        DrawOverlay();
    }

    private void DrawOverlay()
    {
        OverlayCanvas.Children.Clear();
        if (GetImagePlacement() is not { Scale: > 0 } placement)
        {
            return;
        }

        var nodeBrush = new SolidColorBrush(Colors.DodgerBlue) { Opacity = 0.45 };
        var selectedBrush = new SolidColorBrush(Colors.DodgerBlue);
        if (Application.Current.Resources.TryGetValue(
                "AccentFillColorDefaultBrush",
                out var brush) &&
            brush is SolidColorBrush accent)
        {
            selectedBrush = new SolidColorBrush(accent.Color);
        }

        foreach (var node in _visibleNodes)
        {
            if (!ReferenceEquals(node, _selectedNode))
            {
                AddBoundsRectangle(node, placement, nodeBrush, 1, null);
            }
        }

        if (_selectedNode is { } selected)
        {
            AddBoundsRectangle(
                selected,
                placement,
                selectedBrush,
                2,
                new SolidColorBrush(selectedBrush.Color) { Opacity = 0.18 });
        }
    }

    private void AddBoundsRectangle(
        UiHierarchyNode node,
        (double X, double Y, double Scale) placement,
        Brush stroke,
        double strokeThickness,
        Brush? fill)
    {
        if (node.BoundsRect is not { HasArea: true } bounds)
        {
            return;
        }

        var rectangle = new Rectangle
        {
            Width = bounds.Width * placement.Scale,
            Height = bounds.Height * placement.Scale,
            StrokeThickness = strokeThickness,
            Stroke = stroke,
            Fill = fill,
        };
        Canvas.SetLeft(rectangle, placement.X + (bounds.Left * placement.Scale));
        Canvas.SetTop(rectangle, placement.Y + (bounds.Top * placement.Scale));
        OverlayCanvas.Children.Add(rectangle);
    }

    private (double X, double Y, double Scale)? GetImagePlacement()
    {
        if (ScreenshotImage.Source is not BitmapImage bitmap ||
            bitmap.PixelWidth <= 0 ||
            bitmap.PixelHeight <= 0)
        {
            return null;
        }

        var width = ScreenshotHost.ActualWidth;
        var height = ScreenshotHost.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var scale = Math.Min(width / bitmap.PixelWidth, height / bitmap.PixelHeight);
        return (
            (width - (bitmap.PixelWidth * scale)) / 2,
            (height - (bitmap.PixelHeight * scale)) / 2,
            scale);
    }

    private async void OnTapClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSerial is not { } serial ||
            _selectedNode?.BoundsRect is not { HasArea: true } bounds ||
            _busy)
        {
            return;
        }

        var command = string.Create(
            CultureInfo.InvariantCulture,
            $"input tap {bounds.CenterX} {bounds.CenterY}");
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        CommandText.Text = $"adb shell {command}";
        try
        {
            var result = await _toolbox.RunShellAsync(serial, command, cancellationToken: token);
            ShowResult(
                result.Succeeded,
                result.Succeeded
                    ? string.Create(
                        CultureInfo.InvariantCulture,
                        $"Tapped {bounds.CenterX},{bounds.CenterY} on {serial}.")
                    : string.IsNullOrWhiteSpace(result.Message)
                        ? "The tap failed."
                        : result.Message,
                result.Succeeded ? null : "Make sure the device is unlocked and the app is still visible.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ShowResult(false, "The tap was cancelled.", null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(
                false,
                $"The tap failed: {exception.Message}",
                "Make sure the device is still connected, unlocked, and showing the app, then dump the hierarchy again.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnCopySelectorClick(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is not { } node)
        {
            return;
        }

        CopyToClipboard(UiSelector.ForNode(node), "The selector was copied.");
    }

    private void OnCopyCoordinatesClick(object sender, RoutedEventArgs e)
    {
        if (_selectedNode?.BoundsRect is not { HasArea: true } bounds)
        {
            return;
        }

        CopyToClipboard(
            string.Create(CultureInfo.InvariantCulture, $"{bounds.CenterX},{bounds.CenterY}"),
            "The tap coordinates were copied.");
    }

    private void CopyToClipboard(string text, string message)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ShowResult(true, message, null);
    }

    private static string Describe(UiHierarchyNode node)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Selector: {UiSelector.ForNode(node)}");
        builder.AppendLine($"Class: {node.ClassName}");
        builder.AppendLine($"Package: {node.PackageName ?? "(none)"}");
        builder.AppendLine($"Resource id: {node.ResourceId ?? "(none)"}");
        builder.AppendLine($"Text: {node.Text ?? "(none)"}");
        builder.AppendLine($"Content description: {node.ContentDescription ?? "(none)"}");
        builder.AppendLine($"Bounds: {node.Bounds ?? "(none)"}");
        if (node.BoundsRect is { HasArea: true } bounds)
        {
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Tap point: {bounds.CenterX},{bounds.CenterY}"));
        }

        builder.AppendLine($"Depth: {node.Depth} · index: {node.Index}");
        builder.AppendLine(
            $"Flags: enabled={node.Enabled}, visible={node.VisibleToUser}, clickable={node.Clickable}, " +
            $"long-clickable={node.LongClickable}, focusable={node.Focusable}, focused={node.Focused}, " +
            $"scrollable={node.Scrollable}, checked={node.Checked}, selected={node.Selected}, " +
            $"password={node.Password}");
        return builder.ToString();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ResetHierarchy()
    {
        _hierarchy = null;
        _visibleNodes = [];
        _selectedNode = null;
        NodeList.ItemsSource = null;
        NodeDetailsText.Text = "Select a node to see its bounds, selector, and state.";
        NodeEmptyText.Text = "Dump the hierarchy to list the current screen's views.";
        NodeEmptyText.Visibility = Visibility.Visible;
        NodeCountText.Text = "No hierarchy loaded.";
        RawXmlBox.Text = string.Empty;
        CommandText.Text = string.Empty;
        ScreenshotImage.Source = null;
        OverlayCanvas.Children.Clear();
        ScreenshotEmptyText.Text = "Dump the hierarchy to capture the screen.";
        ScreenshotEmptyText.Visibility = Visibility.Visible;
        TapButton.IsEnabled = false;
        CopySelectorButton.IsEnabled = false;
        CopyCoordinatesButton.IsEnabled = false;
        ActionBar.IsOpen = false;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        DumpButton.IsEnabled = !busy;
        DeviceCombo.IsEnabled = !busy;
        DepthBox.IsEnabled = !busy;
        IncludeInvisibleToggle.IsEnabled = !busy;
    }

    private void ShowResult(bool succeeded, string message, string? guidance)
    {
        ActionBar.Severity = succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
        ShowGuidance(guidance);
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

    /// <summary>One hierarchy node shaped for the node list.</summary>
    public sealed class NodeRow
    {
        /// <summary>Creates a row.</summary>
        public NodeRow(UiHierarchyNode node)
        {
            Node = node ?? throw new ArgumentNullException(nameof(node));
        }

        /// <summary>Gets the source node.</summary>
        public UiHierarchyNode Node { get; }

        /// <summary>Gets the primary label.</summary>
        public string Display => Node.Display;

        /// <summary>Gets the secondary detail line.</summary>
        public string Detail => Node.Detail;

        /// <summary>Gets the indentation that mirrors the node depth.</summary>
        public Thickness Indent => new(Node.Depth * 14, 0, 0, 0);
    }
}

using System.Text;
using System.Xml;
using DroidDeck.App.Services;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Toolbox;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>
/// Dumps the current Android view hierarchy with uiautomator and exposes the
/// flattened tree, filters, bounds, and raw XML for inspection.
/// </summary>
public sealed partial class InspectorPage : Page, IRefreshablePage, IDisposable
{
    private const string DumpCommand = "uiautomator dump /sdcard/window_dump.xml";
    private const string ReadCommand = "cat /sdcard/window_dump.xml";

    private readonly IDeviceToolboxService _toolbox;
    private readonly IDeviceService _devices;
    private readonly SessionWorkspace _workspace;
    private readonly DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _cts;
    private UiHierarchy? _hierarchy;
    private bool _busy;
    private bool _disposed;

    /// <summary>Creates the inspector page.</summary>
    public InspectorPage(
        IDeviceToolboxService toolbox,
        IDeviceService devices,
        SessionWorkspace workspace)
    {
        _toolbox = toolbox ?? throw new ArgumentNullException(nameof(toolbox));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        InitializeComponent();
        _devices.DevicesChanged += OnDevicesChanged;
        Refresh();
    }

    /// <inheritdoc />
    public void Refresh() => RefreshDevices();

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

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e) => Dispatch(RefreshDevices);

    private void RefreshDevices()
    {
        var previous = SelectedSerial;
        var usable = _devices.Devices
            .Where(device => device.State == DeviceState.Device)
            .ToArray();
        DeviceCombo.ItemsSource = usable;
        DeviceCombo.SelectedItem = usable.FirstOrDefault(device =>
            string.Equals(device.Serial, previous, StringComparison.Ordinal))
            ?? usable.FirstOrDefault(device =>
                string.Equals(device.Serial, _workspace.DeviceSerial, StringComparison.Ordinal))
            ?? usable.FirstOrDefault();

        if (usable.Length == 0)
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

            LoadHierarchy(serial, read.Output);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ShowResult(false, "The hierarchy dump was cancelled.", null);
        }
        catch (IOException exception)
        {
            ShowResult(false, $"The hierarchy dump failed: {exception.Message}", null);
        }
        catch (UnauthorizedAccessException exception)
        {
            ShowResult(false, $"The hierarchy dump failed: {exception.Message}", null);
        }
        catch (InvalidOperationException exception)
        {
            ShowResult(false, $"The hierarchy dump failed: {exception.Message}", null);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoadHierarchy(string serial, string xml)
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
            return;
        }
        catch (InvalidOperationException exception)
        {
            ShowResult(false, $"The hierarchy XML could not be parsed: {exception.Message}", null);
            return;
        }

        RawXmlBox.Text = _hierarchy.RawXml;
        ApplyFilter();
        ShowResult(
            true,
            $"Dumped {_hierarchy.Nodes.Count} nodes from {serial}.",
            null);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_hierarchy is null)
        {
            NodeCountText.Text = "No hierarchy loaded.";
            NodeEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var rows = _hierarchy.Search(SearchBox.Text)
            .Select(node => new NodeRow(node))
            .ToArray();
        NodeList.ItemsSource = rows;
        NodeDetailsText.Text = "Select a node to see its bounds, resource id, and state.";
        NodeEmptyText.Text = rows.Length == 0
            ? "No nodes match the filter."
            : "Dump the hierarchy to list the current screen's views.";
        NodeEmptyText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NodeCountText.Text = rows.Length == _hierarchy.Nodes.Count
            ? $"{rows.Length} nodes."
            : $"{rows.Length} of {_hierarchy.Nodes.Count} nodes shown.";
    }

    private void OnNodeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NodeList.SelectedItem is NodeRow row)
        {
            NodeDetailsText.Text = Describe(row.Node);
        }
    }

    private static string Describe(UiHierarchyNode node)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Class: {node.ClassName}");
        builder.AppendLine($"Package: {node.PackageName ?? "(none)"}");
        builder.AppendLine($"Resource id: {node.ResourceId ?? "(none)"}");
        builder.AppendLine($"Text: {node.Text ?? "(none)"}");
        builder.AppendLine($"Content description: {node.ContentDescription ?? "(none)"}");
        builder.AppendLine($"Bounds: {node.Bounds ?? "(none)"}");
        builder.AppendLine($"Depth: {node.Depth} · index: {node.Index}");
        builder.AppendLine(
            $"Flags: enabled={node.Enabled}, clickable={node.Clickable}, focusable={node.Focusable}, " +
            $"scrollable={node.Scrollable}, checked={node.Checked}, selected={node.Selected}, " +
            $"password={node.Password}");
        return builder.ToString();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ResetHierarchy()
    {
        _hierarchy = null;
        NodeList.ItemsSource = null;
        NodeDetailsText.Text = "Select a node to see its bounds, resource id, and state.";
        NodeEmptyText.Text = "Dump the hierarchy to list the current screen's views.";
        NodeEmptyText.Visibility = Visibility.Visible;
        NodeCountText.Text = "No hierarchy loaded.";
        RawXmlBox.Text = string.Empty;
        CommandText.Text = string.Empty;
        ActionBar.IsOpen = false;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;
        DumpButton.IsEnabled = !busy;
        DeviceCombo.IsEnabled = !busy;
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

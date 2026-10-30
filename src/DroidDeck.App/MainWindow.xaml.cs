using System.Runtime.InteropServices;
using DroidDeck.App.Commands;
using DroidDeck.App.Controls;
using DroidDeck.App.Services;
using DroidDeck.App.Views;
using DroidDeck.Core;
using DroidDeck.Core.Commands;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Diagnostics;
using DroidDeck.Core.Features;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Notifications;
using DroidDeck.Core.Resources;
using DroidDeck.Core.Runs;
using DroidDeck.Core.Screen;
using DroidDeck.Core.Themes;
using DroidDeck.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace DroidDeck.App;

/// <summary>
/// The single main window. Hosts navigation, the status bar, and global
/// diagnostics actions.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> PageTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = typeof(HomePage),
            ["onboarding"] = typeof(OnboardingPage),
            ["devices"] = typeof(DevicesPage),
            ["devices/tools"] = typeof(DeviceToolsPage),
            ["devices/mirror"] = typeof(MirrorPage),
            ["devices/emulators"] = typeof(EmulatorsPage),
            ["devices/wireless"] = typeof(WirelessPage),
            ["multi-device"] = typeof(MultiDevicePage),
            ["devices/performance"] = typeof(PerformancePage),
            ["devices/inspector"] = typeof(InspectorPage),
            ["projects"] = typeof(ProjectsPage),
            ["tests"] = typeof(TestsPage),
            ["session"] = typeof(SessionPage),
            ["history"] = typeof(HistoryPage),
            ["activity"] = typeof(ActivityPage),
            ["extensions"] = typeof(ExtensionsPage),
            ["catalog"] = typeof(CatalogPage),
            ["settings"] = typeof(SettingsPage),
            ["diagnostics"] = typeof(DiagnosticsPage),
        };

    private static readonly string[] MenuCategoryOrder =
        ["App", "Navigate", "Device", "Run", "Screen", "Plugins", "Built-in"];

    // Maps navigation tags to the FeatureCatalog identifiers hidden in
    // Customize mode. Tags without an entry are always visible.
    private static readonly Dictionary<string, string> NavigationFeatureIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = "nav.home",
            ["devices"] = "nav.devices",
            ["projects"] = "nav.projects",
            ["session"] = "nav.sessions",
            ["devices/performance"] = "nav.performance",
            ["devices/inspector"] = "nav.inspector",
            ["devices/mirror"] = "nav.mirror",
            ["history"] = "nav.history",
            ["settings"] = "nav.settings",
        };

    private readonly IServiceProvider _services;
    private readonly IDeviceService _devices;
    private readonly IDiagnosticsService _diagnostics;
    private readonly IConfigurationService _configuration;
    private readonly ApkInstallService _apkInstalls;
    private readonly PluginHost _pluginHost;
    private readonly PluginUiFaultReporter _faultReporter;
    private readonly SessionWorkspace _workspace;
    private readonly INotificationService _notifications;
    private readonly IResourceGuard _resourceGuard;
    private readonly ICommandRegistry _commands;
    private readonly KeybindingMap _keybindings;
    private readonly ShellCommandCatalog _commandCatalog;
    private readonly AppThemeService _themeService;
    private readonly ThemeManager _themeManager;
    private readonly FeatureVisibilityService _features;
    private readonly ChordRouter _chords = new();
    private readonly ILogger<MainWindow> _logger;
    private readonly Dictionary<string, Page> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NavigationPageEntry> _pluginPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NavigationViewItem> _pluginNavItems = [];
    private readonly List<KeyboardAccelerator> _accelerators = [];
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _messageTimer;
    private CommandPaletteOverlay? _palette;
    private bool _suppressSelectionChanged;
    private bool _placementRestored;
    private bool _allowClose;
    private bool _apkInstallBusy;
    private string? _notificationSessionId;

    /// <summary>Creates the main window.</summary>
    public MainWindow(
        IServiceProvider services,
        IDeviceService devices,
        IDiagnosticsService diagnostics,
        IConfigurationService configuration,
        ApkInstallService apkInstalls,
        PluginHost pluginHost,
        PluginUiFaultReporter faultReporter,
        SessionWorkspace workspace,
        INotificationService notifications,
        IResourceGuard resourceGuard,
        ICommandRegistry commands,
        KeybindingMap keybindings,
        AppThemeService themeService,
        ThemeManager themeManager,
        FeatureVisibilityService features,
        ILogger<MainWindow> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _apkInstalls = apkInstalls ?? throw new ArgumentNullException(nameof(apkInstalls));
        _pluginHost = pluginHost ?? throw new ArgumentNullException(nameof(pluginHost));
        _faultReporter = faultReporter ?? throw new ArgumentNullException(nameof(faultReporter));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _resourceGuard = resourceGuard ?? throw new ArgumentNullException(nameof(resourceGuard));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _keybindings = keybindings ?? throw new ArgumentNullException(nameof(keybindings));
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));
        _themeManager = themeManager ?? throw new ArgumentNullException(nameof(themeManager));
        _features = features ?? throw new ArgumentNullException(nameof(features));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeComponent();
        Title = "DroidDeck";
        AppWindow.Title = "DroidDeck";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        TryEnableMica();

        DiagnosticsIdText.Text = $"Diagnostics {_diagnostics.DiagnosticsId}";
        _devices.DevicesChanged += OnDevicesChanged;
        _notifications.NotificationRaised += OnNotificationRaised;
        _resourceGuard.WarningRaised += OnResourceWarningRaised;
        _configuration.Changed += OnConfigurationChanged;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        // Keybindings load synchronously because the accelerators below need them
        // before the shell becomes interactive; the file is a few kilobytes at most.
        _keybindings.EnsureLoaded();
        _commandCatalog = new ShellCommandCatalog(_commands, _pluginHost, this, _logger);
        _commandCatalog.RegisterShellCommands();
        RebuildMenu();
        RegisterAccelerators();
        _pluginHost.ResultsChanged += OnPluginResultsChanged;
        RebuildPluginNavItems();
        _commands.Changed += OnCommandSetChanged;
        _keybindings.Changed += OnCommandSetChanged;
        _themeManager.Changed += OnThemeManagerChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();
        _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            StatusMessageText.Text = string.Empty;
        };

        ApplyShellTheme();
        UpdateFeatureVisibility();
        RestorePlacement();
        UpdateStatus();
        Navigate(_configuration.Current.Settings.Onboarding.Completed ? "home" : "onboarding");
    }

    /// <summary>Navigates to a page by key. Unknown keys show a safe fallback.</summary>
    public void Navigate(string key)
    {
        if (_pluginPages.TryGetValue(key, out var pluginEntry))
        {
            if (!_pages.TryGetValue(key, out var pluginPage))
            {
                pluginPage = new PluginPageHost(pluginEntry, _pluginHost, _faultReporter, _logger);
                _pages[key] = pluginPage;
            }

            PageHost.Content = pluginPage;
            SelectNavigationItem(key);
            return;
        }

        if (!PageTypes.TryGetValue(key, out var pageType))
        {
            var fallback = new FallbackPage();
            fallback.SetRequestedPage(key);
            PageHost.Content = fallback;
            _logger.LogWarning("Unknown navigation target {Page}", key);
            return;
        }

        if (!_pages.TryGetValue(key, out var page))
        {
            page = (Page)_services.GetRequiredService(pageType);
            _pages[key] = page;
        }

        PageHost.Content = page;
        SelectNavigationItem(key);
        if (page is IRefreshablePage refreshable)
        {
            refreshable.Refresh();
        }

        UpdateStatus();
    }

    /// <summary>Allows the window to close after application services are disposed.</summary>
    public void AllowClose() => _allowClose = true;

    /// <summary>Gets whether a page key is registered for navigation.</summary>
    internal static bool IsKnownPageKey(string key) => PageTypes.ContainsKey(key);

    /// <summary>Gets the cached page for a key, when it has been created.</summary>
    internal Page? TryGetPage(string key) => _pages.TryGetValue(key, out var page) ? page : null;

    /// <summary>Collapses or expands the navigation pane.</summary>
    public void ToggleNavigationPane() => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    /// <summary>Shows the command palette overlay, creating it on first use.</summary>
    public void OpenCommandPalette()
    {
        if (_palette is null)
        {
            _palette = new CommandPaletteOverlay(_commands, _keybindings);
            _palette.Opened += (_, _) => SetAcceleratorsEnabled(false);
            _palette.Closed += (_, _) => SetAcceleratorsEnabled(true);
            _palette.ExecuteRequested += OnPaletteExecuteRequested;
            Grid.SetRowSpan(_palette, 4);
            RootGrid.Children.Add(_palette);
        }

        _palette.Open();
    }

    /// <summary>Runs the APK picker and install flow; used by the command palette.</summary>
    public Task InstallApkAsync() => PickAndInstallApkAsync();

    private async void OnInstallApkClick(object sender, RoutedEventArgs e) => await PickAndInstallApkAsync();

    private async Task PickAndInstallApkAsync()
    {
        if (_apkInstallBusy)
        {
            ShowStatusMessage("An APK install is already in progress.");
            return;
        }

        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".apk");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0)
            {
                await ShowApkInstallAsync(files.Select(file => file.Path).ToArray());
            }
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The APK picker could not be opened.");
            ShowStatusMessage("The APK picker could not be opened.");
        }
    }

    private void OnPaletteExecuteRequested(object? sender, CommandDefinition command)
    {
        _palette?.Close();
        _ = ExecuteCommandAsync(command);
    }

    private async Task ExecuteCommandAsync(CommandDefinition command)
    {
        if (!command.IsEnabled)
        {
            ShowStatusMessage($"'{command.Title}' is not available right now.");
            return;
        }

        try
        {
            await command.Execute(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal outcome for cancellable commands.
        }
        catch (Exception exception)
        {
            // Commands may come from plugins, so any failure is contained here.
            _logger.LogWarning(exception, "The command {CommandId} failed.", command.Id);
            ShowStatusMessage($"'{command.Title}' failed: {exception.Message}");
        }
    }

    private void OnCommandSetChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RebuildCommandSurfaces);

    private void RebuildCommandSurfaces()
    {
        RebuildMenu();
        RegisterAccelerators();
    }

    private void RebuildMenu()
    {
        ShellMenuBar.Items.Clear();
        var groups = _commands.Commands
            .GroupBy(command => command.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => MenuCategoryRank(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var menu = new MenuBarItem { Title = group.Key };
            AutomationProperties.SetAutomationId(menu, $"Menu_{group.Key}");
            foreach (var command in group.OrderBy(
                command => command.Title, StringComparer.OrdinalIgnoreCase))
            {
                var item = new MenuFlyoutItem
                {
                    Text = command.Title,
                    IsEnabled = command.IsEnabled,
                };
                AutomationProperties.SetAutomationId(item, $"MenuCommand_{command.Id}");
                var shortcut = _keybindings.GetEffectiveChord(command);
                if (shortcut is not null)
                {
                    item.KeyboardAcceleratorTextOverride = shortcut.ToString();
                }

                item.Click += (_, _) => _ = ExecuteCommandAsync(command);
                menu.Items.Add(item);
            }

            ShellMenuBar.Items.Add(menu);
        }
    }

    private static int MenuCategoryRank(string category)
    {
        var index = Array.FindIndex(
            MenuCategoryOrder,
            candidate => string.Equals(candidate, category, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? MenuCategoryOrder.Length : index;
    }

    private void RegisterAccelerators()
    {
        foreach (var accelerator in _accelerators)
        {
            RootGrid.KeyboardAccelerators.Remove(accelerator);
        }

        _accelerators.Clear();
        var chords = new List<(CommandDefinition Command, KeyChord Chord)>();
        foreach (var command in _commands.Commands)
        {
            var chord = _keybindings.GetEffectiveChord(command);
            if (chord is null)
            {
                continue;
            }

            if (chord.IsSingleStep)
            {
                var gesture = chord.Steps[0];
                if (!ShortcutKeys.TryGetVirtualKey(gesture.Key, out var key))
                {
                    continue;
                }

                var accelerator = new KeyboardAccelerator
                {
                    Modifiers = ToVirtualKeyModifiers(gesture.Modifiers),
                    Key = key,
                };
                accelerator.Invoked += (sender, args) =>
                {
                    args.Handled = true;
                    _ = ExecuteCommandAsync(command);
                };
                RootGrid.KeyboardAccelerators.Add(accelerator);
                _accelerators.Add(accelerator);
            }
            else
            {
                chords.Add((command, chord));
            }
        }

        _chords.Rebuild(chords);
        SetAcceleratorsEnabled(_palette is null || !_palette.IsOpen);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_palette is { IsOpen: true })
        {
            return;
        }

        // Chord steps must never swallow ordinary typing in text fields; a
        // chord like G A only fires when no text input has focus.
        if (FocusManager.GetFocusedElement() is TextBox or AutoSuggestBox or NumberBox)
        {
            _chords.Reset();
            return;
        }

        if (ShortcutKeys.IsModifier(e.Key))
        {
            return;
        }

        if (!ShortcutKeys.TryGetToken(e.Key, out var token))
        {
            _chords.Reset();
            return;
        }

        var command = _chords.OfferKey(new KeyGesture(ShortcutKeys.CurrentModifiers(), token));
        if (command is not null)
        {
            e.Handled = true;
            _ = ExecuteCommandAsync(command);
        }
    }

    private void SetAcceleratorsEnabled(bool enabled)
    {
        foreach (var accelerator in _accelerators)
        {
            accelerator.IsEnabled = enabled;
        }
    }

    private static VirtualKeyModifiers ToVirtualKeyModifiers(KeyModifiers modifiers)
    {
        VirtualKeyModifiers result = VirtualKeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            result |= VirtualKeyModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result |= VirtualKeyModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            result |= VirtualKeyModifiers.Menu;
        }

        if (modifiers.HasFlag(KeyModifiers.Windows))
        {
            result |= VirtualKeyModifiers.Windows;
        }

        return result;
    }

    /// <summary>Shows the window and brings it to the foreground.</summary>
    public void BringToFront()
    {
        Activate();
        if (AppWindow.Presenter is OverlappedPresenter presenter &&
            presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        var handle = WindowNative.GetWindowHandle(this);
        if (handle != IntPtr.Zero)
        {
            SetForegroundWindow(handle);
        }
    }

    /// <summary>Applies the requested base theme to the shell.</summary>
    public void ApplyTheme(AppTheme theme)
    {
        _themeService.Update(theme);
        ApplyShellTheme();
    }

    /// <summary>
    /// Resolves the effective theme from the theme manager and applies its base
    /// palette, accent, and font to the shell without a restart.
    /// </summary>
    public void ApplyShellTheme()
    {
        var preference = _configuration.Current.Settings.Appearance.Theme;
        var systemBase = _themeService.IsHighContrast
            ? ThemeBase.HighContrast
            : Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ThemeBase.Dark
                : ThemeBase.Light;
        var theme = _themeManager.GetCurrent(systemBase);
        RootGrid.RequestedTheme = preference switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => theme.Base switch
            {
                ThemeBase.Dark => ElementTheme.Dark,
                ThemeBase.Light => ElementTheme.Light,
                _ => ElementTheme.Default,
            },
        };
        ApplyThemeTokens(theme);
    }

    private static void ApplyThemeTokens(ThemeDefinition theme)
    {
        var resources = Application.Current.Resources;
        if (ThemeContrast.TryParseColor(theme.Colors.Accent, out var red, out var green, out var blue))
        {
            var accent = Windows.UI.Color.FromArgb(255, red, green, blue);
            resources["SystemAccentColor"] = accent;
            resources["SystemAccentColorSecondary"] = accent;
            resources["SystemAccentColorTertiary"] = accent;
        }

        // Controls pick their default font from this themed resource, so
        // overriding it at application scope changes the shell font everywhere.
        resources["ContentControlThemeFontFamily"] = new FontFamily(
            string.IsNullOrWhiteSpace(theme.FontFamily) ? "Segoe UI Variable" : theme.FontFamily);
    }

    private void OnThemeManagerChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(ApplyShellTheme);

    /// <summary>Collapses navigation entries whose feature is hidden in Customize mode.</summary>
    public void UpdateFeatureVisibility()
    {
        NavigationViewItem? hiddenSelection = null;
        NavigationViewItem? firstVisible = null;
        foreach (var item in EnumerateNavigationItems())
        {
            var isHidden = item.Tag is string key &&
                NavigationFeatureIds.TryGetValue(key, out var featureId) &&
                !_features.IsVisible(featureId);
            item.Visibility = isHidden ? Visibility.Collapsed : Visibility.Visible;
            if (isHidden && ReferenceEquals(Navigation.SelectedItem, item))
            {
                hiddenSelection = item;
            }

            firstVisible ??= isHidden ? null : item;
        }

        if (hiddenSelection is not null)
        {
            if (firstVisible?.Tag is string key && PageTypes.ContainsKey(key))
            {
                Navigate(key);
            }
            else
            {
                Navigation.SelectedItem = null;
                PageHost.Content = null;
            }
        }
    }

    /// <summary>Computes the desktop rectangle of an element in effective pixels (DIPs).</summary>
    internal bool TryGetScreenBounds(FrameworkElement element, out ScreenBounds bounds)
    {
        bounds = new ScreenBounds(0, 0, 0, 0);
        if (element.XamlRoot is not { } root || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        // scrcpy and WinUI both express window geometry in effective pixels. The
        // AppWindow position is physical, so convert it before adding the XAML offset.
        var point = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var scale = root.RasterizationScale > 0 ? root.RasterizationScale : 1;
        var position = AppWindow.Position;
        bounds = new ScreenBounds(
            (int)Math.Round((position.X / scale) + point.X),
            (int)Math.Round((position.Y / scale) + point.Y),
            Math.Max(1, (int)Math.Round(element.ActualWidth)),
            Math.Max(1, (int)Math.Round(element.ActualHeight)));
        return bounds.IsValid;
    }

    /// <summary>Copies a redacted diagnostics report to the clipboard.</summary>
    public void CopyDiagnosticsReport()
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(ShellDiagnostics.BuildReport(_diagnostics, _pluginHost));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            ShowStatusMessage($"Diagnostics copied ({_diagnostics.DiagnosticsId}).");
        }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "The diagnostics report could not be copied.");
            ShowStatusMessage("Diagnostics could not be copied. Open Diagnostics to save a report.");
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        if (args.SelectedItemContainer?.Tag is string key)
        {
            Navigate(key);
        }
    }

    /// <summary>Adds one navigation entry per registered plugin page, after the built-ins.</summary>
    private void RebuildPluginNavItems()
    {
        foreach (var item in _pluginNavItems)
        {
            Navigation.MenuItems.Remove(item);
        }

        _pluginNavItems.Clear();
        _pluginPages.Clear();
        foreach (var entry in _pluginHost.Extensions.NavigationPages)
        {
            var key = $"plugin/{entry.Page.Id}";
            _pluginPages[key] = entry;
            var item = new NavigationViewItem
            {
                Content = entry.Page.Title,
                Tag = key,
                Icon = CreatePluginPageIcon(entry.Page.Icon),
            };
            AutomationProperties.SetAutomationId(item, $"NavPlugin_{entry.Page.Id}");
            _pluginNavItems.Add(item);
            Navigation.MenuItems.Add(item);
        }
    }

    private static IconElement CreatePluginPageIcon(string glyph) =>
        !string.IsNullOrWhiteSpace(glyph) && glyph.Length <= 4
            ? new FontIcon { Glyph = glyph }
            : new SymbolIcon(Symbol.Document);

    private void OnPluginResultsChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RebuildPluginNavItems);

    private bool SelectNavigationItem(string key)
    {
        foreach (var item in EnumerateNavigationItems())
        {
            if (item.Tag is string tag && string.Equals(tag, key, StringComparison.OrdinalIgnoreCase))
            {
                _suppressSelectionChanged = true;
                try
                {
                    Navigation.SelectedItem = item;
                }
                finally
                {
                    _suppressSelectionChanged = false;
                }

                return true;
            }
        }

        return false;
    }

    private IEnumerable<NavigationViewItem> EnumerateNavigationItems()
    {
        foreach (var item in Navigation.MenuItems)
        {
            if (item is NavigationViewItem navigationItem)
            {
                yield return navigationItem;
            }
        }

        foreach (var item in Navigation.FooterMenuItems)
        {
            if (item is NavigationViewItem navigationItem)
            {
                yield return navigationItem;
            }
        }
    }

    private void OnDevicesChanged(object? sender, DeviceListChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(UpdateStatus);
    }

    private void OnConfigurationChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.Path.StartsWith("features.hidden", StringComparison.OrdinalIgnoreCase))
        {
            DispatcherQueue.TryEnqueue(UpdateFeatureVisibility);
        }
    }

    private void UpdateStatus()
    {
        var devices = _devices.Devices;
        var count = devices.Count;
        AdbStatusText.Text = _devices.IsAdbConnected
            ? "ADB connected"
            : count > 0
                ? "ADB via CLI fallback"
                : "ADB disconnected";
        DeviceCountText.Text = count == 1 ? "1 device" : $"{count} devices";
        FallbackStatusText.Text = _devices.IsUsingCliFallback ? "CLI fallback active" : string.Empty;
        TitleStatusText.Text = count == 0
            ? "No devices"
            : count == 1
                ? "1 device connected"
                : $"{count} devices connected";
        ToolTipService.SetToolTip(
            AdbStatusText,
            string.IsNullOrWhiteSpace(_devices.LastError)
                ? $"Diagnostics ID {_diagnostics.DiagnosticsId}"
                : $"{_devices.LastError}\nDiagnostics ID {_diagnostics.DiagnosticsId}");
        FinishSetupButton.Visibility = _configuration.Current.Settings.Onboarding.Completed
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnFinishSetupClick(object sender, RoutedEventArgs e) => Navigate("onboarding");

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = hasFiles ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (!hasFiles)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        e.DragUIOverride.Caption = "Install APK";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnRootDragLeave(object sender, DragEventArgs e) =>
        DropOverlay.Visibility = Visibility.Collapsed;

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        catch (Exception exception) when (
            exception is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Dropped files could not be read.");
            ShowStatusMessage("The dropped files could not be read.");
            return;
        }

        IReadOnlyList<string> paths;
        try
        {
            paths = await CollectApkPathsAsync(items);
        }
        catch (Exception exception) when (
            exception is COMException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(exception, "The dropped folder could not be read.");
            ShowStatusMessage("The dropped folder could not be read.");
            return;
        }

        await ShowApkInstallAsync(paths);
    }

    private static async Task<IReadOnlyList<string>> CollectApkPathsAsync(
        IReadOnlyList<IStorageItem> items)
    {
        var paths = new List<string>();
        foreach (var item in items)
        {
            switch (item)
            {
                case StorageFile file:
                    paths.Add(file.Path);
                    break;
                case StorageFolder folder:
                    var apks = (await folder.GetFilesAsync())
                        .Where(file => file.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        .Select(file => file.Path)
                        .ToArray();
                    if (apks.Length == 0)
                    {
                        paths.Add(folder.Path);
                    }
                    else
                    {
                        paths.AddRange(apks);
                    }

                    break;
            }
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(200)
            .ToArray();
    }

    private async Task ShowApkInstallAsync(IReadOnlyList<string> apkPaths)
    {
        if (_apkInstallBusy)
        {
            ShowStatusMessage("An APK install is already in progress.");
            return;
        }

        _apkInstallBusy = true;
        InstallApkButton.IsEnabled = false;
        try
        {
            ShowStatusMessage("Inspecting APK...");
            ApkInstallInspection inspection;
            try
            {
                inspection = await _apkInstalls.InspectAsync(apkPaths);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException or UnauthorizedAccessException or AggregateException)
            {
                _logger.LogWarning(exception, "APK inspection failed.");
                await ShowMessageAsync(
                    "APK inspection failed",
                    "DroidDeck could not inspect the dropped files. Check the toolchain diagnostics and try again.");
                return;
            }

            try
            {
                await _devices.RefreshAsync();
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Devices could not be refreshed before the APK install.");
            }

            var dialog = new ApkInstallDialog(inspection, _devices.Devices);
            var choice = await ShowDialogAsync(dialog);
            if (choice == ContentDialogResult.None || dialog.SelectedPlan is not { } plan)
            {
                ShowStatusMessage("APK install canceled.");
                return;
            }

            var selectedDevices = dialog.SelectedDevices;
            ShowStatusMessage(
                $"Installing {plan.PackageName} on {selectedDevices.Count} device(s)...");
            IReadOnlyList<ApkInstallResult> results;
            try
            {
                results = await _apkInstalls.InstallAsync(plan, selectedDevices, dialog.Launch);
            }
            catch (Exception exception) when (
                exception is IOException or InvalidOperationException or UnauthorizedAccessException or AggregateException)
            {
                _logger.LogWarning(exception, "The APK install could not run.");
                await ShowMessageAsync(
                    "APK install failed",
                    "DroidDeck could not start the install. Check the device connection and try again.");
                return;
            }

            var summary = ApkInstallSummary.From(results);
            ShowStatusMessage(
                $"{plan.PackageName}: {summary.Succeeded} installed, {summary.Failed} failed, " +
                $"{summary.Skipped} skipped.");
            await ShowDialogAsync(new ApkInstallResultDialog(plan, results));
        }
        finally
        {
            _apkInstallBusy = false;
            InstallApkButton.IsEnabled = true;
        }
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot = RootGrid.XamlRoot;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogWarning(exception, "A dialog could not be opened because another dialog is active.");
            ShowStatusMessage("Close the open dialog and try again.");
            return ContentDialogResult.None;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        await ShowDialogAsync(dialog);
    }

    private void ShowStatusMessage(string message)
    {
        StatusMessageText.Text = message;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    private void OnCopyDiagnosticsClick(object sender, RoutedEventArgs e) => CopyDiagnosticsReport();

    private void OnNotificationRaised(object? sender, AppNotification notification)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _notificationSessionId = notification.SessionId;
            NotificationBar.Severity = notification.Event switch
            {
                NotificationEvent.RunSucceeded => InfoBarSeverity.Success,
                NotificationEvent.Crash or NotificationEvent.InstallFailed or NotificationEvent.RunFailed =>
                    InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational,
            };
            NotificationBar.Title = notification.Title;
            NotificationBar.Message = notification.Message;
            NotificationActionButton.Visibility = string.IsNullOrWhiteSpace(notification.SessionId)
                ? Visibility.Collapsed
                : Visibility.Visible;
            NotificationBar.IsOpen = true;
        });
    }

    private void OnResourceWarningRaised(object? sender, ResourceWarningEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _notificationSessionId = null;
            NotificationBar.Severity = InfoBarSeverity.Warning;
            NotificationBar.Title = $"Resource warning: {e.Kind}";
            NotificationBar.Message = e.Message;
            NotificationActionButton.Visibility = Visibility.Collapsed;
            NotificationBar.IsOpen = true;
        });
    }

    private void OnNotificationActionClick(object sender, RoutedEventArgs e)
    {
        NotificationBar.IsOpen = false;
        if (string.IsNullOrWhiteSpace(_notificationSessionId))
        {
            return;
        }

        _workspace.OpenSession(_notificationSessionId);
        Navigate("session");
    }

    private void TryEnableMica()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch (COMException exception)
        {
            _logger.LogInformation(exception, "Mica is not available; using the default backdrop.");
        }
    }

    private void RestorePlacement()
    {
        if (_placementRestored)
        {
            return;
        }

        _placementRestored = true;
        var placement = _configuration.Current.Settings.Window;
        var width = Math.Max(640, (int)Math.Round(placement.Width));
        var height = Math.Max(480, (int)Math.Round(placement.Height));
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            width = Math.Min(width, work.Width);
            height = Math.Min(height, work.Height);
            var x = placement.X is { } savedX ? (int)Math.Round(savedX) : work.X + (work.Width - width) / 2;
            var y = placement.Y is { } savedY ? (int)Math.Round(savedY) : work.Y + (work.Height - height) / 2;
            if (x < work.X || y < work.Y || x + width > work.X + work.Width || y + height > work.Y + work.Height)
            {
                x = work.X + Math.Max(0, (work.Width - width) / 2);
                y = work.Y + Math.Max(0, (work.Height - height) / 2);
            }

            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            if (placement.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        catch (COMException exception)
        {
            _logger.LogInformation(exception, "The saved window placement could not be restored.");
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        _ = App.Instance.RequestShutdownAsync(this);
    }

    /// <summary>Persists the current placement. Must run before services are disposed.</summary>
    internal async Task SavePlacementAsync()
    {
        try
        {
            var position = AppWindow.Position;
            var size = AppWindow.Size;
            var isMaximized = AppWindow.Presenter is OverlappedPresenter presenter &&
                              presenter.State == OverlappedPresenterState.Maximized;
            var current = _configuration.Current.Settings.Window;
            if (!current.IsMaximized || !isMaximized)
            {
                await _configuration.SetAsync(SettingsPaths.WindowWidth, size.Width).ConfigureAwait(false);
                await _configuration.SetAsync(SettingsPaths.WindowHeight, size.Height).ConfigureAwait(false);
                await _configuration.SetAsync(SettingsPaths.WindowX, position.X).ConfigureAwait(false);
                await _configuration.SetAsync(SettingsPaths.WindowY, position.Y).ConfigureAwait(false);
            }

            await _configuration.SetAsync(SettingsPaths.WindowIsMaximized, isMaximized).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or AggregateException or
            COMException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Window placement could not be saved.");
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _statusTimer.Stop();
        _messageTimer.Stop();
        _pluginHost.ResultsChanged -= OnPluginResultsChanged;
        _devices.DevicesChanged -= OnDevicesChanged;
        _notifications.NotificationRaised -= OnNotificationRaised;
        _resourceGuard.WarningRaised -= OnResourceWarningRaised;
        _commands.Changed -= OnCommandSetChanged;
        _keybindings.Changed -= OnCommandSetChanged;
        _themeManager.Changed -= OnThemeManagerChanged;
        _configuration.Changed -= OnConfigurationChanged;
        _commandCatalog.Detach();
        AppWindow.Closing -= OnAppWindowClosing;
        foreach (var page in _pages.Values.OfType<IDisposable>())
        {
            page.Dispose();
        }

        _pages.Clear();
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Main window closed for session {SessionId}.", _diagnostics.SessionId);
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
#pragma warning restore SYSLIB1054
}

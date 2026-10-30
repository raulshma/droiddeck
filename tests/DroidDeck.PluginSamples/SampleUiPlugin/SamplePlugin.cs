using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.UI.Kit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SampleUiPlugin;

/// <summary>
/// A sample plugin that contributes every UI surface the host manages: a
/// navigation page, a project tab, a home widget, a live session panel, a
/// settings section, a status bar item with a click action, a toolbar action,
/// a command, and a host-shown dialog.
/// </summary>
public sealed class SampleUiPlugin : IPlugin
{
    /// <summary>The dialog the sample's status bar item, toolbar action, and command show.</summary>
    public const string AboutDialogId = "com.example.sample.ui.dialog";

    private IPluginDialogService? _dialogs;
    private string _lastDialogChoice = "none";

    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _dialogs = context.Dialogs;
        context.Extensions.AddNavigationPage(new SampleMetricsPage());
        context.Extensions.AddProjectTab(new SampleInsightsTab());
        context.Extensions.AddHomeWidget(new SampleBuildHealthWidget());
        context.Extensions.AddSessionPanel(new SampleSessionPanel());
        context.Extensions.AddSettingsSection(new SampleSettingsSection());
        context.Extensions.AddStatusBarItem(new SampleStatusBarItem(this));
        context.Extensions.AddToolbarAction(new SampleDialogToolbarAction(this));
        context.Extensions.AddCommand(new SampleDialogCommand(this));
        context.Extensions.AddDialog(new SampleAboutDialog());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Gets how the last dialog was closed, for the status bar item.</summary>
    public string LastDialogChoice => _lastDialogChoice;

    /// <summary>Shows the sample dialog and remembers the choice.</summary>
    public async Task ShowAboutDialogAsync(CancellationToken cancellationToken)
    {
        if (_dialogs is not { } dialogs)
        {
            return;
        }

        var result = await dialogs.ShowAsync(AboutDialogId, cancellationToken);
        _lastDialogChoice = result.Shown ? result.ButtonId ?? "dismissed" : "unavailable";
    }
}

/// <summary>A navigation page showing build trend and matrix data from the kit.</summary>
public sealed class SampleMetricsPage : INavigationPage
{
    /// <summary>The stable page identifier.</summary>
    public const string PageId = "com.example.sample.ui.page";

    /// <inheritdoc />
    public string Id => PageId;

    /// <inheritdoc />
    public string Title => "Sample metrics";

    /// <inheritdoc />
    public string Icon => "\uE9D9";

    /// <inheritdoc />
    public Task<object> CreateViewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var info = new PluginInfoBar();
        info.Show("All values come from the sample plugin.", InfoBarSeverity.Informational, "Sample data");

        var chart = new PluginMetricChart();
        chart.Plot([3, 5, 4, 8, 7, 10, 9], "CPU %");

        var grid = new PluginDataGrid();
        grid.Bind(
            ["Build", "Status", "Duration"],
            [
                ["debug", "ok", "12s"],
                ["release", "ok", "41s"],
                ["tests", "flaky", "1m 08s"],
            ]);

        var layout = new PluginPageLayout("Sample metrics", "Live view of the sample plugin's build data.")
        {
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    info,
                    new PluginCard("Build trend") { Content = chart },
                    new PluginCard("Build matrix") { Content = grid },
                },
            },
        };
        return Task.FromResult<object>(layout);
    }
}

/// <summary>A project tab that summarizes the sample plugin's module data.</summary>
public sealed class SampleInsightsTab : IProjectTab
{
    /// <summary>The stable tab identifier.</summary>
    public const string TabId = "com.example.sample.ui.projectTab";

    /// <inheritdoc />
    public string Id => TabId;

    /// <inheritdoc />
    public string Title => "Sample insights";

    /// <inheritdoc />
    public int Order => 50;

    /// <inheritdoc />
    public Task<object> CreateViewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var grid = new PluginDataGrid();
        grid.Bind(
            ["Module", "Warnings", "Coverage"],
            [
                ["app", "0", "78%"],
                ["core", "2", "84%"],
                ["tools", "1", "61%"],
            ]);
        return Task.FromResult<object>(new PluginCard("Module insights") { Content = grid });
    }
}

/// <summary>A compact home widget with the sample build health trend.</summary>
public sealed class SampleBuildHealthWidget : IHomeWidget
{
    /// <summary>The stable widget identifier.</summary>
    public const string WidgetId = "com.example.sample.ui.homeWidget";

    /// <inheritdoc />
    public string Id => WidgetId;

    /// <inheritdoc />
    public string Title => "Build health";

    /// <inheritdoc />
    public int Order => 40;

    /// <inheritdoc />
    public Task<object> CreateViewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chart = new PluginMetricChart();
        chart.Plot([9, 8, 9, 7, 8, 9, 9], "Health");
        return Task.FromResult<object>(new PluginCard("Build health") { Content = chart });
    }
}

/// <summary>A live session panel fed by the host's session snapshots.</summary>
public sealed class SampleSessionPanel : ISessionPanel
{
    /// <summary>The stable panel identifier.</summary>
    public const string PanelId = "com.example.sample.ui.sessionPanel";

    private TextBlock? _stateText;
    private TextBlock? _deviceText;
    private TextBlock? _logText;

    /// <inheritdoc />
    public string Id => PanelId;

    /// <inheritdoc />
    public string Title => "Session monitor";

    /// <inheritdoc />
    public int Order => 50;

    /// <inheritdoc />
    public Task<object> CreateViewAsync(SessionPanelContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _deviceText = new TextBlock
        {
            FontSize = 12,
            Text = context.DeviceSerial is { Length: > 0 } serial
                ? $"Device {context.DeviceName ?? serial}"
                : "No device selected",
        };
        _stateText = new TextBlock { FontSize = 12, Text = "Idle" };
        _logText = new TextBlock
        {
            FontSize = 11,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            MaxLines = 4,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            Text = "Waiting for log output...",
        };

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(_deviceText);
        panel.Children.Add(_stateText);
        panel.Children.Add(_logText);
        return Task.FromResult<object>(panel);
    }

    /// <inheritdoc />
    public void UpdateSession(SessionPanelSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_stateText is null || _deviceText is null || _logText is null)
        {
            return;
        }

        _stateText.Text = snapshot.IsFinished
            ? $"State {snapshot.State} · {snapshot.TotalLogLines} lines"
            : $"State {snapshot.State} (live) · {snapshot.TotalLogLines} lines";
        _deviceText.Text = snapshot.DeviceSerial is { Length: > 0 } serial
            ? $"Device {snapshot.DeviceName ?? serial}"
            : "No device selected";
        _logText.Text = snapshot.RecentLogLines.Count > 0
            ? snapshot.RecentLogLines[^1]
            : "Waiting for log output...";
    }
}

/// <summary>A settings section whose values the host validates on save.</summary>
public sealed class SampleSettingsSection : ISettingsSection
{
    /// <summary>The stable section identifier.</summary>
    public const string SectionId = "com.example.sample.ui.settingsSection";

    /// <summary>The settings key the section manages, from the plugin's settings schema.</summary>
    public const string RetriesKey = "retries";

    /// <inheritdoc />
    public string Id => SectionId;

    /// <inheritdoc />
    public string Title => "Sample run options";

    /// <inheritdoc />
    public string? Description => "Retries are stored through the host, which validates them against the sample schema.";

    /// <inheritdoc />
    public int Order => 50;

    /// <inheritdoc />
    public Task<object> CreateViewAsync(ISettingsSectionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var box = new TextBox { Width = 160, PlaceholderText = "0-10" };
        var status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };
        var save = new Button { Content = "Save" };

        async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            status.Text = string.Empty;
            if (!int.TryParse(box.Text, out var retries) || retries is < 0 or > 10)
            {
                status.Text = "Enter a whole number between 0 and 10.";
                return;
            }

            try
            {
                // The host validates the write against the plugin schema; a
                // rejected value throws and nothing is persisted.
                await context.Settings.SetAsync(RetriesKey, box.Text, cancellationToken);
                status.Text = "Saved.";
            }
            catch (Exception exception)
            {
                status.Text = $"The host rejected the value: {exception.Message}";
            }
        }

        save.Click += OnSaveClick;

        async Task LoadAsync()
        {
            try
            {
                box.Text = await context.Settings.GetAsync(RetriesKey, cancellationToken) ?? string.Empty;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _ = LoadAsync();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(save);
        panel.Children.Add(status);
        return Task.FromResult<object>(panel);
    }
}

/// <summary>A clickable status bar item that opens the sample dialog.</summary>
public sealed class SampleStatusBarItem(SampleUiPlugin owner) : IStatusBarClickableItem
{
    /// <summary>The stable item identifier.</summary>
    public const string ItemId = "com.example.sample.ui.statusItem";

    /// <inheritdoc />
    public string Id => ItemId;

    /// <inheritdoc />
    public string Title => "Sample plugin: open the sample dialog";

    /// <inheritdoc />
    public int Order => 50;

    /// <inheritdoc />
    public string GetText() => $"Sample · {owner.LastDialogChoice}";

    /// <inheritdoc />
    public Task InvokeClickAsync(CancellationToken cancellationToken) =>
        owner.ShowAboutDialogAsync(cancellationToken);
}

/// <summary>A toolbar action that opens the sample dialog.</summary>
public sealed class SampleDialogToolbarAction(SampleUiPlugin owner) : IDeviceToolbarAction
{
    /// <summary>The stable action identifier.</summary>
    public const string ActionId = "com.example.sample.ui.toolbar";

    /// <inheritdoc />
    public string Id => ActionId;

    /// <inheritdoc />
    public string Title => "Sample dialog";

    /// <inheritdoc />
    public string? ToolTip => "Opens the sample plugin's dialog.";

    /// <inheritdoc />
    public int Order => 50;

    /// <inheritdoc />
    public bool RequiresConfirmation => false;

    /// <inheritdoc />
    public Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken) =>
        owner.ShowAboutDialogAsync(cancellationToken);
}

/// <summary>A palette command that opens the sample dialog.</summary>
public sealed class SampleDialogCommand(SampleUiPlugin owner) : ICommand
{
    /// <summary>The stable command identifier.</summary>
    public const string CommandId = "com.example.sample.ui.showDialog";

    /// <inheritdoc />
    public string Id => CommandId;

    /// <inheritdoc />
    public string Title => "Sample: show sample dialog";

    /// <inheritdoc />
    public string Description => "Opens the sample plugin's dialog through the host.";

    /// <inheritdoc />
    public IReadOnlyList<string> Keywords { get; } = ["sample", "dialog", "plugin"];

    /// <inheritdoc />
    public Task ExecuteAsync(CancellationToken cancellationToken) =>
        owner.ShowAboutDialogAsync(cancellationToken);
}

/// <summary>The sample dialog, shown by the host as a ContentDialog.</summary>
public sealed class SampleAboutDialog : IPluginDialog
{
    /// <inheritdoc />
    public string Id => SampleUiPlugin.AboutDialogId;

    /// <inheritdoc />
    public string Title => "Sample plugin";

    /// <inheritdoc />
    public IReadOnlyList<PluginDialogButton> Buttons { get; } =
    [
        new PluginDialogButton("primary", "Nice", true),
        new PluginDialogButton("secondary", "Meh", false),
    ];

    /// <inheritdoc />
    public Task<object> CreateContentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = new TextBlock
        {
            Text = "This dialog is hosted by DroidDeck. The sample plugin never hosts its own window.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
        };
        return Task.FromResult<object>(text);
    }

    /// <inheritdoc />
    public Task OnClosedAsync(string? buttonId, CancellationToken cancellationToken) => Task.CompletedTask;
}

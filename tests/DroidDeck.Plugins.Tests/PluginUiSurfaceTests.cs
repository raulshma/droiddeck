using System.Diagnostics;
using System.Text.Json.Nodes;
using DroidDeck.Core;
using DroidDeck.Core.Security;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Covers the host seams behind the remaining plugin UI surfaces: session
/// panel updates that cannot break siblings, dialog content creation and
/// close callbacks under fault isolation, and settings sections whose values
/// the host validates before anything is persisted.
/// </summary>
public sealed class PluginUiSurfaceTests
{
    private const string PluginId = "com.example.surfaces";

    // --- Session panel updates ---

    [Fact]
    public void SessionPanelUpdateRecordsSuccessOnTheCallingThread()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);
        var panel = new RecordingPanel("panel.ok");

        var result = invoker.UpdateSessionPanel(PluginId, panel, SampleSnapshot("Running"));

        Assert.True(result.Succeeded);
        Assert.Equal("Running", panel.LastSnapshot?.State);
        Assert.True(health.Get(PluginId).IsHealthy);
        Assert.Equal(1, health.Get(PluginId).SuccessCount);
    }

    [Fact]
    public void ThrowingPanelUpdateIsIsolatedAndMarksOnlyThatPluginUnhealthy()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);
        var broken = new RecordingPanel("panel.broken", throwOnUpdate: true);
        var sibling = new RecordingPanel("panel.sibling");
        var snapshot = SampleSnapshot("Running");

        var failed = invoker.UpdateSessionPanel(PluginId, broken, snapshot);

        Assert.False(failed.Succeeded);
        Assert.Equal("panel exploded", failed.Error);
        Assert.False(health.Get(PluginId).IsHealthy);
        Assert.Equal("panel exploded", health.Get(PluginId).LastError);
        Assert.Null(sibling.LastSnapshot);

        var delivered = invoker.UpdateSessionPanel(PluginId, sibling, snapshot);

        Assert.True(delivered.Succeeded);
        Assert.Equal(snapshot, sibling.LastSnapshot);
        Assert.True(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public void SessionPanelUpdateRejectsUnknownArguments()
    {
        var invoker = new PluginActionInvoker(new PluginHealthTracker());

        Assert.Throws<ArgumentNullException>(
            () => invoker.UpdateSessionPanel(null!, new RecordingPanel("p"), SampleSnapshot("Running")));
        Assert.Throws<ArgumentNullException>(
            () => invoker.UpdateSessionPanel(PluginId, null!, SampleSnapshot("Running")));
        Assert.Throws<ArgumentNullException>(
            () => invoker.UpdateSessionPanel(PluginId, new RecordingPanel("p"), null!));
    }

    // --- Dialogs ---

    [Fact]
    public async Task DialogCreationCarriesTheIsolatedContentOutcome()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);
        var dialog = new TestDialog("dialog.ok", content: "dialog body");

        var presentation = await invoker.CreateDialogAsync(PluginId, dialog);

        Assert.True(presentation.Succeeded);
        Assert.Equal("dialog body", presentation.Content.Content);
        Assert.Equal(dialog, presentation.Dialog);
        Assert.True(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public async Task FailingDialogContentIsReportedAsAFallbackInsteadOfCrashing()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);

        var presentation = await invoker.CreateDialogAsync(
            PluginId,
            new TestDialog("dialog.broken", throwOnCreate: true));

        Assert.False(presentation.Succeeded);
        Assert.Null(presentation.Content.Content);
        Assert.Equal("dialog content failed", presentation.Content.Error);
        Assert.False(health.Get(PluginId).IsHealthy);
    }

    [Fact]
    public async Task SlowDialogContentTimesOutInsteadOfBlockingTheHost()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health, TimeSpan.FromMilliseconds(50));

        var presentation = await invoker.CreateDialogAsync(
            PluginId,
            new TestDialog("dialog.slow", hangOnCreate: true));

        Assert.False(presentation.Succeeded);
        Assert.True(presentation.Content.TimedOut);
    }

    [Fact]
    public async Task DialogCloseCallbackIsFaultIsolated()
    {
        var health = new PluginHealthTracker();
        var invoker = new PluginActionInvoker(health);

        var result = await invoker.InvokeDialogClosedAsync(
            PluginId,
            new TestDialog("dialog.close", throwOnClose: true),
            buttonId: "primary");

        Assert.False(result.Succeeded);
        Assert.Equal("close handler failed", result.Error);
        Assert.False(health.Get(PluginId).IsHealthy);

        var success = await invoker.InvokeDialogClosedAsync(
            PluginId,
            new TestDialog("dialog.close"),
            buttonId: null);
        Assert.True(success.Succeeded);
    }

    // --- Settings sections ---

    [Fact]
    public async Task SettingsSectionWritesThroughTheHostStoreValidateBeforePersisting()
    {
        using var temp = new TempDirectory();
        var directory = temp.CreateDirectory("plugin");
        await File.WriteAllTextAsync(Path.Combine(directory, PluginSettingsSchemaReader.FileName), """
            { "version": 1, "properties": {
                "retries": { "type": "integer", "minimum": 0, "maximum": 10 }
              } }
            """);
        var read = await PluginSettingsSchemaReader.ReadAsync(directory);
        Assert.True(read.Validation.IsValid, string.Join(Environment.NewLine, read.Validation.Issues));
        using var secrets = new DpapiSecretStore(new AppPaths(temp.Combine("data")));
        using var settings = new SchemaPluginSettings(
            Path.Combine(directory, "settings.json"),
            read.Schema!,
            PluginId,
            secrets);
        var context = new PluginSettingsSectionContext(settings);

        var invalid = await RecordExceptionAsync(() => context.Settings.SetAsync("retries", "9999"));

        Assert.IsType<PluginSettingsValidationException>(invalid);
        Assert.Null(await settings.GetAsync("retries"));
        Assert.False(File.Exists(settings.FilePath), "an invalid value must not persist anything");

        await context.Settings.SetAsync("retries", "3");
        Assert.Equal("3", await settings.GetAsync("retries"));
    }

    [Fact]
    public async Task SettingsSectionValidationSurfacesHostErrorsForTheUi()
    {
        using var temp = new TempDirectory();
        var directory = temp.CreateDirectory("plugin");
        await File.WriteAllTextAsync(Path.Combine(directory, PluginSettingsSchemaReader.FileName), """
            { "version": 1, "properties": {
                "retries": { "type": "integer", "minimum": 0, "maximum": 10 }
              } }
            """);
        var read = await PluginSettingsSchemaReader.ReadAsync(directory);
        Assert.True(read.Validation.IsValid, string.Join(Environment.NewLine, read.Validation.Issues));
        var settingsPath = Path.Combine(directory, "settings.json");
        // A hand-edited or legacy settings file can hold values the schema
        // rejects; the section asks the host to validate the stored document.
        await File.WriteAllTextAsync(
            settingsPath,
            """{ "settingsVersion": 1, "values": { "retries": 99 } }""");
        using var secrets = new DpapiSecretStore(new AppPaths(temp.Combine("data")));
        using var settings = new SchemaPluginSettings(settingsPath, read.Schema!, PluginId, secrets);
        var context = new PluginSettingsSectionContext(settings);

        var errors = await context.ValidateAsync();

        var error = Assert.Single(errors);
        Assert.Equal("retries", error.Field);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));

        await context.Settings.SetAsync("retries", "5");
        Assert.Empty(await context.ValidateAsync());
    }

    [Fact]
    public async Task SettingsSectionWithoutSchemaValidatesAsEmpty()
    {
        var store = new MemoryPluginSettings();
        var context = new PluginSettingsSectionContext(store);

        await store.SetAsync("anything", "goes");
        Assert.Empty(await context.ValidateAsync());
        Assert.Equal("goes", await context.Settings.GetAsync("anything"));
    }

    private static SessionPanelSnapshot SampleSnapshot(string state) =>
        new("session-1", "emulator-5554", "Pixel", state, false, 42, ["I/Tag: line"]);

    private static async Task<Exception?> RecordExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class RecordingPanel(string id, bool throwOnUpdate = false) : ISessionPanel
    {
        public SessionPanelSnapshot? LastSnapshot { get; private set; }

        public string Id => id;

        public string Title => id;

        public int Order => 0;

        public Task<object> CreateViewAsync(SessionPanelContext context, CancellationToken cancellationToken) =>
            Task.FromResult<object>(id);

        public void UpdateSession(SessionPanelSnapshot snapshot)
        {
            if (throwOnUpdate)
            {
                throw new InvalidOperationException("panel exploded");
            }

            LastSnapshot = snapshot;
        }
    }

    private sealed class TestDialog(
        string id,
        string content = "content",
        bool throwOnCreate = false,
        bool hangOnCreate = false,
        bool throwOnClose = false) : IPluginDialog
    {
        public string Id => id;

        public string Title => "Test dialog";

        public IReadOnlyList<PluginDialogButton> Buttons { get; } =
            [new PluginDialogButton("primary", "Primary", true)];

        public Task<object> CreateContentAsync(CancellationToken cancellationToken)
        {
            if (throwOnCreate)
            {
                throw new InvalidOperationException("dialog content failed");
            }

            if (hangOnCreate)
            {
                return HangAsync(cancellationToken);

                static async Task<object> HangAsync(CancellationToken token)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new UnreachableException();
                }
            }

            return Task.FromResult<object>(content);
        }

        public Task OnClosedAsync(string? buttonId, CancellationToken cancellationToken)
        {
            if (throwOnClose)
            {
                throw new InvalidOperationException("close handler failed");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class MemoryPluginSettings : IPluginSettings
    {
        private readonly Dictionary<string, string?> _values = [];

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}

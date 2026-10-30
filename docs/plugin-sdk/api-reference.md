# Plugin API reference

Plugin API version **1.0**. The ABI is stable within a major version.

The contracts ship in two NuGet packages:

| Package | Namespaces | Target |
| --- | --- | --- |
| `DroidDeck.Plugin.Abstractions` | `DroidDeck.Plugin.Abstractions`, `DroidDeck.Plugin.Abstractions.Rendering` | `net10.0` |
| `DroidDeck.Plugin.UI` | `DroidDeck.Plugin.UI.Kit`, `DroidDeck.Plugin.UI.Rendering` | `net10.0-windows10.0.26100.0` |

Everything in this reference is covered by the package's generated XML
documentation. The host shares both assemblies with plugins; never copy them
into a plugin package.

## Lifecycle

### `IPlugin`

Every plugin has exactly one entry type implementing `IPlugin`.

| Member | Description |
| --- | --- |
| `Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)` | Registers extensions and subscribes to events. The host calls it once per load. |
| `Task ShutdownAsync(CancellationToken cancellationToken)` | Releases resources and unsubscribes. The host calls it once per unload. |

The host loads plugins in dependency order, initializes them in that order, and
shuts them down in reverse order. An exception from `InitializeAsync` marks the
plugin failed; the registry contributions made before the exception are removed.

### `IPluginInfo`

Read-only metadata: `Id`, `Name`, `Version`, `ApiVersion`, and `InstallPath`.
`InstallPath` is the plugin's own folder.

### `IPluginContext`

| Member | Description |
| --- | --- |
| `IPluginInfo Info` | Plugin metadata. |
| `ILogger Logger` | Plugin-scoped `Microsoft.Extensions.Logging.ILogger`, visible in the plugin's **Logs**. |
| `IPluginRegistry Extensions` | The shared extension registry. |
| `IPluginDialogService Dialogs` | Shows this plugin's registered dialogs on the UI thread. |
| `IPluginSettings Settings` | The plugin-private settings store. |
| `string DataDirectory` | A plugin-private folder that survives updates. |

### `IPluginSettings`

Raw string access to the plugin's settings document.

| Member | Description |
| --- | --- |
| `Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)` | Reads a value, or `null` when absent. |
| `Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)` | Writes a value; `null` removes it. |
| `Task RemoveAsync(string key, CancellationToken cancellationToken = default)` | Removes a value. |

When a plugin ships `settings.schema.json`, raw writes are validated through
the schema before they are persisted. See [Settings schema](#settings-schema)
and the [settings reference](../settings.md).

## Extension registry

### `IPluginRegistry`

The registry is shared by built-in features and plugins. `Add` methods reject
extensions whose `Id` duplicates one already registered by another plugin.
The matching collection properties enumerate `PluginExtension<T>` records in
registration order.

| Add method | Collection | Contract |
| --- | --- | --- |
| `AddCommand` | `Commands` | `ICommand` |
| `AddProjectDetector` | `ProjectDetectors` | `IProjectDetector` |
| `AddDeviceAction` | `DeviceActions` | `IDeviceAction` |
| `AddDeviceTab` | `DeviceTabs` | `IDeviceTabProvider` |
| `AddToolbarAction` | `ToolbarActions` | `IDeviceToolbarAction` |
| `AddRunStep` | `RunSteps` | `IRunStep` |
| `AddLogParser` | `LogParsers` | `ILogParser` |
| `AddHighlightRuleProvider` | `HighlightRuleProviders` | `IHighlightRuleProvider` |
| `AddTestRunner` | `TestRunners` | `ITestRunner` |
| `AddNavigationPage` | `NavigationPages` | `INavigationPage` |
| `AddProjectTab` | `ProjectTabs` | `IProjectTab` |
| `AddHomeWidget` | `HomeWidgets` | `IHomeWidget` |
| `AddSessionPanel` | `SessionPanels` | `ISessionPanel` |
| `AddSettingsSection` | `SettingsSections` | `ISettingsSection` |
| `AddStatusBarItem` | `StatusBarItems` | `IStatusBarItem` |
| `AddDialog` | `Dialogs` | `IPluginDialog` |

`IPluginRegistry` also exposes:

| Member | Description |
| --- | --- |
| `string? GetOwner(Type extensionType)` | Returns the plugin that registered a type, or `null`. |
| `int RemoveAll(string pluginId)` | Removes every extension of one plugin and returns the count. |
| `event EventHandler? Changed` | Raised after any extension is added or removed. |

### `PluginExtension<T>`

`PluginExtension<T>(string PluginId, T Value)` records a contribution and its
owner. `T` is the contract interface.

### `PluginConstants`

`PluginConstants.BuiltInPluginId` (`"droiddeck.builtin"`) is the owner id used
by host contributions.

### Registration helpers

`PluginUiRegistryExtensions` in `DroidDeck.Plugin.Abstractions` adds generic
helpers that create the contribution:

- `AddNavigationPage<TPage>()`
- `AddProjectTab<TTab>()`
- `AddHomeWidget<TWidget>()` and `AddHomeWidget<TWidget>(int order)`
- `AddSessionPanel<TPanel>()` and `AddSessionPanel<TPanel>(int order)`
- `AddSettingsSection<TSection>()` and `AddSettingsSection<TSection>(int order)`
- `AddStatusBarItem<TItem>()`
- `AddDialog<TDialog>()`

`RendererRegistryExtensions` in `DroidDeck.Plugin.UI.Rendering` adds
`AddRenderer<T>()` and `AddRenderer(Type)`.

All generic helpers require a public parameterless constructor. The overloads
that accept an `order` override the value the contribution declares.

## Commands and detection

### `ICommand`

| Member | Description |
| --- | --- |
| `string Id` | Stable identifier, unique across plugins. |
| `string Title` | Display title in the palette and menus. |
| `string Description` | Description used by search. |
| `IReadOnlyList<string> Keywords` | Additional search keywords. |
| `Task ExecuteAsync(CancellationToken cancellationToken)` | Runs the command. |

### `IProjectDetector`

| Member | Description |
| --- | --- |
| `string Id` | Stable detector identifier. |
| `int Priority` | Lower values run first. |
| `Task<ProjectDetection?> DetectAsync(ProjectDetectionContext context, CancellationToken cancellationToken)` | Returns a detection or `null`. |

`ProjectDetectionContext(string ProjectRoot)` supplies the folder under test.
`ProjectDetection(string Kind, string DisplayName, IReadOnlyList<string> Evidence)`
is the evidence-backed result. The first detector (by priority) that returns a
detection wins.

## Device actions, toolbar actions, and tabs

### `IDeviceAction`

| Member | Description |
| --- | --- |
| `string Id` | Stable action identifier. |
| `string Name` | Display name. |
| `bool RequiresConfirmation` | Whether the host asks the user before running it. |
| `Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)` | Runs the action. |

`DeviceActionContext(string Serial, string? Model)` identifies the target device.

### `IDeviceToolbarAction`

Extends a device action with host toolbar placement:

| Member | Description |
| --- | --- |
| `string Id` | Stable identifier. |
| `string Title` | Button title. |
| `string? ToolTip` | Optional tooltip. |
| `int Order` | Lower values appear first. |
| `bool RequiresConfirmation` | Whether the host asks before running. |
| `Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)` | Runs the action. |

### `IDeviceTabProvider`

| Member | Description |
| --- | --- |
| `string Id` | Stable tab identifier. |
| `string Title` | Tab title. |
| `int Order` | Lower values appear first. |
| `object CreateContent(DeviceTabContext context)` | Creates the tab content once per device. The host owns it and disposes it when the tab closes. |

`DeviceTabContext(string Serial, string? Model)` identifies the device.

## Run steps

### `IRunStep`

| Member | Description |
| --- | --- |
| `string Id` | Stable step identifier. |
| `Task<RunStepResult> ExecuteAsync(RunStepContext context, CancellationToken cancellationToken)` | Runs the step. |

`RunStepContext(string ProjectRoot, string? DeviceSerial)` identifies the run.
`RunStepResult(bool Succeeded, string? Summary)` reports the outcome. A failed
step stops the pipeline.

## Logs

### `ILogParser`

| Member | Description |
| --- | --- |
| `string Id` | Stable parser identifier. |
| `string Name` | Display name. |
| `int Priority` | Lower values run first. |
| `LogParserResult? Parse(LogParserContext context)` | Returns replacement fields, or `null` when the parser does not apply. |

`LogParserContext(string DeviceSerial, string RawLine, string Tag, string Level,
string Message)` carries the parsed fields of one line. Parsers run on the log
pipeline thread, never on the UI thread, and must be fast.

`LogParserResult(string? Tag = null, string? Level = null, string? Message = null,
string? HighlightRuleId = null)` replaces fields; a `null` field keeps the
parsed value.

### `IHighlightRuleProvider`

| Member | Description |
| --- | --- |
| `string Id` | Stable provider identifier. |
| `IReadOnlyList<PluginHighlightRule> Rules` | Rules in evaluation order. |

`PluginHighlightRule(string Id, string Name, string Pattern, bool IsRegex = false,
string Foreground = "#FF5555", string? Background = null, bool Bold = false,
bool IsEnabled = true)` describes one rule. Colors are `#RRGGBB`.

## Test runners

### `ITestRunner`

| Member | Description |
| --- | --- |
| `string Id` | Stable runner identifier. |
| `string Name` | Display name in the test UI. |
| `string? Description` | Optional description. |
| `bool RequiresDevice` | Whether a connected device is required. |
| `bool SupportsRerun` | Whether a failed-only rerun can narrow the selection. |
| `Task<PluginTestRunResult> RunAsync(TestRunnerContext context, CancellationToken cancellationToken)` | Runs one selection. |

`TestRunnerContext` carries `ProjectRoot`, `ProjectName`, `DeviceSerial`,
`VariantName`, `ModuleName`, `PackageName`, `FailedTests`, `ResultDirectory`,
and `Settings` (case-insensitive string pairs).

`TestRunnerTarget(string? ClassName, string Name, string? FilePath)` selects a
failing test for a rerun.

`PluginTestRunResult(bool Succeeded, IReadOnlyList<PluginTestSuite>? Suites = null,
string? Output = null, string? Error = null)` is the run outcome.
`PluginTestSuite(string Name, IReadOnlyList<PluginTestCase> Cases)` groups cases.
`PluginTestCase(string Name, PluginTestStatus Status, string? ClassName = null,
double? DurationSeconds = null, string? Message = null, string? StackTrace = null,
string? FilePath = null, int? Line = null,
IReadOnlyList<PluginTestAttachment>? Attachments = null)` reports one case.
`PluginTestAttachment(string Name, string? Path = null, string? Content = null)`
attaches a file or inline text.

### `PluginTestStatus`

`Passed`, `Failed`, `Errored`, `Skipped`.

## UI surfaces

The host creates each view lazily the first time the surface is shown, on the
UI thread, inside a fault boundary. Views return `object`; return a WinUI
`FrameworkElement` composed from the [UI kit](#ui-kit). The host owns the
returned element. The view creation call is timeout-isolated.

### `INavigationPage`

| Member | Description |
| --- | --- |
| `string Id` | Stable page identifier, unique across plugins. |
| `string Title` | Page and navigation title. |
| `string Icon` | Segoe UI glyph, or empty for the default icon. |
| `Task<object> CreateViewAsync(CancellationToken cancellationToken)` | Creates the content. |

### `IProjectTab`

`Id`, `Title`, `Order` (lower first), and
`Task<object> CreateViewAsync(CancellationToken cancellationToken)`.

### `IHomeWidget`

`Id`, `Title`, `Order` (lower first), and
`Task<object> CreateViewAsync(CancellationToken cancellationToken)`. The host
creates widgets one at a time after the first frame, so a slow widget cannot
block Home or its siblings.

### `ISessionPanel`

| Member | Description |
| --- | --- |
| `string Id` | Stable panel identifier. |
| `string Title` | Panel title. |
| `int Order` | Lower values appear first. |
| `Task<object> CreateViewAsync(SessionPanelContext context, CancellationToken cancellationToken)` | Creates the content for a session. |
| `void UpdateSession(SessionPanelSnapshot snapshot)` | Applies a snapshot on the UI thread. Must not throw. |

`SessionPanelContext(string? SessionId, string? DeviceSerial, string? DeviceName)`
is supplied once. `SessionPanelSnapshot(string? SessionId, string? DeviceSerial,
string? DeviceName, string State, bool IsFinished, int TotalLogLines,
IReadOnlyList<string> RecentLogLines)` is delivered as the session advances;
`RecentLogLines` are pre-formatted, oldest first. `SessionPanelSnapshot.Empty`
is the host's no-session value. A panel whose update throws is replaced with a
fallback card without disturbing its siblings.

### Settings sections

`ISettingsSection` exposes `Id`, `Title`, `string? Description`, `Order`, and
`Task<object> CreateViewAsync(ISettingsSectionContext context,
CancellationToken cancellationToken)`.

`ISettingsSectionContext` supplies:

| Member | Description |
| --- | --- |
| `IPluginSettings Settings` | The plugin's store; writes are validated by the host. |
| `Task<IReadOnlyList<SettingsSectionError>> ValidateAsync(CancellationToken cancellationToken = default)` | Validates the stored values and returns one error per invalid field. |

`SettingsSectionError(string Field, string Message)` is one field-level error.

### Status bar

`IStatusBarItem` exposes `Id`, `Title` (tooltip), `Order`, and `string GetText()`.
The host polls `GetText` on the UI thread a few times a second and isolates
failures; a throwing item shows placeholder text.

`IStatusBarClickableItem` extends `IStatusBarItem` with
`Task InvokeClickAsync(CancellationToken cancellationToken)` and is rendered as a
button.

### Dialogs

`IPluginDialog` describes one dialog:

| Member | Description |
| --- | --- |
| `string Id` | Stable dialog identifier, unique across the plugin. |
| `string Title` | Dialog title. |
| `IReadOnlyList<PluginDialogButton> Buttons` | Buttons; an empty list means a single Close button. |
| `Task<object> CreateContentAsync(CancellationToken cancellationToken)` | Creates the content with timeout isolation. |
| `Task OnClosedAsync(string? buttonId, CancellationToken cancellationToken)` | Receives the clicked button, or `null` when dismissed. |

`PluginDialogButton(string Id, string Text, bool IsPrimary)` describes a button.

`IPluginDialogService` exposes
`Task<PluginDialogResult> ShowAsync(string dialogId, CancellationToken cancellationToken = default)`,
which shows one of the plugin's own dialogs and returns
`PluginDialogResult(bool Shown, string? ButtonId, string? Error)`. The result
factories are `PluginDialogResult.Closed(string? buttonId)` and
`PluginDialogResult.Failed(string error)`.

`IPluginDialogServiceProvider` exposes `CreateScope(string pluginId)`, which
creates a plugin-scoped service; this is host infrastructure.
`NullPluginDialogService` is the host's no-UI implementation and returns
`PluginDialogResult.Failed(...)` for every call.

Plugins never host windows. The host presents dialogs on the shell's UI thread
and isolates plugin faults.

## UI kit

`DroidDeck.Plugin.UI.Kit` provides controls that follow the host theme, scaling,
and high-contrast settings. Compose plugin views from them so plugin surfaces
look and behave like host surfaces.

| Type | Description |
| --- | --- |
| `PluginPageLayout` | Page scaffold with a title, optional subtitle, header actions, and content. |
| `PluginCard` | Themed card with an optional title and arbitrary content. |
| `PluginDataGrid` | Read-only table; call `Bind(columns, rows)`. |
| `PluginInfoBar` | Themed info/warning/error bar; call `Show(...)` and `Close()`. |
| `PluginMetricChart` | Lightweight line chart; call `Plot(values, label)`. |
| `PluginFaultBoundary` | Hosts one plugin view and swaps it for a themed fallback card on failure. |
| `IPluginFaultReporter` | Receives `Report(pluginId, operation, exception)` calls from a boundary so the host can feed its crash guard. Host infrastructure. |

`PluginPageLayout(string title, string? subtitle = null)` exposes `Content`.
`PluginCard(string? title = null)` exposes `Content`.
`PluginInfoBar.Show(string message, InfoBarSeverity severity = InfoBarSeverity.Informational,
string? title = null)` opens the bar.

`PluginFaultBoundary(string pluginId, string operation, IPluginFaultReporter? faultReporter = null)`
exposes `Show(object? content, string? error)` and
`GuardAsync(Func<CancellationToken, Task> interaction, CancellationToken cancellationToken = default)`.
Every plugin interaction that can throw should run through `GuardAsync`.

## Renderers

Renderers replace built-in views on six surfaces. A renderer is a plain class
with a public parameterless constructor, registered with
`registry.AddRenderer<T>()`, and created on demand by the host.

### `RendererKind`

`LogDetail`, `LogLine`, `Chart`, `FilePreview`, `TestResult`, `DataView`.

### `IRenderer`

| Member | Description |
| --- | --- |
| `string Id` | Stable identifier, unique across plugins. |
| `string DisplayName` | Name shown in View as menus. |
| `RendererKind Kind` | The surface the renderer replaces. |
| `int Priority` | Ordering weight among renderers of the same kind; higher wins. |
| `bool CanRender(IRenderContext context)` | Whether the renderer can present the supplied data. |
| `FrameworkElement CreateView(IRenderContext context)` | Creates the view. The host owns it. |
| `void Dispose()` | Releases resources when the view closes. |

### `IRenderContext`

`Data` (the data transfer object), `Kind`, `Services` (host service provider
for optional services), `Theme` (`IThemeService`), `Dispatcher`
(`IUiDispatcher`), `CancellationToken` (cancelled when the view closes), and
`AvailableSize`. The host passes a `RendererContext`, the default
implementation.

`IThemeService` exposes `bool IsDark` and `bool IsHighContrast`.
`IUiDispatcher` exposes `bool HasThreadAccess` and
`Task InvokeAsync(Action action)`.

### Renderer data

| Record | Used by |
| --- | --- |
| `LogDetailData(string Line, string? Tag = null, string? Level = null)` | `LogDetail` |
| `LogLineData(string Text, string? Level = null)` | `LogLine` |
| `ChartData(string Title, IReadOnlyList<ChartSeries> Series, double Minimum = 0, double Maximum = 100, string Unit = "")` | `Chart` |
| `ChartSeries(string Label, IReadOnlyList<double> Values)` | Series inside `ChartData`; the host samples values. |
| `FilePreviewData(string FileName, string? Text = null, byte[]? Bytes = null)` | `FilePreview` |
| `TestResultData(string Name, string Status, string? Message = null, string? StackTrace = null, IReadOnlyList<string>? Attachments = null)` | `TestResult` |
| `DataViewData(string Title, IReadOnlyList<DataViewProperty>? Properties = null, string? Json = null)` | `DataView` |
| `DataViewProperty(string Name, string Value)` | A labeled value inside `DataViewData`. |

### Registration and catalog

`RendererRegistration(string PluginId, Type RendererType)` is one contributed
renderer type. `IRendererSource` exposes the registrations; `IRendererRegistrar`
extends it with `AddRenderer(Type)` and is implemented by the host extension
registry.

`RendererDescriptor(string Id, string PluginId, RendererKind Kind, string DisplayName,
int Priority = 0, bool IsBuiltIn = false)` identifies a renderer without a UI
reference. `RendererStatus(RendererDescriptor Renderer)` adds `Enabled`,
`Health`, `FailureCount`, `DisabledReason`, and `IsAvailable`.

`RendererHealth`: `Unknown`, `Healthy`, `Failing`, `Disabled`.

`RendererCatalog` tracks availability, the per-kind default, and failure
state; it is UI-free. `MaxConsecutiveFailures` is 3. Its members are
`Statuses`, `Register`, `RemoveAll(pluginId)`, `Remove(rendererId)`,
`Find(rendererId)`, `GetDefault(kind)`, `SetDefault(kind, rendererId)`,
`SetEnabled(rendererId, enabled)`, `Select(kind, preferredId, canRender)`,
`SelectBuiltIn(kind, canRender, excludeId)`, `ReportSuccess(rendererId)`,
`ReportFailure(rendererId, error)`, and `Changed`.

`RendererRunner.Render(RendererCatalog catalog, RendererKind kind,
string? preferredId, Func<RendererDescriptor, bool>? canRender,
Func<RendererDescriptor, object?> create)` selects a renderer, creates its view,
and falls back to the best built-in on failure. It returns
`RendererRunResult(RendererDescriptor? Renderer, object? View, bool IsFallback,
Exception? Error)`.

`BuiltInRendererDescriptors` exposes the descriptors of every host renderer,
plus `All`.

### Host renderer infrastructure

`IRendererHost` is the shell service a View as pane uses: `Catalog`,
`Renderers(kind)`, `CreateRenderer(descriptor)`,
`SetDefaultAsync(kind, rendererId, cancellationToken)`, `Theme`, `Dispatcher`,
and `Changed`. `RendererPane` is the reusable View as `UserControl` with
`Render(kind, data)`, `Clear()`, and `Dispose()`. These types are for host and
advanced integration code; a normal plugin only implements `IRenderer`.

## Settings schema

A plugin can ship `settings.schema.json` beside its manifest. The host parses
the schema, generates the settings UI, validates values, and migrates stored
values. The schema is a safe JSON Schema subset.

### `PluginSettingsSchema`

| Member | Description |
| --- | --- |
| `int Version` | The schema version stored settings migrate to. |
| `string? Title`, `string? Description` | Optional presentation text. |
| `IReadOnlyList<PluginSettingsField> Fields` | Declared fields in schema order. |
| `IReadOnlyList<PluginSettingsSchemaMigration> Migrations` | Migration steps in declaration order. |
| `PluginSettingsField? Find(string key)` | Finds a top-level field by key (ordinal). |

### `PluginSettingsField`

`Key`, `Type`, `Title`, `Description`, `Default` (a `JsonNode`), `Required`,
`EnumValues`, `Minimum`, `Maximum`, `MinLength`, `MaxLength`, `Pattern`,
`PatternRegex`, `ItemType`, `Properties`, and `DisplayName`.

### `PluginSettingsFieldType`

`Text`, `Number`, `WholeNumber`, `Boolean`, `Enum`, `Path`, `Secret`, `List`,
`Group`.

### `PluginSettingsSchemaMigration`

`FromVersion`, `Renames`, `Defaults`, and `Removed`. Steps with a `fromVersion`
at or above the stored version run in order; unknown keys are never touched.

See the [manifest and schema reference](manifest.md#settings-schema) for the
JSON shape and the supported keys.

## Manifest, capabilities, and versions

### `PluginManifest` and `PluginDependency`

`PluginManifest` mirrors `plugin.json`: `Id`, `Name`, `Version`, `Author`,
`Description`, `ApiVersion`, `MinHostVersion`, `MaxHostVersion`,
`EntryAssembly`, `EntryType`, `Capabilities`, `Dependencies`, `Icon`,
`Homepage`, and `License`, plus the computed `KnownCapabilities`,
`UnknownCapabilities`, and `HasCapability(capability)`.
`PluginDependency` has `Id` and `MinVersion`.

`PluginJsonContext` is the source-generated `System.Text.Json` context for
manifest types. Tools that read manifests should use it so they parse the same
way the host does.

### `PluginCapability`

A `[Flags]` enum with one value per manifest capability: `None`,
`ProjectDetector`, `StackAdapter`, `RunStep`, `DeviceAction`, `UiPage`,
`UiPanel`, `Renderer`, `Theme`, `Command`, `LogParser`, `TestRunner`,
`HomeWidget`, `ProjectTab`, `DeviceTab`, `SessionPanel`, `ToolbarAction`,
`SettingsSection`, `HighlightRuleProvider`, `ShellCommands`, `PluginData`,
`StatusBarItem`, and `PluginDialog`.

`PluginCapabilities.Parse(IEnumerable<string>? capabilities)` returns
`PluginCapabilityParseResult(PluginCapability Known, IReadOnlyList<string> Unknown)`.
`PluginCapabilities.ToManifestName(capability)` and
`PluginCapabilities.ToDisplayString(capabilities)` format values.

### `PluginApi`

`PluginApi.Version` is `"1.0"` and `PluginApi.HostVersion` is the parsed host
API version.

| Member | Description |
| --- | --- |
| `ApiCompatibility CheckCompatibility(string? pluginApiVersion, string? hostApiVersion = null)` | Compares a plugin API version with the host. |
| `bool IsCompatible(string? pluginApiVersion, string? hostApiVersion = null)` | Whether the plugin can load. Minor mismatches load with a warning. |

### `ApiCompatibility`

`Compatible`, `MinorMismatch`, `MajorMismatch`, `Invalid`.

### `SemanticVersion`

`SemanticVersion(int major, int minor, int patch, string? preRelease = null,
string? buildMetadata = null)` implements `IComparable<SemanticVersion>`,
`IEquatable<SemanticVersion>`, and the comparison operators. `Parse` accepts a
full semantic version; `TryParse` returns `false` instead of throwing.

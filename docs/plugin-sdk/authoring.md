# Authoring guide

This guide walks through building a DroidDeck plugin from a template, adding
each kind of extension, and installing the result. The full contract signatures
are in the [API reference](api-reference.md); the manifest and schema formats
are in the [manifest reference](manifest.md).

## 1. Create the project

Install the templates once per machine. The 0.1.0 packages are attached to
the [release](https://github.com/raulshma/droiddeck/releases/tag/v0.1.0),
not NuGet.org; download all four `.nupkg` files into one folder first:

```text
dotnet new install <sdk-folder>\DroidDeck.Plugin.Templates.0.1.0.nupkg
```

Create a plugin from one of the four starters:

```text
dotnet new droiddeck-plugin-command -n HelloPlugin -o HelloPlugin --pluginId com.example.hello
dotnet new droiddeck-plugin-device-action -n DeviceTools -o DeviceTools --pluginId com.example.devicetools
dotnet new droiddeck-plugin-page -n MetricsPage -o MetricsPage --pluginId com.example.metrics
dotnet new droiddeck-plugin-renderer -n LogInspector -o LogInspector --pluginId com.example.loginspector
```

Use a unique, lowercase plugin id: letters, digits, dots, and dashes. The id is
the folder name used by the host and the identity the user trusts, so do not
change it between releases.

| Template | Creates | Target framework |
| --- | --- | --- |
| `droiddeck-plugin-command` | One command plus its manifest | `net10.0` |
| `droiddeck-plugin-device-action` | One device action plus its manifest | `net10.0` |
| `droiddeck-plugin-page` | One navigation page plus its manifest | `net10.0-windows10.0.26100.0` |
| `droiddeck-plugin-renderer` | One renderer plus its manifest | `net10.0-windows10.0.26100.0` |

The generated project pins the SDK version through the `PluginSdkVersion`
property and, for UI templates, the Windows App SDK version through
`PluginWindowsAppSdkVersion`. Override either on the command line when you need
a different version:

```text
dotnet new droiddeck-plugin-page -n MetricsPage --pluginId com.example.metrics --sdkVersion 0.1.0
```

### Adding the SDK to an existing project

Add the folder that holds the release `.nupkg` files as a package source in
`NuGet.config`, then reference the SDK version:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="droiddeck-sdk" value="<sdk-folder>" />
  </packageSources>
</configuration>
```

A non-UI plugin references the abstractions package only:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DroidDeck.Plugin.Abstractions" Version="0.1.0" />
  </ItemGroup>
</Project>
```

A UI plugin targets Windows and references the component kit instead:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <UseWinUI>true</UseWinUI>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="DroidDeck.Plugin.Abstractions" Version="0.1.0" />
    <PackageReference Include="DroidDeck.Plugin.UI" Version="0.1.0" />
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.5.1" />
  </ItemGroup>
</Project>
```

Do not reference `DroidDeck.App`. The shell is not part of the plugin contract,
and referencing it breaks the isolated load context.

## 2. Implement the entry point

Every plugin has one public type implementing `IPlugin`:

```csharp
using DroidDeck.Plugin.Abstractions;

namespace HelloPlugin;

public sealed class HelloPlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Extensions.AddCommand(new HelloCommand(context.Logger));
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

Register everything in `InitializeAsync`, not in the constructor: the host
creates the entry type before it builds the plugin context. Undo work in
`ShutdownAsync`; the host removes the plugin's registry contributions and
renderers automatically.

`IPluginContext` gives the plugin:

- `Info` — id, name, version, API version, install path.
- `Logger` — an `ILogger` scoped to the plugin and shown in the plugin's log view.
- `Extensions` — the shared registry.
- `Dialogs` — showing plugin dialogs on the UI thread.
- `Settings` — the plugin-private settings store.
- `DataDirectory` — a writable folder that survives updates.

## 3. Add extensions

### Command

```csharp
using DroidDeck.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

internal sealed class HelloCommand(ILogger logger) : ICommand
{
    public string Id => "com.example.hello.sayHello";
    public string Title => "Say hello";
    public string Description => "Writes a greeting to the plugin log.";
    public IReadOnlyList<string> Keywords => ["hello", "sample"];

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Hello from {Plugin}.", "com.example.hello");
        }

        return Task.CompletedTask;
    }
}
```

Use `logger.IsEnabled` around structured logging calls that compute arguments
or properties; the logging source generator analyzer flags unconditional
expensive calls.

### Project detector

A detector inspects a folder and returns evidence-backed detections. Lower
`Priority` values run first.

```csharp
internal sealed class IonicProjectDetector(ILogger logger) : IProjectDetector
{
    public string Id => "com.example.ionic.detector";
    public int Priority => 50;

    public Task<ProjectDetection?> DetectAsync(
        ProjectDetectionContext context,
        CancellationToken cancellationToken)
    {
        var evidence = new List<string>();
        foreach (var name in new[] { "ionic.config.json", "capacitor.config.json" })
        {
            if (File.Exists(Path.Combine(context.ProjectRoot, name)))
            {
                evidence.Add(name);
            }
        }

        if (evidence.Count == 0)
        {
            return Task.FromResult<ProjectDetection?>(null);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Detected an Ionic project with {Count} markers.", evidence.Count);
        }

        return Task.FromResult<ProjectDetection?>(
            new ProjectDetection("ionic", "Ionic", evidence));
    }
}
```

### Device action

`RequiresConfirmation` makes the host show the action's target, name, and
capabilities before running it. Treat the device serial as opaque and never put
it in file names without sanitizing it.

```csharp
internal sealed class ConfirmedMarkerAction(string dataDirectory, ILogger logger) : IDeviceAction
{
    public string Id => "com.example.tools.confirmedMarker";
    public string Name => "Write a marker for this device";
    public bool RequiresConfirmation => true;

    public async Task ExecuteAsync(DeviceActionContext context, CancellationToken cancellationToken)
    {
        var safeSerial = string.Concat(context.Serial.Where(char.IsLetterOrDigit));
        var path = Path.Combine(dataDirectory, $"device-action-{safeSerial}.txt");
        await File.WriteAllTextAsync(
            path,
            $"{context.Model ?? "unknown model"}{Environment.NewLine}",
            cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Wrote a device marker for {Serial}.", context.Serial);
        }
    }
}
```

### Run step

```csharp
internal sealed class BuildStampStep(string dataDirectory) : IRunStep
{
    public string Id => "com.example.hello.buildStamp";

    public async Task<RunStepResult> ExecuteAsync(
        RunStepContext context,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataDirectory, "last-run.txt");
        await File.WriteAllTextAsync(path, context.ProjectRoot, cancellationToken)
            .ConfigureAwait(false);
        return new RunStepResult(true, "Recorded the project root.");
    }
}
```

Return `Succeeded: false` to report a failure; the run configuration's failure
policy decides whether the pipeline continues, retries, or stops. Steps run
under a configured timeout, and a step that throws is reported as a failure with
the exception message.

### Log parser and highlight rules

Log parsers run on the log pipeline thread. Keep `Parse` allocation-light and
never touch UI. Return `null` when the line is not yours.

```csharp
internal sealed class GreetingParser : ILogParser
{
    public string Id => "com.example.hello.logParser";
    public string Name => "Greeting parser";
    public int Priority => 100;

    public LogParserResult? Parse(LogParserContext context) =>
        context.Tag == "greeting"
            ? new LogParserResult(Message: context.Message.ToUpperInvariant())
            : null;
}

internal sealed class GreetingRules : IHighlightRuleProvider
{
    public string Id => "com.example.hello.highlightRules";

    public IReadOnlyList<PluginHighlightRule> Rules { get; } =
    [
        new PluginHighlightRule(
            "com.example.hello.highlightRules.greeting",
            "Greeting",
            "HELLO",
            Foreground: "#4EC9B0",
            Bold: true),
    ];
}
```

### Test runner

A test runner returns parsed suites instead of raw text. `SupportsRerun` says
whether the host may ask for a failed-only rerun through
`TestRunnerContext.FailedTests`.

```csharp
internal sealed class SmokeTestRunner : ITestRunner
{
    public string Id => "com.example.hello.smoke";
    public string Name => "Hello smoke tests";
    public string? Description => "Checks that the sample data directory exists.";
    public bool RequiresDevice => false;
    public bool SupportsRerun => false;

    public Task<PluginTestRunResult> RunAsync(
        TestRunnerContext context,
        CancellationToken cancellationToken)
    {
        var exists = Directory.Exists(context.ProjectRoot);
        var testCase = new PluginTestCase(
            "data-directory",
            exists ? PluginTestStatus.Passed : PluginTestStatus.Failed,
            Message: exists ? null : "The project root does not exist.");
        return Task.FromResult(new PluginTestRunResult(
            Succeeded: true,
            Suites: [new PluginTestSuite("Smoke", [testCase])]));
    }
}
```

### UI page, tab, widget, panel, section, status item, and dialog

UI extensions return host UI elements. Create views lazily; the host calls
`CreateViewAsync` the first time the surface is shown, on the UI thread, and
owns the result. Return `DroidDeck.Plugin.UI.Kit` controls where possible.

```csharp
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.UI.Kit;

public sealed class MetricsPage : INavigationPage
{
    public string Id => "com.example.metrics.page";
    public string Title => "Metrics";
    public string Icon => "\uE9D9";

    public Task<object> CreateViewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var card = new PluginCard("Build duration");
        var chart = new PluginMetricChart();
        chart.Plot([42, 38, 45, 40], "seconds");
        card.Content = chart;

        var page = new PluginPageLayout("Metrics", "Sampled build durations.");
        page.Content = card;
        return Task.FromResult<object>(page);
    }
}
```

Other surfaces follow the same shape:

- `IProjectTab` — a tab on the project page; `Order` controls placement.
- `IHomeWidget` — a home page widget; the host creates widgets one at a time
  after the first frame.
- `ISessionPanel` — a panel on the session page. The host calls
  `UpdateSession(SessionPanelSnapshot)` as state and logs advance. Cache the
  root element and mutate controls in `UpdateSession`; do not recreate views.
- `ISettingsSection` — a section inside the settings page. Read and write
  through `ISettingsSectionContext.Settings` so the host validates every write.
- `IStatusBarItem` / `IStatusBarClickableItem` — status bar text. `GetText` is
  polled on the UI thread; keep it cheap and non-throwing.
- `IPluginDialog` — a host-rendered dialog. Register it, then show it through
  `context.Dialogs.ShowAsync(dialogId)`. Never create a window.

A UI contribution that throws during view creation or update is replaced with a
fallback card. The rest of the shell keeps running.

### Renderer

Renderers receive a data transfer object and return a `FrameworkElement`.
Register the type, not an instance:

```csharp
using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

public sealed class LogInspectorPlugin : IPlugin
{
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Extensions.AddRenderer<InspectorLogDetailRenderer>();
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class InspectorLogDetailRenderer : IRenderer
{
    public string Id => "com.example.loginspector.detail";
    public string DisplayName => "Inspector";
    public RendererKind Kind => RendererKind.LogDetail;
    public int Priority => 10;

    public bool CanRender(IRenderContext context) => context.Data is LogDetailData;

    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (LogDetailData)context.Data;
        return new TextBlock
        {
            Text = data.Line,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
    }

    public void Dispose()
    {
    }
}
```

The host records failures; after three consecutive failures it disables the
renderer and falls back to the built-in view. `CanRender` must not throw:
return `false` for data you cannot present.

## 4. Settings

### Raw values

```csharp
await context.Settings.SetAsync("lastMessage", "hello", cancellationToken);
var last = await context.Settings.GetAsync("lastMessage", cancellationToken);
```

### Schema-driven settings

Ship `settings.schema.json` beside `plugin.json` to get a generated settings
form, validation, and migrations without writing UI:

```json
{
  "version": 1,
  "title": "Hello settings",
  "properties": {
    "greeting": {
      "type": "string",
      "title": "Greeting",
      "default": "Hello",
      "maxLength": 40
    },
    "retryCount": {
      "type": "integer",
      "title": "Retries",
      "default": 3,
      "minimum": 0,
      "maximum": 10
    },
    "mode": {
      "type": "string",
      "title": "Mode",
      "enum": ["plain", "fancy"],
      "default": "plain"
    },
    "token": {
      "type": "string",
      "format": "secret",
      "title": "API token"
    }
  }
}
```

Values are validated by the host before they are persisted, and secret values
are stored encrypted and never displayed. The supported subset, reserved keys,
migration format, and validation codes are documented in the
[manifest reference](manifest.md#settings-schema). The user-facing behavior is
in the [settings reference](../settings.md).

## 5. Test the plugin

Unit-test plain extension classes directly; they are ordinary classes with no
host dependency.

```csharp
[Fact]
public async Task DetectorFindsIonicProject()
{
    using var directory = new TempDirectory();
    await File.WriteAllTextAsync(
        Path.Combine(directory.Path, "ionic.config.json"),
        "{}");

    var detector = new IonicProjectDetector(NullLogger.Instance);
    var detection = await detector.DetectAsync(
        new ProjectDetectionContext(directory.Path),
        CancellationToken.None);

    Assert.NotNull(detection);
    Assert.Equal("ionic", detection.Kind);
}
```

For integration tests, load the built plugin exactly as the host does:
`PluginLoader.LoadAsync(pluginDirectory, ...)` with a staged folder containing
the manifest and assemblies. The repository's
`tests/DroidDeck.Plugins.Tests` project contains working examples, including the
sample plugins under `tests/DroidDeck.PluginSamples`.

## 6. Build, package, and install

Build the plugin:

```text
dotnet build HelloPlugin -c Release
```

The build output folder must contain `plugin.json`. Package it with the
packaging tool:

```text
dotnet tool install --global DroidDeck.PluginPack --add-source <sdk-folder> --version 0.1.0
droiddeck-plugin-pack HelloPlugin/bin/Release/net10.0 --output artifacts
```

The tool writes `com.example.hello-1.0.0.ddplugin` and prints its SHA-256.
Copy the folder (or install the package) as described in the
[packaging guide](packaging.md), then approve the plugin in **Extensions**.

## 7. Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Plugin is listed as failed with `manifest.entryAssembly.missing` | `entryAssembly` does not match the built DLL name, or the DLL was not copied into the package. |
| `manifest.apiVersion.majorMismatch` | The plugin was built against a different major API version. Rebuild against the installed SDK. |
| The plugin loads but nothing appears | Extensions were registered after `InitializeAsync` returned, or with a duplicate `Id` owned by another plugin. |
| A UI surface shows the fallback card | `CreateViewAsync`, the renderer, or a panel update threw. Check the plugin log in **Extensions**. |
| The plugin cannot load its own dependency | The dependency was excluded by the packaging tool because its name matches a host-provided assembly. Rename or vendor it, or rely on the host copy. |
| Settings show validation errors | The stored values do not satisfy the schema. Fix the schema or migrate the values. |

The host's plugin log records load failures, extension conflicts, timeouts, and
UI faults with the plugin id and the failing operation.

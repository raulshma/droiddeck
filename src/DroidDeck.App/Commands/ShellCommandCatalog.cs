using DroidDeck.App.Services;
using DroidDeck.App.Views;
using DroidDeck.Core.Commands;
using DroidDeck.Plugin.Abstractions;
using Microsoft.UI.Dispatching;

namespace DroidDeck.App.Commands;

/// <summary>
/// Registers the shell's palette commands (navigation, core actions, and shell
/// utilities) and mirrors plugin-contributed commands into the command registry.
/// Plugin loads finish in the background, so the catalog re-syncs whenever the
/// plugin registry reports changes.
/// </summary>
public sealed class ShellCommandCatalog
{
    private static readonly Dictionary<string, (string Name, string Keywords)> NavigationTargets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = ("Home", "start overview dashboard"),
            ["onboarding"] = ("First-run setup", "setup welcome tools"),
            ["devices"] = ("Devices", "phone adb connected list"),
            ["devices/tools"] = ("Device tools", "toolbox files intents ports input"),
            ["devices/mirror"] = ("Mirror", "scrcpy screen cast"),
            ["devices/emulators"] = ("Emulators", "avd virtual android"),
            ["devices/wireless"] = ("Wireless", "wifi pairing tcp/ip"),
            ["multi-device"] = ("Multi-device", "run group batch several"),
            ["devices/performance"] = ("Performance", "cpu memory charts sampler"),
            ["devices/inspector"] = ("UI inspector", "hierarchy view selector"),
            ["projects"] = ("Projects", "folder gradle open detect"),
            ["tests"] = ("Tests", "unit instrumented junit results"),
            ["session"] = ("Session", "logs live run output"),
            ["history"] = ("History", "past sessions archive"),
            ["activity"] = ("Activity", "timeline events"),
            ["extensions"] = ("Extensions", "plugins addons"),
            ["settings"] = ("Settings", "preferences configuration options shortcuts"),
            ["diagnostics"] = ("Diagnostics", "report troubleshooting health"),
        };

    private readonly ICommandRegistry _commands;
    private readonly PluginHost _pluginHost;
    private readonly MainWindow _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<string> _pluginCommandIds = [];
    private bool _detached;

    /// <summary>Creates the catalog; call from the UI thread.</summary>
    public ShellCommandCatalog(ICommandRegistry commands, PluginHost pluginHost, MainWindow window)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _pluginHost = pluginHost ?? throw new ArgumentNullException(nameof(pluginHost));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _pluginHost.Registry.Changed += OnPluginRegistryChanged;
    }

    /// <summary>Registers every shell command. Call once at startup.</summary>
    public void RegisterShellCommands()
    {
        RegisterNavigationCommands();
        RegisterActionCommands();
        RegisterPluginCommands();
    }

    /// <summary>Stops listening for plugin registry changes; called when the window closes.</summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _pluginHost.Registry.Changed -= OnPluginRegistryChanged;
    }

    private void OnPluginRegistryChanged(object? sender, EventArgs e) =>
        _dispatcher.TryEnqueue(RegisterPluginCommands);

    private void RegisterNavigationCommands()
    {
        foreach (var (key, target) in NavigationTargets)
        {
            if (!MainWindow.IsKnownPageKey(key))
            {
                continue;
            }

            Register(new CommandDefinition(
                $"nav.{key}",
                $"Go to {target.Name}",
                "Navigate",
                _ =>
                {
                    _window.Navigate(key);
                    return Task.CompletedTask;
                })
            {
                Description = $"Opens the {target.Name} page.",
                Keywords = target.Keywords.Split(' '),
            });
        }
    }

    private void RegisterActionCommands()
    {
        Register(new CommandDefinition(
            "shell.palette",
            "Show command palette",
            "App",
            _ =>
            {
                _window.OpenCommandPalette();
                return Task.CompletedTask;
            })
        {
            Description = "Opens the command palette.",
            Keywords = ["commands", "search", "actions", "fuzzy"],
            DefaultShortcut = KeyGesture.Parse("Ctrl+K"),
        });
        Register(new CommandDefinition(
            "shell.run",
            "Run on selected devices",
            "Run",
            _ => InvokePageAsync<MultiDevicePage>("multi-device", page => page.RunSelectedAsync()))
        {
            Description = "Builds, installs, and launches on the devices selected on the multi-device page.",
            Keywords = ["build", "deploy", "launch", "install", "start", "play"],
            DefaultShortcut = KeyGesture.Parse("F5"),
        });
        Register(new CommandDefinition(
            "shell.dryRun",
            "Run without installing (dry run)",
            "Run",
            _ => InvokePageAsync<MultiDevicePage>("multi-device", page => page.RunSelectedDryAsync()))
        {
            Description = "Validates the run plan and artifact resolution without installing.",
            Keywords = ["dry", "validate", "plan"],
        });
        Register(new CommandDefinition(
            "shell.stop",
            "Stop running sessions",
            "Run",
            _ => InvokePageAsync<MultiDevicePage>("multi-device", page => page.CancelActiveRunAsync()))
        {
            Description = "Cancels the active multi-device run group.",
            Keywords = ["cancel", "abort", "kill", "halt", "terminate"],
            DefaultShortcut = KeyGesture.Parse("Shift+F5"),
        });
        Register(new CommandDefinition(
            "screen.mirror",
            "Start mirror",
            "Screen",
            _ => InvokePageAsync<MirrorPage>("devices/mirror", page => page.StartMirrorAsync()))
        {
            Description = "Starts a scrcpy mirror for the selected device on the mirror page.",
            Keywords = ["scrcpy", "cast", "display", "control"],
            DefaultShortcut = KeyGesture.Parse("Ctrl+Shift+M"),
        });
        Register(new CommandDefinition(
            "screen.mirrorStop",
            "Stop mirror",
            "Screen",
            _ => InvokePageAsync<MirrorPage>("devices/mirror", page => page.StopMirrorAsync()))
        {
            Description = "Stops the active mirror session.",
            Keywords = ["scrcpy", "disconnect"],
        });
        Register(new CommandDefinition(
            "screen.screenshot",
            "Take screenshot",
            "Screen",
            _ => InvokePageAsync<MirrorPage>("devices/mirror", page => page.TakeScreenshotAsync()))
        {
            Description = "Captures a screenshot of the selected device.",
            Keywords = ["capture", "snap", "picture", "photo"],
            DefaultShortcut = KeyGesture.Parse("Ctrl+Shift+S"),
        });
        Register(new CommandDefinition(
            "screen.record",
            "Toggle screen recording",
            "Screen",
            _ => InvokePageAsync<MirrorPage>("devices/mirror", page => page.ToggleRecordingAsync()))
        {
            Description = "Starts or stops a screen recording of the selected device.",
            Keywords = ["video", "capture", "clip", "screen"],
            DefaultShortcut = KeyGesture.Parse("Ctrl+Shift+R"),
        });
        Register(new CommandDefinition(
            "shell.installApk",
            "Install APK",
            "Device",
            _ => _window.InstallApkAsync())
        {
            Description = "Pick one or more APK files and choose the devices to install them on.",
            Keywords = ["sideload", "package", "drop", "apk"],
        });
        Register(new CommandDefinition(
            "shell.copyDiagnostics",
            "Copy diagnostics report",
            "App",
            _ =>
            {
                _window.CopyDiagnosticsReport();
                return Task.CompletedTask;
            })
        {
            Description = "Copies a redacted diagnostics report to the clipboard.",
            Keywords = ["troubleshooting", "health", "support", "log"],
        });
        Register(new CommandDefinition(
            "shell.toggleNavPane",
            "Toggle navigation pane",
            "App",
            _ =>
            {
                _window.ToggleNavigationPane();
                return Task.CompletedTask;
            })
        {
            Description = "Collapses or expands the left navigation rail.",
            Keywords = ["sidebar", "rail", "menu", "collapse", "expand"],
            DefaultShortcut = KeyGesture.Parse("Ctrl+B"),
        });
        Register(new CommandDefinition(
            "shell.shortcuts",
            "Customize keyboard shortcuts",
            "App",
            _ =>
            {
                _window.Navigate("settings");
                return Task.CompletedTask;
            })
        {
            Description = "Opens the keyboard shortcuts section in Settings.",
            Keywords = ["keybindings", "remap", "change", "hotkeys"],
        });
    }

    private void RegisterPluginCommands()
    {
        foreach (var pluginCommandId in _pluginCommandIds)
        {
            _commands.Unregister(pluginCommandId);
        }

        _pluginCommandIds.Clear();
        foreach (var extension in _pluginHost.Registry.Commands)
        {
            var pluginCommand = extension.Value;
            var definition = new CommandDefinition(
                pluginCommand.Id,
                pluginCommand.Title,
                extension.PluginId == PluginConstants.BuiltInPluginId ? "Built-in" : "Plugins",
                pluginCommand.ExecuteAsync)
            {
                Description = pluginCommand.Description,
                Keywords = pluginCommand.Keywords,
            };
            try
            {
                _commands.Register(definition);
                _pluginCommandIds.Add(definition.Id);
            }
            catch (ArgumentException)
            {
                // A command with this id is already registered; skip the duplicate.
            }
        }
    }

    private void Register(CommandDefinition definition)
    {
        try
        {
            _commands.Register(definition);
        }
        catch (ArgumentException)
        {
            // Registration is idempotent across re-syncs; the first definition wins.
        }
    }

    private async Task InvokePageAsync<TPage>(string key, Func<TPage, Task> action)
        where TPage : Microsoft.UI.Xaml.Controls.Page
    {
        _window.Navigate(key);
        if (_window.TryGetPage(key) is not TPage page)
        {
            throw new InvalidOperationException($"The {key} page is not available.");
        }

        await action(page).ConfigureAwait(true);
    }
}

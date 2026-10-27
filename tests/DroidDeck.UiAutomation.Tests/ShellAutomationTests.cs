using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using DroidDeck.TestSupport;

namespace DroidDeck.UiAutomation.Tests;

public sealed class ShellAutomationTests
{
    [Fact]
    [Trait("Category", "UI")]
    public async Task HotpluggedFakeDeviceAppearsInTheDeviceList()
    {
        if (!UiTestsEnabled())
        {
            return;
        }

        const string serial = "FAKE-UI-01";
        using var temp = new TempDirectory("droiddeck-ui");
        var stateDirectory = temp.CreateDirectory("state");
        var dataDirectory = temp.CreateDirectory("data");
        WriteSettings(dataDirectory);
        await using var server = await FakeAdbServerProcess.StartAsync(stateDirectory, "hotplug", serial);

        var process = StartApplication(dataDirectory, stateDirectory, server.Port, "hotplug", serial);
        var processId = process.Id;
        using var application = Application.Attach(process);
        try
        {
            using var automation = new UIA3Automation();
            var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(30)) ??
                throw new InvalidOperationException("The DroidDeck main window did not appear.");

            SelectNavItem(window, "NavDevices");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("DevicesEmptyStateTitle")) is not null,
                TimeSpan.FromSeconds(15),
                "The empty device state did not appear.");

            WriteDevices(
                stateDirectory,
                $"{serial} device product:shiba model:Pixel_8 device:shiba transport_id:1");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId(serial)) is not null,
                TimeSpan.FromSeconds(20),
                "The hotplugged fake device did not appear in the device list.");

            var count = window.FindFirstDescendant(cf => cf.ByAutomationId("DeviceCount"))?.Name ?? string.Empty;
            Assert.True(
                count.Contains("1 device", StringComparison.OrdinalIgnoreCase),
                $"Expected the shell to report one device but it reported '{count}'.");

            var state = window.FindFirstDescendant(cf => cf.ByAutomationId("DeviceStateLabel"))?.Name ?? string.Empty;
            Assert.True(
                state.Contains("Ready", StringComparison.OrdinalIgnoreCase),
                $"Expected the selected device to be ready but the state label was '{state}'.");

            var handle = window.Properties.NativeWindowHandle.ValueOrDefault;
            Assert.NotEqual(IntPtr.Zero, handle);
            Assert.True(
                SetWindowPos(handle, IntPtr.Zero, 120, 140, 920, 700, SwpNoZOrder | SwpNoActivate),
                "The main window could not be moved for the placement test.");
        }
        finally
        {
            CloseApplication(application, processId);
        }

        using var settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dataDirectory, "settings.json")));
        var windowSettings = settings.RootElement.GetProperty("window");
        Assert.True(windowSettings.GetProperty("width").GetInt32() > 0);
        Assert.True(windowSettings.GetProperty("height").GetInt32() > 0);
        Assert.True(windowSettings.TryGetProperty("x", out _));
        Assert.True(windowSettings.TryGetProperty("y", out _));
        Assert.False(windowSettings.GetProperty("isMaximized").GetBoolean());
    }

    [Fact]
    [Trait("Category", "UI")]
    public async Task SettingsShowsTheToolchainDoctor()
    {
        if (!UiTestsEnabled())
        {
            return;
        }

        const string serial = "FAKE-UI-02";
        using var temp = new TempDirectory("droiddeck-ui");
        var stateDirectory = temp.CreateDirectory("state");
        var dataDirectory = temp.CreateDirectory("data");
        WriteSettings(dataDirectory);
        await using var server = await FakeAdbServerProcess.StartAsync(stateDirectory, "empty", serial);

        var process = StartApplication(dataDirectory, stateDirectory, server.Port, "empty", serial);
        var processId = process.Id;
        using var application = Application.Attach(process);
        try
        {
            using var automation = new UIA3Automation();
            var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(30)) ??
                throw new InvalidOperationException("The DroidDeck main window did not appear.");

            SelectNavItem(window, "NavSettings");

            await Wait.UntilAsync(
                () =>
                {
                    var status = window
                        .FindFirstDescendant(cf => cf.ByAutomationId("SettingsToolchainStatus"))
                        ?.Name;
                    return status?.Contains("tools available", StringComparison.OrdinalIgnoreCase) == true;
                },
                TimeSpan.FromSeconds(45),
                "The toolchain doctor did not report a completed check.");

            var texts = window.FindAllDescendants()
                .Where(static element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.Text)
                .Select(static element => element.Properties.Name.ValueOrDefault)
                .OfType<string>()
                .Where(static name => name.Length > 0)
                .ToArray();
            Assert.Contains(texts, static name => string.Equals(name, "adb", StringComparison.Ordinal));
            Assert.Contains(
                texts,
                static name => name.Contains("source: override", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                texts,
                static name => name.StartsWith("Theme changed", StringComparison.OrdinalIgnoreCase));

            var adbPath = window.FindFirstDescendant(cf => cf.ByAutomationId("AdbPathBox"))?.AsTextBox()
                ?? throw new InvalidOperationException("The adb override field was not found.");
            Assert.Equal(TestBinaries.FakeAdbExecutable, adbPath.Text, ignoreCase: true);
            Assert.True(
                window.FindFirstDescendant(cf => cf.ByAutomationId("UnityPathBox")) is not null,
                "The Unity override field was not found.");

            const string sdkPath = @"C:\DroidDeck Test SDK";
            var sdkBox = window.FindFirstDescendant(cf => cf.ByAutomationId("AndroidSdkPathBox"))?.AsTextBox()
                ?? throw new InvalidOperationException("The Android SDK override field was not found.");
            sdkBox.Text = sdkPath;
            var saveButton = window.FindFirstDescendant(cf => cf.ByAutomationId("SaveAndroidSdkPathButton"))?.AsButton()
                ?? throw new InvalidOperationException("The Android SDK save button was not found.");
            saveButton.Invoke();

            var settingsPath = Path.Combine(dataDirectory, "settings.json");
            await Wait.UntilAsync(
                () => string.Equals(
                    ReadSavedToolPath(settingsPath, "androidSdkPath"),
                    sdkPath,
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(10),
                "The Android SDK path override was not saved from settings.");

            await Wait.UntilAsync(
                () => string.Equals(
                    window.FindFirstDescendant(cf => cf.ByAutomationId("AndroidSdkPathSource"))?.Name,
                    "Source: Global",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(5),
                "The Android SDK source hint did not update after saving.");
        }
        finally
        {
            CloseApplication(application, processId);
        }
    }

    [Fact]
    [Trait("Category", "UI")]
    public async Task ExtensionsListsTheBuiltInCommand()
    {
        if (!UiTestsEnabled())
        {
            return;
        }

        const string serial = "FAKE-UI-03";
        using var temp = new TempDirectory("droiddeck-ui");
        var stateDirectory = temp.CreateDirectory("state");
        var dataDirectory = temp.CreateDirectory("data");
        WriteSettings(dataDirectory);
        await using var server = await FakeAdbServerProcess.StartAsync(stateDirectory, "empty", serial);

        var process = StartApplication(dataDirectory, stateDirectory, server.Port, "empty", serial);
        var processId = process.Id;
        using var application = Application.Attach(process);
        try
        {
            using var automation = new UIA3Automation();
            var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(30)) ??
                throw new InvalidOperationException("The DroidDeck main window did not appear.");

            SelectNavItem(window, "NavExtensions");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId(
                    "RunCommand_droiddeck.builtin.openDataDirectory")) is not null,
                TimeSpan.FromSeconds(15),
                "The built-in command did not appear in the extensions list.");

            var texts = window.FindAllDescendants()
                .Where(static element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.Text)
                .Select(static element => element.Properties.Name.ValueOrDefault)
                .OfType<string>()
                .Where(static name => name.Length > 0)
                .ToArray();
            Assert.Contains(texts, static name => string.Equals(name, "Open data folder", StringComparison.Ordinal));
            Assert.Contains(
                texts,
                static name => name.Contains("droiddeck.builtin.openDataDirectory", StringComparison.Ordinal));
        }
        finally
        {
            CloseApplication(application, processId);
        }
    }

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

#pragma warning disable SYSLIB1054
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
#pragma warning restore SYSLIB1054

    private static void SelectNavItem(Window window, string automationId)
    {
        var item = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        Assert.NotNull(item);
        var selection = item.Patterns.SelectionItem.PatternOrDefault;
        if (selection is not null)
        {
            selection.Select();
            return;
        }

        item.Click();
    }

    private static bool UiTestsEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable("DROIDDECK_UI_TESTS"),
            "1",
            StringComparison.Ordinal);

    private static Process StartApplication(
        string dataDirectory,
        string stateDirectory,
        int adbPort,
        string scenario,
        string serial)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = TestBinaries.AppExecutable,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(TestBinaries.AppExecutable)!,
        };
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--no-plugins");
        startInfo.Environment["ANDROID_ADB_SERVER_PORT"] =
            adbPort.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment["FAKEADB_STATE_DIR"] = stateDirectory;
        startInfo.Environment["FAKEADB_SCENARIO"] = scenario;
        startInfo.Environment["FAKEADB_SERIAL"] = serial;

        return Process.Start(startInfo) ??
            throw new InvalidOperationException("DroidDeck could not be started.");
    }

    private static void WriteSettings(string dataDirectory)
    {
        var adbPath = TestBinaries.FakeAdbExecutable.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(dataDirectory, "settings.json"),
            $$"""
            {
              "schemaVersion": 1,
              "tools": {
                "adbPath": "{{adbPath}}"
              }
            }
            """);
    }

    private static string? ReadSavedToolPath(string settingsPath, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.GetProperty("tools").TryGetProperty(propertyName, out var value)
                ? value.GetString()
                : null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidOperationException
                or KeyNotFoundException)
        {
            return null;
        }
    }

    private static void WriteDevices(string stateDirectory, params string[] lines)
    {
        var path = Path.Combine(stateDirectory, "devices.txt");
        var temporary = path + ".tmp";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, overwrite: true);
    }

    private static void CloseApplication(Application application, int processId)
    {
        try
        {
            application.Close();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or COMException or Win32Exception)
        {
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
        }
    }
}

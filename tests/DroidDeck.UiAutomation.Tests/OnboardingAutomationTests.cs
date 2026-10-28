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

public sealed class OnboardingAutomationTests
{
    [Fact]
    [Trait("Category", "UI")]
    public async Task FreshProfileCompletesOnboardingIntoAUsableHome()
    {
        if (!UiTestsEnabled())
        {
            return;
        }

        const string serial = "FAKE-UI-05";
        using var temp = new TempDirectory("droiddeck-ui");
        var stateDirectory = temp.CreateDirectory("state");
        var dataDirectory = temp.CreateDirectory("data");
        WriteSettings(dataDirectory, completed: false);
        await using var server = await FakeAdbServerProcess.StartAsync(stateDirectory, "empty", serial);

        var process = StartApplication(dataDirectory, stateDirectory, server.Port, "empty", serial);
        var processId = process.Id;
        using var application = Application.Attach(process);
        try
        {
            using var automation = new UIA3Automation();
            var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(30)) ??
                throw new InvalidOperationException("The DroidDeck main window did not appear.");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingProgressText"))?.Name?
                    .Contains("Step 1 of 3", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(15),
                "A fresh profile did not open on the onboarding tools step.");

            Assert.NotNull(
                window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingOpenToolSettingsButton")));

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingToolchainStatus"))?.Name?
                    .Contains("tools available", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(60),
                "The onboarding tools step never reported a doctor result.");

            InvokeButton(window, "OnboardingNextButton");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingProgressText"))?.Name?
                    .Contains("Step 2 of 3", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(10),
                "The onboarding project step did not appear after Next.");

            Assert.Contains(
                "No projects added yet",
                window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingProjectsSummary"))?.Name
                    ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            InvokeButton(window, "OnboardingNextButton");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingProgressText"))?.Name?
                    .Contains("Step 3 of 3", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(10),
                "The onboarding device step did not appear after Next.");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingDeviceStatus"))?.Name?
                    .Contains("No devices detected yet", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(15),
                "The onboarding device step did not report the empty device state.");

            Assert.True(
                window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingDeviceGuidance")) is not null,
                "The onboarding device guidance was not rendered.");

            InvokeButton(window, "OnboardingNextButton");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("HomeQuickRunEmptyText")) is not null,
                TimeSpan.FromSeconds(30),
                "Finishing onboarding did not land on a usable Home.");
        }
        finally
        {
            CloseApplication(application, processId);
        }

        using var settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dataDirectory, "settings.json")));
        Assert.True(
            settings.RootElement.GetProperty("onboarding").GetProperty("completed").GetBoolean(),
            "Completing onboarding did not persist the completed flag.");
    }

    [Fact]
    [Trait("Category", "UI")]
    public async Task CompletedOnboardingIsNotShownAgain()
    {
        if (!UiTestsEnabled())
        {
            return;
        }

        const string serial = "FAKE-UI-06";
        using var temp = new TempDirectory("droiddeck-ui");
        var stateDirectory = temp.CreateDirectory("state");
        var dataDirectory = temp.CreateDirectory("data");
        WriteSettings(dataDirectory, completed: true);
        await using var server = await FakeAdbServerProcess.StartAsync(stateDirectory, "empty", serial);

        var process = StartApplication(dataDirectory, stateDirectory, server.Port, "empty", serial);
        var processId = process.Id;
        using var application = Application.Attach(process);
        try
        {
            using var automation = new UIA3Automation();
            var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(30)) ??
                throw new InvalidOperationException("The DroidDeck main window did not appear.");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("HomeQuickRunEmptyText")) is not null,
                TimeSpan.FromSeconds(30),
                "A completed profile did not land directly on Home.");

            Assert.Null(
                window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingProgressText")));
        }
        finally
        {
            CloseApplication(application, processId);
        }
    }

    [Fact]
    [Trait("Category", "UI")]
    public async Task OnboardingResumesAtTheSavedStepAndGuidesUnauthorizedDevices()
    {
        if (!UiTestsEnabled())
        {
            return;
        }

        const string serial = "FAKE-UI-07";
        using var temp = new TempDirectory("droiddeck-ui");
        var stateDirectory = temp.CreateDirectory("state");
        var dataDirectory = temp.CreateDirectory("data");
        WriteSettings(dataDirectory, completed: false, step: "device");
        await using var server = await FakeAdbServerProcess.StartAsync(stateDirectory, "unauthorized", serial);

        var process = StartApplication(dataDirectory, stateDirectory, server.Port, "unauthorized", serial);
        var processId = process.Id;
        using var application = Application.Attach(process);
        try
        {
            using var automation = new UIA3Automation();
            var window = application.GetMainWindow(automation, TimeSpan.FromSeconds(30)) ??
                throw new InvalidOperationException("The DroidDeck main window did not appear.");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingProgressText"))?.Name?
                    .Contains("Step 3 of 3", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(15),
                "Onboarding did not resume at the saved device step.");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("OnboardingDeviceGuidance"))?.Name?
                    .Contains("Allow USB debugging", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(15),
                "The unauthorized device did not show authorization guidance.");

            var texts = window.FindAllDescendants()
                .Where(static element =>
                    element.Properties.ControlType.ValueOrDefault == ControlType.Text)
                .Select(static element => element.Properties.Name.ValueOrDefault)
                .OfType<string>()
                .ToArray();
            Assert.Contains(
                texts,
                static name => name.Contains("USB debugging", StringComparison.OrdinalIgnoreCase));

            InvokeButton(window, "OnboardingSkipStepButton");

            await Wait.UntilAsync(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("HomeQuickRunEmptyText")) is not null,
                TimeSpan.FromSeconds(30),
                "Skipping the last onboarding step did not land on a usable Home.");
        }
        finally
        {
            CloseApplication(application, processId);
        }

        using var settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dataDirectory, "settings.json")));
        Assert.True(
            settings.RootElement.GetProperty("onboarding").GetProperty("completed").GetBoolean(),
            "Skipping onboarding did not persist the completed flag.");
    }

    private static void InvokeButton(Window window, string automationId)
    {
        var button = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.AsButton() ??
            throw new InvalidOperationException($"The {automationId} button was not found.");
        button.Invoke();
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

    private static void WriteSettings(string dataDirectory, bool completed, string? step = null)
    {
        var adbPath = TestBinaries.FakeAdbExecutable.Replace("\\", "\\\\", StringComparison.Ordinal);
        var onboarding = step is null
            ? $"{{ \"completed\": {(completed ? "true" : "false")} }}"
            : $"{{ \"completed\": {(completed ? "true" : "false")}, \"step\": \"{step}\" }}";
        File.WriteAllText(
            Path.Combine(dataDirectory, "settings.json"),
            $$"""
            {
              "schemaVersion": 1,
              "tools": {
                "adbPath": "{{adbPath}}"
              },
              "onboarding": {{onboarding}}
            }
            """);
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

using DroidDeck.Core.Processes;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins.BuiltIns;

/// <summary>
/// Waits until adb reports the target device as ready. Built-in run steps use
/// the same contract as plugin-contributed steps.
/// </summary>
public sealed class WaitForDeviceRunStep(IProcessRunner runner, Func<string> adbPathProvider) : IRunStep
{
    /// <summary>The stable step identifier.</summary>
    public const string StepId = "droiddeck.builtin.runStep.waitForDevice";

    /// <inheritdoc />
    public string Id => StepId;

    /// <inheritdoc />
    public async Task<RunStepResult> ExecuteAsync(RunStepContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.DeviceSerial))
        {
            return new RunStepResult(false, "A device is required to wait for the device.");
        }

        var result = await runner.RunAsync(
            new ProcessRequest
            {
                FileName = adbPathProvider(),
                Arguments = ["-s", context.DeviceSerial, "wait-for-device"],
                Timeout = TimeSpan.FromMinutes(2),
            },
            null,
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return new RunStepResult(true, $"Device {context.DeviceSerial} is ready.");
        }

        if (result.TimedOut)
        {
            return new RunStepResult(
                false,
                $"Device {context.DeviceSerial} did not become ready within the timeout.");
        }

        return new RunStepResult(
            false,
            result.StartError ?? $"adb wait-for-device exited with code {result.ExitCode}.");
    }
}

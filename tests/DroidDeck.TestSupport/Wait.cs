namespace DroidDeck.TestSupport;

/// <summary>Deterministic polling helpers for asynchronous assertions.</summary>
public static class Wait
{
    public static async Task UntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null,
        string? message = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException(message ?? "The condition was not met before the timeout.");
    }

    public static async Task UntilAsync(
        Func<Task<bool>> condition,
        TimeSpan? timeout = null,
        string? message = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException(message ?? "The condition was not met before the timeout.");
    }

    public static async Task ForProcessExitAsync(int processId, TimeSpan? timeout = null)
    {
        await UntilAsync(
            () =>
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(processId);
                    return process.HasExited;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            },
            timeout,
            $"Process {processId} was still running.");
    }
}

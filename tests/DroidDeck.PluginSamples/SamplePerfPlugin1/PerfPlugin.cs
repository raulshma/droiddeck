using DroidDeck.Plugin.Abstractions;

namespace SamplePerfPlugin1;

/// <summary>
/// A minimal plugin used by the five-plugin startup budget test.
/// </summary>
public sealed class PerfPlugin : IPlugin
{
    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

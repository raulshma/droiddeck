namespace DroidDeck.Core.Screen;

/// <summary>One thumbnail frame captured from a device.</summary>
public sealed record ScreenThumbnail
{
    /// <summary>Gets the device serial.</summary>
    public required string Serial { get; init; }

    /// <summary>Gets the PNG bytes.</summary>
    public required byte[] Png { get; init; }

    /// <summary>Gets when the frame was captured.</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Gets the exact command line used to capture the frame.</summary>
    public string CommandLine { get; init; } = string.Empty;
}

/// <summary>Captures throttled device thumbnails without competing with mirroring.</summary>
public interface IThumbnailService
{
    /// <summary>Captures one frame on demand.</summary>
    Task<ScreenThumbnail> CaptureAsync(string serial, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a capture loop at the requested rate. Devices with an active mirror are
    /// skipped so the thumbnail loop cannot steal bandwidth from the interactive stream.
    /// </summary>
    Task RunAsync(
        IReadOnlyCollection<string> serials,
        TimeSpan interval,
        Func<ScreenThumbnail, Task> onThumbnail,
        CancellationToken cancellationToken = default);
}

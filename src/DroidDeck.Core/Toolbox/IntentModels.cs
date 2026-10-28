namespace DroidDeck.Core.Toolbox;

/// <summary>The type of an intent extra.</summary>
public enum IntentExtraType
{
    /// <summary>A string extra, sent with <c>--es</c>.</summary>
    Text,

    /// <summary>An integer extra, sent with <c>--ei</c>.</summary>
    WholeNumber,

    /// <summary>A long extra, sent with <c>--el</c>.</summary>
    BigNumber,

    /// <summary>A boolean extra, sent with <c>--ez</c>.</summary>
    Boolean,

    /// <summary>A float extra, sent with <c>--ef</c>.</summary>
    Fraction,
}

/// <summary>One typed extra attached to an intent or broadcast.</summary>
/// <param name="Key">The extra key.</param>
/// <param name="Type">The extra type.</param>
/// <param name="Value">The value in invariant string form.</param>
public sealed record IntentExtra(string Key, IntentExtraType Type, string Value);

/// <summary>An intent to start on a device.</summary>
public sealed record DeviceIntentRequest
{
    /// <summary>Gets the intent action. Defaults to <c>android.intent.action.VIEW</c> when data is provided.</summary>
    public string? Action { get; init; }

    /// <summary>Gets the data URI.</summary>
    public string? Data { get; init; }

    /// <summary>Gets the MIME type.</summary>
    public string? MimeType { get; init; }

    /// <summary>Gets the explicit target package.</summary>
    public string? Package { get; init; }

    /// <summary>Gets the explicit component in <c>package/activity</c> form.</summary>
    public string? Component { get; init; }

    /// <summary>Gets intent categories.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Gets typed extras.</summary>
    public IReadOnlyList<IntentExtra> Extras { get; init; } = [];

    /// <summary>Gets numeric intent flags.</summary>
    public int? Flags { get; init; }

    /// <summary>Gets whether the start should wait for the launch result.</summary>
    public bool WaitForResult { get; init; } = true;
}

/// <summary>A broadcast to send to a device.</summary>
public sealed record DeviceBroadcastRequest
{
    /// <summary>Gets the broadcast action.</summary>
    public required string Action { get; init; }

    /// <summary>Gets an optional explicit component.</summary>
    public string? Component { get; init; }

    /// <summary>Gets an optional target package.</summary>
    public string? Package { get; init; }

    /// <summary>Gets typed extras.</summary>
    public IReadOnlyList<IntentExtra> Extras { get; init; } = [];

    /// <summary>Gets whether the broadcast targets a background receiver.</summary>
    public bool IncludeStoppedPackages { get; init; } = true;
}

/// <summary>A reboot target mode.</summary>
public enum RebootMode
{
    /// <summary>Reboot to the normal system.</summary>
    System,

    /// <summary>Reboot to recovery.</summary>
    Recovery,

    /// <summary>Reboot to the bootloader.</summary>
    Bootloader,
}

/// <summary>The device tool entry points exposed by the toolbox.</summary>
public enum DeviceToolKind
{
    /// <summary>The performance and frame metrics tool.</summary>
    Performance,

    /// <summary>The UI hierarchy inspector.</summary>
    Inspector,
}

/// <summary>A navigation entry that opens a host tool for a device.</summary>
/// <param name="Kind">The tool kind.</param>
/// <param name="Title">The display title.</param>
/// <param name="Description">A short description.</param>
/// <param name="NavigationKey">The host navigation key that opens the tool.</param>
public sealed record DeviceToolEntry(
    DeviceToolKind Kind,
    string Title,
    string Description,
    string NavigationKey);

/// <summary>Sends intents and broadcasts, reboots devices, and exposes tool entry points.</summary>
public interface IDeviceIntentService
{
    /// <summary>Gets the performance and inspector navigation entries.</summary>
    IReadOnlyList<DeviceToolEntry> ToolEntries { get; }

    /// <summary>Starts an intent and returns the launch output.</summary>
    Task<ToolboxCommandResult> SendIntentAsync(
        string serial,
        DeviceIntentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a broadcast and returns the receiver output.</summary>
    Task<ToolboxCommandResult> SendBroadcastAsync(
        string serial,
        DeviceBroadcastRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reboots a device. The target mode must be named in the confirmation and
    /// <paramref name="confirmed"/> must be <see langword="true"/>.
    /// </summary>
    Task<ToolboxCommandResult> RebootAsync(
        string serial,
        RebootMode mode,
        bool confirmed,
        CancellationToken cancellationToken = default);
}

namespace DroidDeck.Core.Configuration;

/// <summary>The specificity of a device override.</summary>
public enum DeviceOverrideKind
{
    /// <summary>The override applies to every device.</summary>
    All,

    /// <summary>The override applies to a named device group.</summary>
    Group,

    /// <summary>The override applies to one device serial.</summary>
    Serial,
}

/// <summary>Which devices a device override applies to.</summary>
public abstract record DeviceOverrideTarget
{
    private DeviceOverrideTarget()
    {
    }

    /// <summary>Gets the override that applies to every device.</summary>
    public static DeviceOverrideTarget AllDevices { get; } = new AllTarget();

    /// <summary>Creates an override for a named device group.</summary>
    public static DeviceOverrideTarget Group(string name) => new GroupTarget(name);

    /// <summary>Creates an override for a single device serial.</summary>
    public static DeviceOverrideTarget Serial(string serial) => new SerialTarget(serial);

    /// <summary>Gets the specificity of the target.</summary>
    public abstract DeviceOverrideKind Kind { get; }

    /// <summary>Gets the group name or device serial; empty for all devices.</summary>
    public abstract string Key { get; }

    /// <summary>Gets the stable origin label used in the source map.</summary>
    public abstract string Origin { get; }

    private sealed record AllTarget : DeviceOverrideTarget
    {
        public override DeviceOverrideKind Kind => DeviceOverrideKind.All;

        public override string Key => string.Empty;

        public override string Origin => "all-devices";
    }

    private sealed record GroupTarget(string Name) : DeviceOverrideTarget
    {
        public override DeviceOverrideKind Kind => DeviceOverrideKind.Group;

        public override string Key => Name;

        public override string Origin => "group:" + Name;
    }

    private sealed record SerialTarget(string DeviceSerial) : DeviceOverrideTarget
    {
        public override DeviceOverrideKind Kind => DeviceOverrideKind.Serial;

        public override string Key => DeviceSerial;

        public override string Origin => "serial:" + DeviceSerial;
    }
}

/// <summary>
/// The project context that activates the team-shared and local project layers.
/// </summary>
/// <param name="RepositoryRoot">
/// The repository holding <c>.droiddeck/project.json</c>, or <see langword="null"/>
/// when no repository is open.
/// </param>
/// <param name="LocalProjectId">The identifier of the machine-local project overrides, or <see langword="null"/>.</param>
public sealed record ProjectContext(string? RepositoryRoot, string? LocalProjectId);

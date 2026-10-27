using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins;

/// <summary>
/// The host-wide extension registry. Built-ins register directly; each plugin
/// receives a scope that attributes its contributions to its plugin id.
/// </summary>
public sealed class PluginRegistry : IPluginRegistry
{
    private readonly List<PluginExtension<ICommand>> _commands = [];
    private readonly List<PluginExtension<IProjectDetector>> _projectDetectors = [];
    private readonly List<PluginExtension<IDeviceAction>> _deviceActions = [];
    private readonly List<PluginExtension<IRunStep>> _runSteps = [];
    private readonly Dictionary<Type, string> _owners = [];
    private readonly object _sync = new();

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<ICommand>> Commands => Snapshot(_commands);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IProjectDetector>> ProjectDetectors => Snapshot(_projectDetectors);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IDeviceAction>> DeviceActions => Snapshot(_deviceActions);

    /// <inheritdoc />
    public IReadOnlyList<PluginExtension<IRunStep>> RunSteps => Snapshot(_runSteps);

    /// <summary>Creates a registry view that attributes additions to a plugin.</summary>
    public IPluginRegistry CreateScope(string pluginId)
    {
        ValidatePluginId(pluginId);
        return new ScopedPluginRegistry(this, pluginId);
    }

    /// <inheritdoc />
    public void AddCommand(ICommand command) => AddCommand(PluginConstants.BuiltInPluginId, command);

    /// <inheritdoc />
    public void AddProjectDetector(IProjectDetector detector)
        => AddProjectDetector(PluginConstants.BuiltInPluginId, detector);

    /// <inheritdoc />
    public void AddDeviceAction(IDeviceAction action)
        => AddDeviceAction(PluginConstants.BuiltInPluginId, action);

    /// <inheritdoc />
    public void AddRunStep(IRunStep step) => AddRunStep(PluginConstants.BuiltInPluginId, step);

    /// <inheritdoc />
    public string? GetOwner(Type extensionType)
    {
        ArgumentNullException.ThrowIfNull(extensionType);
        lock (_sync)
        {
            return _owners.TryGetValue(extensionType, out var owner) ? owner : null;
        }
    }

    /// <inheritdoc />
    public int RemoveAll(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        int removed;
        lock (_sync)
        {
            removed = _commands.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _projectDetectors.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _deviceActions.RemoveAll(extension => extension.PluginId == pluginId);
            removed += _runSteps.RemoveAll(extension => extension.PluginId == pluginId);
            RebuildOwners();
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    internal void AddCommand(string pluginId, ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Add(pluginId, command, _commands);
    }

    internal void AddProjectDetector(string pluginId, IProjectDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);
        Add(pluginId, detector, _projectDetectors);
    }

    internal void AddDeviceAction(string pluginId, IDeviceAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Add(pluginId, action, _deviceActions);
    }

    internal void AddRunStep(string pluginId, IRunStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        Add(pluginId, step, _runSteps);
    }

    private void Add<T>(string pluginId, T value, List<PluginExtension<T>> target)
        where T : class
    {
        ValidatePluginId(pluginId);
        lock (_sync)
        {
            target.Add(new PluginExtension<T>(pluginId, value));
            _owners[value.GetType()] = pluginId;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildOwners()
    {
        _owners.Clear();
        foreach (var extension in _commands)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _projectDetectors)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _deviceActions)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }

        foreach (var extension in _runSteps)
        {
            _owners[extension.Value.GetType()] = extension.PluginId;
        }
    }

    private PluginExtension<T>[] Snapshot<T>(List<PluginExtension<T>> source)
        where T : class
    {
        lock (_sync)
        {
            return source.ToArray();
        }
    }

    private static void ValidatePluginId(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
    }

    private sealed class ScopedPluginRegistry(PluginRegistry root, string pluginId) : IPluginRegistry
    {
        public IReadOnlyList<PluginExtension<ICommand>> Commands => root.Commands;

        public IReadOnlyList<PluginExtension<IProjectDetector>> ProjectDetectors => root.ProjectDetectors;

        public IReadOnlyList<PluginExtension<IDeviceAction>> DeviceActions => root.DeviceActions;

        public IReadOnlyList<PluginExtension<IRunStep>> RunSteps => root.RunSteps;

        public event EventHandler? Changed
        {
            add => root.Changed += value;
            remove => root.Changed -= value;
        }

        public void AddCommand(ICommand command) => root.AddCommand(pluginId, command);

        public void AddProjectDetector(IProjectDetector detector) => root.AddProjectDetector(pluginId, detector);

        public void AddDeviceAction(IDeviceAction action) => root.AddDeviceAction(pluginId, action);

        public void AddRunStep(IRunStep step) => root.AddRunStep(pluginId, step);

        public string? GetOwner(Type extensionType) => root.GetOwner(extensionType);

        public int RemoveAll(string targetPluginId) => root.RemoveAll(targetPluginId);
    }
}

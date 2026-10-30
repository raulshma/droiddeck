namespace DroidDeck.Plugin.Abstractions.Rendering;

/// <summary>
/// Tracks the renderers available for each kind, the per-kind preference, and
/// repeated-failure state. Selection is UI-free so plugins and tests can reason
/// about renderer choice without loading WinUI.
/// </summary>
public sealed class RendererCatalog
{
    /// <summary>The number of consecutive failures that disables a renderer.</summary>
    public const int MaxConsecutiveFailures = 3;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<RendererKind, string> _defaults = [];
    private readonly object _gate = new();

    /// <summary>Raised after a renderer, its preference, or its health changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets every registered renderer and its current state.</summary>
    public IReadOnlyList<RendererStatus> Statuses
    {
        get
        {
            lock (_gate)
            {
                return _entries.Values.Select(static entry => entry.ToStatus()).ToArray();
            }
        }
    }

    /// <summary>Registers or refreshes a renderer, preserving its failure state.</summary>
    public void Register(RendererDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_gate)
        {
            if (_entries.TryGetValue(descriptor.Id, out var existing))
            {
                existing.Descriptor = descriptor;
            }
            else
            {
                _entries[descriptor.Id] = new Entry(descriptor);
            }
        }

        RaiseChanged();
    }

    /// <summary>Removes every plugin-contributed renderer. Built-ins are never removed.</summary>
    /// <returns>The number of removed renderers.</returns>
    public int RemoveAll(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        int removed;
        lock (_gate)
        {
            var ids = _entries.Values
                .Where(entry => !entry.Descriptor.IsBuiltIn &&
                                string.Equals(entry.Descriptor.PluginId, pluginId, StringComparison.Ordinal))
                .Select(entry => entry.Descriptor.Id)
                .ToArray();
            foreach (var id in ids)
            {
                _entries.Remove(id);
                foreach (var pair in _defaults.Where(pair => string.Equals(pair.Value, id, StringComparison.Ordinal)).ToArray())
                {
                    _defaults.Remove(pair.Key);
                }
            }

            removed = ids.Length;
        }

        if (removed > 0)
        {
            RaiseChanged();
        }

        return removed;
    }

    /// <summary>Removes one plugin-contributed renderer. Built-ins are never removed.</summary>
    /// <returns><see langword="true"/> when the renderer was removed.</returns>
    public bool Remove(string rendererId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rendererId);

        bool removed;
        lock (_gate)
        {
            if (!_entries.TryGetValue(rendererId, out var entry) || entry.Descriptor.IsBuiltIn)
            {
                return false;
            }

            removed = _entries.Remove(rendererId);
            foreach (var pair in _defaults.Where(pair => string.Equals(pair.Value, rendererId, StringComparison.Ordinal)).ToArray())
            {
                _defaults.Remove(pair.Key);
            }
        }

        if (removed)
        {
            RaiseChanged();
        }

        return removed;
    }

    /// <summary>Finds a renderer by identifier.</summary>
    public RendererStatus? Find(string rendererId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rendererId);
        lock (_gate)
        {
            return _entries.TryGetValue(rendererId, out var entry) ? entry.ToStatus() : null;
        }
    }

    /// <summary>Gets the remembered renderer identifier for a kind, if any.</summary>
    public string? GetDefault(RendererKind kind)
    {
        lock (_gate)
        {
            return _defaults.TryGetValue(kind, out var id) ? id : null;
        }
    }

    /// <summary>Remembers a renderer for a kind, or clears the preference when empty.</summary>
    public void SetDefault(RendererKind kind, string? rendererId)
    {
        var changed = false;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(rendererId))
            {
                changed = _defaults.Remove(kind);
            }
            else if (_entries.TryGetValue(rendererId, out var entry) && entry.Descriptor.Kind != kind)
            {
                throw new ArgumentException(
                    $"Renderer '{rendererId}' renders {entry.Descriptor.Kind}, not {kind}.",
                    nameof(rendererId));
            }
            else if (!_defaults.TryGetValue(kind, out var current) ||
                     !string.Equals(current, rendererId, StringComparison.Ordinal))
            {
                _defaults[kind] = rendererId;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>Enables or disables a renderer, resetting its failure state when enabled.</summary>
    public void SetEnabled(string rendererId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rendererId);
        var changed = false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(rendererId, out var entry) || entry.Enabled == enabled)
            {
                return;
            }

            entry.Enabled = enabled;
            if (enabled)
            {
                entry.FailureCount = 0;
                entry.Health = RendererHealth.Unknown;
                entry.DisabledReason = null;
            }

            changed = true;
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>
    /// Selects the best available renderer for a kind. An explicit item override,
    /// then the remembered kind default, then the highest priority candidate wins.
    /// </summary>
    /// <param name="kind">The surface being rendered.</param>
    /// <param name="preferredId">A one-off View as choice for this item.</param>
    /// <param name="canRender">A predicate that rejects renderers that do not fit the data.</param>
    public RendererStatus? Select(
        RendererKind kind,
        string? preferredId = null,
        Func<RendererDescriptor, bool>? canRender = null)
    {
        var candidates = Snapshot(kind);
        foreach (var id in new[] { preferredId, GetDefault(kind) })
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var match = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Renderer.Id, id, StringComparison.Ordinal));
            if (match is not null && Passes(match, canRender))
            {
                return match;
            }
        }

        return candidates
            .Where(candidate => Passes(candidate, canRender))
            .OrderByDescending(candidate => candidate.Renderer.Priority)
            .ThenBy(candidate => candidate.Renderer.IsBuiltIn)
            .ThenBy(candidate => candidate.Renderer.DisplayName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Selects the best available built-in renderer, used after a renderer fails.</summary>
    /// <param name="kind">The surface being rendered.</param>
    /// <param name="canRender">A predicate that rejects renderers that do not fit the data.</param>
    /// <param name="excludeId">A renderer that already failed and must not be retried.</param>
    public RendererStatus? SelectBuiltIn(
        RendererKind kind,
        Func<RendererDescriptor, bool>? canRender = null,
        string? excludeId = null)
    {
        return Snapshot(kind)
            .Where(candidate => candidate.Renderer.IsBuiltIn)
            .Where(candidate => excludeId is null ||
                                !string.Equals(candidate.Renderer.Id, excludeId, StringComparison.Ordinal))
            .Where(candidate => Passes(candidate, canRender))
            .OrderByDescending(candidate => candidate.Renderer.Priority)
            .ThenBy(candidate => candidate.Renderer.DisplayName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Records a successful render and clears the failure streak.</summary>
    public void ReportSuccess(string rendererId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rendererId);
        var changed = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(rendererId, out var entry) &&
                (entry.FailureCount > 0 || entry.Health != RendererHealth.Healthy))
            {
                entry.FailureCount = 0;
                entry.Health = RendererHealth.Healthy;
                entry.DisabledReason = null;
                changed = true;
            }
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    /// <summary>Records a failed render and disables the renderer after repeated failures.</summary>
    /// <returns><see langword="true"/> when this failure disabled the renderer.</returns>
    public bool ReportFailure(string rendererId, string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rendererId);
        bool disabled;
        lock (_gate)
        {
            if (!_entries.TryGetValue(rendererId, out var entry))
            {
                return false;
            }

            entry.FailureCount++;
            disabled = entry.FailureCount >= MaxConsecutiveFailures;
            if (disabled)
            {
                entry.Enabled = false;
                entry.Health = RendererHealth.Disabled;
                entry.DisabledReason =
                    $"Disabled after {entry.FailureCount} consecutive failures. " +
                    $"Last error: {error ?? "unknown"}.";
            }
            else
            {
                entry.Health = RendererHealth.Failing;
                entry.DisabledReason = null;
            }
        }

        RaiseChanged();
        return disabled;
    }

    private RendererStatus[] Snapshot(RendererKind kind)
    {
        lock (_gate)
        {
            return _entries.Values
                .Where(entry => entry.Descriptor.Kind == kind && entry.ToStatus().IsAvailable)
                .Select(static entry => entry.ToStatus())
                .ToArray();
        }
    }

    private static bool Passes(RendererStatus candidate, Func<RendererDescriptor, bool>? canRender)
    {
        if (canRender is null)
        {
            return true;
        }

        try
        {
            return canRender(candidate.Renderer);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Entry(RendererDescriptor descriptor)
    {
        public RendererDescriptor Descriptor { get; set; } = descriptor;

        public bool Enabled { get; set; } = true;

        public RendererHealth Health { get; set; } = RendererHealth.Unknown;

        public int FailureCount { get; set; }

        public string? DisabledReason { get; set; }

        public RendererStatus ToStatus() => new(Descriptor)
        {
            Enabled = Enabled,
            Health = Health,
            FailureCount = FailureCount,
            DisabledReason = DisabledReason,
        };
    }
}

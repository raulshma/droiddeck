using System.Globalization;
using System.Text;
using DroidDeck.Core.Configuration;
using DroidDeck.Core.Devices;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Core.Toolchain;

namespace DroidDeck.Core.Diagnostics;

/// <summary>
/// Produces a local, redacted snapshot of application health: versions, paths,
/// toolchain state, settings sources, devices, and recent errors.
/// </summary>
public sealed class DiagnosticsService : IDiagnosticsService
{
    private const int MaxSettingEntries = 200;
    private readonly AppPaths _paths;
    private readonly IConfigurationService _configuration;
    private readonly IToolchainService _toolchain;
    private readonly IDeviceService _devices;
    private readonly BoundedLogBuffer _log;
    private readonly SessionContext _session;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a diagnostics service.</summary>
    public DiagnosticsService(
        AppPaths paths,
        IConfigurationService configuration,
        IToolchainService toolchain,
        IDeviceService devices,
        BoundedLogBuffer log,
        SessionContext session,
        TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string DiagnosticsId => _session.DiagnosticsId;

    /// <inheritdoc />
    public string SessionId => _session.SessionId;

    /// <inheritdoc />
    public string BuildReport(DiagnosticsReportOptions? options = null)
    {
        options ??= new DiagnosticsReportOptions();
        var now = _timeProvider.GetUtcNow();
        var builder = new StringBuilder();

        builder.AppendLine("DroidDeck diagnostics report");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Diagnostics ID: {DiagnosticsId}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Session ID: {SessionId}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Generated: {now:yyyy-MM-ddTHH:mm:ss.fffZ}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Quote diagnostics ID {DiagnosticsId} when reporting a problem.");
        builder.AppendLine();

        AppendVersions(builder);
        if (options.IncludePaths)
        {
            AppendPaths(builder);
        }

        AppendToolchain(builder);
        AppendSettings(builder);
        AppendDevices(builder);

        foreach (var section in options.ExtraSections)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"## {section.Title}");
            builder.AppendLine(Redactor.Redact(section.Body));
            builder.AppendLine();
        }

        if (options.IncludeRecentErrors)
        {
            AppendRecentErrors(builder, Math.Max(1, options.RecentErrorCount));
        }

        if (options.IncludeRecentLogs)
        {
            AppendRecentLogs(builder, Math.Max(1, options.RecentLogCount));
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public async Task<string> SaveReportAsync(
        DiagnosticsReportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var report = BuildReport(options);
        _paths.EnsureCreated();
        var stamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var path = Path.Combine(_paths.DiagnosticsDirectory, $"diagnostics-{stamp}-{SessionId}.txt");
        await AtomicFile.WriteAllTextAsync(path, report, keepBackup: false, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static void AppendVersions(StringBuilder builder)
    {
        builder.AppendLine("## Versions");
        builder.AppendLine(CultureInfo.InvariantCulture, $"DroidDeck: {AppInfo.Version}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {AppInfo.RuntimeVersion}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"OS: {AppInfo.OperatingSystem}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Architecture: {AppInfo.Architecture}");
        builder.AppendLine();
    }

    private void AppendPaths(StringBuilder builder)
    {
        builder.AppendLine("## Paths");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Data directory: {Redactor.Redact(_paths.DataDirectory)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Logs: {Redactor.Redact(_paths.LogsDirectory)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Settings: {Redactor.Redact(_paths.SettingsFile)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Plugins: {Redactor.Redact(_paths.PluginsDirectory)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Diagnostics exports: {Redactor.Redact(_paths.DiagnosticsDirectory)}");
        builder.AppendLine();
    }

    private void AppendToolchain(StringBuilder builder)
    {
        builder.AppendLine("## Toolchain");
        var snapshot = _toolchain.CachedSnapshot;
        if (snapshot is null)
        {
            builder.AppendLine("Toolchain check has not completed in this session.");
            builder.AppendLine("Run Settings > Tools > Check toolchain to populate this section.");
        }
        else
        {
            builder.AppendLine(Redactor.Redact(snapshot.ToSummary()));
            builder.AppendLine(CultureInfo.InvariantCulture, $"Checked: {snapshot.CheckedAt:yyyy-MM-ddTHH:mm:ssZ}");
        }

        builder.AppendLine();
    }

    private void AppendSettings(StringBuilder builder)
    {
        builder.AppendLine("## Settings");
        var snapshot = _configuration.Current;
        builder.AppendLine(CultureInfo.InvariantCulture, $"Initialized: {_configuration.IsInitialized}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Schema version: {snapshot.SchemaVersion}");
        if (_configuration.LastLoadNotice is { } notice)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Recovery notice: {Redactor.Redact(notice.Message)}");
        }

        var overrides = snapshot
            .GetSourceMap()
            .Where(pair => pair.Value.Scope != ConfigScope.BuiltInDefaults)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSettingEntries)
            .ToArray();
        if (overrides.Length == 0)
        {
            builder.AppendLine("No settings overridden from defaults.");
        }
        else
        {
            builder.AppendLine("Overridden settings:");
            foreach (var (path, source) in overrides)
            {
                var value = snapshot.GetValue(path)?.ToJsonString() ?? "null";
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {path} = {Redactor.Redact(value)} [{source.DisplayName}]");
            }
        }

        builder.AppendLine();
    }

    private void AppendDevices(StringBuilder builder)
    {
        builder.AppendLine("## Devices");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"ADB socket: {(_devices.IsAdbConnected ? "connected" : "disconnected")}");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"CLI fallback: {(_devices.IsUsingCliFallback ? "active" : "inactive")}");
        if (!string.IsNullOrWhiteSpace(_devices.LastError))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Last ADB error: {Redactor.Redact(_devices.LastError)}");
        }

        var devices = _devices.Devices;
        if (devices.Count == 0)
        {
            builder.AppendLine("No devices tracked.");
        }
        else
        {
            foreach (var device in devices)
            {
                var details = string.Join(
                    ", ",
                    new[]
                    {
                        $"state={device.State}",
                        $"connection={device.ConnectionKind}",
                        device.Model is null ? null : $"model={device.Model}",
                        device.Manufacturer is null ? null : $"manufacturer={device.Manufacturer}",
                        device.AndroidVersion is null ? null : $"android={device.AndroidVersion}",
                        device.ApiLevel is null ? null : $"api={device.ApiLevel}",
                        device.Abi is null ? null : $"abi={device.Abi}",
                        device.AvdName is null ? null : $"avd={device.AvdName}",
                        device.MetadataUpdatedAt is null ? null : $"metadata={device.MetadataUpdatedAt:yyyy-MM-ddTHH:mm:ssZ}",
                    }.Where(part => part is not null));
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {Redactor.Redact(device.Serial)}: {Redactor.Redact(details)}");
            }
        }

        builder.AppendLine();
    }

    private void AppendRecentErrors(StringBuilder builder, int max)
    {
        builder.AppendLine(CultureInfo.InvariantCulture, $"## Recent errors ({DiagnosticsId})");
        var errors = _log.RecentErrors(max);
        if (errors.Count == 0)
        {
            builder.AppendLine("No errors logged in this session.");
        }
        else
        {
            foreach (var entry in errors)
            {
                var line = entry with { SessionId = entry.SessionId ?? SessionId };
                builder.AppendLine(Redactor.Redact(line.Format()));
            }
        }

        builder.AppendLine();
    }

    private void AppendRecentLogs(StringBuilder builder, int max)
    {
        builder.AppendLine(CultureInfo.InvariantCulture, $"## Recent log entries (last {max})");
        var entries = _log.Snapshot();
        var start = Math.Max(0, entries.Count - max);
        if (entries.Count == 0)
        {
            builder.AppendLine("No log entries in this session.");
        }
        else
        {
            for (var index = start; index < entries.Count; index++)
            {
                var entry = entries[index] with { SessionId = entries[index].SessionId ?? SessionId };
                builder.AppendLine(Redactor.Redact(entry.Format()));
            }
        }

        builder.AppendLine();
    }
}

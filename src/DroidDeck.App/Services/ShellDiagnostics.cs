using DroidDeck.Core.Diagnostics;

namespace DroidDeck.App.Services;

/// <summary>Builds diagnostics reports with the shell-owned plugin health section.</summary>
public static class ShellDiagnostics
{
    /// <summary>Creates report options that include plugin health.</summary>
    public static DiagnosticsReportOptions CreateOptions(PluginHost pluginHost) => new()
    {
        ExtraSections = [new DiagnosticsSection("Plugins", pluginHost.BuildHealthReport())],
    };

    /// <summary>Builds a redacted report including plugin health.</summary>
    public static string BuildReport(IDiagnosticsService diagnostics, PluginHost pluginHost) =>
        diagnostics.BuildReport(CreateOptions(pluginHost));

    /// <summary>Saves a redacted report including plugin health.</summary>
    public static Task<string> SaveReportAsync(
        IDiagnosticsService diagnostics,
        PluginHost pluginHost,
        CancellationToken cancellationToken = default) =>
        diagnostics.SaveReportAsync(CreateOptions(pluginHost), cancellationToken);
}

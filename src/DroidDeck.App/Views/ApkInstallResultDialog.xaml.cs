using System.Runtime.InteropServices;
using DroidDeck.Core.Runs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DroidDeck.App.Views;

/// <summary>Shows the outcome of one APK install across the selected devices.</summary>
public sealed partial class ApkInstallResultDialog : ContentDialog
{
    private readonly ApkInstallPlan _plan;
    private readonly IReadOnlyList<ResultRow> _rows;

    /// <summary>Creates the result dialog.</summary>
    public ApkInstallResultDialog(
        ApkInstallPlan plan,
        IReadOnlyList<ApkInstallResult> results)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(results);

        InitializeComponent();
        _plan = plan;
        _rows = results.Select(result => new ResultRow(result)).ToArray();
        ResultList.ItemsSource = _rows;

        var summary = ApkInstallSummary.From(results);
        SummaryText.Text =
            $"{plan.PackageName}: {summary.Succeeded} succeeded, {summary.Failed} failed, " +
            $"{summary.Skipped} skipped.";
        if (summary.Failed > 0)
        {
            SummaryBar.Severity = InfoBarSeverity.Error;
            SummaryBar.Title = summary.Failed == 1
                ? "One device failed"
                : $"{summary.Failed} devices failed";
            SummaryBar.Message = "Open Details on a failed device for the adb output and the suggested fix.";
        }
        else if (summary.Succeeded == 0)
        {
            SummaryBar.Severity = InfoBarSeverity.Warning;
            SummaryBar.Title = "No devices installed the APK";
            SummaryBar.Message = "The selected devices were not ready when the install started.";
        }
        else if (summary.Skipped > 0)
        {
            SummaryBar.Severity = InfoBarSeverity.Warning;
            SummaryBar.Title = "The APK was installed with skipped devices";
            SummaryBar.Message = "Skipped devices were not ready when the install started.";
        }
        else
        {
            SummaryBar.Severity = InfoBarSeverity.Success;
            SummaryBar.Title = summary.Succeeded == 1
                ? "Installed on the device"
                : $"Installed on all {summary.Succeeded} devices";
            SummaryBar.Message = "The package was installed successfully.";
        }
    }

    private void OnCopyResultsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(BuildReport());
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception exception) when (
            exception is COMException or UnauthorizedAccessException)
        {
            SummaryBar.Severity = InfoBarSeverity.Warning;
            SummaryBar.Message = "The results could not be copied to the clipboard.";
        }
    }

    private string BuildReport()
    {
        var lines = new List<string>
        {
            "DroidDeck APK install results",
            $"Package: {_plan.PackageName}",
            $"Version: {_plan.BaseApk.VersionName ?? "unknown"} ({_plan.BaseApk.VersionCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"})",
            $"Files: {string.Join(", ", _plan.Files.Select(Path.GetFileName))}",
            string.Empty,
        };
        foreach (var row in _rows)
        {
            lines.Add($"- {row.DeviceName} ({row.Serial}): {row.StateLabel} — {row.Message}");
            if (row.HasDetails)
            {
                lines.Add(row.Details);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>One device result shown in the dialog.</summary>
    public sealed class ResultRow
    {
        /// <summary>Creates a row from an install result.</summary>
        public ResultRow(ApkInstallResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            State = result.State;
            Serial = result.Serial;
            DeviceName = string.IsNullOrWhiteSpace(result.DeviceName)
                ? result.Serial
                : result.DeviceName;
            Message = result.Message;
            var presentation = State switch
            {
                ApkInstallState.Succeeded => (Label: "Succeeded", Glyph: "✓"),
                ApkInstallState.Failed => (Label: "Failed", Glyph: "✕"),
                _ => (Label: "Skipped", Glyph: "–"),
            };
            StateLabel = presentation.Label;
            StateGlyph = presentation.Glyph;

            var sections = new List<string>();
            if (result.Diagnosis is { } diagnosis)
            {
                sections.Add(
                    $"{diagnosis.Title}{Environment.NewLine}{diagnosis.Explanation}" +
                    $"{Environment.NewLine}Guidance: {diagnosis.Guidance}");
            }

            if (!string.IsNullOrWhiteSpace(result.Output))
            {
                sections.Add($"adb output:{Environment.NewLine}{result.Output}");
            }

            Details = string.Join(Environment.NewLine + Environment.NewLine, sections);
        }

        /// <summary>Gets the install state.</summary>
        public ApkInstallState State { get; }

        /// <summary>Gets the device serial.</summary>
        public string Serial { get; }

        /// <summary>Gets the device name.</summary>
        public string DeviceName { get; }

        /// <summary>Gets the result message.</summary>
        public string Message { get; }

        /// <summary>Gets the state label.</summary>
        public string StateLabel { get; }

        /// <summary>Gets the state glyph.</summary>
        public string StateGlyph { get; }

        /// <summary>Gets the diagnosis and raw output shown in Details.</summary>
        public string Details { get; }

        /// <summary>Gets whether there are details to show.</summary>
        public bool HasDetails => Details.Length > 0;
    }
}

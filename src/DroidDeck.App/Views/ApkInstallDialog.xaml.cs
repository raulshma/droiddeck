using DroidDeck.Core.Devices;
using DroidDeck.Core.Runs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>
/// Reviews a dropped APK set and selects the devices that should receive it.
/// </summary>
public sealed partial class ApkInstallDialog : ContentDialog
{
    private readonly IReadOnlyList<DeviceRow> _deviceRows;

    /// <summary>Creates the install dialog for an inspection result and the current devices.</summary>
    public ApkInstallDialog(ApkInstallInspection inspection, IReadOnlyList<DeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(devices);

        InitializeComponent();
        var plans = inspection.Plans
            .Select(plan => new PlanRow(plan))
            .ToArray();
        PlanCombo.ItemsSource = plans;
        PlanCombo.SelectedIndex = plans.Length > 0 ? 0 : -1;
        PlanCombo.Visibility = Visible(plans.Length > 1);
        PlanCard.Visibility = Visible(plans.Length > 0);
        DeviceSection.Visibility = Visible(plans.Length > 0);

        _deviceRows = devices
            .Select(device => new DeviceRow(device))
            .ToArray();
        DeviceList.ItemsSource = _deviceRows;
        var readyCount = _deviceRows.Count(row => row.CanInstall);
        DeviceHint.Text = readyCount == 0
            ? "No ready devices"
            : readyCount == 1
                ? "1 ready device"
                : $"{readyCount} ready devices";

        if (plans.Length == 0)
        {
            Title = "APK cannot be installed";
            PrimaryButtonText = string.Empty;
            SecondaryButtonText = string.Empty;
            CloseButtonText = "Close";
            DefaultButton = ContentDialogButton.Close;
        }
        else
        {
            IsPrimaryButtonEnabled = readyCount > 0;
            IsSecondaryButtonEnabled = readyCount > 0;
            if (readyCount == 0)
            {
                ErrorBar.Title = "No ready devices";
                ErrorBar.Message = "Connect, unlock, or authorize a device, then drop the APK again.";
                ErrorBar.IsOpen = true;
            }
        }

        if (inspection.Errors.Count > 0)
        {
            ErrorCard.Visibility = Visibility.Visible;
            ErrorCardTitle.Text = plans.Length > 0
                ? "Some dropped files were ignored"
                : "DroidDeck could not read this APK";
            ErrorList.ItemsSource = inspection.Errors;
            GuidanceList.ItemsSource = inspection.Guidance;
            GuidanceHeader.Visibility = Visible(inspection.Guidance.Count > 0);
            if (plans.Length == 0)
            {
                ErrorBar.Title = "APK inspection failed";
                ErrorBar.Message = "Fix the problem below, then choose the APK again.";
                ErrorBar.IsOpen = true;
            }
        }

        UpdatePlan();
    }

    /// <summary>Gets the selected APK set, or <see langword="null"/> when none can be installed.</summary>
    internal ApkInstallPlan? SelectedPlan => (PlanCombo.SelectedItem as PlanRow)?.Plan;

    /// <summary>Gets the devices the user selected, including ones that will be skipped.</summary>
    public IReadOnlyList<DeviceInfo> SelectedDevices => _deviceRows
        .Where(row => row.IsSelected)
        .Select(row => row.Device)
        .ToArray();

    /// <summary>Gets whether the user chose to launch the app after installing.</summary>
    public bool Launch { get; private set; }

    private void OnPlanChanged(object sender, SelectionChangedEventArgs e) => UpdatePlan();

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        ValidateAndAccept(launch: true, args);

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) =>
        ValidateAndAccept(launch: false, args);

    private void ValidateAndAccept(bool launch, ContentDialogButtonClickEventArgs args)
    {
        if (SelectedPlan is null)
        {
            args.Cancel = true;
            return;
        }

        if (!SelectedDevices.Any(device => device.CanRunActions))
        {
            args.Cancel = true;
            ErrorBar.Title = "No ready devices selected";
            ErrorBar.Message = "Select at least one ready device, or close the dialog.";
            ErrorBar.IsOpen = true;
            return;
        }

        Launch = launch;
    }

    private void UpdatePlan()
    {
        if (SelectedPlan is not { } plan)
        {
            return;
        }

        var info = plan.BaseApk;
        PackageText.Text = string.IsNullOrWhiteSpace(info.ApplicationLabel)
            ? info.PackageName
            : $"{info.ApplicationLabel} · {info.PackageName}";
        VersionText.Text =
            $"Version {info.VersionName ?? "unknown"} ({Format(info.VersionCode)})";
        SdkText.Text = info.MinSdk is null && info.TargetSdk is null
            ? "The APK does not declare SDK levels."
            : $"minSdk {Format(info.MinSdk)} · targetSdk {Format(info.TargetSdk)}";
        AbiText.Text = info.Abis.Count == 0
            ? "No native ABI restriction was reported."
            : $"ABIs: {string.Join(", ", info.Abis)}";
        FilesText.Text = plan.HasSplits
            ? $"{plan.Files.Count} APK files (base + {plan.Files.Count - 1} split(s))"
            : "Single APK file";
        ToolTipService.SetToolTip(
            FilesText,
            string.Join(Environment.NewLine, plan.Files.Select(Path.GetFileName)));
        ActivityText.Text = string.IsNullOrWhiteSpace(info.LaunchableActivity)
            ? "Launch activity: resolved on the device"
            : $"Launch activity: {info.LaunchableActivity}";
    }

    private static Visibility Visible(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    private static string Format(long? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";

    private static string Format(int? value) =>
        Format(value is { } number ? (long?)number : null);

    /// <summary>One installable APK set in the plan picker.</summary>
    public sealed class PlanRow
    {
        /// <summary>Creates a plan row.</summary>
        public PlanRow(ApkInstallPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            Plan = plan;
            var label = string.IsNullOrWhiteSpace(plan.BaseApk.ApplicationLabel)
                ? plan.PackageName
                : plan.BaseApk.ApplicationLabel;
            var version = string.IsNullOrWhiteSpace(plan.BaseApk.VersionName)
                ? string.Empty
                : $" {plan.BaseApk.VersionName}";
            Title = $"{label}{version} · {plan.PackageName}";
        }

        /// <summary>Gets the APK set.</summary>
        public ApkInstallPlan Plan { get; }

        /// <summary>Gets the label shown in the picker.</summary>
        public string Title { get; }
    }

    /// <summary>One device that can receive the APK set.</summary>
    public sealed class DeviceRow
    {
        /// <summary>Creates a device row.</summary>
        public DeviceRow(DeviceInfo device)
        {
            ArgumentNullException.ThrowIfNull(device);
            Device = device;
            IsSelected = true;
            var details = new List<string>();
            if (!string.Equals(device.DisplayName, device.Serial, StringComparison.Ordinal))
            {
                details.Add(device.Serial);
            }

            if (device.ApiLevel is { } apiLevel)
            {
                details.Add($"API {apiLevel}");
            }

            if (!string.IsNullOrWhiteSpace(device.Abi))
            {
                details.Add(device.Abi);
            }

            Detail = details.Count == 0 ? "No metadata yet" : string.Join(" · ", details);
        }

        /// <summary>Gets the device.</summary>
        public DeviceInfo Device { get; }

        /// <summary>Gets the display name.</summary>
        public string DisplayName => Device.DisplayName;

        /// <summary>Gets the serial and device detail line.</summary>
        public string Detail { get; }

        /// <summary>Gets the transport state label.</summary>
        public string StateLabel => Device.State.ToString();

        /// <summary>Gets whether the device can receive installs.</summary>
        public bool CanInstall => Device.CanRunActions;

        /// <summary>Gets or sets whether the device is selected.</summary>
        public bool IsSelected { get; set; }
    }
}

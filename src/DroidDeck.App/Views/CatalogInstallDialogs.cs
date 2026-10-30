using DroidDeck.Core.Infrastructure;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugins.Catalog;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Views;

/// <summary>
/// Shared dialogs for the catalog install pipeline. Every install and update
/// shows the trust dialog before any file is extracted; the signature is
/// display-only and never required.
/// </summary>
internal static class CatalogInstallDialogs
{
    /// <summary>
    /// Shows the trust decision for an install or update and returns whether
    /// the user approved it. Callers on a background thread are marshalled to
    /// the UI thread through <paramref name="dispatcher"/>; the result and any
    /// failure of the dialog are carried back to the awaiting caller.
    /// </summary>
    public static async Task<bool> ShowTrustDecisionAsync(
        DispatcherQueue? dispatcher,
        XamlRoot xamlRoot,
        CatalogInstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(plan);
        if (dispatcher is null || dispatcher.HasThreadAccess)
        {
            return await ShowTrustAsync(xamlRoot, plan);
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await ShowTrustAsync(xamlRoot, plan));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return await completion.Task;
    }

    /// <summary>
    /// Shows the trust decision for an install or update and returns whether the
    /// user approved it. Must be called on the UI thread.
    /// </summary>
    public static async Task<bool> ShowTrustAsync(XamlRoot xamlRoot, CatalogInstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        ArgumentNullException.ThrowIfNull(plan);
        var capabilities = PluginCapabilities.Parse(plan.Capabilities);
        var capabilitiesLabel =
            capabilities.Known == PluginCapability.None && !capabilities.HasUnknown
                ? "Declared capabilities: none."
                : capabilities.HasUnknown
                    ? $"Declared capabilities: {PluginCapabilities.ToDisplayString(capabilities.Known)} " +
                        $"(unrecognized: {string.Join(", ", capabilities.Unknown)})."
                    : $"Declared capabilities: {PluginCapabilities.ToDisplayString(capabilities.Known)}.";
        var action = plan.IsUpdate
            ? $"update {plan.PluginName} from version {plan.CurrentVersion} to {plan.Version}"
            : $"install {plan.PluginName} {plan.Version}";
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"{plan.PluginId} · version {plan.Version}" +
                (plan.IsUpdate ? $" · installed: {plan.CurrentVersion}" : string.Empty),
        });
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = capabilitiesLabel,
        });
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Package SHA-256 {HashText.ShortHash(plan.Sha256)} · Source: {plan.SourceName} · " +
                (string.IsNullOrWhiteSpace(plan.Signature)
                    ? "Unsigned package (signatures are optional and display-only)."
                    : "Signature: " + Trimmed(plan.Signature, 64)),
        });
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text =
                $"Approving lets DroidDeck {action}. The package hash was verified against the catalog before " +
                "this dialog. Installing does not run the plugin: DroidDeck records a SHA-256 fingerprint of " +
                "the plugin files and asks you to approve them in Extensions before they load.",
        });
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75,
            Text =
                "Capabilities describe which extension points the plugin declares. " +
                "They are disclosed here, not enforced as a sandbox. Only approve plugins whose " +
                "files and source you trust.",
        });

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = plan.IsUpdate
                ? $"Update {plan.PluginName} to v{plan.Version}?"
                : $"Install {plan.PluginName} {plan.Version}?",
            Content = content,
            PrimaryButtonText = plan.IsUpdate ? "Trust and update" : "Trust and install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string Trimmed(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}

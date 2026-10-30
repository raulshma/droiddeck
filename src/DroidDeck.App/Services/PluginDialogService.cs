using System.Runtime.InteropServices;
using DroidDeck.Plugin.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.App.Services;

/// <summary>
/// The host implementation of the plugin dialog service. A plugin requests one
/// of its registered dialogs; the service resolves it from the registry scoped
/// to the requesting plugin, creates the content on the shell's UI thread
/// through the plugin invoker so faults stay isolated, and presents it as a
/// ContentDialog. A plugin never hosts its own window, and a failing dialog
/// shows the host's fallback message instead.
/// </summary>
public sealed class PluginDialogService : IPluginDialogServiceProvider
{
    private readonly Func<PluginHost> _plugins;
    private readonly Func<XamlRoot?> _xamlRoot;
    private readonly DispatcherQueue? _dispatcher;

    /// <summary>
    /// Creates the dialog service. The plugin host, xaml root, and dispatcher
    /// are evaluated when a dialog is shown, so the service can be constructed
    /// while the host itself is still being built.
    /// </summary>
    public PluginDialogService(Func<PluginHost> plugins, Func<XamlRoot?> xamlRoot, DispatcherQueue? dispatcher = null)
    {
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _xamlRoot = xamlRoot ?? throw new ArgumentNullException(nameof(xamlRoot));
        _dispatcher = dispatcher;
    }

    /// <summary>Creates a dialog service that only shows dialogs owned by <paramref name="pluginId"/>.</summary>
    public IPluginDialogService CreateScope(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return new ScopedDialogService(this, pluginId);
    }

    private async Task<PluginDialogResult> ShowAsync(string pluginId, string dialogId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dialogId);
        return await RunOnUiAsync(() => ShowCoreAsync(pluginId, dialogId, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<PluginDialogResult> ShowCoreAsync(string pluginId, string dialogId, CancellationToken cancellationToken)
    {
        var registry = _plugins().Registry;
        var extension = registry.FindDialog(pluginId, dialogId);
        if (extension is null)
        {
            return PluginDialogResult.Failed($"This plugin has no dialog named '{dialogId}'.");
        }

        var xamlRoot = _xamlRoot();
        if (xamlRoot is null)
        {
            return PluginDialogResult.Failed("The shell window is not available for dialogs.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Content creation runs inline on the UI thread, so plugin elements keep
        // their thread affinity while the invoker still time-boxes the plugin.
        var presentation = await _plugins().Invoker.CreateDialogAsync(extension, cancellationToken).ConfigureAwait(true);

        var buttons = extension.Value.Buttons;
        var dialog = new ContentDialog
        {
            Title = extension.Value.Title,
            XamlRoot = xamlRoot,
        };
        ApplyButtons(dialog, buttons);
        dialog.Content = presentation.Succeeded
            ? presentation.Content.Content as FrameworkElement ?? new TextBlock
            {
                Text = presentation.Content.Content?.ToString() ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
            }
            : new TextBlock
            {
                Text = "The plugin dialog could not be shown: " + presentation.Content.Error,
                TextWrapping = TextWrapping.Wrap,
            };

        ContentDialogResult choice;
        try
        {
            choice = await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            // Another dialog is already open, or the window went away.
            return PluginDialogResult.Failed("The dialog could not be shown because another dialog is active.");
        }

        var buttonId = MapResult(choice, buttons);
        await _plugins().Invoker
            .InvokeDialogClosedAsync(extension, buttonId, CancellationToken.None)
            .ConfigureAwait(true);
        return PluginDialogResult.Closed(buttonId);
    }

    private static void ApplyButtons(ContentDialog dialog, IReadOnlyList<PluginDialogButton> buttons)
    {
        var primary = buttons.FirstOrDefault(static button => button.IsPrimary);
        if (primary is not null)
        {
            dialog.PrimaryButtonText = primary.Text;
            dialog.DefaultButton = ContentDialogButton.Primary;
        }

        var others = buttons.Where(static button => !button.IsPrimary).Take(2).ToArray();
        if (others.Length > 0)
        {
            dialog.SecondaryButtonText = others[0].Text;
        }

        if (others.Length > 1)
        {
            dialog.CloseButtonText = others[1].Text;
        }

        if (buttons.Count == 0)
        {
            dialog.CloseButtonText = "Close";
            dialog.DefaultButton = ContentDialogButton.Close;
        }
    }

    private static string? MapResult(ContentDialogResult result, IReadOnlyList<PluginDialogButton> buttons) => result switch
    {
        ContentDialogResult.Primary => buttons.FirstOrDefault(static button => button.IsPrimary)?.Id,
        ContentDialogResult.Secondary => buttons.Where(static button => !button.IsPrimary).FirstOrDefault()?.Id,
        _ => null,
    };

    private Task<T> RunOnUiAsync<T>(Func<Task<T>> action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() => _ = AwaitAsync(action, completion)))
        {
            completion.SetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        }

        return completion.Task;

        static async Task AwaitAsync(Func<Task<T>> action, TaskCompletionSource<T> completion)
        {
            try
            {
                completion.SetResult(await action().ConfigureAwait(true));
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }
    }

    private sealed class ScopedDialogService(PluginDialogService owner, string pluginId) : IPluginDialogService
    {
        public Task<PluginDialogResult> ShowAsync(string dialogId, CancellationToken cancellationToken = default) =>
            owner.ShowAsync(pluginId, dialogId, cancellationToken);
    }
}

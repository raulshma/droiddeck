using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.UI.Kit;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeckPlugin;

/// <summary>Registers this plugin's contributions with DroidDeck.</summary>
public sealed class DroidDeckPlugin : IPlugin
{
    /// <inheritdoc />
    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Extensions.AddNavigationPage(new DroidDeckPluginPage());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A navigation page the host creates the first time it is opened.</summary>
public sealed class DroidDeckPluginPage : INavigationPage
{
    /// <inheritdoc />
    public string Id => "com.example.myplugin.page";

    /// <inheritdoc />
    public string Title => "DroidDeckPlugin";

    /// <inheritdoc />
    public string Icon => string.Empty;

    /// <inheritdoc />
    public Task<object> CreateViewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var layout = new PluginPageLayout("DroidDeckPlugin", "A page contributed by the plugin.")
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Replace this content with your plugin's UI.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "The host owns this page and isolates it inside a fault boundary.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.8,
                    },
                },
            },
        };
        return Task.FromResult<object>(layout);
    }
}

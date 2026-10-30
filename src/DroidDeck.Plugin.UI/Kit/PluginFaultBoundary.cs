using DroidDeck.Plugin.UI.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.Plugin.UI.Kit;

/// <summary>Receives plugin UI faults so the host can feed its crash guard.</summary>
public interface IPluginFaultReporter
{
    /// <summary>Records one plugin UI fault.</summary>
    void Report(string pluginId, string operation, Exception exception);
}

/// <summary>
/// Hosts one plugin view. The host creates the plugin content lazily and hands
/// it to <see cref="Show"/>; the boundary shows a themed fallback card when the
/// content is missing or failed, and plugin interactions run through
/// <see cref="GuardAsync"/> so a throwing handler cannot break the shell. Every
/// fault is reported through the optional reporter so the host can pause a
/// plugin that fails repeatedly.
/// </summary>
public sealed class PluginFaultBoundary : ContentControl, IDisposable
{
    private readonly string _pluginId;
    private readonly string _operation;
    private readonly IPluginFaultReporter? _faultReporter;
    private string? _fallbackMessage;
    private bool _disposed;

    /// <summary>Creates a boundary for one plugin extension.</summary>
    /// <param name="pluginId">The contributing plugin identifier.</param>
    /// <param name="operation">A short description used in diagnostics.</param>
    /// <param name="faultReporter">Where faults are reported, when tracked.</param>
    public PluginFaultBoundary(string pluginId, string operation, IPluginFaultReporter? faultReporter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        _pluginId = pluginId;
        _operation = operation;
        _faultReporter = faultReporter;

        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(this, $"PluginFaultBoundary_{operation}");
        ActualThemeChanged += OnActualThemeChanged;
    }

    /// <summary>Shows plugin content, or the themed fallback card when it failed.</summary>
    public void Show(object? content, string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (error is not null || content is null)
        {
            ShowFallback(error ?? "The plugin did not provide any content.");
            return;
        }

        _fallbackMessage = null;
        Content = content is FrameworkElement element
            ? element
            : new TextBlock
            {
                Text = content.ToString() ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4),
            };
    }

    /// <summary>Runs a plugin interaction; a failure swaps the content for the fallback card.</summary>
    public async Task GuardAsync(Func<CancellationToken, Task> interaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await interaction(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is a normal outcome, not a plugin fault.
        }
        catch (Exception exception)
        {
            ReportFault(exception);
            ShowFallback(exception.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ActualThemeChanged -= OnActualThemeChanged;
        if (Content is IDisposable disposable)
        {
            disposable.Dispose();
        }

        Content = null;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        // Rebuild the fallback card so its brushes follow light, dark, and high contrast.
        if (_fallbackMessage is not null)
        {
            ShowFallback(_fallbackMessage);
        }
    }

    private void ReportFault(Exception exception)
    {
        try
        {
            _faultReporter?.Report(_pluginId, _operation, exception);
        }
        catch (Exception)
        {
            // A failing reporter must never break the host.
        }
    }

    private void ShowFallback(string message)
    {
        _fallbackMessage = message;
        Content = CreateFallbackCard(message);
    }

    private static Border CreateFallbackCard(string message)
    {
        var title = new TextBlock
        {
            Text = "This plugin UI could not be shown.",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var detail = new TextBlock
        {
            Text = message,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        };
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(title);
        text.Children.Add(detail);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(new FontIcon { Glyph = "\uE7BA", FontSize = 18 });
        panel.Children.Add(text);

        return new Border
        {
            Background = RendererVisuals.Brush(
                "SubtleFillColorSecondaryBrush",
                Windows.UI.Color.FromArgb(14, 128, 128, 128)),
            BorderBrush = RendererVisuals.Brush(
                "CardStrokeColorDefaultBrush",
                Windows.UI.Color.FromArgb(46, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = panel,
        };
    }
}

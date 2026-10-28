using DroidDeck.Core.Runs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.App.ViewModels;

/// <summary>Resolves theme brushes so row view models never hard-code colours.</summary>
public static class ThemeBrushes
{
    /// <summary>Resolves a brush from the application theme resources.</summary>
    public static Brush Resolve(string key) =>
        Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush
            ? brush
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);

    /// <summary>Gets the brush that represents a run session state.</summary>
    public static Brush For(RunSessionState state) => Resolve(state switch
    {
        RunSessionState.Succeeded => "SystemFillColorSuccessBrush",
        RunSessionState.Failed or RunSessionState.PartiallyFailed => "SystemFillColorCriticalBrush",
        RunSessionState.Cancelled or RunSessionState.Stopping => "SystemFillColorCautionBrush",
        RunSessionState.Running or RunSessionState.Building or RunSessionState.Installing
            or RunSessionState.Launching => "AccentTextFillColorPrimaryBrush",
        _ => "TextFillColorSecondaryBrush",
    });
}

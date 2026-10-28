using DroidDeck.Core.Toolchain;
using Microsoft.UI.Xaml.Media;

namespace DroidDeck.App.ViewModels;

/// <summary>One doctor result shaped as a home environment chip.</summary>
public sealed record EnvironmentToolRow(string Name, string Value, bool IsAvailable, string Detail, string ChipId)
{
    /// <summary>Gets the status dot colour.</summary>
    public Brush StatusBrush => ThemeBrushes.Resolve(
        IsAvailable ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush");

    /// <summary>Gets whether the tool is missing.</summary>
    public bool IsMissing => !IsAvailable;

    /// <summary>Creates a chip from a doctor result.</summary>
    public static EnvironmentToolRow From(ToolInfo tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var value = tool.IsAvailable
            ? string.IsNullOrWhiteSpace(tool.Version) ? "available" : tool.Version
            : "not found";
        var detail = string.Join(
            " · ",
            new[] { tool.Path, tool.Error, tool.Guidance }
                .Where(static part => !string.IsNullOrWhiteSpace(part)));
        return new EnvironmentToolRow(
            tool.DisplayName,
            value,
            tool.IsAvailable,
            detail,
            AutomationId.For("HomeEnv", tool.Kind.ToString()));
    }
}

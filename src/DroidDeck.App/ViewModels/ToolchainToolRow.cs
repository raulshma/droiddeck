using DroidDeck.Core.Toolchain;

namespace DroidDeck.App.ViewModels;

/// <summary>A formatted row in the toolchain doctor list.</summary>
public sealed record ToolchainToolRow(string DisplayName, string Details)
{
    /// <summary>Formats one tool result for display.</summary>
    public static ToolchainToolRow From(ToolInfo tool)
    {
        var status = tool.IsAvailable
            ? string.IsNullOrWhiteSpace(tool.Version) ? "available" : tool.Version
            : "not found";
        var source = tool.Source switch
        {
            ToolDetectionSource.Override => "override",
            ToolDetectionSource.Automatic => "auto",
            _ => "missing",
        };
        var details = string.Join(
            " · ",
            new[] { status, tool.Path, $"source: {source}" }
                .Where(static part => !string.IsNullOrWhiteSpace(part)));
        if (!string.IsNullOrWhiteSpace(tool.Error))
        {
            details += Environment.NewLine + tool.Error;
        }

        if (!string.IsNullOrWhiteSpace(tool.Guidance))
        {
            details += Environment.NewLine + tool.Guidance;
        }

        return new ToolchainToolRow(tool.DisplayName, details);
    }
}

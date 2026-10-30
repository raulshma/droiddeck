using DroidDeck.Plugin.Abstractions.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DroidDeck.Plugin.UI.Rendering;

/// <summary>The fallback log detail renderer that always works.</summary>
public sealed class PlainLogDetailRenderer : IRenderer
{
    /// <inheritdoc />
    public string Id => BuiltInRendererDescriptors.PlainLogDetail.Id;

    /// <inheritdoc />
    public string DisplayName => BuiltInRendererDescriptors.PlainLogDetail.DisplayName;

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.LogDetail;

    /// <inheritdoc />
    public int Priority => BuiltInRendererDescriptors.PlainLogDetail.Priority;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) => context.Data is LogDetailData;

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (LogDetailData)context.Data;
        var panel = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(data.Tag) || !string.IsNullOrWhiteSpace(data.Level))
        {
            panel.Children.Add(RendererVisuals.Text(
                string.Join("  ",
                    new[] { data.Level, data.Tag }.Where(value => !string.IsNullOrWhiteSpace(value))),
                fontSize: 11,
                bold: true));
        }

        panel.Children.Add(RendererVisuals.Text(data.Line, monospace: true));
        return RendererVisuals.Scroll(panel);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>Pretty-prints the JSON payload embedded in a log line.</summary>
public sealed class JsonLogDetailRenderer : IRenderer
{
    /// <inheritdoc />
    public string Id => BuiltInRendererDescriptors.JsonLogDetail.Id;

    /// <inheritdoc />
    public string DisplayName => BuiltInRendererDescriptors.JsonLogDetail.DisplayName;

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.LogDetail;

    /// <inheritdoc />
    public int Priority => BuiltInRendererDescriptors.JsonLogDetail.Priority;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) =>
        context.Data is LogDetailData data && RendererVisuals.TryFormatJson(data.Line) is not null;

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (LogDetailData)context.Data;
        var json = RendererVisuals.TryFormatJson(data.Line) ?? data.Line;
        return RendererVisuals.ScrollingText(json, monospace: true, wrap: false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>Groups stack trace frames for crash and exception entries.</summary>
public sealed class StackTraceLogDetailRenderer : IRenderer
{
    /// <inheritdoc />
    public string Id => BuiltInRendererDescriptors.StackTraceLogDetail.Id;

    /// <inheritdoc />
    public string DisplayName => BuiltInRendererDescriptors.StackTraceLogDetail.DisplayName;

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.LogDetail;

    /// <inheritdoc />
    public int Priority => BuiltInRendererDescriptors.StackTraceLogDetail.Priority;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context)
    {
        if (context.Data is not LogDetailData data)
        {
            return false;
        }

        var line = data.Line;
        return line.Contains("Exception", StringComparison.Ordinal) &&
               (line.Contains(" at ", StringComparison.Ordinal) ||
                line.Contains("--- Beginning of crash", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var data = (LogDetailData)context.Data;
        var lines = data.Line
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines.FirstOrDefault(line =>
            line.Contains("Exception", StringComparison.Ordinal)) ?? lines.FirstOrDefault() ?? string.Empty;
        var frames = lines
            .Where(line => !string.Equals(line, header, StringComparison.Ordinal))
            .ToArray();

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(RendererVisuals.Text(header, fontSize: 13, bold: true));
        if (frames.Length > 0)
        {
            var framePanel = new StackPanel { Spacing = 2 };
            foreach (var frame in frames)
            {
                framePanel.Children.Add(RendererVisuals.Text(frame, monospace: true, fontSize: 11));
            }

            panel.Children.Add(framePanel);
        }

        return RendererVisuals.Scroll(panel);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>Renders an individual log line for opted-in filtered views.</summary>
public sealed class PlainLogLineRenderer : IRenderer
{
    /// <inheritdoc />
    public string Id => BuiltInRendererDescriptors.PlainLogLine.Id;

    /// <inheritdoc />
    public string DisplayName => BuiltInRendererDescriptors.PlainLogLine.DisplayName;

    /// <inheritdoc />
    public RendererKind Kind => RendererKind.LogLine;

    /// <inheritdoc />
    public int Priority => BuiltInRendererDescriptors.PlainLogLine.Priority;

    /// <inheritdoc />
    public bool CanRender(IRenderContext context) => context.Data is LogLineData or string;

    /// <inheritdoc />
    public FrameworkElement CreateView(IRenderContext context)
    {
        var text = context.Data switch
        {
            LogLineData data => data.Text,
            string line => line,
            _ => context.Data.ToString() ?? string.Empty,
        };

        return RendererVisuals.Text(text, monospace: true, wrap: false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

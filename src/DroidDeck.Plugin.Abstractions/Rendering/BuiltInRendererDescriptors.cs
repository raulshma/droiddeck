namespace DroidDeck.Plugin.Abstractions.Rendering;

/// <summary>
/// Metadata for the renderers the host ships. Built-in renderer implementations
/// use these descriptors so View as menus, settings, and tests agree on identity.
/// </summary>
public static class BuiltInRendererDescriptors
{
    /// <summary>The plain log detail renderer.</summary>
    public static readonly RendererDescriptor PlainLogDetail = Create(
        "droiddeck.builtin.renderer.logDetail.plain",
        RendererKind.LogDetail,
        "Plain text",
        priority: 0);

    /// <summary>The JSON pretty-print log detail renderer.</summary>
    public static readonly RendererDescriptor JsonLogDetail = Create(
        "droiddeck.builtin.renderer.logDetail.json",
        RendererKind.LogDetail,
        "JSON pretty-print",
        priority: 10);

    /// <summary>The stack trace log detail renderer.</summary>
    public static readonly RendererDescriptor StackTraceLogDetail = Create(
        "droiddeck.builtin.renderer.logDetail.stackTrace",
        RendererKind.LogDetail,
        "Stack trace",
        priority: 20);

    /// <summary>The plain log line renderer.</summary>
    public static readonly RendererDescriptor PlainLogLine = Create(
        "droiddeck.builtin.renderer.logLine.plain",
        RendererKind.LogLine,
        "Plain log line",
        priority: 0);

    /// <summary>The line chart renderer.</summary>
    public static readonly RendererDescriptor LineChart = Create(
        "droiddeck.builtin.renderer.chart.line",
        RendererKind.Chart,
        "Line chart",
        priority: 0);

    /// <summary>The text file preview renderer.</summary>
    public static readonly RendererDescriptor TextFilePreview = Create(
        "droiddeck.builtin.renderer.filePreview.text",
        RendererKind.FilePreview,
        "Text preview",
        priority: 0);

    /// <summary>The image file preview renderer.</summary>
    public static readonly RendererDescriptor ImageFilePreview = Create(
        "droiddeck.builtin.renderer.filePreview.image",
        RendererKind.FilePreview,
        "Image preview",
        priority: 10);

    /// <summary>The test result detail renderer.</summary>
    public static readonly RendererDescriptor TestResultDetail = Create(
        "droiddeck.builtin.renderer.testResult.detail",
        RendererKind.TestResult,
        "Test result detail",
        priority: 0);

    /// <summary>The generic data view renderer.</summary>
    public static readonly RendererDescriptor DataView = Create(
        "droiddeck.builtin.renderer.dataView.properties",
        RendererKind.DataView,
        "Data view",
        priority: 0);

    /// <summary>Gets every built-in renderer descriptor.</summary>
    public static IReadOnlyList<RendererDescriptor> All { get; } =
    [
        PlainLogDetail,
        JsonLogDetail,
        StackTraceLogDetail,
        PlainLogLine,
        LineChart,
        TextFilePreview,
        ImageFilePreview,
        TestResultDetail,
        DataView,
    ];

    private static RendererDescriptor Create(
        string id,
        RendererKind kind,
        string displayName,
        int priority) =>
        new(
            id,
            PluginConstants.BuiltInPluginId,
            kind,
            displayName,
            priority,
            IsBuiltIn: true);
}

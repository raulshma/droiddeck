namespace DroidDeck.Plugin.UI.Rendering;

/// <summary>
/// The renderer types the host ships. Built-in extensions register these through
/// the same registry plugins use, so they share the selection and health rules.
/// </summary>
public static class BuiltInRenderers
{
    /// <summary>Gets every built-in renderer type.</summary>
    public static IReadOnlyList<Type> Types { get; } =
    [
        typeof(PlainLogDetailRenderer),
        typeof(JsonLogDetailRenderer),
        typeof(StackTraceLogDetailRenderer),
        typeof(PlainLogLineRenderer),
        typeof(LineChartRenderer),
        typeof(TextFilePreviewRenderer),
        typeof(ImageFilePreviewRenderer),
        typeof(TestResultDetailRenderer),
        typeof(DataViewRenderer),
    ];
}

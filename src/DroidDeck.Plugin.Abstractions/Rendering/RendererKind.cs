namespace DroidDeck.Plugin.Abstractions.Rendering;

/// <summary>The surfaces a renderer can replace.</summary>
public enum RendererKind
{
    /// <summary>The detail view of a selected log entry.</summary>
    LogDetail,

    /// <summary>Custom rendering for individual log lines in filtered views.</summary>
    LogLine,

    /// <summary>Performance and metric charts.</summary>
    Chart,

    /// <summary>A device or local file preview.</summary>
    FilePreview,

    /// <summary>The details of a selected test result.</summary>
    TestResult,

    /// <summary>Inspector nodes and other generic structured data.</summary>
    DataView,
}

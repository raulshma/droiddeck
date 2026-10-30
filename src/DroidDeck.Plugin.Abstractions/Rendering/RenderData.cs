namespace DroidDeck.Plugin.Abstractions.Rendering;

/// <summary>The data a log detail renderer receives.</summary>
/// <param name="Line">The complete log line, including the header.</param>
/// <param name="Tag">The log tag, when known.</param>
/// <param name="Level">The one-letter log level, when known.</param>
public sealed record LogDetailData(string Line, string? Tag = null, string? Level = null);

/// <summary>The data a log line renderer receives.</summary>
/// <param name="Text">The message text.</param>
/// <param name="Level">The one-letter log level, when known.</param>
public sealed record LogLineData(string Text, string? Level = null);

/// <summary>One series in a chart. Values are sampled by the host, never by the renderer.</summary>
/// <param name="Label">The series label.</param>
/// <param name="Values">The sampled values, oldest first.</param>
public sealed record ChartSeries(string Label, IReadOnlyList<double> Values);

/// <summary>The data a chart renderer receives.</summary>
/// <param name="Title">The chart title.</param>
/// <param name="Series">One or more sampled series.</param>
/// <param name="Minimum">The value at the bottom of the chart.</param>
/// <param name="Maximum">The value at the top of the chart.</param>
/// <param name="Unit">The unit suffix, such as <c>%</c> or <c>MB</c>.</param>
public sealed record ChartData(
    string Title,
    IReadOnlyList<ChartSeries> Series,
    double Minimum = 0,
    double Maximum = 100,
    string Unit = "");

/// <summary>The data a file preview renderer receives.</summary>
/// <param name="FileName">The file name, including its extension.</param>
/// <param name="Text">The decoded text, when the host could read it.</param>
/// <param name="Bytes">The raw bytes, when the host could read them.</param>
public sealed record FilePreviewData(string FileName, string? Text = null, byte[]? Bytes = null);

/// <summary>The data a test result renderer receives.</summary>
/// <param name="Name">The test name.</param>
/// <param name="Status">The outcome label.</param>
/// <param name="Message">The failure message, when there is one.</param>
/// <param name="StackTrace">The stack trace, when there is one.</param>
/// <param name="Attachments">Attachment names.</param>
public sealed record TestResultData(
    string Name,
    string Status,
    string? Message = null,
    string? StackTrace = null,
    IReadOnlyList<string>? Attachments = null);

/// <summary>One labeled value in a data view.</summary>
/// <param name="Name">The property name.</param>
/// <param name="Value">The display value.</param>
public sealed record DataViewProperty(string Name, string Value);

/// <summary>The data a generic data view renderer receives.</summary>
/// <param name="Title">The title of the inspected item.</param>
/// <param name="Properties">Named values to show.</param>
/// <param name="Json">Optional JSON that can be pretty-printed.</param>
public sealed record DataViewData(
    string Title,
    IReadOnlyList<DataViewProperty>? Properties = null,
    string? Json = null);

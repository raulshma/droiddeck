using System.Globalization;
using System.Xml.Linq;

namespace DroidDeck.Core.Toolbox;

/// <summary>One view node from a uiautomator hierarchy dump.</summary>
public sealed record UiHierarchyNode
{
    /// <summary>Gets the fully qualified view class.</summary>
    public required string ClassName { get; init; }

    /// <summary>Gets the resource identifier, when the view declares one.</summary>
    public string? ResourceId { get; init; }

    /// <summary>Gets the visible text, when present.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the accessibility content description, when present.</summary>
    public string? ContentDescription { get; init; }

    /// <summary>Gets the owning package.</summary>
    public string? PackageName { get; init; }

    /// <summary>Gets the raw bounds attribute.</summary>
    public string? Bounds { get; init; }

    /// <summary>Gets how deeply the node is nested below the hierarchy root.</summary>
    public int Depth { get; init; }

    /// <summary>Gets the view's sibling index.</summary>
    public int Index { get; init; }

    /// <summary>Gets whether the view can be clicked.</summary>
    public bool Clickable { get; init; }

    /// <summary>Gets whether the view accepts input.</summary>
    public bool Enabled { get; init; }

    /// <summary>Gets whether the view can receive focus.</summary>
    public bool Focusable { get; init; }

    /// <summary>Gets whether the view is scrollable.</summary>
    public bool Scrollable { get; init; }

    /// <summary>Gets whether the view is checkable and checked.</summary>
    public bool Checked { get; init; }

    /// <summary>Gets whether the view is selected.</summary>
    public bool Selected { get; init; }

    /// <summary>Gets whether the view masks its text as a password.</summary>
    public bool Password { get; init; }

    /// <summary>Gets a compact label for the node list.</summary>
    public string Display => FirstNonEmpty(Text, ContentDescription, ResourceId, SimpleClassName);

    /// <summary>Gets the secondary line shown under the node label.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ResourceId))
            {
                parts.Add(ResourceId);
            }

            if (!string.IsNullOrWhiteSpace(Bounds))
            {
                parts.Add(Bounds);
            }

            if (!Enabled)
            {
                parts.Add("disabled");
            }

            if (Clickable)
            {
                parts.Add("clickable");
            }

            if (Focusable)
            {
                parts.Add("focusable");
            }

            if (Scrollable)
            {
                parts.Add("scrollable");
            }

            if (Checked)
            {
                parts.Add("checked");
            }

            if (Selected)
            {
                parts.Add("selected");
            }

            if (Password)
            {
                parts.Add("password");
            }

            return parts.Count == 0 ? SimpleClassName : string.Join(" · ", parts);
        }
    }

    private string SimpleClassName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ClassName))
            {
                return "node";
            }

            var separator = ClassName.LastIndexOf('.');
            return separator >= 0 && separator < ClassName.Length - 1
                ? ClassName[(separator + 1)..]
                : ClassName;
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "node";
    }
}

/// <summary>A parsed uiautomator hierarchy with flattening and filter support.</summary>
public sealed class UiHierarchy
{
    private readonly IReadOnlyList<UiHierarchyNode> _nodes;

    private UiHierarchy(string rawXml, IReadOnlyList<UiHierarchyNode> nodes)
    {
        RawXml = rawXml;
        _nodes = nodes;
    }

    /// <summary>Gets the hierarchy XML exactly as it was dumped.</summary>
    public string RawXml { get; }

    /// <summary>Gets every node in document order.</summary>
    public IReadOnlyList<UiHierarchyNode> Nodes => _nodes;

    /// <summary>Parses a uiautomator hierarchy XML document.</summary>
    public static UiHierarchy Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = document.Root
            ?? throw new InvalidOperationException("The hierarchy XML has no root element.");
        var nodes = new List<UiHierarchyNode>();
        if (string.Equals(root.Name.LocalName, "node", StringComparison.Ordinal))
        {
            AddNode(root, 0, nodes);
        }
        else
        {
            foreach (var element in root.Elements().Where(element =>
                         string.Equals(element.Name.LocalName, "node", StringComparison.Ordinal)))
            {
                AddNode(element, 0, nodes);
            }
        }

        return new UiHierarchy(xml, nodes);
    }

    /// <summary>
    /// Returns nodes matching a query, keeping matched ancestors so the
    /// indentation still describes the tree.
    /// </summary>
    public IReadOnlyList<UiHierarchyNode> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _nodes;
        }

        var term = query.Trim();
        var included = new HashSet<int>();
        for (var index = 0; index < _nodes.Count; index++)
        {
            if (!Matches(_nodes[index], term))
            {
                continue;
            }

            included.Add(index);
            var depth = _nodes[index].Depth;
            for (var ancestor = index - 1; ancestor >= 0 && depth > 0; ancestor--)
            {
                if (_nodes[ancestor].Depth >= depth)
                {
                    continue;
                }

                included.Add(ancestor);
                depth = _nodes[ancestor].Depth;
            }
        }

        var result = new List<UiHierarchyNode>(included.Count);
        for (var index = 0; index < _nodes.Count; index++)
        {
            if (included.Contains(index))
            {
                result.Add(_nodes[index]);
            }
        }

        return result;
    }

    private static bool Matches(UiHierarchyNode node, string term) =>
        Contains(node.Text, term) ||
        Contains(node.ContentDescription, term) ||
        Contains(node.ResourceId, term) ||
        Contains(node.ClassName, term) ||
        Contains(node.PackageName, term);

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static void AddNode(XElement element, int depth, List<UiHierarchyNode> nodes)
    {
        nodes.Add(new UiHierarchyNode
        {
            ClassName = Attribute(element, "class") ?? "node",
            ResourceId = Attribute(element, "resource-id"),
            Text = Attribute(element, "text"),
            ContentDescription = Attribute(element, "content-desc"),
            PackageName = Attribute(element, "package"),
            Bounds = Attribute(element, "bounds"),
            Index = IntegerAttribute(element, "index"),
            Depth = depth,
            Clickable = BooleanAttribute(element, "clickable"),
            Enabled = BooleanAttribute(element, "enabled", defaultValue: true),
            Focusable = BooleanAttribute(element, "focusable"),
            Scrollable = BooleanAttribute(element, "scrollable"),
            Checked = BooleanAttribute(element, "checked"),
            Selected = BooleanAttribute(element, "selected"),
            Password = BooleanAttribute(element, "password"),
        });

        foreach (var child in element.Elements().Where(child =>
                     string.Equals(child.Name.LocalName, "node", StringComparison.Ordinal)))
        {
            AddNode(child, depth + 1, nodes);
        }
    }

    private static string? Attribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool BooleanAttribute(XElement element, string name, bool defaultValue = false)
    {
        var value = element.Attribute(name)?.Value;
        return value is null
            ? defaultValue
            : string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int IntegerAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}

using System.Text;

namespace DroidDeck.Core.Toolbox;

/// <summary>Builds a copyable XPath selector for an inspected view node.</summary>
public static class UiSelector
{
    /// <summary>
    /// Prefers the resource id, then the content description, then the visible text,
    /// and finally the class plus sibling index.
    /// </summary>
    public static string ForNode(UiHierarchyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!string.IsNullOrWhiteSpace(node.ResourceId))
        {
            return $"//*[@resource-id={Literal(node.ResourceId)}]";
        }

        if (!string.IsNullOrWhiteSpace(node.ContentDescription))
        {
            return $"//*[@content-desc={Literal(node.ContentDescription)}]";
        }

        if (!string.IsNullOrWhiteSpace(node.Text))
        {
            return $"//*[@text={Literal(node.Text)}]";
        }

        return $"//*[@class={Literal(node.ClassName)} and @index=\"{node.Index}\"]";
    }

    // XPath 1.0 has no escape sequences inside string literals. Use the other
    // quote style when possible and fall back to concat() when both quotes occur.
    private static string Literal(string value)
    {
        if (!value.Contains('"', StringComparison.Ordinal))
        {
            return $"\"{value}\"";
        }

        if (!value.Contains('\'', StringComparison.Ordinal))
        {
            return $"'{value}'";
        }

        var parts = value.Split('"');
        var builder = new StringBuilder("concat(");
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(", '\"', ");
            }

            builder.Append('"').Append(parts[index]).Append('"');
        }

        return builder.Append(')').ToString();
    }
}

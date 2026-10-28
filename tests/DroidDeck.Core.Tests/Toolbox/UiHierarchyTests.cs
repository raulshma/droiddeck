using DroidDeck.Core.Toolbox;

namespace DroidDeck.Core.Tests.Toolbox;

public sealed class UiHierarchyTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <hierarchy rotation="0">
          <node index="0" text="" resource-id="" class="android.widget.FrameLayout" package="com.example.app" content-desc="" checkable="false" checked="false" clickable="false" enabled="true" focusable="false" focused="false" scrollable="false" long-clickable="false" password="false" selected="false" bounds="[0,0][1080,1920]">
            <node index="0" text="Sign in" resource-id="com.example.app:id/sign_in" class="android.widget.Button" package="com.example.app" content-desc="Sign in button" checkable="false" checked="false" clickable="true" enabled="true" focusable="true" focused="false" scrollable="false" long-clickable="false" password="false" selected="false" bounds="[100,200][500,300]">
              <node index="0" text="" resource-id="" class="android.widget.TextView" package="com.example.app" content-desc="" checkable="false" checked="false" clickable="false" enabled="false" focusable="false" focused="false" scrollable="false" long-clickable="false" password="false" selected="false" bounds="[120,220][480,280]" />
            </node>
            <node index="1" text="Settings" resource-id="com.example.app:id/settings" class="android.widget.TextView" package="com.example.app" content-desc="" checkable="false" checked="false" clickable="true" enabled="true" focusable="true" focused="false" scrollable="false" long-clickable="false" password="false" selected="false" bounds="[100,400][500,500]" />
          </node>
        </hierarchy>
        """;

    [Fact]
    public void Parse_FlattensDepthAndReadsAttributes()
    {
        var hierarchy = UiHierarchy.Parse(Sample);

        Assert.Equal(4, hierarchy.Nodes.Count);
        Assert.Equal(0, hierarchy.Nodes[0].Depth);
        Assert.Equal(1, hierarchy.Nodes[1].Depth);
        Assert.Equal(2, hierarchy.Nodes[2].Depth);
        Assert.Equal(1, hierarchy.Nodes[3].Depth);
        Assert.Equal("Sign in", hierarchy.Nodes[1].Text);
        Assert.Equal("com.example.app:id/sign_in", hierarchy.Nodes[1].ResourceId);
        Assert.True(hierarchy.Nodes[1].Clickable);
        Assert.False(hierarchy.Nodes[2].Enabled);
        Assert.Equal("[100,200][500,300]", hierarchy.Nodes[1].Bounds);
        Assert.Equal("Sign in", hierarchy.Nodes[1].Display);
        Assert.Contains("clickable", hierarchy.Nodes[1].Detail, StringComparison.Ordinal);
        Assert.Contains("disabled", hierarchy.Nodes[2].Detail, StringComparison.Ordinal);
        Assert.Same(Sample, hierarchy.RawXml);
    }

    [Fact]
    public void Parse_UsesContentDescriptionAndClassFallbacks()
    {
        var hierarchy = UiHierarchy.Parse("""
            <hierarchy>
              <node class="android.widget.ImageView" content-desc="Avatar" />
              <node class="android.widget.FrameLayout" />
            </hierarchy>
            """);

        Assert.Equal("Avatar", hierarchy.Nodes[0].Display);
        Assert.Equal("FrameLayout", hierarchy.Nodes[1].Display);
    }

    [Fact]
    public void Search_MatchesTextResourceClassAndPackage()
    {
        var hierarchy = UiHierarchy.Parse(Sample);

        Assert.Equal(2, hierarchy.Search("sign").Count);
        Assert.Equal(2, hierarchy.Search("settings").Count);
        Assert.Equal(4, hierarchy.Search("com.example.app").Count);
        Assert.Equal(4, hierarchy.Search("android.widget").Count);
        Assert.Equal(4, hierarchy.Search(" ").Count);
    }

    [Fact]
    public void Search_WithNoQueryReturnsEveryNode()
    {
        var hierarchy = UiHierarchy.Parse(Sample);

        Assert.Same(hierarchy.Nodes, hierarchy.Search(null));
        Assert.Same(hierarchy.Nodes, hierarchy.Search(""));
        Assert.Same(hierarchy.Nodes, hierarchy.Search("   "));
    }

    [Fact]
    public void Search_RejectsInvalidXml()
    {
        Assert.Throws<System.Xml.XmlException>(() => UiHierarchy.Parse("<hierarchy>"));
    }
}

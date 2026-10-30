using System.Text.Json;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.Plugin.Abstractions.Rendering;
using DroidDeck.Plugin.UI.Rendering;
using DroidDeck.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace DroidDeck.Plugins.Tests.Rendering;

public sealed class RendererPluginTests
{
    private const string PluginId = "com.example.sample.renderers";

    [Fact]
    public async Task ASamplePluginContributesLogDetailAndChartRenderers()
    {
        using var temp = new TempDirectory();
        var directory = StagePlugin(temp);
        var registry = new PluginRegistry();
        await using var loader = new PluginLoader(registry, NullLoggerFactory.Instance, hostVersion: "1.0.0");

        var result = await loader.LoadAsync(directory);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Validation.Issues.Select(issue => issue.ToString())));
        var registrations = registry.Renderers;
        Assert.Equal(2, registrations.Count);
        Assert.All(registrations, registration => Assert.Equal(PluginId, registration.PluginId));

        var renderers = registrations
            .Select(registration => (IRenderer)Activator.CreateInstance(registration.RendererType)!)
            .ToArray();
        Assert.Contains(renderers, renderer => renderer.Kind == RendererKind.LogDetail);
        Assert.Contains(renderers, renderer => renderer.Kind == RendererKind.Chart);
        Assert.All(renderers, renderer => Assert.NotEmpty(renderer.Id));
        Assert.Equal(PluginId, registry.GetOwner(renderers[0].GetType()));
        Assert.Equal(PluginId, registry.GetOwner(renderers[1].GetType()));
    }

    private static string StagePlugin(TempDirectory temp)
    {
        var directory = temp.CreateDirectory("plugins", PluginId);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "SampleRendererPlugin.dll"),
            Path.Combine(directory, "SampleRendererPlugin.dll"),
            overwrite: true);

        var manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "Sample Renderer Plugin",
            Version = "1.0.0",
            ApiVersion = "1.0",
            MinHostVersion = "0.1.0",
            EntryAssembly = "SampleRendererPlugin.dll",
            EntryType = "SampleRendererPlugin.SampleRendererPlugin",
            Capabilities = ["renderer"],
        };
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        return directory;
    }
}

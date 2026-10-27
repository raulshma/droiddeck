using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginAbstractionsArchitectureTests
{
    [Fact]
    public void AbstractionsHaveNoDependencyOnHostImplementationsOrUi()
    {
        var assembly = typeof(IPlugin).Assembly;
        var references = assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .ToArray();

        Assert.Equal("DroidDeck.Plugin.Abstractions", assembly.GetName().Name);
        Assert.DoesNotContain(references, name => name.StartsWith("DroidDeck.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.UI", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.WindowsAppSDK", StringComparison.Ordinal));
    }
}

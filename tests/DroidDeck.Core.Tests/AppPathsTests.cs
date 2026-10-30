using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void ResolveUsesTheDataFolderBesideAPortableMarker()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, AppPaths.PortableMarkerFileName), string.Empty);

        var paths = AppPaths.Resolve(executableDirectory: temp.Path);

        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine(temp.Path, "data"), paths.DataDirectory);
    }

    [Fact]
    public void ConstructorTreatsExplicitDataAsNonPortableByDefault()
    {
        using var temp = new TempDirectory();

        var paths = new AppPaths(temp.Combine("data"));

        Assert.False(paths.IsPortable);
    }
}

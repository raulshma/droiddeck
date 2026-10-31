using DroidDeck.Core.Toolchain;

namespace DroidDeck.Core.Tests.Toolchain;

public sealed class ToolchainProbeTableTests
{
    // Java is custom only because its override is a JDK home that must be resolved to
    // bin/java.exe with a distinct failure message; the rest of its probe is executable-shaped.
    private static readonly ToolKind[] CustomProbeKinds =
    [
        ToolKind.AndroidSdk,
        ToolKind.Java,
        ToolKind.Unity,
        ToolKind.MauiAndroidWorkload,
    ];

    [Fact]
    public void ContainsExactlyOneSpecPerToolKind()
    {
        var kinds = ToolchainProbeTable.All.Select(spec => spec.Kind).ToList();

        Assert.Equal(Enum.GetValues<ToolKind>().Length, kinds.Count);
        Assert.Equal(kinds.Count, kinds.Distinct().Count());
        Assert.All(
            Enum.GetValues<ToolKind>(),
            kind => Assert.Same(
                Assert.Single(ToolchainProbeTable.All, spec => spec.Kind == kind),
                ToolchainProbeTable.Get(kind)));
    }

    [Fact]
    public void EverySpecIsWellFormed()
    {
        Assert.All(ToolchainProbeTable.All, spec =>
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.DisplayName), spec.Kind.ToString());
            Assert.False(string.IsNullOrWhiteSpace(spec.Guidance), spec.Kind.ToString());
            Assert.NotNull(spec.OverridePath);
            Assert.True(spec.Timeout > TimeSpan.Zero, spec.Kind.ToString());
            Assert.All(
                spec.PathNames,
                name => Assert.False(string.IsNullOrWhiteSpace(name), spec.Kind.ToString()));
        });
    }

    [Fact]
    public void PathNamesAreUniqueWithinEachSpec()
    {
        Assert.All(ToolchainProbeTable.All, spec =>
        {
            var duplicate = spec.PathNames
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);

            Assert.Null(duplicate);
        });
    }

    [Fact]
    public void ExecutableSpecsDeclareEverythingTheSharedProbeNeeds()
    {
        var executableSpecs = ToolchainProbeTable.All.Where(
            spec => spec.Kind is not (ToolKind.AndroidSdk or ToolKind.Unity or ToolKind.MauiAndroidWorkload));

        Assert.All(executableSpecs, spec =>
        {
            Assert.NotEmpty(spec.PathNames);
            Assert.NotEmpty(spec.VersionArguments);
            Assert.NotNull(spec.VersionParser);
        });
    }

    [Fact]
    public void OnlyDocumentedKindsUseCustomProbes()
    {
        var actual = ToolchainProbeTable.All
            .Where(spec => spec.CustomProbe is not null)
            .Select(spec => spec.Kind)
            .Order()
            .ToArray();

        Assert.Equal(CustomProbeKinds.Order().ToArray(), actual);
    }
}

using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Plugins.Tests;

public sealed class PluginCompatibilityPolicyTests
{
    [Theory]
    [InlineData("1.0", ApiCompatibility.Compatible, true, false)]
    [InlineData("1.1", ApiCompatibility.MinorMismatch, true, true)]
    [InlineData("2.0", ApiCompatibility.MajorMismatch, false, false)]
    [InlineData("not-a-version", ApiCompatibility.Invalid, false, false)]
    public void ApiVersionDecidesLoadabilityAndWarning(
        string apiVersion,
        ApiCompatibility expectedApi,
        bool expectedCompatible,
        bool expectedWarning)
    {
        var verdict = PluginCompatibilityPolicy.EvaluateVersion(apiVersion, null, null, "1.0.0");

        Assert.Equal(expectedApi, verdict.Api);
        Assert.Equal(expectedCompatible, verdict.IsCompatible);
        Assert.Equal(expectedWarning, verdict.HasWarning);
        Assert.Empty(verdict.HostRangeIssues);
        Assert.Empty(verdict.DependencyIssues);
    }

    [Theory]
    [InlineData("1.0.0", false)]
    [InlineData("1.1.0", false)]
    [InlineData("0.9.9", true)]
    public void MinimumHostVersionIsInclusive(string hostVersion, bool tooOld)
    {
        var verdict = PluginCompatibilityPolicy.EvaluateVersion("1.0", "1.0.0", null, hostVersion);

        if (tooOld)
        {
            var issue = Assert.Single(verdict.HostRangeIssues);
            Assert.Equal(PluginHostRangeIssueKind.HostTooOld, issue.Kind);
            Assert.Equal("1.0.0", issue.DeclaredVersion);
            Assert.Equal("1.0.0", issue.BoundaryVersion);
            Assert.False(verdict.IsCompatible);
        }
        else
        {
            Assert.Empty(verdict.HostRangeIssues);
            Assert.True(verdict.IsCompatible);
        }
    }

    [Theory]
    [InlineData("2.0.0", false)]
    [InlineData("1.9.9", false)]
    [InlineData("2.0.1", true)]
    public void MaximumHostVersionIsInclusive(string hostVersion, bool tooNew)
    {
        var verdict = PluginCompatibilityPolicy.EvaluateVersion("1.0", null, "2.0.0", hostVersion);

        if (tooNew)
        {
            var issue = Assert.Single(verdict.HostRangeIssues);
            Assert.Equal(PluginHostRangeIssueKind.HostTooNew, issue.Kind);
            Assert.Equal("2.0.0", issue.BoundaryVersion);
            Assert.False(verdict.IsCompatible);
        }
        else
        {
            Assert.Empty(verdict.HostRangeIssues);
            Assert.True(verdict.IsCompatible);
        }
    }

    [Theory]
    [InlineData("one", null, PluginHostRangeIssueKind.MinimumHostVersionInvalid)]
    [InlineData(null, "two", PluginHostRangeIssueKind.MaximumHostVersionInvalid)]
    public void InvalidBoundaryVersionsAreReportedWithoutABoundary(
        string? minHostVersion,
        string? maxHostVersion,
        PluginHostRangeIssueKind expectedKind)
    {
        var verdict = PluginCompatibilityPolicy.EvaluateVersion(
            "1.0",
            minHostVersion,
            maxHostVersion,
            "1.0.0");

        var issue = Assert.Single(verdict.HostRangeIssues);
        Assert.Equal(expectedKind, issue.Kind);
        Assert.Null(issue.BoundaryVersion);
        Assert.False(verdict.IsCompatible);
    }

    [Fact]
    public void AnInvalidHostVersionSkipsRangeChecksAndKeepsTheInput()
    {
        var verdict = PluginCompatibilityPolicy.EvaluateVersion(
            "1.0",
            "1.0.0",
            "2.0.0",
            "not-a-version");

        Assert.Empty(verdict.HostRangeIssues);
        Assert.Equal("not-a-version", verdict.HostVersion);
        Assert.True(verdict.IsCompatible);
    }

    [Fact]
    public void AMissingDependencyIsReported()
    {
        var issues = PluginCompatibilityPolicy.EvaluateDependencies(
            [Dependency("com.example.base", "1.0.0")],
            new Dictionary<string, string>());

        var issue = Assert.Single(issues);
        Assert.Equal(PluginDependencyIssueKind.Missing, issue.Kind);
        Assert.Equal("com.example.base", issue.DependencyId);
        Assert.Equal("1.0.0", issue.RequiredVersion);
        Assert.Null(issue.InstalledVersion);
    }

    [Fact]
    public void AnOlderDependencyIsReportedWithTheInstalledVersion()
    {
        var issues = PluginCompatibilityPolicy.EvaluateDependencies(
            [Dependency("com.example.base", "2.0.0")],
            new Dictionary<string, string> { ["com.example.base"] = "1.5.0" });

        var issue = Assert.Single(issues);
        Assert.Equal(PluginDependencyIssueKind.VersionTooOld, issue.Kind);
        Assert.Equal("com.example.base", issue.DependencyId);
        Assert.Equal("2.0.0", issue.RequiredVersion);
        Assert.Equal("1.5.0", issue.InstalledVersion);
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("2.1.0")]
    public void ANewEnoughDependencyIsSatisfied(string installedVersion)
    {
        var issues = PluginCompatibilityPolicy.EvaluateDependencies(
            [Dependency("com.example.base", "2.0.0")],
            new Dictionary<string, string> { ["com.example.base"] = installedVersion });

        Assert.Empty(issues);
    }

    [Fact]
    public void DependencyIdsAreMatchedCaseInsensitively()
    {
        var issues = PluginCompatibilityPolicy.EvaluateDependencies(
            [Dependency("Com.Example.Base", "1.0.0")],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["com.example.base"] = "1.0.0",
            });

        Assert.Empty(issues);
    }

    [Fact]
    public void AnInvalidMinimumVersionDoesNotProduceAnIssue()
    {
        var issues = PluginCompatibilityPolicy.EvaluateDependencies(
            [Dependency("com.example.base", "not-a-version")],
            new Dictionary<string, string> { ["com.example.base"] = "1.0.0" });

        Assert.Empty(issues);
    }

    [Fact]
    public void DependencyIssuesKeepDeclarationOrder()
    {
        var issues = PluginCompatibilityPolicy.EvaluateDependencies(
            [
                Dependency("com.example.first", "1.0.0"),
                Dependency("com.example.second", "1.0.0"),
            ],
            new Dictionary<string, string> { ["com.example.second"] = "0.5.0" });

        Assert.Equal(2, issues.Count);
        Assert.Equal(PluginDependencyIssueKind.Missing, issues[0].Kind);
        Assert.Equal("com.example.first", issues[0].DependencyId);
        Assert.Equal(PluginDependencyIssueKind.VersionTooOld, issues[1].Kind);
        Assert.Equal("com.example.second", issues[1].DependencyId);
    }

    [Fact]
    public void ManifestDependenciesAreResolvedOnlyWhenVersionsAreSupplied()
    {
        var manifest = CreateManifest();
        manifest.MinHostVersion = "1.0.0";
        manifest.Dependencies = [Dependency("com.example.base", "1.0.0")];

        var unresolved = PluginCompatibilityPolicy.Evaluate(manifest, "1.0.0");
        Assert.Empty(unresolved.DependencyIssues);
        Assert.True(unresolved.IsCompatible);

        var resolved = PluginCompatibilityPolicy.Evaluate(
            manifest,
            "1.0.0",
            new Dictionary<string, string> { ["com.example.base"] = "0.9.0" });
        Assert.Single(resolved.DependencyIssues);
        Assert.False(resolved.IsCompatible);
    }

    [Fact]
    public void AHostRangeAndDependenciesAreEvaluatedTogether()
    {
        var manifest = CreateManifest();
        manifest.MinHostVersion = "2.0.0";
        manifest.Dependencies = [Dependency("com.example.base", "1.0.0")];

        var verdict = PluginCompatibilityPolicy.Evaluate(
            manifest,
            "1.0.0",
            new Dictionary<string, string>());

        var hostIssue = Assert.Single(verdict.HostRangeIssues);
        Assert.Equal(PluginHostRangeIssueKind.HostTooOld, hostIssue.Kind);
        var dependencyIssue = Assert.Single(verdict.DependencyIssues);
        Assert.Equal(PluginDependencyIssueKind.Missing, dependencyIssue.Kind);
        Assert.False(verdict.IsCompatible);
    }

    private static PluginManifest CreateManifest() => new()
    {
        Id = "com.example.dependent",
        Name = "Dependent",
        Version = "1.0.0",
        ApiVersion = "1.0",
        EntryAssembly = "Dependent.dll",
        EntryType = "Dependent.Plugin",
    };

    private static PluginDependency Dependency(string id, string minVersion) => new()
    {
        Id = id,
        MinVersion = minVersion,
    };
}

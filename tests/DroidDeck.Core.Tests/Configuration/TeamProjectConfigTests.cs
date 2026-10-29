using System.Text.Json.Nodes;
using DroidDeck.Core.Configuration;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Configuration;

public sealed class TeamProjectConfigTests
{
    [Fact]
    public void AbsolutePathsAreFlaggedWithTheirJsonPath()
    {
        var document = new JsonObject
        {
            ["build"] = new JsonObject
            {
                ["javaHome"] = @"C:\Users\dev\jdk-21",
            },
        };

        var result = TeamProjectConfigValidator.Validate(document);

        Assert.False(result.IsValid);
        var violation = Assert.Single(result.Violations);
        Assert.Equal("build.javaHome", violation.Path);
        Assert.Equal(TeamConfigViolationKind.AbsolutePath, violation.Kind);
    }

    [Theory]
    [InlineData(@"C:\tools\adb.exe")]
    [InlineData(@"\\server\share\sdk")]
    [InlineData("/usr/lib/jvm")]
    public void MachinePathFormsAreRejected(string value)
    {
        var document = new JsonObject { ["tools"] = new JsonObject { ["adbPath"] = value } };

        Assert.False(TeamProjectConfigValidator.Validate(document).IsValid);
    }

    [Fact]
    public void DpapiProtectedBlobsAreFlagged()
    {
        // Base64 of a DPAPI blob header (01 00 00 00 D0 8C 9D DF ...).
        var blob = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA5AAAAAAAAAAAAAAAAAAAAAAAAA";
        var document = new JsonObject
        {
            ["steps"] = new JsonArray(new JsonObject { ["command"] = blob }),
        };

        var result = TeamProjectConfigValidator.Validate(document);

        Assert.False(result.IsValid);
        var violation = Assert.Single(result.Violations);
        Assert.Equal(TeamConfigViolationKind.SecretBlob, violation.Kind);
    }

    [Theory]
    [InlineData("scripts/build.ps1")]
    [InlineData("${secret:SIGNING_KEY}")]
    [InlineData("assembleDebug")]
    [InlineData("jdk-21")]
    public void RelativeValuesAndSecretReferencesAreCommittedSafe(string value)
    {
        var document = new JsonObject
        {
            ["build"] = new JsonObject { ["task"] = value },
            ["steps"] = new JsonArray(new JsonObject { ["command"] = value }),
        };

        Assert.True(TeamProjectConfigValidator.Validate(document).IsValid);
    }

    [Fact]
    public void ValidationWalksArraysAndNestedSections()
    {
        var document = new JsonObject
        {
            ["runConfigs"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "release",
                    ["steps"] = new JsonArray(
                        new JsonObject { ["command"] = "ok.cmd" },
                        new JsonObject { ["command"] = @"D:\tools\sign.bat" }),
                }),
        };

        var result = TeamProjectConfigValidator.Validate(document);

        var violation = Assert.Single(result.Violations);
        Assert.Equal("runConfigs[0].steps[1].command", violation.Path);
    }

    [Fact]
    public void ScriptReferencesAreCollectedForTrustChecks()
    {
        var document = new JsonObject
        {
            ["steps"] = new JsonArray(
                new JsonObject { ["command"] = "scripts/prebuild.ps1" },
                new JsonObject { ["command"] = "gradle assembleDebug" },
                new JsonObject { ["command"] = "scripts/post.bat" },
                new JsonObject { ["command"] = "scripts/same.ps1" },
                new JsonObject { ["command"] = "scripts/prebuild.ps1" }),
        };

        var scripts = TeamProjectConfigValidator.GetScriptReferences(document);

        Assert.Equal(["scripts/prebuild.ps1", "scripts/post.bat", "scripts/same.ps1"], scripts);
    }

    [Theory]
    [InlineData("run.ps1", true)]
    [InlineData("run.cmd", true)]
    [InlineData("run.bat", true)]
    [InlineData("run.sh", true)]
    [InlineData("RUN.PS1", true)]
    [InlineData("run.py", false)]
    [InlineData("run", false)]
    public void ExecutableScriptExtensionsAreRecognized(string value, bool expected)
        => Assert.Equal(expected, TeamProjectConfigValidator.IsExecutableScript(value));

    [Fact]
    public async Task TeamSharedWritesRejectMachinePathsAndKeepTheFileUntouched()
    {
        using var temp = new TempDirectory();
        var repo = temp.Combine("repo");
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetProjectContextAsync(new ProjectContext(repo, "proj-1"));
        await service.SetAsync("build.task", "assembleDebug", ConfigScope.TeamProject);
        var teamFile = Path.Combine(repo, ".droiddeck", "project.json");
        var before = await File.ReadAllTextAsync(teamFile);

        var exception = await Assert.ThrowsAsync<TeamConfigViolationException>(
            () => service.SetAsync("build.javaHome", @"C:\Users\dev\jdk-21", ConfigScope.TeamProject));

        var violation = Assert.Single(exception.Violations);
        Assert.Equal("build.javaHome", violation.Path);
        Assert.Equal(TeamConfigViolationKind.AbsolutePath, violation.Kind);
        Assert.Equal(before, await File.ReadAllTextAsync(teamFile));
        Assert.Equal("assembleDebug", service.GetString("build.task"));
    }

    [Fact]
    public async Task TeamSharedFileOnDiskStaysCommitSafe()
    {
        using var temp = new TempDirectory();
        var repo = temp.Combine("repo");
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetProjectContextAsync(new ProjectContext(repo, "proj-1"));

        await service.SetAsync("build.javaHome", "${secret:JAVA_HOME}", ConfigScope.TeamProject);
        await service.SetAsync("steps", new JsonArray(new JsonObject { ["command"] = "scripts/build.ps1" }), ConfigScope.TeamProject);

        var text = await File.ReadAllTextAsync(Path.Combine(repo, ".droiddeck", "project.json"));
        var document = JsonNode.Parse(text)!.AsObject();
        Assert.True(TeamProjectConfigValidator.Validate(document).IsValid);
    }

    [Fact]
    public async Task LocalProjectWritesMayKeepMachinePaths()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(temp);
        await service.InitializeAsync();
        await service.SetProjectContextAsync(new ProjectContext(temp.Combine("repo"), "proj-1"));

        await service.SetAsync("tools.adbPath", @"C:\tools\adb.exe", ConfigScope.LocalProject);

        Assert.Equal(@"C:\tools\adb.exe", service.GetString("tools.adbPath"));
    }

    private static ConfigurationService CreateService(TempDirectory temp) => new(new AppPaths(temp.Combine("data")));
}

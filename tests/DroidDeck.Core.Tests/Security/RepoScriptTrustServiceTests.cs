using DroidDeck.Core;
using DroidDeck.Core.Security;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Security;

public sealed class RepoScriptTrustServiceTests
{
    [Fact]
    public async Task UntrustedScriptReturnsPendingApprovalWithoutSubscribers()
    {
        using var temp = new TempDirectory();
        var (repo, scriptPath) = WriteScript(temp, "repo/scripts/build.ps1", "Write-Host hi");
        using var service = CreateService(temp);

        var decision = await service.AuthorizeAsync(repo, scriptPath);

        Assert.Equal(ScriptTrustState.PendingApproval, decision.State);
        Assert.False(File.Exists(Path.Combine(Paths(temp).DataDirectory, "trust.json")));
    }

    [Fact]
    public async Task UntrustedScriptPromptsAndInlineApprovalAuthorizesExecution()
    {
        using var temp = new TempDirectory();
        var (repo, scriptPath) = WriteScript(temp, "repo/scripts/build.ps1", "Write-Host hi");
        using var service = CreateService(temp);
        service.TrustRequested += (_, args) =>
        {
            Assert.Equal(scriptPath, args.ScriptPath);
            Assert.Equal("scripts/build.ps1", args.RelativePath);
            service.ApproveAsync(args.RepositoryRoot, args.ScriptPath).GetAwaiter().GetResult();
        };

        var decision = await service.AuthorizeAsync(repo, scriptPath);

        Assert.Equal(ScriptTrustState.Trusted, decision.State);
    }

    [Fact]
    public async Task ApprovedHashIsPinnedAndRememberedAcrossRestarts()
    {
        using var temp = new TempDirectory();
        var (repo, scriptPath) = WriteScript(temp, "repo/scripts/build.ps1", "Write-Host hi");
        using (var service = CreateService(temp))
        {
            await service.ApproveAsync(repo, scriptPath);
        }

        using var restarted = CreateService(temp);
        var decision = await restarted.AuthorizeAsync(repo, scriptPath);

        Assert.Equal(ScriptTrustState.Trusted, decision.State);
        Assert.Equal(Sha256OfFile(scriptPath), decision.Sha256);
    }

    [Fact]
    public async Task DeniedDecisionsAreRememberedUntilTheScriptChanges()
    {
        using var temp = new TempDirectory();
        var (repo, scriptPath) = WriteScript(temp, "repo/scripts/build.ps1", "Write-Host hi");
        using var service = CreateService(temp);
        service.TrustRequested += (_, args) =>
        {
            service.DenyAsync(args.RepositoryRoot, args.ScriptPath).GetAwaiter().GetResult();
        };

        Assert.Equal(ScriptTrustState.Denied, (await service.AuthorizeAsync(repo, scriptPath)).State);
        Assert.Equal(ScriptTrustState.Denied, await service.GetStateAsync(repo, scriptPath));
    }

    [Fact]
    public async Task ModifiedScriptRequiresFreshApproval()
    {
        using var temp = new TempDirectory();
        var (repo, scriptPath) = WriteScript(temp, "repo/scripts/build.ps1", "Write-Host hi");
        using (var service = CreateService(temp))
        {
            await service.ApproveAsync(repo, scriptPath);
        }

        await File.WriteAllTextAsync(scriptPath, "Write-Host evil");
        using var restarted = CreateService(temp);
        var prompts = 0;
        restarted.TrustRequested += (_, _) => prompts++;

        var decision = await restarted.AuthorizeAsync(repo, scriptPath);

        Assert.Equal(ScriptTrustState.PendingApproval, decision.State);
        Assert.Equal(1, prompts);
    }

    [Fact]
    public async Task RevokedApprovalsPromptAgain()
    {
        using var temp = new TempDirectory();
        var (repo, scriptPath) = WriteScript(temp, "repo/scripts/build.ps1", "Write-Host hi");
        using var service = CreateService(temp);
        await service.ApproveAsync(repo, scriptPath);

        await service.RevokeAsync(repo, scriptPath);

        Assert.Equal(ScriptTrustState.PendingApproval, await service.GetStateAsync(repo, scriptPath));
    }

    [Fact]
    public async Task HashComputationMatchesTheKnownSha256()
    {
        using var temp = new TempDirectory();
        var (_, emptyPath) = WriteScript(temp, "repo/empty.cmd", "");
        var (_, abcPath) = WriteScript(temp, "repo/abc.cmd", "abc");
        using var service = CreateService(temp);

        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            await service.ComputeSha256Async(emptyPath));
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            await service.ComputeSha256Async(abcPath));
    }

    [Fact]
    public async Task TrustDataStaysInAppDataAndNeverInTheRepository()
    {
        using var temp = new TempDirectory();
        var (repo, scriptPath) = WriteScript(temp, "repo/scripts/build.ps1", "Write-Host hi");
        using var service = CreateService(temp);

        await service.ApproveAsync(repo, scriptPath);

        Assert.True(File.Exists(Path.Combine(Paths(temp).DataDirectory, "trust.json")));
        Assert.Single(Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DifferentRepositoriesTrustIndependently()
    {
        using var temp = new TempDirectory();
        var first = WriteScript(temp, "repo-a/scripts/build.ps1", "Write-Host hi");
        var second = WriteScript(temp, "repo-b/scripts/build.ps1", "Write-Host hi");
        using var service = CreateService(temp);

        await service.ApproveAsync(first.Repo, first.ScriptPath);

        Assert.Equal(ScriptTrustState.Trusted, await service.GetStateAsync(first.Repo, first.ScriptPath));
        Assert.Equal(ScriptTrustState.PendingApproval, await service.GetStateAsync(second.Repo, second.ScriptPath));
    }

    [Fact]
    public async Task ApprovalFollowsTheScriptWhenItMovesWithinTheRepository()
    {
        using var temp = new TempDirectory();
        var (repo, scriptPath) = WriteScript(temp, "repo/scripts/build.ps1", "Write-Host hi");
        using var service = CreateService(temp);
        await service.ApproveAsync(repo, scriptPath);

        var moved = Path.Combine(repo, "tools", "build.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(scriptPath, moved);

        Assert.Equal(ScriptTrustState.PendingApproval, await service.GetStateAsync(repo, moved));
    }

    private static RepoScriptTrustService CreateService(TempDirectory temp) => new(Paths(temp));

    private static AppPaths Paths(TempDirectory temp) => new(temp.Combine("data"));

    private static string Sha256OfFile(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static (string Repo, string ScriptPath) WriteScript(TempDirectory temp, string relativePath, string contents)
    {
        var scriptPath = temp.WriteFile(relativePath, contents);
        var repositoryRoot = temp.Combine(relativePath.Split('/')[0]);
        return (repositoryRoot, scriptPath);
    }
}

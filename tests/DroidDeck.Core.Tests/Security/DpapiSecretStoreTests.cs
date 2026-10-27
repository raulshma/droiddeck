using DroidDeck.Core.Security;
using DroidDeck.TestSupport;

namespace DroidDeck.Core.Tests.Security;

public sealed class DpapiSecretStoreTests
{
    [Fact]
    public async Task SecretsRoundTripAndSurviveRestart()
    {
        using var temp = new TempDirectory();
        using (var store = new DpapiSecretStore(Paths(temp)))
        {
            await store.SetAsync("github.token", "super-secret-token-42");
            Assert.Equal("super-secret-token-42", await store.GetAsync("github.token"));
            Assert.Equal(["github.token"], await store.ListNamesAsync());
        }

        using var restarted = new DpapiSecretStore(Paths(temp));
        Assert.Equal("super-secret-token-42", await restarted.GetAsync("github.token"));
    }

    [Fact]
    public async Task SecretsNeverAppearInPlaintextOnDisk()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        using var store = new DpapiSecretStore(paths);
        const string secret = "correct-horse-battery-staple-9271";

        await store.SetAsync("signing.key", secret);

        var contents = await File.ReadAllTextAsync(paths.SecretsFile);
        Assert.DoesNotContain(secret, contents, StringComparison.Ordinal);
        Assert.Contains("signing.key", contents, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(paths.DataDirectory, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SettingTheSameNameReplacesTheValue()
    {
        using var temp = new TempDirectory();
        using var store = new DpapiSecretStore(Paths(temp));
        await store.SetAsync("token", "first-value");

        await store.SetAsync("token", "second-value");

        Assert.Equal("second-value", await store.GetAsync("token"));
        Assert.Single(await store.ListNamesAsync());
    }

    [Fact]
    public async Task RemoveDeletesTheSecretAndReportsWhetherItExisted()
    {
        using var temp = new TempDirectory();
        using var store = new DpapiSecretStore(Paths(temp));
        await store.SetAsync("token", "value-1234");

        Assert.True(await store.RemoveAsync("token"));
        Assert.False(await store.RemoveAsync("token"));
        Assert.Null(await store.GetAsync("token"));
        Assert.Empty(await store.ListNamesAsync());
    }

    [Fact]
    public async Task MissingSecretReturnsNull()
    {
        using var temp = new TempDirectory();
        using var store = new DpapiSecretStore(Paths(temp));

        Assert.Null(await store.GetAsync("absent"));
    }

    [Fact]
    public async Task KnownValuesOnlyIncludesValuesLongEnoughToRedact()
    {
        using var temp = new TempDirectory();
        using var store = new DpapiSecretStore(Paths(temp));
        await store.SetAsync("long", "long-enough-value");
        await store.SetAsync("short", "abc");

        var known = await store.GetKnownValuesAsync();

        Assert.Equal(["long-enough-value"], known);
    }

    [Fact]
    public async Task CorruptSecretsFileIsQuarantinedAndTheStoreKeepsWorking()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SecretsFile, "{ not json");
        using var store = new DpapiSecretStore(paths);

        Assert.Empty(await store.ListNamesAsync());
        Assert.NotNull(store.LastLoadWarning);
        Assert.Single(Directory.EnumerateFiles(paths.QuarantineDirectory, "*.corrupt"));

        await store.SetAsync("token", "after-recovery");
        Assert.Equal("after-recovery", await store.GetAsync("token"));
    }

    [Fact]
    public async Task UndecryptableSecretIsIgnoredWithoutCrashing()
    {
        using var temp = new TempDirectory();
        var paths = Paths(temp);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(
            paths.SecretsFile,
            """{ "schemaVersion": 1, "secrets": { "token": "AAAA" } }""");
        using var store = new DpapiSecretStore(paths);

        Assert.Null(await store.GetAsync("token"));
        Assert.Empty(await store.GetKnownValuesAsync());
        Assert.NotNull(store.LastLoadWarning);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("with space")]
    [InlineData("symbols!@#")]
    public async Task InvalidSecretNamesAreRejected(string? name)
    {
        using var temp = new TempDirectory();
        using var store = new DpapiSecretStore(Paths(temp));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SetAsync(name!, "value"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.GetAsync(name!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.RemoveAsync(name!));
    }

    [Fact]
    public async Task OverlongSecretNamesAreRejected()
    {
        using var temp = new TempDirectory();
        using var store = new DpapiSecretStore(Paths(temp));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(new string('a', 129), "value"));
    }

    [Fact]
    public async Task NullSecretValuesAreRejected()
    {
        using var temp = new TempDirectory();
        using var store = new DpapiSecretStore(Paths(temp));

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SetAsync("token", null!));
    }

    [Fact]
    public async Task StoreRejectsUseAfterDispose()
    {
        using var temp = new TempDirectory();
        var store = new DpapiSecretStore(Paths(temp));
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ListNamesAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.SetAsync("token", "value"));
        store.Dispose();
    }

    private static AppPaths Paths(TempDirectory temp) => new(temp.Combine("data"));
}

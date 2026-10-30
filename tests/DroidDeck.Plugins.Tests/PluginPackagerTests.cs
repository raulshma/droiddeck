using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DroidDeck.Core;
using DroidDeck.Plugin.Abstractions;
using DroidDeck.PluginPack;
using DroidDeck.Plugins.Catalog;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests;

/// <summary>
/// Exercises the packaging tool: the archive it writes must be deterministic,
/// leave host-provided assemblies out, and install through the catalog installer.
/// </summary>
public sealed class PluginPackagerTests
{
    private const string PluginId = "com.example.packaged";

    [Fact]
    public async Task ThePackageIsDeterministicAndSkipsHostAssemblies()
    {
        using var temp = new TempDirectory();
        var source = StagePlugin(temp);
        var firstOutput = temp.CreateDirectory("out-first");
        var secondOutput = temp.CreateDirectory("out-second");

        var first = await PluginPackager.CreateAsync(source, firstOutput);
        var second = await PluginPackager.CreateAsync(source, secondOutput);

        Assert.Equal($"{PluginId}-1.2.3.ddplugin", Path.GetFileName(first.PackagePath));
        Assert.Equal(PluginId, first.PluginId);
        Assert.Equal("1.2.3", first.Version);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.Entries, second.Entries);
        Assert.Equal(new FileInfo(first.PackagePath).Length, first.SizeInBytes);
        Assert.Equal(64, first.Sha256.Length);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(first.PackagePath))),
            first.Sha256);

        Assert.Contains("plugin.json", first.Entries);
        Assert.Contains("SamplePlugin.dll", first.Entries);
        Assert.Contains("SamplePlugin.deps.json", first.Entries);
        Assert.DoesNotContain("SamplePlugin.pdb", first.Entries, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DroidDeck.Plugin.Abstractions.dll", first.Entries, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.WinUI.dll", first.Entries, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Microsoft.Extensions.Logging.Abstractions.dll",
            first.Entries,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(first.Entries.OrderBy(entry => entry, StringComparer.Ordinal), first.Entries);
    }

    [Fact]
    public async Task TheCatalogInstallerAcceptsThePackage()
    {
        using var temp = new TempDirectory();
        var package = await PluginPackager.CreateAsync(StagePlugin(temp), temp.CreateDirectory("out"));
        var bytes = await File.ReadAllBytesAsync(package.PackagePath);
        var paths = new AppPaths(temp.Combine("data"));
        paths.EnsureCreated();
        using var audit = new PluginAuditLog(Path.Combine(paths.LogsDirectory, "plugin-audit.jsonl"));
        var installer = new CatalogInstaller(
            new HttpClient(new StaticPackageHandler(bytes)),
            paths,
            audit);

        var result = await installer.InstallAsync(CreateEntry(package), _ => Task.FromResult(true));

        Assert.True(result.Succeeded, result.Error);
        var installed = Path.Combine(paths.PluginsDirectory, PluginId);
        Assert.True(File.Exists(Path.Combine(installed, "plugin.json")));
        Assert.True(File.Exists(Path.Combine(installed, "SamplePlugin.dll")));
        Assert.True(File.Exists(Path.Combine(installed, "SamplePlugin.deps.json")));
        Assert.False(File.Exists(Path.Combine(installed, "DroidDeck.Plugin.Abstractions.dll")));
        Assert.False(File.Exists(Path.Combine(installed, "SamplePlugin.pdb")));
    }

    [Fact]
    public async Task IncludeSymbolsKeepsTheSymbolFiles()
    {
        using var temp = new TempDirectory();
        var source = StagePlugin(temp);

        var result = await PluginPackager.CreateAsync(
            source,
            temp.CreateDirectory("out"),
            includeSymbols: true);

        Assert.Contains("SamplePlugin.pdb", result.Entries);
        Assert.DoesNotContain("DroidDeck.Plugin.Abstractions.dll", result.Entries);
    }

    [Fact]
    public async Task AFolderWithoutAManifestIsRejected()
    {
        using var temp = new TempDirectory();
        var source = temp.CreateDirectory("empty");

        var exception = await Assert.ThrowsAsync<PluginPackException>(
            () => PluginPackager.CreateAsync(source, temp.CreateDirectory("out")));

        Assert.Contains("plugin.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidPluginIdIsRejected()
    {
        using var temp = new TempDirectory();
        var source = StagePlugin(temp, id: "Not A Plugin");

        var exception = await Assert.ThrowsAsync<PluginPackException>(
            () => PluginPackager.CreateAsync(source, temp.CreateDirectory("out")));

        Assert.Contains("plugin id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnInvalidVersionIsRejected()
    {
        using var temp = new TempDirectory();
        var source = StagePlugin(temp, version: "1.2");

        var exception = await Assert.ThrowsAsync<PluginPackException>(
            () => PluginPackager.CreateAsync(source, temp.CreateDirectory("out")));

        Assert.Contains("semantic version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMissingEntryAssemblyIsRejected()
    {
        using var temp = new TempDirectory();
        var source = StagePlugin(temp);
        File.Delete(Path.Combine(source, "SamplePlugin.dll"));

        var exception = await Assert.ThrowsAsync<PluginPackException>(
            () => PluginPackager.CreateAsync(source, temp.CreateDirectory("out")));

        Assert.Contains("SamplePlugin.dll", exception.Message, StringComparison.Ordinal);
    }

    private static string StagePlugin(
        TempDirectory temp,
        string id = PluginId,
        string version = "1.2.3")
    {
        var directory = temp.CreateDirectory("plugin-output");
        var manifest = new PluginManifest
        {
            Id = id,
            Name = "Packaged Sample",
            Version = version,
            ApiVersion = "1.0",
            MinHostVersion = "0.1.0",
            EntryAssembly = "SamplePlugin.dll",
            EntryType = "SamplePlugin.SamplePlugin",
            Capabilities = ["command"],
        };
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        File.WriteAllText(Path.Combine(directory, "SamplePlugin.dll"), "assembly");
        File.WriteAllText(Path.Combine(directory, "SamplePlugin.deps.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "SamplePlugin.pdb"), "symbols");
        File.WriteAllText(Path.Combine(directory, "DroidDeck.Plugin.Abstractions.dll"), "host");
        File.WriteAllText(Path.Combine(directory, "DroidDeck.Plugin.Abstractions.xml"), "docs");
        File.WriteAllText(Path.Combine(directory, "Microsoft.WinUI.dll"), "host");
        File.WriteAllText(
            Path.Combine(directory, "Microsoft.Extensions.Logging.Abstractions.dll"),
            "host");
        return directory;
    }

    private static CatalogEntry CreateEntry(PluginPackageResult package) => new()
    {
        Source = new CatalogSource
        {
            Id = "local",
            Name = "Local",
            Url = "https://example.com/catalog.json",
        },
        Plugin = new CatalogPlugin { Id = package.PluginId, Name = "Packaged Sample" },
        Version = new CatalogVersion
        {
            Version = package.Version,
            ApiVersion = "1.0",
            DownloadUrl = "https://example.com/packaged.ddplugin",
            Sha256 = package.Sha256,
            Size = package.SizeInBytes,
            Capabilities = ["command"],
            Dependencies = [],
        },
        Compatibility = CatalogCompatibility.Compatible,
        CompatibilityLabel = "Compatible",
        IsPrerelease = false,
    };

    private sealed class StaticPackageHandler(byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(package),
            });
    }
}

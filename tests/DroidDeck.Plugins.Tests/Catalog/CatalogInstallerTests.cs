using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DroidDeck.Core;
using DroidDeck.Plugins;
using DroidDeck.Plugins.Catalog;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests.Catalog;

public sealed class CatalogInstallerTests
{
    private const string PluginId = "com.example.chart";

    [Fact]
    public async Task InstallingAValidPackageExtractsItAndRequiresApprovalBeforeLoad()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var entry = harness.CreateEntry("1.0.0");
        CatalogInstallPlan? shown = null;

        var result = await harness.Installer.InstallAsync(entry, Accept(plan => shown = plan));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(CatalogInstallAction.Install, result.Action);
        Assert.Equal(PluginId, result.PluginId);
        Assert.Null(result.FromVersion);
        Assert.Equal("1.0.0", result.ToVersion);
        var installDirectory = temp.Combine("plugins", PluginId);
        Assert.True(File.Exists(Path.Combine(installDirectory, "plugin.json")));
        Assert.True(File.Exists(Path.Combine(installDirectory, "Chart.dll")));
        var manifest = await PluginManifestReader.ReadAsync(installDirectory);
        Assert.Equal("1.0.0", manifest.Manifest!.Version);

        // The trust decision was surfaced before extraction, showing capabilities,
        // the package hash, and the optional signature.
        Assert.NotNull(shown);
        Assert.Equal(PluginId, shown!.PluginId);
        Assert.Equal("1.0.0", shown.Version);
        Assert.Equal(["renderer"], shown.Capabilities);
        Assert.Equal(entry.Version.Sha256, shown.Sha256, ignoreCase: true);
        Assert.Equal("signature-data", shown.Signature);
        Assert.Equal(entry.Source.Id, shown.SourceId);
        Assert.False(shown.IsUpdate);

        // The installed contents are not approved: the existing plugin manager
        // trust flow must run before the plugin can load.
        var trust = await PluginTrustStore.LoadAsync(harness.Paths.PluginsDirectory);
        Assert.Null(trust.Find(PluginId));
        Assert.Equal(PluginTrustDecision.Unknown, trust.Evaluate(PluginId, result.ContentHash!));

        // The temporary download does not linger in quarantine.
        Assert.Empty(Directory.GetFiles(harness.Paths.QuarantineDirectory));

        var audit = Assert.Single(await harness.Audit.ReadAsync());
        Assert.Equal("install", audit.Action);
        Assert.Equal(PluginId, audit.PluginId);
        Assert.Equal("success", audit.Result);
        Assert.Equal("1.0.0", audit.ToVersion);
        Assert.Equal(entry.Source.Id, audit.SourceId);
    }

    [Fact]
    public async Task AHashMismatchAbortsAndDeletesTheTemporaryFile()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var package = BuildPackage("1.0.0");
        harness.Respond(PackageUrl("1.0.0"), package);
        var entry = CreateEntry(package, "1.0.0", sha256: new string('a', 64));

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.False(result.Succeeded);
        Assert.Contains("hash", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(temp.Combine("plugins", PluginId)));
        Assert.Empty(Directory.GetFiles(harness.Paths.QuarantineDirectory));
        var audit = Assert.Single(await harness.Audit.ReadAsync());
        Assert.Equal("install", audit.Action);
        Assert.Equal("failure", audit.Result);
        Assert.Contains("hash", audit.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnEntryWithoutSha256IsRefusedBeforeAnyDownload()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var package = BuildPackage("1.0.0");
        harness.Respond(PackageUrl("1.0.0"), package);
        var entry = CreateEntry(package, "1.0.0", sha256: string.Empty);

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.False(result.Succeeded);
        Assert.Contains("SHA-256", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, harness.Handler.Requests);
        Assert.False(Directory.Exists(temp.Combine("plugins", PluginId)));
    }

    [Fact]
    public async Task APackageLargerThanTheCapIsRejectedAndDeleted()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp, maxBytes: 16);
        var package = BuildPackage("1.0.0");
        harness.Respond(PackageUrl("1.0.0"), package);
        var entry = CreateEntry(package, "1.0.0");

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.False(result.Succeeded);
        Assert.Contains("limit", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(temp.Combine("plugins", PluginId)));
        Assert.Empty(Directory.GetFiles(harness.Paths.QuarantineDirectory));
    }

    [Fact]
    public async Task APackageThatDoesNotMatchTheAdvertisedSizeIsRejected()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var package = BuildPackage("1.0.0");
        harness.Respond(PackageUrl("1.0.0"), package);
        var entry = CreateEntry(package, "1.0.0", size: package.Length + 1);

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.False(result.Succeeded);
        Assert.Contains("advertises", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(temp.Combine("plugins", PluginId)));
    }

    [Fact]
    public async Task APathTraversalEntryAbortsTheInstall()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var package = BuildPackage("1.0.0", [("../evil.txt", "escaped")]);
        harness.Respond(PackageUrl("1.0.0"), package);
        var entry = CreateEntry(package, "1.0.0");

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.False(result.Succeeded);
        Assert.Contains("unsafe path", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(temp.Combine("evil.txt")));
        Assert.False(File.Exists(temp.Combine("plugins", "evil.txt")));
        Assert.False(Directory.Exists(temp.Combine("plugins", PluginId)));
    }

    [Fact]
    public async Task AManifestWhoseIdDiffersFromTheCatalogEntryIsRejected()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var package = BuildPackage("1.0.0", manifestId: "com.other.plugin");
        harness.Respond(PackageUrl("1.0.0"), package);
        var entry = CreateEntry(package, "1.0.0");

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.False(result.Succeeded);
        Assert.Contains("com.other.plugin", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(temp.Combine("plugins", PluginId)));
    }

    [Fact]
    public async Task AMissingDependencyFailsWithAClearMessage()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var entry = harness.CreateEntry(
            "1.0.0",
            dependencies: [new CatalogDependency { Id = "com.example.shared", MinVersion = "2.0.0" }]);

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.False(result.Succeeded);
        Assert.Contains("com.example.shared", result.Error, StringComparison.Ordinal);
        Assert.Contains("2.0.0", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(temp.Combine("plugins", PluginId)));
    }

    [Fact]
    public async Task AnInstalledDependencyAtOrAboveTheMinimumSatisfiesTheRequirement()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        WriteInstalledPlugin(temp, "com.example.shared", "2.0.0");
        var entry = harness.CreateEntry(
            "1.0.0",
            dependencies: [new CatalogDependency { Id = "com.example.shared", MinVersion = "2.0.0" }]);

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.True(result.Succeeded, result.Error);
        Assert.True(Directory.Exists(temp.Combine("plugins", PluginId)));
    }

    [Fact]
    public async Task AnInstalledDependencyBelowTheMinimumFails()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        WriteInstalledPlugin(temp, "com.example.shared", "1.0.0");
        var entry = harness.CreateEntry(
            "1.0.0",
            dependencies: [new CatalogDependency { Id = "com.example.shared", MinVersion = "2.0.0" }]);

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.False(result.Succeeded);
        Assert.Contains("com.example.shared", result.Error, StringComparison.Ordinal);
        Assert.Contains("2.0.0", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnHttpDownloadUrlIsRejectedBeforeAnyRequest()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var package = BuildPackage("1.0.0");
        var entry = CreateEntry(package, "1.0.0", downloadUrl: "http://example.com/chart-1.0.0.ddplugin");

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.False(result.Succeeded);
        Assert.Contains("HTTPS", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, harness.Handler.Requests);
    }

    [Fact]
    public async Task DecliningTheTrustDecisionAbortsBeforeExtraction()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var entry = harness.CreateEntry("1.0.0");

        var result = await harness.Installer.InstallAsync(entry, Decline());

        Assert.False(result.Succeeded);
        Assert.Contains("trust", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(temp.Combine("plugins", PluginId)));
        Assert.Empty(Directory.GetFiles(harness.Paths.QuarantineDirectory));
        var audit = Assert.Single(await harness.Audit.ReadAsync());
        Assert.Equal("install", audit.Action);
        Assert.Equal("failure", audit.Result);
        Assert.Contains("trust", audit.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallingAnAlreadyInstalledPluginIsRefused()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var first = await InstallAsync(harness, "1.0.0");
        Assert.True(first.Succeeded, first.Error);
        var directoryStamp = File.ReadAllText(Path.Combine(temp.Combine("plugins", PluginId), "Chart.dll"));

        var second = await harness.Installer.InstallAsync(harness.CreateEntry("1.0.0"), Accept());

        Assert.False(second.Succeeded);
        Assert.Contains("already installed", second.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            directoryStamp,
            File.ReadAllText(Path.Combine(temp.Combine("plugins", PluginId), "Chart.dll")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdatingInstallsTheNewVersionAndKeepsTheOldForRollback()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        Assert.True((await InstallAsync(harness, "1.0.0")).Succeeded);
        var trust = await PluginTrustStore.LoadAsync(harness.Paths.PluginsDirectory);
        var approvedHash = await PluginContentHasher.ComputeAsync(temp.Combine("plugins", PluginId));
        await trust.ApproveAsync(PluginId, approvedHash, "Chart Renderer", "1.0.0");
        var update = harness.CreateEntry("1.1.0");
        CatalogInstallPlan? shown = null;

        var result = await harness.Installer.UpdateAsync(update, Accept(plan => shown = plan));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(CatalogInstallAction.Update, result.Action);
        Assert.Equal("1.0.0", result.FromVersion);
        Assert.Equal("1.1.0", result.ToVersion);
        var manifest = await PluginManifestReader.ReadAsync(temp.Combine("plugins", PluginId));
        Assert.Equal("1.1.0", manifest.Manifest!.Version);

        // The previous version is kept in the backup directory for rollback.
        var backupManifest = await PluginManifestReader.ReadAsync(temp.Combine("plugin-backups", PluginId));
        Assert.Equal("1.0.0", backupManifest.Manifest!.Version);
        Assert.True(File.Exists(Path.Combine(temp.Combine("plugin-backups", PluginId), "Chart.dll")));

        // The pinned hash still points at the old contents, so the next launch
        // reports the update as changed and prompts for a new trust decision.
        Assert.NotNull(shown);
        Assert.True(shown!.IsUpdate);
        Assert.Equal("1.0.0", shown.CurrentVersion);
        var reloaded = await PluginTrustStore.LoadAsync(harness.Paths.PluginsDirectory);
        Assert.Equal(approvedHash, reloaded.Find(PluginId)!.ContentHash);
        Assert.Equal(PluginTrustDecision.Changed, result.Trust);

        var audit = Assert.Single(await harness.Audit.ReadAsync(), entry => entry.Action == "update");
        Assert.Equal("1.0.0", audit.FromVersion);
        Assert.Equal("1.1.0", audit.ToVersion);
        Assert.Equal("success", audit.Result);
    }

    [Fact]
    public async Task RollingBackRestoresThePreviousVersionAndRemovesTheBackup()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        Assert.True((await InstallAsync(harness, "1.0.0")).Succeeded);
        var trust = await PluginTrustStore.LoadAsync(harness.Paths.PluginsDirectory);
        var approvedHash = await PluginContentHasher.ComputeAsync(temp.Combine("plugins", PluginId));
        await trust.ApproveAsync(PluginId, approvedHash, "Chart Renderer", "1.0.0");
        Assert.True((await harness.Installer.UpdateAsync(harness.CreateEntry("1.1.0"), Accept())).Succeeded);

        var result = await harness.Installer.RollbackAsync(PluginId);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(CatalogInstallAction.Rollback, result.Action);
        Assert.Equal("1.1.0", result.FromVersion);
        Assert.Equal("1.0.0", result.ToVersion);
        var manifest = await PluginManifestReader.ReadAsync(temp.Combine("plugins", PluginId));
        Assert.Equal("1.0.0", manifest.Manifest!.Version);
        Assert.True(File.Exists(Path.Combine(temp.Combine("plugins", PluginId), "Chart.dll")));

        // The backup was consumed by the rollback.
        Assert.False(Directory.Exists(temp.Combine("plugin-backups", PluginId)));

        // The restored contents match the pinned hash, so the plugin is trusted again.
        Assert.Equal(PluginTrustDecision.Trusted, result.Trust);
        var reloaded = await PluginTrustStore.LoadAsync(harness.Paths.PluginsDirectory);
        Assert.Equal(PluginTrustDecision.Trusted, reloaded.Evaluate(PluginId, result.ContentHash!));

        var audit = Assert.Single(await harness.Audit.ReadAsync(), entry => entry.Action == "rollback");
        Assert.Equal("1.1.0", audit.FromVersion);
        Assert.Equal("1.0.0", audit.ToVersion);
        Assert.Equal("success", audit.Result);
    }

    [Fact]
    public async Task AFailedUpdateLeavesThePreviousVersionWorking()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        Assert.True((await InstallAsync(harness, "1.0.0")).Succeeded);
        var trust = await PluginTrustStore.LoadAsync(harness.Paths.PluginsDirectory);
        var approvedHash = await PluginContentHasher.ComputeAsync(temp.Combine("plugins", PluginId));
        await trust.ApproveAsync(PluginId, approvedHash, "Chart Renderer", "1.0.0");
        var package = BuildPackage("1.1.0");
        harness.Respond(PackageUrl("1.1.0"), package);
        var update = CreateEntry(package, "1.1.0", sha256: new string('b', 64));

        var result = await harness.Installer.UpdateAsync(update, Accept());

        Assert.False(result.Succeeded);
        var manifest = await PluginManifestReader.ReadAsync(temp.Combine("plugins", PluginId));
        Assert.Equal("1.0.0", manifest.Manifest!.Version);
        Assert.False(Directory.Exists(temp.Combine("plugin-backups", PluginId)));
        Assert.Equal(
            approvedHash,
            await PluginContentHasher.ComputeAsync(temp.Combine("plugins", PluginId)));
        Assert.Empty(Directory.GetFiles(harness.Paths.QuarantineDirectory));
    }

    [Fact]
    public async Task UpdatingToTheSameOrOlderVersionIsRefused()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        Assert.True((await InstallAsync(harness, "1.0.0")).Succeeded);

        var same = await harness.Installer.UpdateAsync(harness.CreateEntry("1.0.0"), Accept());
        Assert.False(same.Succeeded);
        Assert.Contains("up to date", same.Error, StringComparison.OrdinalIgnoreCase);

        var older = await harness.Installer.UpdateAsync(harness.CreateEntry("0.9.0"), Accept());
        Assert.False(older.Succeeded);
        Assert.Contains("up to date", older.Error, StringComparison.OrdinalIgnoreCase);

        var missing = await harness.Installer.UpdateAsync(
            harness.CreateEntry("1.1.0", pluginId: "com.example.other"), Accept());
        Assert.False(missing.Succeeded);
        Assert.Contains("not installed", missing.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollingBackWithoutABackupIsRefused()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        Assert.True((await InstallAsync(harness, "1.0.0")).Succeeded);

        var result = await harness.Installer.RollbackAsync(PluginId);

        Assert.False(result.Succeeded);
        Assert.Contains("backup", result.Error, StringComparison.OrdinalIgnoreCase);
        var manifest = await PluginManifestReader.ReadAsync(temp.Combine("plugins", PluginId));
        Assert.Equal("1.0.0", manifest.Manifest!.Version);
    }

    [Fact]
    public async Task UninstallingRemovesThePluginDirectoryAndIsAudited()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        Assert.True((await InstallAsync(harness, "1.0.0")).Succeeded);

        var result = await harness.Installer.UninstallAsync(PluginId);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(CatalogInstallAction.Uninstall, result.Action);
        Assert.Equal("1.0.0", result.FromVersion);
        Assert.False(Directory.Exists(temp.Combine("plugins", PluginId)));
        var audit = Assert.Single(await harness.Audit.ReadAsync(), entry => entry.Action == "uninstall");
        Assert.Equal("success", audit.Result);
        Assert.Equal("1.0.0", audit.FromVersion);
    }

    [Fact]
    public async Task UninstallingAnUnknownPluginIsRefused()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);

        var result = await harness.Installer.UninstallAsync(PluginId);

        Assert.False(result.Succeeded);
        Assert.Contains("not installed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovingASourceLeavesInstalledPluginsInPlace()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var entry = harness.CreateEntry("1.0.0");
        Assert.True((await harness.Installer.InstallAsync(entry, Accept())).Succeeded);
        var store = await CatalogSourceStore.LoadAsync(harness.Paths.CatalogDirectory);
        var source = (await store.AddAsync("Team", entry.Source.Url)).Source!;

        Assert.True(await store.RemoveAsync(source.Id));

        Assert.True(Directory.Exists(temp.Combine("plugins", PluginId)));
        var manifest = await PluginManifestReader.ReadAsync(temp.Combine("plugins", PluginId));
        Assert.Equal("1.0.0", manifest.Manifest!.Version);
    }

    [Fact]
    public async Task InstalledAndRollbackScansReportVersions()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        Assert.True((await InstallAsync(harness, "1.0.0")).Succeeded);
        Assert.True((await harness.Installer.UpdateAsync(harness.CreateEntry("1.1.0"), Accept())).Succeeded);

        var installed = await harness.Installer.GetInstalledAsync();
        var entry = Assert.Single(installed, plugin => plugin.PluginId == PluginId);
        Assert.Equal("1.1.0", entry.Version);

        var rollback = await harness.Installer.GetRollbackVersionsAsync();
        Assert.Equal("1.0.0", rollback[PluginId]);
    }

    [Fact]
    public async Task AServerErrorIsReportedAsAFailedDownload()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        harness.Handler.Respond(
            PackageUrl("1.0.0"),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var package = BuildPackage("1.0.0");
        var entry = CreateEntry(package, "1.0.0");

        var result = await harness.Installer.InstallAsync(entry, Accept());

        Assert.False(result.Succeeded);
        Assert.Contains("500", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(temp.Combine("plugins", PluginId)));
    }

    private static async Task<CatalogInstallResult> InstallAsync(Harness harness, string version)
    {
        var result = await harness.Installer.InstallAsync(harness.CreateEntry(version), Accept());
        Assert.True(result.Succeeded, result.Error);
        return result;
    }

    private static void WriteInstalledPlugin(TempDirectory temp, string pluginId, string version)
    {
        var directory = temp.CreateDirectory("plugins", pluginId);
        File.WriteAllText(
            Path.Combine(directory, "plugin.json"),
            $$"""
            {
              "id": "{{pluginId}}",
              "name": "Shared Library",
              "version": "{{version}}",
              "apiVersion": "1.0",
              "entryAssembly": "Shared.dll",
              "entryType": "Shared.SharedPlugin"
            }
            """);
        File.WriteAllText(Path.Combine(directory, "Shared.dll"), "assembly");
    }

    private static CatalogTrustDecision Accept(Action<CatalogInstallPlan>? capture = null) =>
        plan =>
        {
            capture?.Invoke(plan);
            return Task.FromResult(true);
        };

    private static CatalogTrustDecision Decline() => plan => Task.FromResult(false);

    private static string PackageUrl(string version) => $"https://example.com/chart-{version}.ddplugin";

    private static string HashOf(byte[] package) =>
        Convert.ToHexString(SHA256.HashData(package));

    private static CatalogEntry CreateEntry(
        byte[] package,
        string version,
        string? sha256 = null,
        long? size = null,
        string? downloadUrl = null,
        string pluginId = PluginId,
        IReadOnlyList<CatalogDependency>? dependencies = null)
    {
        return new CatalogEntry
        {
            Source = new CatalogSource { Id = "src-1", Name = "Team", Url = "https://example.com/catalog.json" },
            Plugin = new CatalogPlugin { Id = pluginId, Name = "Chart Renderer" },
            Version = new CatalogVersion
            {
                Version = version,
                ApiVersion = "1.0",
                DownloadUrl = downloadUrl ?? PackageUrl(version),
                Sha256 = sha256 ?? HashOf(package),
                Size = size ?? package.Length,
                Signature = "signature-data",
                Capabilities = ["renderer"],
                Dependencies = dependencies is null ? [] : [.. dependencies],
            },
            Compatibility = CatalogCompatibility.Compatible,
            CompatibilityLabel = "Compatible",
            IsPrerelease = false,
        };
    }

    private static byte[] BuildPackage(
        string version,
        (string Name, string Contents)[]? extraFiles = null,
        string? manifestId = null)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "plugin.json", ManifestJson(version, manifestId));
            AddEntry(archive, "Chart.dll", $"assembly {version}");
            foreach (var (name, contents) in extraFiles ?? [])
            {
                AddEntry(archive, name, contents);
            }
        }

        return buffer.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(contents));
    }

    private static string ManifestJson(string version, string? manifestId = null) => $$"""
        {
          "id": "{{manifestId ?? PluginId}}",
          "name": "Chart Renderer",
          "version": "{{version}}",
          "apiVersion": "1.0",
          "entryAssembly": "Chart.dll",
          "entryType": "Chart.ChartPlugin",
          "capabilities": ["renderer"]
        }
        """;

    private static Harness CreateHarness(TempDirectory temp, int maxBytes = PluginCatalogClient.DefaultMaxBytes)
    {
        var handler = new RecordingHandler();
        var paths = new AppPaths(temp.Path);
        var audit = new PluginAuditLog(temp.Combine("logs", "plugin-audit.jsonl"));
        var installer = new CatalogInstaller(
            new HttpClient(handler),
            paths,
            audit,
            maxBytes: maxBytes);
        return new Harness(temp, paths, audit, installer, handler);
    }

    private sealed record Harness(
        TempDirectory Temp,
        AppPaths Paths,
        PluginAuditLog Audit,
        CatalogInstaller Installer,
        RecordingHandler Handler)
    {
        public void Respond(string url, byte[] body) => Handler.Respond(url, body);

        public void Respond(string url, Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            Handler.Respond(url, responder);

        public CatalogEntry CreateEntry(
            string version,
            string? pluginId = null,
            IReadOnlyList<CatalogDependency>? dependencies = null)
        {
            var package = BuildPackage(version);
            Handler.Respond(PackageUrl(version), package);
            return CatalogInstallerTests.CreateEntry(
                package,
                version,
                pluginId: pluginId ?? PluginId,
                dependencies: dependencies);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _responses =
            new(StringComparer.Ordinal);

        public int Requests { get; private set; }

        public void Respond(string url, byte[] body) =>
            _responses[url] = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };

        public void Respond(string url, Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responses[url] = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            var url = request.RequestUri!.ToString();
            if (_responses.TryGetValue(url, out var responder))
            {
                return Task.FromResult(responder(request));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}

using DroidDeck.Core;
using DroidDeck.Plugins.Catalog;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests.Catalog;

public sealed class PluginCatalogServiceTests
{
    [Fact]
    public async Task RefreshLoadsEntriesFromEveryEnabledSource()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteFile("team/first.json", CatalogDocumentTests.ValidCatalogJson);
        var second = temp.WriteFile(
            "team/second.json",
            CatalogDocumentTests.ValidCatalogJson.Replace("com.example.chart", "com.example.detector", StringComparison.Ordinal));
        var service = await CreateServiceAsync(temp);

        Assert.True((await service.AddSourceAsync("First", first)).Succeeded);
        Assert.True((await service.AddSourceAsync("Second", second)).Succeeded);
        var refresh = await service.RefreshAsync();

        Assert.True(refresh.Succeeded, string.Join("; ", refresh.Errors.Select(error => error.Message)));
        Assert.Equal(2, service.Browse(new CatalogQuery()).Count);
        Assert.All(service.SourceStatuses, status => Assert.False(status.IsFromCache));
    }

    [Fact]
    public async Task CachedCatalogsAreBrowsableOffline()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("team/catalog.json", CatalogDocumentTests.ValidCatalogJson);
        var online = await CreateServiceAsync(temp);
        await online.AddSourceAsync("Team", file);
        Assert.True((await online.RefreshAsync()).Succeeded);

        File.Delete(file);
        var offline = await CreateServiceAsync(temp);

        Assert.Single(offline.Browse(new CatalogQuery()));
        var refresh = await offline.RefreshAsync();

        Assert.False(refresh.Succeeded);
        Assert.Contains(refresh.Errors, error => error.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
        Assert.Single(offline.Browse(new CatalogQuery()));
        Assert.True(Assert.Single(offline.SourceStatuses).IsFromCache);
    }

    [Fact]
    public async Task AnUnreachableSourceReportsAnErrorAndKeepsOtherSources()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("team/catalog.json", CatalogDocumentTests.ValidCatalogJson);
        var service = await CreateServiceAsync(
            temp,
            new StubHandler(_ => throw new HttpRequestException("No such host is known.")));
        await service.AddSourceAsync("Team", file);
        await service.AddSourceAsync("Remote", "https://plugins.example.com/catalog.json");

        var refresh = await service.RefreshAsync();

        Assert.False(refresh.Succeeded);
        var error = Assert.Single(refresh.Errors);
        Assert.Contains("plugins.example.com", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(service.Browse(new CatalogQuery()));
        Assert.Contains(
            service.SourceStatuses,
            status => status.Source.Name == "Remote" && status.Error is not null);
    }

    [Fact]
    public async Task AnInvalidCatalogSchemaIsReportedWithoutEntries()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("team/catalog.json", """{ "schemaVersion": 2, "plugins": [] }""");
        var service = await CreateServiceAsync(temp);
        await service.AddSourceAsync("Team", file);

        var refresh = await service.RefreshAsync();

        Assert.False(refresh.Succeeded);
        Assert.Contains(refresh.Errors, error => error.Message.Contains("schema", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(service.Browse(new CatalogQuery()));
    }

    [Fact]
    public async Task AddingASourceAndBrowsingNeverTouchesThePluginDirectory()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("team/catalog.json", CatalogDocumentTests.ValidCatalogJson);
        var paths = new AppPaths(temp.Path);
        var service = await CreateServiceAsync(temp);
        await service.AddSourceAsync("Team", file);
        await service.RefreshAsync();
        service.Browse(new CatalogQuery());

        Assert.False(Directory.Exists(paths.PluginsDirectory));
    }

    [Fact]
    public async Task DisabledSourcesAreNotBrowsedAndReloadFromCacheWhenEnabled()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("team/catalog.json", CatalogDocumentTests.ValidCatalogJson);
        var service = await CreateServiceAsync(temp);
        var source = (await service.AddSourceAsync("Team", file)).Source!;
        await service.RefreshAsync();
        Assert.Single(service.Browse(new CatalogQuery()));

        Assert.True(await service.SetSourceEnabledAsync(source.Id, enabled: false));
        Assert.Empty(service.Browse(new CatalogQuery()));

        Assert.True(await service.SetSourceEnabledAsync(source.Id, enabled: true));
        Assert.Single(service.Browse(new CatalogQuery()));
    }

    [Fact]
    public async Task RemovingASourceHidesItsCachedEntries()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("team/catalog.json", CatalogDocumentTests.ValidCatalogJson);
        var service = await CreateServiceAsync(temp);
        var source = (await service.AddSourceAsync("Team", file)).Source!;
        await service.RefreshAsync();

        Assert.True(await service.RemoveSourceAsync(source.Id));

        Assert.Empty(service.Sources);
        Assert.Empty(service.Browse(new CatalogQuery()));
    }

    [Fact]
    public async Task SearchCanBeScopedToOneSource()
    {
        using var temp = new TempDirectory();
        var first = temp.WriteFile("team/first.json", CatalogDocumentTests.ValidCatalogJson);
        var second = temp.WriteFile(
            "team/second.json",
            CatalogDocumentTests.ValidCatalogJson.Replace("com.example.chart", "com.example.detector", StringComparison.Ordinal));
        var service = await CreateServiceAsync(temp);
        var firstSource = (await service.AddSourceAsync("First", first)).Source!;
        await service.AddSourceAsync("Second", second);
        await service.RefreshAsync();

        var entries = service.Browse(new CatalogQuery { SourceId = firstSource.Id });

        Assert.Equal("com.example.chart", Assert.Single(entries).Plugin.Id);
    }

    [Fact]
    public async Task BrowsingExposesTagsWithoutCaseDuplicates()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile(
            "team/catalog.json",
            CatalogDocumentTests.ValidCatalogJson.Replace(
                "\"tags\": [\"renderer\", \"charts\"]",
                "\"tags\": [\"renderer\", \"charts\", \"Charts\"]",
                StringComparison.Ordinal));
        var service = await CreateServiceAsync(temp);
        await service.AddSourceAsync("Team", file);
        await service.RefreshAsync();

        Assert.Equal(["charts", "renderer"], service.AllTags);
    }

    private static async Task<PluginCatalogService> CreateServiceAsync(
        TempDirectory temp,
        HttpMessageHandler? handler = null)
    {
        var service = new PluginCatalogService(
            new AppPaths(temp.Path),
            new PluginCatalogClient(
                new HttpClient(handler ?? new StubHandler(_ => throw new InvalidOperationException("HTTP is not expected."))),
                new AppPaths(temp.Path).CatalogDirectory,
                userAgent: "DroidDeck/1.0.0"),
            hostVersion: "1.0.0",
            hostApiVersion: "1.0");
        await service.InitializeAsync();
        return service;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}

using System.Net;
using System.Text;
using DroidDeck.Plugins.Catalog;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests.Catalog;

public sealed class PluginCatalogClientTests
{
    [Fact]
    public async Task FetchingAnHttpsSourceParsesAndCachesTheCatalog()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(request =>
        {
            Assert.Equal("DroidDeck/1.2.3", request.Headers.UserAgent.ToString());
            return Json(CatalogDocumentTests.ValidCatalogJson);
        });
        var client = CreateClient(temp, handler);

        var result = await client.FetchAsync(Source());

        Assert.True(result.Succeeded, result.Error);
        Assert.False(result.IsFromCache);
        Assert.Equal("com.example.chart", Assert.Single(result.Catalog!.Plugins).Id);
        Assert.True(File.Exists(client.GetCachePath(Source())));
    }

    [Fact]
    public async Task AnHttpErrorIsReportedClearly()
    {
        using var temp = new TempDirectory();
        var client = CreateClient(
            temp,
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await client.FetchAsync(Source());

        Assert.False(result.Succeeded);
        Assert.Null(result.Catalog);
        Assert.Contains("500", result.Error, StringComparison.Ordinal);
        Assert.Contains("could not", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARedirectToANonHttpsLocationIsRejectedAndNotCached()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // SocketsHttpHandler records the final address after a redirect here.
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://example.com/catalog.json"),
            Content = new StringContent(CatalogDocumentTests.ValidCatalogJson, Encoding.UTF8, "application/json"),
        });
        var client = CreateClient(temp, handler);

        var result = await client.FetchAsync(Source());

        Assert.False(result.Succeeded);
        Assert.Contains("HTTPS", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(client.GetCachePath(Source())));
    }

    [Fact]
    public async Task AnInvalidSchemaIsRejectedAndNotCached()
    {
        using var temp = new TempDirectory();
        const string badSchema = """{ "schemaVersion": 9, "plugins": [] }""";
        var client = CreateClient(temp, new StubHandler(_ => Json(badSchema)));

        var result = await client.FetchAsync(Source());

        Assert.False(result.Succeeded);
        Assert.Contains("schema", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(client.GetCachePath(Source())));
    }

    [Fact]
    public async Task WhenTheNetworkFailsTheCachedCatalogIsUsed()
    {
        using var temp = new TempDirectory();
        var online = CreateClient(temp, new StubHandler(_ => Json(CatalogDocumentTests.ValidCatalogJson)));
        Assert.True((await online.FetchAsync(Source())).Succeeded);
        var offline = CreateClient(
            temp,
            new StubHandler(_ => throw new HttpRequestException("The network is down.")));

        var result = await offline.FetchAsync(Source());

        Assert.True(result.Succeeded, result.Error);
        Assert.True(result.IsFromCache);
        Assert.NotNull(result.FetchedAt);
        Assert.Contains("cached", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Single(result.Catalog!.Plugins);
    }

    [Fact]
    public async Task AnUnreachableSourceWithoutACacheIsAClearError()
    {
        using var temp = new TempDirectory();
        var client = CreateClient(
            temp,
            new StubHandler(_ => throw new HttpRequestException("No such host is known.")));

        var result = await client.FetchAsync(Source());

        Assert.False(result.Succeeded);
        Assert.Contains("example.com", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No such host", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ALocalFileSourceIsReadWithoutHttp()
    {
        using var temp = new TempDirectory();
        var file = temp.WriteFile("team/catalog.json", CatalogDocumentTests.ValidCatalogJson);
        var client = CreateClient(temp, new StubHandler(_ => throw new InvalidOperationException("HTTP must not be used.")));
        var source = new CatalogSource
        {
            Id = "src-local",
            Name = "Team feed",
            Url = file,
        };

        var result = await client.FetchAsync(source);

        Assert.True(result.Succeeded, result.Error);
        Assert.Single(result.Catalog!.Plugins);
        Assert.True(File.Exists(client.GetCachePath(source)));
    }

    [Fact]
    public async Task AnOversizedCatalogIsRejected()
    {
        using var temp = new TempDirectory();
        var client = new PluginCatalogClient(
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string(' ', 4096), Encoding.UTF8, "application/json"),
            })),
            temp.Combine("catalog"),
            maxBytes: 1024,
            userAgent: "DroidDeck/1.2.3");

        var result = await client.FetchAsync(Source());

        Assert.False(result.Succeeded);
        Assert.Contains("too large", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static PluginCatalogClient CreateClient(TempDirectory temp, HttpMessageHandler handler) =>
        new(new HttpClient(handler), temp.Combine("catalog"), userAgent: "DroidDeck/1.2.3");

    private static CatalogSource Source() => new()
    {
        Id = "src-0123456789ab",
        Name = "Example",
        Url = "https://example.com/catalog.json",
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}

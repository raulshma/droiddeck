using System.Net;
using System.Text;
using DroidDeck.Core.Updates;

namespace DroidDeck.Core.Tests.Updates;

public sealed class UpdateCheckerTests
{
    private static readonly Uri Feed = new("https://api.example.test/releases/latest");

    [Fact]
    public async Task DisabledCheckMakesNoRequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("No request expected."));
        var checker = new UpdateChecker(new HttpClient(handler), "1.0.0", Feed);

        var result = await checker.CheckAsync(enabled: false);

        Assert.Equal(UpdateCheckStatus.Disabled, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task NewerReleaseIsReportedWithTheReleasePage()
    {
        var json = ReleaseJson("v1.2.0", "https://github.com/raulshma/droiddeck/releases/tag/v1.2.0");
        var checker = CreateChecker(json);

        var result = await checker.CheckAsync(enabled: true);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("1.2.0", result.LatestVersion);
        Assert.Equal(new Uri("https://github.com/raulshma/droiddeck/releases/tag/v1.2.0"), result.ReleasePage);
        Assert.Contains("1.2.0", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SameVersionIsUpToDate()
    {
        var checker = CreateChecker(ReleaseJson("1.0.0", "https://example.test/release"));

        var result = await checker.CheckAsync(enabled: true);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.False(result.UpdateAvailable);
        Assert.Contains("latest", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OlderReleaseIsUpToDate()
    {
        var checker = CreateChecker(ReleaseJson("0.9.9", null));

        var result = await checker.CheckAsync(enabled: true);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.ReleasePage);
    }

    [Fact]
    public async Task FollowsHttpsRedirects()
    {
        var requests = 0;
        var handler = new StubHandler(request =>
        {
            requests++;
            if (requests == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://api.example.test/moved/latest");
                return redirect;
            }

            Assert.Equal(new Uri("https://api.example.test/moved/latest"), request.RequestUri);
            return JsonResponse(ReleaseJson("1.1.0", null));
        });
        var checker = new UpdateChecker(new HttpClient(handler), "1.0.0", Feed);

        var result = await checker.CheckAsync(enabled: true);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task RejectsRedirectsToHttp()
    {
        var handler = new StubHandler(_ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("http://api.example.test/latest");
            return redirect;
        });
        var checker = new UpdateChecker(new HttpClient(handler), "1.0.0", Feed);

        var result = await checker.CheckAsync(enabled: true);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RejectsNonHttpsFeedWithoutARequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("No request expected."));
        var checker = new UpdateChecker(new HttpClient(handler), "1.0.0", new Uri("http://example.test/latest"));

        var result = await checker.CheckAsync(enabled: true);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task NoPublishedStableReleaseIsUpToDate()
    {
        var checker = CreateChecker(string.Empty, HttpStatusCode.NotFound);

        var result = await checker.CheckAsync(enabled: true);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.False(result.UpdateAvailable);
        Assert.Null(result.LatestVersion);
        Assert.Contains("stable release", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportsHttpFailures()
    {
        var checker = CreateChecker(string.Empty, HttpStatusCode.Forbidden);

        var result = await checker.CheckAsync(enabled: true);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("403", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":"banana"}""")]
    public async Task RejectsUnreadableReleaseDocuments(string json)
    {
        var checker = CreateChecker(json);

        var result = await checker.CheckAsync(enabled: true);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task RejectsOversizedResponses()
    {
        var payload = new string('x', (int)UpdateChecker.MaximumResponseBytes + 1);
        var checker = CreateChecker(payload);

        var result = await checker.CheckAsync(enabled: true);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    private static UpdateChecker CreateChecker(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(
            new HttpClient(new StubHandler(_ => JsonResponse(json, statusCode))),
            "1.0.0",
            Feed);

    private static string ReleaseJson(string tag, string? page)
    {
        var pageJson = page is null ? "null" : $"\"{page}\"";
        return $"{{\"tag_name\":\"{tag}\",\"html_url\":{pageJson}}}";
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }
}

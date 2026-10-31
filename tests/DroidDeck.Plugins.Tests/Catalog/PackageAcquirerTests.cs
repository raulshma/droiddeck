using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using DroidDeck.Plugins.Catalog;
using DroidDeck.TestSupport;

namespace DroidDeck.Plugins.Tests.Catalog;

public sealed class PackageAcquirerTests
{
    private const string PluginId = "com.example.package";
    private const string DownloadUrl = "https://example.com/package.ddplugin";

    [Fact]
    public async Task APackageAboveTheAdvertisedSizeLimitIsRejectedBeforeAnyRequest()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler();
        var acquirer = new PackageAcquirer(new HttpClient(handler), maxBytes: 16);
        var entry = CreateEntry([1, 2, 3], size: 17);
        var destination = temp.Combine("package.ddplugin");

        var error = await acquirer.AcquireAsync(entry, destination);

        Assert.NotNull(error);
        Assert.Contains("advertised", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Requests);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task ARedirectToHttpIsRejectedWithoutFollowingIt()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler();
        handler.Respond(DownloadUrl, _ => Redirect("http://evil.example.com/package.ddplugin"));
        var acquirer = new PackageAcquirer(new HttpClient(handler));
        var entry = CreateEntry([1, 2, 3]);

        var error = await acquirer.AcquireAsync(entry, temp.Combine("package.ddplugin"));

        Assert.NotNull(error);
        Assert.Contains("non-HTTPS", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task ARedirectLoopIsRejectedAfterTheHopLimit()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler();
        handler.Respond(DownloadUrl, _ => Redirect("https://mirror.example.com/package.ddplugin"));
        handler.Respond("https://mirror.example.com/package.ddplugin", _ => Redirect(DownloadUrl));
        var acquirer = new PackageAcquirer(new HttpClient(handler));
        var entry = CreateEntry([1, 2, 3]);

        var error = await acquirer.AcquireAsync(entry, temp.Combine("package.ddplugin"));

        Assert.NotNull(error);
        Assert.Contains("redirected too many times", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, handler.Requests);
    }

    [Fact]
    public async Task AContentLengthAboveTheLimitIsRejectedBeforeReadingTheBody()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler();
        handler.Respond(DownloadUrl, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream([1])),
            };
            response.Content.Headers.ContentLength = 100;
            return response;
        });
        var acquirer = new PackageAcquirer(new HttpClient(handler), maxBytes: 16);
        var entry = CreateEntry([1], size: 1);

        var error = await acquirer.AcquireAsync(entry, temp.Combine("package.ddplugin"));

        Assert.NotNull(error);
        Assert.Contains("too large (100 bytes)", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStreamedBodyAboveTheLimitIsRejectedWithoutAContentLength()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler();
        handler.Respond(DownloadUrl, _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new byte[100])),
        });
        var acquirer = new PackageAcquirer(new HttpClient(handler), maxBytes: 16);
        var entry = CreateEntry(new byte[100], size: 0);

        var error = await acquirer.AcquireAsync(entry, temp.Combine("package.ddplugin"));

        Assert.NotNull(error);
        Assert.Contains("too large; the limit is 16 bytes", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyThatDoesNotMatchTheAdvertisedSizeIsRejected()
    {
        using var temp = new TempDirectory();
        var body = Encoding.UTF8.GetBytes("package");
        var handler = new StubHandler();
        handler.Respond(DownloadUrl, body);
        var acquirer = new PackageAcquirer(new HttpClient(handler));
        var entry = CreateEntry(body, size: body.Length + 1);

        var error = await acquirer.AcquireAsync(entry, temp.Combine("package.ddplugin"));

        Assert.NotNull(error);
        Assert.Contains("advertises", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHashMismatchIsRejected()
    {
        using var temp = new TempDirectory();
        var body = Encoding.UTF8.GetBytes("package");
        var handler = new StubHandler();
        handler.Respond(DownloadUrl, body);
        var acquirer = new PackageAcquirer(new HttpClient(handler));
        var entry = CreateEntry(body, sha256: new string('a', 64));

        var error = await acquirer.AcquireAsync(entry, temp.Combine("package.ddplugin"));

        Assert.NotNull(error);
        Assert.Contains("hash does not match", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AHashComparisonIgnoresCaseAndReturnsTheDownloadedPackage()
    {
        using var temp = new TempDirectory();
        var body = Encoding.UTF8.GetBytes("package");
        var handler = new StubHandler();
        handler.Respond(DownloadUrl, body);
        var acquirer = new PackageAcquirer(new HttpClient(handler));
        var entry = CreateEntry(body, sha256: HashOf(body).ToLowerInvariant());
        var destination = temp.Combine("package.ddplugin");

        var error = await acquirer.AcquireAsync(entry, destination);

        Assert.Null(error);
        Assert.Equal(body, await File.ReadAllBytesAsync(destination));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("dir/evil.txt:stream")]
    [InlineData("CON.txt")]
    [InlineData("libs/COM1.dll")]
    [InlineData("plugin.json.")]
    [InlineData("plugin.json ")]
    [InlineData("libs/Chart.dll.")]
    public async Task UnsafeEntryNamesAreRejectedBeforeAnythingIsWritten(string entryName)
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("extract");
        using var archive = OpenArchive(("plugin.json", "{}"), (entryName, "escaped"));

        var error = await PackageAcquirer.ExtractAsync(archive, PluginId, destination);

        Assert.NotNull(error);
        Assert.Contains("unsafe path", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(temp.Combine("evil.txt")));
    }

    [Fact]
    public async Task DuplicateEntryNamesAreRejected()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("extract");
        using var archive = OpenArchive(("plugin.json", "{}"), ("plugin.json", "{}"));

        var error = await PackageAcquirer.ExtractAsync(archive, PluginId, destination);

        Assert.NotNull(error);
        Assert.Contains("more than once", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("plugin.json", "PLUGIN.JSON")]
    [InlineData("lib/Chart.dll", "lib\\Chart.dll")]
    [InlineData("lib/Chart.dll", "lib/./Chart.dll")]
    public async Task EntryNameAliasesForTheSameFileAreRejectedAsDuplicates(string first, string second)
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("extract");
        using var archive = OpenArchive((first, "first"), (second, "second"));

        var error = await PackageAcquirer.ExtractAsync(archive, PluginId, destination);

        Assert.NotNull(error);
        Assert.Contains("more than once", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task AnArchiveAboveTheExtractionCapIsRejectedAndCleanedUp()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("extract");
        using var archive = CreateBombArchive(megabytes: 65);

        var error = await PackageAcquirer.ExtractAsync(archive, PluginId, destination);

        Assert.NotNull(error);
        Assert.Contains("unpacks to more than", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task AFileAndDirectoryCollisionIsRejectedAndCleanedUp()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("extract");
        using var archive = OpenArchive(("data", "a file"), ("data/inner.txt", "inside"));

        var error = await PackageAcquirer.ExtractAsync(archive, PluginId, destination);

        Assert.NotNull(error);
        Assert.Contains("could not be extracted", error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task AValidArchiveExtractsEveryFile()
    {
        using var temp = new TempDirectory();
        var destination = temp.Combine("extract");
        using var archive = OpenArchive(
            ("plugin.json", """{"id":"com.example.package"}"""),
            ("lib/Chart.dll", "assembly"),
            ("data/readme.txt", "hello"));

        var error = await PackageAcquirer.ExtractAsync(archive, PluginId, destination);

        Assert.Null(error);
        Assert.Equal(
            """{"id":"com.example.package"}""",
            await File.ReadAllTextAsync(Path.Combine(destination, "plugin.json")));
        Assert.Equal(
            "assembly",
            await File.ReadAllTextAsync(Path.Combine(destination, "lib", "Chart.dll")));
        Assert.Equal(
            "hello",
            await File.ReadAllTextAsync(Path.Combine(destination, "data", "readme.txt")));
    }

    private static CatalogEntry CreateEntry(
        byte[] body,
        long? size = null,
        string? sha256 = null) => new()
        {
            Source = new CatalogSource
            {
                Id = "src-1",
                Name = "Test",
                Url = "https://example.com/catalog.json",
            },
            Plugin = new CatalogPlugin { Id = PluginId, Name = "Package" },
            Version = new CatalogVersion
            {
                Version = "1.0.0",
                ApiVersion = "1.0",
                DownloadUrl = DownloadUrl,
                Sha256 = sha256 ?? HashOf(body),
                Size = size ?? body.Length,
            },
            Compatibility = CatalogCompatibility.Compatible,
            CompatibilityLabel = "Compatible",
            IsPrerelease = false,
        };

    private static string HashOf(byte[] body) => Convert.ToHexString(SHA256.HashData(body));

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location) },
        Content = new ByteArrayContent([]),
    };

    private static ZipArchive OpenArchive(params (string Name, string Contents)[] entries)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, contents) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(contents));
            }
        }

        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    private static ZipArchive CreateBombArchive(int megabytes)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("payload.bin");
            using var stream = entry.Open();
            var megabyte = new byte[1024 * 1024];
            for (var index = 0; index < megabytes; index++)
            {
                stream.Write(megabyte);
            }
        }

        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    private sealed class NonSeekableStream : MemoryStream
    {
        public NonSeekableStream(byte[] buffer)
            : base(buffer)
        {
        }

        public override bool CanSeek => false;
    }

    private sealed class StubHandler : HttpMessageHandler
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

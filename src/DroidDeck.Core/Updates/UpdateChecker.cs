using System.Buffers;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DroidDeck.Core.Infrastructure;
using DroidDeck.Plugin.Abstractions;

namespace DroidDeck.Core.Updates;

/// <summary>The outcome of a manual update check.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The user has not allowed update checks, so nothing was requested.</summary>
    Disabled,

    /// <summary>A newer release is available.</summary>
    UpdateAvailable,

    /// <summary>The running version is the latest release.</summary>
    UpToDate,

    /// <summary>The check could not complete.</summary>
    Failed,
}

/// <summary>The result of a manual update check, including the message shown to the user.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Message">A short, user-facing explanation.</param>
/// <param name="LatestVersion">The released version, when one could be read.</param>
/// <param name="ReleasePage">The HTTPS release page, when the feed supplied one.</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string Message,
    string? LatestVersion = null,
    Uri? ReleasePage = null)
{
    /// <summary>Gets whether the release feed was read successfully.</summary>
    public bool Succeeded => Status is UpdateCheckStatus.UpdateAvailable or UpdateCheckStatus.UpToDate;

    /// <summary>Gets whether a newer release is available.</summary>
    public bool UpdateAvailable => Status == UpdateCheckStatus.UpdateAvailable;
}

/// <summary>
/// Checks the public release feed for a newer version. The check only runs when
/// the user explicitly asks for it and has opted in; DroidDeck never polls.
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>The largest release document the checker accepts.</summary>
    public const long MaximumResponseBytes = 512 * 1024;

    /// <summary>The maximum number of HTTPS redirects the checker follows.</summary>
    public const int MaximumRedirects = 3;

    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly Uri _releaseApiUri;

    /// <summary>Creates an update checker.</summary>
    public UpdateChecker(HttpClient http, string currentVersion, Uri? releaseApiUri = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        _currentVersion = currentVersion;
        _releaseApiUri = releaseApiUri ?? new Uri(AppInfo.LatestReleaseApiUrl);
    }

    /// <summary>
    /// Checks the release feed when manual checks are enabled. A disabled check
    /// makes no network request.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Disabled,
                "Update checks are off. Enable manual checks to look for a newer release.");
        }

        if (!SemanticVersion.TryParse(_currentVersion, out var current))
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                "The running version could not be compared with the release feed.");
        }

        HttpResponseMessage response;
        try
        {
            var fetch = await FetchAsync(cancellationToken).ConfigureAwait(false);
            if (fetch.Response is null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, fetch.Error ?? "The update check failed.");
            }

            response = fetch.Response;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException &&
            !cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                $"The release feed could not be reached: {exception.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpToDate,
                    "No stable release has been published yet.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    $"The release feed returned HTTP {(int)response.StatusCode}.");
            }

            byte[] payload;
            try
            {
                payload = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or TaskCanceledException &&
                !cancellationToken.IsCancellationRequested)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    $"The release feed could not be read: {exception.Message}");
            }

            if (payload.Length == 0)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    "The release feed returned an empty document.");
            }

            GitHubRelease? release;
            try
            {
                release = JsonSerializer.Deserialize(payload, UpdateJsonContext.Default.GitHubRelease);
            }
            catch (JsonException)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    "The release feed returned an unexpected document.");
            }

            var tag = release?.TagName?.Trim();
            if (string.IsNullOrEmpty(tag) || !SemanticVersion.TryParse(TrimTagPrefix(tag), out var latest))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    "The release feed did not include a readable version.");
            }

            var page = TryGetHttpsUri(release?.HtmlUrl);
            if (latest.CompareTo(current) > 0)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.UpdateAvailable,
                    $"DroidDeck {latest} is available. You are running {current}.",
                    latest.ToString(),
                    page);
            }

            return new UpdateCheckResult(
                UpdateCheckStatus.UpToDate,
                $"DroidDeck {current} is the latest release.",
                latest.ToString(),
                page);
        }
    }

    private async Task<(HttpResponseMessage? Response, string? Error)> FetchAsync(CancellationToken cancellationToken)
    {
        var uri = _releaseApiUri;
        for (var redirects = 0; redirects <= MaximumRedirects; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                return (null, "The release feed must use HTTPS.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd($"DroidDeck/{_currentVersion}");

            var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                return (response, null);
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                return (null, "The release feed redirected without a destination.");
            }

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }

        return (null, "The release feed redirected too many times.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static async Task<byte[]> ReadLimitedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > MaximumResponseBytes)
        {
            return [];
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(rented, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumResponseBytes)
                {
                    return [];
                }

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return buffer.ToArray();
    }

    private static string TrimTagPrefix(string tag) =>
        tag.Length > 0 && (tag[0] == 'v' || tag[0] == 'V') ? tag[1..] : tag;

    private static Uri? TryGetHttpsUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }
}

/// <summary>Source-generated JSON metadata for the release feed.</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(UpdateChecker.GitHubRelease))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext
{
}

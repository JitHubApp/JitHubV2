using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Markdown.Images;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;

namespace JitHub.Services;

/// <summary>
/// Downloads one remote image over HTTPS with conditional revalidation, bounded
/// redirects that each pass the source policy, admitted byte reads, and a
/// bounded transient retry.
/// </summary>
/// <remarks>
/// <c>partial</c> only so CsWinRT can generate its ABI projection of IDisposable.
/// </remarks>
internal sealed partial class GitHubImageHttpFetcher : IDisposable
{
    private const int MaxRedirects = 3;
    // Shields/Camo badges are small; let the isolated SVG worker own full
    // validation of large documents instead of parsing them twice on fetch.
    private const int MaxEarlySvgValidationBytes = 64 * 1024;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _requestAddressResolver;

    /// <param name="httpClient">The client used for every request.</param>
    /// <param name="ownsHttpClient">Whether <see cref="Dispose"/> disposes the client.</param>
    /// <param name="requestAddressResolver">
    /// When supplied, every logical request first resolves its host and rejects
    /// any non-public destination. The production client validates each physical
    /// connection in its connect callback instead, so it passes <see langword="null"/>.
    /// </param>
    internal GitHubImageHttpFetcher(
        HttpClient httpClient,
        bool ownsHttpClient,
        Func<string, CancellationToken, Task<IPAddress[]>>? requestAddressResolver)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _requestAddressResolver = requestAddressResolver;
    }

    internal static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectValidatedAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        HttpClient client = new(handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JitHub/1.0");
        return client;
    }

    internal static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    internal async Task<GitHubImageDownload?> FetchAsync(
        string sourceUrl,
        GitHubImageCacheEntry? cached,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= GitHubImageFetchRetryPolicy.MaxAttempts; attempt++)
        {
            try
            {
                return await FetchAttemptAsync(
                    sourceUrl,
                    cached,
                    scope,
                    admission,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                attempt < GitHubImageFetchRetryPolicy.MaxAttempts &&
                !cancellationToken.IsCancellationRequested &&
                GitHubImageFetchRetryPolicy.IsTransientHttpFailure(exception))
            {
                // Only transient failures reach this point. Back off the second
                // retry to avoid immediately repeating a throttled CDN request.
                await GitHubImageFetchRetryPolicy.DelayBeforeRetryAsync(attempt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("The bounded image retry loop completed unexpectedly.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<GitHubImageDownload?> FetchAttemptAsync(
        string sourceUrl,
        GitHubImageCacheEntry? cached,
        GitHubImageFetchScope scope,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        Uri currentUri = new(sourceUrl, UriKind.Absolute);
        for (int redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            if (_requestAddressResolver is not null)
            {
                await EnsurePublicNetworkDestinationAsync(currentUri, cancellationToken).ConfigureAwait(false);
            }

            // Only the first hop revalidates; a redirect target is a different resource.
            using HttpRequestMessage request = CreateRequest(currentUri, redirectCount == 0 ? cached : null);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new GitHubImageDownload(null, cached?.ContentType, IsNotModified: true);
            }

            if (!IsRedirect(response.StatusCode))
            {
                return await ReadDownloadAsync(response, currentUri, admission, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (redirectCount == MaxRedirects || response.Headers.Location is not Uri location)
            {
                throw new InvalidDataException("Remote image exceeded the redirect limit.");
            }

            Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (!GitHubImageSourcePolicy.IsAllowedRedirect(currentUri, nextUri, scope))
            {
                throw new InvalidDataException("Remote image redirect violates the source policy.");
            }

            currentUri = nextUri;
        }

        throw new InvalidDataException("Remote image exceeded the redirect limit.");
    }

    private static HttpRequestMessage CreateRequest(Uri uri, GitHubImageCacheEntry? revalidate)
    {
        HttpRequestMessage request = new(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(revalidate?.ETag) &&
            EntityTagHeaderValue.TryParse(revalidate.ETag, out EntityTagHeaderValue? entityTag))
        {
            request.Headers.IfNoneMatch.Add(entityTag);
        }

        if (revalidate?.LastModified is DateTimeOffset lastModified)
        {
            request.Headers.IfModifiedSince = lastModified;
        }

        return request;
    }

    private static async Task<GitHubImageDownload> ReadDownloadAsync(
        HttpResponseMessage response,
        Uri currentUri,
        IMarkdownImageSourceByteAdmission? admission,
        CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        (byte[] bytes, IDisposable? sourceByteLease) = await AdmittedImageContentReader.ReadAsync(
            response.Content, GitHubImageService.MaxImageBytes, admission, cancellationToken).ConfigureAwait(false);
        try
        {
            // Camo can briefly return a truncated SVG with HTTP 200 and
            // image/svg+xml. Reject it before it poisons the source cache;
            // the existing bounded GET retry then has a chance to obtain
            // the complete badge. Raster bytes with a stale SVG media type
            // still follow their validated signature below.
            if (bytes.Length <= MaxEarlySvgValidationBytes &&
                GitHubImageContentSniffer.TryDetectSupportedImageContentType(bytes, contentType, out string detectedType) &&
                detectedType == "image/svg+xml" &&
                !GitHubImageContentSniffer.IsWellFormedSvg(bytes))
            {
                if (currentUri.Host.Equals("camo.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                    throw new TransientCamoSvgPayloadException();

                throw new InvalidDataException("Remote SVG is not well-formed XML.");
            }

            GitHubImageDownload download = new(
                bytes,
                contentType,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified,
                SourceByteLease: sourceByteLease);
            sourceByteLease = null;
            return download;
        }
        finally
        {
            // Rejecting a downloaded SVG must release its buffer reference
            // before returning the quota, even if its task is retained.
            bytes = Array.Empty<byte>();
            sourceByteLease?.Dispose();
        }
    }

    private async Task EnsurePublicNetworkDestinationAsync(Uri uri, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await _requestAddressResolver!(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new InvalidDataException("Remote image host could not be resolved safely.", ex);
        }

        if (addresses.Length == 0 || addresses.Any(static address => !GloballyRoutableAddressPolicy.IsGloballyRoutable(address)))
        {
            throw new InvalidDataException("Remote image host does not resolve exclusively to globally routable destinations.");
        }
    }

    private static async ValueTask<Stream> ConnectValidatedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await ResolveHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(static address => !GloballyRoutableAddressPolicy.IsGloballyRoutable(address)))
        {
            throw new InvalidDataException("Remote image host does not resolve exclusively to globally routable destinations.");
        }

        Exception? lastError = null;
        foreach (IPAddress address in addresses)
        {
            Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = ex;
                if (ex is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException("Remote image host could not be reached through a validated address.", lastError);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.Markdown;

namespace JitHub.Services;

public sealed record GitHubImageDownload(
    byte[]? Bytes,
    string? ContentType,
    string? ETag = null,
    DateTimeOffset? LastModified = null,
    bool IsNotModified = false);

public sealed record GitHubCachedImage(
    string FilePath,
    byte[] Bytes,
    string? ContentType,
    bool IsFromCache,
    bool IsStale = false,
    Task<GitHubCachedImage?>? RefreshTask = null);

public delegate Task<GitHubImageDownload?> GitHubImageFetcher(
    GitHubImageCacheEntry? cachedEntry,
    CancellationToken cancellationToken);

public enum GitHubImageFetchScope
{
    TrustedGitHub,
    UserApprovedHttps,
}

public interface IGitHubImageService
{
    Task<GitHubCachedImage?> GetAsync(string sourceUrl, CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> TryGetCachedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default);

    Task<GitHubCachedImage?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        CancellationToken cancellationToken = default);

    Task InvalidateAsync(string sourceUrl, CancellationToken cancellationToken = default);
}

public sealed partial class GitHubImageService : IGitHubImageService, IDisposable
{
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan Freshness =
        GitHubCachePolicy.TtlForResource(GitHubCachePolicy.AvatarImageResource);

    private readonly IGitHubImageCacheStore _cacheStore;
    private readonly HttpClient _httpClient;
    private readonly Func<long> _partitionProvider;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostAddressResolver;
    private readonly bool _validateDnsBeforeRequest;
    private readonly bool _ownsHttpClient;
    private readonly IAccountWorkQuiescence? _accountWork;
    private readonly ConcurrentDictionary<string, SharedImageRequest> _misses =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SharedImageRequest> _refreshes =
        new(StringComparer.Ordinal);

    public GitHubImageService(
        IGitHubImageCacheStore cacheStore,
        IAccountService accountService,
        IAccountWorkQuiescence accountWork)
        : this(
            cacheStore,
            CreateHttpClient(),
            ownsHttpClient: true,
            accountService.GetUser,
            accountWork: accountWork)
    {
    }

    internal GitHubImageService(
        IGitHubImageCacheStore cacheStore,
        HttpClient httpClient,
        bool ownsHttpClient = false,
        Func<long>? partitionProvider = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null,
        IAccountWorkQuiescence? accountWork = null)
    {
        _cacheStore = cacheStore;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _partitionProvider = partitionProvider ?? (() => 0);
        _hostAddressResolver = hostAddressResolver ?? ResolveHostAddressesAsync;
        _validateDnsBeforeRequest = ownsHttpClient || hostAddressResolver is not null;
        _accountWork = accountWork;
    }

    public Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        CancellationToken cancellationToken = default) =>
        GetAsync(sourceUrl, GitHubImageFetchScope.TrustedGitHub, cancellationToken);

    public Task<GitHubCachedImage?> GetAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default) =>
        GetOrFetchCoreAsync(
            sourceUrl,
            (cached, token) => FetchHttpAsync(sourceUrl, cached, scope, token),
            scope,
            cancellationToken);

    public async Task<GitHubCachedImage?> TryGetCachedAsync(
        string sourceUrl,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken = default)
    {
        if (!IsAllowedSource(sourceUrl, scope))
        {
            return null;
        }

        long accountId = _partitionProvider();
        using IAccountWorkLease? lease = EnterAccountWork(accountId, cancellationToken);
        CancellationToken operationToken = lease?.CancellationToken ?? cancellationToken;
        GitHubImageCacheRead? cached = await _cacheStore.TryReadAsync(
            GetCacheKey(accountId, sourceUrl),
            operationToken).ConfigureAwait(false);
        if (cached is null)
        {
            return null;
        }

        bool isStale = DateTimeOffset.UtcNow - cached.Entry.CachedAt > Freshness;
        return ToResult(cached, isFromCache: true) with { IsStale = isStale };
    }

    public async Task<GitHubCachedImage?> GetOrFetchAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        CancellationToken cancellationToken = default)
        => await GetOrFetchCoreAsync(
            sourceUrl,
            fetcher,
            GitHubImageFetchScope.UserApprovedHttps,
            cancellationToken).ConfigureAwait(false);

    private async Task<GitHubCachedImage?> GetOrFetchCoreAsync(
        string sourceUrl,
        GitHubImageFetcher fetcher,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        if (!IsAllowedSource(sourceUrl, scope))
        {
            return null;
        }

        long accountId = _partitionProvider();
        using IAccountWorkLease? readLease = EnterAccountWork(accountId, cancellationToken);
        CancellationToken readToken = readLease?.CancellationToken ?? cancellationToken;
        string cacheKey = GetCacheKey(accountId, sourceUrl);
        GitHubImageCacheRead? cached = await _cacheStore.TryReadAsync(cacheKey, readToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            if (DateTimeOffset.UtcNow - cached.Entry.CachedAt <= Freshness)
            {
                return ToResult(cached, isFromCache: true);
            }

            Task<GitHubCachedImage?> refreshTask = GetSharedTask(
                _refreshes,
                cacheKey,
                token => FetchAndStoreWithLeaseAsync(
                    accountId,
                    cacheKey,
                    sourceUrl,
                    cached,
                    fetcher,
                    token),
                cancellationToken);
            return new GitHubCachedImage(
                cached.Entry.FilePath,
                cached.Bytes,
                cached.Entry.ContentType,
                IsFromCache: true,
                IsStale: true,
                RefreshTask: refreshTask);
        }

        Task<GitHubCachedImage?> missTask = GetSharedTask(
            _misses,
            cacheKey,
            token => FetchAndStoreWithLeaseAsync(
                accountId,
                cacheKey,
                sourceUrl,
                null,
                fetcher,
                token),
            cancellationToken);
        return await missTask.ConfigureAwait(false);
    }

    public async Task InvalidateAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        long accountId = _partitionProvider();
        using IAccountWorkLease? lease = EnterAccountWork(accountId, cancellationToken);
        await _cacheStore.InvalidateAsync(
            GetCacheKey(accountId, sourceUrl),
            lease?.CancellationToken ?? cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (SharedImageRequest request in _misses.Values)
        {
            request.Cancel();
        }

        foreach (SharedImageRequest request in _refreshes.Values)
        {
            request.Cancel();
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<GitHubCachedImage?> FetchAndStoreAsync(
        string cacheKey,
        string sourceUrl,
        GitHubImageCacheRead? cached,
        GitHubImageFetcher fetcher,
        CancellationToken cancellationToken)
    {
        try
        {
            GitHubImageDownload? download = await fetcher(cached?.Entry, cancellationToken).ConfigureAwait(false);
            if (download?.IsNotModified == true && cached is not null)
            {
                await _cacheStore.MarkFreshAsync(cacheKey, cancellationToken).ConfigureAwait(false);
                GitHubImageCacheRead? refreshed = await _cacheStore.TryReadAsync(cacheKey, cancellationToken)
                    .ConfigureAwait(false);
                return refreshed is null ? null : ToResult(refreshed, isFromCache: true);
            }

            if (download?.Bytes is not { Length: > 0 } bytes ||
                bytes.Length > MaxImageBytes ||
                download.ContentType is null ||
                !download.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                !LooksLikeSupportedImage(bytes, download.ContentType))
            {
                throw new InvalidDataException("Remote content is not a supported image.");
            }

            string extension = GetExtension(download.ContentType, sourceUrl);
            GitHubImageCacheEntry stored = await _cacheStore.PutAsync(
                    cacheKey,
                    bytes,
                    extension,
                    new GitHubImageCacheWriteMetadata(download.ETag, download.LastModified, download.ContentType),
                    cancellationToken)
                .ConfigureAwait(false);
            return ToResult(stored, bytes, isFromCache: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch when (cached is not null)
        {
            return ToResult(cached, isFromCache: true) with { IsStale = true };
        }
    }

    private async Task<GitHubCachedImage?> FetchAndStoreWithLeaseAsync(
        long accountId,
        string cacheKey,
        string sourceUrl,
        GitHubImageCacheRead? cached,
        GitHubImageFetcher fetcher,
        CancellationToken cancellationToken)
    {
        using IAccountWorkLease? lease = EnterAccountWork(accountId, cancellationToken);
        return await FetchAndStoreAsync(
            cacheKey,
            sourceUrl,
            cached,
            fetcher,
            lease?.CancellationToken ?? cancellationToken).ConfigureAwait(false);
    }

    private async Task<GitHubImageDownload?> FetchHttpAsync(
        string sourceUrl,
        GitHubImageCacheEntry? cached,
        GitHubImageFetchScope scope,
        CancellationToken cancellationToken)
    {
        Uri currentUri = new(sourceUrl, UriKind.Absolute);
        const int maxRedirects = 3;
        for (int redirectCount = 0; redirectCount <= maxRedirects; redirectCount++)
        {
            if (_validateDnsBeforeRequest)
            {
                await EnsurePublicNetworkDestinationAsync(currentUri, cancellationToken).ConfigureAwait(false);
            }

            using HttpRequestMessage request = new(HttpMethod.Get, currentUri);
            if (redirectCount == 0 &&
                !string.IsNullOrWhiteSpace(cached?.ETag) &&
                EntityTagHeaderValue.TryParse(cached.ETag, out EntityTagHeaderValue? entityTag))
            {
                request.Headers.IfNoneMatch.Add(entityTag);
            }

            if (redirectCount == 0 && cached?.LastModified is DateTimeOffset lastModified)
            {
                request.Headers.IfModifiedSince = lastModified;
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new GitHubImageDownload(null, cached?.ContentType, IsNotModified: true);
            }

            if (IsRedirect(response.StatusCode))
            {
                if (redirectCount == maxRedirects || response.Headers.Location is not Uri location)
                {
                    throw new InvalidDataException("Remote image exceeded the redirect limit.");
                }

                Uri nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                if (!IsAllowedSource(nextUri.ToString(), scope))
                {
                    throw new InvalidDataException("Remote image redirect violates the source policy.");
                }

                currentUri = nextUri;
                continue;
            }

            response.EnsureSuccessStatusCode();
            string? contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Remote content is not an image.");
            }

            byte[] bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return new GitHubImageDownload(
                bytes,
                contentType,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified);
        }

        throw new InvalidDataException("Remote image exceeded the redirect limit.");
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > MaxImageBytes)
        {
            throw new InvalidDataException($"Remote image exceeds the {MaxImageBytes}-byte limit.");
        }

        await using Stream input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream output = new();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaxImageBytes)
            {
                throw new InvalidDataException($"Remote image exceeds the {MaxImageBytes}-byte limit.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private static Task<GitHubCachedImage?> GetSharedTask(
        ConcurrentDictionary<string, SharedImageRequest> tasks,
        string cacheKey,
        Func<CancellationToken, Task<GitHubCachedImage?>> taskFactory,
        CancellationToken callerCancellationToken)
    {
        while (true)
        {
            SharedImageRequest request = tasks.GetOrAdd(cacheKey, _ => new SharedImageRequest(taskFactory));
            if (request.TryAddWaiter())
            {
                return AwaitSharedAsync(tasks, cacheKey, request, callerCancellationToken);
            }

            tasks.TryRemove(new KeyValuePair<string, SharedImageRequest>(cacheKey, request));
        }
    }

    private static async Task<GitHubCachedImage?> AwaitSharedAsync(
        ConcurrentDictionary<string, SharedImageRequest> tasks,
        string cacheKey,
        SharedImageRequest request,
        CancellationToken callerCancellationToken)
    {
        Task<GitHubCachedImage?> task = request.Task;
        try
        {
            return await task.WaitAsync(callerCancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SharedImageRequestRelease release = request.ReleaseWaiter(task.IsCompleted);
            if (release.ShouldCancel)
            {
                request.Cancel();
            }

            if (release.ShouldRetire)
            {
                tasks.TryRemove(new KeyValuePair<string, SharedImageRequest>(cacheKey, request));
                request.DisposeWhenComplete();
            }
        }
    }

    private IAccountWorkLease? EnterAccountWork(long accountId, CancellationToken cancellationToken) =>
        accountId > 0
            ? _accountWork?.Enter(accountId.ToString(CultureInfo.InvariantCulture), cancellationToken)
            : null;

    private static string GetCacheKey(long accountId, string sourceUrl) =>
        $"{accountId.ToString(CultureInfo.InvariantCulture)}:{NormalizeCacheIdentity(sourceUrl)}";

    internal static string NormalizeCacheIdentity(string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? uri))
        {
            return sourceUrl.Trim();
        }

        UriBuilder builder = new(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Fragment = string.Empty,
            Port = uri.IsDefaultPort ? -1 : uri.Port
        };
        return builder.Uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }

    private static bool IsAllowedSource(string sourceUrl, GitHubImageFetchScope scope)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri? sourceUri) ||
            sourceUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(sourceUri.UserInfo) ||
            !IsGloballyRoutableDestination(sourceUri))
        {
            return false;
        }

        return scope == GitHubImageFetchScope.UserApprovedHttps ||
            MarkdownRemoteImagePolicy.IsTrustedGitHubHost(sourceUri.Host);
    }

    private static bool IsGloballyRoutableDestination(Uri uri)
    {
        string host = uri.Host.TrimEnd('.');
        if (uri.IsLoopback || GloballyRoutableAddressPolicy.IsSpecialUseHost(host))
        {
            return false;
        }

        return !IPAddress.TryParse(host, out IPAddress? address) ||
            GloballyRoutableAddressPolicy.IsGloballyRoutable(address);
    }

    private async Task EnsurePublicNetworkDestinationAsync(Uri uri, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await _hostAddressResolver(uri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
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

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static GitHubCachedImage ToResult(GitHubImageCacheRead read, bool isFromCache) =>
        new(read.Entry.FilePath, read.Bytes, read.Entry.ContentType, isFromCache);

    private static GitHubCachedImage ToResult(
        GitHubImageCacheEntry entry,
        byte[] bytes,
        bool isFromCache) =>
        new(entry.FilePath, bytes, entry.ContentType, isFromCache);

    private static bool LooksLikeSupportedImage(byte[] bytes, string contentType)
    {
        if (contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
        }

        if (contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        }

        if (contentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 6 &&
                bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' &&
                bytes[3] == (byte)'8' && (bytes[4] == (byte)'7' || bytes[4] == (byte)'9') &&
                bytes[5] == (byte)'a';
        }

        if (contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 12 &&
                bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
                bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
        }

        if (contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            string prefix = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            return prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase);
        }

        if (contentType.Equals("image/bmp", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 2 && bytes[0] == (byte)'B' && bytes[1] == (byte)'M';
        }

        if (contentType.Equals("image/x-icon", StringComparison.OrdinalIgnoreCase) ||
            contentType.Equals("image/vnd.microsoft.icon", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0;
        }

        if (contentType.Equals("image/tiff", StringComparison.OrdinalIgnoreCase))
        {
            return bytes.Length >= 4 &&
                ((bytes[0] == (byte)'I' && bytes[1] == (byte)'I' && bytes[2] == 0x2A && bytes[3] == 0) ||
                 (bytes[0] == (byte)'M' && bytes[1] == (byte)'M' && bytes[2] == 0 && bytes[3] == 0x2A));
        }

        return false;
    }

    private static HttpClient CreateHttpClient()
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectValidatedAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JitHub/1.0");
        return client;
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

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    private static string GetExtension(string mediaType, string sourceUrl)
    {
        string? extension = mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
            "image/tiff" => ".tiff",
            _ => null
        };

        if (extension is not null)
        {
            return extension;
        }

        string pathExtension = Path.GetExtension(new Uri(sourceUrl).AbsolutePath);
        return string.IsNullOrWhiteSpace(pathExtension) || pathExtension.Length > 12
            ? ".img"
            : pathExtension;
    }

    internal readonly record struct SharedImageRequestRelease(bool ShouldRetire, bool ShouldCancel);

    internal sealed class SharedImageRequest
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly Lazy<Task<GitHubCachedImage?>> _task;
        private int _waiterCount;
        private int _disposeScheduled;
        private bool _retired;

        public SharedImageRequest(Func<CancellationToken, Task<GitHubCachedImage?>> taskFactory)
        {
            _task = new Lazy<Task<GitHubCachedImage?>>(
                () => taskFactory(_cancellationTokenSource.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task<GitHubCachedImage?> Task => _task.Value;

        public bool TryAddWaiter()
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                _waiterCount++;
                return true;
            }
        }

        public SharedImageRequestRelease ReleaseWaiter(bool taskCompleted)
        {
            lock (_gate)
            {
                if (_waiterCount <= 0)
                {
                    throw new InvalidOperationException("Shared image request waiter count is already zero.");
                }

                _waiterCount--;
                if (_waiterCount > 0)
                {
                    return new SharedImageRequestRelease(ShouldRetire: false, ShouldCancel: false);
                }

                // Retire while holding the same lock used by TryAddWaiter. A late caller can no
                // longer join this transfer between the zero-count decision and cancellation.
                _retired = true;
                return new SharedImageRequestRelease(
                    ShouldRetire: true,
                    ShouldCancel: !taskCompleted);
            }
        }

        public void Cancel()
        {
            try
            {
                _cancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void DisposeWhenComplete()
        {
            if (Interlocked.Exchange(ref _disposeScheduled, 1) != 0)
            {
                return;
            }

            _ = Task.ContinueWith(
                _ => _cancellationTokenSource.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}

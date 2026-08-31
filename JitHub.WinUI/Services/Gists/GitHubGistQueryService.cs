using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.WinUI.Helpers;

namespace JitHub.Services;

public sealed class GitHubGistQueryService : IGitHubGistQueryService
{
    internal const int MaximumRawFileBytes = 10 * 1024 * 1024;
    private const int MaximumRawFileRedirects = 3;
    private static readonly TimeSpan RawFileTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan CachePageIndexTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan LocalDurabilityOperationTimeout = TimeSpan.FromSeconds(5);
    private readonly IGitHubQueryService _queryService;
    private readonly IGitHubRequestQueue _requestQueue;
    private readonly IGitHubCacheStore _cacheStore;
    private readonly IGistMutationJournal _mutationJournal;
    private readonly HttpClient _httpClient;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostAddressResolver;
    private readonly TimeSpan _rawFileTtl;
    private readonly bool _validateDnsBeforeRequest;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly object _backgroundGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly SemaphoreSlim _cachePageIndexGate = new(1, 1);
    private readonly object _previewGate = new();
    private readonly Dictionary<string, GitHubGist> _previewOverrides = new(StringComparer.Ordinal);
    private readonly HashSet<string> _previewDeletedIds = new(StringComparer.Ordinal);
    private readonly object _reconciliationGate = new();
    private readonly Dictionary<string, GistLibraryReconciliationSession> _reconciliations = new(StringComparer.Ordinal);
    private int _previewCreateSequence;

    public GitHubGistQueryService(
        IGitHubQueryService queryService,
        IGitHubRequestQueue requestQueue,
        IGitHubCacheStore cacheStore)
        : this(
            queryService,
            requestQueue,
            cacheStore,
            CreateDefaultHttpClient(),
            mutationJournal: NullGistMutationJournal.Instance,
            validateDnsBeforeRequest: false)
    {
    }

    public GitHubGistQueryService(
        IGitHubQueryService queryService,
        IGitHubRequestQueue requestQueue,
        IGitHubCacheStore cacheStore,
        IGistMutationJournal mutationJournal)
        : this(
            queryService,
            requestQueue,
            cacheStore,
            CreateDefaultHttpClient(),
            mutationJournal: mutationJournal,
            validateDnsBeforeRequest: false)
    {
    }

    public GitHubGistQueryService(
        IGitHubQueryService queryService,
        IGitHubRequestQueue requestQueue,
        IGitHubCacheStore cacheStore,
        IGistMutationJournal mutationJournal,
        IApplicationTaskCoordinator taskCoordinator)
        : this(
            queryService,
            requestQueue,
            cacheStore,
            CreateDefaultHttpClient(),
            mutationJournal: mutationJournal,
            validateDnsBeforeRequest: false,
            taskCoordinator: taskCoordinator)
    {
    }

    internal GitHubGistQueryService(
        IGitHubQueryService queryService,
        IGitHubRequestQueue requestQueue,
        IGitHubCacheStore cacheStore,
        HttpClient httpClient,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null,
        TimeSpan? rawFileTtl = null,
        bool validateDnsBeforeRequest = true,
        IGistMutationJournal? mutationJournal = null,
        IApplicationTaskCoordinator? taskCoordinator = null)
    {
        _queryService = queryService;
        _requestQueue = requestQueue;
        _cacheStore = cacheStore;
        _mutationJournal = mutationJournal ?? NullGistMutationJournal.Instance;
        _httpClient = httpClient;
        _hostAddressResolver = hostAddressResolver ?? ResolveHostAddressesAsync;
        _rawFileTtl = rawFileTtl ?? RawFileTtl;
        _validateDnsBeforeRequest = validateDnsBeforeRequest;
        _taskCoordinator = taskCoordinator ?? new ApplicationTaskCoordinator();
        _httpClient.BaseAddress ??= new Uri("https://api.github.com/");
    }

    public async Task<GistCachedLibrarySnapshot> GetCachedLibraryAsync(
        string accessToken,
        string userId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return new GistCachedLibrarySnapshot([], 0, IsComplete: false, CacheState.Miss);
        }

        string partition = GitHubAccountPartition.Resolve(accessToken, userId);
        int normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        GistCachePageIndex? index = await TryGetCachePageIndexAsync(
            partition,
            normalizedPageSize,
            cancellationToken).ConfigureAwait(false);
        int pageLimit = index is { HighestKnownPage: > 0 }
            ? Math.Min(index.HighestKnownPage, 1000)
            : 1000;
        bool fallbackScan = index is null;
        bool missingKnownPage = false;
        bool inferredComplete = false;
        int cachedPageCount = 0;
        CacheState aggregateState = CacheState.Fresh;
        Dictionary<string, GitHubGist> items = new(StringComparer.Ordinal);

        for (int page = 1; page <= pageLimit; page++)
        {
            GitHubQuery<GitHubGist[]> query = CreatePageQuery(
                accessToken,
                partition,
                page,
                normalizedPageSize,
                GitHubRequestPriority.BackgroundRefresh);
            CachedResult<GitHubGist[]>? cached = await _cacheStore.TryGetAsync(query, cancellationToken).ConfigureAwait(false);
            if (cached?.Value is not { } pageItems)
            {
                if (fallbackScan)
                {
                    break;
                }

                missingKnownPage = true;
                continue;
            }

            cachedPageCount++;
            if (cached.CacheState != CacheState.Fresh)
            {
                aggregateState = CacheState.Stale;
            }

            foreach (GitHubGist gist in pageItems)
            {
                if (!string.IsNullOrWhiteSpace(gist.Id))
                {
                    items[gist.Id] = gist;
                }
            }

            if (pageItems.Length < normalizedPageSize)
            {
                inferredComplete = true;
                if (fallbackScan)
                {
                    break;
                }
            }
        }

        bool isComplete = !missingKnownPage &&
            (index?.IsComplete == true || inferredComplete) &&
            cachedPageCount > 0;
        IReadOnlyList<GistMutationJournalEntry> mutations = await _mutationJournal.ReadAsync(
            partition,
            cancellationToken).ConfigureAwait(false);
        GitHubGist[] restoredItems = ApplyMutationOverlay(items.Values, mutations, includeCreates: true);
        return new GistCachedLibrarySnapshot(
            restoredItems,
            cachedPageCount,
            isComplete,
            cachedPageCount == 0 ? CacheState.Miss : aggregateState);
    }

    public async Task<CachedResult<GitHubGist[]>> GetPageAsync(
        string accessToken,
        string userId,
        int page,
        int pageSize,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        CancellationToken cancellationToken = default)
    {
        int normalizedPage = Math.Max(1, page);
        int normalizedPageSize = Math.Clamp(pageSize, 1, 100);
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            GitHubGist[] preview = CreatePreviewPage(normalizedPage, normalizedPageSize);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new CachedResult<GitHubGist[]>(preview, CacheState.Fresh, now, now.AddMinutes(5));
        }

        GitHubQuery<GitHubGist[]> query = CreatePageQuery(
            accessToken,
            userId,
            normalizedPage,
            normalizedPageSize,
            priority);
        CachedResult<GitHubGist[]> result = await _queryService.GetAsync(query, fetchPolicy, cancellationToken).ConfigureAwait(false);
        if (result.Value is { } authoritativePageItems)
        {
            await RecordCachedPageAsync(
                query.UserId,
                normalizedPage,
                normalizedPageSize,
                authoritativePageItems.Length,
                cancellationToken).ConfigureAwait(false);

            IReadOnlyList<GistMutationJournalEntry> mutations = await _mutationJournal.ReadAsync(
                query.UserId,
                cancellationToken).ConfigureAwait(false);
            GitHubGist[] visibleItems = ApplyMutationOverlay(
                authoritativePageItems,
                mutations,
                includeCreates: normalizedPage == 1);
            result = result with { Value = visibleItems };

            if (fetchPolicy == QueryFetchPolicy.NetworkOnly)
            {
                GitHubGist[]? authoritativeLibrary = RecordAuthoritativePage(
                    query.UserId,
                    normalizedPage,
                    normalizedPageSize,
                    authoritativePageItems);
                if (authoritativeLibrary is not null)
                {
                    await ReconcileLibraryMutationsAsync(
                        query.UserId,
                        authoritativeLibrary,
                        mutations,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return result;
    }

    public async Task<CachedResult<GitHubGist>> GetDetailAsync(
        string accessToken,
        string userId,
        string gistId,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        CancellationToken cancellationToken = default)
    {
        string normalizedId = NormalizeGistId(gistId);
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            GitHubGist value;
            lock (_previewGate)
            {
                value = _previewOverrides.TryGetValue(normalizedId, out GitHubGist? preview)
                    ? preview
                    : CreatePreviewGist(ParsePreviewIndex(normalizedId));
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new CachedResult<GitHubGist>(value, CacheState.Fresh, now, now.AddMinutes(5));
        }

        string partition = GitHubAccountPartition.Resolve(accessToken, userId);
        IReadOnlyList<GistMutationJournalEntry> mutations = await _mutationJournal.ReadAsync(
            partition,
            cancellationToken).ConfigureAwait(false);
        GistMutationJournalEntry? mutation = mutations.LastOrDefault(entry =>
            string.Equals(entry.GistId, normalizedId, StringComparison.Ordinal));
        if (mutation?.Kind == GistMutationKind.Deleted)
        {
            throw new KeyNotFoundException("The Gist was deleted locally and is awaiting GitHub reconciliation.");
        }

        if (mutation?.Gist is { } overlay && fetchPolicy != QueryFetchPolicy.NetworkOnly)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new CachedResult<GitHubGist>(
                overlay,
                CacheState.Stale,
                mutation.RecordedAt,
                now,
                IsRefreshInProgress: true);
        }

        GitHubQuery<GitHubGist> query = CreateDetailQuery(accessToken, partition, normalizedId, priority);
        CachedResult<GitHubGist> result = await _queryService.GetAsync(query, fetchPolicy, cancellationToken).ConfigureAwait(false);
        if (result.Value is not { } authoritative)
        {
            return result;
        }

        if (mutation?.Gist is { } pending)
        {
            GitHubGist visible = pending;
            if (fetchPolicy == QueryFetchPolicy.NetworkOnly && AreDetailsEquivalent(authoritative, pending))
            {
                await _mutationJournal.RemoveAsync(partition, normalizedId, cancellationToken).ConfigureAwait(false);
                visible = authoritative;
            }

            return result with { Value = visible };
        }

        return result;
    }

    public async Task<CachedResult<string>> GetRawFileAsync(
        string userId,
        string rawUrl,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible,
        CancellationToken cancellationToken = default)
    {
        Uri source = NormalizeRawGistUri(rawUrl);
        string partition = GitHubAccountPartition.Require(userId);
        GitHubQuery<string> query = CreateRawFileQuery(partition, source, priority);
        CachedResult<string>? cached = await _cacheStore.TryGetAsync(query, cancellationToken).ConfigureAwait(false);
        if (fetchPolicy != QueryFetchPolicy.NetworkOnly)
        {
            if (cached?.Value is not null)
            {
                if (fetchPolicy == QueryFetchPolicy.CacheFirst ||
                    (cached.CacheState == CacheState.Fresh && fetchPolicy != QueryFetchPolicy.RefreshInBackground))
                {
                    return cached;
                }

                StartBackgroundRawFileRefresh(query, source, cached, cancellationToken);
                return cached with { IsRefreshInProgress = true };
            }
        }

        return await RefreshRawFileAsync(query, source, cached, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetRawFileContentAsync(
        string userId,
        string rawUrl,
        CancellationToken cancellationToken = default)
    {
        CachedResult<string> result = await GetRawFileAsync(
            userId,
            rawUrl,
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.Visible,
            cancellationToken).ConfigureAwait(false);
        return result.Value ?? throw new InvalidDataException("GitHub returned an empty Gist file.");
    }

    public async Task DrainBackgroundWorkAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] pending;
            lock (_backgroundGate)
            {
                pending = _backgroundTasks.Where(static task => !task.IsCompleted).ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
            }

            lock (_backgroundGate)
            {
                _backgroundTasks.RemoveWhere(static task => task.IsCompleted);
            }
        }
    }

    public async Task<GistMutationResult<GitHubGist>> CreateAsync(
        string accessToken,
        string userId,
        GitHubGistCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCreateRequest(request);
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            int index = 1000 + Interlocked.Increment(ref _previewCreateSequence);
            GitHubGist previewCreated = CreateFromRequest($"preview-created-{index}", request);
            lock (_previewGate)
            {
                _previewOverrides[previewCreated.Id] = previewCreated;
                _previewDeletedIds.Remove(previewCreated.Id);
            }

            return new GistMutationResult<GitHubGist>(previewCreated, GistMutationDurability.Durable);
        }

        GitHubGist created = await SendJsonMutationAsync(
            accessToken,
            userId,
            HttpMethod.Post,
            "gists",
            JsonContent.Create(request, GitHubJsonSerializerContext.Default.GitHubGistCreateRequest),
            GitHubJsonSerializerContext.Default.GitHubGist,
            "create",
            cancellationToken);
        string partition = GitHubAccountPartition.Resolve(accessToken, userId);
        bool isDurable = await TryPersistMutationRecoveryAsync(
            token => _mutationJournal.RecordUpsertAsync(
                partition,
                created.Id,
                created,
                isCreate: true,
                token),
            partition,
            created.Id).ConfigureAwait(false);
        if (isDurable)
        {
            await TryWriteMutationDetailAfterSuccessAsync(accessToken, partition, created.Id, created).ConfigureAwait(false);
        }

        return new GistMutationResult<GitHubGist>(
            created,
            isDurable ? GistMutationDurability.Durable : GistMutationDurability.Degraded);
    }

    public async Task<GistMutationResult<GitHubGist>> UpdateAsync(
        string accessToken,
        string userId,
        string gistId,
        GitHubGistUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string normalizedId = NormalizeGistId(gistId);
        ValidateUpdateRequest(request);
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            lock (_previewGate)
            {
                GitHubGist existing = _previewOverrides.TryGetValue(normalizedId, out GitHubGist? preview)
                    ? preview
                    : CreatePreviewGist(ParsePreviewIndex(normalizedId));
                GitHubGist previewUpdated = ApplyUpdate(existing, request);
                _previewOverrides[normalizedId] = previewUpdated;
                return new GistMutationResult<GitHubGist>(previewUpdated, GistMutationDurability.Durable);
            }
        }

        GitHubGist updated = await SendJsonMutationAsync(
            accessToken,
            userId,
            HttpMethod.Patch,
            $"gists/{Uri.EscapeDataString(normalizedId)}",
            JsonContent.Create(request, GitHubJsonSerializerContext.Default.GitHubGistUpdateRequest),
            GitHubJsonSerializerContext.Default.GitHubGist,
            $"update-{normalizedId}",
            cancellationToken);
        string partition = GitHubAccountPartition.Resolve(accessToken, userId);
        bool isDurable = await TryPersistMutationRecoveryAsync(
            token => _mutationJournal.RecordUpsertAsync(
                partition,
                normalizedId,
                updated,
                isCreate: false,
                token),
            partition,
            normalizedId).ConfigureAwait(false);
        if (isDurable)
        {
            await TryWriteMutationDetailAfterSuccessAsync(accessToken, partition, normalizedId, updated).ConfigureAwait(false);
        }

        return new GistMutationResult<GitHubGist>(
            updated,
            isDurable ? GistMutationDurability.Durable : GistMutationDurability.Degraded);
    }

    public async Task<GistMutationResult<bool>> DeleteAsync(
        string accessToken,
        string userId,
        string gistId,
        CancellationToken cancellationToken = default)
    {
        string normalizedId = NormalizeGistId(gistId);
        if (!GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            await SendDeleteMutationAsync(accessToken, userId, normalizedId, cancellationToken);
        }
        else
        {
            lock (_previewGate)
            {
                _previewOverrides.Remove(normalizedId);
                _previewDeletedIds.Add(normalizedId);
            }
        }

        if (!GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            string partition = GitHubAccountPartition.Resolve(accessToken, userId);
            bool isDurable = await TryPersistMutationRecoveryAsync(
                token => _mutationJournal.RecordDeleteAsync(partition, normalizedId, token),
                partition,
                normalizedId).ConfigureAwait(false);
            return new GistMutationResult<bool>(
                true,
                isDurable ? GistMutationDurability.Durable : GistMutationDurability.Degraded);
        }

        return new GistMutationResult<bool>(true, GistMutationDurability.Durable);
    }

    private async Task<bool> TryPersistMutationRecoveryAsync(
        Func<CancellationToken, Task> writeJournal,
        string partition,
        string gistId)
    {
        try
        {
            using CancellationTokenSource durabilityTimeout = new(LocalDurabilityOperationTimeout);
            await writeJournal(durabilityTimeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            HandledFailureReporter.Report(exception, "gists-mutation-recovery-persistence");
            await TryInvalidateMutationCachesAsync(partition, gistId).ConfigureAwait(false);
            return false;
        }
    }

    private async Task TryInvalidateMutationCachesAsync(string partition, string gistId)
    {
        try
        {
            using CancellationTokenSource invalidationTimeout = new(LocalDurabilityOperationTimeout);
            await _queryService.InvalidateTagsAsync(
                partition,
                (string[])
                [
                    GistCacheTagPolicy.List(partition),
                    GistCacheTagPolicy.ListIndex(partition),
                    GistCacheTagPolicy.Detail(partition, gistId)
                ],
                invalidationTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HandledFailureReporter.Report(exception, "gists-mutation-cache-invalidation");
        }
    }

    private async Task TryWriteMutationDetailAfterSuccessAsync(
        string accessToken,
        string partition,
        string gistId,
        GitHubGist gist)
    {
        using CancellationTokenSource cacheWriteTimeout = new(LocalDurabilityOperationTimeout);
        await TryWriteMutationDetailAsync(
            accessToken,
            partition,
            gistId,
            gist,
            cacheWriteTimeout.Token).ConfigureAwait(false);
    }

    private async Task TryWriteMutationDetailAsync(
        string accessToken,
        string partition,
        string gistId,
        GitHubGist gist,
        CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GitHubQuery<GitHubGist> query = CreateDetailQuery(
                accessToken,
                partition,
                gistId,
                GitHubRequestPriority.Visible);
            await _cacheStore.PutAsync(
                query,
                new GitHubRestResponse<GitHubGist>(
                    HttpStatusCode.OK,
                    gist,
                    IsNotModified: false,
                    ETag: null,
                    LastModified: null,
                    Link: null,
                    RateLimitRemaining: null,
                    RateLimitReset: null,
                    RetryAfter: null,
                    FetchedAt: now),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            HandledFailureReporter.Report(exception, "gists-mutation-cache-write-through");
        }
    }

    private async Task<T> SendJsonMutationAsync<T>(
        string accessToken,
        string userId,
        HttpMethod method,
        string path,
        HttpContent content,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> responseType,
        string operation,
        CancellationToken cancellationToken)
        where T : class
    {
        string partition = GitHubAccountPartition.Require(userId);
        try
        {
            return await _requestQueue.EnqueueForAccountAsync(
                partition,
                $"{partition}:gist:{operation}",
                GitHubRequestPriority.Mutation,
                async token =>
                {
                    using HttpRequestMessage message = CreateRequest(accessToken, method, path, content);
                    using HttpResponseMessage response = await _httpClient.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        token);
                    await EnsureSuccessAsync(response, token);
                    T? value = await response.Content.ReadFromJsonAsync(responseType, token);
                    return value ?? throw new GitHubApiException(response.StatusCode, "GitHub returned an empty gist response.");
                },
                cancellationToken);
        }
        finally
        {
            content.Dispose();
        }
    }

    private Task SendDeleteMutationAsync(
        string accessToken,
        string userId,
        string gistId,
        CancellationToken cancellationToken)
    {
        string partition = GitHubAccountPartition.Require(userId);
        return _requestQueue.EnqueueForAccountAsync(
            partition,
            $"{partition}:gist:delete-{gistId}",
            GitHubRequestPriority.Mutation,
            async token =>
            {
                using HttpRequestMessage message = CreateRequest(
                    accessToken,
                    HttpMethod.Delete,
                    $"gists/{Uri.EscapeDataString(gistId)}",
                    content: null);
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    token);
                await EnsureSuccessAsync(response, token);
                return true;
            },
            cancellationToken);
    }

    private async Task<CachedResult<string>> RefreshRawFileAsync(
        GitHubQuery<string> query,
        Uri source,
        CachedResult<string>? cached,
        CancellationToken cancellationToken)
    {
        string dedupeKey = GitHubQueryKeys.CreateDedupeKey(
            query.UserId,
            HttpMethod.Get,
            query.RelativePath,
            query.AcceptMediaType,
            query.JsonTypeInfo.Type);
        GitHubRestResponse<string> response = await _requestQueue.EnqueueForAccountAsync(
            query.UserId,
            dedupeKey,
            query.Priority,
            token => DownloadRawFileAsync(source, cached, token),
            cancellationToken).ConfigureAwait(false);

        if (response.IsNotModified)
        {
            await _cacheStore.MarkRevalidatedAsync(query, response, cancellationToken).ConfigureAwait(false);
            CachedResult<string>? revalidated = await _cacheStore.TryGetAsync(query, cancellationToken).ConfigureAwait(false);
            return revalidated?.Value is not null
                ? revalidated with { CacheState = CacheState.Fresh }
                : throw new InvalidDataException("GitHub revalidated a Gist file that is no longer cached.");
        }

        if (response.Payload is null)
        {
            throw new InvalidDataException("GitHub returned an empty Gist file.");
        }

        await _cacheStore.PutAsync(query, response, cancellationToken).ConfigureAwait(false);
        return new CachedResult<string>(
            response.Payload,
            CacheState.Fresh,
            response.FetchedAt,
            response.FetchedAt.Add(query.Ttl),
            ETag: response.ETag,
            LastModified: response.LastModified);
    }

    private async Task<GitHubRestResponse<string>> DownloadRawFileAsync(
        Uri source,
        CachedResult<string>? cached,
        CancellationToken cancellationToken)
    {
        Uri current = source;
        for (int redirect = 0; redirect <= MaximumRawFileRedirects; redirect++)
        {
            if (_validateDnsBeforeRequest)
            {
                await EnsurePublicNetworkDestinationAsync(current, cancellationToken).ConfigureAwait(false);
            }

            using HttpRequestMessage request = new(HttpMethod.Get, current);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("JitHub", "1.0"));
            if (redirect == 0 && !string.IsNullOrWhiteSpace(cached?.ETag) &&
                EntityTagHeaderValue.TryParse(cached.ETag, out EntityTagHeaderValue? entityTag))
            {
                request.Headers.IfNoneMatch.Add(entityTag);
            }

            if (redirect == 0 && cached?.LastModified is DateTimeOffset lastModified)
            {
                request.Headers.IfModifiedSince = lastModified;
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new GitHubRestResponse<string>(
                    response.StatusCode,
                    Payload: null,
                    IsNotModified: true,
                    response.Headers.ETag?.ToString() ?? cached?.ETag,
                    response.Content.Headers.LastModified ?? cached?.LastModified,
                    Link: null,
                    RateLimitRemaining: null,
                    RateLimitReset: null,
                    RetryAfter: null,
                    DateTimeOffset.UtcNow);
            }

            if (IsRedirect(response.StatusCode))
            {
                Uri? location = response.Headers.Location;
                if (location is null || redirect == MaximumRawFileRedirects)
                {
                    throw new GitHubApiException(response.StatusCode, "The full Gist file redirected to an invalid location.");
                }

                current = NormalizeRawGistUri(location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(current, location).AbsoluteUri);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new GitHubApiException(response.StatusCode, $"GitHub returned HTTP {(int)response.StatusCode} while loading the Gist file.");
            }

            byte[] bytes = await ReadBoundedRawFileAsync(response.Content, cancellationToken).ConfigureAwait(false);
            string content = DecodeRawFile(bytes, response.Content.Headers.ContentType?.CharSet);
            return new GitHubRestResponse<string>(
                response.StatusCode,
                content,
                IsNotModified: false,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified,
                Link: null,
                RateLimitRemaining: null,
                RateLimitReset: null,
                RetryAfter: null,
                DateTimeOffset.UtcNow);
        }

        throw new InvalidOperationException("The full Gist file could not be loaded.");
    }

    private void StartBackgroundRawFileRefresh(
        GitHubQuery<string> query,
        Uri source,
        CachedResult<string> cached,
        CancellationToken cancellationToken)
    {
        GitHubQuery<string> backgroundQuery = query with { Priority = GitHubRequestPriority.BackgroundRefresh };
        Task refreshTask = _taskCoordinator.RunAsync(
            token => RefreshRawFileAsync(backgroundQuery, source, cached, token),
            new ApplicationTaskOptions("gists.raw_file.refresh", query.UserId),
            cancellationToken);
        lock (_backgroundGate)
        {
            _backgroundTasks.RemoveWhere(static task => task.IsCompleted);
            _backgroundTasks.Add(refreshTask);
        }
    }

    private static async Task<byte[]> ReadBoundedRawFileAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > MaximumRawFileBytes)
        {
            throw new InvalidDataException("This Gist file is too large to preview safely.");
        }

        int capacity = content.Headers.ContentLength is > 0 and <= MaximumRawFileBytes
            ? (int)content.Headers.ContentLength.Value
            : 16 * 1024;
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new(capacity);
        byte[] chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                int remaining = MaximumRawFileBytes - checked((int)buffer.Length);
                int requested = Math.Min(chunk.Length, remaining + 1);
                int read = await stream.ReadAsync(chunk.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (read > remaining)
                {
                    throw new InvalidDataException("This Gist file is too large to preview safely.");
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static string DecodeRawFile(byte[] bytes, string? charset)
    {
        Encoding encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim(' ', '\"', '\''));
            }
            catch (ArgumentException)
            {
            }
        }

        using MemoryStream stream = new(bytes, writable: false);
        using StreamReader reader = new(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static HttpRequestMessage CreateRequest(
        string accessToken,
        HttpMethod method,
        string path,
        HttpContent? content)
    {
        HttpRequestMessage request = new(method, path) { Content = content };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("JitHub", "1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string message;
        try
        {
            GitHubApiError? error = await response.Content.ReadFromJsonAsync(
                GitHubJsonSerializerContext.Default.GitHubApiError,
                cancellationToken);
            message = string.IsNullOrWhiteSpace(error?.Message)
                ? $"GitHub returned HTTP {(int)response.StatusCode}."
                : JitHub.WinUI.Helpers.UserFacingError.ForInternalMessage(
                    error.Message,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
                    "gist-api");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or IOException)
        {
            message = $"GitHub returned HTTP {(int)response.StatusCode}.";
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubAuthenticationException(message);
        }

        throw new GitHubApiException(response.StatusCode, message);
    }

    private static GitHubQuery<T> CreateQuery<T>(
        string accessToken,
        string userId,
        string relativePath,
        string resourceKind,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        string[] tags,
        GitHubRequestPriority priority)
        where T : class
    {
        string partition = GitHubAccountPartition.Resolve(accessToken, userId);
        return new GitHubQuery<T>(
            accessToken,
            partition,
            HttpMethod.Get,
            relativePath,
            GitHubQueryKeys.Create(partition, HttpMethod.Get, relativePath),
            resourceKind,
            GitHubCachePolicy.TtlForResource(resourceKind),
            jsonTypeInfo,
            tags,
            priority);
    }

    private static GitHubQuery<GitHubGist> CreateDetailQuery(
        string accessToken,
        string partition,
        string gistId,
        GitHubRequestPriority priority) =>
        CreateQuery(
            accessToken,
            partition,
            $"gists/{Uri.EscapeDataString(gistId)}",
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubGist,
            [GistCacheTagPolicy.Detail(partition, gistId)],
            priority);

    private static GitHubGist[] ApplyMutationOverlay(
        IEnumerable<GitHubGist> source,
        IReadOnlyList<GistMutationJournalEntry> mutations,
        bool includeCreates)
    {
        List<GitHubGist> items = source
            .Where(static gist => !string.IsNullOrWhiteSpace(gist.Id))
            .ToList();
        foreach (GistMutationJournalEntry mutation in mutations.OrderBy(static entry => entry.RecordedAt))
        {
            int index = items.FindIndex(gist => string.Equals(gist.Id, mutation.GistId, StringComparison.Ordinal));
            if (mutation.Kind == GistMutationKind.Deleted)
            {
                if (index >= 0)
                {
                    items.RemoveAt(index);
                }

                continue;
            }

            if (mutation.Gist is not { } gist)
            {
                continue;
            }

            if (index >= 0)
            {
                items[index] = gist;
            }
            else if (includeCreates && mutation.Kind == GistMutationKind.Created)
            {
                items.Insert(0, gist);
            }
        }

        return items.ToArray();
    }

    private GitHubGist[]? RecordAuthoritativePage(
        string partition,
        int page,
        int pageSize,
        IReadOnlyCollection<GitHubGist> authoritativeItems)
    {
        lock (_reconciliationGate)
        {
            if (page == 1)
            {
                _reconciliations[partition] = new GistLibraryReconciliationSession(pageSize);
            }

            if (!_reconciliations.TryGetValue(partition, out GistLibraryReconciliationSession? session) ||
                session.PageSize != pageSize ||
                session.NextPage != page)
            {
                _reconciliations.Remove(partition);
                return null;
            }

            foreach (GitHubGist gist in authoritativeItems)
            {
                if (!string.IsNullOrWhiteSpace(gist.Id))
                {
                    session.Items[gist.Id] = gist;
                }
            }

            session.NextPage++;
            if (authoritativeItems.Count >= pageSize)
            {
                return null;
            }

            _reconciliations.Remove(partition);
            return session.Items.Values.ToArray();
        }
    }

    private async Task ReconcileLibraryMutationsAsync(
        string partition,
        IReadOnlyCollection<GitHubGist> authoritativeItems,
        IReadOnlyList<GistMutationJournalEntry> observedMutations,
        CancellationToken cancellationToken)
    {
        Dictionary<string, GitHubGist> authoritative = authoritativeItems
            .Where(static gist => !string.IsNullOrWhiteSpace(gist.Id))
            .ToDictionary(static gist => gist.Id, StringComparer.Ordinal);
        foreach (GistMutationJournalEntry mutation in observedMutations)
        {
            bool confirmed = mutation.Kind switch
            {
                GistMutationKind.Deleted => !authoritative.ContainsKey(mutation.GistId),
                GistMutationKind.Created when mutation.Gist is { } created =>
                    authoritative.TryGetValue(mutation.GistId, out GitHubGist? server) &&
                    AreListDetailsEquivalent(server, created),
                _ => false
            };
            if (confirmed)
            {
                if (mutation.Kind == GistMutationKind.Deleted)
                {
                    await _queryService.InvalidateTagsAsync(
                        partition,
                        (string[])[GistCacheTagPolicy.Detail(partition, mutation.GistId)],
                        cancellationToken).ConfigureAwait(false);
                }

                await _mutationJournal.RemoveAsync(partition, mutation.GistId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool AreListDetailsEquivalent(GitHubGist authoritative, GitHubGist expected) =>
        string.Equals(authoritative.Description ?? string.Empty, expected.Description ?? string.Empty, StringComparison.Ordinal) &&
        authoritative.Public == expected.Public &&
        authoritative.Files.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(expected.Files.Keys);

    private static bool AreDetailsEquivalent(GitHubGist authoritative, GitHubGist expected)
    {
        if (!AreListDetailsEquivalent(authoritative, expected))
        {
            return false;
        }

        foreach ((string filename, GitHubGistFile expectedFile) in expected.Files)
        {
            if (!authoritative.Files.TryGetValue(filename, out GitHubGistFile? actualFile))
            {
                return false;
            }

            if (!expectedFile.Truncated && expectedFile.Content is { } expectedContent &&
                !string.Equals(actualFile.Content, expectedContent, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static GitHubQuery<GitHubGist[]> CreatePageQuery(
        string accessToken,
        string userId,
        int page,
        int pageSize,
        GitHubRequestPriority priority)
    {
        string partition = GitHubAccountPartition.Resolve(accessToken, userId);
        return CreateQuery(
            accessToken,
            partition,
            $"gists?per_page={pageSize}&page={page}",
            GitHubCachePolicy.MutableResource,
            Phase0GitHubJsonSerializerContext.Default.GitHubGistArray,
            [GistCacheTagPolicy.List(partition)],
            priority);
    }

    private async Task<GistCachePageIndex?> TryGetCachePageIndexAsync(
        string partition,
        int pageSize,
        CancellationToken cancellationToken)
    {
        CachedResult<GistCachePageIndex>? cached = await _cacheStore.TryGetAsync(
            CreateCachePageIndexQuery(partition, pageSize),
            cancellationToken).ConfigureAwait(false);
        return cached?.Value is { PageSize: > 0, HighestKnownPage: > 0 } value && value.PageSize == pageSize
            ? value
            : null;
    }

    private async Task RecordCachedPageAsync(
        string partition,
        int page,
        int pageSize,
        int itemCount,
        CancellationToken cancellationToken)
    {
        try
        {
            await _cachePageIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                GitHubQuery<GistCachePageIndex> query = CreateCachePageIndexQuery(partition, pageSize);
                CachedResult<GistCachePageIndex>? cached = await _cacheStore.TryGetAsync(query, cancellationToken).ConfigureAwait(false);
                GistCachePageIndex? existing = cached?.Value;
                bool isLastPage = itemCount < pageSize;
                int highestKnownPage = isLastPage
                    ? page
                    : Math.Max(page, existing?.HighestKnownPage ?? 0);
                bool isComplete = isLastPage ||
                    (existing?.IsComplete == true && existing.HighestKnownPage > page);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                GistCachePageIndex index = new()
                {
                    PageSize = pageSize,
                    HighestKnownPage = highestKnownPage,
                    IsComplete = isComplete,
                    UpdatedAt = now
                };
                GitHubRestResponse<GistCachePageIndex> response = new(
                    HttpStatusCode.OK,
                    index,
                    IsNotModified: false,
                    ETag: null,
                    LastModified: null,
                    Link: null,
                    RateLimitRemaining: null,
                    RateLimitReset: null,
                    RetryAfter: null,
                    now);
                await _cacheStore.PutAsync(query, response, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _cachePageIndexGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            HandledFailureReporter.Report(ex, "gists-cache-page-index");
        }
    }

    private static GitHubQuery<GistCachePageIndex> CreateCachePageIndexQuery(string partition, int pageSize)
    {
        string path = $"gists/cache-index?per_page={pageSize}";
        return new GitHubQuery<GistCachePageIndex>(
            AccessToken: string.Empty,
            UserId: GitHubAccountPartition.Require(partition),
            HttpMethod.Get,
            path,
            GitHubQueryKeys.Create(
                partition,
                HttpMethod.Get,
                path,
                acceptMediaType: null,
                GistCacheJsonSerializerContext.Default.GistCachePageIndex.Type),
            ResourceKind: "gist-cache-index",
            CachePageIndexTtl,
            GistCacheJsonSerializerContext.Default.GistCachePageIndex,
            (string[])[GistCacheTagPolicy.ListIndex(partition)],
            GitHubRequestPriority.BackgroundRefresh);
    }

    private GitHubQuery<string> CreateRawFileQuery(
        string partition,
        Uri source,
        GitHubRequestPriority priority)
    {
        string identity = CreateRawFileIdentity(source);
        string relativePath = $"gist/raw/{identity}";
        return new GitHubQuery<string>(
            AccessToken: string.Empty,
            partition,
            HttpMethod.Get,
            relativePath,
            GitHubQueryKeys.Create(
                partition,
                HttpMethod.Get,
                relativePath,
                acceptMediaType: null,
                GistCacheJsonSerializerContext.Default.String.Type),
            ResourceKind: "gist-raw-file",
            _rawFileTtl,
            GistCacheJsonSerializerContext.Default.String,
            (string[])[GistCacheTagPolicy.Raw(partition, identity)],
            priority);
    }

    private static void ValidateCreateRequest(GitHubGistCreateRequest request)
    {
        if (request.Files.Count == 0 || request.Files.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A gist requires at least one named file.", nameof(request));
        }
    }

    private static void ValidateUpdateRequest(GitHubGistUpdateRequest request)
    {
        if (request.Files.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Gist file names cannot be blank.", nameof(request));
        }
    }

    private static string NormalizeGistId(string gistId) => string.IsNullOrWhiteSpace(gistId)
        ? throw new ArgumentException("A gist id is required.", nameof(gistId))
        : gistId.Trim();

    private static HttpClient CreateDefaultHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = ConnectValidatedAsync,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    {
        BaseAddress = new Uri("https://api.github.com/"),
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static Uri NormalizeRawGistUri(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("gist.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            (!uri.IsDefaultPort && uri.Port != 443))
        {
            throw new ArgumentException("A trusted GitHub Gist raw URL is required.", nameof(rawUrl));
        }

        string normalized = uri.GetComponents(
            UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
        return new Uri(normalized, UriKind.Absolute);
    }

    private static string CreateRawFileIdentity(Uri source)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri));
        return Convert.ToHexString(hash).ToLowerInvariant();
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
            throw new InvalidDataException("The Gist raw-file host could not be resolved safely.", ex);
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress))
        {
            throw new InvalidDataException("The Gist raw-file host resolves to a local or private destination.");
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPrivateAddress(address.MapToIPv4());
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 0 ||
                bytes[0] == 10 ||
                (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) ||
                bytes[0] == 127 ||
                (bytes[0] == 169 && bytes[1] == 254) ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) ||
                (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) ||
                (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) ||
                (bytes[0] == 192 && bytes[1] == 168) ||
                (bytes[0] == 198 && bytes[1] is 18 or 19) ||
                (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) ||
                (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) ||
                bytes[0] >= 224;
        }

        return address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6None) ||
            address.Equals(IPAddress.IPv6Loopback) ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast ||
            (bytes.Length == 16 && bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8) ||
            (bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static async ValueTask<Stream> ConnectValidatedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await ResolveHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress))
        {
            throw new InvalidDataException("The Gist raw-file host resolves to a local or private destination.");
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

        throw new HttpRequestException("The Gist raw-file host could not be reached through a validated address.", lastError);
    }

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    private GitHubGist[] CreatePreviewPage(int page, int pageSize)
    {
        int total = ProductPerformanceLargeAccountFixture.IsBenchmarkEnabled
            ? ProductPerformanceLargeAccountFixture.BenchmarkItemCount(ProductPerformanceLargeAccountFixture.GistCount)
            : 137;
        GitHubGist[] snapshot;
        lock (_previewGate)
        {
            snapshot = _previewOverrides.Values
                .Where(gist => gist.Id.StartsWith("preview-created-", StringComparison.Ordinal))
                .Concat(Enumerable.Range(1, total).Select(CreatePreviewGist))
                .Where(gist => !_previewDeletedIds.Contains(gist.Id))
                .Select(gist => _previewOverrides.TryGetValue(gist.Id, out GitHubGist? updated) ? updated : gist)
                .OrderByDescending(static gist => gist.UpdatedAt)
                .ThenBy(static gist => gist.Id, StringComparer.Ordinal)
                .ToArray();
        }

        return snapshot.Skip((page - 1) * pageSize).Take(pageSize).ToArray();
    }

    private static GitHubGist CreatePreviewGist(int index)
    {
        string id = $"preview-gist-{index:D3}";
        int templateIndex = (index - 1) % 6;
        string[] descriptions =
        [
            "Release checklist",
            "Helpful PowerShell commands",
            "Repository query example",
            "Markdown table example",
            "Package verification script",
            "Search filter sample"
        ];
        string[] filenames =
        [
            "release-checklist.md",
            "release-tools.ps1",
            "repository-query.cs",
            "markdown-table.md",
            "verify-package.ps1",
            "search-filter.cs"
        ];
        string[] contents =
        [
            "# Release checklist\n\n- Review open pull requests\n- Verify the Windows package\n- Publish release notes",
            "Get-ChildItem .\\artifacts | Sort-Object LastWriteTime -Descending",
            "public static string RepositoryQuery(string owner, string name) => $\"{owner}/{name}\";",
            "| Area | Status |\n| --- | --- |\n| Windows package | Ready |",
            "Write-Output 'Package verification complete'",
            "public static bool Matches(string value, string query) => value.Contains(query, StringComparison.OrdinalIgnoreCase);"
        ];
        string filename = filenames[templateIndex];
        string content = contents[templateIndex];
        return new GitHubGist
        {
            Id = id,
            Description = descriptions[templateIndex],
            HtmlUrl = $"https://gist.github.com/jithub/{id}",
            ApiUrl = $"https://api.github.com/gists/{id}",
            Public = index % 4 != 0,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-index - 4),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-index),
            Comments = index % 4,
            Owner = new GitHubActor { Login = "jithub", AvatarUrl = string.Empty },
            Files = new Dictionary<string, GitHubGistFile>(StringComparer.OrdinalIgnoreCase)
            {
                [filename] = new GitHubGistFile
                {
                    Filename = filename,
                    Language = filename.EndsWith(".cs", StringComparison.Ordinal) ? "C#" : filename.EndsWith(".md", StringComparison.Ordinal) ? "Markdown" : "PowerShell",
                    Type = "text/plain",
                    Size = content.Length,
                    Content = content
                }
            }
        };
    }

    private static int ParsePreviewIndex(string gistId)
    {
        string suffix = gistId.Split('-').LastOrDefault() ?? "1";
        return int.TryParse(suffix, out int index) ? Math.Max(1, index) : 1;
    }

    private static GitHubGist CreateFromRequest(string id, GitHubGistCreateRequest request)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new GitHubGist
        {
            Id = id,
            Description = request.Description,
            HtmlUrl = $"https://gist.github.com/jithub/{id}",
            ApiUrl = $"https://api.github.com/gists/{id}",
            Public = request.Public,
            CreatedAt = now,
            UpdatedAt = now,
            Owner = new GitHubActor { Login = "jithub" },
            Files = request.Files.ToDictionary(
                static pair => pair.Key,
                static pair => new GitHubGistFile
                {
                    Filename = pair.Key,
                    Content = pair.Value.Content,
                    Size = pair.Value.Content.Length,
                    Type = "text/plain"
                },
                StringComparer.OrdinalIgnoreCase)
        };
    }

    private static GitHubGist ApplyUpdate(GitHubGist gist, GitHubGistUpdateRequest request)
    {
        gist.Description = request.Description;
        foreach ((string originalName, GitHubGistFileUpdateRequest? update) in request.Files)
        {
            if (update is null)
            {
                gist.Files.Remove(originalName);
                continue;
            }

            string filename = string.IsNullOrWhiteSpace(update.Filename) ? originalName : update.Filename;
            gist.Files.TryGetValue(originalName, out GitHubGistFile? existingFile);
            gist.Files.Remove(originalName);
            string content = update.Content ?? existingFile?.Content ?? string.Empty;
            gist.Files[filename] = new GitHubGistFile
            {
                Filename = filename,
                Content = content,
                Size = update.Content is null ? existingFile?.Size ?? content.Length : content.Length,
                Type = existingFile?.Type ?? "text/plain",
                Language = existingFile?.Language,
                RawUrl = existingFile?.RawUrl ?? string.Empty,
                Truncated = existingFile?.Truncated ?? false
            };
        }

        gist.UpdatedAt = DateTimeOffset.UtcNow;
        return gist;
    }

    private sealed class GistLibraryReconciliationSession(int pageSize)
    {
        public int PageSize { get; } = pageSize;

        public int NextPage { get; set; } = 1;

        public Dictionary<string, GitHubGist> Items { get; } = new(StringComparer.Ordinal);
    }

    private sealed class NullGistMutationJournal : IGistMutationJournal
    {
        public static NullGistMutationJournal Instance { get; } = new();

        public Task<IReadOnlyList<GistMutationJournalEntry>> ReadAsync(
            string accountPartition,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GistMutationJournalEntry>>([]);

        public Task RecordUpsertAsync(
            string accountPartition,
            string gistId,
            GitHubGist gist,
            bool isCreate,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RecordDeleteAsync(
            string accountPartition,
            string gistId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(
            string accountPartition,
            string gistId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAccountAsync(
            string accountPartition,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

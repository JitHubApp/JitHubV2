using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class GitHubGistQueryServiceTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        Path.GetTempPath(),
        "JitHubGistQueryServiceTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetPage_ClampsPaginationAndUsesListTags()
    {
        RecordingQueryService queryService = new();
        GitHubGistQueryService service = new(
            queryService,
            new ImmediateRequestQueue(),
            new NullGitHubCacheStore(),
            new HttpClient(new RecordingHandler()));

        await service.GetPageAsync(
            "token",
            "42",
            page: 0,
            pageSize: 500,
            QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority.BackgroundRefresh);

        Assert.Equal("gists?per_page=100&page=1", queryService.LastRelativePath);
        Assert.Equal(QueryFetchPolicy.StaleFirst, queryService.LastFetchPolicy);
        Assert.Equal(GitHubRequestPriority.BackgroundRefresh, queryService.LastPriority);
        Assert.Equal([GistCacheTagPolicy.List("42")], queryService.LastTags);
    }

    [Fact]
    public async Task CachedLibrary_RestoresEveryIndexedPageAcrossServiceRecreation()
    {
        MemoryGistCacheStore cache = new();
        GitHubGist[] firstPage = Enumerable.Range(1, 100)
            .Select(index => CreateGist(index.ToString(), $"Gist {index}", true, $"{index}.txt", "Text", DateTimeOffset.UtcNow))
            .ToArray();
        GitHubGist[] secondPage =
        [
            CreateGist("101", "Gist 101", true, "101.txt", "Text", DateTimeOffset.UtcNow),
            CreateGist("102", "Gist 102", false, "102.txt", "Text", DateTimeOffset.UtcNow)
        ];
        CacheWritingPageQueryService writer = new(cache, new Dictionary<int, GitHubGist[]>
        {
            [1] = firstPage,
            [2] = secondPage
        });
        GitHubGistQueryService firstService = new(
            writer,
            new ImmediateRequestQueue(),
            cache,
            new HttpClient(new RecordingHandler()));

        await firstService.GetPageAsync("token", "42", 1, 100, QueryFetchPolicy.NetworkOnly);
        await firstService.GetPageAsync("token", "42", 2, 100, QueryFetchPolicy.NetworkOnly);

        GitHubGistQueryService relaunched = new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            cache,
            new HttpClient(new RecordingHandler()));
        GistCachedLibrarySnapshot restored = await relaunched.GetCachedLibraryAsync("token", "42", 100);

        Assert.True(restored.IsComplete);
        Assert.Equal(2, restored.CachedPageCount);
        Assert.Equal(102, restored.Items.Length);
        Assert.Contains(restored.Items, static gist => gist.Id == "1");
        Assert.Contains(restored.Items, static gist => gist.Id == "102");
    }

    [Fact]
    public async Task CachedLibrary_TwoAccountsSurviveRelaunchAndCurrentAccountMutations()
    {
        MemoryGistCacheStore cache = new();
        GitHubGist[] accountAFirst = Enumerable.Range(1, 100)
            .Select(index => CreateGist($"a-{index}", $"Account A {index}", true, $"a-{index}.txt", "Text", DateTimeOffset.UtcNow))
            .ToArray();
        GitHubGist[] accountASecond = [CreateGist("a-101", "Account A 101", true, "a-101.txt", "Text", DateTimeOffset.UtcNow)];
        GitHubGist[] accountBFirst = Enumerable.Range(1, 100)
            .Select(index => CreateGist($"b-{index}", $"Account B {index}", true, $"b-{index}.txt", "Text", DateTimeOffset.UtcNow))
            .ToArray();
        GitHubGist[] accountBSecond = [CreateGist("b-101", "Account B 101", false, "b-101.txt", "Text", DateTimeOffset.UtcNow)];
        GitHubGistQueryService accountA = new(
            new CacheWritingPageQueryService(cache, new Dictionary<int, GitHubGist[]> { [1] = accountAFirst, [2] = accountASecond }),
            new ImmediateRequestQueue(),
            cache,
            new HttpClient(new RecordingHandler()),
            mutationJournal: new GistMutationJournal(Path.Combine(_cacheRoot, "mutation-journal.json")));
        GitHubGistQueryService accountB = new(
            new CacheWritingPageQueryService(cache, new Dictionary<int, GitHubGist[]> { [1] = accountBFirst, [2] = accountBSecond }),
            new ImmediateRequestQueue(),
            cache,
            new HttpClient(new RecordingHandler()),
            mutationJournal: new GistMutationJournal(Path.Combine(_cacheRoot, "mutation-journal.json")));

        await accountA.GetPageAsync("token-a", "42", 1, 100, QueryFetchPolicy.NetworkOnly);
        await accountA.GetPageAsync("token-a", "42", 2, 100, QueryFetchPolicy.NetworkOnly);
        await accountB.GetPageAsync("token-b", "84", 1, 100, QueryFetchPolicy.NetworkOnly);
        await accountB.GetPageAsync("token-b", "84", 2, 100, QueryFetchPolicy.NetworkOnly);
        cache.AddTaggedSentinel("42", "target-detail-a-1", GistCacheTagPolicy.Detail("42", "a-1"));
        cache.AddTaggedSentinel("42", "target-detail-a-2", GistCacheTagPolicy.Detail("42", "a-2"));
        cache.AddTaggedSentinel("42", "unrelated-detail", GistCacheTagPolicy.Detail("42", "a-99"));
        cache.AddTaggedSentinel("42", "immutable-raw", GistCacheTagPolicy.Raw("42", "raw-a-1"));
        cache.AddTaggedSentinel("84", "other-account-detail", GistCacheTagPolicy.Detail("84", "b-1"));
        cache.AddTaggedSentinel("84", "other-account-raw", GistCacheTagPolicy.Raw("84", "raw-b-1"));
        await accountA.CreateAsync(
            "token-a",
            "42",
            new GitHubGistCreateRequest
            {
                Description = "Created",
                Files = new Dictionary<string, GitHubGistFileWriteRequest> { ["created.txt"] = new() { Content = "created" } }
            });
        await accountA.UpdateAsync(
            "token-a",
            "42",
            "a-1",
            new GitHubGistUpdateRequest
            {
                Description = "Updated",
                Files = new Dictionary<string, GitHubGistFileUpdateRequest?>
                {
                    ["a-1.txt"] = new() { Content = "updated" }
                }
            });
        await accountA.DeleteAsync("token-a", "42", "a-2");

        Assert.True(cache.HasEntriesWithTag(GistCacheTagPolicy.Detail("42", "a-1")));
        Assert.True(cache.HasEntriesWithTag(GistCacheTagPolicy.Detail("42", "a-2")));
        Assert.True(cache.HasEntriesWithTag(GistCacheTagPolicy.Detail("42", "a-99")));
        Assert.True(cache.HasEntriesWithTag(GistCacheTagPolicy.Raw("42", "raw-a-1")));
        Assert.True(cache.HasEntriesWithTag(GistCacheTagPolicy.Detail("84", "b-1")));
        Assert.True(cache.HasEntriesWithTag(GistCacheTagPolicy.Raw("84", "raw-b-1")));

        GitHubGistQueryService relaunched = new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            cache,
            new HttpClient(new ThrowingRawFileHandler()),
            mutationJournal: new GistMutationJournal(Path.Combine(_cacheRoot, "mutation-journal.json")));
        GistCachedLibrarySnapshot restoredA = await relaunched.GetCachedLibraryAsync("token-a", "42", 100);
        GistCachedLibrarySnapshot restoredB = await relaunched.GetCachedLibraryAsync("token-b", "84", 100);

        Assert.True(restoredA.IsComplete);
        Assert.True(restoredB.IsComplete);
        Assert.Equal(101, restoredA.Items.Length);
        Assert.Equal(101, restoredB.Items.Length);
        Assert.Contains(restoredA.Items, static gist => gist.Id == "created" && gist.Description == "Created");
        Assert.Contains(restoredA.Items, static gist => gist.Id == "a-1" && gist.Description == "Updated");
        Assert.DoesNotContain(restoredA.Items, static gist => gist.Id == "a-2");
        Assert.All(restoredB.Items, static gist => Assert.StartsWith("b-", gist.Id, StringComparison.Ordinal));
        Assert.True(cache.HasEntriesWithTag(GistCacheTagPolicy.List("42")));
        Assert.True(cache.HasEntriesWithTag(GistCacheTagPolicy.List("84")));
        Assert.True(cache.HasEntriesWithTag(GistCacheTagPolicy.ListIndex("42")));
        Assert.True(cache.HasEntriesWithTag(GistCacheTagPolicy.ListIndex("84")));
    }

    [Fact]
    public async Task CachedLibrary_RestoresPagesAfterMissingPageButReportsIncompleteScope()
    {
        MemoryGistCacheStore cache = new();
        Dictionary<int, GitHubGist[]> pages = new()
        {
            [1] = Enumerable.Range(1, 100)
                .Select(index => CreateGist(index.ToString(), $"Gist {index}", true, $"{index}.txt", "Text", DateTimeOffset.UtcNow))
                .ToArray(),
            [2] = Enumerable.Range(101, 100)
                .Select(index => CreateGist(index.ToString(), $"Gist {index}", true, $"{index}.txt", "Text", DateTimeOffset.UtcNow))
                .ToArray(),
            [3] = [CreateGist("201", "Gist 201", true, "201.txt", "Text", DateTimeOffset.UtcNow)]
        };
        GitHubGistQueryService service = new(
            new CacheWritingPageQueryService(cache, pages),
            new ImmediateRequestQueue(),
            cache,
            new HttpClient(new RecordingHandler()));
        await service.GetPageAsync("token", "42", 1, 100, QueryFetchPolicy.NetworkOnly);
        await service.GetPageAsync("token", "42", 2, 100, QueryFetchPolicy.NetworkOnly);
        await service.GetPageAsync("token", "42", 3, 100, QueryFetchPolicy.NetworkOnly);
        cache.Remove("42", "gists?per_page=100&page=2");

        GistCachedLibrarySnapshot restored = await service.GetCachedLibraryAsync("token", "42", 100);

        Assert.False(restored.IsComplete);
        Assert.Equal(2, restored.CachedPageCount);
        Assert.Equal(101, restored.Items.Length);
        Assert.Contains(restored.Items, static gist => gist.Id == "201");
    }

    [Fact]
    public async Task PublicPreview_PaginatesAutomaticallyBeyondThirtyRows()
    {
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            new NullGitHubCacheStore(),
            new HttpClient(new RecordingHandler()));

        CachedResult<GitHubGist[]> first = await service.GetPageAsync(GitHubAuthenticationConstants.PublicAccessToken, "public", 1, 100);
        CachedResult<GitHubGist[]> second = await service.GetPageAsync(GitHubAuthenticationConstants.PublicAccessToken, "public", 2, 100);
        CachedResult<GitHubGist[]> third = await service.GetPageAsync(GitHubAuthenticationConstants.PublicAccessToken, "public", 3, 100);

        Assert.Equal(100, first.Value!.Length);
        Assert.Equal(37, second.Value!.Length);
        Assert.Empty(third.Value!);
        Assert.Equal(137, first.Value.Concat(second.Value).Select(static gist => gist.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task PublicPreview_CrudRemainsVisibleToSubsequentReads()
    {
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            new NullGitHubCacheStore(),
            new HttpClient(new RecordingHandler()));
        GitHubGist created = (await service.CreateAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "public",
            new GitHubGistCreateRequest
            {
                Description = "Created locally",
                Files = new Dictionary<string, GitHubGistFileWriteRequest>
                {
                    ["one.txt"] = new() { Content = "one" },
                    ["two.txt"] = new() { Content = "two" }
                }
            })).Value;

        GitHubGist detail = (await service.GetDetailAsync(GitHubAuthenticationConstants.PublicAccessToken, "public", created.Id)).Value!;
        Assert.Equal("Created locally", detail.Description);

        await service.UpdateAsync(
            GitHubAuthenticationConstants.PublicAccessToken,
            "public",
            created.Id,
            new GitHubGistUpdateRequest
            {
                Description = "Edited locally",
                Files = new Dictionary<string, GitHubGistFileUpdateRequest?>
                {
                    ["one.txt"] = new() { Filename = "renamed-one.txt", Content = "updated" },
                    ["two.txt"] = new() { Filename = "renamed-two.txt", Content = null }
                }
            });
        GitHubGist updated = (await service.GetDetailAsync(GitHubAuthenticationConstants.PublicAccessToken, "public", created.Id)).Value!;
        Assert.Equal("Edited locally", updated.Description);
        Assert.Equal("updated", updated.Files["renamed-one.txt"].Content);
        Assert.Equal("two", updated.Files["renamed-two.txt"].Content);

        await service.DeleteAsync(GitHubAuthenticationConstants.PublicAccessToken, "public", created.Id);
        GitHubGist[] all = (await service.GetPageAsync(GitHubAuthenticationConstants.PublicAccessToken, "public", 1, 100)).Value!
            .Concat((await service.GetPageAsync(GitHubAuthenticationConstants.PublicAccessToken, "public", 2, 100)).Value!)
            .ToArray();
        Assert.DoesNotContain(all, gist => gist.Id == created.Id);
    }

    [Fact]
    public async Task GetDetail_UsesStableGistCacheTag()
    {
        RecordingQueryService queryService = new();
        GitHubGistQueryService service = new(
            queryService,
            new ImmediateRequestQueue(),
            new NullGitHubCacheStore(),
            new HttpClient(new RecordingHandler()));

        await service.GetDetailAsync("token", "42", " sample-id ");

        Assert.Equal("gists/sample-id", queryService.LastRelativePath);
        Assert.Equal([GistCacheTagPolicy.Detail("42", "sample-id")], queryService.LastTags);
    }

    [Fact]
    public async Task GetRawFileContent_UsesTrustedHostWithoutSendingOAuthToken()
    {
        RawFileHandler handler = new("complete gist content");
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            new NullGitHubCacheStore(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            hostAddressResolver: ResolvePublicHostAsync);

        string content = await service.GetRawFileContentAsync(
            "42",
            "https://gist.githubusercontent.com/octo/id/raw/revision/file.txt");

        Assert.Equal("complete gist content", content);
        Assert.Equal("gist.githubusercontent.com", handler.LastHost);
        Assert.Null(handler.LastAuthorization);
    }

    [Fact]
    public async Task GetRawFile_RejectsUnstableCurrentAccountPartition()
    {
        GitHubGistQueryService service = CreateRawService(new NullGitHubCacheStore(), new RawFileHandler("unused"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetRawFileAsync(
            "current",
            "https://gist.githubusercontent.com/octo/id/raw/revision/file.txt"));
    }

    [Fact]
    public async Task GetRawFileContent_RealQueueKeepsConcurrentDifferentUrlsDistinct()
    {
        PathRawFileHandler handler = new();
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new GitHubRequestQueue(foregroundReadConcurrency: 2, backgroundReadConcurrency: 1, mutationConcurrency: 1),
            new NullGitHubCacheStore(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            hostAddressResolver: ResolvePublicHostAsync);

        Task<string> first = service.GetRawFileContentAsync(
            "42",
            "https://gist.githubusercontent.com/octo/id/raw/revision/first.txt");
        Task<string> second = service.GetRawFileContentAsync(
            "42",
            "https://gist.githubusercontent.com/octo/id/raw/revision/second.txt");

        string[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(new[] { "first", "second" }, results);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetRawFile_PersistsAcrossServiceRecreationAndPartitionsByAccount()
    {
        SqliteGitHubCacheStore firstStore = CreatePersistentCacheStore();
        GitHubGistQueryService firstService = CreateRawService(
            firstStore,
            new RawFileHandler("persisted content"));
        const string rawUrl = "https://gist.githubusercontent.com/octo/id/raw/revision/persisted.txt";

        CachedResult<string> network = await firstService.GetRawFileAsync(
            "42",
            rawUrl,
            QueryFetchPolicy.NetworkOnly);

        Assert.Equal("persisted content", network.Value);

        ThrowingRawFileHandler offlineHandler = new();
        GitHubGistQueryService relaunchedService = CreateRawService(
            CreatePersistentCacheStore(),
            offlineHandler);
        CachedResult<string> cached = await relaunchedService.GetRawFileAsync(
            "42",
            rawUrl,
            QueryFetchPolicy.CacheFirst);

        Assert.Equal("persisted content", cached.Value);
        Assert.Equal(CacheState.Fresh, cached.CacheState);
        Assert.Equal(0, offlineHandler.RequestCount);

        await Assert.ThrowsAsync<HttpRequestException>(() => relaunchedService.GetRawFileAsync(
            "84",
            rawUrl,
            QueryFetchPolicy.CacheFirst));
        Assert.Equal(1, offlineHandler.RequestCount);
    }

    [Fact]
    public async Task GetRawFile_UsesValidatorsAndReusesCachedPayloadOnNotModified()
    {
        ConditionalRawFileHandler handler = new();
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            CreatePersistentCacheStore(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            hostAddressResolver: ResolvePublicHostAsync,
            rawFileTtl: TimeSpan.Zero);
        const string rawUrl = "https://gist.githubusercontent.com/octo/id/raw/revision/conditional.txt";

        CachedResult<string> first = await service.GetRawFileAsync("42", rawUrl, QueryFetchPolicy.NetworkOnly);
        CachedResult<string> second = await service.GetRawFileAsync("42", rawUrl, QueryFetchPolicy.NetworkOnly);

        Assert.Equal("validator payload", first.Value);
        Assert.Equal("validator payload", second.Value);
        Assert.Equal(2, handler.RequestCount);
        Assert.True(handler.SawIfNoneMatch);
        Assert.True(handler.SawIfModifiedSince);
    }

    [Fact]
    public async Task GetRawFile_RejectsOversizedContentLengthBeforeReadingBody()
    {
        OversizedLengthHandler handler = new();
        GitHubGistQueryService service = CreateRawService(new NullGitHubCacheStore(), handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetRawFileContentAsync(
            "42",
            "https://gist.githubusercontent.com/octo/id/raw/revision/large.txt"));

        Assert.False(handler.Content.StreamRequested);
    }

    [Fact]
    public async Task GetRawFile_RejectsUnknownLengthStreamBeyondByteLimit()
    {
        OversizedStreamHandler handler = new();
        GitHubGistQueryService service = CreateRawService(new NullGitHubCacheStore(), handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetRawFileContentAsync(
            "42",
            "https://gist.githubusercontent.com/octo/id/raw/revision/streamed-large.txt"));

        Assert.True(handler.Stream.BytesRead <= GitHubGistQueryService.MaximumRawFileBytes + 1);
    }

    [Fact]
    public async Task GetRawFile_RejectsPrivateDnsDestinationBeforeSendingRequest()
    {
        RawFileHandler handler = new("unused");
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            new NullGitHubCacheStore(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            hostAddressResolver: static (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetRawFileContentAsync(
            "42",
            "https://gist.githubusercontent.com/octo/id/raw/revision/private.txt"));
        Assert.Null(handler.LastHost);
    }

    [Theory]
    [InlineData("http://gist.githubusercontent.com/octo/id/raw/revision/file.txt")]
    [InlineData("https://example.com/octo/id/raw/revision/file.txt")]
    [InlineData("https://127.0.0.1/octo/id/raw/revision/file.txt")]
    public async Task GetRawFile_RejectsRedirectsOutsideTrustedRawOrigin(string location)
    {
        RedirectRawFileHandler handler = new(location);
        GitHubGistQueryService service = CreateRawService(new NullGitHubCacheStore(), handler);

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetRawFileContentAsync(
            "42",
            "https://gist.githubusercontent.com/octo/id/raw/revision/file.txt"));

        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("198.51.100.10")]
    [InlineData("203.0.113.10")]
    [InlineData("2001:db8::1")]
    public async Task GetRawFile_RejectsReservedDnsDestinations(string address)
    {
        RawFileHandler handler = new("unused");
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            new NullGitHubCacheStore(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            hostAddressResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.GetRawFileContentAsync(
            "42",
            "https://gist.githubusercontent.com/octo/id/raw/revision/private.txt"));
        Assert.Null(handler.LastHost);
    }

    [Fact]
    public async Task GetRawFile_UsesOpaqueHashIdentityAndGistCacheTags()
    {
        RecordingRawCacheStore cache = new();
        GitHubGistQueryService service = CreateRawService(cache, new RawFileHandler("content"));

        await service.GetRawFileContentAsync(
            "42",
            "https://gist.githubusercontent.com/octo/private-id/raw/revision/secret-name.txt");

        Assert.Equal("42", cache.UserId);
        Assert.StartsWith("gist/raw/", cache.RelativePath, StringComparison.Ordinal);
        Assert.DoesNotContain("octo", cache.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-name", cache.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, cache.RelativePath[(cache.RelativePath.LastIndexOf('/') + 1)..].Length);
        string identity = cache.RelativePath[(cache.RelativePath.LastIndexOf('/') + 1)..];
        Assert.Equal([GistCacheTagPolicy.Raw("42", identity)], cache.Tags);
    }

    [Fact]
    public async Task GetRawFile_StaleBackgroundRefreshIsObservedCancelableAndDrainable()
    {
        StaleRawCacheStore cache = new("cached while offline");
        CancellableRawFileHandler handler = new();
        GitHubGistQueryService service = CreateRawService(cache, handler);
        using CancellationTokenSource cancellation = new();

        CachedResult<string> result = await service.GetRawFileAsync(
            "42",
            "https://gist.githubusercontent.com/octo/id/raw/revision/stale.txt",
            QueryFetchPolicy.StaleFirst,
            cancellationToken: cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await service.DrainBackgroundWorkAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("cached while offline", result.Value);
        Assert.True(result.IsRefreshInProgress);
        Assert.True(handler.CancellationObserved);
    }

    [Theory]
    [InlineData("http://gist.githubusercontent.com/octo/id/raw/file.txt")]
    [InlineData("https://example.com/file.txt")]
    [InlineData("https://127.0.0.1/file.txt")]
    public async Task GetRawFileContent_RejectsUntrustedSources(string source)
    {
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            new NullGitHubCacheStore(),
            new HttpClient(new RawFileHandler("unused")) { BaseAddress = new Uri("https://api.github.com/") });

        await Assert.ThrowsAsync<ArgumentException>(() => service.GetRawFileContentAsync("42", source));
    }

    [Fact]
    public void SynchronizationGate_InvalidatesOlderReconciliationAfterMutation()
    {
        GistSynchronizationGate gate = new();
        int reconciliation = gate.Capture();

        Assert.True(gate.IsCurrent(reconciliation));
        gate.Invalidate();

        Assert.False(gate.IsCurrent(reconciliation));
        Assert.True(gate.IsCurrent(gate.Capture()));
    }

    [Fact]
    public void FileContentPolicy_LabelsNonEmptyTruncatedContentAsIncomplete()
    {
        GitHubGistFile file = new() { Content = "partial", Truncated = true };

        Assert.Equal("partial", GistFileContentPolicy.GetPreviewText(file));
        Assert.Contains("incomplete", GistFileContentPolicy.GetTruncationMessage(file), StringComparison.OrdinalIgnoreCase);

        file.Content = "complete";
        file.Truncated = false;
        Assert.Empty(GistFileContentPolicy.GetTruncationMessage(file));
    }

    [Fact]
    public void FileRenderPolicy_CapsTenMiBPreviewWithinInteractiveBudget()
    {
        string content = new('x', GitHubGistQueryService.MaximumRawFileBytes);

        Stopwatch stopwatch = Stopwatch.StartNew();
        GistFileRenderModel result = GistFileRenderPolicy.Create(content);
        stopwatch.Stop();

        Assert.True(result.IsCapped);
        Assert.Equal(GistFileRenderPolicy.MaximumPreviewCharacters, result.PreviewText.Length);
        Assert.Equal(content.Length, result.FullCharacterCount);
        Assert.Contains("Copy", result.StatusText, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250), $"Preview projection took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task Create_UsesMutationLaneWithoutDestroyingOfflineLibrary()
    {
        RecordingQueryService queryService = new();
        ImmediateRequestQueue queue = new();
        RecordingHandler handler = new();
        GitHubGistQueryService service = CreateService(queryService, queue, handler);

        GitHubGist created = (await service.CreateAsync(
            "token",
            "42",
            new GitHubGistCreateRequest
            {
                Description = "Native sample",
                Public = false,
                Files = new Dictionary<string, GitHubGistFileWriteRequest>
                {
                    ["sample.cs"] = new() { Content = "return true;" }
                }
            })).Value;

        Assert.Equal("created", created.Id);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("gists", handler.LastRelativePath);
        Assert.Contains("\"public\":false", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("sample.cs", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal(GitHubRequestPriority.Mutation, queue.LastPriority);
        Assert.Empty(queryService.InvalidatedTagSets);
        GistMutationJournalEntry journalEntry = Assert.Single(await new GistMutationJournal(
            Path.Combine(_cacheRoot, "mutation-journal.json")).ReadAsync("42"));
        Assert.Equal(GistMutationKind.Created, journalEntry.Kind);
        Assert.Equal("created", journalEntry.GistId);
    }

    [Fact]
    public async Task Update_UsesPatchAndSupportsRenameAndRemoval()
    {
        RecordingQueryService queryService = new();
        RecordingHandler handler = new();
        GitHubGistQueryService service = CreateService(queryService, new ImmediateRequestQueue(), handler);

        await service.UpdateAsync(
            "token",
            "42",
            "gist-7",
            new GitHubGistUpdateRequest
            {
                Description = "Updated",
                Files = new Dictionary<string, GitHubGistFileUpdateRequest?>
                {
                    ["old.txt"] = new() { Filename = "new.txt", Content = "updated" },
                    ["remove.txt"] = null
                }
            });

        Assert.Equal(HttpMethod.Patch, handler.LastMethod);
        Assert.Equal("gists/gist-7", handler.LastRelativePath);
        Assert.Contains("new.txt", handler.LastBody, StringComparison.Ordinal);
        Assert.Contains("\"remove.txt\":null", handler.LastBody, StringComparison.Ordinal);
        Assert.Empty(queryService.InvalidatedTagSets);
        GistMutationJournalEntry journalEntry = Assert.Single(await new GistMutationJournal(
            Path.Combine(_cacheRoot, "mutation-journal.json")).ReadAsync("42"));
        Assert.Equal(GistMutationKind.Updated, journalEntry.Kind);
        Assert.Equal("gist-7", journalEntry.GistId);
    }

    [Fact]
    public async Task Delete_UsesDeleteAndInvalidatesDetail()
    {
        RecordingQueryService queryService = new();
        RecordingHandler handler = new();
        GitHubGistQueryService service = CreateService(queryService, new ImmediateRequestQueue(), handler);

        await service.DeleteAsync("token", "42", "gist-9");

        Assert.Equal(HttpMethod.Delete, handler.LastMethod);
        Assert.Equal("gists/gist-9", handler.LastRelativePath);
        Assert.Empty(queryService.InvalidatedTagSets);
        GistMutationJournalEntry journalEntry = Assert.Single(await new GistMutationJournal(
            Path.Combine(_cacheRoot, "mutation-journal.json")).ReadAsync("42"));
        Assert.Equal(GistMutationKind.Deleted, journalEntry.Kind);
        Assert.Equal("gist-9", journalEntry.GistId);
    }

    [Fact]
    public async Task ConcurrentCreates_WithIdenticalStableQueueKeys_SendBothDistinctMutations()
    {
        ConcurrentMutationHandler handler = new(expectedRequests: 2);
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new GitHubRequestQueue(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 2),
            new NullGitHubCacheStore(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            mutationJournal: new GistMutationJournal(Path.Combine(_cacheRoot, "concurrent-create-journal.json")));

        Task<GistMutationResult<GitHubGist>> first = service.CreateAsync(
            "token",
            "42",
            CreateMutationRequest("First create"));
        Task<GistMutationResult<GitHubGist>> second = service.CreateAsync(
            "token",
            "42",
            CreateMutationRequest("Second create"));

        GistMutationResult<GitHubGist>[] results = await Task.WhenAll(first, second);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, results.Select(static result => result.Value.Description).Distinct(StringComparer.Ordinal).Count());
        Assert.All(results, static result => Assert.Equal(GistMutationDurability.Durable, result.Durability));
        Assert.Contains(handler.Bodies, static body => body.Contains("First create", StringComparison.Ordinal));
        Assert.Contains(handler.Bodies, static body => body.Contains("Second create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConcurrentUpdates_WithIdenticalStableQueueKeys_SendBothDistinctMutations()
    {
        ConcurrentMutationHandler handler = new(expectedRequests: 2);
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new GitHubRequestQueue(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 2),
            new NullGitHubCacheStore(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            mutationJournal: new GistMutationJournal(Path.Combine(_cacheRoot, "concurrent-update-journal.json")));

        Task<GistMutationResult<GitHubGist>> first = service.UpdateAsync(
            "token",
            "42",
            "same-gist",
            new GitHubGistUpdateRequest { Description = "First update" });
        Task<GistMutationResult<GitHubGist>> second = service.UpdateAsync(
            "token",
            "42",
            "same-gist",
            new GitHubGistUpdateRequest { Description = "Second update" });

        GistMutationResult<GitHubGist>[] results = await Task.WhenAll(first, second);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, results.Select(static result => result.Value.Description).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(handler.Bodies, static body => body.Contains("First update", StringComparison.Ordinal));
        Assert.Contains(handler.Bodies, static body => body.Contains("Second update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_JournalWriteFailure_ReturnsRemoteSuccessAndInvalidatesRecoveryCaches()
    {
        RecordingQueryService queryService = new();
        RecordingHandler handler = new();
        GitHubGistQueryService service = CreateServiceWithJournal(queryService, handler, new FailingMutationJournal());

        GistMutationResult<GitHubGist> result = await service.CreateAsync(
            "token",
            "42",
            CreateMutationRequest("Created remotely"));

        Assert.Equal("created", result.Value.Id);
        Assert.True(result.IsDurabilityDegraded);
        Assert.Equal(1, handler.RequestCount);
        AssertMutationRecoveryInvalidated(queryService, "42", "created");
    }

    [Fact]
    public async Task Update_JournalWriteFailure_ReturnsRemoteSuccessAndInvalidatesRecoveryCaches()
    {
        RecordingQueryService queryService = new();
        RecordingHandler handler = new();
        GitHubGistQueryService service = CreateServiceWithJournal(queryService, handler, new FailingMutationJournal());

        GistMutationResult<GitHubGist> result = await service.UpdateAsync(
            "token",
            "42",
            "gist-7",
            new GitHubGistUpdateRequest { Description = "Updated" });

        Assert.Equal("gist-7", result.Value.Id);
        Assert.True(result.IsDurabilityDegraded);
        Assert.Equal(1, handler.RequestCount);
        AssertMutationRecoveryInvalidated(queryService, "42", "gist-7");
    }

    [Fact]
    public async Task Delete_JournalWriteFailure_ReturnsRemoteSuccessAndInvalidatesRecoveryCaches()
    {
        RecordingQueryService queryService = new();
        RecordingHandler handler = new();
        GitHubGistQueryService service = CreateServiceWithJournal(queryService, handler, new FailingMutationJournal());

        GistMutationResult<bool> result = await service.DeleteAsync("token", "42", "gist-9");

        Assert.True(result.Value);
        Assert.True(result.IsDurabilityDegraded);
        Assert.Equal(1, handler.RequestCount);
        AssertMutationRecoveryInvalidated(queryService, "42", "gist-9");
    }

    [Fact]
    public async Task FailedMutation_DoesNotWriteJournalOrRegressCachedLibrary()
    {
        MemoryGistCacheStore cache = new();
        GitHubGist existing = CreateGist(
            "existing",
            "Cached truth",
            true,
            "existing.txt",
            "Text",
            DateTimeOffset.UtcNow);
        GitHubGistQueryService cacheWriter = new(
            new CacheWritingPageQueryService(cache, new Dictionary<int, GitHubGist[]> { [1] = [existing] }),
            new ImmediateRequestQueue(),
            cache,
            new HttpClient(new RecordingHandler()));
        await cacheWriter.GetPageAsync("token", "42", 1, 100, QueryFetchPolicy.NetworkOnly);

        string journalPath = Path.Combine(_cacheRoot, "failed-mutation-journal.json");
        GitHubGistQueryService service = new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            cache,
            new HttpClient(new FailingMutationHandler()),
            mutationJournal: new GistMutationJournal(journalPath));

        await Assert.ThrowsAsync<GitHubApiException>(() => service.UpdateAsync(
            "token",
            "42",
            "existing",
            new GitHubGistUpdateRequest { Description = "Rejected" }));

        Assert.Empty(await new GistMutationJournal(journalPath).ReadAsync("42"));
        GistCachedLibrarySnapshot restored = await service.GetCachedLibraryAsync("token", "42", 100);
        GitHubGist cached = Assert.Single(restored.Items);
        Assert.Equal("Cached truth", cached.Description);
    }

    [Fact]
    public async Task Journal_ClearsOnlyOperationsConfirmedByAuthoritativeReconciliation()
    {
        string journalPath = Path.Combine(_cacheRoot, "reconciliation-journal.json");
        GistMutationJournal journal = new(journalPath);
        GitHubGist created = CreateGist("created", "Created", false, "created.txt", "Text", DateTimeOffset.UtcNow);
        GitHubGist updated = CreateGist("updated", "Updated", true, "updated.txt", "Text", DateTimeOffset.UtcNow);
        await journal.RecordUpsertAsync("42", created.Id, created, isCreate: true);
        await journal.RecordUpsertAsync("42", updated.Id, updated, isCreate: false);
        await journal.RecordDeleteAsync("42", "deleted");

        GitHubGist serverUpdateBeforeConvergence = CreateGist(
            "updated",
            "Old server value",
            true,
            "updated.txt",
            "Text",
            updated.UpdatedAt.AddMinutes(-5));
        GitHubGistQueryService service = new(
            new CacheWritingPageQueryService(
                new MemoryGistCacheStore(),
                new Dictionary<int, GitHubGist[]> { [1] = [created, serverUpdateBeforeConvergence] }),
            new ImmediateRequestQueue(),
            new NullGitHubCacheStore(),
            new HttpClient(new RecordingHandler()),
            mutationJournal: journal);

        CachedResult<GitHubGist[]> visible = await service.GetPageAsync(
            "token",
            "42",
            1,
            100,
            QueryFetchPolicy.NetworkOnly);

        Assert.Contains(visible.Value!, static gist => gist.Id == "updated" && gist.Description == "Updated");
        GistMutationJournalEntry remaining = Assert.Single(await new GistMutationJournal(journalPath).ReadAsync("42"));
        Assert.Equal(GistMutationKind.Updated, remaining.Kind);
        Assert.Equal("updated", remaining.GistId);

        RecordingQueryService detailQuery = new() { DetailResult = updated };
        GitHubGistQueryService detailService = new(
            detailQuery,
            new ImmediateRequestQueue(),
            new NullGitHubCacheStore(),
            new HttpClient(new RecordingHandler()),
            mutationJournal: new GistMutationJournal(journalPath));
        CachedResult<GitHubGist> detail = await detailService.GetDetailAsync(
            "token",
            "42",
            "updated",
            QueryFetchPolicy.NetworkOnly);

        Assert.Equal("Updated", detail.Value!.Description);
        Assert.Empty(await new GistMutationJournal(journalPath).ReadAsync("42"));
    }

    [Fact]
    public async Task MutationJournal_RelaunchKeepsAccountsStrictlyPartitioned()
    {
        string journalPath = Path.Combine(_cacheRoot, "partitioned-journal.json");
        GistMutationJournal writer = new(journalPath);
        GitHubGist accountA = CreateGist("same-id", "Account A", true, "a.txt", "Text", DateTimeOffset.UtcNow);
        GitHubGist accountB = CreateGist("same-id", "Account B", false, "b.txt", "Text", DateTimeOffset.UtcNow);
        await writer.RecordUpsertAsync("42", accountA.Id, accountA, isCreate: false);
        await writer.RecordUpsertAsync("84", accountB.Id, accountB, isCreate: false);

        GistMutationJournal relaunched = new(journalPath);

        Assert.Equal("Account A", Assert.Single(await relaunched.ReadAsync("42")).Gist!.Description);
        Assert.Equal("Account B", Assert.Single(await relaunched.ReadAsync("84")).Gist!.Description);
    }

    [Fact]
    public void Projection_SearchFilterSortAndStableEqualityAreDeterministic()
    {
        GitHubGist publicGist = CreateGist("2", "Zeta", isPublic: true, "code.cs", "C#", DateTimeOffset.Parse("2026-07-15T00:00:00Z"));
        GitHubGist secretGist = CreateGist("1", "Alpha", isPublic: false, "notes.md", "Markdown", DateTimeOffset.Parse("2026-07-16T00:00:00Z"));

        Assert.True(GistLibraryProjection.Matches(publicGist, "c#", GistVisibilityFilter.Public));
        Assert.False(GistLibraryProjection.Matches(publicGist, string.Empty, GistVisibilityFilter.Secret));
        Assert.True(GistLibraryProjection.Matches(secretGist, "notes", GistVisibilityFilter.Secret));
        Assert.Equal(new[] { "1", "2" }, GistLibraryProjection.Sort([publicGist, secretGist], GistLibrarySort.Title).Select(static gist => gist.Id));
        Assert.True(GistLibraryProjection.HasSameListProjection(publicGist, CreateGist("2", "Zeta", true, "code.cs", "Rust", publicGist.UpdatedAt)));
        Assert.False(GistLibraryProjection.HasSameListProjection(publicGist, CreateGist("2", "Changed", true, "code.cs", "C#", publicGist.UpdatedAt)));
    }

    private GitHubGistQueryService CreateService(
        RecordingQueryService queryService,
        ImmediateRequestQueue queue,
        RecordingHandler handler) =>
        new(
            queryService,
            queue,
            new NullGitHubCacheStore(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            mutationJournal: new GistMutationJournal(Path.Combine(_cacheRoot, "mutation-journal.json")));

    private GitHubGistQueryService CreateServiceWithJournal(
        RecordingQueryService queryService,
        RecordingHandler handler,
        IGistMutationJournal journal) =>
        new(
            queryService,
            new GitHubRequestQueue(foregroundReadConcurrency: 1, backgroundReadConcurrency: 1, mutationConcurrency: 2),
            new NullGitHubCacheStore(),
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            mutationJournal: journal);

    private static GitHubGistCreateRequest CreateMutationRequest(string description) =>
        new()
        {
            Description = description,
            Public = false,
            Files = new Dictionary<string, GitHubGistFileWriteRequest>
            {
                ["sample.cs"] = new() { Content = description }
            }
        };

    private static void AssertMutationRecoveryInvalidated(
        RecordingQueryService queryService,
        string partition,
        string gistId)
    {
        IReadOnlyCollection<string> tags = Assert.Single(queryService.InvalidatedTagSets);
        Assert.Contains(GistCacheTagPolicy.List(partition), tags);
        Assert.Contains(GistCacheTagPolicy.ListIndex(partition), tags);
        Assert.Contains(GistCacheTagPolicy.Detail(partition, gistId), tags);
    }

    private GitHubGistQueryService CreateRawService(
        IGitHubCacheStore cacheStore,
        HttpMessageHandler handler) =>
        new(
            new RecordingQueryService(),
            new ImmediateRequestQueue(),
            cacheStore,
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            hostAddressResolver: ResolvePublicHostAsync);

    private SqliteGitHubCacheStore CreatePersistentCacheStore() =>
        new(
            Path.Combine(_cacheRoot, "jithub-cache.db"),
            Path.Combine(_cacheRoot, "payloads"),
            GitHubCachePolicy.Default);

    private static Task<IPAddress[]> ResolvePublicHostAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheRoot))
            {
                Directory.Delete(_cacheRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static GitHubGist CreateGist(
        string id,
        string description,
        bool isPublic,
        string filename,
        string language,
        DateTimeOffset updatedAt) =>
        new()
        {
            Id = id,
            Description = description,
            Public = isPublic,
            CreatedAt = updatedAt.AddDays(-1),
            UpdatedAt = updatedAt,
            Files = new Dictionary<string, GitHubGistFile>
            {
                [filename] = new() { Filename = filename, Language = language }
            }
        };

    private class NullGitHubCacheStore : IGitHubCacheStore
    {
        public virtual Task<CachedResult<T>?> TryGetAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
            where T : class =>
            Task.FromResult<CachedResult<T>?>(null);

        public virtual Task PutAsync<T>(
            GitHubQuery<T> query,
            GitHubRestResponse<T> response,
            CancellationToken cancellationToken = default)
            where T : class =>
            Task.CompletedTask;

        public virtual Task MarkRevalidatedAsync<T>(
            GitHubQuery<T> query,
            GitHubRestResponse<T> response,
            CancellationToken cancellationToken = default)
            where T : class =>
            Task.CompletedTask;

        public virtual Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task ClearAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public virtual Task<long> GetTotalPayloadBytesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public virtual Task<long> GetTotalMetadataBytesAsync(CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public virtual Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

        public virtual Task EnforceCapsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class MemoryGistCacheStore : NullGitHubCacheStore
    {
        private readonly Dictionary<string, object> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _tagsByKey = new(StringComparer.Ordinal);

        public override Task<CachedResult<T>?> TryGetAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
        {
            string key = CreateKey(query.UserId, query.RelativePath);
            return Task.FromResult(_entries.TryGetValue(key, out object? value)
                ? (CachedResult<T>?)value
                : null);
        }

        public override Task PutAsync<T>(
            GitHubQuery<T> query,
            GitHubRestResponse<T> response,
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset fetchedAt = response.FetchedAt;
            string key = CreateKey(query.UserId, query.RelativePath);
            _entries[key] = new CachedResult<T>(
                response.Payload,
                CacheState.Fresh,
                fetchedAt,
                fetchedAt.Add(query.Ttl),
                ETag: response.ETag,
                LastModified: response.LastModified);
            _tagsByKey[key] = query.Tags is { Count: > 0 }
                ? new HashSet<string>(query.Tags, StringComparer.Ordinal)
                : [];
            return Task.CompletedTask;
        }

        public override Task InvalidateTagsAsync(
            IReadOnlyCollection<string> tags,
            CancellationToken cancellationToken = default)
        {
            HashSet<string> requested = new(tags, StringComparer.Ordinal);
            string[] affectedKeys = _tagsByKey
                .Where(pair => pair.Value.Overlaps(requested))
                .Select(static pair => pair.Key)
                .ToArray();
            foreach (string key in affectedKeys)
            {
                _entries.Remove(key);
                _tagsByKey.Remove(key);
            }

            return Task.CompletedTask;
        }

        public void Remove(string userId, string relativePath)
        {
            string key = CreateKey(userId, relativePath);
            _entries.Remove(key);
            _tagsByKey.Remove(key);
        }

        public bool HasEntriesWithTag(string tag) =>
            _tagsByKey.Values.Any(tags => tags.Contains(tag));

        public void AddTaggedSentinel(string userId, string relativePath, string tag)
        {
            string key = CreateKey(userId, relativePath);
            _entries[key] = new object();
            _tagsByKey[key] = new HashSet<string>([tag], StringComparer.Ordinal);
        }

        private static string CreateKey(string userId, string relativePath) => $"{userId}|{relativePath}";
    }

    private sealed class CacheWritingPageQueryService(
        MemoryGistCacheStore cache,
        IReadOnlyDictionary<int, GitHubGist[]> pages) : IGitHubQueryService
    {
        public async Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            int page = int.Parse(query.RelativePath[(query.RelativePath.LastIndexOf("page=", StringComparison.Ordinal) + 5)..]);
            GitHubGist[] payload = pages.TryGetValue(page, out GitHubGist[]? value) ? value : [];
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GitHubRestResponse<T> response = new(
                HttpStatusCode.OK,
                (T)(object)payload,
                IsNotModified: false,
                ETag: null,
                LastModified: null,
                Link: null,
                RateLimitRemaining: null,
                RateLimitReset: null,
                RetryAfter: null,
                now);
            await cache.PutAsync(query, response, cancellationToken);
            return new CachedResult<T>((T)(object)payload, CacheState.Fresh, now, now.Add(query.Ttl));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default)
            where T : class => GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default) =>
            cache.InvalidateTagsAsync(tags, cancellationToken);
    }

    private sealed class RecordingRawCacheStore : NullGitHubCacheStore
    {
        public string UserId { get; private set; } = string.Empty;

        public string RelativePath { get; private set; } = string.Empty;

        public IReadOnlyList<string> Tags { get; private set; } = [];

        public override Task PutAsync<T>(
            GitHubQuery<T> query,
            GitHubRestResponse<T> response,
            CancellationToken cancellationToken = default)
        {
            UserId = query.UserId;
            RelativePath = query.RelativePath;
            Tags = query.Tags ?? [];
            return Task.CompletedTask;
        }
    }

    private sealed class StaleRawCacheStore(string content) : NullGitHubCacheStore
    {
        public override Task<CachedResult<T>?> TryGetAsync<T>(
            GitHubQuery<T> query,
            CancellationToken cancellationToken = default)
        {
            if (typeof(T) != typeof(string))
            {
                return base.TryGetAsync(query, cancellationToken);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            CachedResult<string> result = new(content, CacheState.Stale, now.AddDays(-31), now.AddMinutes(-1));
            return Task.FromResult<CachedResult<T>?>((CachedResult<T>)(object)result);
        }
    }

    private sealed class RecordingQueryService : IGitHubQueryService
    {
        public string? LastRelativePath { get; private set; }

        public IReadOnlyCollection<string> LastTags { get; private set; } = [];

        public QueryFetchPolicy LastFetchPolicy { get; private set; }

        public GitHubRequestPriority LastPriority { get; private set; }

        public List<IReadOnlyCollection<string>> InvalidatedTagSets { get; } = [];

        public GitHubGist? DetailResult { get; init; }

        public Task<CachedResult<T>> GetAsync<T>(
            GitHubQuery<T> query,
            QueryFetchPolicy fetchPolicy,
            CancellationToken cancellationToken = default)
            where T : class
        {
            LastRelativePath = query.RelativePath;
            LastTags = query.Tags ?? [];
            LastFetchPolicy = fetchPolicy;
            LastPriority = query.Priority;
            object value = typeof(T) == typeof(GitHubGist[])
                ? Array.Empty<GitHubGist>()
                : DetailResult ?? new GitHubGist();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new CachedResult<T>((T)value, CacheState.Fresh, now, now.AddMinutes(5)));
        }

        public Task<CachedResult<T>> RefreshAsync<T>(GitHubQuery<T> query, CancellationToken cancellationToken = default)
            where T : class => GetAsync(query, QueryFetchPolicy.NetworkOnly, cancellationToken);

        public Task InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
        {
            InvalidatedTagSets.Add(tags);
            return Task.CompletedTask;
        }
    }

    private sealed class ImmediateRequestQueue : IGitHubRequestQueue
    {
        public GitHubRequestPriority LastPriority { get; private set; }

        public Task<T> EnqueueAsync<T>(
            string dedupeKey,
            GitHubRequestPriority priority,
            Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken = default)
        {
            LastPriority = priority;
            return work(cancellationToken);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _requestCount;

        public HttpMethod? LastMethod { get; private set; }

        public string LastRelativePath { get; private set; } = string.Empty;

        public string LastBody { get; private set; } = string.Empty;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            LastMethod = request.Method;
            LastRelativePath = request.RequestUri?.PathAndQuery.TrimStart('/') ?? string.Empty;
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            string id = request.Method == HttpMethod.Patch
                ? LastRelativePath.Split('/').Last()
                : "created";
            string description = LastBody.Contains("\"description\":\"Updated\"", StringComparison.Ordinal)
                ? "Updated"
                : LastBody.Contains("\"description\":\"Created\"", StringComparison.Ordinal)
                    ? "Created"
                    : "Saved";
            string json = $"{{\"id\":\"{id}\",\"description\":\"{description}\",\"public\":false," +
                $"\"html_url\":\"https://gist.github.com/{id}\",\"url\":\"https://api.github.com/gists/{id}\"," +
                "\"files\":{\"sample.cs\":{\"filename\":\"sample.cs\",\"type\":\"text/plain\"," +
                "\"language\":\"C#\",\"size\":12,\"content\":\"return true;\"}}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ConcurrentMutationHandler(int expectedRequests) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public ConcurrentQueue<string> Bodies { get; } = new();

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Enqueue(body);
            int requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber == expectedRequests)
            {
                _allStarted.TrySetResult();
            }

            await _allStarted.Task.WaitAsync(cancellationToken);
            string description = string.IsNullOrEmpty(body)
                ? string.Empty
                : JsonDocument.Parse(body).RootElement.GetProperty("description").GetString() ?? string.Empty;
            string id = request.Method == HttpMethod.Patch
                ? request.RequestUri!.Segments.Last().Trim('/')
                : $"created-{requestNumber}";
            string json = JsonSerializer.Serialize(new
            {
                id,
                description,
                @public = false,
                html_url = $"https://gist.github.com/{id}",
                url = $"https://api.github.com/gists/{id}",
                files = new Dictionary<string, object>
                {
                    ["sample.cs"] = new
                    {
                        filename = "sample.cs",
                        type = "text/plain",
                        language = "C#",
                        size = 12,
                        content = "return true;"
                    }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FailingMutationJournal : IGistMutationJournal
    {
        public Task<IReadOnlyList<GistMutationJournalEntry>> ReadAsync(
            string accountPartition,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GistMutationJournalEntry>>([]);

        public Task RecordUpsertAsync(
            string accountPartition,
            string gistId,
            GitHubGist gist,
            bool isCreate,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("The journal is unavailable."));

        public Task RecordDeleteAsync(
            string accountPartition,
            string gistId,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("The journal is unavailable."));

        public Task RemoveAsync(
            string accountPartition,
            string gistId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ClearAccountAsync(
            string accountPartition,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FailingMutationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"message\":\"temporarily unavailable\"}", Encoding.UTF8, "application/json")
            });
    }

    private sealed class RawFileHandler(string content) : HttpMessageHandler
    {
        public string? LastHost { get; private set; }

        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastHost = request.RequestUri?.Host;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/plain")
            });
        }
    }

    private sealed class PathRawFileHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            await Task.Delay(50, cancellationToken);
            string filename = Path.GetFileNameWithoutExtension(request.RequestUri?.AbsolutePath) ?? string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(filename, Encoding.UTF8, "text/plain")
            };
        }
    }

    private sealed class RedirectRawFileHandler(string location) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            HttpResponseMessage response = new(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(location, UriKind.Absolute);
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingRawFileHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            throw new HttpRequestException("offline");
        }
    }

    private sealed class ConditionalRawFileHandler : HttpMessageHandler
    {
        private static readonly DateTimeOffset LastModified = DateTimeOffset.Parse("2026-07-20T12:00:00Z");
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public bool SawIfNoneMatch { get; private set; }

        public bool SawIfModifiedSince { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber == 1)
            {
                HttpResponseMessage response = new(HttpStatusCode.OK)
                {
                    Content = new StringContent("validator payload", Encoding.UTF8, "text/plain")
                };
                response.Headers.ETag = new EntityTagHeaderValue("\"raw-v1\"");
                response.Content.Headers.LastModified = LastModified;
                return Task.FromResult(response);
            }

            SawIfNoneMatch = request.Headers.IfNoneMatch.Any(static value => value.Tag == "\"raw-v1\"");
            SawIfModifiedSince = request.Headers.IfModifiedSince == LastModified;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }
    }

    private sealed class OversizedLengthHandler : HttpMessageHandler
    {
        public OversizedLengthContent Content { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = Content });
    }

    private sealed class OversizedLengthContent : HttpContent
    {
        public OversizedLengthContent()
        {
            Headers.ContentLength = GitHubGistQueryService.MaximumRawFileBytes + 1L;
            Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        }

        public bool StreamRequested { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            StreamRequested = true;
            throw new InvalidOperationException("The body should not be requested after the length check.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = GitHubGistQueryService.MaximumRawFileBytes + 1L;
            return true;
        }
    }

    private sealed class OversizedStreamHandler : HttpMessageHandler
    {
        public CountingReadStream Stream { get; } = new(GitHubGistQueryService.MaximumRawFileBytes + 1L);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(Stream)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("text/plain") }
                }
            });
    }

    private sealed class CountingReadStream(long length) : Stream
    {
        private long _position;

        public long BytesRead => Interlocked.Read(ref _position);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Min(count, length - _position);
            if (read <= 0)
            {
                return 0;
            }

            Array.Clear(buffer, offset, read);
            Interlocked.Add(ref _position, read);
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = (int)Math.Min(buffer.Length, length - _position);
            if (read <= 0)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[..read].Clear();
            Interlocked.Add(ref _position, read);
            return ValueTask.FromResult(read);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellableRawFileHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancellable handler unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }
}

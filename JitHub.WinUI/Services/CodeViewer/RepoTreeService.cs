using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Models.GitHub;

namespace JitHub.Services.CodeViewer;

public sealed class RepoTreeService : IRepoTreeService
{
    private const int MaximumCachedTreeCount = 8;
    private const int MaximumCachedTreeNodes = 75_000;
    private readonly IGitHubRepoCodeQueryService _queryService;
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly object _treeCacheGate = new();
    private readonly Dictionary<string, LinkedListNode<TreeMemoryEntry>> _treeCache = new(StringComparer.Ordinal);
    private readonly LinkedList<TreeMemoryEntry> _treeLru = new();
    private readonly ConcurrentDictionary<string, Lazy<SharedTreeLoad>> _treeLoads = new(StringComparer.Ordinal);
    private int _cachedTreeNodes;

    public RepoTreeService(
        IGitHubRepoCodeQueryService queryService,
        IAuthService authService,
        IAccountService accountService)
    {
        _queryService = queryService;
        _authService = authService;
        _accountService = accountService;
    }

    public async Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
        string owner,
        string name,
        string refOrSha,
        CancellationToken ct,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
    {
        (string token, string userId) = GetAuthenticationContext();
        string cacheKey = CreateTreeCacheKey(userId, owner, name, refOrSha);
        if (fetchPolicy == QueryFetchPolicy.StaleFirst &&
            TryGetCachedTree(cacheKey, out RepoCodeLoadResult<RepoTree> cached))
        {
            if (!IsStale(cached))
            {
                return cached;
            }

            _ = GetOrStartTreeLoad(
                cacheKey,
                token,
                userId,
                owner,
                name,
                refOrSha,
                QueryFetchPolicy.NetworkOnly);
            return cached with
            {
                CacheState = CacheState.Stale,
                IsRefreshInProgress = true
            };
        }

        SharedTreeLoad load = GetOrStartTreeLoad(
            cacheKey,
            token,
            userId,
            owner,
            name,
            refOrSha,
            fetchPolicy);
        return await load.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task PrefetchTreeAsync(
        string owner,
        string name,
        string refOrSha,
        CancellationToken ct)
    {
        _ = await LoadTreeAsync(owner, name, refOrSha, ct).ConfigureAwait(false);
    }

    public async Task ClearMemoryCacheAsync(
        string? accountPartition = null,
        CancellationToken cancellationToken = default)
    {
        string? partitionPrefix = string.IsNullOrWhiteSpace(accountPartition)
            ? null
            : $"{GitHubAccountPartition.Require(accountPartition).ToLowerInvariant()}:";
        lock (_treeCacheGate)
        {
            LinkedListNode<TreeMemoryEntry>? node = _treeLru.First;
            while (node is not null)
            {
                LinkedListNode<TreeMemoryEntry>? next = node.Next;
                if (partitionPrefix is null || node.Value.Key.StartsWith(partitionPrefix, StringComparison.Ordinal))
                {
                    _treeLru.Remove(node);
                    _treeCache.Remove(node.Value.Key);
                    _cachedTreeNodes -= node.Value.NodeCount;
                }

                node = next;
            }
        }

        List<Task> pending = [];
        foreach ((string requestKey, Lazy<SharedTreeLoad> lazy) in _treeLoads.ToArray())
        {
            if (partitionPrefix is not null && !requestKey.StartsWith(partitionPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            SharedTreeLoad load = lazy.Value;
            load.Cancel();
            pending.Add(ObserveCompletionAsync(load.Task));
        }

        if (pending.Count > 0)
        {
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private SharedTreeLoad GetOrStartTreeLoad(
        string cacheKey,
        string token,
        string userId,
        string owner,
        string name,
        string refOrSha,
        QueryFetchPolicy fetchPolicy)
    {
        string requestKey = $"{cacheKey}|{fetchPolicy}";
        Lazy<SharedTreeLoad> lazy = _treeLoads.GetOrAdd(
            requestKey,
            _ => new Lazy<SharedTreeLoad>(
                () => StartSharedTreeLoad(
                    requestKey,
                    cacheKey,
                    token,
                    userId,
                    owner,
                    name,
                    refOrSha,
                    fetchPolicy),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private SharedTreeLoad StartSharedTreeLoad(
        string requestKey,
        string cacheKey,
        string token,
        string userId,
        string owner,
        string name,
        string refOrSha,
        QueryFetchPolicy fetchPolicy)
    {
        CancellationTokenSource cancellation = new();
        Task<RepoCodeLoadResult<RepoTree>> task = LoadProjectAndPromoteTreeAsync(
            cacheKey,
            token,
            userId,
            owner,
            name,
            refOrSha,
            fetchPolicy,
            cancellation.Token);
        SharedTreeLoad load = new(cancellation, task);
        _ = RemoveTreeLoadWhenCompleteAsync(requestKey, load);
        return load;
    }

    private async Task<RepoCodeLoadResult<RepoTree>> LoadProjectAndPromoteTreeAsync(
        string cacheKey,
        string token,
        string userId,
        string owner,
        string name,
        string refOrSha,
        QueryFetchPolicy fetchPolicy,
        CancellationToken cancellationToken)
    {
        RepoCodeLoadResult<RepoTree> result = await LoadAndProjectTreeAsync(
            token,
            userId,
            owner,
            name,
            refOrSha,
            fetchPolicy,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        PromoteTree(cacheKey, result);
        return result;
    }

    private async Task RemoveTreeLoadWhenCompleteAsync(string requestKey, SharedTreeLoad load)
    {
        try
        {
            _ = await load.Task.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            if (_treeLoads.TryGetValue(requestKey, out Lazy<SharedTreeLoad>? current) &&
                current.IsValueCreated &&
                ReferenceEquals(current.Value, load))
            {
                _treeLoads.TryRemove(new KeyValuePair<string, Lazy<SharedTreeLoad>>(requestKey, current));
            }

            load.Cancellation.Dispose();
        }
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private Task<RepoCodeLoadResult<RepoTree>> LoadAndProjectTreeAsync(
        string token,
        string userId,
        string owner,
        string name,
        string refOrSha,
        QueryFetchPolicy fetchPolicy,
        CancellationToken ct) =>
        Task.Run(
            async () =>
            {
                CachedResult<GitHubTree> result = await _queryService.GetTreeAsync(
                    token,
                    userId,
                    owner,
                    name,
                    refOrSha,
                    fetchPolicy,
                    ct).ConfigureAwait(false);
                GitHubTree tree = result.Value ?? throw new InvalidOperationException("GitHub returned no repository tree.");
                return MapResult(result, BuildRepoTree(tree));
            },
            ct);

    private bool TryGetCachedTree(string key, out RepoCodeLoadResult<RepoTree> result)
    {
        lock (_treeCacheGate)
        {
            if (!_treeCache.TryGetValue(key, out LinkedListNode<TreeMemoryEntry>? node))
            {
                result = null!;
                return false;
            }

            _treeLru.Remove(node);
            _treeLru.AddFirst(node);
            result = node.Value.Result;
            return true;
        }
    }

    private static bool IsStale(RepoCodeLoadResult<RepoTree> result) =>
        result.CacheState == CacheState.Stale ||
        result.StaleAfter is DateTimeOffset staleAfter && staleAfter <= DateTimeOffset.UtcNow;

    private void PromoteTree(string key, RepoCodeLoadResult<RepoTree> result)
    {
        int nodeCount = CountNodes(result.Value.Root);
        if (nodeCount > MaximumCachedTreeNodes)
        {
            return;
        }

        lock (_treeCacheGate)
        {
            if (_treeCache.Remove(key, out LinkedListNode<TreeMemoryEntry>? existing))
            {
                _treeLru.Remove(existing);
                _cachedTreeNodes -= existing.Value.NodeCount;
            }

            LinkedListNode<TreeMemoryEntry> node = new(new TreeMemoryEntry(key, result, nodeCount));
            _treeLru.AddFirst(node);
            _treeCache[key] = node;
            _cachedTreeNodes += nodeCount;
            while (_treeLru.Count > MaximumCachedTreeCount || _cachedTreeNodes > MaximumCachedTreeNodes)
            {
                LinkedListNode<TreeMemoryEntry>? tail = _treeLru.Last;
                if (tail is null)
                {
                    break;
                }

                _treeLru.RemoveLast();
                _treeCache.Remove(tail.Value.Key);
                _cachedTreeNodes -= tail.Value.NodeCount;
            }
        }
    }

    private static string CreateTreeCacheKey(string userId, string owner, string name, string refOrSha) =>
        $"{userId.Trim().ToLowerInvariant()}:{owner.Trim().ToLowerInvariant()}/{name.Trim().ToLowerInvariant()}@{refOrSha.Trim()}";

    private sealed class SharedTreeLoad
    {
        public SharedTreeLoad(
            CancellationTokenSource cancellation,
            Task<RepoCodeLoadResult<RepoTree>> task)
        {
            Cancellation = cancellation;
            Task = task;
        }

        public CancellationTokenSource Cancellation { get; }

        public Task<RepoCodeLoadResult<RepoTree>> Task { get; }

        public void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static int CountNodes(RepoTreeNode node)
    {
        int count = 0;
        Stack<RepoTreeNode> pending = new();
        pending.Push(node);
        while (pending.TryPop(out RepoTreeNode? current))
        {
            count++;
            foreach (RepoTreeNode child in current.Children)
            {
                pending.Push(child);
            }
        }

        return count;
    }

    public async Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
        string owner,
        string name,
        string path,
        string refOrSha,
        CancellationToken ct,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
    {
        (string token, string userId) = GetAuthenticationContext();
        CachedResult<GitHubRepositoryContent[]> result = await _queryService.GetDirectoryAsync(
            token,
            userId,
            owner,
            name,
            path,
            refOrSha,
            fetchPolicy,
            ct).ConfigureAwait(false);
        GitHubRepositoryContent[] contents = result.Value ?? [];
        IReadOnlyList<RepoTreeNode> nodes = contents
            .Select(static content => new RepoTreeNode
            {
                Name = content.Name ?? string.Empty,
                Path = content.Path ?? string.Empty,
                Sha = content.Sha,
                Size = content.Size,
                IsDirectory = string.Equals(content.Type, "dir", StringComparison.OrdinalIgnoreCase)
            })
            .OrderByDescending(static node => node.IsDirectory)
            .ThenBy(static node => node.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return MapResult(result, nodes);
    }

    public async Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
        string owner,
        string name,
        string sha,
        CancellationToken ct,
        QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
    {
        (string token, string userId) = GetAuthenticationContext();
        CachedResult<GitHubBlob> result = await _queryService.GetBlobAsync(
            token,
            userId,
            owner,
            name,
            sha,
            fetchPolicy,
            ct).ConfigureAwait(false);
        GitHubBlob blob = result.Value ?? throw new InvalidOperationException("GitHub returned no repository blob.");
        byte[] bytes = await Task.Run(() => DecodeBlob(blob.Content, blob.Encoding), ct).ConfigureAwait(false);
        bool isBinary = IsBinaryContent(bytes);
        RepoFileBlob mapped = new()
        {
            Sha = blob.Sha,
            Encoding = blob.Encoding,
            Bytes = bytes,
            Text = isBinary ? null : DecodeText(bytes),
            IsBinary = isBinary
        };
        return MapResult(result, mapped);
    }

    private (string Token, string UserId) GetAuthenticationContext()
    {
        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        string token = _authService.GetToken(userId) ?? GitHubAuthenticationConstants.PublicAccessToken;
        string partition = userId > 0 ? userId.ToString(CultureInfo.InvariantCulture) : "current";
        return (token, partition);
    }

    internal static RepoTree BuildRepoTree(GitHubTree gitTree)
    {
        RepoTreeNode root = new()
        {
            Name = string.Empty,
            Path = string.Empty,
            IsDirectory = true
        };
        Dictionary<string, RepoTreeNode> nodeMap = new(StringComparer.Ordinal)
        {
            [string.Empty] = root
        };

        foreach (GitHubTreeEntry entry in gitTree.Tree ?? [])
        {
            if (!string.IsNullOrEmpty(entry.Path))
            {
                EnsurePath(entry.Path, entry, nodeMap);
            }
        }

        SortChildren(root);
        return new RepoTree
        {
            Sha = gitTree.Sha,
            Truncated = gitTree.Truncated,
            Root = root
        };
    }

    private static RepoTreeNode EnsurePath(
        string path,
        GitHubTreeEntry? entry,
        Dictionary<string, RepoTreeNode> nodeMap)
    {
        if (nodeMap.TryGetValue(path, out RepoTreeNode? existing))
        {
            return existing;
        }

        int slashIndex = path.LastIndexOf('/');
        string parentPath = slashIndex < 0 ? string.Empty : path[..slashIndex];
        string name = slashIndex < 0 ? path : path[(slashIndex + 1)..];
        RepoTreeNode parent = EnsurePath(parentPath, entry: null, nodeMap);
        RepoTreeNode node = new()
        {
            Name = name,
            Path = path,
            Sha = entry?.Sha,
            Size = entry?.Size,
            IsDirectory = entry is null || string.Equals(entry.Type, "tree", StringComparison.Ordinal),
            ParentPath = parentPath
        };
        nodeMap[path] = node;
        parent.Children.Add(node);
        return node;
    }

    private static void SortChildren(RepoTreeNode node)
    {
        List<RepoTreeNode> sorted = node.Children
            .OrderByDescending(static child => child.IsDirectory)
            .ThenBy(static child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        node.Children.Clear();
        foreach (RepoTreeNode child in sorted)
        {
            node.Children.Add(child);
            SortChildren(child);
        }
    }

    private static RepoCodeLoadResult<TMapped> MapResult<TSource, TMapped>(
        CachedResult<TSource> source,
        TMapped value)
        where TSource : class
        where TMapped : class =>
        new(
            value,
            source.CacheState,
            source.IsRefreshInProgress,
            source.RefreshError is null
                ? null
                : JitHub.WinUI.Helpers.UserFacingError.For(
                    source.RefreshError,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                    "repository-tree"),
            source.FetchedAt,
            source.StaleAfter);

    private static byte[] DecodeBlob(string? content, string? encoding)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            string normalized = content.Replace("\r", string.Empty).Replace("\n", string.Empty);
            return Convert.FromBase64String(normalized);
        }

        return Encoding.UTF8.GetBytes(content);
    }

    private static bool IsBinaryContent(byte[] bytes)
    {
        int scanLength = Math.Min(bytes.Length, 8192);
        for (int index = 0; index < scanLength; index++)
        {
            if (bytes[index] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string? DecodeText(byte[] bytes)
    {
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private sealed record TreeMemoryEntry(
        string Key,
        RepoCodeLoadResult<RepoTree> Result,
        int NodeCount);
}

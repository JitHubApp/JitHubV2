using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using Microsoft.UI.Dispatching;

namespace JitHub.WinUI.ViewModels.CodeViewer;

public sealed partial class RepoCodePageViewModel : ObservableObject
{
    private readonly IGitHubService _github;
    private readonly IRepoTreeService _treeService;
    private readonly IRepoFileCacheService _cache;
    private readonly IFilePreviewResolver _previewResolver;
    private readonly ILanguageIdResolver _languageResolver;
    private readonly DispatcherQueue _dispatcherQueue;

    // Navigation state
    private string _owner = string.Empty;
    private string _repositoryName = string.Empty;
    private string _ref = string.Empty;

    // Back/forward stacks
    private readonly List<RepoTreeNode> _backStack = [];
    private readonly List<RepoTreeNode> _forwardStack = [];

    // Per-selection cancellation
    private CancellationTokenSource? _selectionCts;

    public string Owner => _owner;
    public string RepositoryName => _repositoryName;
    public string Ref => _ref;

    public RepoFileTreeViewModel Tree { get; }
    public RepoFilePreviewViewModel Preview { get; }
    public RepoCodeBreadcrumbViewModel Breadcrumb { get; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? LoadError { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    public partial bool CanGoForward { get; set; }

    public RepoCodePageViewModel(
        IGitHubService github,
        IRepoTreeService treeService,
        IRepoFileCacheService cache,
        IFilePreviewResolver previewResolver,
        ILanguageIdResolver languageResolver)
    {
        _github = github;
        _treeService = treeService;
        _cache = cache;
        _previewResolver = previewResolver;
        _languageResolver = languageResolver;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        Tree = new RepoFileTreeViewModel(treeService, languageResolver);
        Preview = new RepoFilePreviewViewModel();
        Breadcrumb = new RepoCodeBreadcrumbViewModel();

        // Wire tree selection to page-level handler.
        Tree.OnSelectNode = (node, ct) => SelectFileAsync(node.IsDirectory ? null! : ToModelNode(node), ct);
        Breadcrumb.OnNavigate = _ => { /* tree expansion handled by view */ };

        // InitializeCommand (no-arg): re-triggers initialization with currently stored owner/name/ref.
        InitializeCommand = new AsyncRelayCommand(
            () => InitializeAsync(_owner, _repositoryName, _ref, CancellationToken.None));
        SelectFileCommand = new AsyncRelayCommand<RepoTreeNode>(
            node => SelectFileAsync(node!, CancellationToken.None));
        GoBackCommand = new RelayCommand(GoBack, () => CanGoBack);
        GoForwardCommand = new RelayCommand(GoForward, () => CanGoForward);
    }

    // Exposed commands (named exactly as in the contract).
    public AsyncRelayCommand InitializeCommand { get; }
    public AsyncRelayCommand<RepoTreeNode> SelectFileCommand { get; }
    public RelayCommand GoBackCommand { get; }
    public RelayCommand GoForwardCommand { get; }

    public async Task InitializeAsync(string owner, string name, string @ref, CancellationToken ct)
    {
        _owner = owner;
        _repositoryName = name;
        _ref = @ref;

        _backStack.Clear();
        _forwardStack.Clear();
        UpdateNavigation();
        Preview.Reset();

        RunOnUi(() =>
        {
            IsLoading = true;
            LoadError = null;
            Tree.IsLoading = true;
        });

        try
        {
            RepoTree tree = await _treeService.LoadTreeAsync(owner, name, @ref, ct);
            RunOnUi(() =>
            {
                Tree.Load(tree, owner, name, @ref);
                Tree.IsTruncated = tree.Truncated;
                Tree.IsLoading = false;
                IsLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            RunOnUi(() =>
            {
                Tree.IsLoading = false;
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                Tree.IsLoading = false;
                IsLoading = false;
                LoadError = ex.Message;
            });
        }
    }

    public async Task SelectFileAsync(RepoTreeNode? node, CancellationToken ct)
    {
        if (node is null || node.IsDirectory) return;

        // Cancel any in-flight selection.
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await SelectFileAsyncInternal(node, _selectionCts.Token, push: true);
    }

    private void GoBack()
    {
        if (_backStack.Count <= 1) return;

        // Pop current (last) from back, push to forward.
        RepoTreeNode current = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        _forwardStack.Add(current);
        UpdateNavigation();

        if (_backStack.Count > 0)
        {
            RepoTreeNode target = _backStack[^1];
            _ = NavigateWithoutStackPushAsync(target);
        }
    }

    private void GoForward()
    {
        if (_forwardStack.Count == 0) return;

        RepoTreeNode target = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);
        _backStack.Add(target);
        UpdateNavigation();
        _ = NavigateWithoutStackPushAsync(target);
    }

    private async Task NavigateWithoutStackPushAsync(RepoTreeNode node)
    {
        // Navigate without pushing onto back stack again.
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = new CancellationTokenSource();

        // Temporarily detach the push logic by using the internal select logic.
        // We reuse SelectFileAsync but intercept the push at the end.
        await SelectFileAsyncInternal(node, _selectionCts.Token, push: false);
    }

    private void PushBackStack(RepoTreeNode node)
    {
        // Avoid duplicate top entry.
        if (_backStack.Count > 0 && _backStack[^1].Path == node.Path) return;

        _backStack.Add(node);
        _forwardStack.Clear();
        UpdateNavigation();
    }

    private void UpdateNavigation()
    {
        CanGoBack = _backStack.Count > 1;
        CanGoForward = _forwardStack.Count > 0;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Core implementation shared by SelectFileAsync and back/forward navigation.
    /// </summary>
    private async Task SelectFileAsyncInternal(RepoTreeNode node, CancellationToken token, bool push)
    {
        if (node.IsDirectory) return;

        RunOnUi(() =>
        {
            Preview.IsLoading = true;
            Preview.ErrorMessage = null;
            Preview.CurrentFile = node;
        });

        try
        {
            byte[] bytes;
            string? text;
            string? encoding;
            long byteSize;

            var cacheKey = new RepoFileCacheKey(_owner, _repositoryName, node.Sha ?? string.Empty);
            if (_cache.TryGet(cacheKey, out RepoFileCacheEntry cached))
            {
                bytes = cached.Bytes;
                text = cached.Text;
                encoding = cached.Encoding;
                byteSize = cached.ByteLength;
            }
            else
            {
                RepoFileCacheEntry? asyncCached = await _cache.GetAsync(cacheKey, token);
                if (asyncCached is not null)
                {
                    bytes = asyncCached.Bytes;
                    text = asyncCached.Text;
                    encoding = asyncCached.Encoding;
                    byteSize = asyncCached.ByteLength;
                }
                else
                {
                    RepoFileBlob blob = await _treeService.LoadBlobAsync(_owner, _repositoryName, node.Sha ?? string.Empty, token);
                    bytes = blob.Bytes ?? [];
                    text = blob.Text;
                    encoding = blob.Encoding;
                    byteSize = bytes.LongLength;

                    if (text is null && !blob.IsBinary && bytes.Length > 0)
                    {
                        text = await Task.Run(() => Encoding.UTF8.GetString(bytes), token);
                    }

                    var entry = new RepoFileCacheEntry
                    {
                        Sha = node.Sha ?? string.Empty,
                        ByteLength = byteSize,
                        IsBinary = blob.IsBinary,
                        Bytes = bytes,
                        Text = text,
                        Encoding = encoding,
                        CachedAt = DateTimeOffset.UtcNow,
                    };
                    await _cache.PutAsync(cacheKey, entry, token);
                }
            }

            int sniffLen = (int)Math.Min(bytes.LongLength, 8192L);
            ReadOnlyMemory<byte> headSample = bytes.AsMemory(0, sniffLen);
            FilePreviewDescriptor descriptor = _previewResolver.Resolve(node.Path, byteSize, headSample);

            string gitHubUrl = $"https://github.com/{_owner}/{_repositoryName}/blob/{_ref}/{node.Path}";
            string rawUrl = $"https://raw.githubusercontent.com/{_owner}/{_repositoryName}/{_ref}/{node.Path}";

            RunOnUi(() =>
            {
                Preview.Kind = descriptor.Kind;
                Preview.LanguageId = descriptor.LanguageId;
                Preview.ByteSize = byteSize;
                Preview.Encoding = encoding;
                Preview.ImageMimeType = descriptor.ImageMimeType;

                if (descriptor.Kind is RepoFilePreviewKind.TooLarge or RepoFilePreviewKind.Unsupported)
                {
                    Preview.GitHubBlobUrl = gitHubUrl;
                    Preview.Text = null;
                    Preview.Bytes = null;
                }
                else if (descriptor.IsLikelyBinary)
                {
                    Preview.Bytes = bytes;
                    Preview.Text = null;
                }
                else
                {
                    Preview.Text = text;
                    Preview.Bytes = bytes;
                }

                Preview.IsLoading = false;

                Breadcrumb.BuildFromPath(_repositoryName, node.Path);
                Breadcrumb.CurrentRawUrl = rawUrl;
                Breadcrumb.CurrentGitHubUrl = gitHubUrl;
            });

            if (push) PushBackStack(node);
        }
        catch (OperationCanceledException)
        {
            RunOnUi(() => Preview.IsLoading = false);
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                Preview.IsLoading = false;
                Preview.ErrorMessage = ex.Message;
            });
        }
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => action());
        }
    }

    private static RepoTreeNode ToModelNode(RepoTreeNodeViewModel vm) => new()
    {
        Name = vm.Name,
        Path = vm.Path,
        Sha = vm.Sha,
        Size = vm.Size,
        IsDirectory = vm.IsDirectory,
    };
}

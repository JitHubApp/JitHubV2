using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Common;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GistViewItem : ObservableObject
{
    public GitHubGist Gist { get; private set; } = new();

    public string StableKey => Gist.Id;

    public string AutomationId => $"GistRow_{(string.IsNullOrWhiteSpace(StableKey) ? "unknown" : StableKey)}";

    public string Title => GistLibraryProjection.GetTitle(Gist);

    public string FileSummary => GistLibraryProjection.GetFileSummary(Gist);

    public string VisibilityText => Gist.Public ? "Public" : "Secret";

    public string UpdatedText => FormatRelativeTime(Gist.UpdatedAt);

    public string AutomationName => $"{Title}, {VisibilityText}, {FileSummary}, {UpdatedText}";

    public bool ApplyGist(GitHubGist gist)
    {
        bool changed = !GistLibraryProjection.HasSameListProjection(Gist, gist);
        Gist = gist;
        if (changed)
        {
            OnPropertyChanged(nameof(Gist));
            OnPropertyChanged(nameof(StableKey));
            OnPropertyChanged(nameof(AutomationId));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(FileSummary));
            OnPropertyChanged(nameof(VisibilityText));
            OnPropertyChanged(nameof(UpdatedText));
            OnPropertyChanged(nameof(AutomationName));
        }

        return changed;
    }

    public static GistViewItem Create(GitHubGist gist)
    {
        GistViewItem item = new();
        item.ApplyGist(gist);
        return item;
    }

    private static string FormatRelativeTime(DateTimeOffset value)
    {
        TimeSpan age = DateTimeOffset.Now - value.ToLocalTime();
        if (age.TotalMinutes < 1)
        {
            return "Updated just now";
        }

        if (age.TotalHours < 1)
        {
            return $"Updated {(int)Math.Max(1, age.TotalMinutes)}m ago";
        }

        if (age.TotalDays < 1)
        {
            return $"Updated {(int)Math.Max(1, age.TotalHours)}h ago";
        }

        return age.TotalDays < 30
            ? $"Updated {(int)Math.Max(1, age.TotalDays)}d ago"
            : value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GistFileViewItem : ObservableObject
{
    private GistFileRenderModel _renderModel = GistFileRenderPolicy.Create(string.Empty);

    public GitHubGistFile File { get; private set; } = new();

    public string StableKey => File.Filename;

    public string Filename => string.IsNullOrWhiteSpace(File.Filename) ? "file" : File.Filename;

    public string LanguageText => string.IsNullOrWhiteSpace(File.Language) ? "Text" : File.Language;

    public string SizeText => File.Size < 1024
        ? $"{File.Size} B"
        : $"{File.Size / 1024d:0.#} KB";

    public string ContentText => _renderModel.PreviewText;

    public bool IsPreviewCapped => _renderModel.IsCapped;

    public string PreviewStatus => _renderModel.StatusText;

    public bool IsTruncated => File.Truncated;

    public string TruncationText => GistFileContentPolicy.GetTruncationMessage(File);

    public string AutomationName => $"{Filename}, {LanguageText}, {SizeText}";

    public bool ApplyFile(GitHubGistFile file)
    {
        bool changed = !string.Equals(File.Filename, file.Filename, StringComparison.Ordinal)
            || !string.Equals(File.Content, file.Content, StringComparison.Ordinal)
            || !string.Equals(File.Language, file.Language, StringComparison.Ordinal)
            || File.Size != file.Size
            || File.Truncated != file.Truncated;
        File = file;
        _renderModel = GistFileRenderPolicy.Create(GistFileContentPolicy.GetPreviewText(file));
        if (changed)
        {
            OnPropertyChanged(nameof(File));
            OnPropertyChanged(nameof(StableKey));
            OnPropertyChanged(nameof(Filename));
            OnPropertyChanged(nameof(LanguageText));
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(ContentText));
            OnPropertyChanged(nameof(IsPreviewCapped));
            OnPropertyChanged(nameof(PreviewStatus));
            OnPropertyChanged(nameof(IsTruncated));
            OnPropertyChanged(nameof(TruncationText));
            OnPropertyChanged(nameof(AutomationName));
        }

        return changed;
    }

    public void ApplyFullContent(string content)
    {
        File.Content = content;
        File.Truncated = false;
        _renderModel = GistFileRenderPolicy.Create(content);
        OnPropertyChanged(nameof(File));
        OnPropertyChanged(nameof(ContentText));
        OnPropertyChanged(nameof(IsPreviewCapped));
        OnPropertyChanged(nameof(PreviewStatus));
        OnPropertyChanged(nameof(IsTruncated));
        OnPropertyChanged(nameof(TruncationText));
        OnPropertyChanged(nameof(AutomationName));
    }

    public static GistFileViewItem Create(GitHubGistFile file)
    {
        GistFileViewItem item = new();
        item.ApplyFile(file);
        return item;
    }
}

public sealed partial class GistEditorFileDraft : ObservableObject
{
    public string OriginalFilename { get; init; } = string.Empty;

    public bool IsContentAvailable { get; init; } = true;

    [ObservableProperty]
    public partial string Filename { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Content { get; set; } = string.Empty;

    public string AutomationName => string.IsNullOrWhiteSpace(Filename)
        ? "Untitled Gist file"
        : $"Gist file {Filename.Trim()}";

    partial void OnFilenameChanged(string value)
    {
        OnPropertyChanged(nameof(AutomationName));
    }
}

public sealed partial class GistEditorSession : ObservableObject
{
    private int _newFileSequence = 1;

    private GistEditorSession(bool isNew)
    {
        IsNew = isNew;
        CanChangeVisibility = isNew;
        Title = isNew ? "New gist" : "Edit gist";
        AddFileCommand = new RelayCommand(AddFile);
        RemoveFileCommand = new RelayCommand(RemoveSelectedFile, () => CanRemoveFile);
    }

    public bool IsNew { get; }

    public bool CanChangeVisibility { get; }

    public string Title { get; }

    public ObservableCollection<GistEditorFileDraft> Files { get; } = [];

    public IRelayCommand AddFileCommand { get; }

    public IRelayCommand RemoveFileCommand { get; }

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPublic { get; set; }

    [ObservableProperty]
    public partial GistEditorFileDraft? SelectedFile { get; set; }

    public bool CanRemoveFile => Files.Count > 1 && SelectedFile is not null;

    public bool CanSave => Files.Count > 0
        && Files.All(static file => !string.IsNullOrWhiteSpace(file.Filename))
        && Files.Select(static file => file.Filename.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == Files.Count;

    public void CommitDisplayedFile(
        GistEditorFileDraft? displayedFile,
        string filename,
        string content,
        bool commitContent)
    {
        if (displayedFile is null || !Files.Contains(displayedFile))
        {
            return;
        }

        displayedFile.Filename = filename;
        if (commitContent && displayedFile.IsContentAvailable)
        {
            displayedFile.Content = content;
        }
    }

    public static GistEditorSession CreateNew()
    {
        GistEditorSession session = new(isNew: true);
        session.AddFile();
        return session;
    }

    public static GistEditorSession CreateForEdit(GitHubGist gist)
    {
        GistEditorSession session = new(isNew: false)
        {
            Description = gist.Description ?? string.Empty,
            IsPublic = gist.Public
        };
        foreach (GitHubGistFile file in gist.Files.Values.OrderBy(static file => file.Filename, StringComparer.OrdinalIgnoreCase))
        {
            session.AddDraft(new GistEditorFileDraft
            {
                OriginalFilename = file.Filename,
                Filename = file.Filename,
                Content = file.Content ?? string.Empty,
                IsContentAvailable = !file.Truncated && file.Content is not null
            });
        }

        if (session.Files.Count == 0)
        {
            session.AddFile();
        }

        session.SelectedFile = session.Files[0];
        return session;
    }

    public void AddFile()
    {
        string candidate;
        do
        {
            candidate = $"file{_newFileSequence++}.txt";
        }
        while (Files.Any(file => string.Equals(file.Filename, candidate, StringComparison.OrdinalIgnoreCase)));

        GistEditorFileDraft draft = new() { Filename = candidate };
        AddDraft(draft);
        SelectedFile = draft;
        NotifyValidationChanged();
    }

    public void RemoveSelectedFile()
    {
        if (!CanRemoveFile || SelectedFile is null)
        {
            return;
        }

        int index = Files.IndexOf(SelectedFile);
        GistEditorFileDraft removed = SelectedFile;
        removed.PropertyChanged -= Draft_PropertyChanged;
        Files.Remove(removed);
        SelectedFile = Files[Math.Clamp(index, 0, Files.Count - 1)];
        NotifyValidationChanged();
    }

    public GitHubGistCreateRequest BuildCreateRequest() => new()
    {
        Description = Description.Trim(),
        Public = IsPublic,
        Files = Files.ToDictionary(
            static file => file.Filename.Trim(),
            static file => new GitHubGistFileWriteRequest { Content = file.Content },
            StringComparer.OrdinalIgnoreCase)
    };

    public GitHubGistUpdateRequest BuildUpdateRequest(GitHubGist original)
    {
        Dictionary<string, GitHubGistFileUpdateRequest?> files = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> retainedOriginalNames = Files
            .Where(static file => !string.IsNullOrWhiteSpace(file.OriginalFilename))
            .Select(static file => file.OriginalFilename)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string originalFilename in original.Files.Keys)
        {
            if (!retainedOriginalNames.Contains(originalFilename))
            {
                files[originalFilename] = null;
            }
        }

        foreach (GistEditorFileDraft file in Files)
        {
            string key = string.IsNullOrWhiteSpace(file.OriginalFilename)
                ? file.Filename.Trim()
                : file.OriginalFilename;
            bool isNewFile = string.IsNullOrWhiteSpace(file.OriginalFilename);
            bool contentChanged = isNewFile;
            if (!isNewFile && file.IsContentAvailable)
            {
                contentChanged = !original.Files.TryGetValue(file.OriginalFilename, out GitHubGistFile? originalFile) ||
                    !originalFile.Truncated && !string.Equals(originalFile.Content, file.Content, StringComparison.Ordinal);
            }
            files[key] = new GitHubGistFileUpdateRequest
            {
                Filename = string.Equals(key, file.Filename.Trim(), StringComparison.Ordinal) ? null : file.Filename.Trim(),
                Content = contentChanged ? file.Content : null
            };
        }

        return new GitHubGistUpdateRequest { Description = Description.Trim(), Files = files };
    }

    partial void OnSelectedFileChanged(GistEditorFileDraft? value)
    {
        OnPropertyChanged(nameof(CanRemoveFile));
        RemoveFileCommand.NotifyCanExecuteChanged();
    }

    private void AddDraft(GistEditorFileDraft draft)
    {
        draft.PropertyChanged += Draft_PropertyChanged;
        Files.Add(draft);
    }

    private void Draft_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => NotifyValidationChanged();

    private void NotifyValidationChanged()
    {
        OnPropertyChanged(nameof(CanRemoveFile));
        OnPropertyChanged(nameof(CanSave));
        RemoveFileCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class GistsPageViewModel : ViewModelBase, IDisposable, IAsyncDisposable
{
    private const int PageSize = 100;
    private readonly IGitHubGistQueryService _queryService;
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly ITelemetryService _telemetry;
    private readonly Dictionary<string, GitHubGist> _library = new(StringComparer.Ordinal);
    private readonly object _libraryGate = new();
    private readonly GistSynchronizationGate _synchronizationGate = new();
    private readonly SemaphoreSlim _projectionApplyGate = new(1, 1);
    private readonly object _backgroundWorkGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private readonly Dictionary<CancellationTokenSource, Task> _cancellationSourceTasks = [];
    private readonly List<CancellationTokenSource> _retiredCancellationTokenSources = [];
    private CancellationTokenSource? _lifetimeCancellationTokenSource;
    private CancellationTokenSource? _synchronizationCancellationTokenSource;
    private CancellationTokenSource? _detailCancellationTokenSource;
    private CancellationTokenSource? _fullFileCancellationTokenSource;
    private CancellationTokenSource? _mutationCancellationTokenSource;
    private CancellationTokenSource? _searchCancellationTokenSource;
    private TaskCompletionSource? _initializationCompletion;
    private bool _initialized;
    private bool _hasCompleteCachedLibrary;
    private string? _activeAccountPartition;
    private int _accountGeneration;
    private int _projectionVersion;
    private int _detailLoadVersion;
    private int _fullFileLoadVersion;
    private Task _projectionCompletion = Task.CompletedTask;

    internal GistProjectionApplyStatistics LastProjectionApplyStatistics { get; private set; }

    internal string? ActiveAccountPartition => _activeAccountPartition;

    public GistsPageViewModel(
        IGitHubGistQueryService queryService,
        IAuthService authService,
        IAccountService accountService,
        ITelemetryService telemetry)
    {
        _queryService = queryService;
        _authService = authService;
        _accountService = accountService;
        _telemetry = SafeTelemetryService.Wrap(telemetry);
        NewGistCommand = new RelayCommand(() => NewGistRequested?.Invoke(this, EventArgs.Empty));
        EditGistCommand = new RelayCommand(() => EditGistRequested?.Invoke(this, EventArgs.Empty), () => HasSelection && !IsMutating && !IsDetailLoading);
        DeleteGistCommand = new RelayCommand(() => DeleteGistRequested?.Invoke(this, EventArgs.Empty), () => HasSelection && !IsMutating);
        CopyLinkCommand = new RelayCommand(() => CopyRequested?.Invoke(this, EventArgs.Empty), () => HasSelection);
        ShareCommand = new RelayCommand(() => ShareRequested?.Invoke(this, EventArgs.Empty), () => HasSelection);
        CopyFileCommand = new RelayCommand(() => CopyFileRequested?.Invoke(this, EventArgs.Empty), () => CanExportSelectedFile);
        SaveFileCommand = new RelayCommand(() => SaveFileRequested?.Invoke(this, EventArgs.Empty), () => CanExportSelectedFile);
        LoadFullFileCommand = new AsyncRelayCommand(LoadFullFileAsync, () => CanLoadFullFile);
    }

    public event EventHandler? NewGistRequested;

    public event EventHandler? EditGistRequested;

    public event EventHandler? DeleteGistRequested;

    public event EventHandler? ShareRequested;

    public event EventHandler? CopyRequested;

    public event EventHandler? CopyFileRequested;

    public event EventHandler? SaveFileRequested;

    internal event EventHandler? VisibleProjectionApplying;

    internal event EventHandler? VisibleProjectionApplied;

    public KeyedObservableCollection<GistViewItem, GitHubGist> Gists { get; } = [];

    public KeyedObservableCollection<GistFileViewItem, GitHubGistFile> Files { get; } = [];

    public IRelayCommand NewGistCommand { get; }

    public IRelayCommand EditGistCommand { get; }

    public IRelayCommand DeleteGistCommand { get; }

    public IRelayCommand CopyLinkCommand { get; }

    public IRelayCommand ShareCommand { get; }

    public IRelayCommand CopyFileCommand { get; }

    public IRelayCommand SaveFileCommand { get; }

    public IAsyncRelayCommand LoadFullFileCommand { get; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial GistVisibilityFilter VisibilityFilter { get; set; }

    [ObservableProperty]
    public partial GistLibrarySort Sort { get; set; }

    [ObservableProperty]
    public partial GistViewItem? SelectedGistItem { get; set; }

    [ObservableProperty]
    public partial GistFileViewItem? SelectedFile { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    public partial bool IsDetailLoading { get; set; }

    [ObservableProperty]
    public partial bool IsMutating { get; set; }

    [ObservableProperty]
    public partial bool IsFullFileLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool IsErrorVisible { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDurabilityWarningVisible { get; set; }

    [ObservableProperty]
    public partial string DurabilityWarningMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    public string SelectedTitle => SelectedGistItem?.Title ?? "Select a gist";

    public string SelectedDescription => string.IsNullOrWhiteSpace(SelectedGistItem?.Gist.Description)
        ? "No description"
        : SelectedGistItem.Gist.Description!;

    public string SelectedVisibilityText => SelectedGistItem?.VisibilityText ?? string.Empty;

    public string SelectedUpdatedText => SelectedGistItem?.UpdatedText ?? string.Empty;

    public string SelectedFileContent => SelectedFile?.ContentText ?? string.Empty;

    public string SelectedFileMeta => SelectedFile is null ? string.Empty : $"{SelectedFile.LanguageText}  {SelectedFile.SizeText}";

    public bool IsSelectedFileTruncated => SelectedFile?.IsTruncated == true;

    public string SelectedFileTruncationText => SelectedFile?.TruncationText ?? string.Empty;

    public bool IsSelectedFilePreviewCapped => SelectedFile?.IsPreviewCapped == true;

    public string SelectedFilePreviewStatus => SelectedFile?.PreviewStatus ?? string.Empty;

    public bool CanExportSelectedFile => SelectedFile?.File is { Truncated: false, Content: not null };

    public bool CanLoadFullFile => !IsFullFileLoading
        && SelectedFile?.File is { Truncated: true, RawUrl.Length: > 0 };

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        if (!TryGetSession(out string accessToken, out string userId))
        {
            if (_activeAccountPartition is not null)
            {
                ResetForAccountChange(accountPartition: null);
            }

            _initialized = true;
            ShowError("Sign in to view and manage gists.");
            UpdateEmptyState();
            return;
        }

        string accountPartition = GitHubAccountPartition.Resolve(accessToken, userId);
        if (!string.Equals(_activeAccountPartition, accountPartition, StringComparison.Ordinal))
        {
            ResetForAccountChange(accountPartition);
        }

        _initialized = true;
        _lifetimeCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _initializationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = _lifetimeCancellationTokenSource.Token;
        _telemetry.TrackEvent("gists.opened", new Dictionary<string, string?> { ["source"] = "shell" });
        IsLoading = Gists.Count == 0;
        Stopwatch cachedFirstStopwatch = Stopwatch.StartNew();
        try
        {
            GistCachedLibrarySnapshot cachedLibrary = await _queryService.GetCachedLibraryAsync(
                accessToken,
                userId,
                PageSize,
                token);
            _hasCompleteCachedLibrary = cachedLibrary.IsComplete;
            await MergeAndApplyVisibleGistsAsync(cachedLibrary.Items, cancellationToken: token);

            if (cachedLibrary.Items.Length > 0)
            {
                TrackListLoaded(
                    cachedLibrary.CacheState,
                    "success",
                    GetLibraryCount(),
                    "cached_first",
                    cachedFirstStopwatch.Elapsed);
            }
            else
            {
                CachedResult<GitHubGist[]> firstPage = await _queryService.GetPageAsync(
                    accessToken,
                    userId,
                    1,
                    PageSize,
                    QueryFetchPolicy.StaleFirst,
                    GitHubRequestPriority.Visible,
                    token);
                GitHubGist[] firstPageItems = firstPage.Value ?? [];
                _hasCompleteCachedLibrary = firstPageItems.Length < PageSize;
                await MergeAndApplyVisibleGistsAsync(firstPageItems, cancellationToken: token);
                TrackListLoaded(
                    firstPage.CacheState,
                    "success",
                    GetLibraryCount(),
                    "cached_first",
                    cachedFirstStopwatch.Elapsed);
            }

            StartSynchronization(accessToken, userId);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ShowError(Gists.Count == 0
                ? "Gists could not be loaded."
                : "Showing saved gists. Sync is temporarily unavailable.");
            TrackListLoaded(
                CacheState.Error,
                "error",
                GetLibraryCount(),
                "cached_first",
                cachedFirstStopwatch.Elapsed);
        }
        finally
        {
            IsLoading = false;
            UpdateEmptyState();
            _initializationCompletion.TrySetResult();
        }
    }

    public void SetVisibilityFilter(GistVisibilityFilter filter)
    {
        if (VisibilityFilter == filter)
        {
            return;
        }

        VisibilityFilter = filter;
        ScheduleVisibleGistsProjection();
        _telemetry.TrackEvent("gists.filter.changed", new Dictionary<string, string?> { ["filter_type"] = "visibility" });
    }

    public void SetSort(GistLibrarySort sort)
    {
        if (Sort == sort)
        {
            return;
        }

        Sort = sort;
        ScheduleVisibleGistsProjection();
        _telemetry.TrackEvent("gists.filter.changed", new Dictionary<string, string?> { ["filter_type"] = "sort" });
    }

    public GistEditorSession CreateNewEditorSession() => GistEditorSession.CreateNew();

    public GistEditorSession? CreateEditEditorSession() => SelectedGistItem is null
        ? null
        : GistEditorSession.CreateForEdit(SelectedGistItem.Gist);

    public Task<bool> CreateGistAsync(GistEditorSession session, CancellationToken cancellationToken = default) =>
        RunOwnedMutationAsync("create", token => CreateGistCoreAsync(session, token), cancellationToken);

    private async Task<bool> CreateGistCoreAsync(GistEditorSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetSession(out string accessToken, out string userId))
        {
            ShowError("Sign in to create a gist.");
            return false;
        }

        if (!await EnsureGistWriteAccessAsync(accessToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetSession(out accessToken, out userId))
        {
            ShowError("GitHub authentication is unavailable after reconnecting.");
            return false;
        }

        BeginMutation();
        IsMutating = true;
        bool remoteCommitted = false;
        bool durabilityDegraded = false;
        try
        {
            GistMutationResult<GitHubGist> mutation = await _queryService.CreateAsync(
                accessToken,
                userId,
                session.BuildCreateRequest(),
                cancellationToken);
            remoteCommitted = true;
            durabilityDegraded = mutation.IsDurabilityDegraded;
            GitHubGist created = mutation.Value;
            ClearError();
            bool projectionFailed = !await TryApplyCommittedMutationAsync(
                () => UpsertLibraryItem(created),
                created.Id);
            ShowPostCommitWarning(mutation.IsDurabilityDegraded, projectionFailed);
            return true;
        }
        catch (Exception ex) when (remoteCommitted)
        {
            Debug.WriteLine($"GitHub completed the Gist creation, but local commit projection failed: {ex}");
            ClearError();
            ShowPostCommitWarning(durabilityDegraded, projectionFailed: true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError("The gist could not be created. Your draft is still available.");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsMutating = false;
            RestartSynchronizationAfterMutation(remoteCommitted, durabilityDegraded, cancellationToken);
        }
    }

    public Task<bool> UpdateSelectedGistAsync(GistEditorSession session, CancellationToken cancellationToken = default) =>
        RunOwnedMutationAsync("edit", token => UpdateSelectedGistCoreAsync(session, token), cancellationToken);

    private async Task<bool> UpdateSelectedGistCoreAsync(GistEditorSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedGistItem is not { } selected || !TryGetSession(out string accessToken, out string userId))
        {
            return false;
        }

        if (!await EnsureGistWriteAccessAsync(accessToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetSession(out accessToken, out userId))
        {
            ShowError("GitHub authentication is unavailable after reconnecting.");
            return false;
        }

        BeginMutation();
        IsMutating = true;
        string gistId = selected.StableKey;
        bool remoteCommitted = false;
        bool durabilityDegraded = false;
        try
        {
            GistMutationResult<GitHubGist> mutation = await _queryService.UpdateAsync(
                accessToken,
                userId,
                gistId,
                session.BuildUpdateRequest(selected.Gist),
                cancellationToken);
            remoteCommitted = true;
            durabilityDegraded = mutation.IsDurabilityDegraded;
            GitHubGist updated = mutation.Value;
            ClearError();
            bool projectionFailed = !await TryApplyCommittedMutationAsync(
                () =>
                {
                    UpsertLibraryItem(updated);
                    selected.ApplyGist(updated);
                    ApplySelectedDetail(updated);
                },
                gistId);
            ShowPostCommitWarning(mutation.IsDurabilityDegraded, projectionFailed);
            return true;
        }
        catch (Exception ex) when (remoteCommitted)
        {
            Debug.WriteLine($"GitHub completed the Gist update, but local commit projection failed: {ex}");
            ClearError();
            ShowPostCommitWarning(durabilityDegraded, projectionFailed: true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError("The gist could not be saved. Your edits are still available.");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsMutating = false;
            RestartSynchronizationAfterMutation(remoteCommitted, durabilityDegraded, cancellationToken);
        }
    }

    public Task<bool> DeleteSelectedGistAsync(CancellationToken cancellationToken = default) =>
        RunOwnedMutationAsync("delete", DeleteSelectedGistCoreAsync, cancellationToken);

    private async Task<bool> DeleteSelectedGistCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedGistItem is not { } selected || !TryGetSession(out string accessToken, out string userId))
        {
            return false;
        }

        if (!await EnsureGistWriteAccessAsync(accessToken))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetSession(out accessToken, out userId))
        {
            ShowError("GitHub authentication is unavailable after reconnecting.");
            return false;
        }

        BeginMutation();
        IsMutating = true;
        string gistId = selected.StableKey;
        bool remoteCommitted = false;
        bool durabilityDegraded = false;
        try
        {
            GistMutationResult<bool> mutation = await _queryService.DeleteAsync(
                accessToken,
                userId,
                gistId,
                cancellationToken);
            remoteCommitted = true;
            durabilityDegraded = mutation.IsDurabilityDegraded;
            ClearError();
            bool projectionFailed = !await TryApplyCommittedMutationAsync(
                () =>
                {
                    RemoveLibraryItem(gistId);
                    SelectedGistItem = null;
                });
            ShowPostCommitWarning(mutation.IsDurabilityDegraded, projectionFailed);
            return true;
        }
        catch (Exception ex) when (remoteCommitted)
        {
            Debug.WriteLine($"GitHub completed the Gist deletion, but local commit projection failed: {ex}");
            ClearError();
            ShowPostCommitWarning(durabilityDegraded, projectionFailed: true);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError("The gist could not be deleted. Nothing was removed locally.");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            IsMutating = false;
            RestartSynchronizationAfterMutation(remoteCommitted, durabilityDegraded, cancellationToken);
        }
    }

    public void ReportActionError(string message, string action)
    {
        ShowError(message);
        TrackAction(action, "error");
    }

    public void TrackShareSuccess() => TrackAction("share", "success");

    public void TrackCopySuccess() => TrackAction("copy_link", "success");

    public void TrackCopyFileSuccess() => TrackAction("copy_file", "success");

    public void TrackActionSuccess(string action) => TrackAction(action, "success");

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _initialized = false;
        _synchronizationGate.Invalidate();
        CancelBackgroundWork();

        Task? initializationTask = _initializationCompletion?.Task;
        Task? fullFileTask = LoadFullFileCommand.ExecutionTask;
        while (true)
        {
            Task[] tracked;
            lock (_backgroundWorkGate)
            {
                tracked = _backgroundTasks.Where(static task => !task.IsCompleted).ToArray();
            }

            Task[] pending = tracked
                .Concat(initializationTask is { IsCompleted: false } ? [initializationTask] : [])
                .Concat(fullFileTask is { IsCompleted: false } ? [fullFileTask] : [])
                .Distinct()
                .ToArray();
            if (pending.Length == 0)
            {
                break;
            }

            try
            {
                await Task.WhenAll(pending).WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
            }

            initializationTask = null;
            fullFileTask = null;
        }

        await _queryService.DrainBackgroundWorkAsync(cancellationToken);
        DisposeCancellationTokenSources();
        _initializationCompletion = null;
        IsLoading = false;
        IsLoadingMore = false;
        IsDetailLoading = false;
        IsFullFileLoading = false;
        IsMutating = false;
    }

    public void Dispose() => CancelBackgroundWork();

    public ValueTask DisposeAsync() => new(StopAsync());

    private void CancelBackgroundWork()
    {
        _searchCancellationTokenSource?.Cancel();
        _detailCancellationTokenSource?.Cancel();
        _fullFileCancellationTokenSource?.Cancel();
        _mutationCancellationTokenSource?.Cancel();
        _synchronizationCancellationTokenSource?.Cancel();
        _lifetimeCancellationTokenSource?.Cancel();
    }

    partial void OnSearchTextChanged(string value)
    {
        CancellationTokenSource source = ReplaceOperationCancellationTokenSource(ref _searchCancellationTokenSource);
        TrackBackgroundTask(ApplySearchAfterDelayAsync(source.Token), source);
    }

    partial void OnSelectedGistItemChanged(GistViewItem? value)
    {
        _fullFileCancellationTokenSource?.Cancel();
        Interlocked.Increment(ref _fullFileLoadVersion);
        HasSelection = value is not null;
        NotifySelectedGistProjectionChanged();
        NotifyCommandStateChanged();
        if (value is null)
        {
            Files.ApplySnapshot(
                Array.Empty<GitHubGistFile>(),
                static file => file.Filename,
                static item => item.StableKey,
                GistFileViewItem.Create,
                static (item, file) => item.ApplyFile(file));
            SelectedFile = null;
            return;
        }

        ApplySelectedDetail(value.Gist);
        CancellationTokenSource source = ReplaceOperationCancellationTokenSource(ref _detailCancellationTokenSource);
        TrackBackgroundTask(LoadSelectedDetailAsync(value.StableKey, source.Token), source);
    }

    partial void OnSelectedFileChanged(GistFileViewItem? value)
    {
        _fullFileCancellationTokenSource?.Cancel();
        Interlocked.Increment(ref _fullFileLoadVersion);
        OnPropertyChanged(nameof(SelectedFileContent));
        OnPropertyChanged(nameof(SelectedFileMeta));
        OnPropertyChanged(nameof(IsSelectedFileTruncated));
        OnPropertyChanged(nameof(SelectedFileTruncationText));
        OnPropertyChanged(nameof(IsSelectedFilePreviewCapped));
        OnPropertyChanged(nameof(SelectedFilePreviewStatus));
        OnPropertyChanged(nameof(CanExportSelectedFile));
        CopyFileCommand.NotifyCanExecuteChanged();
        SaveFileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanLoadFullFile));
        LoadFullFileCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsMutatingChanged(bool value) => NotifyCommandStateChanged();

    partial void OnIsDetailLoadingChanged(bool value) => NotifyCommandStateChanged();

    partial void OnIsFullFileLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLoadFullFile));
        LoadFullFileCommand.NotifyCanExecuteChanged();
    }

    private async Task SynchronizeAllPagesAsync(
        string accessToken,
        string userId,
        int generation,
        bool followsCommittedMutation,
        bool committedMutationDurabilityDegraded,
        CancellationToken cancellationToken)
    {
        Stopwatch reconciliationStopwatch = Stopwatch.StartNew();
        IsLoadingMore = true;
        HashSet<string> completeSnapshotIds = new(StringComparer.Ordinal);
        try
        {
            for (int page = 1; page <= 1000; page++)
            {
                CachedResult<GitHubGist[]> result = await _queryService.GetPageAsync(
                    accessToken,
                    userId,
                    page,
                    PageSize,
                    QueryFetchPolicy.NetworkOnly,
                    GitHubRequestPriority.BackgroundRefresh,
                    cancellationToken);
                if (!_synchronizationGate.IsCurrent(generation))
                {
                    return;
                }

                GitHubGist[] pageItems = result.Value ?? [];
                foreach (GitHubGist gist in pageItems)
                {
                    completeSnapshotIds.Add(gist.Id);
                }

                await MergeAndApplyVisibleGistsAsync(pageItems, cancellationToken: cancellationToken);
                if (pageItems.Length < PageSize)
                {
                    if (!_synchronizationGate.IsCurrent(generation))
                    {
                        return;
                    }

                    string? selectedId = SelectedGistItem?.StableKey;
                    bool selectionRemoved = await ReconcileAuthoritativeLibraryAsync(
                        completeSnapshotIds,
                        selectedId,
                        cancellationToken);
                    if (selectionRemoved)
                    {
                        SelectedGistItem = null;
                    }

                    _hasCompleteCachedLibrary = true;
                    await ApplyVisibleGistsAsync(cancellationToken: cancellationToken);
                    TrackListLoaded(
                        result.CacheState,
                        "success",
                        GetLibraryCount(),
                        "reconciliation",
                        reconciliationStopwatch.Elapsed);
                    IsErrorVisible = false;
                    IsDurabilityWarningVisible = false;
                    DurabilityWarningMessage = string.Empty;
                    return;
                }
            }

            if (followsCommittedMutation)
            {
                ClearError();
                ShowPostCommitWarning(committedMutationDurabilityDegraded, projectionFailed: true);
            }
            else
            {
                ShowError("Gist synchronization reached its safety limit. Saved rows remain available.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            int libraryCount = GetLibraryCount();
            if (followsCommittedMutation)
            {
                ClearError();
                ShowPostCommitWarning(committedMutationDurabilityDegraded, projectionFailed: true);
            }
            else
            {
                ShowError(libraryCount == 0
                    ? "Gists could not be synchronized."
                    : _hasCompleteCachedLibrary
                        ? "Offline. Showing all saved gists."
                        : $"Offline. Showing {libraryCount} cached gists; the library may be incomplete.");
            }
            TrackListLoaded(
                CacheState.Error,
                "error",
                libraryCount,
                "reconciliation",
                reconciliationStopwatch.Elapsed);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private async Task LoadSelectedDetailAsync(string gistId, CancellationToken cancellationToken)
    {
        Stopwatch detailStopwatch = Stopwatch.StartNew();
        int version = ++_detailLoadVersion;
        int generation = _synchronizationGate.Capture();
        if (!TryGetSession(out string accessToken, out string userId))
        {
            return;
        }

        IsDetailLoading = true;
        try
        {
            CachedResult<GitHubGist> result = await _queryService.GetDetailAsync(
                accessToken,
                userId,
                gistId,
                QueryFetchPolicy.StaleFirst,
                GitHubRequestPriority.Visible,
                cancellationToken);
            if (!CanApplyDetail(version, generation, gistId) || result.Value is null)
            {
                return;
            }

            ApplyRefreshedDetail(gistId, result.Value);
            IsErrorVisible = false;

            if (version == _detailLoadVersion)
            {
                IsDetailLoading = false;
            }

            if (result.CacheState == CacheState.Stale || result.IsRefreshInProgress)
            {
                CachedResult<GitHubGist> refreshed = await _queryService.GetDetailAsync(
                    accessToken,
                    userId,
                    gistId,
                    QueryFetchPolicy.NetworkOnly,
                    GitHubRequestPriority.BackgroundRefresh,
                    cancellationToken);
                if (!CanApplyDetail(version, generation, gistId) || refreshed.Value is null)
                {
                    return;
                }

                ApplyRefreshedDetail(gistId, refreshed.Value);
                IsErrorVisible = false;
            }

            TrackAction("detail_selection", "success", detailStopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ShowError("Showing saved gist details. The latest files are temporarily unavailable.");
            TrackAction("detail_selection", "error", detailStopwatch.Elapsed);
        }
        finally
        {
            if (version == _detailLoadVersion)
            {
                IsDetailLoading = false;
            }
        }
    }

    private async Task ApplySearchAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(140, cancellationToken);
            await ApplyVisibleGistsAsync(cancellationToken: cancellationToken);
            _telemetry.TrackEvent("gists.filter.changed", new Dictionary<string, string?> { ["filter_type"] = "search" });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MergeAndApplyVisibleGistsAsync(
        GitHubGist[] gists,
        string? preferredSelectionId = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(
            () =>
            {
                lock (_libraryGate)
                {
                    MergePageCore(gists);
                }
            },
            cancellationToken);
        await ApplyVisibleGistsAsync(preferredSelectionId, cancellationToken);
    }

    private void MergePageCore(IEnumerable<GitHubGist> gists)
    {
        foreach (GitHubGist gist in gists)
        {
            if (!string.IsNullOrWhiteSpace(gist.Id))
            {
                PreserveLoadedFileContentCore(gist);
                _library[gist.Id] = gist;
            }
        }
    }

    private void ScheduleVisibleGistsProjection()
    {
        CancellationToken token = _lifetimeCancellationTokenSource?.Token ?? default;
        Task projection = ApplyVisibleGistsAsync(cancellationToken: token);
        _projectionCompletion = projection;
        TrackBackgroundTask(projection);
    }

    internal Task WaitForProjectionAsync(CancellationToken cancellationToken = default) =>
        _projectionCompletion.WaitAsync(cancellationToken);

    private async Task ApplyVisibleGistsAsync(
        string? preferredSelectionId = null,
        CancellationToken cancellationToken = default)
    {
        int projectionVersion = Interlocked.Increment(ref _projectionVersion);
        int accountGeneration = Volatile.Read(ref _accountGeneration);
        string search = SearchText;
        GistVisibilityFilter filter = VisibilityFilter;
        GistLibrarySort sort = Sort;
        GistLibraryProjectionSnapshot projection = await Task.Run(
            () =>
            {
                lock (_libraryGate)
                {
                    return GistLibraryProjection.CreateSnapshot(_library.Values, search, filter, sort);
                }
            },
            cancellationToken);
        if (!CanApplyProjection(projectionVersion, accountGeneration))
        {
            return;
        }

        await _projectionApplyGate.WaitAsync(cancellationToken);
        try
        {
            if (!CanApplyProjection(projectionVersion, accountGeneration))
            {
                return;
            }

            string? selectedId = preferredSelectionId ?? SelectedGistItem?.StableKey;
            VisibleProjectionApplying?.Invoke(this, EventArgs.Empty);
            try
            {
                LastProjectionApplyStatistics = await ApplyProjectionBudgetedAsync(projection, cancellationToken);
            }
            finally
            {
                VisibleProjectionApplied?.Invoke(this, EventArgs.Empty);
            }

            GistViewItem? selection = selectedId is null
                ? null
                : Gists.FirstOrDefault(item => string.Equals(item.StableKey, selectedId, StringComparison.Ordinal));
            if (selection is not null && !ReferenceEquals(selection, SelectedGistItem))
            {
                SelectedGistItem = selection;
            }
            else if (SelectedGistItem is null && Gists.Count > 0)
            {
                SelectedGistItem = Gists[0];
            }

            UpdateEmptyState();
        }
        finally
        {
            _projectionApplyGate.Release();
        }
    }

    private bool CanApplyProjection(int projectionVersion, int accountGeneration) =>
        projectionVersion == Volatile.Read(ref _projectionVersion) &&
        accountGeneration == Volatile.Read(ref _accountGeneration);

    private async Task<GistProjectionApplyStatistics> ApplyProjectionBudgetedAsync(
        GistLibraryProjectionSnapshot projection,
        CancellationToken cancellationToken)
    {
        Dictionary<string, GistViewItem> existingItems = new(StringComparer.Ordinal);
        int operations = 0;
        int sliceOperations = 0;
        int maximumSliceOperations = 0;
        int yieldCount = 0;
        long sliceStarted = Stopwatch.GetTimestamp();

        async ValueTask RecordOperationAsync()
        {
            operations++;
            sliceOperations++;
            if (sliceOperations < GistProjectionApplyPolicy.MaximumOperationsPerSlice &&
                Stopwatch.GetElapsedTime(sliceStarted) < GistProjectionApplyPolicy.MaximumTimePerSlice)
            {
                return;
            }

            maximumSliceOperations = Math.Max(maximumSliceOperations, sliceOperations);
            yieldCount++;
            sliceOperations = 0;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            sliceStarted = Stopwatch.GetTimestamp();
        }

        foreach (GistViewItem item in Gists)
        {
            existingItems.TryAdd(item.StableKey, item);
            await RecordOperationAsync();
        }

        for (int targetIndex = 0; targetIndex < projection.Items.Length; targetIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitHubGist gist = projection.Items[targetIndex];
            GistViewItem item;
            if (targetIndex < Gists.Count &&
                string.Equals(Gists[targetIndex].StableKey, gist.Id, StringComparison.Ordinal))
            {
                item = Gists[targetIndex];
            }
            else if (existingItems.TryGetValue(gist.Id, out GistViewItem? existing))
            {
                item = existing;
                int currentIndex = Gists.IndexOf(item);
                if (currentIndex >= 0 && currentIndex != targetIndex)
                {
                    Gists.Move(currentIndex, targetIndex);
                }
            }
            else
            {
                item = GistViewItem.Create(gist);
                Gists.Insert(targetIndex, item);
                existingItems[gist.Id] = item;
            }

            item.ApplyGist(gist);
            await RecordOperationAsync();
        }

        for (int index = Gists.Count - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!projection.Keys.Contains(Gists[index].StableKey))
            {
                Gists.RemoveAt(index);
            }

            await RecordOperationAsync();
        }

        maximumSliceOperations = Math.Max(maximumSliceOperations, sliceOperations);
        return new GistProjectionApplyStatistics(
            projection.Items.Length,
            operations,
            yieldCount,
            maximumSliceOperations);
    }

    private Task<bool> ReconcileAuthoritativeLibraryAsync(
        IReadOnlySet<string> authoritativeIds,
        string? selectedId,
        CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                lock (_libraryGate)
                {
                    foreach (string missingId in _library.Keys
                        .Where(id => !authoritativeIds.Contains(id))
                        .ToArray())
                    {
                        _library.Remove(missingId);
                    }

                    return selectedId is not null && !_library.ContainsKey(selectedId);
                }
            },
            cancellationToken);

    private void ResetForAccountChange(string? accountPartition)
    {
        _activeAccountPartition = accountPartition is null
            ? null
            : GitHubAccountPartition.Require(accountPartition);
        Interlocked.Increment(ref _accountGeneration);
        Interlocked.Increment(ref _projectionVersion);
        Interlocked.Increment(ref _detailLoadVersion);
        Interlocked.Increment(ref _fullFileLoadVersion);
        _synchronizationGate.Invalidate();
        lock (_libraryGate)
        {
            _library.Clear();
        }

        SelectedGistItem = null;
        SelectedFile = null;
        Gists.Clear();
        Files.Clear();
        _hasCompleteCachedLibrary = false;
        IsErrorVisible = false;
        ErrorMessage = string.Empty;
        IsDurabilityWarningVisible = false;
        DurabilityWarningMessage = string.Empty;
        IsEmpty = true;
        LastProjectionApplyStatistics = default;
        _projectionCompletion = Task.CompletedTask;
    }

    private int GetLibraryCount()
    {
        lock (_libraryGate)
        {
            return _library.Count;
        }
    }

    private void UpsertLibraryItem(GitHubGist gist)
    {
        lock (_libraryGate)
        {
            PreserveLoadedFileContentCore(gist);
            _library[gist.Id] = gist;
        }
    }

    private void RemoveLibraryItem(string gistId)
    {
        lock (_libraryGate)
        {
            _library.Remove(gistId);
        }
    }

    private void ApplySelectedDetail(GitHubGist gist)
    {
        string? selectedFilename = SelectedFile?.StableKey;
        GitHubGistFile[] files = gist.Files.Values
            .OrderBy(static file => file.Filename, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Files.ApplySnapshot(
            files,
            static file => file.Filename,
            static item => item.StableKey,
            GistFileViewItem.Create,
            static (item, file) => item.ApplyFile(file));
        SelectedFile = selectedFilename is null
            ? Files.FirstOrDefault()
            : Files.FirstOrDefault(file => string.Equals(file.StableKey, selectedFilename, StringComparison.Ordinal)) ?? Files.FirstOrDefault();
        NotifySelectedGistProjectionChanged();
    }

    private async Task LoadFullFileAsync()
    {
        if (SelectedGistItem is not { } selectedGist ||
            SelectedFile is not { File.Truncated: true } selectedFile ||
            string.IsNullOrWhiteSpace(selectedFile.File.RawUrl) ||
            !TryGetSession(out _, out string userId))
        {
            return;
        }

        string gistId = selectedGist.StableKey;
        string filename = selectedFile.StableKey;
        int version = Interlocked.Increment(ref _fullFileLoadVersion);
        int generation = _synchronizationGate.Capture();
        CancellationTokenSource source = ReplaceOperationCancellationTokenSource(ref _fullFileCancellationTokenSource);
        IsFullFileLoading = true;
        try
        {
            string partition = GitHubAccountPartition.Require(userId);
            CachedResult<string> result = await _queryService.GetRawFileAsync(
                partition,
                selectedFile.File.RawUrl!,
                QueryFetchPolicy.StaleFirst,
                GitHubRequestPriority.Visible,
                source.Token);
            if (!CanApplyFullFile(version, generation, gistId, filename) || result.Value is null)
            {
                return;
            }

            ApplyFullFileContent(gistId, filename, result.Value);
            IsErrorVisible = false;

            if (result.CacheState == CacheState.Stale || result.IsRefreshInProgress)
            {
                CachedResult<string> refreshed = await _queryService.GetRawFileAsync(
                    partition,
                    selectedFile.File.RawUrl!,
                    QueryFetchPolicy.NetworkOnly,
                    GitHubRequestPriority.BackgroundRefresh,
                    source.Token);
                if (!CanApplyFullFile(version, generation, gistId, filename) || refreshed.Value is null)
                {
                    return;
                }

                ApplyFullFileContent(gistId, filename, refreshed.Value);
                IsErrorVisible = false;
            }

            TrackAction("load_full_file", "success");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ShowError("The full Gist file could not be loaded. The partial preview remains visible.");
            TrackAction("load_full_file", "error");
        }
        finally
        {
            if (version == _fullFileLoadVersion)
            {
                IsFullFileLoading = false;
            }
        }
    }

    private bool CanApplyDetail(int version, int generation, string gistId) =>
        version == _detailLoadVersion &&
        _synchronizationGate.IsCurrent(generation) &&
        string.Equals(SelectedGistItem?.StableKey, gistId, StringComparison.Ordinal);

    private void ApplyRefreshedDetail(string gistId, GitHubGist gist)
    {
        if (!string.Equals(gist.Id, gistId, StringComparison.Ordinal))
        {
            return;
        }

        lock (_libraryGate)
        {
            PreserveLoadedFileContentCore(gist);
            _library[gistId] = gist;
        }

        SelectedGistItem!.ApplyGist(gist);
        ApplySelectedDetail(gist);
    }

    private bool CanApplyFullFile(int version, int generation, string gistId, string filename) =>
        version == _fullFileLoadVersion &&
        _synchronizationGate.IsCurrent(generation) &&
        string.Equals(SelectedGistItem?.StableKey, gistId, StringComparison.Ordinal) &&
        string.Equals(SelectedFile?.StableKey, filename, StringComparison.Ordinal);

    private void ApplyFullFileContent(string gistId, string filename, string content)
    {
        SelectedFile!.ApplyFullContent(content);
        lock (_libraryGate)
        {
            if (_library.TryGetValue(gistId, out GitHubGist? gist) &&
                gist.Files.TryGetValue(filename, out GitHubGistFile? file))
            {
                file.Content = content;
                file.Truncated = false;
            }
        }

        OnPropertyChanged(nameof(SelectedFileContent));
        OnPropertyChanged(nameof(IsSelectedFileTruncated));
        OnPropertyChanged(nameof(SelectedFileTruncationText));
        OnPropertyChanged(nameof(IsSelectedFilePreviewCapped));
        OnPropertyChanged(nameof(SelectedFilePreviewStatus));
        OnPropertyChanged(nameof(CanExportSelectedFile));
        OnPropertyChanged(nameof(CanLoadFullFile));
        CopyFileCommand.NotifyCanExecuteChanged();
        SaveFileCommand.NotifyCanExecuteChanged();
        LoadFullFileCommand.NotifyCanExecuteChanged();
    }

    private void StartSynchronization(
        string accessToken,
        string userId,
        bool followsCommittedMutation = false,
        bool committedMutationDurabilityDegraded = false)
    {
        CancellationTokenSource source = ReplaceOperationCancellationTokenSource(ref _synchronizationCancellationTokenSource);
        int generation = _synchronizationGate.Capture();
        TrackBackgroundTask(
            SynchronizeAllPagesAsync(
                accessToken,
                userId,
                generation,
                followsCommittedMutation,
                committedMutationDurabilityDegraded,
                source.Token),
            source);
    }

    private void RestartSynchronization(
        bool followsCommittedMutation = false,
        bool committedMutationDurabilityDegraded = false)
    {
        if (_initialized && TryGetSession(out string accessToken, out string userId))
        {
            StartSynchronization(
                accessToken,
                userId,
                followsCommittedMutation,
                committedMutationDurabilityDegraded);
        }
    }

    private void RestartSynchronizationAfterMutation(
        bool remoteCommitted,
        bool durabilityDegraded,
        CancellationToken cancellationToken)
    {
        if (!_initialized || (!remoteCommitted && cancellationToken.IsCancellationRequested))
        {
            return;
        }

        try
        {
            RestartSynchronization(remoteCommitted, durabilityDegraded);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Gist reconciliation could not be scheduled: {ex}");
            if (remoteCommitted)
            {
                ClearError();
                ShowPostCommitWarning(durabilityDegraded, projectionFailed: true);
            }
        }
    }

    private void BeginMutation()
    {
        _synchronizationGate.Invalidate();
        _synchronizationCancellationTokenSource?.Cancel();
        _detailCancellationTokenSource?.Cancel();
        _fullFileCancellationTokenSource?.Cancel();
        Interlocked.Increment(ref _detailLoadVersion);
        Interlocked.Increment(ref _fullFileLoadVersion);
    }

    private async Task<bool> RunOwnedMutationAsync(
        string action,
        Func<CancellationToken, Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        CancellationTokenSource source = ReplaceOperationCancellationTokenSource(
            ref _mutationCancellationTokenSource,
            cancellationToken);
        Task<bool> operationTask = operation(source.Token);
        TrackBackgroundTask(operationTask, source);
        bool succeeded = await operationTask;
        TrackAction(action, succeeded ? "success" : "error", stopwatch.Elapsed);
        return succeeded;
    }

    private CancellationTokenSource ReplaceOperationCancellationTokenSource(
        ref CancellationTokenSource? current,
        CancellationToken cancellationToken = default)
    {
        if (current is not null)
        {
            current.Cancel();
            bool disposeNow = false;
            lock (_backgroundWorkGate)
            {
                if (_cancellationSourceTasks.TryGetValue(current, out Task? task) && task.IsCompleted)
                {
                    _cancellationSourceTasks.Remove(current);
                    disposeNow = true;
                }
                else
                {
                    _retiredCancellationTokenSources.Add(current);
                }
            }

            if (disposeNow)
            {
                current.Dispose();
            }
        }

        current = (_lifetimeCancellationTokenSource, cancellationToken.CanBeCanceled) switch
        {
            ({ } lifetime, true) => CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken),
            ({ } lifetime, false) => CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token),
            (null, true) => CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
            _ => new CancellationTokenSource()
        };
        return current;
    }

    private void TrackBackgroundTask(Task task, CancellationTokenSource? source = null)
    {
        Task observedTask = ObserveBackgroundTaskAsync(task, source);
        lock (_backgroundWorkGate)
        {
            _backgroundTasks.RemoveWhere(static candidate => candidate.IsCompleted);
            _backgroundTasks.Add(observedTask);
            if (source is not null)
            {
                _cancellationSourceTasks[source] = observedTask;
            }
        }
    }

    private async Task ObserveBackgroundTaskAsync(Task task, CancellationTokenSource? source)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Gists background operation failed: {ex}");
        }
        finally
        {
            CancellationTokenSource? disposeSource = null;
            lock (_backgroundWorkGate)
            {
                if (source is not null && !IsActiveCancellationSource(source))
                {
                    _cancellationSourceTasks.Remove(source);
                    _retiredCancellationTokenSources.Remove(source);
                    disposeSource = source;
                }

                _backgroundTasks.RemoveWhere(static candidate => candidate.IsCompleted);
            }

            disposeSource?.Dispose();
        }
    }

    private bool IsActiveCancellationSource(CancellationTokenSource source) =>
        ReferenceEquals(source, _searchCancellationTokenSource) ||
        ReferenceEquals(source, _detailCancellationTokenSource) ||
        ReferenceEquals(source, _fullFileCancellationTokenSource) ||
        ReferenceEquals(source, _mutationCancellationTokenSource) ||
        ReferenceEquals(source, _synchronizationCancellationTokenSource) ||
        ReferenceEquals(source, _lifetimeCancellationTokenSource);

    private void DisposeCancellationTokenSources()
    {
        CancellationTokenSource?[] active =
        [
            _searchCancellationTokenSource,
            _detailCancellationTokenSource,
            _fullFileCancellationTokenSource,
            _mutationCancellationTokenSource,
            _synchronizationCancellationTokenSource,
            _lifetimeCancellationTokenSource
        ];
        CancellationTokenSource[] trackedSources;
        CancellationTokenSource[] retiredSources;
        lock (_backgroundWorkGate)
        {
            trackedSources = _cancellationSourceTasks.Keys.ToArray();
            retiredSources = _retiredCancellationTokenSources.ToArray();
            _cancellationSourceTasks.Clear();
            _retiredCancellationTokenSources.Clear();
            _backgroundTasks.RemoveWhere(static task => task.IsCompleted);
        }

        foreach (CancellationTokenSource source in active
            .Where(static source => source is not null)
            .Cast<CancellationTokenSource>()
            .Concat(retiredSources)
            .Concat(trackedSources)
            .Distinct())
        {
            source.Dispose();
        }

        _searchCancellationTokenSource = null;
        _detailCancellationTokenSource = null;
        _fullFileCancellationTokenSource = null;
        _mutationCancellationTokenSource = null;
        _synchronizationCancellationTokenSource = null;
        _lifetimeCancellationTokenSource = null;
    }

    private void PreserveLoadedFileContentCore(GitHubGist incoming)
    {
        if (!_library.TryGetValue(incoming.Id, out GitHubGist? existing))
        {
            return;
        }

        foreach ((string filename, GitHubGistFile incomingFile) in incoming.Files)
        {
            if (!existing.Files.TryGetValue(filename, out GitHubGistFile? existingFile))
            {
                continue;
            }

            if (string.IsNullOrEmpty(incomingFile.Content) && !string.IsNullOrEmpty(existingFile.Content))
            {
                incomingFile.Content = existingFile.Content;
            }

            incomingFile.RawUrl ??= existingFile.RawUrl;

            if (!existingFile.Truncated && !string.IsNullOrEmpty(existingFile.Content))
            {
                incomingFile.Truncated = false;
            }
        }
    }

    private void NotifySelectedGistProjectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTitle));
        OnPropertyChanged(nameof(SelectedDescription));
        OnPropertyChanged(nameof(SelectedVisibilityText));
        OnPropertyChanged(nameof(SelectedUpdatedText));
    }

    private void NotifyCommandStateChanged()
    {
        EditGistCommand.NotifyCanExecuteChanged();
        DeleteGistCommand.NotifyCanExecuteChanged();
        CopyLinkCommand.NotifyCanExecuteChanged();
        ShareCommand.NotifyCanExecuteChanged();
    }

    private void UpdateEmptyState() => IsEmpty = !IsLoading && Gists.Count == 0;

    private bool TryGetSession(out string accessToken, out string userId)
    {
        long accountId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        accessToken = _authService.GetToken(accountId) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            userId = string.Empty;
            return false;
        }

        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            userId = "public";
            return true;
        }

        if (accountId <= 0)
        {
            userId = string.Empty;
            return false;
        }

        userId = accountId.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private async Task<bool> EnsureGistWriteAccessAsync(string accessToken)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(accessToken))
        {
            return true;
        }

        try
        {
            if (await _authService.EnsureScopesAsync("gist"))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not verify Gist OAuth scope: {ex}");
        }

        ShowError("Reconnect GitHub to create or change gists. Your current draft is still available.");
        TrackAction("scope_upgrade", "error");
        return false;
    }

    private void ShowError(string message)
    {
        ErrorMessage = message;
        IsErrorVisible = true;
    }

    private void ClearError()
    {
        ErrorMessage = string.Empty;
        IsErrorVisible = false;
    }

    private async Task<bool> TryApplyCommittedMutationAsync(
        Action applyLocalMutation,
        string? preferredSelectionId = null)
    {
        try
        {
            applyLocalMutation();
            await ApplyVisibleGistsAsync(preferredSelectionId, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GitHub completed the Gist mutation, but the local projection failed: {ex}");
            return false;
        }
    }

    private void ShowPostCommitWarning(bool isDurabilityDegraded, bool projectionFailed)
    {
        if (!isDurabilityDegraded && !projectionFailed)
        {
            IsDurabilityWarningVisible = false;
            DurabilityWarningMessage = string.Empty;
            return;
        }

        DurabilityWarningMessage = (isDurabilityDegraded, projectionFailed) switch
        {
            (true, true) =>
                "GitHub completed the change, but JitHub could not refresh the local view or save offline recovery data. JitHub will reconcile in the background.",
            (true, false) =>
                "GitHub completed the change, but JitHub could not save offline recovery data. Keep JitHub online until synchronization completes.",
            _ =>
                "GitHub completed the change, but JitHub could not refresh the local view. JitHub will reconcile in the background."
        };
        IsDurabilityWarningVisible = true;
    }

    private void TrackListLoaded(
        CacheState state,
        string result,
        int count,
        string? phase = null,
        TimeSpan? duration = null) =>
        _telemetry.TrackEvent(
            "gists.list.loaded",
            new Dictionary<string, string?>
            {
                ["cache_state"] = state.ToString(),
                ["result"] = result,
                ["count_bucket"] = CountBucket(count),
                ["phase"] = phase,
                ["duration_bucket"] = duration is null
                    ? null
                    : TelemetrySanitizer.CreateDurationBucket(duration.Value)
            });

    private void TrackAction(string action, string result, TimeSpan? duration = null)
    {
        try
        {
            _telemetry.TrackEvent(
                "gists.action.executed",
                new Dictionary<string, string?>
                {
                    ["action"] = action,
                    ["result"] = result,
                    ["duration_bucket"] = duration is null
                        ? null
                        : TelemetrySanitizer.CreateDurationBucket(duration.Value)
                });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Gists telemetry is unavailable for action '{action}': {ex}");
        }
    }

    private static string CountBucket(int count) => TelemetryTaxonomy.CountBucket(count);
}

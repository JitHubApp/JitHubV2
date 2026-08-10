using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Tests.TestDoubles;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class GistsPageViewModelTests
{
    [Fact]
    public void EditorFileDraft_ExposesCurrentFilenameToAutomation()
    {
        GistEditorFileDraft draft = new() { Filename = "first.txt" };
        List<string?> changedProperties = [];
        draft.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        Assert.Equal("Gist file first.txt", draft.AutomationName);

        draft.Filename = "renamed.txt";

        Assert.Equal("Gist file renamed.txt", draft.AutomationName);
        Assert.Contains(nameof(GistEditorFileDraft.AutomationName), changedProperties);
    }

    [Fact]
    public void EditorSession_RenamesUnavailableFileWithoutOverwritingItsContent()
    {
        GitHubGist gist = CreateGist("one", "Large file", truncated: true);
        GistEditorSession session = GistEditorSession.CreateForEdit(gist);

        session.SelectedFile!.Filename = "renamed.txt";
        GitHubGistUpdateRequest request = session.BuildUpdateRequest(gist);

        GitHubGistFileUpdateRequest update = Assert.IsType<GitHubGistFileUpdateRequest>(request.Files["sample.txt"]);
        Assert.Equal("renamed.txt", update.Filename);
        Assert.Null(update.Content);
    }

    [Fact]
    public async Task StopAsync_CancelsAndDrainsInFlightSynchronization()
    {
        BlockingGistQueryService queryService = new(blockSynchronization: true);
        GistsPageViewModel viewModel = CreateViewModel(queryService);

        await viewModel.InitializeAsync();
        await queryService.SynchronizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await viewModel.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(queryService.SynchronizationCancellationObserved);
        Assert.Equal(1, queryService.DrainCalls);
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.IsLoadingMore);
        Assert.False(viewModel.IsDetailLoading);
        Assert.False(viewModel.IsFullFileLoading);
    }

    [Fact]
    public async Task StopAndReinitialize_AfterRapidSearchReplacement_DoesNotUseDisposedTokens()
    {
        BlockingGistQueryService queryService = new(blockSynchronization: false);
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        await viewModel.InitializeAsync();

        viewModel.SearchText = "a";
        viewModel.SearchText = "ab";
        viewModel.SearchText = "abc";

        await viewModel.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.InitializeAsync();
        await viewModel.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, queryService.DrainCalls);
    }

    [Fact]
    public void EditorSession_PreservesMultilineContentInCreateRequest()
    {
        GistEditorSession session = GistEditorSession.CreateNew();
        session.SelectedFile!.Filename = "sample.cs";
        session.SelectedFile.Content = "line one\r\nline two\r\nline three";

        GitHubGistCreateRequest request = session.BuildCreateRequest();

        Assert.True(session.CanSave);
        Assert.Equal("line one\r\nline two\r\nline three", request.Files["sample.cs"].Content);
    }

    [Fact]
    public void EditorSession_AddAndRemovePreserveDistinctDisplayedDraftFields()
    {
        GistEditorSession session = GistEditorSession.CreateNew();
        GistEditorFileDraft first = Assert.IsType<GistEditorFileDraft>(session.SelectedFile);
        session.CommitDisplayedFile(first, "first.cs", "first content", commitContent: true);

        session.AddFile();
        GistEditorFileDraft second = Assert.IsType<GistEditorFileDraft>(session.SelectedFile);
        session.CommitDisplayedFile(second, "second.md", "second content", commitContent: true);

        session.AddFile();
        session.RemoveSelectedFile();

        Assert.Collection(
            session.Files,
            file =>
            {
                Assert.Equal("first.cs", file.Filename);
                Assert.Equal("first content", file.Content);
            },
            file =>
            {
                Assert.Equal("second.md", file.Filename);
                Assert.Equal("second content", file.Content);
            });
        GitHubGistCreateRequest request = session.BuildCreateRequest();
        Assert.Equal("first content", request.Files["first.cs"].Content);
        Assert.Equal("second content", request.Files["second.md"].Content);
    }

    [Fact]
    public async Task Initialize_RestoresCompleteCachedLibraryBeforeBackgroundSynchronization()
    {
        GitHubGist first = CreateGist("one", "Cached one");
        GitHubGist second = CreateGist("two", "Cached two");
        BlockingGistQueryService queryService = new(blockSynchronization: true)
        {
            CachedLibrary = new GistCachedLibrarySnapshot([first, second], 2, true, CacheState.Stale)
        };
        GistsPageViewModel viewModel = CreateViewModel(queryService);

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.Gists.Count);
        Assert.Contains(viewModel.Gists, static gist => gist.StableKey == "one");
        Assert.Contains(viewModel.Gists, static gist => gist.StableKey == "two");
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task SelectedDetail_AppliesStaleValueThenGenerationGuardedFreshValue()
    {
        GitHubGist listItem = CreateGist("one", "List value");
        BlockingGistQueryService queryService = new(blockSynchronization: true)
        {
            CachedLibrary = new GistCachedLibrarySnapshot([listItem], 1, true, CacheState.Stale),
            CachedDetail = CreateGist("one", "Cached detail"),
            FreshDetail = CreateGist("one", "Fresh detail")
        };
        GistsPageViewModel viewModel = CreateViewModel(queryService);

        await viewModel.InitializeAsync();
        await queryService.DetailRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Cached detail", viewModel.SelectedDescription);

        queryService.ReleaseDetailRefresh.TrySetResult();
        await WaitUntilAsync(() => viewModel.SelectedDescription == "Fresh detail");

        Assert.Equal("Fresh detail", viewModel.SelectedDescription);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task AuthoritativeReconciliation_RemovesMissingSelectionAndSelectsExistingRow()
    {
        GitHubGist removed = CreateGist("removed", "Removed");
        GitHubGist remaining = CreateGist("remaining", "Remaining");
        BlockingGistQueryService queryService = new(blockSynchronization: false)
        {
            CachedLibrary = new GistCachedLibrarySnapshot([removed], 1, true, CacheState.Stale),
            SynchronizationItems = [remaining]
        };
        GistsPageViewModel viewModel = CreateViewModel(queryService);

        await viewModel.InitializeAsync();
        await queryService.SynchronizationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.SelectedGistItem?.StableKey == "remaining");

        Assert.Single(viewModel.Gists);
        Assert.Equal("remaining", viewModel.SelectedGistItem?.StableKey);
        Assert.DoesNotContain(viewModel.Gists, static gist => gist.StableKey == "removed");
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task StopAsync_CancelsAndDrainsOwnedMutation()
    {
        BlockingGistQueryService queryService = new(blockSynchronization: false, blockMutation: true);
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        await viewModel.InitializeAsync();
        GistEditorSession session = viewModel.CreateNewEditorSession();
        session.SelectedFile!.Content = "draft";

        Task<bool> mutation = viewModel.CreateGistAsync(session);
        await queryService.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await viewModel.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(await mutation);
        Assert.True(queryService.MutationCancellationObserved);
        Assert.False(viewModel.IsMutating);
    }

    [Fact]
    public async Task RemoteMutationSuccess_WithDegradedDurability_ClosesSuccessfullyAndShowsSeparateWarning()
    {
        BlockingGistQueryService queryService = new(blockSynchronization: true)
        {
            MutationDurability = GistMutationDurability.Degraded
        };
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        await viewModel.InitializeAsync();
        GistEditorSession session = viewModel.CreateNewEditorSession();
        session.SelectedFile!.Content = "saved remotely";

        bool succeeded = await viewModel.CreateGistAsync(session);

        Assert.True(succeeded);
        Assert.False(viewModel.IsErrorVisible);
        Assert.True(viewModel.IsDurabilityWarningVisible);
        Assert.Contains("GitHub completed the change", viewModel.DurabilityWarningMessage, StringComparison.Ordinal);
        Assert.Contains(viewModel.Gists, static gist => gist.StableKey == "created");
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task RemoteCreateSuccess_WhenProjectionFails_RemainsCommittedAndRetrySafe()
    {
        BlockingGistQueryService queryService = new(blockSynchronization: true);
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        await viewModel.InitializeAsync();
        InjectSingleProjectionFailure(viewModel);
        GistEditorSession session = viewModel.CreateNewEditorSession();
        session.SelectedFile!.Content = "saved remotely";

        bool succeeded = await viewModel.CreateGistAsync(session);

        Assert.True(succeeded);
        Assert.Equal(1, queryService.CreateCalls);
        Assert.False(viewModel.IsErrorVisible);
        Assert.True(viewModel.IsDurabilityWarningVisible);
        Assert.Contains("GitHub completed the change", viewModel.DurabilityWarningMessage, StringComparison.Ordinal);
        Assert.Contains("reconcile", viewModel.DurabilityWarningMessage, StringComparison.OrdinalIgnoreCase);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task RemoteCreateSuccess_WhenCallerCancelsAtResponseBoundary_RemainsCommitted()
    {
        using CancellationTokenSource cancellation = new();
        BlockingGistQueryService queryService = new(blockSynchronization: true)
        {
            AfterRemoteSuccess = cancellation.Cancel
        };
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        await viewModel.InitializeAsync();
        GistEditorSession session = viewModel.CreateNewEditorSession();
        session.SelectedFile!.Content = "saved remotely";

        bool succeeded = await viewModel.CreateGistAsync(session, cancellation.Token);

        Assert.True(succeeded);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, queryService.CreateCalls);
        Assert.False(viewModel.IsErrorVisible);
        Assert.Contains(viewModel.Gists, static gist => gist.StableKey == "created");
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task RemoteCreateSuccess_WhenTelemetryThrows_RemainsCommittedAndRetrySafe()
    {
        BlockingGistQueryService queryService = new(blockSynchronization: true);
        GistsPageViewModel viewModel = CreateViewModel(
            queryService,
            new TestAuthService(),
            new ThrowingActionTelemetryService());
        await viewModel.InitializeAsync();
        GistEditorSession session = viewModel.CreateNewEditorSession();
        session.SelectedFile!.Content = "saved remotely";

        bool succeeded = await viewModel.CreateGistAsync(session);

        Assert.True(succeeded);
        Assert.Equal(1, queryService.CreateCalls);
        Assert.False(viewModel.IsErrorVisible);
        Assert.Contains(viewModel.Gists, static gist => gist.StableKey == "created");
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task RemoteCreateSuccess_WhenBackgroundReconciliationFails_RemainsCommittedWithWarning()
    {
        BlockingGistQueryService queryService = new(blockSynchronization: true);
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        await viewModel.InitializeAsync();
        queryService.FailSynchronization = true;
        GistEditorSession session = viewModel.CreateNewEditorSession();
        session.SelectedFile!.Content = "saved remotely";

        bool succeeded = await viewModel.CreateGistAsync(session);
        await queryService.SynchronizationFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => viewModel.IsDurabilityWarningVisible);

        Assert.True(succeeded);
        Assert.Equal(1, queryService.CreateCalls);
        Assert.False(viewModel.IsErrorVisible);
        Assert.Contains("GitHub completed the change", viewModel.DurabilityWarningMessage, StringComparison.Ordinal);
        Assert.Contains("reconcile", viewModel.DurabilityWarningMessage, StringComparison.OrdinalIgnoreCase);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task RemoteUpdateSuccess_WhenProjectionFails_RemainsCommittedAndRetrySafe()
    {
        GitHubGist gist = CreateGist("one", "Before");
        BlockingGistQueryService queryService = new(blockSynchronization: true)
        {
            CachedLibrary = new GistCachedLibrarySnapshot([gist], 1, true, CacheState.Fresh)
        };
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedGistItem?.StableKey == "one");
        GistEditorSession session = viewModel.CreateEditEditorSession()!;
        session.Description = "After";
        InjectSingleProjectionFailure(viewModel);

        bool succeeded = await viewModel.UpdateSelectedGistAsync(session);

        Assert.True(succeeded);
        Assert.Equal(1, queryService.UpdateCalls);
        Assert.False(viewModel.IsErrorVisible);
        Assert.True(viewModel.IsDurabilityWarningVisible);
        Assert.Equal("Updated remotely", viewModel.SelectedGistItem?.Gist.Description);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task RemoteDeleteSuccess_WhenProjectionFails_ClearsStaleErrorAndRemainsCommitted()
    {
        GitHubGist gist = CreateGist("one", "Delete me");
        BlockingGistQueryService queryService = new(blockSynchronization: true)
        {
            CachedLibrary = new GistCachedLibrarySnapshot([gist], 1, true, CacheState.Fresh)
        };
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedGistItem?.StableKey == "one");
        viewModel.ReportActionError("A previous delete failed.", "delete");
        InjectSingleProjectionFailure(viewModel);

        bool succeeded = await viewModel.DeleteSelectedGistAsync();

        Assert.True(succeeded);
        Assert.Equal(1, queryService.DeleteCalls);
        Assert.False(viewModel.IsErrorVisible);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        Assert.True(viewModel.IsDurabilityWarningVisible);
        Assert.Null(viewModel.SelectedGistItem);
        await viewModel.StopAsync();
    }

    [Theory]
    [InlineData(GistMutationDurability.Durable, false)]
    [InlineData(GistMutationDurability.Degraded, true)]
    public async Task RemoteDeleteSuccess_ClearsStaleErrorBeforeSuccessWarning(
        GistMutationDurability durability,
        bool expectsWarning)
    {
        GitHubGist gist = CreateGist("one", "Delete me");
        BlockingGistQueryService queryService = new(blockSynchronization: true)
        {
            CachedLibrary = new GistCachedLibrarySnapshot([gist], 1, true, CacheState.Fresh),
            MutationDurability = durability
        };
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedGistItem?.StableKey == "one");
        viewModel.ReportActionError("A previous delete failed.", "delete");

        bool succeeded = await viewModel.DeleteSelectedGistAsync();

        Assert.True(succeeded);
        Assert.Equal(1, queryService.DeleteCalls);
        Assert.False(viewModel.IsErrorVisible);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        Assert.Equal(expectsWarning, viewModel.IsDurabilityWarningVisible);
        if (expectsWarning)
        {
            Assert.Contains("GitHub completed the change", viewModel.DurabilityWarningMessage, StringComparison.Ordinal);
        }

        Assert.Null(viewModel.SelectedGistItem);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task FullFile_AppliesCachedContentThenVisibleFreshRefreshWithoutBlanking()
    {
        GitHubGist gist = CreateGist("one", "Large file", truncated: true);
        BlockingGistQueryService queryService = new(blockSynchronization: true)
        {
            CachedLibrary = new GistCachedLibrarySnapshot([gist], 1, true, CacheState.Stale),
            CachedDetail = gist,
            FreshDetail = gist,
            CachedRawContent = "cached full content",
            FreshRawContent = new string('z', GistFileRenderPolicy.MaximumPreviewCharacters + 64)
        };
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.CanLoadFullFile);

        Task load = viewModel.LoadFullFileCommand.ExecuteAsync(null);
        await queryService.RawRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("cached full content", viewModel.SelectedFileContent);

        queryService.ReleaseRawRefresh.TrySetResult();
        await load.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(viewModel.IsSelectedFilePreviewCapped);
        Assert.Equal(GistFileRenderPolicy.MaximumPreviewCharacters, viewModel.SelectedFileContent.Length);
        Assert.Equal(queryService.FreshRawContent, viewModel.SelectedFile!.File.Content);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task AccountChange_AfterStopDestructivelyClearsPreviousAccountBeforeOfflineLoad()
    {
        TestAuthService authService = new();
        PartitionedOfflineGistQueryService queryService = new();
        queryService.CachedLibraries["42"] = new GistCachedLibrarySnapshot(
            [CreateGist("account-a", "Account A")],
            1,
            true,
            CacheState.Fresh);
        queryService.CachedLibraries["84"] = new GistCachedLibrarySnapshot([], 0, false, CacheState.Miss);
        GistsPageViewModel viewModel = CreateViewModel(queryService, authService);

        await viewModel.InitializeAsync();
        Assert.Equal("42", viewModel.ActiveAccountPartition);
        Assert.Equal("account-a", Assert.Single(viewModel.Gists).StableKey);
        Assert.NotEmpty(viewModel.Files);
        await viewModel.StopAsync();

        authService.AuthenticatedUser = new GitHubUser { Id = 84, Login = "second-viewer" };
        await viewModel.InitializeAsync();

        Assert.Equal("84", viewModel.ActiveAccountPartition);
        Assert.Empty(viewModel.Gists);
        Assert.Empty(viewModel.Files);
        Assert.Null(viewModel.SelectedGistItem);
        Assert.Null(viewModel.SelectedFile);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task AccountChange_ToSignedOutDestructivelyClearsPreviousAccountState()
    {
        TestAuthService authService = new();
        PartitionedOfflineGistQueryService queryService = new();
        queryService.CachedLibraries["42"] = new GistCachedLibrarySnapshot(
            [CreateGist("account-a", "Account A")],
            1,
            true,
            CacheState.Fresh);
        GistsPageViewModel viewModel = CreateViewModel(queryService, authService);

        await viewModel.InitializeAsync();
        Assert.NotEmpty(viewModel.Gists);
        Assert.NotEmpty(viewModel.Files);
        await viewModel.StopAsync();

        authService.AuthenticatedUser = null;
        authService.ReturnToken = false;
        await viewModel.InitializeAsync();

        Assert.Null(viewModel.ActiveAccountPartition);
        Assert.Empty(viewModel.Gists);
        Assert.Empty(viewModel.Files);
        Assert.Null(viewModel.SelectedGistItem);
        Assert.Null(viewModel.SelectedFile);
        Assert.True(viewModel.IsErrorVisible);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task LargeLibrary_UsesBudgetedProjectionAndPreservesKeyedSelectionAcrossReorder()
    {
        GitHubGist[] cached = Enumerable.Range(1, 5001)
            .Select(index => CreateGist($"gist-{index:D5}", $"Gist {5002 - index:D5}"))
            .ToArray();
        BlockingGistQueryService queryService = new(blockSynchronization: true)
        {
            CachedLibrary = new GistCachedLibrarySnapshot(cached, 51, true, CacheState.Fresh)
        };
        GistsPageViewModel viewModel = CreateViewModel(queryService);
        Stopwatch stopwatch = Stopwatch.StartNew();

        await viewModel.InitializeAsync();

        stopwatch.Stop();
        Assert.Equal(5001, viewModel.Gists.Count);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Projection took {stopwatch.Elapsed}.");
        Assert.True(viewModel.LastProjectionApplyStatistics.YieldCount > 0);
        Assert.InRange(
            viewModel.LastProjectionApplyStatistics.MaximumOperationsInSlice,
            1,
            GistProjectionApplyPolicy.MaximumOperationsPerSlice);

        GistViewItem selected = viewModel.Gists[2500];
        viewModel.SelectedGistItem = selected;
        viewModel.SetSort(GistLibrarySort.Title);
        await viewModel.WaitForProjectionAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(selected, viewModel.SelectedGistItem);
        Assert.Contains(viewModel.Gists, item => ReferenceEquals(item, selected));
        Assert.Equal(5001, viewModel.Gists.Count);
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task Telemetry_RecordsSanitizedDurationsForCachedContentReconciliationDetailAndMutation()
    {
        GitHubGist gist = CreateGist("one", "Cached");
        BlockingGistQueryService queryService = new(blockSynchronization: false)
        {
            CachedLibrary = new GistCachedLibrarySnapshot([gist], 1, true, CacheState.Fresh),
            SynchronizationItems = [gist],
            CachedDetail = gist,
            FreshDetail = gist
        };
        queryService.ReleaseDetailRefresh.TrySetResult();
        RecordingTelemetryService telemetry = new();
        GistsPageViewModel viewModel = CreateViewModel(queryService, new TestAuthService(), telemetry);

        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => telemetry.HasTimedEvent("gists.list.loaded", "phase", "reconciliation"));
        await WaitUntilAsync(() => telemetry.HasTimedEvent("gists.action.executed", "action", "detail_selection"));
        GistEditorSession session = viewModel.CreateNewEditorSession();
        session.SelectedFile!.Content = "timed mutation";
        Assert.True(await viewModel.CreateGistAsync(session));

        Assert.True(telemetry.HasTimedEvent("gists.list.loaded", "phase", "cached_first"));
        Assert.True(telemetry.HasTimedEvent("gists.list.loaded", "phase", "reconciliation"));
        Assert.True(telemetry.HasTimedEvent("gists.action.executed", "action", "detail_selection"));
        Assert.True(telemetry.HasTimedEvent("gists.action.executed", "action", "create"));
        Assert.All(
            telemetry.Events.Where(static entry => entry.Properties.ContainsKey("duration_bucket")),
            static entry => Assert.Matches("^(lt_|gte_)", entry.Properties["duration_bucket"]!));
        await viewModel.StopAsync();
    }

    [Fact]
    public async Task ThrowingTelemetry_DoesNotTurnSuccessfulInitializationIntoLoadFailure()
    {
        GitHubGist gist = CreateGist("safe-boundary", "Cached");
        BlockingGistQueryService queryService = new(blockSynchronization: false)
        {
            CachedLibrary = new GistCachedLibrarySnapshot([gist], 1, true, CacheState.Fresh),
            SynchronizationItems = [gist],
            CachedDetail = gist,
            FreshDetail = gist
        };
        queryService.ReleaseDetailRefresh.TrySetResult();
        GistsPageViewModel viewModel = CreateViewModel(
            queryService,
            new TestAuthService(),
            new ThrowingTelemetryService());

        await viewModel.InitializeAsync();

        Assert.Single(viewModel.Gists);
        Assert.False(viewModel.IsErrorVisible);
        await viewModel.StopAsync();
    }

    [Fact]
    public void CopyFileTelemetry_UsesDedicatedActionIdentity()
    {
        RecordingTelemetryService telemetry = new();
        GistsPageViewModel viewModel = CreateViewModel(
            new BlockingGistQueryService(blockSynchronization: false),
            new TestAuthService(),
            telemetry);

        viewModel.TrackCopyFileSuccess();

        TelemetryEntry entry = Assert.Single(telemetry.Events);
        Assert.Equal("gists.action.executed", entry.Name);
        Assert.Equal("copy_file", entry.Properties["action"]);
        Assert.Equal("success", entry.Properties["result"]);
    }

    private static GitHubGist CreateGist(string id, string description, bool truncated = false) => new()
    {
        Id = id,
        Description = description,
        Public = true,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        UpdatedAt = DateTimeOffset.UtcNow,
        Files = new Dictionary<string, GitHubGistFile>
        {
            ["sample.txt"] = new()
            {
                Filename = "sample.txt",
                Content = truncated ? "partial" : "content",
                Truncated = truncated,
                RawUrl = "https://gist.githubusercontent.com/octo/id/raw/revision/sample.txt"
            }
        }
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void InjectSingleProjectionFailure(GistsPageViewModel viewModel)
    {
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            viewModel.VisibleProjectionApplying -= handler;
            throw new InvalidOperationException("Injected post-commit projection failure.");
        };
        viewModel.VisibleProjectionApplying += handler;
    }

    private static GistsPageViewModel CreateViewModel(IGitHubGistQueryService queryService) =>
        CreateViewModel(queryService, new TestAuthService());

    private static GistsPageViewModel CreateViewModel(
        IGitHubGistQueryService queryService,
        TestAuthService authService,
        ITelemetryService? telemetry = null) =>
        new(queryService, authService, new TestAccountService(), telemetry ?? new NoopTelemetryService());

    private sealed class PartitionedOfflineGistQueryService : IGitHubGistQueryService
    {
        public Dictionary<string, GistCachedLibrarySnapshot> CachedLibraries { get; } = new(StringComparer.Ordinal);

        public Task<GistCachedLibrarySnapshot> GetCachedLibraryAsync(
            string accessToken,
            string userId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CachedLibraries.TryGetValue(userId, out GistCachedLibrarySnapshot? snapshot)
                ? snapshot
                : new GistCachedLibrarySnapshot([], 0, false, CacheState.Miss));

        public Task<CachedResult<GitHubGist[]>> GetPageAsync(
            string accessToken,
            string userId,
            int page,
            int pageSize,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority priority = GitHubRequestPriority.Visible,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CachedResult<GitHubGist[]>>(new System.Net.Http.HttpRequestException("offline"));

        public Task<CachedResult<GitHubGist>> GetDetailAsync(
            string accessToken,
            string userId,
            string gistId,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority priority = GitHubRequestPriority.Visible,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CachedResult<GitHubGist>>(new System.Net.Http.HttpRequestException("offline"));

        public Task<CachedResult<string>> GetRawFileAsync(
            string userId,
            string rawUrl,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority priority = GitHubRequestPriority.Visible,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CachedResult<string>>(new System.Net.Http.HttpRequestException("offline"));

        public Task<string> GetRawFileContentAsync(string userId, string rawUrl, CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new System.Net.Http.HttpRequestException("offline"));

        public Task DrainBackgroundWorkAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GistMutationResult<GitHubGist>> CreateAsync(string accessToken, string userId, GitHubGistCreateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GistMutationResult<GitHubGist>> UpdateAsync(string accessToken, string userId, string gistId, GitHubGistUpdateRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GistMutationResult<bool>> DeleteAsync(string accessToken, string userId, string gistId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingGistQueryService(bool blockSynchronization, bool blockMutation = false) : IGitHubGistQueryService
    {
        public TaskCompletionSource SynchronizationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool SynchronizationCancellationObserved { get; private set; }

        public TaskCompletionSource SynchronizationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SynchronizationFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource DetailRefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDetailRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource RawRefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRawRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource MutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool MutationCancellationObserved { get; private set; }

        public GistCachedLibrarySnapshot CachedLibrary { get; set; } = new([], 0, false, CacheState.Miss);

        public GitHubGist[] SynchronizationItems { get; set; } = [];

        public bool FailSynchronization { get; set; }

        public GitHubGist? CachedDetail { get; set; }

        public GitHubGist? FreshDetail { get; set; }

        public string CachedRawContent { get; set; } = "cached";

        public string FreshRawContent { get; set; } = "fresh";

        public int DrainCalls { get; private set; }

        public GistMutationDurability MutationDurability { get; set; } = GistMutationDurability.Durable;

        public Action? AfterRemoteSuccess { get; set; }

        public int CreateCalls { get; private set; }

        public int UpdateCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public Task<GistCachedLibrarySnapshot> GetCachedLibraryAsync(
            string accessToken,
            string userId,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CachedLibrary);

        public async Task<CachedResult<GitHubGist[]>> GetPageAsync(
            string accessToken,
            string userId,
            int page,
            int pageSize,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority priority = GitHubRequestPriority.Visible,
            CancellationToken cancellationToken = default)
        {
            if (fetchPolicy == QueryFetchPolicy.NetworkOnly && FailSynchronization)
            {
                SynchronizationFailed.TrySetResult();
                throw new InvalidOperationException("Injected reconciliation failure.");
            }

            if (fetchPolicy == QueryFetchPolicy.NetworkOnly && blockSynchronization)
            {
                SynchronizationStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    SynchronizationCancellationObserved = true;
                    throw;
                }
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (fetchPolicy == QueryFetchPolicy.NetworkOnly)
            {
                SynchronizationCompleted.TrySetResult();
            }

            return new CachedResult<GitHubGist[]>(SynchronizationItems, CacheState.Fresh, now, now.AddMinutes(5));
        }

        public Task<CachedResult<GitHubGist>> GetDetailAsync(
            string accessToken,
            string userId,
            string gistId,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority priority = GitHubRequestPriority.Visible,
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GitHubGist fallback = Array.Find(
                CachedLibrary.Items,
                gist => string.Equals(gist.Id, gistId, StringComparison.Ordinal)) ??
                CreateGist(gistId, "Detail");
            if (fetchPolicy != QueryFetchPolicy.NetworkOnly)
            {
                GitHubGist cached = CachedDetail ?? fallback;
                return Task.FromResult(new CachedResult<GitHubGist>(
                    cached,
                    CacheState.Stale,
                    now.AddMinutes(-10),
                    now.AddMinutes(-1),
                    IsRefreshInProgress: true));
            }

            return RefreshDetailAsync(cancellationToken);
        }

        public Task<CachedResult<string>> GetRawFileAsync(
            string userId,
            string rawUrl,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst,
            GitHubRequestPriority priority = GitHubRequestPriority.Visible,
            CancellationToken cancellationToken = default)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (fetchPolicy != QueryFetchPolicy.NetworkOnly)
            {
                return Task.FromResult(new CachedResult<string>(
                    CachedRawContent,
                    CacheState.Stale,
                    now.AddMinutes(-10),
                    now.AddMinutes(-1),
                    IsRefreshInProgress: true));
            }

            return RefreshRawAsync(cancellationToken);
        }

        public Task<string> GetRawFileContentAsync(
            string userId,
            string rawUrl,
            CancellationToken cancellationToken = default) => Task.FromResult(CachedRawContent);

        public Task DrainBackgroundWorkAsync(CancellationToken cancellationToken = default)
        {
            DrainCalls++;
            return Task.CompletedTask;
        }

        public Task<GistMutationResult<GitHubGist>> CreateAsync(
            string accessToken,
            string userId,
            GitHubGistCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            if (blockMutation)
            {
                return BlockMutationAsync(cancellationToken);
            }

            GistMutationResult<GitHubGist> result = new(
                CreateGist("created", request.Description),
                MutationDurability);
            AfterRemoteSuccess?.Invoke();
            return Task.FromResult(result);
        }

        public Task<GistMutationResult<GitHubGist>> UpdateAsync(
            string accessToken,
            string userId,
            string gistId,
            GitHubGistUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            UpdateCalls++;
            GistMutationResult<GitHubGist> result = new(
                CreateGist(gistId, "Updated remotely"),
                MutationDurability);
            AfterRemoteSuccess?.Invoke();
            return Task.FromResult(result);
        }

        public Task<GistMutationResult<bool>> DeleteAsync(
            string accessToken,
            string userId,
            string gistId,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            if (blockMutation)
            {
                return BlockDeleteAsync(cancellationToken);
            }

            GistMutationResult<bool> result = new(true, MutationDurability);
            AfterRemoteSuccess?.Invoke();
            return Task.FromResult(result);
        }

        private async Task<CachedResult<GitHubGist>> RefreshDetailAsync(CancellationToken cancellationToken)
        {
            DetailRefreshStarted.TrySetResult();
            await ReleaseDetailRefresh.Task.WaitAsync(cancellationToken);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new CachedResult<GitHubGist>(FreshDetail ?? CachedDetail ?? CreateGist("detail", "Detail"), CacheState.Fresh, now, now.AddMinutes(5));
        }

        private async Task<CachedResult<string>> RefreshRawAsync(CancellationToken cancellationToken)
        {
            RawRefreshStarted.TrySetResult();
            await ReleaseRawRefresh.Task.WaitAsync(cancellationToken);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new CachedResult<string>(FreshRawContent, CacheState.Fresh, now, now.AddMinutes(5));
        }

        private async Task<GistMutationResult<GitHubGist>> BlockMutationAsync(CancellationToken cancellationToken)
        {
            MutationStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new GistMutationResult<GitHubGist>(
                    CreateGist("created", "Created"),
                    GistMutationDurability.Durable);
            }
            catch (OperationCanceledException)
            {
                MutationCancellationObserved = true;
                throw;
            }
        }

        private async Task<GistMutationResult<bool>> BlockDeleteAsync(CancellationToken cancellationToken)
        {
            await BlockMutationAsync(cancellationToken);
            return new GistMutationResult<bool>(true, GistMutationDurability.Durable);
        }
    }

    private sealed class TestAuthService : IAuthService
    {
        public bool Authenticated { get; set; } = true;

        public bool ReturnToken { get; set; } = true;

        public GitHubUser? AuthenticatedUser { get; set; } = new() { Id = 42, Login = "viewer" };

        public AuthSessionRecoveryState RecoveryState => AuthSessionRecoveryState.None;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task Authenticate() => Task.CompletedTask;

        public Task<bool> EnsureScopesAsync(params string[] scopes) => Task.FromResult(true);

        public Task<bool> Authorize(string response) => Task.FromResult(true);

        public Task<GitHubUser?> RefreshAuthenticatedUserAsync() => Task.FromResult(AuthenticatedUser);

        public string? GetToken(long userId) => ReturnToken ? "token" : null;

        public bool CheckAuth(long userId) => true;

        public void SignOut()
        {
        }
    }

    private sealed class TestAccountService : IAccountService
    {
        public void RemoveUser()
        {
        }

        public void SaveUser(long userId)
        {
        }

        public long GetUser() => 42;
    }

    private sealed class RecordingTelemetryService : ITelemetryService
    {
        private readonly ConcurrentQueue<TelemetryEntry> _events = new();

        public TelemetryEntry[] Events => _events.ToArray();

        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            _events.Enqueue(new TelemetryEntry(
                name,
                new Dictionary<string, string?>(properties ?? new Dictionary<string, string?>(), StringComparer.Ordinal)));

        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            new NoopTrace();

        public bool HasTimedEvent(string name, string property, string value) =>
            _events.Any(entry =>
                string.Equals(entry.Name, name, StringComparison.Ordinal) &&
                entry.Properties.TryGetValue(property, out string? actual) &&
                string.Equals(actual, value, StringComparison.Ordinal) &&
                entry.Properties.TryGetValue("duration_bucket", out string? duration) &&
                !string.IsNullOrWhiteSpace(duration));
    }

    private sealed record TelemetryEntry(string Name, IReadOnlyDictionary<string, string?> Properties);

    private sealed class NoopTelemetryService : ITelemetryService
    {
        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            new NoopTrace();
    }

    private sealed class ThrowingActionTelemetryService : ITelemetryService
    {
        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
        {
            if (string.Equals(name, "gists.action.executed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected telemetry sink failure.");
            }
        }

        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null)
        {
        }

        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            new NoopTrace();
    }

    private sealed class NoopTrace : IPerformanceTrace
    {
        public void Dispose()
        {
        }

        public void SetProperty(string key, string? value)
        {
        }
    }
}

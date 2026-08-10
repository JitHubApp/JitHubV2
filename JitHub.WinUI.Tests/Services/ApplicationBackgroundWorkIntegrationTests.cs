using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ApplicationBackgroundWorkIntegrationTests
{
    [Fact]
    public void Activation_IsSerializedTrackedCancelableAndFailureObserved()
    {
        string source = Read("JitHub.WinUI", "App.xaml.cs");

        Assert.Contains("ApplicationActivationGate _activationGate", source, StringComparison.Ordinal);
        Assert.Contains("new ApplicationTaskOptions(\"app.activation\")", source, StringComparison.Ordinal);
        Assert.Contains("HandleActivationAsync(activationRequest, innerToken)", source, StringComparison.Ordinal);
        Assert.Contains("ResumePendingAccountRemovalAsync(cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", source, StringComparison.Ordinal);
        Assert.Contains("throw;", ExtractMethod(source, "private async Task HandleActivationAsync", "private async Task ActivateCoreAsync"), StringComparison.Ordinal);
        Assert.DoesNotContain("_ = HandleActivationAsync(activationRequest)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellInitializationAndCommandSearch_ArePageLifetimeAndAccountCoordinated()
    {
        string source = Read("JitHub.WinUI", "Views", "Pages", "ShellPage.xaml.cs");

        Assert.Contains("QueueShellWork(\"shell.initialize\", InitializeShellAsync)", source, StringComparison.Ordinal);
        Assert.Contains("\"shell.command_search\"", source, StringComparison.Ordinal);
        Assert.Contains("new ApplicationTaskOptions(taskName, GetActiveAccountPartition())", source, StringComparison.Ordinal);
        Assert.Contains("lifetime.Token", source, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = InitializeShellAsync()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RefreshSearchSuggestionsAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DelayedCommitPrefetch_IsSelectionCancelableAccountWork()
    {
        string source = Read("JitHub.WinUI", "ViewModels", "Pages", "RepoCommitsPageViewModel.cs");
        string schedule = ExtractMethod(
            source,
            "private IDisposable ScheduleTrackedPrefetch",
            "private async Task RunScheduledTrackedPrefetchAsync");

        Assert.Contains("_taskCoordinator.RunAsync", schedule, StringComparison.Ordinal);
        Assert.Contains("new ApplicationTaskOptions(\"commits.page_prefetch\", userPartition)", schedule, StringComparison.Ordinal);
        Assert.Contains("cancellation.Token", schedule, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RunScheduledTrackedPrefetchAsync", schedule, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RunTrackedPrefetchAsync", source, StringComparison.Ordinal);
        Assert.Contains("_ = QueueTrackedPrefetch(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StarsProjectionRefreshes_AreAccountCoordinatedAndHaveNoDetachedTaskRun()
    {
        string source = Read("JitHub.WinUI", "ViewModels", "Pages", "StarLibraryPageViewModel.cs");

        Assert.Contains("ScheduleProjectionRefresh(StarProjectionRefresh.SyncStatus)", source, StringComparison.Ordinal);
        Assert.Contains("ScheduleAccountTask(", source, StringComparison.Ordinal);
        Assert.Contains("new ApplicationTaskOptions(taskName, _userId)", source, StringComparison.Ordinal);
        Assert.Contains("_pageLifetime.Token", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RefreshNavigationAsync()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = RefreshFromStoreAsync()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = UpdateSyncStatusAsync()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = Task.Run", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StarsCachedPage_LivesAcrossNavigationAndSnapshotProjectionIsSilent()
    {
        string pageSource = Read("JitHub.WinUI", "Views", "Pages", "StarsPage.xaml.cs");
        string viewModelSource = Read("JitHub.WinUI", "ViewModels", "Pages", "StarLibraryPageViewModel.cs");
        string projection = ExtractMethod(
            viewModelSource,
            "private void ApplyNavigationSnapshot",
            "private void ApplyPage");

        Assert.DoesNotContain("Unloaded +=", pageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.Dispose()", pageSource, StringComparison.Ordinal);
        Assert.Contains("NavigationCacheMode = NavigationCacheMode.Required", pageSource, StringComparison.Ordinal);
        Assert.Contains("await ViewModel.InitializeAsync()", pageSource, StringComparison.Ordinal);
        Assert.Contains("Task.Run(", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("ViewModelBase, IDisposable", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("_suppressQueryChanges = true", projection, StringComparison.Ordinal);
        Assert.Contains("_suppressQueryChanges = wasSuppressingQueryChanges", projection, StringComparison.Ordinal);
        Assert.Contains("SelectedLanguage = ReplaceOptions", projection, StringComparison.Ordinal);
        Assert.Contains("SelectedOwner = ReplaceOptions", projection, StringComparison.Ordinal);
        Assert.Contains("SelectedTopic = ReplaceOptions", projection, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundFailureDiagnostics_UseOnlyStableIdentifierFreeFields()
    {
        string source = Read("JitHub.WinUI", "App.xaml.cs");
        string method = ExtractMethod(
            source,
            "private static void RecordBackgroundTaskFailure",
            "internal void QueueDiagnosticsCloseProbeIfRequested");

        Assert.Contains("[\"feature\"] = failure.Name", method, StringComparison.Ordinal);
        Assert.Contains("[\"error_kind\"] = failure.Exception.GetBaseException().GetType().Name", method, StringComparison.Ordinal);
        Assert.Contains("[\"phase\"] = \"background\"", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception.Message", method, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", method, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountPartition", method, StringComparison.Ordinal);
    }

    private static string ExtractMethod(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        Assert.True(end > start, $"Missing source marker: {endMarker}");
        return source[start..end];
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. path]));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}

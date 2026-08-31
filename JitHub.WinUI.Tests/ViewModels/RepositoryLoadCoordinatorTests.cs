using System.Threading;
using System.Collections.Generic;
using JitHub.Models.GitHub;
using JitHub.WinUI.ViewModels.Common;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class RepositoryLoadCoordinatorTests
{
    [Fact]
    public void CompleteRepositoryRow_IsImmediatelyNavigableWithoutMetadataResolution()
    {
        GitHubRepository repository = new()
        {
            Id = 42,
            Name = "app",
            FullName = "octo/app",
            DefaultBranch = "main",
            Owner = new GitHubRepositoryOwner { Login = "octo" },
        };

        Assert.True(RepositoryNavigationMetadataPolicy.CanNavigateImmediately(repository));
    }

    [Fact]
    public void RepositoryRouteWithoutNumericId_IsImmediatelyNavigableWhileMetadataPromotes()
    {
        GitHubRepository repository = new()
        {
            Id = 0,
            Name = "app",
            FullName = "octo/app",
            DefaultBranch = "main",
            Owner = new GitHubRepositoryOwner { Login = "octo" },
        };

        Assert.True(RepositoryNavigationMetadataPolicy.CanNavigateImmediately(repository));
    }

    [Theory]
    [InlineData(42, "", "app", "main")]
    [InlineData(42, "octo", "", "main")]
    [InlineData(42, "octo", "app", "")]
    public void RepositoryRouteMissingContentIdentity_StillRequiresMetadataResolution(
        long id,
        string owner,
        string name,
        string defaultBranch)
    {
        GitHubRepository repository = new()
        {
            Id = id,
            Name = name,
            DefaultBranch = defaultBranch,
            Owner = new GitHubRepositoryOwner { Login = owner },
        };

        Assert.False(RepositoryNavigationMetadataPolicy.CanNavigateImmediately(repository));
    }

    [Fact]
    public void BeginNewRepository_ImmediatelyDisablesPreviousRepositoryActions()
    {
        RepositoryLoadCoordinator coordinator = new();
        long first = coordinator.Begin();
        Assert.True(coordinator.MarkRepositoryAvailable(first));
        Assert.True(coordinator.MarkStarStateKnown(first));
        Assert.True(coordinator.MarkWatchStateKnown(first));
        Assert.True(coordinator.Complete(first));
        Assert.True(coordinator.CanFork);
        Assert.True(coordinator.CanToggleStar);
        Assert.True(coordinator.CanToggleWatch);

        long second = coordinator.Begin();

        Assert.True(coordinator.IsLoading);
        Assert.False(coordinator.HasRepository);
        Assert.False(coordinator.IsStarStateKnown);
        Assert.False(coordinator.IsWatchStateKnown);
        Assert.False(coordinator.CanFork);
        Assert.False(coordinator.CanToggleStar);
        Assert.False(coordinator.CanToggleWatch);
        Assert.False(coordinator.MarkStarStateKnown(first));
        Assert.False(coordinator.MarkWatchStateKnown(first));
        Assert.False(coordinator.Complete(first));
        Assert.True(coordinator.IsCurrent(second));
    }

    [Fact]
    public void CurrentRepositoryActionsEnableOnlyAfterCurrentLoadCompletes()
    {
        RepositoryLoadCoordinator coordinator = new();
        long generation = coordinator.Begin();

        Assert.True(coordinator.MarkRepositoryAvailable(generation));
        Assert.True(coordinator.MarkStarStateKnown(generation));
        Assert.True(coordinator.MarkWatchStateKnown(generation));
        Assert.False(coordinator.CanFork);
        Assert.False(coordinator.CanToggleStar);
        Assert.False(coordinator.CanToggleWatch);

        Assert.True(coordinator.Complete(generation));

        Assert.True(coordinator.CanFork);
        Assert.True(coordinator.CanToggleStar);
        Assert.True(coordinator.CanToggleWatch);
    }

    [Fact]
    public void SameRepositoryReloadPreservesKnownSectionStateOnFailure()
    {
        RepositoryLoadCoordinator coordinator = new();
        long initial = coordinator.Begin();
        Assert.True(coordinator.MarkRepositoryAvailable(initial));
        Assert.True(coordinator.MarkBranchStateKnown(initial));
        Assert.True(coordinator.MarkStarStateKnown(initial));
        Assert.True(coordinator.MarkWatchStateKnown(initial));
        Assert.True(coordinator.Complete(initial));

        long reload = coordinator.Begin(preserveAvailableState: true);

        Assert.True(coordinator.HasRepository);
        Assert.Equal(RepositoryDataAvailability.Available, coordinator.BranchState);
        Assert.Equal(RepositoryDataAvailability.Available, coordinator.StarState);
        Assert.Equal(RepositoryDataAvailability.Available, coordinator.WatchState);
        Assert.True(coordinator.Complete(reload));
        Assert.True(coordinator.CanToggleStar);
        Assert.True(coordinator.CanToggleWatch);
    }

    [Fact]
    public void OptimisticAndRollbackDisplayPublishCountsBeforeSelectionAndDerivedProperties()
    {
        List<string> notifications = [];
        int count = 10;
        bool selected = false;

        RepositoryActionDisplayMutation.Publish(
            11,
            true,
            value => { count = value; notifications.Add($"count:{count}"); },
            value => { selected = value; notifications.Add($"selected:{selected}:count:{count}"); },
            () => notifications.Add($"derived:{selected}:count:{count}"));
        RepositoryActionDisplayMutation.Publish(
            10,
            false,
            value => { count = value; notifications.Add($"count:{count}"); },
            value => { selected = value; notifications.Add($"selected:{selected}:count:{count}"); },
            () => notifications.Add($"derived:{selected}:count:{count}"));

        Assert.Equal(
            [
                "count:11",
                "selected:True:count:11",
                "derived:True:count:11",
                "count:10",
                "selected:False:count:10",
                "derived:False:count:10"
            ],
            notifications);
    }

    [Fact]
    public void OptimisticWatchCount_ClampsStaleWatchingZeroSubscriberStateAtZero()
    {
        const bool staleWatchingState = true;
        const int staleSubscriberCount = 0;

        int optimisticCount = RepositoryActionDisplayMutation.CalculateOptimisticCount(
            staleSubscriberCount,
            desiredSelection: !staleWatchingState);

        Assert.Equal(0, optimisticCount);
    }

    [Fact]
    public void ThrowIfStale_RejectsCompletionFromPreviousRepository()
    {
        RepositoryLoadCoordinator coordinator = new();
        long first = coordinator.Begin();
        coordinator.Begin();

        Assert.Throws<OperationCanceledException>(() =>
            coordinator.ThrowIfStale(first, CancellationToken.None));
    }

    [Fact]
    public void PublishIfCurrent_NeverPublishesResolvedRepositoryFromStaleNavigation()
    {
        RepositoryLoadCoordinator coordinator = new();
        long stale = coordinator.Begin();
        coordinator.Begin();
        string? published = null;

        Assert.Throws<OperationCanceledException>(() =>
            coordinator.PublishIfCurrent(
                stale,
                "old/repository",
                value => published = value,
                CancellationToken.None));

        Assert.Null(published);
    }

    [Fact]
    public void FailedActionProbes_AreUnavailableInsteadOfKnownFalse()
    {
        RepositoryLoadCoordinator coordinator = new();
        long generation = coordinator.Begin();

        Assert.True(coordinator.MarkRepositoryAvailable(generation));
        Assert.True(coordinator.MarkBranchStateUnavailable(generation));
        Assert.True(coordinator.MarkStarStateUnavailable(generation));
        Assert.True(coordinator.MarkWatchStateUnavailable(generation));
        Assert.True(coordinator.Complete(generation));

        Assert.Equal(RepositoryDataAvailability.Unavailable, coordinator.BranchState);
        Assert.Equal(RepositoryDataAvailability.Unavailable, coordinator.StarState);
        Assert.Equal(RepositoryDataAvailability.Unavailable, coordinator.WatchState);
        Assert.False(coordinator.IsStarStateKnown);
        Assert.False(coordinator.IsWatchStateKnown);
        Assert.False(coordinator.CanToggleStar);
        Assert.False(coordinator.CanToggleWatch);
    }

    [Fact]
    public void PreviousOperationCompletion_CannotClearNewerLoadingOwner()
    {
        LatestOperationCoordinator coordinator = new();
        long fork = coordinator.Begin();
        long navigation = coordinator.Begin();

        Assert.False(coordinator.Complete(fork));
        Assert.True(coordinator.IsRunning);
        Assert.True(coordinator.Complete(navigation));
        Assert.False(coordinator.IsRunning);
    }

    [Fact]
    public void UnknownRepositoryStates_RenderExplicitLoadingAndUnavailableText()
    {
        Assert.Equal(
            "Loading star status",
            RepositoryActionPresentation.StarLabel(RepositoryDataAvailability.Loading, isStarred: false));
        Assert.Equal(
            "Star status unavailable",
            RepositoryActionPresentation.StarLabel(RepositoryDataAvailability.Unavailable, isStarred: false));
        Assert.Equal(
            "Watch status unavailable",
            RepositoryActionPresentation.WatchLabel(RepositoryDataAvailability.Unavailable, isWatching: false));
        Assert.Equal(
            "N/A",
            RepositoryActionPresentation.ValueText(RepositoryDataAvailability.Unavailable, 0));
        Assert.Equal(
            "Branches unavailable",
            RepositoryActionPresentation.BranchStatus(RepositoryDataAvailability.Unavailable, 0));
    }
}

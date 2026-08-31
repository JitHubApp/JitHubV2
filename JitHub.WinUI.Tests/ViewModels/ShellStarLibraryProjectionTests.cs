using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Pages;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class ShellStarLibraryProjectionTests
{
    [Fact]
    public async Task CanonicalItemsNotificationRefreshesShellCountFromLocalIndex()
    {
        IGitHubStarLibraryService library = Substitute.For<IGitHubStarLibraryService>();
        int indexedCount = 4;
        library.QueryAsync(Arg.Any<StarLibraryQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(CreatePage("42", indexedCount)));
        library.GetDegradedState("42").Returns(StarLibraryDegradedState.Healthy);
        using ShellStarLibraryProjection projection = new(library);
        ShellStarLibrarySnapshot initial = await WaitForSnapshotAsync(
            projection,
            () => projection.SetUserAsync("42"));
        Assert.Equal(4, initial.IndexedCount);

        indexedCount = 5;
        ShellStarLibrarySnapshot updated = await WaitForSnapshotAsync(
            projection,
            () =>
            {
                library.Changed += Raise.Event<EventHandler<StarLibraryChangedEventArgs>>(
                    library,
                    new StarLibraryChangedEventArgs("42", StarLibraryChangeKind.Items));
                return Task.CompletedTask;
            });

        Assert.Equal(5, updated.IndexedCount);
        Assert.Equal("42", updated.UserId);
        await library.Received(2).QueryAsync(
            Arg.Is<StarLibraryQuery>(query => query.UserId == "42" && query.Limit == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NotificationForAnotherAccountDoesNotRefreshActiveShellProjection()
    {
        IGitHubStarLibraryService library = Substitute.For<IGitHubStarLibraryService>();
        library.QueryAsync(Arg.Any<StarLibraryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreatePage("42", 3)));
        library.GetDegradedState(Arg.Any<string>()).Returns(StarLibraryDegradedState.Healthy);
        using ShellStarLibraryProjection projection = new(library);
        await projection.SetUserAsync("42");

        library.Changed += Raise.Event<EventHandler<StarLibraryChangedEventArgs>>(
            library,
            new StarLibraryChangedEventArgs("7", StarLibraryChangeKind.Items));
        await Task.Delay(100);

        await library.Received(1).QueryAsync(
            Arg.Any<StarLibraryQuery>(),
            Arg.Any<CancellationToken>());
    }

    private static async Task<ShellStarLibrarySnapshot> WaitForSnapshotAsync(
        ShellStarLibraryProjection projection,
        Func<Task> action)
    {
        TaskCompletionSource<ShellStarLibrarySnapshot> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<ShellStarLibrarySnapshot>? handler = null;
        handler = (_, snapshot) => completion.TrySetResult(snapshot);
        projection.Changed += handler;
        try
        {
            await action();
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            projection.Changed -= handler;
        }
    }

    private static StarLibraryPage CreatePage(string userId, int count) =>
        new(
            [],
            count,
            HasMore: count > 0,
            new StarSyncState(
                userId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                IsComplete: true,
                IsSyncing: false,
                IndexedCount: count,
                ErrorMessage: string.Empty));
}

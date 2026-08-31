using System;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ApplicationTaskCoordinatorTests
{
    [Fact]
    public async Task ActivationGate_SerializesConcurrentActivationsWithoutBlockingCaller()
    {
        ApplicationActivationGate gate = new();
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task first = gate.RunAsync(async token =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(token);
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task second = gate.RunAsync(_ =>
        {
            secondEntered.SetResult();
            return Task.CompletedTask;
        });

        await Task.Delay(30);
        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ActivationGate_CancelledWaiterDoesNotRunAndDoesNotBlockLaterActivation()
    {
        ApplicationActivationGate gate = new();
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = gate.RunAsync(token => releaseFirst.Task.WaitAsync(token));
        using CancellationTokenSource cancellation = new();
        bool canceledOperationRan = false;
        Task canceled = gate.RunAsync(
            _ =>
            {
                canceledOperationRan = true;
                return Task.CompletedTask;
            },
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.False(canceledOperationRan);

        releaseFirst.SetResult();
        await first;
        bool laterRan = false;
        await gate.RunAsync(_ =>
        {
            laterRan = true;
            return Task.CompletedTask;
        });
        Assert.True(laterRan);
    }

    [Theory]
    [InlineData("commits.page_prefetch")]
    [InlineData("stars.page_projection_refresh")]
    [InlineData("shell.command_search")]
    public async Task AccountCancellation_DrainsLifecycleProducer(string taskName)
    {
        using ApplicationTaskCoordinator coordinator = new();
        Task started = coordinator.RunAsync(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            new ApplicationTaskOptions(taskName, "42"));

        await coordinator.CancelAccountAsync("42").WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(started.IsCompletedSuccessfully);
        Assert.Equal(0, coordinator.ActiveTaskCount);
    }

    [Fact]
    public async Task Shutdown_DrainsActivationShellCommitAndStarsWork()
    {
        using ApplicationTaskCoordinator coordinator = new();
        string[] taskNames =
        [
            "app.activation",
            "shell.initialize",
            "commits.page_prefetch",
            "stars.page_projection_refresh"
        ];
        Task[] tasks = taskNames
            .Select(name => coordinator.RunAsync(
                token => Task.Delay(Timeout.InfiniteTimeSpan, token),
                new ApplicationTaskOptions(name)))
            .ToArray();

        ApplicationTaskShutdownResult result = await coordinator
            .ShutdownAsync(TimeSpan.FromSeconds(2))
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(result.Completed);
        Assert.Equal(0, result.PendingTaskCount);
        Assert.All(tasks, task => Assert.True(task.IsCompletedSuccessfully));
    }

    [Fact]
    public async Task RunAsync_ObservesFailureAndRemovesCompletedTask()
    {
        using ApplicationTaskCoordinator coordinator = new();
        ApplicationTaskFailure? failure = null;
        coordinator.TaskFailed += (_, value) => failure = value;

        await coordinator.RunAsync(
            _ => Task.FromException(new InvalidOperationException("failure")),
            new ApplicationTaskOptions("test.failure", "42"));

        Assert.NotNull(failure);
        Assert.Equal("test.failure", failure!.Name);
        Assert.Equal("42", failure.AccountPartition);
        Assert.Equal(0, coordinator.ActiveTaskCount);
    }

    [Fact]
    public async Task CancelAccountAsync_CancelsOnlyMatchingAccountWork()
    {
        using ApplicationTaskCoordinator coordinator = new();
        TaskCompletionSource otherRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = coordinator.RunAsync(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            new ApplicationTaskOptions("test.first", "42"));
        Task second = coordinator.RunAsync(
            async token => await otherRelease.Task.WaitAsync(token),
            new ApplicationTaskOptions("test.second", "43"));

        await coordinator.CancelAccountAsync("42");

        Assert.True(first.IsCompletedSuccessfully);
        Assert.False(second.IsCompleted);
        otherRelease.SetResult();
        await second;
    }

    [Fact]
    public async Task ActivateAccount_AllowsFreshWorkOnlyAfterCancelledGenerationDrains()
    {
        using ApplicationTaskCoordinator coordinator = new();
        Task running = coordinator.RunAsync(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            new ApplicationTaskOptions("test.removed", "42"));

        await coordinator.CancelAccountAsync("42");
        bool lateExecuted = false;
        Task late = coordinator.RunAsync(
            _ =>
            {
                lateExecuted = true;
                return Task.CompletedTask;
            },
            new ApplicationTaskOptions("test.late", "42"));

        Assert.True(running.IsCompletedSuccessfully);
        await late;
        Assert.False(lateExecuted);
        coordinator.ActivateAccount("42");

        bool executed = false;
        await coordinator.RunAsync(
            _ =>
            {
                executed = true;
                return Task.CompletedTask;
            },
            new ApplicationTaskOptions("test.reauthenticated", "42"));

        Assert.True(executed);
    }

    [Fact]
    public async Task AccountWorkActivation_ReopensCoordinatorGenerationAfterQuiescence()
    {
        using ApplicationTaskCoordinator coordinator = new();
        AccountWorkQuiescence accountWork = new(coordinator);

        await coordinator.CancelAccountAsync("42");
        await accountWork.QuiesceAsync("42");
        accountWork.Activate("42");

        bool executed = false;
        await coordinator.RunAsync(
            _ =>
            {
                executed = true;
                return Task.CompletedTask;
            },
            new ApplicationTaskOptions("test.session", "42"));

        Assert.True(executed);
    }

    [Fact]
    public async Task ShutdownAsync_CancelsTrackedWorkAndRejectsLateSubmissions()
    {
        using ApplicationTaskCoordinator coordinator = new();
        Task running = coordinator.RunAsync(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            new ApplicationTaskOptions("test.shutdown"));

        ApplicationTaskShutdownResult result = await coordinator.ShutdownAsync(TimeSpan.FromSeconds(2));
        Task late = coordinator.RunAsync(
            _ => Task.CompletedTask,
            new ApplicationTaskOptions("test.late"));

        Assert.True(result.Completed);
        Assert.Equal(0, result.PendingTaskCount);
        Assert.True(running.IsCompletedSuccessfully);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late);
    }

    [Fact]
    public async Task ShutdownAsync_ReportsPendingWorkThatIgnoresCancellation()
    {
        using ApplicationTaskCoordinator coordinator = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task running = coordinator.RunAsync(
            _ => release.Task,
            new ApplicationTaskOptions("test.non_cooperative"));

        ApplicationTaskShutdownResult result = await coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(20));

        Assert.False(result.Completed);
        Assert.Equal(1, result.PendingTaskCount);
        release.SetResult();
        await running;
    }

    [Fact]
    public async Task CancelAccountAsync_CannotMissOperationThatBlocksBeforeReturningTask()
    {
        using ApplicationTaskCoordinator coordinator = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        Task running = Task.Run(() => coordinator.RunAsync(
            _ =>
            {
                entered.Set();
                release.Wait();
                return Task.CompletedTask;
            },
            new ApplicationTaskOptions("test.synchronous_start", "42")));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        Task cancellation = coordinator.CancelAccountAsync("42");
        await Task.Delay(40);

        Assert.False(cancellation.IsCompleted);
        Assert.Equal(1, coordinator.ActiveTaskCount);

        release.Set();
        await Task.WhenAll(running, cancellation).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, coordinator.ActiveTaskCount);
    }

    [Fact]
    public async Task ShutdownAsync_TracksOperationBeforeSynchronousDelegateReturns()
    {
        using ApplicationTaskCoordinator coordinator = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        Task running = Task.Run(() => coordinator.RunAsync(
            _ =>
            {
                entered.Set();
                release.Wait();
                return Task.CompletedTask;
            },
            new ApplicationTaskOptions("test.synchronous_shutdown")));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        ApplicationTaskShutdownResult result = await coordinator.ShutdownAsync(TimeSpan.FromMilliseconds(40));

        Assert.False(result.Completed);
        Assert.Equal(1, result.PendingTaskCount);

        release.Set();
        await running.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, coordinator.ActiveTaskCount);
    }
}

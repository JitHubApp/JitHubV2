using System.Diagnostics;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class ImageResolverDeadlineTests
{
    [Fact]
    public async Task TimeoutCancelsResolverTokenAndStopsWaitingForIgnoringResolver()
    {
        var never = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken resolverToken = default;
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await ImageResolverDeadline.RunAsync(
                token =>
                {
                    resolverToken = token;
                    return new ValueTask<int>(never.Task);
                },
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None));

        stopwatch.Stop();
        Assert.True(resolverToken.IsCancellationRequested);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DocumentCancellationRemainsCancellationRatherThanTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ImageResolverDeadline.RunAsync(
                _ => ValueTask.FromResult(42),
                TimeSpan.FromSeconds(1),
                cancellation.Token));
    }

    [Fact]
    public async Task CompletedResolutionIsReturnedBeforeDeadline()
    {
        int result = await ImageResolverDeadline.RunAsync(
            _ => ValueTask.FromResult(42),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(42, result);
    }
}

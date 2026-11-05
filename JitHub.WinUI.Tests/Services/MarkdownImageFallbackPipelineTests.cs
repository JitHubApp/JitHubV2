using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using Xunit;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceHttpDoubles;
using static JitHub.WinUI.Tests.Services.GitHubImageServiceAdmissionDoubles;

namespace JitHub.WinUI.Tests.Services;

/// <summary>
/// Ordered and hedged fallback between independent image representations.
/// </summary>
public sealed class MarkdownImageFallbackPipelineTests
{
    [Fact]
    public async Task MarkdownImageFallback_TransportTimeoutContinuesToIndependentOrigin()
    {
        int attempts = 0;
        int reportedAttempt = -1;

        string? result = await MarkdownImageFallbackPipeline.FirstSuccessfulAsync(
            _ =>
            {
                attempts++;
                return Task.FromException<string?>(
                    new TaskCanceledException("Simulated HttpClient timeout."));
            },
            _ =>
            {
                attempts++;
                return Task.FromResult<string?>("canonical-origin");
            },
            (attempt, _) => reportedAttempt = attempt,
            CancellationToken.None);

        Assert.Equal("canonical-origin", result);
        Assert.Equal(2, attempts);
        Assert.Equal(0, reportedAttempt);
    }

    [Fact]
    public async Task MarkdownImageFallback_CallerCancellationNeverStartsNextAttempt()
    {
        using CancellationTokenSource cancellation = new();
        bool secondStarted = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MarkdownImageFallbackPipeline.FirstSuccessfulAsync(
                token =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<string?>(token);
                },
                _ =>
                {
                    secondStarted = true;
                    return Task.FromResult<string?>("must-not-run");
                },
                (_, _) => { },
                cancellation.Token));

        Assert.False(secondStarted);
    }

    [Fact]
    public async Task MarkdownImageFallback_HedgeUsesFastPrimaryWithoutOriginRequest()
    {
        bool originStarted = false;
        string? result = await MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            _ => Task.FromResult<string?>("camo"),
            _ =>
            {
                originStarted = true;
                return Task.FromResult<string?>("origin");
            },
            (_, _) => { },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal("camo", result);
        Assert.False(originStarted);
    }

    [Fact]
    public async Task MarkdownImageFallback_HedgeUsesOriginWhilePrimaryIsStalled()
    {
        CancellationToken primaryToken = default;
        int failures = 0;
        string? result = await MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            async token =>
            {
                primaryToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "camo";
            },
            _ => Task.FromResult<string?>("origin"),
            (_, _) => Interlocked.Increment(ref failures),
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        // Inspect the canceled token itself, without making this behavioral
        // assertion depend on a separate callback's completion signal.
        Assert.True(primaryToken.CanBeCanceled);
        Assert.True(primaryToken.IsCancellationRequested);
        Assert.Equal("origin", result);
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task MarkdownImageFallback_WinnerDoesNotWaitForUnresponsiveLoser()
    {
        var releasePrimary = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken primaryToken = default;
        Task<string?> resultTask = MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            token =>
            {
                primaryToken = token;
                return releasePrimary.Task;
            },
            _ => Task.FromResult<string?>("origin"),
            (_, _) => { },
            TimeSpan.Zero,
            CancellationToken.None);

        try
        {
            Assert.Equal("origin", await resultTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(primaryToken.IsCancellationRequested);
            Assert.False(releasePrimary.Task.IsCompleted);
        }
        finally
        {
            releasePrimary.TrySetResult(null);
            await resultTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task MarkdownImageFallback_FailedHedgeStillAcceptsLatePrimary()
    {
        var primary = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string?> resultTask = MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            _ => primary.Task,
            _ => Task.FromException<string?>(new IOException("Origin failed.")),
            (_, _) => { },
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.False(resultTask.IsCompleted);
        primary.SetResult("camo");
        Assert.Equal("camo", await resultTask);
    }

    [Fact]
    public async Task MarkdownImageFallback_HedgePropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        Task<string?> resultTask = MarkdownImageFallbackPipeline.FirstSuccessfulHedgedAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "camo";
            },
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "origin";
            },
            (_, _) => { },
            TimeSpan.Zero,
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resultTask);
    }
}

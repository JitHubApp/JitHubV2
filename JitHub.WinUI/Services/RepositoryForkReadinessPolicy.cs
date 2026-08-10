using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace JitHub.Services;

public enum RepositoryForkReadinessFailure
{
    None,
    AttemptsExhausted,
    DeadlineExceeded,
    RateLimited
}

public sealed record RepositoryForkReadinessResult(
    bool IsReady,
    RepositoryForkReadinessFailure Failure,
    DateTimeOffset? RetryAvailableAt = null);

public static class RepositoryForkReadinessPolicy
{
    public const int DefaultMaxAttempts = 8;
    public static readonly TimeSpan DefaultMaxTotalDelay = TimeSpan.FromSeconds(45);

    public static async Task<bool> WaitForReadyAsync(
        Func<int, CancellationToken, Task<bool>> readinessProbe,
        CancellationToken cancellationToken,
        int maxAttempts = DefaultMaxAttempts,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? maxTotalDelay = null) =>
        (await WaitForReadyResultAsync(
            readinessProbe,
            cancellationToken,
            maxAttempts,
            delay,
            maxTotalDelay).ConfigureAwait(false)).IsReady;

    public static async Task<RepositoryForkReadinessResult> WaitForReadyResultAsync(
        Func<int, CancellationToken, Task<bool>> readinessProbe,
        CancellationToken cancellationToken,
        int maxAttempts = DefaultMaxAttempts,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? maxTotalDelay = null)
    {
        ArgumentNullException.ThrowIfNull(readinessProbe);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        delay ??= static (duration, token) => Task.Delay(duration, token);
        TimeSpan wallClockBudget = maxTotalDelay ?? DefaultMaxTotalDelay;
        if (wallClockBudget < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalDelay));
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = wallClockBudget - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return new(false, RepositoryForkReadinessFailure.DeadlineExceeded);
            }

            try
            {
                using CancellationTokenSource probeDeadline =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeDeadline.CancelAfter(remaining);
                Task<bool> probeTask = readinessProbe(attempt, probeDeadline.Token);
                if (await probeTask.WaitAsync(probeDeadline.Token).ConfigureAwait(false))
                {
                    return new(true, RepositoryForkReadinessFailure.None);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(false, RepositoryForkReadinessFailure.DeadlineExceeded);
            }
            catch (GitHubRateLimitException ex)
            {
                TimeSpan rateLimitDelay = ex.RetryDelay <= TimeSpan.Zero
                    ? DelayForAttempt(attempt)
                    : ex.RetryDelay;
                DateTimeOffset retryAvailableAt = DateTimeOffset.UtcNow.Add(rateLimitDelay);
                if (attempt >= maxAttempts - 1)
                {
                    return new(false, RepositoryForkReadinessFailure.RateLimited, retryAvailableAt);
                }

                if (!await TryDelayWithinDeadlineAsync(
                    rateLimitDelay,
                    delay,
                    stopwatch,
                    wallClockBudget,
                    cancellationToken).ConfigureAwait(false))
                {
                    return new(false, RepositoryForkReadinessFailure.RateLimited, retryAvailableAt);
                }

                continue;
            }
            catch (GitHubApiException ex) when (IsPreparationStatus(ex.StatusCode))
            {
                if (attempt >= maxAttempts - 1)
                {
                    return new(false, RepositoryForkReadinessFailure.AttemptsExhausted);
                }

                if (!await TryDelayWithinDeadlineAsync(
                    DelayForAttempt(attempt),
                    delay,
                    stopwatch,
                    wallClockBudget,
                    cancellationToken).ConfigureAwait(false))
                {
                    return new(false, RepositoryForkReadinessFailure.DeadlineExceeded);
                }

                continue;
            }

            if (attempt < maxAttempts - 1)
            {
                if (!await TryDelayWithinDeadlineAsync(
                    DelayForAttempt(attempt),
                    delay,
                    stopwatch,
                    wallClockBudget,
                    cancellationToken).ConfigureAwait(false))
                {
                    return new(false, RepositoryForkReadinessFailure.DeadlineExceeded);
                }
            }
        }

        return new(false, RepositoryForkReadinessFailure.AttemptsExhausted);
    }

    public static TimeSpan DelayForAttempt(int attempt)
    {
        int safeAttempt = Math.Clamp(attempt, 0, 4);
        return TimeSpan.FromMilliseconds(500 * (1 << safeAttempt));
    }

    private static bool IsPreparationStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict;

    private static async Task<bool> TryDelayWithinDeadlineAsync(
        TimeSpan requestedDelay,
        Func<TimeSpan, CancellationToken, Task> delay,
        Stopwatch stopwatch,
        TimeSpan wallClockBudget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan remaining = wallClockBudget - stopwatch.Elapsed;
        if (requestedDelay < TimeSpan.Zero || requestedDelay > remaining)
        {
            return false;
        }

        try
        {
            using CancellationTokenSource delayDeadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            delayDeadline.CancelAfter(remaining);
            await delay(requestedDelay, delayDeadline.Token)
                .WaitAsync(delayDeadline.Token)
                .ConfigureAwait(false);
            return stopwatch.Elapsed < wallClockBudget;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public static bool IsAccessibleRepositoryReady(string? defaultBranch, int? branchCount = null) =>
        string.IsNullOrWhiteSpace(defaultBranch) || branchCount is > 0;
}

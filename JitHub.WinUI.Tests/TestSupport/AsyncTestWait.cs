using System;
using System.Threading.Tasks;
using Xunit;

namespace JitHub.WinUI.Tests.TestSupport;

/// <summary>
/// Waits for signals that the code under test must raise. The timeout only stops a
/// hung test. It must never be the thing that decides whether timing-sensitive
/// behavior passed: assert ordering with explicit signals instead.
/// </summary>
internal static class AsyncTestWait
{
    /// <summary>
    /// Long enough that a loaded CI runner running the whole suite in parallel never
    /// trips it, so a timeout means a real hang or a lost signal.
    /// </summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Polls until <paramref name="condition"/> holds, failing only after <see cref="HangGuard"/>.
    /// Use for state the code under test reaches on its own without raising a signal,
    /// such as cleanup that runs after a task has already completed.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, string description = "condition")
    {
        ArgumentNullException.ThrowIfNull(condition);
        DateTime deadline = DateTime.UtcNow + HangGuard;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"Timed out after {HangGuard.TotalSeconds:0} s waiting for {description}.");
            }

            await Task.Delay(PollInterval);
        }
    }
}

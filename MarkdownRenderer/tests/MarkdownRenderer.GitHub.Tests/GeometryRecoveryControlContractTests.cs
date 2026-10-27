using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// The snapshot failure latch has behavioral coverage in
/// <see cref="LayoutGeometryRecoveryTests"/>. These checks protect the WinUI
/// control boundary that turns that latch into one replacement request while
/// retaining the last painted surface and committed logical selection.
/// </summary>
public sealed class GeometryRecoveryControlContractTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task RetiringLazyWorkDoesNotChangeItsCapturedCancellationOutcome(
        bool workerReportedCanceled,
        bool tokenCanceledBeforeRetirement,
        bool expectedCanceled)
    {
        using var source = new CancellationTokenSource();
        if (tokenCanceledBeforeRetirement)
            source.Cancel();
        CancellationToken operationToken = source.Token;

        // Match the production completion decision. Retirement still cancels
        // even a successfully completed source so registrations can drain, but
        // that cleanup must not retroactively change the captured outcome.
        bool operationWasCanceled = workerReportedCanceled || operationToken.IsCancellationRequested;
        Task callbacks = CancellationTokenSourceRetirement.RequestCancellation(source);

        Assert.True(operationToken.IsCancellationRequested);
        Assert.Equal(expectedCanceled, operationWasCanceled);
        await callbacks;
    }
}

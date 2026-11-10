using MarkdownRenderer.Svg.Resvg.Internal;
using MarkdownRenderer.Svg.Resvg.Internal.Workers.Scheduling;

namespace MarkdownRenderer.Svg.Resvg.Tests;

/// <summary>
/// Holds the first real admission of one worker operation until the test
/// releases it, so the test can act while that operation stands at worker
/// admission with every borrow it took on the way still held.
/// </summary>
internal sealed class GatedAdmissionObserver(WorkerOperation gatedOperation) : IWorkerAdmissionObserver
{
    private readonly ManualResetEventSlim _entered = new();
    private readonly ManualResetEventSlim _release = new();
    private int _gated;

    /// <summary>The token of the held admission, once it has arrived.</summary>
    internal CancellationToken AdmissionToken { get; private set; }

    public void AdmissionRequested(WorkerOperation operation, CancellationToken cancellationToken)
    {
        if (operation != gatedOperation || Interlocked.Exchange(ref _gated, 1) != 0)
            return;

        AdmissionToken = cancellationToken;
        _entered.Set();
        _release.Wait();
    }

    /// <summary>Waits until the gated operation reaches admission.</summary>
    internal bool WaitUntilEntered(TimeSpan timeout) => _entered.Wait(timeout);

    /// <summary>Lets the held admission continue.</summary>
    internal void Release() => _release.Set();
}
